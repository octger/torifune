using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Torifune.Core.Models;
using Torifune.Core.Platform;
using Torifune.Core.Services.PostProcessing;
using Torifune.Core.Services.Queue;
using Torifune.Core.Services.Ytdlp;

namespace Torifune.Core.Tests;

public sealed class DownloadQueueServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), "Torifune.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void 既存キューとの状態番号互換性を維持する()
    {
        Assert.Equal(0, (int)DownloadQueueStatus.Queued);
        Assert.Equal(4, (int)DownloadQueueStatus.Paused);
        Assert.Equal(5, (int)DownloadQueueStatus.Completed);
        Assert.Equal(6, (int)DownloadQueueStatus.Failed);
        Assert.Equal(7, (int)DownloadQueueStatus.Canceled);
        Assert.Equal(8, (int)DownloadQueueStatus.Upscaling);
    }

    [Fact]
    public async Task 最大同時実行数を超えず空きスロットへ次項目を投入する()
    {
        var ytdlp = new ControlledYtdlpService();
        await using var queue = CreateQueue(ytdlp);
        queue.MaxConcurrentDownloads = 2;

        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await queue.EnqueueAsync($"item-{i}", CreateOptions(i)));
        }

        await WaitUntilAsync(() => ytdlp.ActiveCount == 2);
        Assert.Equal(2, ytdlp.MaxObservedActiveCount);
        Assert.Equal(2, queue.Items.Count(item => item.Status == DownloadQueueStatus.Running));
        Assert.Equal(2, queue.Items.Count(item => item.Status == DownloadQueueStatus.Queued));

        ytdlp.Complete(0);
        await WaitUntilAsync(() => ytdlp.StartedIndexes.Contains(2));

        Assert.True(ytdlp.MaxObservedActiveCount <= 2);
        Assert.Equal(2, ytdlp.ActiveCount);
    }

    [Fact]
    public async Task 実行中項目を一時停止して再開できる()
    {
        var ytdlp = new ControlledYtdlpService();
        await using var queue = CreateQueue(ytdlp);
        queue.MaxConcurrentDownloads = 1;
        var id = await queue.EnqueueAsync("pause-test", CreateOptions(1));

        await WaitUntilAsync(() => ytdlp.ActiveCount == 1);
        await queue.PauseAsync(id);
        await WaitUntilAsync(() => ytdlp.ActiveCount == 0);
        Assert.Equal(DownloadQueueStatus.Paused, queue.Items.Single().Status);

        await queue.ResumeAsync(id);
        await WaitUntilAsync(() => ytdlp.InvocationCount(1) == 2);
        Assert.Equal(DownloadQueueStatus.Running, queue.Items.Single().Status);
    }

    [Fact]
    public async Task 実行中だった永続化項目は一時停止として復元する()
    {
        var paths = new TestAppPaths(_tempDirectory);
        Directory.CreateDirectory(paths.ConfigDirectory);
        var persisted = new DownloadQueueItem
        {
            Id = Guid.NewGuid(),
            Title = "restore-test",
            Options = CreateOptions(1),
            Status = DownloadQueueStatus.Running,
            ProgressPercent = 42,
            SpeedBytesPerSec = 1000,
            Eta = TimeSpan.FromSeconds(10),
        };
        await File.WriteAllTextAsync(
            Path.Combine(paths.ConfigDirectory, "queue.json"),
            JsonSerializer.Serialize(new[] { persisted }, new JsonSerializerOptions { WriteIndented = true }));

        await using var queue = new DownloadQueueService(
            new ControlledYtdlpService(),
            new NoOpPostProcessingService(),
            paths,
            NullLogger<DownloadQueueService>.Instance);
        await queue.LoadAsync();

        var restored = Assert.Single(queue.Items);
        Assert.Equal(DownloadQueueStatus.Paused, restored.Status);
        Assert.Equal(42, restored.ProgressPercent);
        Assert.Null(restored.SpeedBytesPerSec);
        Assert.Null(restored.Eta);
    }

    [Fact]
    public async Task 完了済みの低解像度項目をFHDへ変換して情報を更新できる()
    {
        var paths = new TestAppPaths(_tempDirectory);
        Directory.CreateDirectory(paths.ConfigDirectory);
        var outputPath = Path.Combine(_tempDirectory, "completed.mp4");
        Directory.CreateDirectory(_tempDirectory);
        await File.WriteAllTextAsync(outputPath, "media");
        var id = Guid.NewGuid();
        var persisted = new DownloadQueueItem
        {
            Id = id,
            Title = "upscale-test",
            Options = CreateOptions(1),
            Status = DownloadQueueStatus.Completed,
            OutputPath = outputPath,
            VideoWidth = 1280,
            VideoHeight = 720,
            CompletedAt = DateTimeOffset.UtcNow,
        };
        await File.WriteAllTextAsync(
            Path.Combine(paths.ConfigDirectory, "queue.json"),
            JsonSerializer.Serialize(new[] { persisted }));
        var postProcessing = new RecordingPostProcessingService();
        await using var queue = new DownloadQueueService(
            new ControlledYtdlpService(),
            postProcessing,
            paths,
            NullLogger<DownloadQueueService>.Instance);
        await queue.LoadAsync();

        await queue.UpscaleCompletedToFhdAsync(id);
        await WaitUntilAsync(() => postProcessing.CallCount == 1 &&
                                   queue.Items.Single().Status == DownloadQueueStatus.Completed &&
                                   queue.Items.Single().VideoWidth == 1920);

        var converted = queue.Items.Single();
        Assert.Equal(1920, converted.VideoWidth);
        Assert.Equal(1080, converted.VideoHeight);
        Assert.Equal(outputPath, converted.OutputPath);
        Assert.True(postProcessing.LastOptions?.UpscaleToFhd);
        Assert.False(postProcessing.LastOptions?.NormalizeAudio);
    }

    [Fact]
    public async Task 手動FHD変換中だった項目は元ファイルを保持した完了状態へ復元する()
    {
        var paths = new TestAppPaths(_tempDirectory);
        Directory.CreateDirectory(paths.ConfigDirectory);
        var outputPath = Path.Combine(_tempDirectory, "interrupted.mp4");
        Directory.CreateDirectory(_tempDirectory);
        await File.WriteAllTextAsync(outputPath, "media");
        var persisted = new DownloadQueueItem
        {
            Id = Guid.NewGuid(),
            Title = "interrupted-upscale",
            Options = CreateOptions(1),
            Status = DownloadQueueStatus.Upscaling,
            IsManualPostProcessing = true,
            OutputPath = outputPath,
            VideoWidth = 1280,
            VideoHeight = 720,
        };
        await File.WriteAllTextAsync(
            Path.Combine(paths.ConfigDirectory, "queue.json"),
            JsonSerializer.Serialize(new[] { persisted }));
        await using var queue = new DownloadQueueService(
            new ControlledYtdlpService(),
            new NoOpPostProcessingService(),
            paths,
            NullLogger<DownloadQueueService>.Instance);

        await queue.LoadAsync();

        var restored = Assert.Single(queue.Items);
        Assert.Equal(DownloadQueueStatus.Completed, restored.Status);
        Assert.False(restored.IsManualPostProcessing);
        Assert.Equal(outputPath, restored.OutputPath);
        Assert.Contains("中断", restored.ErrorMessage);
    }

    private DownloadQueueService CreateQueue(IYtdlpService ytdlp) => new(
        ytdlp,
        new NoOpPostProcessingService(),
        new TestAppPaths(_tempDirectory),
        NullLogger<DownloadQueueService>.Instance);

    private static DownloadOptions CreateOptions(int index) => new()
    {
        Url = $"https://example.com/watch?v={index}",
        OutputDirectory = Path.GetTempPath(),
    };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private sealed class TestAppPaths(string root) : IAppPaths
    {
        public string ConfigDirectory { get; } = Path.Combine(root, "config");
        public string LocalDataDirectory { get; } = Path.Combine(root, "local");
        public string ToolsDirectory { get; } = Path.Combine(root, "tools");
        public string LogsDirectory { get; } = Path.Combine(root, "logs");
    }

    private sealed class ControlledYtdlpService : IYtdlpService
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _gates = [];
        private readonly ConcurrentDictionary<int, int> _invocations = [];
        private int _activeCount;
        private int _maxObservedActiveCount;

        public int ActiveCount => Volatile.Read(ref _activeCount);
        public int MaxObservedActiveCount => Volatile.Read(ref _maxObservedActiveCount);
        public IReadOnlyCollection<int> StartedIndexes => [.. _gates.Keys];
        public int InvocationCount(int index) => _invocations.GetValueOrDefault(index);

        public Task<MediaInfo> FetchMediaInfoAsync(string url, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async Task<DownloadResult> DownloadAsync(
            DownloadOptions options,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            var index = int.Parse(options.Url[(options.Url.LastIndexOf('=') + 1)..]);
            _invocations.AddOrUpdate(index, 1, (_, count) => count + 1);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates[index] = gate;

            var active = Interlocked.Increment(ref _activeCount);
            UpdateMax(active);
            try
            {
                progress?.Report(new DownloadProgress(
                    DownloadState.Downloading, 10, 10, 100, 1000, TimeSpan.FromSeconds(1), null));
                await gate.Task.WaitAsync(ct);
                return new DownloadResult(true, $"{index}.mp4", null);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCount);
            }
        }

        public void Complete(int index)
        {
            if (_gates.TryGetValue(index, out var gate))
            {
                gate.TrySetResult();
            }
        }

        private void UpdateMax(int active)
        {
            int current;
            while (active > (current = Volatile.Read(ref _maxObservedActiveCount)) &&
                   Interlocked.CompareExchange(ref _maxObservedActiveCount, active, current) != current)
            {
            }
        }
    }

    private sealed class NoOpPostProcessingService : IMediaPostProcessingService
    {
        public Task<MediaPostProcessingResult> ProcessAsync(
            string mediaPath,
            MediaPostProcessingOptions options,
            IProgress<MediaPostProcessingProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(
            new MediaPostProcessingResult(mediaPath, false, false, null, null));
    }

    private sealed class RecordingPostProcessingService : IMediaPostProcessingService
    {
        public int CallCount { get; private set; }
        public MediaPostProcessingOptions? LastOptions { get; private set; }

        public Task<MediaPostProcessingResult> ProcessAsync(
            string mediaPath,
            MediaPostProcessingOptions options,
            IProgress<MediaPostProcessingProgress>? progress = null,
            CancellationToken ct = default)
        {
            CallCount++;
            LastOptions = options;
            return Task.FromResult(new MediaPostProcessingResult(
                mediaPath,
                false,
                true,
                1920,
                1080));
        }
    }
}
