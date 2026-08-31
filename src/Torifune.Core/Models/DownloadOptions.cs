namespace Torifune.Core.Models;

/// <summary>
/// 1回のダウンロードと後処理に対する指定。
/// </summary>
public sealed record DownloadOptions
{
    public required string Url { get; init; }

    /// <summary>保存先ディレクトリ。</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>出力ファイル名テンプレート(yt-dlp -o)。</summary>
    public string OutputTemplate { get; init; } = "%(title)s [%(id)s].%(ext)s";

    /// <summary>-f に渡すフォーマット文字列。null の場合は yt-dlp 既定(最高品質)。</summary>
    public string? FormatString { get; init; }

    /// <summary>
    /// -S に渡すソート文字列。
    /// 既定は AVC(H.264) の 1080p を優先し、AAC は可能な限り高ビットレートを優先する。
    /// 条件に合わない場合は yt-dlp の通常フォールバックで取得する。
    /// (yt-dlp 公式の mp4 プリセットと同様の指定)。
    /// </summary>
    public string? FormatSort { get; init; } = "vcodec:h264,res:1080,fps,acodec:aac,abr,lang,quality,hdr:12";

    /// <summary>音声のみ抽出(-x)。</summary>
    public bool AudioOnly { get; init; }

    /// <summary>音声抽出時の変換先(mp3, m4a 等)。null なら best。</summary>
    public string? AudioFormat { get; init; }

    /// <summary>コンテナ変換先(--remux-video)。既定は mp4。</summary>
    public string? RemuxTo { get; init; } = "mp4";

    /// <summary>映像/音声マージ時のコンテナ(--merge-output-format)。既定は mp4。</summary>
    public string? MergeOutputFormat { get; init; } = "mp4";

    /// <summary>指定秒数を境にダウンロード対象を切り出す開始時刻(秒)。</summary>
    public double? StartTimeSeconds { get; init; }

    /// <summary>指定秒数を境にダウンロード対象を切り出す終了時刻(秒)。</summary>
    public double? EndTimeSeconds { get; init; }

    /// <summary>ダウンロード完了後に音声を EBU R128 基準で正規化する。</summary>
    public bool NormalizeAudio { get; init; } = true;

    /// <summary>FHD未満の映像をアスペクト比維持で1920x1080へ変換する。</summary>
    public bool UpscaleToFhd { get; init; }

    /// <summary>正規化後の統合ラウドネス目標(LUFS)。オンライン動画向けの既定は -16。</summary>
    public double TargetLoudnessLufs { get; init; } = -16.0;

    /// <summary>正規化後の最大 True Peak(dBTP)。</summary>
    public double TargetTruePeakDb { get; init; } = -1.5;

    /// <summary>正規化時の目標 Loudness Range(LU)。</summary>
    public double TargetLoudnessRange { get; init; } = 11.0;

    /// <summary>正規化処理を適用する開始時刻(秒)。null の場合は先頭から。</summary>
    public double? NormalizeStartTimeSeconds { get; init; }

    /// <summary>正規化処理を適用する終了時刻(秒)。null の場合は末尾まで。</summary>
    public double? NormalizeEndTimeSeconds { get; init; }
}
