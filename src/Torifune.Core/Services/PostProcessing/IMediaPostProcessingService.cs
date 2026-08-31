namespace Torifune.Core.Services.PostProcessing;

public sealed record MediaPostProcessingOptions(
    bool NormalizeAudio,
    bool UpscaleToFhd,
    double TargetLoudnessLufs,
    double TargetLoudnessRange,
    double TargetTruePeakDb,
    int AudioBitrateKbps = 192,
    double? NormalizeStartTimeSeconds = null,
    double? NormalizeEndTimeSeconds = null);

public sealed record MediaPostProcessingProgress(string Message, bool IsUpscaling);

public sealed record MediaPostProcessingResult(
    string OutputPath,
    bool WasNormalized,
    bool WasUpscaled,
    int? VideoWidth,
    int? VideoHeight,
    string? VideoCodec = null,
    string? AudioCodec = null,
    long? AudioBitrate = null,
    int? AudioSampleRate = null);

/// <summary>ダウンロード後の音声正規化とFHD変換を一つの書き出し工程に統合する。</summary>
public interface IMediaPostProcessingService
{
    Task<MediaPostProcessingResult> ProcessAsync(
        string mediaPath,
        MediaPostProcessingOptions options,
        IProgress<MediaPostProcessingProgress>? progress = null,
        CancellationToken ct = default);
}
