using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Torifune.Core.Models;
using Torifune.Core.Services.PostProcessing;
using Torifune.Core.Services.Tools;

namespace Torifune.Core.Services.Ytdlp;

/// <summary>
/// yt-dlp をサブプロセスとして実行するサービス。
/// 引数は常に ArgumentList 経由(シェル非経由)で渡す。
/// </summary>
public sealed class YtdlpService : IYtdlpProcessService
{
    private static readonly TimeSpan StallWarningThreshold = TimeSpan.FromMinutes(2);

    private readonly IToolManager _tools;
    private readonly IMediaPostProcessingService _postProcessing;
    private readonly ILogger<YtdlpService> _logger;

    public YtdlpService(
        IToolManager tools,
        IMediaPostProcessingService postProcessing,
        ILogger<YtdlpService> logger)
    {
        _tools = tools;
        _postProcessing = postProcessing;
        _logger = logger;
    }

    public async Task<MediaInfo> FetchMediaInfoAsync(string url, CancellationToken ct = default)
    {
        ValidateUrl(url);
        var args = YtdlpArgumentBuilder.BuildFetchInfoArgs(url);

        var stdout = new StringBuilder();
        var errorLines = new List<string>();

        var exitCode = await RunAsync(
            args,
            line => stdout.AppendLine(line),
            line => CollectError(line, errorLines),
            onHeartbeat: null,
            ct).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw new YtdlpException(
                BuildErrorMessage("メタデータの取得に失敗しました", errorLines),
                exitCode, errorLines);
        }

        return YtdlpOutputParser.ParseMediaInfo(stdout.ToString(), url);
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadOptions options,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ValidateUrl(options.Url);
        Directory.CreateDirectory(options.OutputDirectory);

        var args = YtdlpArgumentBuilder.BuildDownloadArgs(options, _tools.FfmpegDirectory);

        _logger.LogInformation(
            "Download begin: url={Url}, outputDir={OutputDir}, audioOnly={AudioOnly}, normalize={Normalize}, upscaleToFhd={Upscale}, start={Start}, end={End}, format={Format}",
            options.Url,
            options.OutputDirectory,
            options.AudioOnly,
            options.NormalizeAudio,
            options.UpscaleToFhd,
            options.StartTimeSeconds,
            options.EndTimeSeconds,
            options.FormatString ?? "(default)");

        string? outputPath = null;
        var errorLines = new List<string>();
        var progressEvents = 0;
        var stateTransitions = 0;
        DownloadState? lastState = null;
        var lastPreparationReportAt = DateTimeOffset.MinValue;
        var lastStallWarningAt = DateTimeOffset.MinValue;

        progress?.Report(new DownloadProgress(DownloadState.Preparing, null, null, null, null, null, "解析中..."));

        var sectionSummary = FormatSectionSummary(options.StartTimeSeconds, options.EndTimeSeconds);
        var formatSummary = string.IsNullOrWhiteSpace(options.FormatString)
            ? $"{options.FormatSort ?? "(既定)"}"
            : options.FormatString;

