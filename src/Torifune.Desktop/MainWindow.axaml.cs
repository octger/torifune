using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using LibVLCSharp.Shared;
using Torifune.Desktop.Diagnostics;
using Torifune.ViewModels;

namespace Torifune.Desktop;

public partial class MainWindow : Window
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly DispatcherTimer _positionTimer;
    private MainViewModel? _currentVm;
    private bool _isPreviewViewAttached;
    private string? _pendingPreviewPath;
    private bool _isSeekBarDragging;
    private bool _isUpdatingSeekBarFromPlayback;
    private bool _pausePreviewWhenReady;
    private DateTimeOffset _lastQueueIndicatorRefreshAt = DateTimeOffset.MinValue;
    private readonly DebugConsoleLogStore? _debugLogStore;
    private DebugConsoleWindow? _debugConsoleWindow;

    public MainWindow()
    {
        InitializeComponent();

        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--quiet");
        _mediaPlayer = new MediaPlayer(_libVlc);
        _mediaPlayer.Volume = 0;

        if (Application.Current is App app)
        {
            _debugLogStore = app.Services.GetService(typeof(DebugConsoleLogStore)) as DebugConsoleLogStore;
        }

        PreviewVideoView.AttachedToVisualTree += (_, _) =>
        {
            _isPreviewViewAttached = true;
            PreviewVideoView.MediaPlayer = _mediaPlayer;

            if (!string.IsNullOrWhiteSpace(_pendingPreviewPath))
            {
                var path = _pendingPreviewPath;
                _pendingPreviewPath = null;
                OpenPreviewVideo(path);
            }
        };
        PreviewVideoView.DetachedFromVisualTree += (_, _) =>
        {
            _isPreviewViewAttached = false;
            PreviewVideoView.MediaPlayer = null;
        };

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _positionTimer.Tick += (_, _) => SyncPreviewPlaybackState();
        _positionTimer.Start();

        DataContextChanged += OnDataContextChanged;

        // ウィンドウ表示後にツールのセットアップを開始する
        Opened += (_, _) =>
        {
            ApplyDisplayBounds();

            if (DataContext is MainViewModel vm)
            {
                vm.InitializeCommand.Execute(null);
            }
        };

        Closed += (_, _) =>
        {
            if (_debugConsoleWindow is { } debugWindow)
            {
                debugWindow.Close();
                _debugConsoleWindow = null;
            }

            _positionTimer.Stop();
            _mediaPlayer.Dispose();
            _libVlc.Dispose();
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_currentVm is not null)
        {
            _currentVm.PropertyChanged -= OnViewModelPropertyChanged;
            _currentVm = null;
        }

        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        _currentVm = vm;
        _currentVm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm)
        {
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.PreviewVideoPath))
        {
            OpenPreviewVideo(vm.PreviewVideoPath);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.PreviewSeekRequestId))
        {
            SeekPreview(vm.PreviewSeekTargetSeconds);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.PreviewTogglePlaybackRequestId))
        {
            TogglePreviewPlayback();
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.PreviewPlaybackVolumePercent))
        {
            _mediaPlayer.Volume = Math.Clamp(vm.PreviewPlaybackVolumePercent, 0, 100);
        }
    }

    private void OpenPreviewVideo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            _pendingPreviewPath = null;
            _mediaPlayer.Stop();
            return;
        }

        if (!_isPreviewViewAttached)
        {
            _pendingPreviewPath = path;
            return;
        }

        if (!ReferenceEquals(PreviewVideoView.MediaPlayer, _mediaPlayer))
        {
            PreviewVideoView.MediaPlayer = _mediaPlayer;
        }

        _pausePreviewWhenReady = true;
        using var media = new Media(_libVlc, new Uri(path));
        _mediaPlayer.Play(media);
    }

    private void SeekPreview(double seconds)
    {
        if (_mediaPlayer.Media is null)
        {
            return;
        }

        var targetMs = Math.Max(0, (long)(seconds * 1000));
        _mediaPlayer.Time = targetMs;
    }

    private void SyncPreviewPlaybackState()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastQueueIndicatorRefreshAt >= TimeSpan.FromSeconds(1))
        {
            vm.RefreshQueueLiveIndicators();
            _lastQueueIndicatorRefreshAt = now;
        }

        vm.IsPreviewPlaying = _mediaPlayer.IsPlaying;
        if (!_isSeekBarDragging)
        {
            _isUpdatingSeekBarFromPlayback = true;
            vm.PreviewCurrentPositionSeconds = _mediaPlayer.Time > 0 ? _mediaPlayer.Time / 1000.0 : 0;
            _isUpdatingSeekBarFromPlayback = false;
        }

        if (_pausePreviewWhenReady && _mediaPlayer.IsPlaying)
        {
            _mediaPlayer.SetPause(true);
            _pausePreviewWhenReady = false;
            vm.IsPreviewPlaying = false;
        }

        var lengthMs = _mediaPlayer.Length;
        if (lengthMs > 0)
        {
            vm.PreviewDurationSeconds = lengthMs / 1000.0;
        }
    }

    private void PreviewSeekBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isSeekBarDragging = true;
    }

    private void PreviewSeekBar_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isSeekBarDragging = false;

        if (sender is Slider slider)
        {
            SeekPreview(slider.Value);
        }
    }

    private void PreviewSeekBar_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isSeekBarDragging = false;
    }

    private void PreviewSeekBar_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingSeekBarFromPlayback)
        {
            return;
        }

        SeekPreview(e.NewValue);
    }

    private void TogglePreviewPlayback()
    {
        if (_mediaPlayer.Media is null)
        {
            return;
        }

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.SetPause(true);
        }
        else
        {
            _mediaPlayer.SetPause(false);
            _mediaPlayer.Play();
        }
    }

    private void PreviewVideoOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            TogglePreviewPlayback();
            e.Handled = true;
        }
    }

    private void PreviewHistogramBeforeHost_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        vm.PreviewHistogramBeforeDisplayWidth = Math.Max(120, e.NewSize.Width);
    }

    private void ApplyDisplayBounds()
    {
        var screen = Screens.ScreenFromVisual(this);
        if (screen is null)
        {
            return;
        }

        var workingArea = screen.WorkingArea;
        MaxWidth = Math.Max(MinWidth, workingArea.Width - 24);
        MaxHeight = Math.Max(MinHeight, workingArea.Height - 24);

        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);

        ApplyQueueListBounds(workingArea.Height);
    }

    private void ApplyQueueListBounds(double workingAreaHeight)
    {
        var listMaxHeight = Math.Clamp(workingAreaHeight * 0.42, 220, 520);
        ActiveQueueList.MaxHeight = listMaxHeight;
        CompletedQueueList.MaxHeight = listMaxHeight;
    }

    private async void BrowseOutputDirectory_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var start = string.IsNullOrWhiteSpace(vm.DefaultOutputDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : vm.DefaultOutputDirectory;
        var suggested = await StorageProvider.TryGetFolderFromPathAsync(start);

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "保存先フォルダを選択",
            SuggestedStartLocation = suggested,
            AllowMultiple = false,
        });

        var selected = folders.FirstOrDefault();
        if (selected is null)
        {
            return;
        }

        await vm.SetOutputDirectoryCommand.ExecuteAsync(selected.Path.LocalPath);
    }

    private async void OutputDirectoryTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            await vm.SetOutputDirectoryCommand.ExecuteAsync(vm.DefaultOutputDirectory);
        }
    }

    private void OpenDebugConsole_Click(object? sender, RoutedEventArgs e)
    {
        if (_debugLogStore is null)
        {
            return;
        }

        if (_debugConsoleWindow is null)
        {
            _debugConsoleWindow = new DebugConsoleWindow(_debugLogStore);
            _debugConsoleWindow.Closed += (_, _) => _debugConsoleWindow = null;
        }

        if (!_debugConsoleWindow.IsVisible)
        {
            _debugConsoleWindow.Show(this);
        }
        _debugConsoleWindow.Activate();
    }
}
