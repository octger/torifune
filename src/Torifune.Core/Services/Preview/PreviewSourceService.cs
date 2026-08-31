using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Torifune.Core.Models;
using Torifune.Core.Services.Tools;
using Torifune.Core.Services.Ytdlp;

namespace Torifune.Core.Services.Preview;

public sealed class PreviewSourceService : IPreviewSourceService
{
    private readonly IYtdlpService _ytdlp;
    private readonly IToolManager _tools;
    private readonly ILogger<PreviewSourceService> _logger;

    public PreviewSourceService(
        IYtdlpService ytdlp,
        IToolManager tools,
        ILogger<PreviewSourceService> logger)
    {
        _ytdlp = ytdlp;
        _tools = tools;
        _logger = logger;
    }

    public async Task<PreviewSourceResult> EnsureSourceAsync(
        string url,
        string formatString,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var cacheDirectory = BuildCacheDirectoryPath(url, formatString);
        Directory.CreateDirectory(cacheDirectory);

        var existing = ResolveExistingSource(cacheDirectory);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return new PreviewSourceResult(existing, FromCache: true);
        }

        var options = new DownloadOptions
        {
            Url = url,
            OutputDirectory = cacheDirectory,
            OutputTemplate = "preview-source.%(ext)s",
            FormatString = formatString,
            FormatSort = null,
            RemuxTo = null,
            MergeOutputFormat = null,
            NormalizeAudio = false,
            StartTimeSeconds = null,
            EndTimeSeconds = null,
        };

        var result = await _ytdlp.DownloadAsync(options, progress, ct).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.OutputPath) || !File.Exists(result.OutputPath))
        {
            throw new InvalidOperationException(result.ErrorMessage ?? "確認用動画の取得に失敗しました。");
        }

        return new PreviewSourceResult(result.OutputPath, FromCache: false);
    }

    public async Task<double?> ProbeDurationSecondsAsync(string videoPath, CancellationToken ct = default)
    {
        var ffprobePath = Path.Combine(
            _tools.FfmpegDirectory,
            OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (!File.Exists(ffprobePath))
        {
            return null;
        }

        var psi = new ProcessStartInfo(ffprobePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-v", "error",
                     "-show_entries", "format=duration",
                     "-of", "default=noprint_wrappers=1:nokey=1",
                     videoPath,
                 })
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                _logger.LogDebug("ffprobe duration detection failed: {Error}", stderr.Trim());
                return null;
            }

            return double.TryParse(
                stdout.Trim(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var duration)
                ? duration
                : null;
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

    private static string BuildCacheDirectoryPath(string url, string formatString)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{url}|{formatString}"));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), "torifune-preview-cache", hash);
    }

    private static string? ResolveExistingSource(string cacheDirectory) =>
        Directory
            .EnumerateFiles(cacheDirectory, "preview-source.*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault(File.Exists);
}
