namespace Torifune.Core.Services.Tools;

/// <summary>管理対象の外部ツール。</summary>
public enum ToolKind
{
    Ytdlp,
    Ffmpeg,
}

/// <summary>ツールのインストール状態。</summary>
public sealed record ToolStatus(ToolKind Kind, bool IsInstalled, string? Version, string? Path);

/// <summary>ツール取得の進捗通知。</summary>
public sealed record ToolProgress(ToolKind Kind, string Phase, double? Percent);

/// <summary>
/// ユーザーが依存ツールの取得元・用途・ライセンスを確認して同意した記録。
/// </summary>
public sealed record ToolDownloadConsent(bool Accepted, DateTimeOffset AcceptedAt)
{
    public static ToolDownloadConsent GrantedNow() => new(true, DateTimeOffset.UtcNow);
}

/// <summary>yt-dlp / ffmpeg の配置・取得・更新を担う。</summary>
public interface IToolManager
{
    /// <summary>yt-dlp 実行ファイルのフルパス(存在保証はしない)。</summary>
    string YtdlpPath { get; }

    /// <summary>ffmpeg 実行ファイルのフルパス(存在保証はしない)。</summary>
    string FfmpegPath { get; }

    /// <summary>ffmpeg/ffprobe が置かれたディレクトリ(yt-dlp の --ffmpeg-location に渡す)。</summary>
    string FfmpegDirectory { get; }

    /// <summary>両ツールの現在の状態を返す。</summary>
    Task<IReadOnlyList<ToolStatus>> GetStatusAsync(CancellationToken ct = default);

    /// <summary>明示同意を検証した上で、未導入のツールをダウンロードする。</summary>
    Task DownloadMissingToolsAsync(
        ToolDownloadConsent consent,
        IProgress<ToolProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>yt-dlp を最新版に更新する。更新した場合 true。</summary>
    Task<bool> UpdateYtdlpAsync(IProgress<ToolProgress>? progress = null, CancellationToken ct = default);
}
