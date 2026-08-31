using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Torifune.Core.Services.Normalization;
using Torifune.Core.Services.Tools;

namespace Torifune.Core.Services.PostProcessing;

public sealed class MediaPostProcessingService : IMediaPostProcessingService
{
    private const int TargetWidth = 1920;
    private const int TargetHeight = 1080;
    private static readonly TimeSpan MinimumProcessingTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultProcessingTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaximumProcessingTimeout = TimeSpan.FromHours(2);
    private readonly IToolManager _tools;
    private readonly IAudioNormalizationService _audioNormalization;
    private readonly ILogger<MediaPostProcessingService> _logger;

    public MediaPostProcessingService(
        IToolManager tools,
        IAudioNormalizationService audioNormalization,
        ILogger<MediaPostProcessingService> logger)
    {
        _tools = tools;
        _audioNormalization = audioNormalization;
        _logger = logger;
    }

    public async Task<MediaPostProcessingResult> ProcessAsync(
        string mediaPath,
        MediaPostProcessingOptions options,
        IProgress<MediaPostProcessingProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(mediaPath))
        {
            throw new FileNotFoundException("後処理対象ファイルが見つかりません。", mediaPath);
        }

        var media = await ProbeAsync(mediaPath, ct).ConfigureAwait(false);
        var processingTimeout = ResolveProcessingTimeout(media.Duration);
        var needsUpscale = options.UpscaleToFhd &&
                           media.VideoWidth is { } width &&
                           media.VideoHeight is { } height &&
                           ShouldUpscaleToFhd(width, height);

        if (!needsUpscale)
        {
            if (options.NormalizeAudio)
            {
                var normalizationOptions = new AudioNormalizationOptions(
                    options.TargetLoudnessLufs,
                    options.TargetLoudnessRange,
                    options.TargetTruePeakDb,
                    options.AudioBitrateKbps,
                    options.NormalizeStartTimeSeconds,
                    options.NormalizeEndTimeSeconds);
                var normalizationProgress = new InlineProgress<AudioNormalizationProgress>(value =>
                    progress?.Report(new MediaPostProcessingProgress(value.Message, IsUpscaling: false)));
                await _audioNormalization
                    .NormalizeAsync(mediaPath, normalizationOptions, normalizationProgress, ct)
                    .ConfigureAwait(false);
            }

            var finalMedia = await ProbeAsync(mediaPath, ct).ConfigureAwait(false);
            return new MediaPostProcessingResult(
                mediaPath,
                options.NormalizeAudio,
                WasUpscaled: false,
                finalMedia.VideoWidth,
                finalMedia.VideoHeight,
                finalMedia.VideoCodec,
                finalMedia.AudioCodec,
                finalMedia.AudioBitrate,
                finalMedia.AudioSampleRate);
        }

        progress?.Report(new MediaPostProcessingProgress(
            options.NormalizeAudio
                ? "音量解析とFHD変換を準備しています..."
                : "FHD変換を準備しています...",
            IsUpscaling: true));

        LoudnormMeasurement? measurement = null;
        AudioNormalizationOptions? normalization = null;
        if (options.NormalizeAudio && media.HasAudio)
        {
            normalization = new AudioNormalizationOptions(
                options.TargetLoudnessLufs,
                options.TargetLoudnessRange,
                options.TargetTruePeakDb,
                options.AudioBitrateKbps,
                options.NormalizeStartTimeSeconds,
                options.NormalizeEndTimeSeconds);
            var analysis = await RunFfmpegAsync(
                [
                    "-hide_banner", "-nostats", "-i", mediaPath,
                    "-map", "0:a:0",
                    "-af", AudioNormalizationService.BuildAnalysisFilter(normalization),
                    "-f", "null", "-",
                ],
                processingTimeout,
                ct).ConfigureAwait(false);
            if (analysis.ExitCode != 0)
            {
                throw new AudioNormalizationException(
                    "FHD変換前の音量解析に失敗しました。" + SummarizeError(analysis.StandardError));
            }

            measurement = LoudnormMeasurement.Parse(analysis.StandardError);
        }

