using Torifune.Core.Models;

namespace Torifune.Core.Services.Settings;

/// <summary>設定の読み書きを担うストア。</summary>
public interface IAppSettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
