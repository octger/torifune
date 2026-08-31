using Microsoft.Extensions.Logging;
using Torifune.Core.Models;

namespace Torifune.Core.Services.Ytdlp;

public sealed record YtdlpRecoveryOptions
{
    public int MaxAttempts { get; init; } = 2;
    public TimeSpan MonitorInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan PreparingTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MinimumDownloadTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan DefaultDownloadTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaximumDownloadTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan PostProcessingTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan DefaultNormalizationTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan MinimumNormalizationTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaximumNormalizationTimeout { get; init; } = TimeSpan.FromHours(2);
    public TimeSpan CancellationWaitTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>yt-dlp実行を監視し、進捗停止時にプロセスをキャンセルして1回だけ再試行する。</summary>
public sealed class ResilientYtdlpService : IYtdlpService
{
    private readonly IYtdlpProcessService _inner;
    private readonly YtdlpRecoveryOptions _options;
    private readonly ILogger<ResilientYtdlpService> _logger;

    public ResilientYtdlpService(
        IYtdlpProcessService inner,
        YtdlpRecoveryOptions options,
        ILogger<ResilientYtdlpService> logger)
    {
        _inner = inner;
        _options = options;
        _logger = logger;
    }

    public Task<MediaInfo> FetchMediaInfoAsync(string url, CancellationToken ct = default) =>
        ExecuteWithFixedTimeoutAsync(
            token => _inner.FetchMediaInfoAsync(url, token),
            "メタデータ取得",
            _options.MetadataTimeout,
            ct);

