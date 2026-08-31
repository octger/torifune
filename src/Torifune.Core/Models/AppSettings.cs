namespace Torifune.Core.Models;

/// <summary>アプリ全体の既定設定。</summary>
public sealed record AppSettings
{
    /// <summary>新規追加時の既定ダウンロード方式キー。</summary>
    public string DefaultFormatModeKey { get; init; } = "avc-aac";

    /// <summary>新規追加時に音声正規化を既定で有効化するか。</summary>
    public bool DefaultNormalizeAudio { get; init; } = true;

    /// <summary>新規追加時にFHD未満の映像を1920x1080へ変換するか。</summary>
    public bool DefaultUpscaleToFhd { get; init; }

    /// <summary>音量正規化時の目標統合ラウドネス (LUFS)。</summary>
    public double DefaultTargetLoudnessLufs { get; init; } = -14.0;

    /// <summary>音量正規化時の目標 True Peak (dBTP)。</summary>
    public double DefaultTargetTruePeakDb { get; init; } = -1.0;

    /// <summary>音量正規化時の目標 Loudness Range (LU)。</summary>
    public double DefaultTargetLoudnessRange { get; init; } = 9.0;

    /// <summary>保存先ディレクトリの既定値。空の場合は OS の Downloads を利用する。</summary>
    public string DefaultOutputDirectory { get; init; } = "";

    /// <summary>yt-dlp の出力ファイル名テンプレート既定値。</summary>
    public string DefaultOutputTemplate { get; init; } = "%(title)s [%(id)s].%(ext)s";

    /// <summary>キューの既定並列数。</summary>
    public int MaxConcurrentDownloads { get; init; } = 3;

    /// <summary>プレビュー再生の既定音量(0-100)。</summary>
    public int DefaultPreviewVolumePercent { get; init; }

    /// <summary>プレビュー用軽量動画の品質モード。</summary>
    public string PreviewQualityModeKey { get; init; } = "balanced";
}
