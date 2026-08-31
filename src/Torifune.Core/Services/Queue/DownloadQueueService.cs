using System.Text.Json;
using Microsoft.Extensions.Logging;
using Torifune.Core.Models;
using Torifune.Core.Platform;
using Torifune.Core.Services.PostProcessing;
using Torifune.Core.Services.Ytdlp;

namespace Torifune.Core.Services.Queue;

/// <summary>
/// yt-dlp の実行キュー。空きスロットへ Queued 項目を投入し、状態を JSON に永続化する。
/// </summary>
public sealed class DownloadQueueService : IDownloadQueueService
{
    private const string QueueFileName = "queue.json";

    private readonly object _sync = new();
    private readonly IYtdlpService _ytdlp;
    private readonly IMediaPostProcessingService _postProcessing;
    private readonly ILogger<DownloadQueueService> _logger;
    private readonly string _queueFilePath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly List<DownloadQueueItem> _items = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _running = [];
    private readonly Dictionary<Guid, Task> _runningTasks = [];
    private int _maxConcurrentDownloads = 3;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public DownloadQueueService(
        IYtdlpService ytdlp,
        IMediaPostProcessingService postProcessing,
        IAppPaths appPaths,
        ILogger<DownloadQueueService> logger)
    {
        _ytdlp = ytdlp;
        _postProcessing = postProcessing;
        _logger = logger;
        _queueFilePath = Path.Combine(appPaths.ConfigDirectory, QueueFileName);
    }

    public event EventHandler<IReadOnlyList<DownloadQueueItem>>? ItemsChanged;

    public IReadOnlyList<DownloadQueueItem> Items
    {
        get
        {
            lock (_sync)
            {
                return [.. _items];
            }
        }
    }