        var exitCode = await RunAsync(
            args,
            line =>
            {
                if (YtdlpOutputParser.TryParseProgressLine(line) is { } p)
                {
                    progressEvents++;
                    if (lastState != p.State)
                    {
                        stateTransitions++;
                        lastState = p.State;
                        _logger.LogDebug("yt-dlp progress state changed: {State}", p.State);
                    }

                    if (progressEvents <= 3 || progressEvents % 20 == 0)
                    {
                        _logger.LogDebug(
                            "yt-dlp progress event: state={State}, percent={Percent:0.0}, downloaded={Downloaded}, total={Total}, speed={Speed}, eta={Eta}",
                            p.State,
                            p.Percent,
                            p.DownloadedBytes,
                            p.TotalBytes,
                            p.SpeedBytesPerSec,
                            p.Eta);
                    }
                    progress?.Report(p);
                }
                else if (YtdlpOutputParser.TryParseFilePathLine(line) is { } path)
                {
                    outputPath = path;
                    _logger.LogDebug("yt-dlp output path detected (after_move): {Path}", outputPath);
                }
                else if (YtdlpOutputParser.TryParseFallbackFilePathLine(line) is { } fallbackPath)
                {
                    outputPath = fallbackPath;
                    _logger.LogDebug("yt-dlp output path detected (fallback): {Path}", outputPath);
                }
            },
            line =>
            {
                if (YtdlpOutputParser.TryParseFallbackFilePathLine(line) is { } fallbackPath)
                {
                    outputPath = fallbackPath;
                    _logger.LogDebug("yt-dlp output path detected from stderr: {Path}", outputPath);
                }

                CollectError(line, errorLines);
            },
            heartbeat =>
            {
                // 進捗行が未到達の待機が長い場合に、現在の待機フェーズをUIへ明示する。
                if (heartbeat.StdoutLines > 0 || heartbeat.StderrLines > 0)
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                var shouldReport = heartbeat.Elapsed >= TimeSpan.FromSeconds(20) &&
                                   (lastPreparationReportAt == DateTimeOffset.MinValue ||
                                    now - lastPreparationReportAt >= TimeSpan.FromSeconds(20));
                if (!shouldReport)
                {
                    return;
                }

                lastPreparationReportAt = now;
                var isStalled = heartbeat.LastOutput >= StallWarningThreshold;
                var message = isStalled
                    ? $"警告: yt-dlp から {heartbeat.LastOutput.TotalSeconds:0} 秒間応答がありません。処理は継続中です。必要に応じて中止してください。"
                    : $"接続準備中: 出力未受信 {heartbeat.Elapsed.TotalSeconds:0}s / 最終出力 {heartbeat.LastOutput.TotalSeconds:0}s前 / 区間 {sectionSummary} / 形式 {formatSummary}";
                progress?.Report(new DownloadProgress(
                    DownloadState.Preparing,
                    null,
                    null,
                    null,
                    null,
                    null,
                    message));

                if (isStalled &&
                    (lastStallWarningAt == DateTimeOffset.MinValue ||
                     now - lastStallWarningAt >= StallWarningThreshold))
                {
                    lastStallWarningAt = now;
                    _logger.LogWarning(
                        "yt-dlp is unresponsive: elapsedSec={ElapsedSec:0.0}, lastOutputSec={LastOutputSec:0.0}, section={Section}, format={Format}",
                        heartbeat.Elapsed.TotalSeconds,
                        heartbeat.LastOutput.TotalSeconds,
                        sectionSummary,
                        formatSummary);
                }
            },
            ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Download process finished: exitCode={ExitCode}, progressEvents={ProgressEvents}, stateTransitions={StateTransitions}, outputPath={OutputPath}",
            exitCode,
            progressEvents,
            stateTransitions,
            outputPath);

        if (exitCode != 0)
        {
            return new DownloadResult(false, null,
                BuildErrorMessage("ダウンロードに失敗しました", errorLines));
        }

        if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
        {
            _logger.LogError(
                "yt-dlp completed without a valid output path: outputPath={OutputPath}, outputDir={OutputDir}",
                outputPath,
                options.OutputDirectory);
            return new DownloadResult(
                false,
                null,
                BuildErrorMessage(
                    "ダウンロードは完了しましたが、yt-dlp から出力ファイルを特定できませんでした。再試行してください",
                    errorLines));
        }

        if (options.NormalizeAudio || options.UpscaleToFhd)
        {
            _logger.LogInformation(
                "Media post-processing begin: path={Path}, normalize={Normalize}, upscaleToFhd={Upscale}",
                outputPath,
                options.NormalizeAudio,
                options.UpscaleToFhd);
            var postProcessingOptions = new MediaPostProcessingOptions(
                options.NormalizeAudio,
                options.UpscaleToFhd,
                options.TargetLoudnessLufs,
                options.TargetLoudnessRange,
                options.TargetTruePeakDb,
                192,
                options.NormalizeStartTimeSeconds,
                options.NormalizeEndTimeSeconds);
            var postProcessingProgress = new InlineProgress<MediaPostProcessingProgress>(value =>
                progress?.Report(new DownloadProgress(
                    value.IsUpscaling ? DownloadState.Upscaling : DownloadState.Normalizing,
                    null,
                    null,
                    null,
                    null,
                    null,
                    value.Message)));
            var postProcessingResult = await _postProcessing
                .ProcessAsync(outputPath, postProcessingOptions, postProcessingProgress, ct)
                .ConfigureAwait(false);
            outputPath = postProcessingResult.OutputPath;
            _logger.LogInformation(
                "Media post-processing end: path={Path}, normalized={Normalized}, upscaled={Upscaled}",
                outputPath,
                postProcessingResult.WasNormalized,
                postProcessingResult.WasUpscaled);
        }