        var outputPath = ResolveOutputPath(mediaPath);
        var tempPath = BuildTempPath(outputPath);
        try
        {
            progress?.Report(new MediaPostProcessingProgress(
                options.NormalizeAudio ? "FHD変換と音声正規化を実行中..." : "FHD変換を実行中...",
                IsUpscaling: true));

            var arguments = BuildConversionArguments(
                mediaPath,
                tempPath,
                media.HasAudio,
                normalization,
                measurement,
                media.AudioSampleRate,
                ShouldReencodeAudioForContainer(mediaPath, outputPath));
            var conversion = await RunFfmpegAsync(arguments, processingTimeout, ct).ConfigureAwait(false);
            if (conversion.ExitCode != 0 || !File.Exists(tempPath))
            {
                throw new InvalidOperationException(
                    "FHD変換済みファイルの生成に失敗しました。" + SummarizeError(conversion.StandardError));
            }

            var converted = await ProbeAsync(tempPath, ct).ConfigureAwait(false);
            if (converted.VideoWidth != TargetWidth || converted.VideoHeight != TargetHeight)
            {
                throw new InvalidOperationException(
                    $"FHD変換後の解像度が不正です: {converted.VideoWidth}x{converted.VideoHeight}");
            }

            File.Move(tempPath, outputPath, overwrite: true);
            if (!string.Equals(outputPath, mediaPath, StringComparison.OrdinalIgnoreCase) && File.Exists(mediaPath))
            {
                File.Delete(mediaPath);
            }

            progress?.Report(new MediaPostProcessingProgress("FHD変換が完了しました。", IsUpscaling: true));
            return new MediaPostProcessingResult(
                outputPath,
                options.NormalizeAudio && media.HasAudio,
                WasUpscaled: true,
                TargetWidth,
                TargetHeight,
                converted.VideoCodec,
                converted.AudioCodec,
                converted.AudioBitrate,
                converted.AudioSampleRate);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "FHD変換一時ファイルの削除に失敗: {Path}", tempPath);
                }
            }
        }
    }

    public static bool ShouldUpscaleToFhd(int width, int height) =>
        width > 0 && height > 0 &&
        (width < TargetWidth || height < TargetHeight) &&
        !(width >= TargetWidth && height >= TargetHeight);

    private static IReadOnlyList<string> BuildConversionArguments(
        string inputPath,
        string outputPath,
        bool hasAudio,
        AudioNormalizationOptions? normalization,
        LoudnormMeasurement? measurement,
        int? inputSampleRate,
        bool reencodeAudioForContainer)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostats", "-y",
            "-i", inputPath,
            "-map", "0:v:0",
        };
        if (hasAudio)
        {
            args.AddRange(["-map", "0:a:0?"]);
        }
        args.AddRange([
            "-map_metadata", "0",
            "-map_chapters", "0",
            "-c:v", "libx264",
            "-preset", "medium",
            "-crf", "19",
            "-pix_fmt", "yuv420p",
            "-vf", "scale=1920:1080:force_original_aspect_ratio=decrease:flags=lanczos,pad=1920:1080:(ow-iw)/2:(oh-ih)/2:black,setsar=1",
        ]);

        if (hasAudio && normalization is not null && measurement is not null)
        {
            args.AddRange([
                "-c:a", "aac",
                "-b:a", $"{normalization.AudioBitrateKbps}k",
                "-ar", ResolveOutputSampleRate(inputSampleRate).ToString(CultureInfo.InvariantCulture),
                "-af", AudioNormalizationService.BuildApplyFilter(normalization, measurement),
            ]);
        }
        else if (hasAudio && reencodeAudioForContainer)
        {
            args.AddRange([
                "-c:a", "aac",
                "-b:a", "192k",
                "-ar", ResolveOutputSampleRate(inputSampleRate).ToString(CultureInfo.InvariantCulture),
            ]);
        }
        else if (hasAudio)
        {
            args.AddRange(["-c:a", "copy"]);
        }

        var extension = Path.GetExtension(outputPath);
        if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mov", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["-movflags", "+faststart"]);
        }
        args.Add(outputPath);
        return args;
    }

    private async Task<MediaProbe> ProbeAsync(string mediaPath, CancellationToken ct)
    {
        var ffprobePath = Path.Combine(
            _tools.FfmpegDirectory,
            OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (!File.Exists(ffprobePath))
        {
            throw new FileNotFoundException("FFprobe が見つかりません。", ffprobePath);
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
        foreach (var argument in new[]
                 {
                     "-v", "error",
                     "-print_format", "json",
                     "-show_streams",
                     "-show_format",
                     mediaPath,
                 })
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("FFprobe の起動に失敗しました。");
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("メディア情報の取得に失敗しました。" + SummarizeError(stderr));
            }

            using var document = JsonDocument.Parse(stdout);
            var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var video = streams.FirstOrDefault(stream => GetString(stream, "codec_type") == "video");
            var audio = streams.FirstOrDefault(stream => GetString(stream, "codec_type") == "audio");
            return new MediaProbe(
                GetInt(video, "width"),
                GetInt(video, "height"),
                GetString(video, "codec_name"),
                audio.ValueKind == JsonValueKind.Object,
                GetString(audio, "codec_name"),
                GetLong(audio, "bit_rate"),
                GetInt(audio, "sample_rate"),
                document.RootElement.TryGetProperty("format", out var format)
                    ? GetTimeSpan(format, "duration")
                    : null);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private async Task<ProcessResult> RunFfmpegAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_tools.FfmpegPath)
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
            psi.ArgumentList.Add(argument);
        }

        _logger.LogDebug("FFmpeg media post-processing: {Arguments}", string.Join(' ', arguments));
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("FFmpeg の起動に失敗しました。");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new ProcessResult(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException(
                $"FHD後処理が想定時間（{timeout.TotalMinutes:0.0}分）を超えたため、FFmpegを終了しました。");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static string ResolveOutputPath(string mediaPath)
    {
        var extension = Path.GetExtension(mediaPath);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            ? mediaPath
            : Path.Combine(
                Path.GetDirectoryName(mediaPath)!,
                Path.GetFileNameWithoutExtension(mediaPath) + ".fhd.mp4");
    }

    private static string BuildTempPath(string outputPath) => Path.Combine(
        Path.GetDirectoryName(outputPath)!,
        Path.GetFileNameWithoutExtension(outputPath) + ".torifune-fhd" + Path.GetExtension(outputPath));

    private static int ResolveOutputSampleRate(int? inputSampleRate) =>
        inputSampleRate is >= 48000 ? 48000 : 44100;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property)
            ? property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        int.TryParse(GetString(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static long? GetLong(JsonElement element, string name) =>
        long.TryParse(GetString(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static TimeSpan? GetTimeSpan(JsonElement element, string name) =>
        double.TryParse(GetString(element, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds) &&
        seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    private static TimeSpan ResolveProcessingTimeout(TimeSpan? duration)
    {
        if (duration is null)
        {
            return DefaultProcessingTimeout;
        }

        var doubledTicks = duration.Value.Ticks > TimeSpan.MaxValue.Ticks / 2
            ? TimeSpan.MaxValue.Ticks
            : duration.Value.Ticks * 2;
        var doubled = TimeSpan.FromTicks(doubledTicks);
        return doubled < MinimumProcessingTimeout
            ? MinimumProcessingTimeout
            : doubled > MaximumProcessingTimeout
                ? MaximumProcessingTimeout
                : doubled;
    }

    private static bool ShouldReencodeAudioForContainer(string inputPath, string outputPath) =>
        !string.Equals(Path.GetExtension(inputPath), Path.GetExtension(outputPath), StringComparison.OrdinalIgnoreCase) &&
        Path.GetExtension(outputPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string SummarizeError(string stderr)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .LastOrDefault();
        return line is null ? string.Empty : $" ({line})";
    }

    private sealed record MediaProbe(
        int? VideoWidth,
        int? VideoHeight,
        string? VideoCodec,
        bool HasAudio,
        string? AudioCodec,
        long? AudioBitrate,
        int? AudioSampleRate,
        TimeSpan? Duration);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
