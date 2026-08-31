using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Torifune.Core.Models;
using Torifune.Core.Services.Preview;
using Torifune.Core.Services.Tools;
using Torifune.Core.Services.Ytdlp;

namespace Torifune.Core.Tests;

public sealed class PreviewSourceServiceTests : IDisposable
{
    private readonly string _url = $"https://example.com/watch?v={Guid.NewGuid():N}";
    private readonly List<string> _cacheDirectories = [];

    [Fact]
    public async Task 軽量動画を指定形式で取得し次回はキャッシュを再利用する()
    {
        var ytdlp = new RecordingYtdlpService();
        var service = new PreviewSourceService(
            ytdlp,
            new NoOpToolManager(),
            NullLogger<PreviewSourceService>.Instance);

        const string format = "bestvideo[height<=480]+bestaudio/best";
        _cacheDirectories.Add(BuildCacheDirectoryPath(_url, format));

        var first = await service.EnsureSourceAsync(_url, format);
        var second = await service.EnsureSourceAsync(_url, format);

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(1, ytdlp.DownloadCallCount);
        Assert.NotNull(ytdlp.LastOptions);
        Assert.Equal(format, ytdlp.LastOptions.FormatString);
        Assert.Equal("preview-source.%(ext)s", ytdlp.LastOptions.OutputTemplate);
        Assert.False(ytdlp.LastOptions.NormalizeAudio);
        Assert.Null(ytdlp.LastOptions.RemuxTo);
        Assert.Null(ytdlp.LastOptions.MergeOutputFormat);
    }

    [Fact]
    public async Task 画質形式が異なる場合は別キャッシュとして取得する()
    {
        const string fastFormat = "bestvideo[height<=360]+bestaudio/best";
        const string visualFormat = "bestvideo[height<=720]+bestaudio/best";
        _cacheDirectories.Add(BuildCacheDirectoryPath(_url, fastFormat));
        _cacheDirectories.Add(BuildCacheDirectoryPath(_url, visualFormat));
        var ytdlp = new RecordingYtdlpService();
        var service = new PreviewSourceService(
            ytdlp,
            new NoOpToolManager(),
            NullLogger<PreviewSourceService>.Instance);

        var fast = await service.EnsureSourceAsync(_url, fastFormat);
        var visual = await service.EnsureSourceAsync(_url, visualFormat);

        Assert.False(fast.FromCache);
        Assert.False(visual.FromCache);
        Assert.NotEqual(fast.Path, visual.Path);
        Assert.Equal(2, ytdlp.DownloadCallCount);
    }

    public void Dispose()
    {
        foreach (var cacheDirectory in _cacheDirectories)
        {
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }

    private static string BuildCacheDirectoryPath(string url, string formatString)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{url}|{formatString}"));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), "torifune-preview-cache", hash);
    }

    private sealed class RecordingYtdlpService : IYtdlpService
    {
        public int DownloadCallCount { get; private set; }
        public DownloadOptions? LastOptions { get; private set; }

        public Task<MediaInfo> FetchMediaInfoAsync(string url, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async Task<DownloadResult> DownloadAsync(
            DownloadOptions options,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            DownloadCallCount++;
            LastOptions = options;
            var outputPath = Path.Combine(options.OutputDirectory, "preview-source.mp4");
            await File.WriteAllTextAsync(outputPath, "preview", ct);
            return new DownloadResult(true, outputPath, null);
        }
    }

    private sealed class NoOpToolManager : IToolManager
    {
        public string YtdlpPath => "yt-dlp";
        public string FfmpegPath => "ffmpeg";
        public string FfmpegDirectory => "ffmpeg";

        public Task<IReadOnlyList<ToolStatus>> GetStatusAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolStatus>>([]);

        public Task DownloadMissingToolsAsync(
            ToolDownloadConsent consent,
            IProgress<ToolProgress>? progress = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> UpdateYtdlpAsync(
            IProgress<ToolProgress>? progress = null,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
