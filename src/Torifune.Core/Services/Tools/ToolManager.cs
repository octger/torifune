using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Torifune.Core.Platform;

namespace Torifune.Core.Services.Tools;

/// <summary>
/// yt-dlp / ffmpeg を GitHub 公式リリースから取得・更新する。
/// バイナリは配布物に同梱せず、ユーザーの明示同意後にユーザー環境へダウンロードする。
/// </summary>
public sealed class ToolManager : IToolManager
{
    private const string YtdlpDownloadBase = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
    private const string FfmpegDownloadBase = "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/";
    private const string YtdlpChecksumManifest = "SHA2-256SUMS";
    private const string FfmpegChecksumManifest = "checksums.sha256";

    private readonly IAppPaths _paths;
    private readonly HttpClient _http;
    private readonly ILogger<ToolManager> _logger;

    public ToolManager(IAppPaths paths, HttpClient http, ILogger<ToolManager> logger)
    {
        _paths = paths;
        _http = http;
        _logger = logger;
    }

    public string YtdlpPath => Path.Combine(_paths.ToolsDirectory, YtdlpAssetName);

    public string FfmpegDirectory => Path.Combine(_paths.ToolsDirectory, "ffmpeg");

    public string FfmpegPath => Path.Combine(FfmpegDirectory, ExecutableName("ffmpeg"));

    private string FfprobePath => Path.Combine(FfmpegDirectory, ExecutableName("ffprobe"));

    public async Task<IReadOnlyList<ToolStatus>> GetStatusAsync(CancellationToken ct = default)
    {
        var results = new List<ToolStatus>(2);

        if (File.Exists(YtdlpPath))
        {
            var version = await TryGetVersionAsync(YtdlpPath, "--version", ct).ConfigureAwait(false);
            results.Add(new ToolStatus(ToolKind.Ytdlp, true, version, YtdlpPath));
        }
        else
        {
            results.Add(new ToolStatus(ToolKind.Ytdlp, false, null, null));
        }

        if (File.Exists(FfmpegPath))
        {
            var version = await TryGetVersionAsync(FfmpegPath, "-version", ct).ConfigureAwait(false);
            // "ffmpeg version N-xxxxx ..." の先頭行から抜粋
            var firstLine = version?.Split('\n').FirstOrDefault()?.Trim();
            results.Add(new ToolStatus(ToolKind.Ffmpeg, true, firstLine, FfmpegPath));
        }
        else
        {
            results.Add(new ToolStatus(ToolKind.Ffmpeg, false, null, null));
        }

        return results;
    }

    public async Task DownloadMissingToolsAsync(
        ToolDownloadConsent consent,
        IProgress<ToolProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!consent.Accepted)
        {
            throw new InvalidOperationException("依存ツールのダウンロードにはユーザーの明示同意が必要です。");
        }

        if (!File.Exists(YtdlpPath))
        {
            await DownloadYtdlpAsync(progress, ct).ConfigureAwait(false);
        }

