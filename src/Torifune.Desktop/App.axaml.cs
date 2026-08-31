using System;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Torifune.Core.Platform;
using Torifune.Core.Services.Normalization;
using Torifune.Core.Services.PostProcessing;
using Torifune.Core.Services.Preview;
using Torifune.Core.Services.Queue;
using Torifune.Core.Services.Settings;
using Torifune.Core.Services.Tools;
using Torifune.Core.Services.Ytdlp;
using Torifune.Desktop.Diagnostics;
using Torifune.ViewModels;

namespace Torifune.Desktop;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;

    /// <summary>アプリ全体の DI コンテナ。</summary>
    public IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _serviceProvider = ConfigureServices();
        Services = _serviceProvider;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownRequested += OnShutdownRequested;
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        try
        {
            if (_serviceProvider is not null)
            {
                await _serviceProvider.DisposeAsync();
            }
        }
        finally
        {
            _shutdownCompleted = true;
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<DebugConsoleLogStore>();
        services.AddSingleton<ILoggerProvider, DebugConsoleLoggerProvider>();
        services.AddLogging(builder =>
        {
#if DEBUG
            builder.SetMinimumLevel(LogLevel.Debug);
#else
            builder.SetMinimumLevel(LogLevel.Warning);
#endif
        });

        // GitHub Releases へのアクセス用(User-Agent 必須)
        services.AddSingleton(_ =>
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"Torifune/{version}");
            return client;
        });

        // Core サービス
        services.AddSingleton<IAppPaths, DefaultAppPaths>();
        services.AddSingleton<IAppSettingsStore, JsonAppSettingsStore>();
        services.AddSingleton<IToolManager, ToolManager>();
        services.AddSingleton<IAudioNormalizationService, AudioNormalizationService>();
        services.AddSingleton<IMediaPostProcessingService, MediaPostProcessingService>();
        services.AddSingleton(new YtdlpRecoveryOptions());
        services.AddSingleton<IYtdlpProcessService, YtdlpService>();
        services.AddSingleton<IYtdlpService, ResilientYtdlpService>();
        services.AddSingleton<IPreviewSourceService, PreviewSourceService>();
        services.AddSingleton<IPreviewAnalysisService, PreviewAnalysisService>();
        services.AddSingleton<IDownloadQueueService, DownloadQueueService>();

        // ViewModels
        services.AddTransient<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
