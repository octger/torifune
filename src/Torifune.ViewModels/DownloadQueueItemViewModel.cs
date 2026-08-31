using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;
using Torifune.Core.Models;
using Torifune.Core.Services.PostProcessing;
using Torifune.Core.Services.Queue;

namespace Torifune.ViewModels;

/// <summary>キューの1行を表示・操作する ViewModel。</summary>
public sealed partial class DownloadQueueItemViewModel : ViewModelBase
{
    private readonly IDownloadQueueService _queue;
    private DownloadQueueItem _item;

    public DownloadQueueItemViewModel(IDownloadQueueService queue, DownloadQueueItem item)
    {
        _queue = queue;
        _item = item;
    }

    public DownloadQueueItem Snapshot => _item;

    public Guid Id => _item.Id;
    public string Title => _item.Title;
    public double ProgressPercent => _item.ProgressPercent;
    public bool HasMeasuredProgress => _item.ProgressPercent > 0 || _item.DownloadedBytes is not null || _item.TotalBytes is not null;
    public string StatusColor => _item.Status switch
    {
        DownloadQueueStatus.Completed => "#FF1B8F4A",
        DownloadQueueStatus.Failed => "#FFC7332D",
        DownloadQueueStatus.Canceled => "#FF7A7A7A",
        DownloadQueueStatus.Queued => "#FF6E7F8E",
        DownloadQueueStatus.Running when !HasMeasuredProgress => "#FF8A63D2",
        DownloadQueueStatus.Running => "#FF1769AA",
        DownloadQueueStatus.PostProcessing => "#FFC67C00",
        DownloadQueueStatus.Normalizing => "#FF2AA889",
        DownloadQueueStatus.Upscaling => "#FF2C8FDB",
        DownloadQueueStatus.Paused => "#FF8A8A8A",
        _ => "#FF1769AA",
    };

    public string SecondaryStatusText => _item.Status switch
    {
        DownloadQueueStatus.Queued => "開始待ち",
        DownloadQueueStatus.Running when !HasMeasuredProgress => "ダウンロード準備中",
        DownloadQueueStatus.Running => "転送中",
        DownloadQueueStatus.PostProcessing => "後処理中",
        DownloadQueueStatus.Normalizing => "正規化中",
        DownloadQueueStatus.Upscaling => "FHD変換中",
        DownloadQueueStatus.Paused => "手動停止",
        DownloadQueueStatus.Completed => "保存完了",
        DownloadQueueStatus.Failed => "要再試行",
        DownloadQueueStatus.Canceled => "中断",
        _ => "",
    };

    public string StatusText => _item.Status switch
    {
        DownloadQueueStatus.Queued => "待機中",
        DownloadQueueStatus.Running when !HasMeasuredProgress => "準備中",
        DownloadQueueStatus.Running => "ダウンロード中",
        DownloadQueueStatus.PostProcessing => "後処理中",
        DownloadQueueStatus.Normalizing => "音声正規化中",
        DownloadQueueStatus.Upscaling => "FHD変換中",
        DownloadQueueStatus.Paused => "一時停止",
        DownloadQueueStatus.Completed => "完了",
        DownloadQueueStatus.Failed => "失敗",
        DownloadQueueStatus.Canceled => "キャンセル済み",
        _ => _item.Status.ToString(),
    };