        if (!File.Exists(outputPath))
        {
            return new DownloadResult(
                false,
                null,
                "後処理後の出力ファイルが見つかりませんでした。再試行してください");
        }

        progress?.Report(new DownloadProgress(DownloadState.Finished, 100, null, null, null, null, null));
        var mediaInfo = await ProbeDownloadedMediaInfoAsync(outputPath, ct).ConfigureAwait(false);
        _logger.LogInformation("Download success: outputPath={OutputPath}", outputPath);
        return new DownloadResult(true, outputPath, null, mediaInfo);
    }

    private async Task<DownloadedMediaInfo?> ProbeDownloadedMediaInfoAsync(string? outputPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
        {
            return null;
        }

        var ffprobePath = ResolveFfprobePath();
        if (ffprobePath is null)
        {
            _logger.LogDebug("ffprobe が見つからないためメディア情報取得をスキップ");
            return null;
        }

        var psi = new ProcessStartInfo(ffprobePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[]
                 {
                     "-v", "error",
                     "-print_format", "json",
                     "-show_streams",
                     "-show_format",
                     outputPath,
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return null;
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            _logger.LogDebug("ffprobe 解析失敗: {Error}", stderr);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var streams = root.TryGetProperty("streams", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().ToArray()
                : [];

            var video = streams.FirstOrDefault(stream => GetString(stream, "codec_type") == "video");
            var audio = streams.FirstOrDefault(stream => GetString(stream, "codec_type") == "audio");

            return new DownloadedMediaInfo(
                GetInt(video, "width"),
                GetInt(video, "height"),
                GetVideoFps(video),
                GetString(video, "codec_name"),
                GetString(audio, "codec_name"),
                GetLong(audio, "bit_rate"),
                GetLong(root.TryGetProperty("format", out var format) ? format : default, "bit_rate"),
                GetInt(audio, "sample_rate"),
                GetInt(audio, "channels"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "ffprobe JSON のパースに失敗");
            return null;
        }
    }

    private string? ResolveFfprobePath()
    {
        var dir = _tools.FfmpegDirectory;
        var candidates = new[]
        {
            Path.Combine(dir, "ffprobe.exe"),
            Path.Combine(dir, "ffprobe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? GetString(JsonElement elem, string name)
    {
        if (elem.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!elem.TryGetProperty(name, out var prop))
        {
            return null;
        }

        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
    }

    private static int? GetInt(JsonElement elem, string name)
    {
        var value = GetString(elem, name);
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static long? GetLong(JsonElement elem, string name)
    {
        var value = GetString(elem, name);
        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    private static double? GetVideoFps(JsonElement video)
    {
        var avg = ParseFpsFraction(GetString(video, "avg_frame_rate"));
        if (avg is > 0)
        {
            return avg;
        }

        var raw = ParseFpsFraction(GetString(video, "r_frame_rate"));
        return raw is > 0 ? raw : null;
    }

    private static double? ParseFpsFraction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split('/');
        if (parts.Length != 2)
        {
            return double.TryParse(value, out var direct) ? direct : null;
        }

        if (!double.TryParse(parts[0], out var numerator) ||
            !double.TryParse(parts[1], out var denominator) ||
            denominator == 0)
        {
            return null;
        }

        return numerator / denominator;
    }

    /// <summary>
    /// yt-dlp を起動し、stdout/stderr を行単位でコールバックへ流す。
    /// キャンセル時はプロセスツリーごと Kill する。
    /// </summary>
    private async Task<int> RunAsync(
        IReadOnlyList<string> args,
        Action<string> onStdout,
        Action<string> onStderr,
        Action<YtdlpHeartbeat>? onHeartbeat,
        CancellationToken ct)
    {
        var ytdlpPath = _tools.YtdlpPath;
        if (!File.Exists(ytdlpPath))
        {
            throw new FileNotFoundException("yt-dlp が見つかりません。セットアップを先に実行してください。", ytdlpPath);
        }

        var psi = new ProcessStartInfo(ytdlpPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        _logger.LogDebug("yt-dlp {Args}", string.Join(' ', args));

        long stdoutCount = 0;
        long stderrCount = 0;
        var startAt = DateTimeOffset.UtcNow;
        long lastOutputTicks = startAt.UtcTicks;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Interlocked.Increment(ref stdoutCount);
                Interlocked.Exchange(ref lastOutputTicks, DateTimeOffset.UtcNow.UtcTicks);
                onStdout(e.Data);
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Interlocked.Increment(ref stderrCount);
                Interlocked.Exchange(ref lastOutputTicks, DateTimeOffset.UtcNow.UtcTicks);
                onStderr(e.Data);
            }
        };

        if (!proc.Start())
        {
            throw new InvalidOperationException("yt-dlp の起動に失敗しました。");
        }

        _logger.LogInformation("yt-dlp process started: pid={Pid}", proc.Id);

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var heartbeatCts = new CancellationTokenSource();
        var heartbeat = Task.Run(async () =>
        {
            while (!heartbeatCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), heartbeatCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (proc.HasExited)
                {
                    break;
                }

                var elapsed = DateTimeOffset.UtcNow - startAt;
                var lastOutput = DateTimeOffset.UtcNow - new DateTimeOffset(Interlocked.Read(ref lastOutputTicks), TimeSpan.Zero);
                _logger.LogDebug(
                    "yt-dlp heartbeat: pid={Pid}, elapsedSec={ElapsedSec:0.0}, stdoutLines={Stdout}, stderrLines={Stderr}, lastOutputSec={LastOutputSec:0.0}",
                    proc.Id,
                    elapsed.TotalSeconds,
                    Interlocked.Read(ref stdoutCount),
                    Interlocked.Read(ref stderrCount),
                    lastOutput.TotalSeconds);

                onHeartbeat?.Invoke(new YtdlpHeartbeat(
                    elapsed,
                    lastOutput,
                    Interlocked.Read(ref stdoutCount),
                    Interlocked.Read(ref stderrCount)));
            }
        });

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                _logger.LogInformation("yt-dlp process killed due to cancellation: pid={Pid}", proc.Id);
            }
            catch (InvalidOperationException)
            {
                // 既に終了している場合は無視
            }

            heartbeatCts.Cancel();
            await heartbeat.ConfigureAwait(false);
            throw;
        }

        heartbeatCts.Cancel();
        await heartbeat.ConfigureAwait(false);

        _logger.LogInformation(
            "yt-dlp process exited: pid={Pid}, exitCode={ExitCode}, stdoutLines={Stdout}, stderrLines={Stderr}",
            proc.Id,
            proc.ExitCode,
            Interlocked.Read(ref stdoutCount),
            Interlocked.Read(ref stderrCount));

        return proc.ExitCode;
    }

    private static string FormatSectionSummary(double? start, double? end)
    {
        if (start is null && end is null)
        {
            return "全体";
        }

        var startText = start is null ? "0" : start.Value.ToString("0.###");
        var endText = end is null ? "末尾" : end.Value.ToString("0.###");
        return $"{startText}-{endText}s";
    }

    private void CollectError(string line, List<string> errorLines)
    {
        _logger.LogDebug("yt-dlp stderr: {Line}", line);
        if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
        {
            errorLines.Add(line);
        }
    }

    private static string BuildErrorMessage(string prefix, IReadOnlyList<string> errorLines) =>
        errorLines.Count > 0 ? $"{prefix}: {string.Join(" / ", errorLines)}" : prefix;

    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("http/https の URL を指定してください。", nameof(url));
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private readonly record struct YtdlpHeartbeat(
        TimeSpan Elapsed,
        TimeSpan LastOutput,
        long StdoutLines,
        long StderrLines);
}
