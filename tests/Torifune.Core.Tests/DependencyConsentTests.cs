using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Torifune.Core.Models;
using Torifune.Core.Platform;
using Torifune.Core.Services.Queue;
using Torifune.Core.Services.Preview;
using Torifune.Core.Services.Settings;
using Torifune.Core.Services.Tools;
using Torifune.Core.Services.Ytdlp;
using Torifune.ViewModels;

namespace Torifune.Core.Tests;

public class DependencyConsentTests
{
    [Fact]
    public async Task ToolManagerは未同意のダウンロード要求を拒否する()
    {
        var root = Path.Combine(Path.GetTempPath(), "Torifune.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new ToolManager(
                new TemporaryAppPaths(root),
                new HttpClient(),
                NullLogger<ToolManager>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.DownloadMissingToolsAsync(
                    new ToolDownloadConsent(false, DateTimeOffset.UtcNow)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ToolManagerは公式チェックサムを取得できなければ導入を拒否する()
    {
        var root = Path.Combine(Path.GetTempPath(), "Torifune.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "tools"));
            var handler = new StubHttpMessageHandler(request =>
                request.RequestUri!.AbsolutePath.EndsWith("SHA2-256SUMS", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : CreateBytesResponse("binary"u8.ToArray()));
            var manager = new ToolManager(
                new TemporaryAppPaths(root),
                new HttpClient(handler),
                NullLogger<ToolManager>.Instance);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.DownloadMissingToolsAsync(ToolDownloadConsent.GrantedNow()));

            Assert.Contains("公式チェックサム", error.Message);
            Assert.False(File.Exists(manager.YtdlpPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ToolManagerはFFmpegの公式チェックサムに対象がなければ導入を拒否する()
    {
        var root = Path.Combine(Path.GetTempPath(), "Torifune.Tests", Guid.NewGuid().ToString("N"));
        var ytdlpBytes = "verified-ytdlp"u8.ToArray();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "tools"));
            var handler = new StubHttpMessageHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("SHA2-256SUMS", StringComparison.Ordinal))
                {
                    var assetName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe"
                        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "yt-dlp_macos"
                        : "yt-dlp";
                    var checksum = Convert.ToHexString(SHA256.HashData(ytdlpBytes));
                    return CreateTextResponse($"{checksum}  {assetName}\n");
                }

                if (path.EndsWith("checksums.sha256", StringComparison.Ordinal))
                {
                    return CreateTextResponse("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  other.zip\n");
                }

                return path.EndsWith("yt-dlp.exe", StringComparison.Ordinal) ||
                       path.EndsWith("yt-dlp_macos", StringComparison.Ordinal) ||
                       path.EndsWith("/yt-dlp", StringComparison.Ordinal)
                    ? CreateBytesResponse(ytdlpBytes)
                    : CreateBytesResponse("ffmpeg-archive"u8.ToArray());
            });
            var manager = new ToolManager(
                new TemporaryAppPaths(root),
                new HttpClient(handler),
                NullLogger<ToolManager>.Instance);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.DownloadMissingToolsAsync(ToolDownloadConsent.GrantedNow()));

            Assert.Contains("公式チェックサム一覧", error.Message);
            Assert.True(File.Exists(manager.YtdlpPath));
            Assert.False(File.Exists(manager.FfmpegPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task 起動時は未導入ツールをダウンロードせず同意を要求する()
    {
        var tools = new ConsentToolManager();
        var viewModel = CreateViewModel(tools);

        await viewModel.InitializeCommand.ExecuteAsync(null);

        Assert.Equal(0, tools.DownloadCallCount);
        Assert.True(viewModel.IsDependencyConsentRequired);
        Assert.False(viewModel.IsReady);
        Assert.Contains("yt-dlp", viewModel.MissingDependenciesText);
        Assert.Contains("FFmpeg", viewModel.MissingDependenciesText);
    }

    [Fact]
    public async Task チェック後の同意コマンドからのみ依存ツールを取得する()
    {
        var tools = new ConsentToolManager();
        var queue = new NoOpQueueService();
        var viewModel = CreateViewModel(tools, queue);
        await viewModel.InitializeCommand.ExecuteAsync(null);

        Assert.False(viewModel.AcceptDependencyDownloadCommand.CanExecute(null));
        viewModel.HasAcceptedDependencyTerms = true;
        Assert.True(viewModel.AcceptDependencyDownloadCommand.CanExecute(null));

        await viewModel.AcceptDependencyDownloadCommand.ExecuteAsync(null);

        Assert.Equal(1, tools.DownloadCallCount);
        Assert.True(tools.LastConsent?.Accepted);
        Assert.False(viewModel.IsDependencyConsentRequired);
        Assert.True(viewModel.IsReady);
        Assert.Equal(1, queue.LoadCallCount);
    }

    [Fact]
    public async Task 設定の並列数変更は即時にキューへ反映される()
    {
        var tools = new ConsentToolManager();
        tools.ForceInstalled();
        var queue = new NoOpQueueService();
        var viewModel = CreateViewModel(
            tools,
            queue,
            new InMemorySettingsStore(new AppSettings { MaxConcurrentDownloads = 2 }));

        await viewModel.InitializeCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsReady);
        Assert.Equal(2, queue.MaxConcurrentDownloads);

        viewModel.DefaultMaxConcurrentDownloads = 4;
        Assert.Equal(4, queue.MaxConcurrentDownloads);
        Assert.Contains("即時反映", viewModel.SettingsMessage);
    }

    [Fact]
    public async Task 変更した保存先は即時保存され次回設定として保持される()
    {
        var root = Path.Combine(Path.GetTempPath(), "Torifune.Tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "downloads");
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var tools = new ConsentToolManager();
            tools.ForceInstalled();
            var settingsStore = new InMemorySettingsStore();
            var viewModel = CreateViewModel(tools, settingsStore: settingsStore);
            await viewModel.InitializeCommand.ExecuteAsync(null);

            await viewModel.SetOutputDirectoryCommand.ExecuteAsync(outputDirectory);

            Assert.Equal(outputDirectory, viewModel.DefaultOutputDirectory);
            Assert.Equal(outputDirectory, settingsStore.Current.DefaultOutputDirectory);
            Assert.Contains("次回起動時", viewModel.SettingsMessage);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task 起動時に保存先が存在しなければ既定パスへ戻して保存する()
    {
        var missingDirectory = Path.Combine(
            Path.GetTempPath(),
            "Torifune.Tests",
            Guid.NewGuid().ToString("N"),
            "missing");
        var tools = new ConsentToolManager();
        tools.ForceInstalled();
        var settingsStore = new InMemorySettingsStore(new AppSettings
        {
            DefaultOutputDirectory = missingDirectory,
        });
        var viewModel = CreateViewModel(tools, settingsStore: settingsStore);

        await viewModel.InitializeCommand.ExecuteAsync(null);

        Assert.NotEqual(missingDirectory, viewModel.DefaultOutputDirectory);
        Assert.True(Directory.Exists(viewModel.DefaultOutputDirectory));
        Assert.Equal(viewModel.DefaultOutputDirectory, settingsStore.Current.DefaultOutputDirectory);
        Assert.Contains("既定のDownloads", viewModel.SettingsMessage);
    }

    private static MainViewModel CreateViewModel(
        ConsentToolManager tools,
        NoOpQueueService? queue = null,
        InMemorySettingsStore? settingsStore = null) => new(
            tools,
            new NoOpYtdlpService(),
            new NoOpPreviewSourceService(),
            new NoOpPreviewAnalysisService(),
            queue ?? new NoOpQueueService(),
            settingsStore ?? new InMemorySettingsStore(),
            NullLogger<MainViewModel>.Instance);

    private sealed class ConsentToolManager : IToolManager
    {
        private bool _installed;
        public int DownloadCallCount { get; private set; }
        public ToolDownloadConsent? LastConsent { get; private set; }
        public string YtdlpPath => "yt-dlp.exe";
        public string FfmpegPath => "ffmpeg.exe";
        public string FfmpegDirectory => "ffmpeg";

        public Task<IReadOnlyList<ToolStatus>> GetStatusAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolStatus>>
            ([
                new(ToolKind.Ytdlp, _installed, _installed ? "test" : null, null),
                new(ToolKind.Ffmpeg, _installed, _installed ? "test" : null, null),
            ]);

        public Task DownloadMissingToolsAsync(
            ToolDownloadConsent consent,
            IProgress<ToolProgress>? progress = null,
            CancellationToken ct = default)
        {
            DownloadCallCount++;
            LastConsent = consent;
            _installed = true;
            return Task.CompletedTask;
        }

        public Task<bool> UpdateYtdlpAsync(
            IProgress<ToolProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(false);

        public void ForceInstalled() => _installed = true;
    }

    private sealed class TemporaryAppPaths(string root) : IAppPaths
    {
        public string ConfigDirectory { get; } = Path.Combine(root, "config");
        public string LocalDataDirectory { get; } = Path.Combine(root, "local");
        public string ToolsDirectory { get; } = Path.Combine(root, "tools");
        public string LogsDirectory { get; } = Path.Combine(root, "logs");
    }

    private sealed class NoOpYtdlpService : IYtdlpService
    {
        public Task<MediaInfo> FetchMediaInfoAsync(string url, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DownloadResult> DownloadAsync(
            DownloadOptions options,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoOpPreviewSourceService : IPreviewSourceService
    {
        public Task<PreviewSourceResult> EnsureSourceAsync(
            string url,
            string formatString,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<double?> ProbeDurationSecondsAsync(
            string videoPath,
            CancellationToken ct = default) => Task.FromResult<double?>(null);
    }

    private sealed class NoOpPreviewAnalysisService : IPreviewAnalysisService
    {
        public Task<PreviewAnalysisResult> AnalyzeAsync(
            PreviewAnalysisRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoOpQueueService : IDownloadQueueService
    {
        public int LoadCallCount { get; private set; }
        public event EventHandler<IReadOnlyList<DownloadQueueItem>>? ItemsChanged;
        public IReadOnlyList<DownloadQueueItem> Items => [];
        public int MaxConcurrentDownloads { get; set; } = 3;

        public Task LoadAsync(CancellationToken ct = default)
        {
            LoadCallCount++;
            ItemsChanged?.Invoke(this, []);
            return Task.CompletedTask;
        }

        public Task<Guid> EnqueueAsync(string title, DownloadOptions options, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task PauseAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResumeAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CancelAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RetryAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpscaleCompletedToFhdAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InMemorySettingsStore : IAppSettingsStore
    {
        private AppSettings _settings;

        public AppSettings Current => _settings;

        public InMemorySettingsStore(AppSettings? initial = null)
        {
            _settings = initial ?? new AppSettings();
        }

        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(_settings);

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    private static HttpResponseMessage CreateBytesResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
    };

    private static HttpResponseMessage CreateTextResponse(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8),
    };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
