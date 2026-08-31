using Torifune.Core.Models;

namespace Torifune.Core.Services.Ytdlp;

/// <summary>yt-dlp サブプロセスの実行を抽象化する。</summary>
public interface IYtdlpService
{
    /// <summary>URL を解析しメタデータを取得する。</summary>
    Task<MediaInfo> FetchMediaInfoAsync(string url, CancellationToken ct = default);

    /// <summary>ダウンロードを実行する。進捗は progress へ通知される。</summary>
    Task<DownloadResult> DownloadAsync(
        DownloadOptions options,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>再試行制御でラップされるyt-dlp生実行サービス。</summary>
public interface IYtdlpProcessService : IYtdlpService;
