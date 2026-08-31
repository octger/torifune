using System.Text.Json;
using Microsoft.Extensions.Logging;
using Torifune.Core.Models;
using Torifune.Core.Platform;

namespace Torifune.Core.Services.Settings;

/// <summary>settings.json へ原子的に保存する設定ストア。</summary>
public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private const string SettingsFileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _settingsFilePath;
    private readonly ILogger<JsonAppSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonAppSettingsStore(IAppPaths paths, ILogger<JsonAppSettingsStore> logger)
    {
        _logger = logger;
        _settingsFilePath = Path.Combine(paths.ConfigDirectory, SettingsFileName);
    }

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return new AppSettings();
            }

            await using var stream = File.OpenRead(_settingsFilePath);
            var settings = await JsonSerializer
                .DeserializeAsync<AppSettings>(stream, JsonOptions, ct)
                .ConfigureAwait(false);
            return settings is null
                ? new AppSettings()
                : Normalize(settings);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "設定ファイルの読み込みに失敗: {Path}", _settingsFilePath);
            return new AppSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        var normalized = Normalize(settings);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!);
            var tempPath = _settingsFilePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, ct).ConfigureAwait(false);
            }
            File.Move(tempPath, _settingsFilePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        var concurrency = Math.Max(1, settings.MaxConcurrentDownloads);
        var modeKey = string.IsNullOrWhiteSpace(settings.DefaultFormatModeKey)
            ? "avc-aac"
            : settings.DefaultFormatModeKey;
        var outputTemplate = string.IsNullOrWhiteSpace(settings.DefaultOutputTemplate)
            ? "%(title)s [%(id)s].%(ext)s"
            : settings.DefaultOutputTemplate;
        var targetLufs = Clamp(settings.DefaultTargetLoudnessLufs, -70.0, -5.0, -14.0);
        var truePeakDb = Clamp(settings.DefaultTargetTruePeakDb, -9.0, 0.0, -1.0);
        var lra = Clamp(settings.DefaultTargetLoudnessRange, 1.0, 50.0, 9.0);
        var previewVolume = Math.Clamp(settings.DefaultPreviewVolumePercent, 0, 100);
        var previewQualityModeKey = string.IsNullOrWhiteSpace(settings.PreviewQualityModeKey)
            ? "balanced"
            : settings.PreviewQualityModeKey;

        return settings with
        {
            DefaultFormatModeKey = modeKey,
            MaxConcurrentDownloads = concurrency,
            DefaultOutputTemplate = outputTemplate,
            DefaultTargetLoudnessLufs = targetLufs,
            DefaultTargetTruePeakDb = truePeakDb,
            DefaultTargetLoudnessRange = lra,
            DefaultPreviewVolumePercent = previewVolume,
            PreviewQualityModeKey = previewQualityModeKey,
        };
    }

    private static double Clamp(double value, double min, double max, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return fallback;
        }

        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }
}