        if (!File.Exists(FfmpegPath) || !File.Exists(FfprobePath))
        {
            await DownloadFfmpegAsync(progress, ct).ConfigureAwait(false);
        }
    }

    public async Task<bool> UpdateYtdlpAsync(IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(YtdlpPath))
        {
            throw new InvalidOperationException(
                "yt-dlp は未導入です。依存ツールの同意画面からダウンロードしてください。");
        }

        // yt-dlp 自身の自己更新機能を利用する(チャネル固定: stable)
        progress?.Report(new ToolProgress(ToolKind.Ytdlp, "更新確認中", null));
        var psi = new ProcessStartInfo(YtdlpPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--update-to");
        psi.ArgumentList.Add("stable");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("yt-dlp の起動に失敗しました。");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        var updated = stdout.Contains("Updated yt-dlp to", StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation("yt-dlp update result: {Output}", stdout.Trim());
        return updated;
    }

    // ---- yt-dlp ----

    private async Task DownloadYtdlpAsync(IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        var assetName = YtdlpAssetName;
        var url = YtdlpDownloadBase + assetName;
        _logger.LogInformation("Downloading yt-dlp from {Url}", url);

        var tempFile = Path.Combine(_paths.ToolsDirectory, assetName + ".tmp");
        try
        {
            await DownloadFileAsync(url, tempFile, ToolKind.Ytdlp, progress, ct).ConfigureAwait(false);

            // 公式チェックサム(SHA2-256SUMS)で検証
            progress?.Report(new ToolProgress(ToolKind.Ytdlp, "検証中", null));
            await VerifyChecksumAsync(
                    tempFile,
                    YtdlpDownloadBase + YtdlpChecksumManifest,
                    assetName,
                    ct)
                .ConfigureAwait(false);

            File.Move(tempFile, YtdlpPath, overwrite: true);
            MakeExecutableIfNeeded(YtdlpPath);
            progress?.Report(new ToolProgress(ToolKind.Ytdlp, "完了", 100));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    // ---- ffmpeg ----

    private async Task DownloadFfmpegAsync(IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        var assetName = FfmpegAssetName;
        var url = FfmpegDownloadBase + assetName;
        _logger.LogInformation("Downloading ffmpeg from {Url}", url);

        var tempZip = Path.Combine(_paths.ToolsDirectory, assetName + ".tmp");
        try
        {
            await DownloadFileAsync(url, tempZip, ToolKind.Ffmpeg, progress, ct).ConfigureAwait(false);

            progress?.Report(new ToolProgress(ToolKind.Ffmpeg, "検証中", null));
            await VerifyChecksumAsync(
                    tempZip,
                    FfmpegDownloadBase + FfmpegChecksumManifest,
                    assetName,
                    ct)
                .ConfigureAwait(false);

            progress?.Report(new ToolProgress(ToolKind.Ffmpeg, "展開中", null));
            Directory.CreateDirectory(FfmpegDirectory);

            using var archive = ZipFile.OpenRead(tempZip);
            foreach (var entry in archive.Entries)
            {
                // zip 内の bin/ffmpeg.exe, bin/ffprobe.exe のみ抽出(トップレベルフォルダ名は可変)
                var fileName = Path.GetFileName(entry.FullName);
                if (fileName is not ("ffmpeg.exe" or "ffprobe.exe" or "ffmpeg" or "ffprobe"))
                {
                    continue;
                }
                var dest = Path.Combine(FfmpegDirectory, fileName);
                entry.ExtractToFile(dest, overwrite: true);
                MakeExecutableIfNeeded(dest);
            }

            if (!File.Exists(FfmpegPath) || !File.Exists(FfprobePath))
            {
                throw new InvalidOperationException("ffmpeg アーカイブに ffmpeg/ffprobe 実行ファイルが見つかりませんでした。");
            }
            progress?.Report(new ToolProgress(ToolKind.Ffmpeg, "完了", 100));
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                File.Delete(tempZip);
            }
        }
    }

    // ---- 共通ヘルパ ----

    private async Task DownloadFileAsync(
        string url, string destPath, ToolKind kind, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dest = File.Create(destPath);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total is > 0)
            {
                progress?.Report(new ToolProgress(kind, "ダウンロード中", read * 100.0 / total.Value));
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private async Task VerifyChecksumAsync(
        string filePath,
        string manifestUrl,
        string assetName,
        CancellationToken ct)
    {
        string manifest;
        try
        {
            manifest = await _http.GetStringAsync(manifestUrl, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"{assetName} の公式チェックサムを取得できないため、インストールを中止しました。",
                ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"{assetName} の公式チェックサム取得がタイムアウトしたため、インストールを中止しました。",
                ex);
        }

        var expected = ParseChecksum(manifest, assetName)
            ?? throw new InvalidOperationException(
                $"公式チェックサム一覧に {assetName} がないため、インストールを中止しました。");
        var actual = await ComputeSha256Async(filePath, ct).ConfigureAwait(false);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{assetName} のチェックサム検証に失敗したため、インストールを中止しました。");
        }

        _logger.LogInformation("Checksum verified: {AssetName} sha256={Checksum}", assetName, actual);
    }

    private static string? ParseChecksum(string manifest, string assetName)
    {
        foreach (var line in manifest.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var fileName = parts[^1].TrimStart('*');
            if (!string.Equals(fileName, assetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var checksum = parts[0];
            return checksum.Length == 64 && checksum.All(Uri.IsHexDigit)
                ? checksum
                : null;
        }

        return null;
    }

    private static async Task<string?> TryGetVersionAsync(string exePath, string arg, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }
            var output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string YtdlpAssetName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "yt-dlp_macos"
        : "yt-dlp";

    private static string FfmpegAssetName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "ffmpeg-master-latest-win64-gpl.zip"
            : "ffmpeg-master-latest-linux64-gpl.tar.xz"; // Linux/macOS は将来対応

    private static string ExecutableName(string baseName) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? baseName + ".exe" : baseName;

    private static void MakeExecutableIfNeeded(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