    public async Task<DownloadResult> DownloadAsync(
        DownloadOptions options,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tracker = new ProgressTracker(options, _options);
            var forwardingProgress = new InlineProgress<DownloadProgress>(value =>
            {
                tracker.Report(value);
                progress?.Report(value);
            });

            var operation = _inner.DownloadAsync(options, forwardingProgress, attemptCts.Token);
            var monitor = MonitorForStallAsync(tracker, _options.MonitorInterval, monitorCts.Token);
            var completed = await Task.WhenAny(operation, monitor).ConfigureAwait(false);
            if (completed == operation)
            {
                monitorCts.Cancel();
                await IgnoreCancellationAsync(monitor).ConfigureAwait(false);
                return await operation.ConfigureAwait(false);
            }

            var stall = await monitor.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (stall is null)
            {
                return await operation.ConfigureAwait(false);
            }

            _logger.LogError(
                "yt-dlp/FFmpeg progress stalled: attempt={Attempt}, state={State}, silenceSec={SilenceSec:0.0}, timeoutSec={TimeoutSec:0.0}, url={Url}",
                attempt,
                stall.State,
                stall.Silence.TotalSeconds,
                stall.Timeout.TotalSeconds,
                options.Url);
            progress?.Report(new DownloadProgress(
                stall.State,
                null,
                null,
                null,
                null,
                null,
                attempt < _options.MaxAttempts
                    ? "応答停止を検出しました。関連プロセスを終了してダウンロードを再試行します..."
                    : "再試行後も応答が停止したため、処理を終了します。"));

            attemptCts.Cancel();
            await WaitForForcedCancellationAsync(operation, _options.CancellationWaitTimeout).ConfigureAwait(false);
            if (attempt >= _options.MaxAttempts)
            {
                throw new TimeoutException(
                    $"{stall.State} の進捗が {stall.Timeout.TotalMinutes:0.0} 分以上停止しました。自動再試行後も復旧できませんでした。");
            }

            await Task.Delay(_options.RetryDelay, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("ダウンロード再試行制御が予期しない状態で終了しました。");
    }

    private async Task<T> ExecuteWithFixedTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operationFactory,
        string operationName,
        TimeSpan timeout,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var operation = operationFactory(attemptCts.Token);
            var timeoutTask = Task.Delay(timeout, ct);
            if (await Task.WhenAny(operation, timeoutTask).ConfigureAwait(false) == operation)
            {
                return await operation.ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            _logger.LogError(
                "yt-dlp operation timed out: operation={Operation}, attempt={Attempt}, timeoutSec={TimeoutSec:0.0}",
                operationName,
                attempt,
                timeout.TotalSeconds);
            attemptCts.Cancel();
            await WaitForForcedCancellationAsync(operation, _options.CancellationWaitTimeout).ConfigureAwait(false);
            if (attempt >= _options.MaxAttempts)
            {
                throw new TimeoutException(
                    $"{operationName}がタイムアウトし、自動再試行後も復旧できませんでした。");
            }

            await Task.Delay(_options.RetryDelay, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("yt-dlp再試行制御が予期しない状態で終了しました。");
    }

    private static async Task<StallInfo?> MonitorForStallAsync(
        ProgressTracker tracker,
        TimeSpan monitorInterval,
        CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(monitorInterval, ct).ConfigureAwait(false);
                var snapshot = tracker.GetSnapshot();
                var silence = DateTimeOffset.UtcNow - snapshot.LastMeaningfulProgressAt;
                if (silence >= snapshot.Timeout)
                {
                    return new StallInfo(snapshot.State, silence, snapshot.Timeout);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task WaitForForcedCancellationAsync(
        Task operation,
        TimeSpan cancellationWaitTimeout)
    {
        try
        {
            await operation.WaitAsync(cancellationWaitTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // キャンセル完了、または生実行側の終了待機上限超過。後者は次の試行でログに残る。
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class ProgressTracker
    {
        private readonly object _sync = new();
        private readonly DownloadOptions _options;
        private readonly YtdlpRecoveryOptions _recoveryOptions;
        private DateTimeOffset _lastMeaningfulProgressAt = DateTimeOffset.UtcNow;
        private DownloadState _state = DownloadState.Preparing;
        private long? _downloadedBytes;
        private double? _percent;
        private string? _message;
        private TimeSpan? _eta;

        public ProgressTracker(
            DownloadOptions options,
            YtdlpRecoveryOptions recoveryOptions)
        {
            _options = options;
            _recoveryOptions = recoveryOptions;
        }

        public void Report(DownloadProgress progress)
        {
            lock (_sync)
            {
                var meaningful = progress.State != _state ||
                                 progress.DownloadedBytes > _downloadedBytes ||
                                 progress.Percent > _percent ||
                                 (progress.State == DownloadState.Normalizing &&
                                  !string.Equals(progress.Message, _message, StringComparison.Ordinal) &&
                                  !(progress.Message?.StartsWith("警告:", StringComparison.Ordinal) ?? false));
                _state = progress.State;
                _downloadedBytes = progress.DownloadedBytes ?? _downloadedBytes;
                _percent = progress.Percent ?? _percent;
                _message = progress.Message ?? _message;
                _eta = progress.Eta ?? _eta;
                if (meaningful)
                {
                    _lastMeaningfulProgressAt = DateTimeOffset.UtcNow;
                }
            }
        }

        public ProgressSnapshot GetSnapshot()
        {
            lock (_sync)
            {
                return new ProgressSnapshot(
                    _state,
                    _lastMeaningfulProgressAt,
                    ResolveTimeout(_state, _eta, _options, _recoveryOptions));
            }
        }

        private static TimeSpan ResolveTimeout(
            DownloadState state,
            TimeSpan? eta,
            DownloadOptions options,
            YtdlpRecoveryOptions recoveryOptions) => state switch
            {
                DownloadState.Preparing => recoveryOptions.PreparingTimeout,
                DownloadState.Downloading => eta is { } value && value > TimeSpan.Zero
                    ? Clamp(
                        Multiply(value, 2),
                        recoveryOptions.MinimumDownloadTimeout,
                        recoveryOptions.MaximumDownloadTimeout)
                    : recoveryOptions.DefaultDownloadTimeout,
                DownloadState.PostProcessing => recoveryOptions.PostProcessingTimeout,
                DownloadState.Normalizing => ResolveNormalizationTimeout(options, recoveryOptions),
                DownloadState.Upscaling => ResolveNormalizationTimeout(options, recoveryOptions),
                _ => recoveryOptions.DefaultDownloadTimeout,
            };

        private static TimeSpan ResolveNormalizationTimeout(
            DownloadOptions options,
            YtdlpRecoveryOptions recoveryOptions)
        {
            var start = options.NormalizeStartTimeSeconds ?? 0;
            var duration = options.NormalizeEndTimeSeconds is { } end && end > start
                ? TimeSpan.FromSeconds(end - start)
                : (TimeSpan?)null;
            return duration is { } value
                ? Clamp(
                    Multiply(value, 2),
                    recoveryOptions.MinimumNormalizationTimeout,
                    recoveryOptions.MaximumNormalizationTimeout)
                : recoveryOptions.DefaultNormalizationTimeout;
        }

        private static TimeSpan Multiply(TimeSpan value, int factor) =>
            TimeSpan.FromTicks(value.Ticks > TimeSpan.MaxValue.Ticks / factor
                ? TimeSpan.MaxValue.Ticks
                : value.Ticks * factor);

        private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum) =>
            value < minimum ? minimum : value > maximum ? maximum : value;
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed record ProgressSnapshot(
        DownloadState State,
        DateTimeOffset LastMeaningfulProgressAt,
        TimeSpan Timeout);

    private sealed record StallInfo(DownloadState State, TimeSpan Silence, TimeSpan Timeout);
}
