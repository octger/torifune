namespace Torifune.Core.Services.Normalization;

/// <summary>音声ラウドネス正規化の設定。</summary>
public sealed record AudioNormalizationOptions(
    double IntegratedLoudnessLufs,
    double LoudnessRange,
    double TruePeakDb,
    int AudioBitrateKbps = 192,
    double? StartTimeSeconds = null,
    double? EndTimeSeconds = null);

/// <summary>正規化処理の現在段階。</summary>
public sealed record AudioNormalizationProgress(string Message);

/// <summary>メディアファイル内の音声を正規化する。</summary>
public interface IAudioNormalizationService
{
    /// <summary>
    /// 2パス EBU R128 正規化を適用する。映像はコピーし、成功時のみ元ファイルを置換する。
    /// </summary>
    Task NormalizeAsync(
        string mediaPath,
        AudioNormalizationOptions options,
        IProgress<AudioNormalizationProgress>? progress = null,
        CancellationToken ct = default);
}
