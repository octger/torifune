using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Torifune.Core.Services.Tools;

namespace Torifune.Core.Services.Normalization;

/// <summary>FFmpeg loudnorm フィルターによる2パス EBU R128 正規化。</summary>
public sealed class AudioNormalizationService : IAudioNormalizationService
{
    private static readonly TimeSpan StallWarningThreshold = TimeSpan.FromMinutes(2);

    private readonly IToolManager _tools;
    private readonly ILogger<AudioNormalizationService> _logger;

    public AudioNormalizationService(
        IToolManager tools,
        ILogger<AudioNormalizationService> logger)
    {
        _tools = tools;
        _logger = logger;
    }

    public async Task NormalizeAsync(
        string mediaPath,
        AudioNormalizationOptions options,
        IProgress<AudioNormalizationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(mediaPath))
        {
            throw new FileNotFoundException("正規化対象ファイルが見つかりません。", mediaPath);
        }

        ValidateOptions(options);
        progress?.Report(new AudioNormalizationProgress("音量を解析中 (1/2)..."));

        var analysisFilter = BuildAnalysisFilter(options);
        var analysisArgs = new List<string>
        {
            "-hide_banner", "-nostats", "-i", mediaPath,
            "-map", "0:a:0",
            "-af", analysisFilter,
            "-f", "null", "-",
        };
        var analysis = await RunFfmpegAsync(
                analysisArgs,
                elapsed => progress?.Report(new AudioNormalizationProgress(
                    $"警告: 音量解析が {elapsed.TotalMinutes:0.0} 分継続しています。必要に応じて中止してください。")),
                ct)
            .ConfigureAwait(false);
        if (analysis.ExitCode != 0)
        {
            throw new AudioNormalizationException(
                "音量解析に失敗しました。音声トラックが存在するか確認してください。" + SummarizeError(analysis.StandardError));
        }

        LoudnormMeasurement measurement;
        try
        {
            measurement = LoudnormMeasurement.Parse(analysis.StandardError);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new AudioNormalizationException($"音量解析結果を読み取れませんでした: {ex.Message}");
        }

        progress?.Report(new AudioNormalizationProgress("音量を正規化中 (2/2)..."));

        var extension = Path.GetExtension(mediaPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            throw new AudioNormalizationException("出力コンテナを判定できないファイル名です。");
        }

        var tempPath = Path.Combine(
            Path.GetDirectoryName(mediaPath)!,
            Path.GetFileNameWithoutExtension(mediaPath) + ".torifune-normalizing" + extension);

        var inputSampleRate = await ProbeInputSampleRateAsync(mediaPath, ct).ConfigureAwait(false);
        var outputSampleRate = ResolveOutputSampleRate(inputSampleRate);

        _logger.LogInformation(
            "Audio normalization sample-rate: input={InputSampleRate}, output={OutputSampleRate}",
            inputSampleRate,
            outputSampleRate);