    public int MaxConcurrentDownloads
    {
        get
        {
            lock (_sync)
            {
                return _maxConcurrentDownloads;
            }
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _maxConcurrentDownloads = value;
            }
            StartQueuedItems();
        }
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        List<DownloadQueueItem> loaded = [];
        if (File.Exists(_queueFilePath))
        {
            try
            {
                await using var stream = File.OpenRead(_queueFilePath);
                loaded = await JsonSerializer
                    .DeserializeAsync<List<DownloadQueueItem>>(stream, JsonOptions, ct)
                    .ConfigureAwait(false) ?? [];
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "キューファイルの読み込みに失敗: {Path}", _queueFilePath);
                var brokenPath = _queueFilePath + ".broken-" + DateTimeOffset.Now.ToString("yyyyMMddHHmmss");
                File.Move(_queueFilePath, brokenPath, overwrite: true);
            }
        }

        lock (_sync)
        {
            _items.Clear();
            _items.AddRange(loaded.Select(RestoreLoadedItem));
        }

        _logger.LogInformation("Queue loaded: count={Count}, path={Path}", _items.Count, _queueFilePath);

        PublishItems();
        await SaveAsync(ct).ConfigureAwait(false);
        StartQueuedItems();
    }

    public async Task<Guid> EnqueueAsync(
        string title,
        DownloadOptions options,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var item = new DownloadQueueItem
        {
            Id = Guid.NewGuid(),
            Title = string.IsNullOrWhiteSpace(title) ? options.Url : title,
            Options = options,
        };

        lock (_sync)
        {
            _items.Add(item);
        }

        _logger.LogInformation(
            "Queue enqueue: id={Id}, title={Title}, url={Url}, outputDir={OutputDir}, normalize={Normalize}, upscaleToFhd={Upscale}, start={Start}, end={End}",
            item.Id,
            item.Title,
            options.Url,
            options.OutputDirectory,
            options.NormalizeAudio,
            options.UpscaleToFhd,
            options.StartTimeSeconds,
            options.EndTimeSeconds);

        PublishItems();
        await SaveAsync(ct).ConfigureAwait(false);
        StartQueuedItems();
        return item.Id;
    }

    public async Task PauseAsync(Guid id, CancellationToken ct = default)
    {
        CancellationTokenSource? running = null;
        var changed = false;

        lock (_sync)
        {
            var index = FindIndex(id);
            if (index < 0)
            {
                return;
            }

            var item = _items[index];
            if (item.Status is DownloadQueueStatus.Queued or
                DownloadQueueStatus.Running or DownloadQueueStatus.PostProcessing or DownloadQueueStatus.Normalizing or DownloadQueueStatus.Upscaling)
            {
                _items[index] = item with
                {
                    Status = DownloadQueueStatus.Paused,
                    SpeedBytesPerSec = null,
                    Eta = null,
                };
                _running.TryGetValue(id, out running);
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        _logger.LogInformation("Queue pause requested: id={Id}", id);

        PublishItems();
        await SaveAsync(ct).ConfigureAwait(false);
        running?.Cancel();
        StartQueuedItems();
    }

    public async Task ResumeAsync(Guid id, CancellationToken ct = default)
    {
        if (!UpdateStatusForRestart(id, DownloadQueueStatus.Paused))
        {
            return;
        }

        _logger.LogInformation("Queue resume requested: id={Id}", id);

        PublishItems();
        await SaveAsync(ct).ConfigureAwait(false);
        StartQueuedItems();
    }

    public async Task CancelAsync(Guid id, CancellationToken ct = default)
    {
        CancellationTokenSource? running = null;
        var changed = false;

        lock (_sync)
        {
            var index = FindIndex(id);
            if (index < 0 || _items[index].Status is DownloadQueueStatus.Completed or DownloadQueueStatus.Canceled)
            {
                return;
            }

            _items[index] = _items[index] with
            {
                Status = DownloadQueueStatus.Canceled,
                SpeedBytesPerSec = null,
                Eta = null,
                ErrorMessage = null,
                StatusMessage = null,
            };
            _running.TryGetValue(id, out running);
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        _logger.LogInformation("Queue cancel requested: id={Id}", id);

        PublishItems();
        await SaveAsync(ct).ConfigureAwait(false);
        running?.Cancel();
        StartQueuedItems();
    }

    public async Task RetryAsync(Guid id, CancellationToken ct = default)
    {
        var changed = false;
        lock (_sync)
        {
            var index = FindIndex(id);
            if (index >= 0 && _items[index].Status is DownloadQueueStatus.Failed or DownloadQueueStatus.Canceled)
            {
                var item = _items[index];
                _items[index] = item with
                {
                    Status = DownloadQueueStatus.Queued,
                    ProgressPercent = 0,
                    DownloadedBytes = null,
                    TotalBytes = null,
                    SpeedBytesPerSec = null,
                    Eta = null,
                    OutputPath = null,
                    VideoWidth = null,
                    VideoHeight = null,
                    VideoFps = null,
                    VideoCodec = null,
                    AudioCodec = null,
                    AudioBitrate = null,
                    TotalBitrate = null,
                    AudioSampleRate = null,
                    AudioChannels = null,
                    ErrorMessage = null,
                    StatusMessage = null,
                    IsManualPostProcessing = false,
                    CompletedAt = null,
                };
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        _logger.LogInformation("Queue retry requested: id={Id}", id);

        PublishItems();
        await SaveAsync(ct).ConfigureAwait(false);
        StartQueuedItems();
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        CancellationTokenSource? running = null;
        var changed = false;
        lock (_sync)
        {
            var index = FindIndex(id);
            if (index >= 0)
            {
                _running.TryGetValue(id, out running);
                _items.RemoveAt(index);
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        _logger.LogInformation("Queue remove requested: id={Id}", id);

        running?.Cancel();
        PublishItems();
        await SaveAsync(ct).ConfigureAwait(false);
        StartQueuedItems();
    }

    public Task UpscaleCompletedToFhdAsync(Guid id, CancellationToken ct = default)
    {
        CancellationTokenSource? operationCts = null;
        string? outputPath = null;
        lock (_sync)
        {
            var index = FindIndex(id);
            if (index < 0)
            {
                return Task.CompletedTask;
            }

            var item = _items[index];
            if (item.Status != DownloadQueueStatus.Completed ||
                string.IsNullOrWhiteSpace(item.OutputPath) ||
                !File.Exists(item.OutputPath) ||
                (item.VideoWidth is { } width &&
                 item.VideoHeight is { } height &&
                 !MediaPostProcessingService.ShouldUpscaleToFhd(width, height)))
            {
                return Task.CompletedTask;
            }

            operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            outputPath = item.OutputPath;
            _items[index] = item with
            {
                Status = DownloadQueueStatus.Upscaling,
                ProgressPercent = 0,
                ErrorMessage = null,
                StatusMessage = "FHD変換を開始しています...",
                IsManualPostProcessing = true,
                CompletedAt = null,
            };
            _running[id] = operationCts;
        }

        PublishItems();
        _ = SaveSafelyAsync();
        var task = RunCompletedUpscaleAsync(id, outputPath!, operationCts!);
        lock (_sync)
        {
            _runningTasks[id] = task;
        }
        return Task.CompletedTask;
    }

    private async Task RunCompletedUpscaleAsync(
        Guid id,
        string outputPath,
        CancellationTokenSource cts)
    {
        await Task.Yield();

        try
        {
            var progress = new InlineProgress<MediaPostProcessingProgress>(value =>
                UpdateProgress(id, new DownloadProgress(
                    DownloadState.Upscaling,
                    null,
                    null,
                    null,
                    null,
                    null,
                    value.Message)));
            var result = await _postProcessing.ProcessAsync(
                    outputPath,
                    new MediaPostProcessingOptions(
                        NormalizeAudio: false,
                        UpscaleToFhd: true,
                        TargetLoudnessLufs: -14,
                        TargetLoudnessRange: 9,
                        TargetTruePeakDb: -1),
                    progress,
                    cts.Token)
                .ConfigureAwait(false);

            lock (_sync)
            {
                var index = FindIndex(id);
                if (index >= 0)
                {
                    _items[index] = _items[index] with
                    {
                        Status = DownloadQueueStatus.Completed,
                        ProgressPercent = 100,
                        OutputPath = result.OutputPath,
                        VideoWidth = result.VideoWidth,
                        VideoHeight = result.VideoHeight,
                        VideoCodec = result.VideoCodec,
                        AudioCodec = result.AudioCodec,
                        AudioBitrate = result.AudioBitrate,
                        AudioSampleRate = result.AudioSampleRate,
                        TotalBitrate = null,
                        ErrorMessage = null,
                        StatusMessage = result.WasUpscaled
                            ? "FHD変換が完了しました。"
                            : "映像はFHD変換の対象外でした。",
                        IsManualPostProcessing = false,
                        CompletedAt = DateTimeOffset.UtcNow,
                    };
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            lock (_sync)
            {
                var index = FindIndex(id);
                if (index >= 0 && _items[index].Status is DownloadQueueStatus.Upscaling or DownloadQueueStatus.Canceled)
                {
                    _items[index] = _items[index] with
                    {
                        Status = DownloadQueueStatus.Completed,
                        ErrorMessage = "FHD変換をキャンセルしました。元ファイルは保持されています。",
                        StatusMessage = null,
                        IsManualPostProcessing = false,
                        CompletedAt = DateTimeOffset.UtcNow,
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "完了済み項目のFHD変換に失敗: {Id}", id);
            lock (_sync)
            {
                var index = FindIndex(id);
                if (index >= 0)
                {
                    _items[index] = _items[index] with
                    {
                        Status = DownloadQueueStatus.Completed,
                        ErrorMessage = $"FHD変換に失敗しました: {ex.Message}",
                        StatusMessage = null,
                        IsManualPostProcessing = false,
                        CompletedAt = DateTimeOffset.UtcNow,
                    };
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                _running.Remove(id);
                _runningTasks.Remove(id);
            }
            cts.Dispose();
            PublishItems();
            await SaveSafelyAsync().ConfigureAwait(false);
            StartQueuedItems();
        }
    }

    private bool UpdateStatusForRestart(Guid id, DownloadQueueStatus requiredStatus)
    {
        lock (_sync)
        {
            var index = FindIndex(id);
            if (index < 0 || _items[index].Status != requiredStatus)
            {
                return false;
            }

            _items[index] = _items[index] with
            {
                Status = DownloadQueueStatus.Queued,
                SpeedBytesPerSec = null,
                Eta = null,
                ErrorMessage = null,
                StatusMessage = null,
            };
            return true;
        }
    }

    /// <summary>実行スロットが空いている限り、Queued 項目を開始する。</summary>
    private void StartQueuedItems()
    {
        var starts = new List<(Guid Id, DownloadOptions Options, CancellationTokenSource Cts)>();

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            while (_running.Count < _maxConcurrentDownloads)
            {
                var index = _items.FindIndex(item => item.Status == DownloadQueueStatus.Queued);
                if (index < 0)
                {
                    break;
                }

                var item = _items[index];
                var cts = new CancellationTokenSource();
                _items[index] = item with
                {
                    Status = DownloadQueueStatus.Running,
                    ErrorMessage = null,
                };
                _running.Add(item.Id, cts);
                starts.Add((item.Id, item.Options, cts));
            }

            var queuedCount = _items.Count(item => item.Status == DownloadQueueStatus.Queued);
            _logger.LogDebug(
                "Queue scheduler tick: running={Running}, queued={Queued}, maxConcurrent={Max}",
                _running.Count,
                queuedCount,
                _maxConcurrentDownloads);
        }

        if (starts.Count == 0)
        {
            return;
        }

        PublishItems();
        _ = SaveSafelyAsync();

        foreach (var start in starts)
        {
            _logger.LogInformation(
                "Queue start item: id={Id}, url={Url}, outputDir={OutputDir}, normalize={Normalize}",
                start.Id,
                start.Options.Url,
                start.Options.OutputDirectory,
                start.Options.NormalizeAudio);
            var task = RunItemAsync(start.Id, start.Options, start.Cts);
            lock (_sync)
            {
                _runningTasks[start.Id] = task;
                if (task.IsCompleted)
                {
                    _runningTasks.Remove(start.Id);
                }
            }
        }
    }

    private async Task RunItemAsync(Guid id, DownloadOptions options, CancellationTokenSource cts)
    {
        // StartQueuedItems がタスク辞書へ登録してから実処理へ進む。
        await Task.Yield();

        _logger.LogInformation("Queue run begin: id={Id}", id);

        try
        {
            var progress = new InlineProgress<DownloadProgress>(value => UpdateProgress(id, value));
            var result = await _ytdlp.DownloadAsync(options, progress, cts.Token).ConfigureAwait(false);

            lock (_sync)
            {
                var index = FindIndex(id);
                if (index < 0 || _items[index].Status is DownloadQueueStatus.Paused or DownloadQueueStatus.Canceled)
                {
                    return;
                }

                var item = _items[index];
                _items[index] = item with
                {
                    Status = result.Success ? DownloadQueueStatus.Completed : DownloadQueueStatus.Failed,
                    ProgressPercent = result.Success ? 100 : item.ProgressPercent,
                    SpeedBytesPerSec = null,
                    Eta = null,
                    OutputPath = result.OutputPath,
                    VideoWidth = result.MediaInfo?.VideoWidth,
                    VideoHeight = result.MediaInfo?.VideoHeight,
                    VideoFps = result.MediaInfo?.VideoFps,
                    VideoCodec = result.MediaInfo?.VideoCodec,
                    AudioCodec = result.MediaInfo?.AudioCodec,
                    AudioBitrate = result.MediaInfo?.AudioBitrate,
                    TotalBitrate = result.MediaInfo?.TotalBitrate,
                    AudioSampleRate = result.MediaInfo?.AudioSampleRate,
                    AudioChannels = result.MediaInfo?.AudioChannels,
                    ErrorMessage = result.ErrorMessage,
                    StatusMessage = null,
                    IsManualPostProcessing = false,
                    CompletedAt = result.Success ? DateTimeOffset.UtcNow : null,
                };
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Pause/Cancel/Remove が先に状態を確定しているため、ここでは変更しない。
            _logger.LogInformation("Queue run canceled: id={Id}", id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "キュー項目の実行に失敗: {Id}", id);
            lock (_sync)
            {
                var index = FindIndex(id);
                if (index >= 0 && _items[index].Status is DownloadQueueStatus.Running or DownloadQueueStatus.PostProcessing or DownloadQueueStatus.Normalizing or DownloadQueueStatus.Upscaling)
                {
                    _items[index] = _items[index] with
                    {
                        Status = DownloadQueueStatus.Failed,
                        SpeedBytesPerSec = null,
                        Eta = null,
                        ErrorMessage = ex.Message,
                        StatusMessage = null,
                    };
                }
            }
        }
        finally
        {
            DownloadQueueStatus? finalStatus = null;
            lock (_sync)
            {
                _running.Remove(id);
                _runningTasks.Remove(id);
                var index = FindIndex(id);
                if (index >= 0)
                {
                    finalStatus = _items[index].Status;
                }
            }
            cts.Dispose();
            _logger.LogInformation("Queue run end: id={Id}, finalStatus={Status}", id, finalStatus);
            PublishItems();
            await SaveSafelyAsync().ConfigureAwait(false);
            StartQueuedItems();
        }
    }

    private void UpdateProgress(Guid id, DownloadProgress progress)
    {
        DownloadQueueStatus? oldStatus = null;
        DownloadQueueStatus? newStatus = null;
        bool shouldLog = false;
        double? newPercent = null;

        lock (_sync)
        {
            var index = FindIndex(id);
            if (index < 0 || _items[index].Status is DownloadQueueStatus.Paused or DownloadQueueStatus.Canceled)
            {
                return;
            }

            var item = _items[index];
            oldStatus = item.Status;
            var updatedStatus = progress.State switch
            {
                DownloadState.PostProcessing => DownloadQueueStatus.PostProcessing,
                DownloadState.Normalizing => DownloadQueueStatus.Normalizing,
                DownloadState.Upscaling => DownloadQueueStatus.Upscaling,
                _ => DownloadQueueStatus.Running,
            };
            var updated = item with
            {
                Status = updatedStatus,
                ProgressPercent = progress.Percent ?? item.ProgressPercent,
                DownloadedBytes = progress.DownloadedBytes ?? item.DownloadedBytes,
                TotalBytes = progress.TotalBytes ?? item.TotalBytes,
                SpeedBytesPerSec = progress.SpeedBytesPerSec,
                Eta = progress.Eta,
                StatusMessage = progress.Message,
            };
            _items[index] = updated;

            newStatus = updated.Status;
            newPercent = updated.ProgressPercent;
            var statusChanged = oldStatus != newStatus;
            var hasStatusMessage = !string.IsNullOrWhiteSpace(progress.Message);
            var crossedTenPercent = Math.Floor(updated.ProgressPercent / 10) > Math.Floor(item.ProgressPercent / 10);
            shouldLog = statusChanged || hasStatusMessage || crossedTenPercent;
        }

        if (shouldLog)
        {
            _logger.LogDebug(
                "Queue progress: id={Id}, state={State}, status={Status}, percent={Percent:0.0}, message={Message}",
                id,
                progress.State,
                newStatus,
                newPercent,
                progress.Message);
        }
        PublishItems();
    }

    private int FindIndex(Guid id) => _items.FindIndex(item => item.Id == id);

    private static DownloadQueueItem RestoreLoadedItem(DownloadQueueItem item)
    {
        if (item.Status == DownloadQueueStatus.Upscaling && item.IsManualPostProcessing)
        {
            return item with
            {
                Status = DownloadQueueStatus.Completed,
                SpeedBytesPerSec = null,
                Eta = null,
                ErrorMessage = "FHD変換はアプリ終了により中断されました。元ファイルは保持されています。",
                StatusMessage = null,
                IsManualPostProcessing = false,
                CompletedAt = DateTimeOffset.UtcNow,
            };
        }

        return item.Status is DownloadQueueStatus.Running or
            DownloadQueueStatus.PostProcessing or
            DownloadQueueStatus.Normalizing or
            DownloadQueueStatus.Upscaling
            ? item with
            {
                Status = DownloadQueueStatus.Paused,
                SpeedBytesPerSec = null,
                Eta = null,
            }
            : item;
    }

    private void PublishItems()
    {
        IReadOnlyList<DownloadQueueItem> snapshot;
        lock (_sync)
        {
            snapshot = [.. _items];
        }
        ItemsChanged?.Invoke(this, snapshot);
    }

    private async Task SaveAsync(CancellationToken ct = default)
    {
        IReadOnlyList<DownloadQueueItem> snapshot;
        lock (_sync)
        {
            snapshot = [.. _items];
        }

        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_queueFilePath)!);
            var tempPath = _queueFilePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, ct).ConfigureAwait(false);
            }
            File.Move(tempPath, _queueFilePath, overwrite: true);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task SaveSafelyAsync()
    {
        try
        {
            await SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "キューの保存に失敗: {Path}", _queueFilePath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var cts in _running.Values)
            {
                cts.Cancel();
            }
            tasks = [.. _runningTasks.Values];
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 終了時キャンセル
        }

        await SaveSafelyAsync().ConfigureAwait(false);
        _saveGate.Dispose();
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
