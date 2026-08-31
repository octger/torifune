using Torifune.Core.Models;

namespace Torifune.Core.Services.Preview;

public sealed record PreviewSourceResult(string Path, bool FromCache);

/// <summary>軽量プレビュー動画の取得・キャッシュと基本情報解析を担う。</summary>
public interface IPreviewSourceService
{
    Task<PreviewSourceResult> EnsureSourceAsync(
        string url,
        string formatString,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);

    Task<double?> ProbeDurationSecondsAsync(string videoPath, CancellationToken ct = default);
}
