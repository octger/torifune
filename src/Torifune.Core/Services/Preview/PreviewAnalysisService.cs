using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Torifune.Core.Services.Normalization;
using Torifune.Core.Services.Tools;

namespace Torifune.Core.Services.Preview;

public sealed class PreviewAnalysisService : IPreviewAnalysisService
{
    private readonly IToolManager _tools;
    private readonly ILogger<PreviewAnalysisService> _logger;

    public PreviewAnalysisService(
        IToolManager tools,
        ILogger<PreviewAnalysisService> logger)
    {
        _tools = tools;
        _logger = logger;
    }

    public async Task<PreviewAnalysisResult> AnalyzeAsync(
        PreviewAnalysisRequest request,
        CancellationToken ct = default)
    {
        var cacheKey = $"{request.Url}|{request.StartSeconds}|{request.EndSeconds}|" +
                       $"{request.TargetLoudnessLufs}|{request.TargetTruePeakDb}|" +
                       $"{request.TargetLoudnessRange}|viz-v3";
        var directory = BuildCacheDirectoryPath(cacheKey);
        Directory.CreateDirectory(directory);

        var beforePath = Path.Combine(directory, "histogram-before.png");
        var afterPath = Path.Combine(directory, "histogram-after.png");
        var metricsPath = Path.Combine(directory, "metrics.json");
        var normalizedPath = Path.Combine(directory, "preview-normalized.wav");

        if (!File.Exists(beforePath))
        {
            await GenerateHistogramImageAsync(
                request.SourcePath,
                beforePath,
                request.StartSeconds,
                request.EndSeconds,
                ct).ConfigureAwait(false);
        }

        if (!File.Exists(normalizedPath))
        {
            await GenerateNormalizedAudioAsync(request, normalizedPath, ct).ConfigureAwait(false);
        }

        if (!File.Exists(afterPath))
        {
            await GenerateHistogramImageAsync(
                normalizedPath,
                afterPath,
                null,
                null,
                ct).ConfigureAwait(false);
        }

        var metrics = await ReadMetricsAsync(metricsPath, ct).ConfigureAwait(false)
            ?? await AnalyzeMetricsAsync(request, normalizedPath, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            metricsPath,
            JsonSerializer.Serialize(metrics),
            ct).ConfigureAwait(false);

        return new PreviewAnalysisResult(cacheKey, beforePath, afterPath, metrics);
    }

