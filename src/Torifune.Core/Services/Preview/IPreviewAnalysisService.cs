namespace Torifune.Core.Services.Preview;

public sealed record PreviewAnalysisRequest(
    string Url,
    string SourcePath,
    int? StartSeconds,
    int? EndSeconds,
    double TargetLoudnessLufs,
    double TargetTruePeakDb,
    double TargetLoudnessRange);

public sealed record PreviewAnalysisMetrics(
    double InputIntegratedLufs,
    double InputTruePeakDbtp,
    double InputLraLu,
    double OutputIntegratedLufs,
    double OutputTruePeakDbtp,
    double OutputLraLu);

public sealed record PreviewAnalysisResult(
    string CacheKey,
    string BeforeImagePath,
    string AfterImagePath,
    PreviewAnalysisMetrics Metrics);

/// <summary>プレビュー音声の正規化比較と波形画像生成を担う。</summary>
public interface IPreviewAnalysisService
{
    Task<PreviewAnalysisResult> AnalyzeAsync(
        PreviewAnalysisRequest request,
        CancellationToken ct = default);
}
