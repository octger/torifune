namespace Torifune.Core.Models;

/// <summary>キュー項目の状態。</summary>
public enum DownloadQueueStatus
{
    Queued,
    Running,
    PostProcessing,
    Normalizing,
    Paused,
    Completed,
    Failed,
    Canceled,
    Upscaling,
}

/// <summary>
/// キュー項目の不変スナップショット。Core から UI と永続化層へ公開する。
/// </summary>
public sealed record DownloadQueueItem
{
    public required Guid Id { get; init; }
    public required DownloadOptions Options { get; init; }
    public required string Title { get; init; }
    public DownloadQueueStatus Status { get; init; } = DownloadQueueStatus.Queued;
    public double ProgressPercent { get; init; }
    public long? DownloadedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public double? SpeedBytesPerSec { get; init; }
    public TimeSpan? Eta { get; init; }
    public string? OutputPath { get; init; }
    public int? VideoWidth { get; init; }
    public int? VideoHeight { get; init; }
    public double? VideoFps { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public long? AudioBitrate { get; init; }
    public long? TotalBitrate { get; init; }
    public int? AudioSampleRate { get; init; }
    public int? AudioChannels { get; init; }
    public string? ErrorMessage { get; init; }
    public string? StatusMessage { get; init; }
    public bool IsManualPostProcessing { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
}
