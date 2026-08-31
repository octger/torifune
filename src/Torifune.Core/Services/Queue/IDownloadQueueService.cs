using Torifune.Core.Models;

namespace Torifune.Core.Services.Queue;

/// <summary>ダウンロードキューの管理と並列実行を担う。</summary>
public interface IDownloadQueueService : IAsyncDisposable
{
    /// <summary>項目が追加・更新・削除された時に最新スナップショットを通知する。</summary>
    event EventHandler<IReadOnlyList<DownloadQueueItem>>? ItemsChanged;

    IReadOnlyList<DownloadQueueItem> Items { get; }

    /// <summary>同時実行数。1以上。</summary>
    int MaxConcurrentDownloads { get; set; }

    /// <summary>永続化されたキューを読み込む。Running/PostProcessing は Paused として復元する。</summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>項目を末尾へ追加し、空きスロットがあれば実行を開始する。</summary>
    Task<Guid> EnqueueAsync(string title, DownloadOptions options, CancellationToken ct = default);

    Task PauseAsync(Guid id, CancellationToken ct = default);
    Task ResumeAsync(Guid id, CancellationToken ct = default);
    Task CancelAsync(Guid id, CancellationToken ct = default);
    Task RetryAsync(Guid id, CancellationToken ct = default);
    Task UpscaleCompletedToFhdAsync(Guid id, CancellationToken ct = default);
    Task RemoveAsync(Guid id, CancellationToken ct = default);
}