        try
        {
            var applyFilter = BuildApplyFilter(options, measurement);
            var applyArgs = new List<string>
            {
                "-hide_banner", "-nostats", "-y", "-i", mediaPath,
                "-map", "0",
                "-map_metadata", "0",
                "-map_chapters", "0",
                "-c", "copy",
                "-c:a", "aac",
                "-b:a", $"{options.AudioBitrateKbps}k",
                "-ar", outputSampleRate.ToString(CultureInfo.InvariantCulture),
                "-af", applyFilter,
                "-max_muxing_queue_size", "4096",
                tempPath,
            };

            var apply = await RunFfmpegAsync(
                    applyArgs,
                    elapsed => progress?.Report(new AudioNormalizationProgress(
                        $"警告: 音声正規化が {elapsed.TotalMinutes:0.0} 分継続しています。必要に応じて中止してください。")),
                    ct)
                .ConfigureAwait(false);
            if (apply.ExitCode != 0 || !File.Exists(tempPath))
            {
                throw new AudioNormalizationException(
                    "正規化済みファイルの生成に失敗しました。" + SummarizeError(apply.StandardError));
            }

            File.Move(tempPath, mediaPath, overwrite: true);
            progress?.Report(new AudioNormalizationProgress("音声の正規化が完了しました"));
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public static string BuildAnalysisFilter(AudioNormalizationOptions options) =>
        string.Create(CultureInfo.InvariantCulture,
            $"loudnorm=I={options.IntegratedLoudnessLufs}:LRA={options.LoudnessRange}:TP={options.TruePeakDb}:print_format=json");

    public static string BuildApplyFilter(
        AudioNormalizationOptions options,
        LoudnormMeasurement measurement)
    {
        return
        string.Create(CultureInfo.InvariantCulture,
            $"loudnorm=I={options.IntegratedLoudnessLufs}:LRA={options.LoudnessRange}:TP={options.TruePeakDb}" +
            $":measured_I={measurement.InputIntegratedLoudness}" +
            $":measured_LRA={measurement.InputLoudnessRange}" +
            $":measured_TP={measurement.InputTruePeak}" +
            $":measured_thresh={measurement.InputThreshold}" +
            $":offset={measurement.TargetOffset}:linear=true:print_format=summary");
    }

    private async Task<int?> ProbeInputSampleRateAsync(string mediaPath, CancellationToken ct)
    {
        var ffprobePath = ResolveFfprobePath();
        if (ffprobePath is null)
        {
            _logger.LogDebug("ffprobe が見つからないため入力サンプルレート判定をスキップ");
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
                     "-select_streams", "a:0",
                     "-show_entries", "stream=sample_rate",
                     "-of", "default=nokey=1:noprint_wrappers=1",
                     mediaPath,
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
        var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        var stderr = await stderrTask.ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            _logger.LogDebug("ffprobe sample_rate 取得失敗: {Error}", stderr.Trim());
            return null;
        }

        return int.TryParse(stdout, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
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

    private static int ResolveOutputSampleRate(int? inputSampleRate)
    {
        if (inputSampleRate is null)
        {
            return 48000;
        }

        return inputSampleRate.Value >= 48000 ? 48000 : 44100;
    }

    private async Task<ProcessResult> RunFfmpegAsync(
        IReadOnlyList<string> arguments,
        Action<TimeSpan>? onStall,
        CancellationToken ct)
    {
        if (!File.Exists(_tools.FfmpegPath))
        {
            throw new FileNotFoundException("FFmpeg が見つかりません。", _tools.FfmpegPath);
        }

        var startInfo = new ProcessStartInfo(_tools.FfmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _logger.LogDebug("FFmpeg normalization: {Arguments}", string.Join(' ', arguments));
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("FFmpeg の起動に失敗しました。");
        using var heartbeatCts = new CancellationTokenSource();
        var startedAt = DateTimeOffset.UtcNow;
        var heartbeat = MonitorProcessAsync(process, startedAt, onStall, heartbeatCts.Token);

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return new ProcessResult(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 既に終了済み
            }
            throw;
        }
        finally
        {
            heartbeatCts.Cancel();
            await heartbeat.ConfigureAwait(false);
        }
    }

    private async Task MonitorProcessAsync(
        Process process,
        DateTimeOffset startedAt,
        Action<TimeSpan>? onStall,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(StallWarningThreshold, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (process.HasExited)
            {
                return;
            }

            var elapsed = DateTimeOffset.UtcNow - startedAt;
            _logger.LogWarning(
                "FFmpeg normalization is still running: pid={Pid}, elapsedSec={ElapsedSec:0.0}",
                process.Id,
                elapsed.TotalSeconds);
            onStall?.Invoke(elapsed);
        }
    }

    private static void ValidateOptions(AudioNormalizationOptions options)
    {
        if (options.IntegratedLoudnessLufs is < -70 or > -5)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "目標LUFSは -70〜-5 の範囲で指定してください。");
        }
        if (options.LoudnessRange is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "目標LRAは 1〜50 の範囲で指定してください。");
        }
        if (options.TruePeakDb is < -9 or > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "目標True Peakは -9〜0 の範囲で指定してください。");
        }
        if (options.AudioBitrateKbps is < 64 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "AACビットレートは 64〜512kbps の範囲で指定してください。");
        }
    }

    private static string SummarizeError(string stderr)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .LastOrDefault(value => value.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                                    value.Contains("Invalid", StringComparison.OrdinalIgnoreCase));
        return line is null ? string.Empty : $" ({line})";
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
