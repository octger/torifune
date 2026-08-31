namespace Torifune.Core.Models;

/// <summary>ダウンロードの進行段階。</summary>
public enum DownloadState
{
    Preparing,
    Downloading,
    PostProcessing,
    Normalizing,
    Upscaling,
    Finished,
}

/// <summary>進捗通知。</summary>
public sealed record DownloadProgress(
    DownloadState State,
    double? Percent,
    long? DownloadedBytes,
    long? TotalBytes,
    double? SpeedBytesPerSec,
    TimeSpan? Eta,
    string? Message);

/// <summary>実際に出力されたメディアの技術情報。</summary>
public sealed record DownloadedMediaInfo(
    int? VideoWidth,
    int? VideoHeight,
    double? VideoFps,
    string? VideoCodec,
    string? AudioCodec,
    long? AudioBitrate,
    long? TotalBitrate,
    int? AudioSampleRate,
    int? AudioChannels);

/// <summary>ダウンロード完了結果。</summary>
public sealed record DownloadResult(
    bool Success,
    string? OutputPath,
    string? ErrorMessage,
    DownloadedMediaInfo? MediaInfo = null);
