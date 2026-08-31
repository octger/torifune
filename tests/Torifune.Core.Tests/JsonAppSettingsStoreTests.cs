using Microsoft.Extensions.Logging.Abstractions;
using Torifune.Core.Models;
using Torifune.Core.Platform;
using Torifune.Core.Services.Settings;

namespace Torifune.Core.Tests;

public sealed class JsonAppSettingsStoreTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), "Torifune.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task 保存した設定を再読み込みできる()
    {
        var paths = new TestAppPaths(_tempDirectory);
        var store = new JsonAppSettingsStore(paths, NullLogger<JsonAppSettingsStore>.Instance);

        var settings = new AppSettings
        {
            DefaultFormatModeKey = "best",
            DefaultNormalizeAudio = false,
            DefaultUpscaleToFhd = true,
            DefaultTargetLoudnessLufs = -14.0,
            DefaultTargetTruePeakDb = -1.0,
            DefaultTargetLoudnessRange = 9.0,
            DefaultOutputDirectory = @"D:\Media",
            DefaultOutputTemplate = "%(title)s.%(ext)s",
            MaxConcurrentDownloads = 5,
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equal("best", loaded.DefaultFormatModeKey);
        Assert.False(loaded.DefaultNormalizeAudio);
        Assert.True(loaded.DefaultUpscaleToFhd);
        Assert.Equal(-14.0, loaded.DefaultTargetLoudnessLufs);
        Assert.Equal(-1.0, loaded.DefaultTargetTruePeakDb);
        Assert.Equal(9.0, loaded.DefaultTargetLoudnessRange);
        Assert.Equal(@"D:\Media", loaded.DefaultOutputDirectory);
        Assert.Equal("%(title)s.%(ext)s", loaded.DefaultOutputTemplate);
        Assert.Equal(5, loaded.MaxConcurrentDownloads);
    }

    [Fact]
    public async Task 並列数は1以上に正規化される()
    {
        var paths = new TestAppPaths(_tempDirectory);
        var store = new JsonAppSettingsStore(paths, NullLogger<JsonAppSettingsStore>.Instance);

        await store.SaveAsync(new AppSettings
        {
            DefaultFormatModeKey = "",
            DefaultNormalizeAudio = true,
            DefaultTargetLoudnessLufs = -99,
            DefaultTargetTruePeakDb = 2,
            DefaultTargetLoudnessRange = 0,
            DefaultOutputDirectory = "",
            DefaultOutputTemplate = "",
            MaxConcurrentDownloads = 0,
        });

        var loaded = await store.LoadAsync();
        Assert.Equal("avc-aac", loaded.DefaultFormatModeKey);
        Assert.Equal("%(title)s [%(id)s].%(ext)s", loaded.DefaultOutputTemplate);
        Assert.Equal(1, loaded.MaxConcurrentDownloads);
        Assert.Equal(-70.0, loaded.DefaultTargetLoudnessLufs);
        Assert.Equal(0.0, loaded.DefaultTargetTruePeakDb);
        Assert.Equal(1.0, loaded.DefaultTargetLoudnessRange);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private sealed class TestAppPaths(string root) : IAppPaths
    {
        public string ConfigDirectory { get; } = Path.Combine(root, "config");
        public string LocalDataDirectory { get; } = Path.Combine(root, "local");
        public string ToolsDirectory { get; } = Path.Combine(root, "tools");
        public string LogsDirectory { get; } = Path.Combine(root, "logs");
    }
}