    private async Task GenerateHistogramImageAsync(
        string sourcePath,
        string outputPath,
        int? startSeconds,
        int? endSeconds,
        CancellationToken ct)
    {
        var rangeFilter = BuildRangeFilter(startSeconds, endSeconds);
        var filter = $"[0:a]{rangeFilter}aformat=channel_layouts=mono," +
                     "showwavespic=s=640x160:split_channels=0[v]";
        var result = await RunFfmpegAsync(
            [
                "-hide_banner", "-nostats", "-y",
                "-i", sourcePath,
                "-filter_complex", filter,
                "-map", "[v]",
                "-frames:v", "1",
                outputPath,
            ],
            ct).ConfigureAwait(false);

        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException(
                $"ヒストグラム画像の生成に失敗しました。{Summarize(result.StandardError)}");
        }
    }

    private async Task GenerateNormalizedAudioAsync(
        PreviewAnalysisRequest request,
        string outputPath,
        CancellationToken ct)
    {
        var segmentPath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? Path.GetTempPath(),
            Path.GetFileNameWithoutExtension(outputPath) + ".segment.wav");
        if (!File.Exists(segmentPath))
        {
            await ExtractAudioSegmentAsync(request, segmentPath, ct).ConfigureAwait(false);
        }

        var options = CreateNormalizationOptions(request);
        var analysis = await RunFfmpegAsync(
            [
                "-hide_banner", "-nostats",
                "-i", segmentPath,
                "-map", "0:a:0",
                "-af", AudioNormalizationService.BuildAnalysisFilter(options),
                "-f", "null", "-",
            ],
            ct).ConfigureAwait(false);
        if (analysis.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ラウドネス解析に失敗しました。{Summarize(analysis.StandardError)}");
        }

        LoudnormMeasurement measurement;
        try
        {
            measurement = LoudnormMeasurement.Parse(analysis.StandardError);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new InvalidOperationException(
                $"ラウドネス解析結果(JSON)を取得できませんでした。{ex.Message}");
        }

        var apply = await RunFfmpegAsync(
            [
                "-hide_banner", "-nostats", "-y",
                "-i", segmentPath,
                "-map", "0:a:0",
                "-c:a", "pcm_s16le",
                "-af", AudioNormalizationService.BuildApplyFilter(options, measurement),
                outputPath,
            ],
            ct).ConfigureAwait(false);
        if (apply.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException(
                $"正規化プレビュー音声の生成に失敗しました。{Summarize(apply.StandardError)}");
        }
    }

    private async Task ExtractAudioSegmentAsync(
        PreviewAnalysisRequest request,
        string outputPath,
        CancellationToken ct)
    {
        var rangeFilter = BuildRangeFilter(request.StartSeconds, request.EndSeconds);
        var audioFilter = string.IsNullOrWhiteSpace(rangeFilter)
            ? "aformat=channel_layouts=mono"
            : rangeFilter + "aformat=channel_layouts=mono";
        var result = await RunFfmpegAsync(
            [
                "-hide_banner", "-nostats", "-y",
                "-i", request.SourcePath,
                "-map", "0:a:0",
                "-c:a", "pcm_s16le",
                "-af", audioFilter,
                outputPath,
            ],
            ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException(
                $"プレビュー音声区間の抽出に失敗しました。{Summarize(result.StandardError)}");
        }
    }

    private async Task<PreviewAnalysisMetrics> AnalyzeMetricsAsync(
        PreviewAnalysisRequest request,
        string normalizedPath,
        CancellationToken ct)
    {
        var before = await AnalyzeLoudnessAsync(
            request.SourcePath,
            request,
            request.StartSeconds,
            request.EndSeconds,
            ct).ConfigureAwait(false);
        var after = await AnalyzeLoudnessAsync(
            normalizedPath,
            request,
            null,
            null,
            ct).ConfigureAwait(false);

        return new PreviewAnalysisMetrics(
            GetMetric(before, "input_i"),
            GetMetric(before, "input_tp"),
            GetMetric(before, "input_lra"),
            GetMetric(after, "input_i"),
            GetMetric(after, "input_tp"),
            GetMetric(after, "input_lra"));
    }

    private async Task<JsonElement> AnalyzeLoudnessAsync(
        string sourcePath,
        PreviewAnalysisRequest request,
        int? startSeconds,
        int? endSeconds,
        CancellationToken ct)
    {
        var loudnorm = string.Create(
            CultureInfo.InvariantCulture,
            $"loudnorm=I={request.TargetLoudnessLufs}:LRA={request.TargetLoudnessRange}:" +
            $"TP={request.TargetTruePeakDb}:print_format=json");
        var result = await RunFfmpegAsync(
            [
                "-hide_banner", "-nostats",
                "-i", sourcePath,
                "-map", "0:a:0",
                "-af", BuildRangeFilter(startSeconds, endSeconds) + loudnorm,
                "-f", "null", "-",
            ],
            ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ラウドネス解析に失敗しました。{Summarize(result.StandardError)}");
        }

        var json = ExtractLastJsonObject(result.StandardError)
            ?? throw new InvalidOperationException("ラウドネス解析結果(JSON)を取得できませんでした。");
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async Task<ProcessResult> RunFfmpegAsync(
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_tools.FfmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        _logger.LogDebug("FFmpeg preview analysis: {Arguments}", string.Join(' ', arguments));
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("FFmpeg の起動に失敗しました。");
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
    }

    private static AudioNormalizationOptions CreateNormalizationOptions(
        PreviewAnalysisRequest request) => new(
        request.TargetLoudnessLufs,
        request.TargetLoudnessRange,
        request.TargetTruePeakDb,
        192);

    private static async Task<PreviewAnalysisMetrics?> ReadMetricsAsync(
        string path,
        CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<PreviewAnalysisMetrics>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildRangeFilter(int? startSeconds, int? endSeconds)
    {
        var parts = new List<string>();
        if (startSeconds is > 0)
        {
            parts.Add($"start={startSeconds.Value.ToString(CultureInfo.InvariantCulture)}");
        }
        if (endSeconds is > 0)
        {
            parts.Add($"end={endSeconds.Value.ToString(CultureInfo.InvariantCulture)}");
        }
        return parts.Count == 0
            ? string.Empty
            : $"atrim={string.Join(':', parts)},asetpts=PTS-STARTPTS,";
    }

    private static string BuildCacheDirectoryPath(string key)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), "torifune-preview-hist-cache", hash);
    }

    private static double GetMetric(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property))
        {
            return 0;
        }

        var raw = property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
        return double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static string? ExtractLastJsonObject(string text)
    {
        var start = text.LastIndexOf('{');
        var end = text.LastIndexOf('}');
        return start < 0 || end <= start ? null : text[start..(end + 1)];
    }

    private static string Summarize(string stderr) =>
        string.IsNullOrWhiteSpace(stderr)
            ? "(FFmpeg エラー詳細なし)"
            : stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim()
              ?? stderr.Trim();

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