    public string DetailText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_item.ErrorMessage))
            {
                return _item.ErrorMessage;
            }
            if (_item.Status == DownloadQueueStatus.Completed)
            {
                return _item.OutputPath ?? "保存完了";
            }
            if (!string.IsNullOrWhiteSpace(_item.StatusMessage))
            {
                return _item.StatusMessage;
            }

            if (_item.Status is DownloadQueueStatus.Running or DownloadQueueStatus.PostProcessing or DownloadQueueStatus.Normalizing or DownloadQueueStatus.Upscaling)
            {
                return ProgressHintText;
            }

            var parts = new List<string>(3);
            if (_item is { DownloadedBytes: { } downloaded, TotalBytes: { } total })
            {
                parts.Add($"{downloaded / 1048576.0:F1}MB / {total / 1048576.0:F1}MB");
            }
            if (_item.SpeedBytesPerSec is { } speed)
            {
                parts.Add($"{speed / 1048576.0:F1}MB/s");
            }
            if (_item.Eta is { } eta)
            {
                parts.Add($"残り {eta:mm\\:ss}");
            }
            return string.Join("  ", parts);
        }
    }

    public bool HasMediaTechnicalInfo =>
        !string.IsNullOrWhiteSpace(VideoResolutionText) ||
        !string.IsNullOrWhiteSpace(AudioTechnicalInfoText) ||
        !string.IsNullOrWhiteSpace(TotalBitrateText);

    public string ProgressHintText => _item.Status switch
    {
        DownloadQueueStatus.Running when !HasMeasuredProgress => "接続とメタ情報取得を進めています...",
        DownloadQueueStatus.Running => "ダウンロード進行中",
        DownloadQueueStatus.PostProcessing => "ダウンロード後の変換・マージを進めています...",
        DownloadQueueStatus.Normalizing => "音声正規化を進めています...",
        DownloadQueueStatus.Upscaling => "アスペクト比を維持して1920x1080へ変換しています...",
        DownloadQueueStatus.Paused => "一時停止中",
        _ => "",
    };

    public string ElapsedText
    {
        get
        {
            var end = _item.CompletedAt ?? DateTimeOffset.UtcNow;
            var elapsed = end - _item.CreatedAt;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            var prefix = _item.Status == DownloadQueueStatus.Completed ? "所要" : "経過";
            return elapsed.TotalHours >= 1
                ? $"{prefix} {elapsed:hh\\:mm\\:ss}"
                : $"{prefix} {elapsed:mm\\:ss}";
        }
    }

    public bool ShowMeasuredProgressBar => IsProgressVisible && HasMeasuredProgress;

    public bool ShowIndeterminateProgressBar => IsProgressVisible && !HasMeasuredProgress;

    public string VideoResolutionText
    {
        get
        {
            if (_item.VideoWidth is null || _item.VideoHeight is null)
            {
                return "";
            }

            var codecText = string.IsNullOrWhiteSpace(_item.VideoCodec) ? "" : $" / {_item.VideoCodec}";
            var fpsText = _item.VideoFps is > 0 ? $" / {FormatFps(_item.VideoFps.Value)} fps" : "";
            return $"映像: {_item.VideoWidth}x{_item.VideoHeight} ({_item.VideoHeight}p){fpsText}{codecText}";
        }
    }

    public string AudioTechnicalInfoText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(_item.AudioCodec))
            {
                parts.Add(_item.AudioCodec!);
            }
            if (_item.AudioBitrate is { } bitrate && bitrate > 0)
            {
                parts.Add($"{bitrate / 1000.0:F0} kbps");
            }
            if (_item.AudioSampleRate is { } sampleRate && sampleRate > 0)
            {
                parts.Add($"{sampleRate} Hz");
            }
            if (_item.AudioChannels is { } channels && channels > 0)
            {
                parts.Add($"{channels} ch");
            }

            return parts.Count == 0 ? "" : $"音声: {string.Join(" / ", parts)}";
        }
    }

    public string TotalBitrateText =>
        _item.TotalBitrate is > 0
            ? $"総ビットレート: {_item.TotalBitrate.Value / 1000.0:F0} kbps"
            : "";

    public bool IsProgressVisible => _item.Status is
        DownloadQueueStatus.Running or DownloadQueueStatus.PostProcessing or
        DownloadQueueStatus.Normalizing or DownloadQueueStatus.Upscaling or DownloadQueueStatus.Paused;
    public bool CanPause => _item.Status is
        DownloadQueueStatus.Running or DownloadQueueStatus.PostProcessing or DownloadQueueStatus.Normalizing;
    public bool CanResume => _item.Status == DownloadQueueStatus.Paused;
    public bool CanCancel => _item.Status is
        DownloadQueueStatus.Queued or DownloadQueueStatus.Running or
        DownloadQueueStatus.PostProcessing or DownloadQueueStatus.Normalizing or DownloadQueueStatus.Upscaling or DownloadQueueStatus.Paused;
    public bool CanRetry => _item.Status is DownloadQueueStatus.Failed or DownloadQueueStatus.Canceled;
    public bool CanOpenOutputFile =>
        _item.Status == DownloadQueueStatus.Completed &&
        !string.IsNullOrWhiteSpace(_item.OutputPath) &&
        File.Exists(_item.OutputPath);

    public bool CanOpenOutputFolder =>
        _item.Status == DownloadQueueStatus.Completed &&
        ResolveOutputFolderPath() is not null;

    public bool CanUpscaleToFhd =>
        _item.Status == DownloadQueueStatus.Completed &&
        !string.IsNullOrWhiteSpace(_item.OutputPath) &&
        File.Exists(_item.OutputPath) &&
        (_item.VideoWidth is null ||
         _item.VideoHeight is null ||
         MediaPostProcessingService.ShouldUpscaleToFhd(_item.VideoWidth.Value, _item.VideoHeight.Value));

    public void Update(DownloadQueueItem item)
    {
        _item = item;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(HasMeasuredProgress));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(SecondaryStatusText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(ProgressHintText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(HasMediaTechnicalInfo));
        OnPropertyChanged(nameof(VideoResolutionText));
        OnPropertyChanged(nameof(AudioTechnicalInfoText));
        OnPropertyChanged(nameof(TotalBitrateText));
        OnPropertyChanged(nameof(IsProgressVisible));
        OnPropertyChanged(nameof(ShowMeasuredProgressBar));
        OnPropertyChanged(nameof(ShowIndeterminateProgressBar));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanOpenOutputFile));
        OnPropertyChanged(nameof(CanOpenOutputFolder));
        OnPropertyChanged(nameof(CanUpscaleToFhd));
    }

    public void RefreshLiveIndicators()
    {
        if (_item.Status is DownloadQueueStatus.Completed or DownloadQueueStatus.Failed or DownloadQueueStatus.Canceled)
        {
            return;
        }

        OnPropertyChanged(nameof(ElapsedText));
    }

    [RelayCommand]
    private Task PauseAsync() => _queue.PauseAsync(Id);

    [RelayCommand]
    private Task ResumeAsync() => _queue.ResumeAsync(Id);

    [RelayCommand]
    private Task CancelAsync() => _queue.CancelAsync(Id);

    [RelayCommand]
    private Task RetryAsync() => _queue.RetryAsync(Id);

    [RelayCommand]
    private Task UpscaleToFhdAsync() => _queue.UpscaleCompletedToFhdAsync(Id);

    [RelayCommand]
    private Task RemoveAsync() => _queue.RemoveAsync(Id);

    [RelayCommand]
    private void OpenOutputFile()
    {
        if (!CanOpenOutputFile)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _item.OutputPath!,
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var folderPath = ResolveOutputFolderPath();
        if (folderPath is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_item.OutputPath) && File.Exists(_item.OutputPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_item.OutputPath}\"",
                UseShellExecute = true,
            });
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = folderPath,
            UseShellExecute = true,
        });
    }

    private string? ResolveOutputFolderPath()
    {
        if (!string.IsNullOrWhiteSpace(_item.OutputPath) && File.Exists(_item.OutputPath))
        {
            return Path.GetDirectoryName(_item.OutputPath);
        }

        var outputDir = _item.Options.OutputDirectory;
        return Directory.Exists(outputDir) ? outputDir : null;
    }

    private static string FormatFps(double fps)
    {
        var rounded = Math.Round(fps);
        return Math.Abs(fps - rounded) < 0.01 ? rounded.ToString("F0") : fps.ToString("F2");
    }
}
