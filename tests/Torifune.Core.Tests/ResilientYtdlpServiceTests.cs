using Microsoft.Extensions.Logging.Abstractions;
using Torifune.Core.Models;
using Torifune.Core.Services.Ytdlp;

namespace Torifune.Core.Tests;

public sealed class ResilientYtdlpServiceTests
{
    [Fact]
    public async Task 進捗停止時は生実行をキャンセルして1回再試行する()
    {
        var raw = new StallingYtdlpProcessService(successfulAttempt: 2);
        var messages = new List<string>();
        var service = CreateService(raw);

        var result = await service.DownloadAsync(
            CreateOptions(),
            new InlineProgress<DownloadProgress>(value =>
            {
                if (!string.IsNullOrWhiteSpace(value.Message))
                {
                    messages.Add(value.Message);
                }
            }));

        Assert.True(result.Success);
        Assert.Equal(2, raw.DownloadAttempts);
        Assert.Equal(1, raw.CanceledAttempts);
        Assert.Contains(messages, message => message.Contains("再試行", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 再試行後も進捗停止した場合はタイムアウトとして終了する()
    {
        var raw = new StallingYtdlpProcessService(successfulAttempt: null);
        var service = CreateService(raw);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            service.DownloadAsync(CreateOptions()));

        Assert.Equal(2, raw.DownloadAttempts);
        Assert.Equal(2, raw.CanceledAttempts);
    }

    [Fact]
    public async Task 利用者キャンセル時は自動再試行しない()
    {
        var raw = new StallingYtdlpProcessService(successfulAttempt: null);
        var service = CreateService(raw);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DownloadAsync(CreateOptions(), ct: cts.Token));

        Assert.Equal(1, raw.DownloadAttempts);
    }

    [Fact]
    public async Task メタデータ取得停止時もキャンセルして再試行する()
    {
        var raw = new MetadataStallingYtdlpProcessService();
        var service = CreateService(raw);

        var result = await service.FetchMediaInfoAsync("https://example.com/video");

        Assert.Equal("https://example.com/video", result.Url);
        Assert.Equal(2, raw.MetadataAttempts);
        Assert.Equal(1, raw.CanceledAttempts);
    }

    private static ResilientYtdlpService CreateService(IYtdlpProcessService raw) => new(
        raw,
        new YtdlpRecoveryOptions
        {
            MonitorInterval = TimeSpan.FromMilliseconds(10),
            MetadataTimeout = TimeSpan.FromMilliseconds(60),
            PreparingTimeout = TimeSpan.FromMilliseconds(60),
            MinimumDownloadTimeout = TimeSpan.FromMilliseconds(60),
            DefaultDownloadTimeout = TimeSpan.FromMilliseconds(60),
            MaximumDownloadTimeout = TimeSpan.FromMilliseconds(100),
            PostProcessingTimeout = TimeSpan.FromMilliseconds(60),
            DefaultNormalizationTimeout = TimeSpan.FromMilliseconds(60),
            MinimumNormalizationTimeout = TimeSpan.FromMilliseconds(60),
            MaximumNormalizationTimeout = TimeSpan.FromMilliseconds(100),
            CancellationWaitTimeout = TimeSpan.FromMilliseconds(500),
            RetryDelay = TimeSpan.Zero,
        },
        NullLogger<ResilientYtdlpService>.Instance);

    private static DownloadOptions CreateOptions() => new()
    {
        Url = "https://example.com/video",
        OutputDirectory = Path.GetTempPath(),
    };

    private sealed class StallingYtdlpProcessService(int? successfulAttempt) : IYtdlpProcessService
    {
        public int DownloadAttempts { get; private set; }
        public int CanceledAttempts { get; private set; }

        public Task<MediaInfo> FetchMediaInfoAsync(string url, CancellationToken ct = default) =>
            Task.FromResult(new MediaInfo { Url = url });

        public async Task<DownloadResult> DownloadAsync(
            DownloadOptions options,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            DownloadAttempts++;
            if (DownloadAttempts == successfulAttempt)
            {
                return new DownloadResult(true, "completed.mp4", null);
            }

            progress?.Report(new DownloadProgress(
                DownloadState.Downloading,
                10,
                100,
                1000,
                100,
                TimeSpan.FromMilliseconds(30),
                null));
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("到達しません。");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CanceledAttempts++;
                throw;
            }
        }
    }

    private sealed class MetadataStallingYtdlpProcessService : IYtdlpProcessService
    {
        public int MetadataAttempts { get; private set; }
        public int CanceledAttempts { get; private set; }

        public async Task<MediaInfo> FetchMediaInfoAsync(
            string url,
            CancellationToken ct = default)
        {
            MetadataAttempts++;
            if (MetadataAttempts == 2)
            {
                return new MediaInfo { Url = url };
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("到達しません。");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CanceledAttempts++;
                throw;
            }
        }

        public Task<DownloadResult> DownloadAsync(
            DownloadOptions options,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
