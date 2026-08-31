using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Torifune.Core.Models;
using Torifune.Core.Services.Preview;
using Torifune.Core.Services.Queue;
using Torifune.Core.Services.Settings;
using Torifune.Core.Services.Tools;
using Torifune.Core.Services.Ytdlp;

namespace Torifune.ViewModels;

/// <summary>
/// メインウィンドウの ViewModel。
/// ツールセットアップ、URL解析、プレイリスト選択、設定保存を担う。
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private const string FormatModeAvcAac = "avc-aac";
    private const string FormatModeBest = "best";
    private const string FormatModeAudioOnly = "audio-only";
    private const string FormatModeSelect = "select";
    private const string PreviewQualityFast = "fast";
    private const string PreviewQualityBalanced = "balanced";
    private const string PreviewQualityVisual = "visual";

    private readonly IToolManager _toolManager;
    private readonly IYtdlpService _ytdlp;
    private readonly IPreviewSourceService _previewSourceService;
    private readonly IPreviewAnalysisService _previewAnalysisService;
    private readonly IDownloadQueueService _queue;
    private readonly IAppSettingsStore _settingsStore;
    private readonly ILogger<MainViewModel> _logger;
    private readonly SynchronizationContext? _uiContext;

    private AppSettings _settings = new();
    private readonly Dictionary<string, MediaInfo> _playlistInfoCache = [];
    private string? _analyzedUrl;
    private MediaInfo? _analyzedMediaInfo;
    private int _toastVersion;
    private bool _isApplyingNormalizationPreset;
    private bool _isNormalizingTimeInput;
    private string? _previewSourceVideoPath;
    private string? _previewSourceUrl;
    private string? _previewSourceFormatString;
    private string? _previewHistogramCacheKey;

    [ObservableProperty]
    private string _ytdlpStatus = "確認中...";

    [ObservableProperty]
    private string _ffmpegStatus = "確認中...";

    [ObservableProperty]
    private string _setupMessage = "";

    [ObservableProperty]
    private double _setupProgress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptDependencyDownloadCommand))]
    private bool _isSettingUp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptDependencyDownloadCommand))]
    private bool _isDependencyConsentRequired;

    [ObservableProperty]
    private string _missingDependenciesText = "";

    [ObservableProperty]
    private string _dependencyConsentMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptDependencyDownloadCommand))]
    private bool _hasAcceptedDependencyTerms;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeUrlCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedPlaylistToQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshPreviewNormalizationCommand))]
    private bool _isReady;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeUrlCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedPlaylistToQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryPreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshPreviewNormalizationCommand))]
    private string _urlInput = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeUrlCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedPlaylistToQueueCommand))]
    private bool _isAddingToQueue;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeUrlCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedPlaylistToQueueCommand))]
    private bool _isAnalyzingUrl;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedPlaylistToQueueCommand))]
    private bool _isAddingPlaylistToQueue;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedPlaylistToQueueCommand))]
    [NotifyPropertyChangedFor(nameof(IsFormatListMode))]
    private FormatModeOption? _selectedFormatMode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToQueueCommand))]
    private FormatChoiceOption? _selectedVideoFormatOption;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToQueueCommand))]
    private FormatChoiceOption? _selectedAudioFormatOption;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    private FormatModeOption? _defaultFormatMode;

    [ObservableProperty]
    private bool _defaultNormalizeAudio = true;

    [ObservableProperty]
    private bool _normalizeAudioForCurrent = true;

    [ObservableProperty]
    private bool _defaultUpscaleToFhd;

    [ObservableProperty]
    private bool _upscaleToFhdForCurrent;

    [ObservableProperty]
    private string _startTimeText = "00:00:00";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryPreviewCommand))]
    private string _endTimeText = "";

    [ObservableProperty]
    private string _selectionTimeMessage = "開始・終了は HH:MM:SS 形式で指定できます。";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryPreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshPreviewNormalizationCommand))]
    private bool _isGeneratingSnapshot;

    [ObservableProperty]
    private bool _isPreviewSourceDownloading;

    [ObservableProperty]
    private double? _previewSourceProgressPercent;

    [ObservableProperty]
    private string _previewSourceProgressText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewSourceEta))]
    private string _previewSourceEtaText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewSourceSpeed))]
    private string _previewSourceSpeedText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPositionText))]
    [NotifyPropertyChangedFor(nameof(PreviewHistogramPlaybackMarkerLeft))]
    private double _previewCurrentPositionSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPositionText))]
    [NotifyPropertyChangedFor(nameof(PreviewHistogramPlaybackMarkerLeft))]
    private double _previewDurationSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewHistogramPlaybackMarkerLeft))]
    private double _previewHistogramBeforeDisplayWidth = 480;

    [ObservableProperty]
    private bool _isPreviewPrepared;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewStatusOverlay))]
    private string _previewStatusOverlayText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewVolumeButtonText))]
    private bool _isPreviewMuted = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPlayPauseButtonText))]
    private bool _isPreviewPlaying;

    [ObservableProperty]
    private double _previewSegmentStartSeconds;

    [ObservableProperty]
    private double _previewSegmentEndSeconds;

    [ObservableProperty]
    private double _previewSeekTargetSeconds;

    [ObservableProperty]
    private int _previewSeekRequestId;

    [ObservableProperty]
    private int _previewTogglePlaybackRequestId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewVolumeDisplayText))]
    [NotifyPropertyChangedFor(nameof(PreviewVolumeButtonText))]
    private int _previewPlaybackVolumePercent;

    [ObservableProperty]
    private string? _snapshotImagePath;

    [ObservableProperty]
    private string? _snapshotPreviewPath;

    [ObservableProperty]
    private string? _startPreviewImagePath;

    [ObservableProperty]
    private string? _endPreviewImagePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewVideo))]
    private string? _previewVideoPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewHistogram))]
    private string? _previewHistogramBeforeImagePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewHistogram))]
    private string? _previewHistogramAfterImagePath;

    [ObservableProperty]
    private bool _isPreviewHistogramGenerating;

    [ObservableProperty]
    private string _previewHistogramMessage = "";

    [ObservableProperty]
    private string _previewHistogramAppliedRangeText = "";

    [ObservableProperty]
    private bool _isPreviewNormalizationStale;

    [ObservableProperty]
    private string _previewNormalizationProgressText = "";

    [ObservableProperty]
    private string _previewHistogramBeforeMetricsLabel = "";

    [ObservableProperty]
    private string _previewHistogramAfterMetricsLabel = "";

    [ObservableProperty]
    private string _previewHistogramLufsDiffLabel = "";

    [ObservableProperty]
    private string _previewHistogramTpDiffLabel = "";

    [ObservableProperty]
    private string _previewHistogramLraDiffLabel = "";

    [ObservableProperty]
    private string _previewHistogramLufsDiffColor = "#9AA7B2";

    [ObservableProperty]
    private string _previewHistogramTpDiffColor = "#9AA7B2";

    [ObservableProperty]
    private string _previewHistogramLraDiffColor = "#9AA7B2";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryPreviewCommand))]
    private bool _hasPreviewGenerationError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryPreviewCommand))]
    private bool _isAutoPreviewEnabled = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryPreviewCommand))]
    private bool _isAutoPreviewPending;

    [ObservableProperty]
    private bool _isPreviewCacheBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    private double _defaultTargetLoudnessLufs = -14.0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    private double _defaultTargetTruePeakDb = -1.0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    private double _defaultTargetLoudnessRange = 9.0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    private NormalizationPresetOption? _selectedNormalizationPreset;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    private int _defaultMaxConcurrentDownloads = 3;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    private string _defaultOutputDirectory = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    private string _defaultOutputTemplate = "%(title)s [%(id)s].%(ext)s";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    private PreviewQualityOption? _selectedPreviewQualityOption;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private string _settingsMessage = "";

    [ObservableProperty]
    private bool _hasAvailableVideoFormats;

    [ObservableProperty]
    private bool _hasAvailableAudioFormats;

    [ObservableProperty]
    private bool _isPlaylistDetected;

    [ObservableProperty]
    private string _playlistTitle = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedPlaylistToQueueCommand))]
    private int _selectedPlaylistCount;

    [ObservableProperty]
    private string _queueMessage = "";

    [ObservableProperty]
    private string _queueSummary = "キューは空です";

    [ObservableProperty]
    private bool _hasQueueItems;

    [ObservableProperty]
    private bool _hasActiveQueueItems;

    [ObservableProperty]
    private bool _hasCompletedQueueItems;

    [ObservableProperty]
    private DownloadQueueItemViewModel? _selectedQueueItem;

    [ObservableProperty]
    private bool _isToastVisible;

    [ObservableProperty]
    private string _toastMessage = "";

    [ObservableProperty]
    private bool _isTransientBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyPlaylistEntryDetailCommand))]
    [NotifyPropertyChangedFor(nameof(IsPlaylistDetailOpen))]
    private PlaylistEntrySelectionViewModel? _editingPlaylistEntry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyPlaylistEntryDetailCommand))]
    private FormatChoiceOption? _selectedPlaylistDetailVideoFormatOption;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyPlaylistEntryDetailCommand))]
    private FormatChoiceOption? _selectedPlaylistDetailAudioFormatOption;

    [ObservableProperty]
    private bool _usePlaylistDetailNormalizeOverride;

    [ObservableProperty]
    private bool _playlistDetailNormalizeAudioOverride;

    [ObservableProperty]
    private bool _isLoadingPlaylistEntryDetail;

    [ObservableProperty]
    private string _playlistDetailMessage = "";

    public ObservableCollection<FormatModeOption> FormatModes { get; } =
    [
        new(FormatModeAvcAac, "推奨 (AVC/H.264 + AAC, MP4)"),
        new(FormatModeBest, "最高品質 (形式自動)"),
        new(FormatModeAudioOnly, "音声のみ (AAC)"),
        new(FormatModeSelect, "一覧選択 (映像+音声の組み合わせ)")
    ];

    public ObservableCollection<FormatChoiceOption> AvailableVideoFormats { get; } = [];
    public ObservableCollection<FormatChoiceOption> AvailableAudioFormats { get; } = [];
    public ObservableCollection<PlaylistEntrySelectionViewModel> PlaylistEntries { get; } = [];
    public ObservableCollection<DownloadQueueItemViewModel> QueueItems { get; } = [];
    public ObservableCollection<DownloadQueueItemViewModel> ActiveQueueItems { get; } = [];
    public ObservableCollection<DownloadQueueItemViewModel> CompletedQueueItems { get; } = [];
    public ObservableCollection<FormatChoiceOption> PlaylistDetailVideoFormats { get; } = [];
    public ObservableCollection<FormatChoiceOption> PlaylistDetailAudioFormats { get; } = [];
    public ObservableCollection<NormalizationPresetOption> NormalizationPresets { get; } =
    [
        new("streaming", "配信向け (-16 LUFS / -1.5 dBTP / LRA 11)", -16.0, -1.5, 11.0),
        new("music", "音楽向け (-14 LUFS / -1.0 dBTP / LRA 9)", -14.0, -1.0, 9.0),
        new("custom", "カスタム", null, null, null),
    ];
    public ObservableCollection<PreviewQualityOption> PreviewQualityOptions { get; } =
    [
        new(PreviewQualityFast, "高速", "360p 優先 / 軽量"),
        new(PreviewQualityBalanced, "標準", "480p 優先 / バランス"),
        new(PreviewQualityVisual, "高画質", "720p 優先 / 見た目重視"),
    ];

    public bool IsFormatListMode => SelectedFormatMode?.Key == FormatModeSelect;
    public bool HasPlaylistEntries => PlaylistEntries.Count > 0;
    public bool HasPreviewSourceEta => !string.IsNullOrWhiteSpace(PreviewSourceEtaText);
    public bool HasPreviewSourceSpeed => !string.IsNullOrWhiteSpace(PreviewSourceSpeedText);
    public bool HasPreviewVideo => !string.IsNullOrWhiteSpace(PreviewVideoPath) && File.Exists(PreviewVideoPath);
    public bool HasPreviewHistogram =>
        !string.IsNullOrWhiteSpace(PreviewHistogramBeforeImagePath) &&
        !string.IsNullOrWhiteSpace(PreviewHistogramAfterImagePath);
    public string PreviewPositionText =>
        $"{FormatClock(PreviewCurrentPositionSeconds)} / {FormatClock(PreviewDurationSeconds)}";
    public string PreviewPlayPauseButtonText => IsPreviewPlaying ? "停止" : "再生";
    public bool HasPreviewStatusOverlay => !string.IsNullOrWhiteSpace(PreviewStatusOverlayText);
    public string PreviewVolumeButtonText => IsPreviewMuted || PreviewPlaybackVolumePercent == 0 ? "ミュート解除" : "ミュート";
    public string PreviewVolumeDisplayText => $"{PreviewPlaybackVolumePercent}%";
    public double PreviewHistogramPlaybackMarkerLeft =>
        PreviewDurationSeconds <= 0
            ? 0
            : Math.Clamp(PreviewCurrentPositionSeconds / PreviewDurationSeconds, 0.0, 1.0) * Math.Max(0.0, PreviewHistogramBeforeDisplayWidth - 2.0);
    public bool HasAppliedNormalizationRange => !string.IsNullOrWhiteSpace(PreviewHistogramAppliedRangeText);
    public bool IsPlaylistDetailOpen => EditingPlaylistEntry is not null;
    public bool HasSelectedQueueItem => SelectedQueueItem is not null;
    public bool IsContextBusy => IsSettingUp || IsAnalyzingUrl || IsAddingToQueue || IsAddingPlaylistToQueue || IsTransientBusy;

    public MainViewModel(
        IToolManager toolManager,
        IYtdlpService ytdlp,
        IPreviewSourceService previewSourceService,
        IPreviewAnalysisService previewAnalysisService,
        IDownloadQueueService queue,
        IAppSettingsStore settingsStore,
        ILogger<MainViewModel> logger)
    {
        _toolManager = toolManager;
        _ytdlp = ytdlp;
        _previewSourceService = previewSourceService;
        _previewAnalysisService = previewAnalysisService;
        _queue = queue;
        _settingsStore = settingsStore;
        _logger = logger;
        _uiContext = SynchronizationContext.Current;

        _queue.ItemsChanged += OnQueueItemsChanged;

        SelectedFormatMode = FormatModes[0];
        DefaultFormatMode = FormatModes[0];
        SelectedNormalizationPreset = NormalizationPresets[0];
    }

    partial void OnUrlInputChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(_analyzedUrl, value.Trim(), StringComparison.Ordinal))
        {
            _analyzedUrl = null;
            _analyzedMediaInfo = null;
            SnapshotImagePath = null;
            SnapshotPreviewPath = null;
            StartPreviewImagePath = null;
            EndPreviewImagePath = null;
            PreviewVideoPath = null;
            PreviewCurrentPositionSeconds = 0;
            PreviewDurationSeconds = 0;
            PreviewSegmentStartSeconds = 0;
            PreviewSegmentEndSeconds = 0;
            PreviewSeekTargetSeconds = 0;
            PreviewSeekRequestId = 0;
            IsPreviewPlaying = false;
            PreviewHistogramBeforeImagePath = null;
            PreviewHistogramAfterImagePath = null;
            IsPreviewHistogramGenerating = false;
            PreviewHistogramMessage = "";
            PreviewHistogramBeforeMetricsLabel = "";
            PreviewHistogramAfterMetricsLabel = "";
            PreviewHistogramLufsDiffLabel = "";
            PreviewHistogramTpDiffLabel = "";
            PreviewHistogramLraDiffLabel = "";
            PreviewHistogramLufsDiffColor = "#9AA7B2";
            PreviewHistogramTpDiffColor = "#9AA7B2";
            PreviewHistogramLraDiffColor = "#9AA7B2";
            _previewSourceVideoPath = null;
            _previewSourceUrl = null;
            _previewSourceFormatString = null;
            _previewHistogramCacheKey = null;
            HasPreviewGenerationError = false;
        }
    }

    partial void OnSelectedFormatModeChanged(FormatModeOption? value)
    {
        if (value?.Key != FormatModeSelect)
        {
            SelectedVideoFormatOption = null;
            SelectedAudioFormatOption = null;
        }
    }

    partial void OnStartTimeTextChanged(string value)
    {
        if (_isNormalizingTimeInput)
        {
            return;
        }

        var normalized = NormalizeTimeInput(value);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            _isNormalizingTimeInput = true;
            try
            {
                StartTimeText = normalized;
            }
            finally
            {
                _isNormalizingTimeInput = false;
            }

            return;
        }

        UpdatePreviewSegmentBounds();
        MarkPreviewRangeChanged();
    }

    partial void OnEndTimeTextChanged(string value)
    {
        if (_isNormalizingTimeInput)
        {
            return;
        }

        var normalized = NormalizeTimeInput(value);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            _isNormalizingTimeInput = true;
            try
            {
                EndTimeText = normalized;
            }
            finally
            {
                _isNormalizingTimeInput = false;
            }

            return;
        }

        UpdatePreviewSegmentBounds();
        MarkPreviewRangeChanged();
    }

    partial void OnPreviewPlaybackVolumePercentChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, 100);
        if (clamped != value)
        {
            PreviewPlaybackVolumePercent = clamped;
            return;
        }

        if (clamped > 0 && IsPreviewMuted)
        {
            IsPreviewMuted = false;
            return;
        }

        if (clamped == 0 && !IsPreviewMuted)
        {
            IsPreviewMuted = true;
        }
    }

    partial void OnPreviewVideoPathChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            PreviewStatusOverlayText = "";
            IsPreviewPlaying = false;
            return;
        }

        PreviewStatusOverlayText = "停止中（クリック/再生ボタンで再生）";
    }

    partial void OnIsPreviewPlayingChanged(bool value)
    {
        if (!HasPreviewVideo)
        {
            return;
        }

        PreviewStatusOverlayText = value
            ? "再生中（クリックで停止）"
            : "停止中（クリック/再生ボタンで再生）";
    }

    partial void OnSelectedQueueItemChanged(DownloadQueueItemViewModel? value) =>
        OnPropertyChanged(nameof(HasSelectedQueueItem));

    partial void OnDefaultTargetLoudnessLufsChanged(double value) =>
        SyncNormalizationPresetFromValues();

    partial void OnDefaultTargetTruePeakDbChanged(double value) =>
        SyncNormalizationPresetFromValues();

    partial void OnDefaultTargetLoudnessRangeChanged(double value) =>
        SyncNormalizationPresetFromValues();

    partial void OnSelectedNormalizationPresetChanged(NormalizationPresetOption? value)
    {
        if (_isApplyingNormalizationPreset || value is null ||
            value.TargetLufs is null || value.TargetTruePeakDb is null || value.TargetLra is null)
        {
            return;
        }

        _isApplyingNormalizationPreset = true;
        try
        {
            DefaultTargetLoudnessLufs = value.TargetLufs.Value;
            DefaultTargetTruePeakDb = value.TargetTruePeakDb.Value;
            DefaultTargetLoudnessRange = value.TargetLra.Value;
        }
        finally
        {
            _isApplyingNormalizationPreset = false;
        }
    }

    partial void OnIsSettingUpChanged(bool value) => OnPropertyChanged(nameof(IsContextBusy));
    partial void OnIsAnalyzingUrlChanged(bool value) => OnPropertyChanged(nameof(IsContextBusy));
    partial void OnIsAddingToQueueChanged(bool value) => OnPropertyChanged(nameof(IsContextBusy));
    partial void OnIsAddingPlaylistToQueueChanged(bool value) => OnPropertyChanged(nameof(IsContextBusy));

    partial void OnDefaultMaxConcurrentDownloadsChanged(int value)
    {
        if (value < 1)
        {
            DefaultMaxConcurrentDownloads = 1;
            return;
        }

        if (IsReady && _queue.MaxConcurrentDownloads != value)
        {
            _queue.MaxConcurrentDownloads = value;
            SettingsMessage = $"並列数を即時反映: {value}";
            UpdateQueueSummary();
        }
    }

    private bool CanAnalyzeUrl() =>
        IsReady && !IsAddingToQueue && !IsAnalyzingUrl && !string.IsNullOrWhiteSpace(UrlInput);

    private bool CanAddToQueue() =>
        IsReady &&
        !IsAddingToQueue &&
        !IsAnalyzingUrl &&
        !IsPlaylistDetected &&
        !string.IsNullOrWhiteSpace(UrlInput) &&
        (!IsFormatListMode || (SelectedVideoFormatOption is not null && SelectedAudioFormatOption is not null));

    private bool CanAddSelectedPlaylistToQueue() =>
        IsReady &&
        IsPlaylistDetected &&
        SelectedPlaylistCount > 0 &&
        !IsAddingPlaylistToQueue &&
        !IsAddingToQueue &&
        !IsAnalyzingUrl;

    private bool CanSaveSettings() =>
        IsReady &&
        !IsSettingUp &&
        DefaultFormatMode is not null &&
        DefaultMaxConcurrentDownloads >= 1 &&
        IsNormalizationRangeValid(DefaultTargetLoudnessLufs, -70.0, -5.0) &&
        IsNormalizationRangeValid(DefaultTargetTruePeakDb, -9.0, 0.0) &&
        IsNormalizationRangeValid(DefaultTargetLoudnessRange, 1.0, 50.0) &&
        !string.IsNullOrWhiteSpace(DefaultOutputDirectory) &&
        !string.IsNullOrWhiteSpace(DefaultOutputTemplate);

    private bool CanGenerateSnapshot() =>
        IsReady && !IsGeneratingSnapshot && !string.IsNullOrWhiteSpace(UrlInput);

    private bool CanRefreshPreviewNormalization() =>
        IsReady &&
        !IsGeneratingSnapshot &&
        !IsPreviewHistogramGenerating &&
        !string.IsNullOrWhiteSpace(UrlInput);

    private bool CanRetryPreview() =>
        IsReady && !IsGeneratingSnapshot && HasPreviewGenerationError && !string.IsNullOrWhiteSpace(UrlInput);

    private bool CanApplyPlaylistEntryDetail() =>
        EditingPlaylistEntry is not null &&
        SelectedPlaylistDetailVideoFormatOption is not null &&
        SelectedPlaylistDetailAudioFormatOption is not null;

    private bool CanApplyPlaylistEntryDetailToSimilar() =>
        EditingPlaylistEntry is not null &&
        SelectedPlaylistDetailVideoFormatOption is not null &&
        SelectedPlaylistDetailAudioFormatOption is not null;

    /// <summary>URL を解析し、動画/音声候補またはプレイリスト項目を更新する。</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyzeUrl), IncludeCancelCommand = true)]
    private async Task AnalyzeUrlAsync(CancellationToken ct)
    {
        var url = UrlInput.Trim();
        IsAnalyzingUrl = true;
        QueueMessage = "URL を解析しています...";

        try
        {
            var info = await _ytdlp.FetchMediaInfoAsync(url, ct);
            _analyzedUrl = url;
            _analyzedMediaInfo = info;

            if (info.IsPlaylist)
            {
                UpdatePlaylistEntries(info);
                QueueMessage =
                    $"プレイリスト({PlaylistEntries.Count} 件)を検出しました。必要な項目を選択してキュー追加してください。";
                ShowToast("プレイリストを検出しました。項目を選択してください。", false);
                return;
            }

            ClearPlaylistEntries();
            UpdateFormatOptions(info.Formats);
            ApplyAnalyzedDuration(info);
            QueueMessage =
                $"解析完了: 動画候補 {AvailableVideoFormats.Count} 件 / 音声候補 {AvailableAudioFormats.Count} 件";
            ShowToast("フォーマット候補を取得しました。", false);

            await PreparePreviewForCurrentRangeAsync(url, ct, reportProgress: true);
        }
        catch (OperationCanceledException)
        {
            QueueMessage = "URL解析をキャンセルしました";
            ShowToast("URL解析をキャンセルしました。", false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "URL解析に失敗");
            QueueMessage = $"エラー: {ex.Message}";
            ShowToast("URL解析に失敗しました。", true);
        }
        finally
        {
            IsAnalyzingUrl = false;
        }
    }

    /// <summary>単一動画をキューへ追加する。</summary>
    [RelayCommand(CanExecute = nameof(CanAddToQueue), IncludeCancelCommand = true)]
    private async Task AddToQueueAsync(CancellationToken ct)
    {
        var url = UrlInput.Trim();
        IsAddingToQueue = true;
        QueueMessage = "URL を解析しています...";

        try
        {
            var info = await ResolveMediaInfoAsync(url, ct);
            if (info.IsPlaylist)
            {
                UpdatePlaylistEntries(info);
                QueueMessage = "プレイリストが検出されました。下の一覧から項目を選んで追加してください。";
                return;
            }

            UpdateFormatOptions(info.Formats);
            if (!TryResolveClipRange(out var startSeconds, out var endSeconds, out var timeError))
            {
                QueueMessage = timeError;
                ShowToast(timeError, true);
                return;
            }

            var options = BuildDownloadOptions(
                url,
                SelectedFormatMode,
                SelectedVideoFormatOption?.FormatId,
                SelectedAudioFormatOption?.FormatId,
                NormalizeAudioForCurrent,
                UpscaleToFhdForCurrent,
                startSeconds,
                endSeconds);
            await _queue.EnqueueAsync(info.Title ?? url, options, ct);
            QueueMessage = "キューに追加しました";
            ShowToast("キューに追加しました。", false);
            UrlInput = "";
            StartTimeText = "00:00:00";
            EndTimeText = "";
        }
        catch (OperationCanceledException)
        {
            QueueMessage = "URL解析をキャンセルしました";
            ShowToast("キュー追加をキャンセルしました。", false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "キューへの追加に失敗");
            QueueMessage = $"エラー: {ex.Message}";
            ShowToast("キューへの追加に失敗しました。", true);
        }
        finally
        {
            IsAddingToQueue = false;
        }
    }

    /// <summary>プレイリストの選択項目を順次キューへ追加する。</summary>
    [RelayCommand(CanExecute = nameof(CanAddSelectedPlaylistToQueue), IncludeCancelCommand = true)]
    private async Task AddSelectedPlaylistToQueueAsync(CancellationToken ct)
    {
        IsAddingPlaylistToQueue = true;
        try
        {
            var selected = PlaylistEntries.Where(entry => entry.IsSelected).ToArray();
            var invalid = selected
                .Where(entry => entry.SelectedFormatMode.Key == FormatModeSelect &&
                                (string.IsNullOrWhiteSpace(entry.SelectedVideoFormatId) ||
                                 string.IsNullOrWhiteSpace(entry.SelectedAudioFormatId)))
                .Select(entry => entry.Title)
                .Take(3)
                .ToArray();
            if (invalid.Length > 0)
            {
                QueueMessage = $"一覧選択モードの設定が不足しています: {string.Join(" / ", invalid)}";
                ShowToast("一覧選択の設定が不足しています。", true);
                return;
            }

            foreach (var entry in selected)
            {
                ct.ThrowIfCancellationRequested();
                if (!TryResolveClipRange(out var startSeconds, out var endSeconds, out var timeError))
                {
                    QueueMessage = timeError;
                    ShowToast(timeError, true);
                    return;
                }

                var options = BuildDownloadOptions(
                    entry.Url,
                    entry.SelectedFormatMode,
                    entry.SelectedVideoFormatId,
                    entry.SelectedAudioFormatId,
                    entry.UseNormalizeOverride ? entry.NormalizeAudioOverride : NormalizeAudioForCurrent,
                    UpscaleToFhdForCurrent,
                    startSeconds,
                    endSeconds);
                await _queue.EnqueueAsync(entry.Title, options, ct);
            }

            QueueMessage = $"プレイリストから {selected.Length} 件をキューに追加しました";
            ShowToast($"{selected.Length}件をキューに追加しました。", false);
        }
        catch (OperationCanceledException)
        {
            QueueMessage = "プレイリスト追加をキャンセルしました";
            ShowToast("プレイリスト追加をキャンセルしました。", false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "プレイリスト項目の追加に失敗");
            QueueMessage = $"エラー: {ex.Message}";
            ShowToast("プレイリスト項目の追加に失敗しました。", true);
        }
        finally
        {
            IsAddingPlaylistToQueue = false;
        }
    }

    [RelayCommand]
    private void SelectAllPlaylistEntries()
    {
        foreach (var entry in PlaylistEntries)
        {
            entry.IsSelected = true;
        }
        UpdatePlaylistSelectionCount();
    }

    [RelayCommand]
    private void ClearPlaylistSelection()
    {
        foreach (var entry in PlaylistEntries)
        {
            entry.IsSelected = false;
        }
        UpdatePlaylistSelectionCount();
    }

    [RelayCommand(CanExecute = nameof(CanGenerateSnapshot))]
    private async Task GenerateSnapshotAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(UrlInput))
        {
            SelectionTimeMessage = "先に URL を入力してください。";
            ShowToast("先に URL を入力してください。", true);
            return;
        }

        await PreparePreviewForCurrentRangeAsync(UrlInput.Trim(), ct, reportProgress: true);
    }

    private async Task PreparePreviewForCurrentRangeAsync(string url, CancellationToken ct, bool reportProgress)
    {
        if (!TryResolveClipRange(out var startSeconds, out var endSeconds, out var error))
        {
            SelectionTimeMessage = error;
            ShowToast(error, true);
            return;
        }

        IsGeneratingSnapshot = true;
        HasPreviewGenerationError = false;
        SelectionTimeMessage = "確認用動画を準備しています...";
        IsPreviewSourceDownloading = false;
        PreviewSourceProgressPercent = null;
        PreviewSourceProgressText = "";
        PreviewSourceEtaText = "";
        PreviewSourceSpeedText = "";

        try
        {
            var sourcePath = await EnsurePreviewSourceVideoAsync(url, ct, reportProgress: reportProgress);
            PreviewVideoPath = sourcePath;
            var previewDurationSeconds = await _previewSourceService.ProbeDurationSecondsAsync(sourcePath, ct);
            PreviewDurationSeconds = previewDurationSeconds ?? 0;

            var rawStartSeconds = startSeconds ?? 0;
            var rawEndSeconds = endSeconds ?? rawStartSeconds;
            PreviewSegmentStartSeconds = ClampFrameOffset(rawStartSeconds, previewDurationSeconds);
            PreviewSegmentEndSeconds = ClampFrameOffset(rawEndSeconds, previewDurationSeconds);
            RequestPreviewSeek(PreviewSegmentStartSeconds);

            PreviewNormalizationProgressText = "動画全体の正規化前後を解析しています...";
            await EnsurePreviewHistogramAsync(url, sourcePath, null, null, ct);

            SelectionTimeMessage = "動画プレビューを準備しました。先頭フレームで停止表示しています。音量ヒストグラムは全体表示済みです。開始/終了を変更した場合は反映ボタンで再解析してください。";
        }
        catch (OperationCanceledException)
        {
            SelectionTimeMessage = "プレビュー生成をキャンセルしました。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "プレビュー生成に失敗");
            HasPreviewGenerationError = true;
            SelectionTimeMessage = $"プレビュー生成に失敗しました: {ex.Message}";
            ShowToast("プレビュー生成に失敗しました。Retry で再実行できます。", true);
        }
        finally
        {
            IsGeneratingSnapshot = false;
            IsPreviewSourceDownloading = false;
            PreviewSourceProgressPercent = null;
            PreviewSourceEtaText = "";
            PreviewSourceSpeedText = "";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRetryPreview), IncludeCancelCommand = true)]
    private async Task RetryPreviewAsync(CancellationToken ct)
    {
        await GenerateSnapshotAsync(ct);
    }

    [RelayCommand(CanExecute = nameof(CanRefreshPreviewNormalization), IncludeCancelCommand = true)]
    private async Task RefreshPreviewNormalizationAsync(CancellationToken ct)
    {
        if (!TryResolveClipRange(out var startSeconds, out var endSeconds, out var error))
        {
            SelectionTimeMessage = error;
            ShowToast(error, true);
            return;
        }

        if (string.IsNullOrWhiteSpace(UrlInput))
        {
            SelectionTimeMessage = "先に URL を入力してください。";
            ShowToast("先に URL を入力してください。", true);
            return;
        }

        var sourcePath = await EnsurePreviewSourceVideoAsync(UrlInput.Trim(), ct, reportProgress: true);
        UpdatePreviewSegmentBounds(startSeconds, endSeconds);
        PreviewNormalizationProgressText = "選択範囲の正規化結果を再計算しています...";
        await EnsurePreviewHistogramAsync(UrlInput.Trim(), sourcePath, startSeconds, endSeconds, ct);
        SelectionTimeMessage = "現在の開始/終了時刻に合わせて正規化前後表示を更新しました。";
    }

    [RelayCommand]
    private void TogglePreviewMute()
    {
        IsPreviewMuted = !IsPreviewMuted;
        if (IsPreviewMuted)
        {
            PreviewPlaybackVolumePercent = 0;
        }
        else if (PreviewPlaybackVolumePercent == 0)
        {
            PreviewPlaybackVolumePercent = _settings.DefaultPreviewVolumePercent > 0 ? _settings.DefaultPreviewVolumePercent : 30;
        }
    }

    [RelayCommand]
    private void ClearPreviewCaches()
    {
        IsPreviewCacheBusy = true;
        try
        {
            var previewCacheRoot = Path.Combine(Path.GetTempPath(), "torifune-preview-cache");
            var histogramCacheRoot = Path.Combine(Path.GetTempPath(), "torifune-preview-hist-cache");
            if (Directory.Exists(previewCacheRoot))
            {
                Directory.Delete(previewCacheRoot, recursive: true);
            }

            if (Directory.Exists(histogramCacheRoot))
            {
                Directory.Delete(histogramCacheRoot, recursive: true);
            }

            _previewSourceUrl = null;
            _previewSourceVideoPath = null;
            _previewSourceFormatString = null;
            _previewHistogramCacheKey = null;
            SettingsMessage = "プレビュー/正規化表示キャッシュを削除しました。";
            ShowToast("キャッシュを削除しました。", false);
        }
        finally
        {
            IsPreviewCacheBusy = false;
        }
    }

    [RelayCommand]
    private void SeekPreviewToStart() => RequestPreviewSeek(PreviewSegmentStartSeconds);

    [RelayCommand]
    private void SeekPreviewToEnd() => RequestPreviewSeek(PreviewSegmentEndSeconds);

    [RelayCommand]
    private void SeekPreviewBackward() => RequestPreviewSeek(Math.Max(0, PreviewCurrentPositionSeconds - 3));

    [RelayCommand]
    private void SeekPreviewForward() => RequestPreviewSeek(PreviewCurrentPositionSeconds + 3);

    [RelayCommand]
    private void TogglePreviewPlayback() => PreviewTogglePlaybackRequestId++;

    [RelayCommand]
    private void SetStartTimeFromPreviewPosition()
    {
        StartTimeText = FormatClock(PreviewCurrentPositionSeconds);
        SelectionTimeMessage = "現在の再生位置を開始時刻に反映しました。";
    }

    [RelayCommand]
    private void SetEndTimeFromPreviewPosition()
    {
        EndTimeText = FormatClock(PreviewCurrentPositionSeconds);
        SelectionTimeMessage = "現在の再生位置を終了時刻に反映しました。";
    }

    private void RequestPreviewSeek(double seconds)
    {
        PreviewSeekTargetSeconds = Math.Max(0, seconds);
        PreviewSeekRequestId++;
    }

    private async Task EnsurePreviewHistogramAsync(string url, string sourcePath, int? startSeconds, int? endSeconds, CancellationToken parentCt)
    {
        try
        {
            IsPreviewHistogramGenerating = true;
            PreviewHistogramMessage = "音量ヒストグラムを生成しています...";
            PreviewHistogramLufsDiffLabel = "";
            PreviewHistogramTpDiffLabel = "";
            PreviewHistogramLraDiffLabel = "";
            PreviewHistogramLufsDiffColor = "#9AA7B2";
            PreviewHistogramTpDiffColor = "#9AA7B2";
            PreviewHistogramLraDiffColor = "#9AA7B2";

            var result = await _previewAnalysisService.AnalyzeAsync(
                new PreviewAnalysisRequest(
                    url,
                    sourcePath,
                    startSeconds,
                    endSeconds,
                    DefaultTargetLoudnessLufs,
                    DefaultTargetTruePeakDb,
                    DefaultTargetLoudnessRange),
                parentCt);
            var metrics = result.Metrics;

            _previewHistogramCacheKey = result.CacheKey;
            PreviewHistogramBeforeImagePath = result.BeforeImagePath;
            PreviewHistogramAfterImagePath = result.AfterImagePath;
            PreviewHistogramBeforeMetricsLabel = BuildHistogramMetricsLabel(metrics.InputIntegratedLufs, metrics.InputTruePeakDbtp, metrics.InputLraLu);
            PreviewHistogramAfterMetricsLabel = BuildHistogramMetricsLabel(metrics.OutputIntegratedLufs, metrics.OutputTruePeakDbtp, metrics.OutputLraLu);
            PreviewHistogramLufsDiffLabel = BuildMetricDiffLabel("LUFS", metrics.InputIntegratedLufs, metrics.OutputIntegratedLufs, "");
            PreviewHistogramTpDiffLabel = BuildMetricDiffLabel("TP", metrics.InputTruePeakDbtp, metrics.OutputTruePeakDbtp, " dBTP");
            PreviewHistogramLraDiffLabel = BuildMetricDiffLabel("LRA", metrics.InputLraLu, metrics.OutputLraLu, " LU");
            PreviewHistogramLufsDiffColor = BuildMetricDiffColor(metrics.InputIntegratedLufs, metrics.OutputIntegratedLufs, DefaultTargetLoudnessLufs);
            PreviewHistogramTpDiffColor = BuildMetricDiffColor(metrics.InputTruePeakDbtp, metrics.OutputTruePeakDbtp, DefaultTargetTruePeakDb);
            PreviewHistogramLraDiffColor = BuildMetricDiffColor(metrics.InputLraLu, metrics.OutputLraLu, DefaultTargetLoudnessRange);
            PreviewHistogramAppliedRangeText = $"表示中範囲: {FormatClock(startSeconds ?? 0)} - {FormatClock(endSeconds ?? (int)Math.Max(startSeconds ?? 0, PreviewDurationSeconds > 0 ? PreviewDurationSeconds : 0))}";
            IsPreviewNormalizationStale = false;
            PreviewNormalizationProgressText = "";
            PreviewHistogramMessage = "音量ヒストグラム（正規化前/後）を表示しています。";
        }
        catch (OperationCanceledException)
        {
            PreviewHistogramMessage = "音量ヒストグラム生成をキャンセルしました。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "音量ヒストグラム生成に失敗");
            PreviewHistogramMessage = $"音量ヒストグラムの生成に失敗しました: {ex.Message}";
            PreviewHistogramBeforeMetricsLabel = "";
            PreviewHistogramAfterMetricsLabel = "";
            PreviewHistogramLufsDiffLabel = "";
            PreviewHistogramTpDiffLabel = "";
            PreviewHistogramLraDiffLabel = "";
            PreviewHistogramLufsDiffColor = "#9AA7B2";
            PreviewHistogramTpDiffColor = "#9AA7B2";
            PreviewHistogramLraDiffColor = "#9AA7B2";
            PreviewHistogramAppliedRangeText = "";
            PreviewNormalizationProgressText = "";
        }
        finally
        {
            IsPreviewHistogramGenerating = false;
        }
    }

    private static string BuildHistogramMetricsLabel(double lufs, double tp, double lra) =>
        $"LUFS: {lufs:F1} / TP: {tp:F1} dBTP / LRA: {lra:F1} LU";

    private static string BuildMetricDiffLabel(string metricName, double before, double after, string unit)
    {
        var delta = after - before;
        return $"{metricName}: {before:F1} -> {after:F1}{unit} (Δ {delta:+0.0;-0.0;0.0})";
    }

    private static string BuildMetricDiffColor(double before, double after, double target)
    {
        var beforeDistance = Math.Abs(before - target);
        var afterDistance = Math.Abs(after - target);
        var epsilon = 0.05;

        if (afterDistance + epsilon < beforeDistance)
        {
            return "#1FA768";
        }

        if (afterDistance > beforeDistance + epsilon)
        {
            return "#D45B5B";
        }

        return "#9AA7B2";
    }

    private async Task WarmupPreviewSourceAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await EnsurePreviewSourceVideoAsync(url, cts.Token, reportProgress: false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "プレビュー用軽量動画の事前取得に失敗");
        }
    }

    private async Task<string> EnsurePreviewSourceVideoAsync(string url, CancellationToken ct, bool reportProgress)
    {
        var formatString = BuildPreviewFormatString();
        if (string.Equals(_previewSourceUrl, url, StringComparison.Ordinal) &&
            string.Equals(_previewSourceFormatString, formatString, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(_previewSourceVideoPath) &&
            File.Exists(_previewSourceVideoPath))
        {
            if (reportProgress)
            {
                SetPreviewSourceProgress(false, null, "プレビュー用動画キャッシュを再利用しています...", null, null);
            }

            return _previewSourceVideoPath;
        }

        if (reportProgress)
        {
            SetPreviewSourceProgress(true, null, "軽量プレビュー動画をダウンロードしています...", null, null);
        }

        var progress = reportProgress
            ? new Progress<DownloadProgress>(p =>
            {
                var message = p.State switch
                {
                    DownloadState.Preparing => "軽量プレビュー動画を準備しています...",
                    DownloadState.Downloading => "軽量プレビュー動画をダウンロードしています...",
                    DownloadState.PostProcessing => "軽量プレビュー動画を処理しています...",
                    _ => "軽量プレビュー動画を準備しています..."
                };

                SetPreviewSourceProgress(true, p.Percent, message, p.Eta, p.SpeedBytesPerSec);
            })
            : null;

        var result = await _previewSourceService.EnsureSourceAsync(
            url,
            formatString,
            progress,
            ct);

        _previewSourceUrl = url;
        _previewSourceVideoPath = result.Path;
        _previewSourceFormatString = formatString;

        if (reportProgress)
        {
            var message = result.FromCache
                ? "プレビュー用動画キャッシュを再利用しています..."
                : "軽量プレビュー動画の準備が完了しました。";
            SetPreviewSourceProgress(false, result.FromCache ? null : 100, message, null, null);
        }

        return result.Path;
    }

    private void SetPreviewSourceProgress(bool isDownloading, double? percent, string message, TimeSpan? eta, double? speedBytesPerSec)
    {
        void Apply()
        {
            IsPreviewSourceDownloading = isDownloading;
            PreviewSourceProgressPercent = percent;
            PreviewSourceProgressText = message;
            PreviewSourceEtaText = eta is { } e ? $"推定残り: {FormatEta(e)}" : "";
            PreviewSourceSpeedText = speedBytesPerSec is > 0
                ? $"現在速度: {FormatSpeedWithUnit(speedBytesPerSec.Value)}"
                : "";
        }

        if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(_ => Apply(), null);
            return;
        }

        Apply();
    }

    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalHours >= 1)
        {
            return $"{(int)eta.TotalHours:D2}:{eta.Minutes:D2}:{eta.Seconds:D2}";
        }

        return $"{eta.Minutes:D2}:{eta.Seconds:D2}";
    }

    private static string FormatSpeedWithUnit(double bytesPerSec)
    {
        var kbps = bytesPerSec / 1024.0;
        if (kbps < 1024.0)
        {
            return $"{kbps:F1} KB/s";
        }

        var mbps = kbps / 1024.0;
        if (mbps < 1024.0)
        {
            return mbps >= 100.0 ? $"{mbps:F0} MB/s" : $"{mbps:F2} MB/s";
        }

        var gbps = mbps / 1024.0;
        return gbps >= 100.0 ? $"{gbps:F0} GB/s" : $"{gbps:F2} GB/s";
    }

    private string BuildPreviewFormatString() =>
        (SelectedPreviewQualityOption?.Key ?? PreviewQualityBalanced) switch
        {
            PreviewQualityFast => BuildPreviewFormatForHeight(360),
            PreviewQualityVisual => BuildPreviewFormatForHeight(720),
            _ => BuildPreviewFormatForHeight(480),
        };

    private static string BuildPreviewFormatForHeight(int height) =>
        $"bestvideo[height<={height}][vcodec^=avc1]+bestaudio[ext=m4a]/" +
        $"bestvideo[height<={height}]+bestaudio/best[height<={height}]/" +
        "bestvideo+bestaudio/best";

    private static double ClampFrameOffset(double offsetSeconds, double? durationSeconds)
    {
        var safeOffset = Math.Max(0.0, offsetSeconds);
        if (durationSeconds is null || durationSeconds <= 0)
        {
            return safeOffset;
        }

        return Math.Min(safeOffset, Math.Max(0.0, durationSeconds.Value - 0.1));
    }

    private static string FormatClock(double seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"00:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    [RelayCommand]
    private void ToggleSettings()
    {
        IsSettingsOpen = !IsSettingsOpen;
        SettingsMessage = "";
    }

    [RelayCommand(CanExecute = nameof(CanSaveSettings), IncludeCancelCommand = true)]
    private async Task SaveSettingsAsync(CancellationToken ct)
    {
        try
        {
            var mode = DefaultFormatMode ?? FormatModes[0];
            var previewQuality = SelectedPreviewQualityOption?.Key ?? PreviewQualityBalanced;
            _settings = new AppSettings
            {
                DefaultFormatModeKey = mode.Key,
                DefaultNormalizeAudio = DefaultNormalizeAudio,
                DefaultUpscaleToFhd = DefaultUpscaleToFhd,
                DefaultTargetLoudnessLufs = DefaultTargetLoudnessLufs,
                DefaultTargetTruePeakDb = DefaultTargetTruePeakDb,
                DefaultTargetLoudnessRange = DefaultTargetLoudnessRange,
                DefaultOutputDirectory = DefaultOutputDirectory.Trim(),
                DefaultOutputTemplate = DefaultOutputTemplate.Trim(),
                MaxConcurrentDownloads = Math.Max(1, DefaultMaxConcurrentDownloads),
                DefaultPreviewVolumePercent = PreviewPlaybackVolumePercent,
                PreviewQualityModeKey = previewQuality,
            };

            await _settingsStore.SaveAsync(_settings, ct);

            SelectedFormatMode = mode;
            _queue.MaxConcurrentDownloads = _settings.MaxConcurrentDownloads;
            SettingsMessage = "設定を保存しました";
            UpdateQueueSummary();
            ShowToast("設定を保存しました。", false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "設定の保存に失敗");
            SettingsMessage = $"設定の保存に失敗しました: {ex.Message}";
            ShowToast("設定の保存に失敗しました。", true);
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task OpenPlaylistEntryDetailAsync(PlaylistEntrySelectionViewModel? entry, CancellationToken ct)
    {
        if (entry is null)
        {
            return;
        }

        EditingPlaylistEntry = entry;
        IsLoadingPlaylistEntryDetail = true;
        PlaylistDetailMessage = "フォーマット候補を取得しています...";
        PlaylistDetailVideoFormats.Clear();
        PlaylistDetailAudioFormats.Clear();
        SelectedPlaylistDetailVideoFormatOption = null;
        SelectedPlaylistDetailAudioFormatOption = null;
        UsePlaylistDetailNormalizeOverride = entry.UseNormalizeOverride;
        PlaylistDetailNormalizeAudioOverride = entry.NormalizeAudioOverride;

        try
        {
            var info = await ResolvePlaylistEntryInfoAsync(entry.Url, ct);
            UpdatePlaylistDetailFormatOptions(info.Formats, entry.SelectedVideoFormatId, entry.SelectedAudioFormatId);
            UsePlaylistDetailNormalizeOverride = entry.UseNormalizeOverride;
            PlaylistDetailNormalizeAudioOverride = entry.NormalizeAudioOverride;

            if (SelectedPlaylistDetailVideoFormatOption is null || SelectedPlaylistDetailAudioFormatOption is null)
            {
                PlaylistDetailMessage = "候補を取得できませんでした。別の項目を選択してください。";
            }
            else
            {
                PlaylistDetailMessage = "映像IDと音声IDを選択して適用してください。";
            }
        }
        catch (OperationCanceledException)
        {
            PlaylistDetailMessage = "候補取得をキャンセルしました。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "プレイリスト項目のフォーマット取得に失敗");
            PlaylistDetailMessage = $"候補取得に失敗しました: {ex.Message}";
        }
        finally
        {
            IsLoadingPlaylistEntryDetail = false;
        }
    }

    [RelayCommand]
    private void ClosePlaylistEntryDetail()
    {
        EditingPlaylistEntry = null;
        PlaylistDetailMessage = "";
        PlaylistDetailVideoFormats.Clear();
        PlaylistDetailAudioFormats.Clear();
        SelectedPlaylistDetailVideoFormatOption = null;
        SelectedPlaylistDetailAudioFormatOption = null;
        UsePlaylistDetailNormalizeOverride = false;
        PlaylistDetailNormalizeAudioOverride = false;
    }

    [RelayCommand(CanExecute = nameof(CanApplyPlaylistEntryDetail))]
    private void ApplyPlaylistEntryDetail()
    {
        if (EditingPlaylistEntry is null ||
            SelectedPlaylistDetailVideoFormatOption is null ||
            SelectedPlaylistDetailAudioFormatOption is null)
        {
            return;
        }

        EditingPlaylistEntry.SelectedFormatMode = FormatModes.First(mode => mode.Key == FormatModeSelect);
        EditingPlaylistEntry.SelectedVideoFormatId = SelectedPlaylistDetailVideoFormatOption.FormatId;
        EditingPlaylistEntry.SelectedAudioFormatId = SelectedPlaylistDetailAudioFormatOption.FormatId;
        EditingPlaylistEntry.UseNormalizeOverride = UsePlaylistDetailNormalizeOverride;
        EditingPlaylistEntry.NormalizeAudioOverride = PlaylistDetailNormalizeAudioOverride;
        PlaylistDetailMessage = "この項目に一覧選択設定を適用しました。";
    }

    [RelayCommand(CanExecute = nameof(CanApplyPlaylistEntryDetailToSimilar))]
    private void ApplyPlaylistEntryDetailToSimilar()
    {
        if (EditingPlaylistEntry is null ||
            SelectedPlaylistDetailVideoFormatOption is null ||
            SelectedPlaylistDetailAudioFormatOption is null)
        {
            return;
        }

        var targetModeKey = EditingPlaylistEntry.SelectedFormatMode.Key;
        var targets = PlaylistEntries
            .Where(entry => entry.SelectedFormatMode.Key == targetModeKey)
            .ToArray();

        foreach (var entry in targets)
        {
            entry.SelectedFormatMode = FormatModes.First(mode => mode.Key == FormatModeSelect);
            entry.SelectedVideoFormatId = SelectedPlaylistDetailVideoFormatOption.FormatId;
            entry.SelectedAudioFormatId = SelectedPlaylistDetailAudioFormatOption.FormatId;
            entry.UseNormalizeOverride = UsePlaylistDetailNormalizeOverride;
            entry.NormalizeAudioOverride = PlaylistDetailNormalizeAudioOverride;
        }

        PlaylistDetailMessage = $"{targets.Length} 件へ一括適用しました。";
        ShowToast($"{targets.Length}件へ一括適用しました。", false);
    }

    [RelayCommand]
    private async Task SetOutputDirectoryAsync(string? directory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var path = directory.Trim();
        if (!Directory.Exists(path))
        {
            DefaultOutputDirectory = ResolvePersistedOutputDirectory();
            SettingsMessage = "指定した保存先が存在しません。既存のフォルダを指定してください。";
            ShowToast("指定した保存先が存在しません。", true);
            return;
        }

        var previousDirectory = ResolvePersistedOutputDirectory();
        try
        {
            DefaultOutputDirectory = path;
            var updatedSettings = _settings with { DefaultOutputDirectory = path };
            await _settingsStore.SaveAsync(updatedSettings, ct);
            _settings = updatedSettings;
            SettingsMessage = "保存先を保存しました。次回起動時にも使用します。";
        }
        catch (OperationCanceledException)
        {
            DefaultOutputDirectory = previousDirectory;
            SettingsMessage = "保存先の保存をキャンセルしました。";
        }
        catch (Exception ex)
        {
            DefaultOutputDirectory = previousDirectory;
            _logger.LogError(ex, "保存先設定の保存に失敗");
            SettingsMessage = $"保存先の保存に失敗しました: {ex.Message}";
            ShowToast("保存先の保存に失敗しました。", true);
        }
    }

    [RelayCommand]
    private async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            IsSettingUp = true;
            SetupMessage = "外部ツールを確認しています...";
            var statuses = await RefreshStatusAsync(ct);
            var missing = statuses.Where(status => !status.IsInstalled).ToArray();
            if (missing.Length == 0)
            {
                await CompleteInitializationAsync(ct);
                return;
            }

            MissingDependenciesText = string.Join("、", missing.Select(status =>
                status.Kind == ToolKind.Ytdlp ? "yt-dlp" : "FFmpeg / FFprobe"));
            DependencyConsentMessage = "";
            HasAcceptedDependencyTerms = false;
            IsDependencyConsentRequired = true;
            SetupMessage = "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ツールのセットアップに失敗");
            SetupMessage = $"セットアップに失敗しました: {ex.Message}";
        }
        finally
        {
            IsSettingUp = false;
        }
    }

    private bool CanAcceptDependencyDownload() =>
        IsDependencyConsentRequired && HasAcceptedDependencyTerms && !IsSettingUp;

    [RelayCommand(CanExecute = nameof(CanAcceptDependencyDownload), IncludeCancelCommand = true)]
    private async Task AcceptDependencyDownloadAsync(CancellationToken ct)
    {
        try
        {
            IsDependencyConsentRequired = false;
            IsSettingUp = true;
            SetupMessage = "依存ツールをダウンロードしています...";
            SetupProgress = 0;

            var progress = new Progress<ToolProgress>(p =>
            {
                var toolName = p.Kind == ToolKind.Ytdlp ? "yt-dlp" : "FFmpeg";
                SetupMessage = $"{toolName}: {p.Phase}" + (p.Percent is { } pc ? $" ({pc:F0}%)" : "");
                SetupProgress = p.Percent ?? 0;
            });

            await _toolManager.DownloadMissingToolsAsync(
                ToolDownloadConsent.GrantedNow(), progress, ct);
            var statuses = await RefreshStatusAsync(ct);
            if (statuses.Any(status => !status.IsInstalled))
            {
                throw new InvalidOperationException("必要な依存ツールをすべて導入できませんでした。");
            }

            await CompleteInitializationAsync(ct);
        }
        catch (OperationCanceledException)
        {
            DependencyConsentMessage = "ダウンロードを中止しました。";
            IsDependencyConsentRequired = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "依存ツールのダウンロードに失敗");
            DependencyConsentMessage = $"ダウンロードに失敗しました: {ex.Message}";
            IsDependencyConsentRequired = true;
        }
        finally
        {
            IsSettingUp = false;
        }
    }

    [RelayCommand]
    private void DeclineDependencyDownload()
    {
        HasAcceptedDependencyTerms = false;
        DependencyConsentMessage =
            "同意されなかったためダウンロード機能は利用できません。後から同意して続行できます。";
    }

    [RelayCommand]
    private async Task UpdateYtdlpAsync(CancellationToken ct)
    {
        try
        {
            IsSettingUp = true;
            var progress = new Progress<ToolProgress>(p =>
                SetupMessage = $"yt-dlp: {p.Phase}" + (p.Percent is { } pc ? $" ({pc:F0}%)" : ""));

            var updated = await _toolManager.UpdateYtdlpAsync(progress, ct);
            SetupMessage = updated ? "yt-dlp を更新しました" : "yt-dlp は最新です";
            await RefreshStatusAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "yt-dlp の更新に失敗");
            SetupMessage = $"更新に失敗しました: {ex.Message}";
        }
        finally
        {
            IsSettingUp = false;
        }
    }

    private void ApplyAnalyzedDuration(MediaInfo info)
    {
        if (info.Duration is not { } duration || duration <= TimeSpan.Zero)
        {
            return;
        }

        EndTimeText = FormatDurationToHHMMSS(duration);
        UpdatePreviewSegmentBounds();
        SelectionTimeMessage = "解析が完了しました。プレビューを自動準備しています。正規化前後表示は必要に応じて反映ボタンを押してください。";
        PreviewStatusOverlayText = "読み込み後に停止表示します";
    }

    private void UpdatePreviewSegmentBounds(int? startSeconds = null, int? endSeconds = null)
    {
        if (startSeconds is null || endSeconds is null)
        {
            if (!TryParseTimeValue(StartTimeText, out startSeconds) ||
                !TryParseTimeValue(EndTimeText, out endSeconds, allowEmpty: true))
            {
                return;
            }
        }

        var rawStartSeconds = startSeconds ?? 0;
        var rawEndSeconds = endSeconds ?? rawStartSeconds;
        PreviewSegmentStartSeconds = ClampFrameOffset(rawStartSeconds, PreviewDurationSeconds > 0 ? PreviewDurationSeconds : null);
        PreviewSegmentEndSeconds = ClampFrameOffset(rawEndSeconds, PreviewDurationSeconds > 0 ? PreviewDurationSeconds : null);
    }

    private void MarkPreviewRangeChanged()
    {
        IsAutoPreviewPending = false;

        if (HasPreviewHistogram)
        {
            PreviewHistogramMessage = "開始/終了時刻が変更されました。正規化前後の表示を更新するには反映ボタンを押してください。";
            IsPreviewNormalizationStale = true;
        }

        if (!IsGeneratingSnapshot)
        {
            SelectionTimeMessage = "開始/終了時刻を変更しました。正規化前後表示は反映ボタンを押したときだけ更新します。";
        }
    }

    private static string FormatDurationToHHMMSS(TimeSpan duration)
    {
        var totalSeconds = Math.Min((int)Math.Ceiling(duration.TotalSeconds), 99 * 3600 + 59 * 60 + 59);
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;
        return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
    }

    private void QueueAutoPreviewIfPossible(string? startText, string? endText, bool force = false)
    {
        if (!IsAutoPreviewEnabled || string.IsNullOrWhiteSpace(UrlInput) || IsGeneratingSnapshot)
        {
            return;
        }

        if (!TryParseTimeValue(startText, out var parsedStart))
        {
            return;
        }

        if (!TryParseTimeValue(endText, out var parsedEnd, allowEmpty: true))
        {
            return;
        }

        if (parsedStart is not null && parsedEnd is not null && parsedStart.Value > parsedEnd.Value)
        {
            return;
        }

        if (!force && IsAutoPreviewPending)
        {
            return;
        }

        IsAutoPreviewPending = true;

        _ = Task.Run(async () =>
        {
            await Task.Delay(700);
            if (_uiContext is not null)
            {
                _uiContext.Post(_ =>
                {
                    if (!IsAutoPreviewEnabled || string.IsNullOrWhiteSpace(UrlInput) || IsGeneratingSnapshot)
                    {
                        IsAutoPreviewPending = false;
                        return;
                    }

                    _ = GenerateSnapshotAsync(CancellationToken.None);
                    IsAutoPreviewPending = false;
                }, null);
            }
            else
            {
                _ = GenerateSnapshotAsync(CancellationToken.None);
            }
        });
    }

    private async Task<MediaInfo> ResolveMediaInfoAsync(string url, CancellationToken ct)
    {
        if (string.Equals(_analyzedUrl, url, StringComparison.Ordinal) && _analyzedMediaInfo is not null)
        {
            return _analyzedMediaInfo;
        }

        var info = await _ytdlp.FetchMediaInfoAsync(url, ct);
        _analyzedUrl = url;
        _analyzedMediaInfo = info;
        return info;
    }

    private async Task<MediaInfo> ResolvePlaylistEntryInfoAsync(string url, CancellationToken ct)
    {
        if (_playlistInfoCache.TryGetValue(url, out var cached))
        {
            return cached;
        }

        var info = await _ytdlp.FetchMediaInfoAsync(url, ct);
        if (info.IsPlaylist)
        {
            throw new InvalidOperationException("プレイリスト項目の詳細が取得できませんでした。");
        }

        _playlistInfoCache[url] = info;
        return info;
    }

    partial void OnSnapshotImagePathChanged(string? value)
    {
        SnapshotPreviewPath = string.IsNullOrWhiteSpace(value) || !File.Exists(value) ? null : value;
    }

    private DownloadOptions BuildDownloadOptions(
        string url,
        FormatModeOption? mode,
        string? selectedVideoFormatId,
        string? selectedAudioFormatId,
        bool? normalizeOverride,
        bool upscaleToFhd,
        int? startSeconds,
        int? endSeconds)
    {
        var key = mode?.Key ?? FormatModeAvcAac;
        var normalize = normalizeOverride ?? DefaultNormalizeAudio;
        var outputDirectory = ResolveOutputDirectory();
        var outputTemplate = ResolveOutputTemplate();
        var (effectiveStart, effectiveEnd) = NormalizeDownloadSectionRange(startSeconds, endSeconds);

        return key switch
        {
            FormatModeBest => new DownloadOptions
            {
                Url = url,
                OutputDirectory = outputDirectory,
                OutputTemplate = outputTemplate,
                FormatSort = null,
                RemuxTo = null,
                MergeOutputFormat = null,
                StartTimeSeconds = effectiveStart,
                EndTimeSeconds = effectiveEnd,
                NormalizeAudio = normalize,
                UpscaleToFhd = upscaleToFhd,
                TargetLoudnessLufs = DefaultTargetLoudnessLufs,
                TargetTruePeakDb = DefaultTargetTruePeakDb,
                TargetLoudnessRange = DefaultTargetLoudnessRange,
                NormalizeStartTimeSeconds = startSeconds,
                NormalizeEndTimeSeconds = endSeconds,
            },
            FormatModeAudioOnly => new DownloadOptions
            {
                Url = url,
                OutputDirectory = outputDirectory,
                OutputTemplate = outputTemplate,
                AudioOnly = true,
                AudioFormat = "m4a",
                FormatSort = null,
                RemuxTo = null,
                MergeOutputFormat = null,
                StartTimeSeconds = effectiveStart,
                EndTimeSeconds = effectiveEnd,
                NormalizeAudio = normalize,
                UpscaleToFhd = false,
                TargetLoudnessLufs = DefaultTargetLoudnessLufs,
                TargetTruePeakDb = DefaultTargetTruePeakDb,
                TargetLoudnessRange = DefaultTargetLoudnessRange,
                NormalizeStartTimeSeconds = startSeconds,
                NormalizeEndTimeSeconds = endSeconds,
            },
            FormatModeSelect => new DownloadOptions
            {
                Url = url,
                OutputDirectory = outputDirectory,
                OutputTemplate = outputTemplate,
                FormatString = BuildSelectedFormatString(selectedVideoFormatId, selectedAudioFormatId),
                FormatSort = null,
                RemuxTo = null,
                MergeOutputFormat = null,
                StartTimeSeconds = effectiveStart,
                EndTimeSeconds = effectiveEnd,
                NormalizeAudio = normalize,
                UpscaleToFhd = upscaleToFhd,
                TargetLoudnessLufs = DefaultTargetLoudnessLufs,
                TargetTruePeakDb = DefaultTargetTruePeakDb,
                TargetLoudnessRange = DefaultTargetLoudnessRange,
                NormalizeStartTimeSeconds = startSeconds,
                NormalizeEndTimeSeconds = endSeconds,
            },
            _ => new DownloadOptions
            {
                Url = url,
                OutputDirectory = outputDirectory,
                OutputTemplate = outputTemplate,
                StartTimeSeconds = effectiveStart,
                EndTimeSeconds = effectiveEnd,
                NormalizeAudio = normalize,
                UpscaleToFhd = upscaleToFhd,
                TargetLoudnessLufs = DefaultTargetLoudnessLufs,
                TargetTruePeakDb = DefaultTargetTruePeakDb,
                TargetLoudnessRange = DefaultTargetLoudnessRange,
                NormalizeStartTimeSeconds = startSeconds,
                NormalizeEndTimeSeconds = endSeconds,
            },
        };
    }

    private static (int? Start, int? End) NormalizeDownloadSectionRange(int? startSeconds, int? endSeconds)
    {
        // 0秒開始かつ終了未指定は全体ダウンロードとみなし、区間指定を付けない。
        if ((startSeconds ?? 0) <= 0 && endSeconds is null)
        {
            return (null, null);
        }

        return (startSeconds, endSeconds);
    }

    private bool TryResolveClipRange(out int? startSeconds, out int? endSeconds, out string error)
    {
        startSeconds = null;
        endSeconds = null;
        error = "";

        if (!TryParseTimeValue(StartTimeText, out var parsedStart))
        {
            error = "開始時刻は HH:MM:SS 形式で入力してください。";
            return false;
        }

        if (!TryParseTimeValue(EndTimeText, out var parsedEnd, allowEmpty: true))
        {
            error = "終了時刻は HH:MM:SS 形式で入力してください。";
            return false;
        }

        if (parsedStart is not null && parsedEnd is not null && parsedStart.Value > parsedEnd.Value)
        {
            error = "開始時刻は終了時刻より前にしてください。";
            return false;
        }

        startSeconds = parsedStart;
        endSeconds = parsedEnd;
        SelectionTimeMessage = "開始・終了は HH:MM:SS 形式で指定できます。";
        return true;
    }

    private static string NormalizeTimeInput(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var digits = new string(raw.Where(char.IsDigit).Take(6).ToArray());
        return digits.Length switch
        {
            <= 2 => digits,
            <= 4 => $"{digits[..2]}:{digits[2..]}",
            _ => $"{digits[..2]}:{digits[2..4]}:{digits[4..]}"
        };
    }

    private static bool TryParseTimeValue(string? value, out int? seconds, bool allowEmpty = false)
    {
        seconds = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            if (allowEmpty)
            {
                return true;
            }

            return false;
        }

        var trimmed = value.Trim();
        if (!Regex.IsMatch(trimmed, "^\\d{2}:\\d{2}:\\d{2}$"))
        {
            return false;
        }

        if (!int.TryParse(trimmed[..2], out var hours) ||
            !int.TryParse(trimmed[3..5], out var minutes) ||
            !int.TryParse(trimmed[6..8], out var secondsPart))
        {
            return false;
        }

        if (hours > 99 || minutes > 59 || secondsPart > 59)
        {
            return false;
        }

        seconds = hours * 3600 + minutes * 60 + secondsPart;
        return true;
    }

    private static string BuildSelectedFormatString(string? selectedVideoFormatId, string? selectedAudioFormatId)
    {
        if (string.IsNullOrWhiteSpace(selectedVideoFormatId) || string.IsNullOrWhiteSpace(selectedAudioFormatId))
        {
            throw new InvalidOperationException("映像と音声のフォーマットIDを選択してください。");
        }

        return $"{selectedVideoFormatId}+{selectedAudioFormatId}";
    }

    private void UpdateFormatOptions(IReadOnlyList<FormatInfo> formats)
    {
        var currentVideo = SelectedVideoFormatOption?.FormatId;
        var currentAudio = SelectedAudioFormatOption?.FormatId;

        AvailableVideoFormats.Clear();
        AvailableAudioFormats.Clear();

        FillFormatCollections(formats, AvailableVideoFormats, AvailableAudioFormats);

        HasAvailableVideoFormats = AvailableVideoFormats.Count > 0;
        HasAvailableAudioFormats = AvailableAudioFormats.Count > 0;

        SelectedVideoFormatOption = currentVideo is not null
            ? AvailableVideoFormats.FirstOrDefault(option => option.FormatId == currentVideo)
            : AvailableVideoFormats.FirstOrDefault();
        SelectedAudioFormatOption = currentAudio is not null
            ? AvailableAudioFormats.FirstOrDefault(option => option.FormatId == currentAudio)
            : AvailableAudioFormats.FirstOrDefault();
    }

    private void UpdatePlaylistDetailFormatOptions(
        IReadOnlyList<FormatInfo> formats,
        string? selectedVideoFormatId,
        string? selectedAudioFormatId)
    {
        PlaylistDetailVideoFormats.Clear();
        PlaylistDetailAudioFormats.Clear();

        FillFormatCollections(formats, PlaylistDetailVideoFormats, PlaylistDetailAudioFormats);

        SelectedPlaylistDetailVideoFormatOption = selectedVideoFormatId is not null
            ? PlaylistDetailVideoFormats.FirstOrDefault(option => option.FormatId == selectedVideoFormatId)
            : PlaylistDetailVideoFormats.FirstOrDefault();
        SelectedPlaylistDetailAudioFormatOption = selectedAudioFormatId is not null
            ? PlaylistDetailAudioFormats.FirstOrDefault(option => option.FormatId == selectedAudioFormatId)
            : PlaylistDetailAudioFormats.FirstOrDefault();
    }

    private static void FillFormatCollections(
        IReadOnlyList<FormatInfo> formats,
        ICollection<FormatChoiceOption> videoTarget,
        ICollection<FormatChoiceOption> audioTarget)
    {
        foreach (var video in formats
                     .Where(format => format.HasVideo)
                     .OrderByDescending(format => format.Height ?? 0)
                     .ThenByDescending(format => format.Fps ?? 0)
                     .ThenByDescending(format => format.Tbr ?? 0)
                     .Take(80))
        {
            videoTarget.Add(new FormatChoiceOption(video.FormatId, BuildFormatLabel(video)));
        }

        foreach (var audio in formats
                     .Where(format => format.HasAudio)
                     .OrderByDescending(format => format.Abr ?? format.Tbr ?? 0)
                     .ThenBy(format => format.FormatId)
                     .Take(80))
        {
            audioTarget.Add(new FormatChoiceOption(audio.FormatId, BuildFormatLabel(audio)));
        }
    }

    private void UpdatePlaylistEntries(MediaInfo info)
    {
        ClearPlaylistEntries();

        IsPlaylistDetected = true;
        PlaylistTitle = info.Title ?? "プレイリスト";

        var defaultMode = SelectedFormatMode?.Key == FormatModeSelect
            ? FormatModes[0]
            : (SelectedFormatMode ?? FormatModes[0]);

        foreach (var entry in info.Entries)
        {
            var url = entry.Url;
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                continue;
            }

            var vm = new PlaylistEntrySelectionViewModel(
                url,
                entry.Title ?? url,
                entry.Duration,
                true,
                FormatModes,
                defaultMode);

            vm.PropertyChanged += OnPlaylistEntryPropertyChanged;
            PlaylistEntries.Add(vm);
        }

        OnPropertyChanged(nameof(HasPlaylistEntries));
        UpdatePlaylistSelectionCount();
    }

    private void ClearPlaylistEntries()
    {
        foreach (var entry in PlaylistEntries)
        {
            entry.PropertyChanged -= OnPlaylistEntryPropertyChanged;
        }

        PlaylistEntries.Clear();
        IsPlaylistDetected = false;
        PlaylistTitle = "";
        SelectedPlaylistCount = 0;
        ClosePlaylistEntryDetail();
        _playlistInfoCache.Clear();

        OnPropertyChanged(nameof(HasPlaylistEntries));
    }

    private void OnPlaylistEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistEntrySelectionViewModel.IsSelected))
        {
            UpdatePlaylistSelectionCount();
            return;
        }

        if (e.PropertyName is nameof(PlaylistEntrySelectionViewModel.SelectedFormatMode) or
            nameof(PlaylistEntrySelectionViewModel.SelectedVideoFormatId) or
            nameof(PlaylistEntrySelectionViewModel.SelectedAudioFormatId))
        {
            AddSelectedPlaylistToQueueCommand.NotifyCanExecuteChanged();
        }
    }

    private void UpdatePlaylistSelectionCount()
    {
        SelectedPlaylistCount = PlaylistEntries.Count(entry => entry.IsSelected);
    }

    private static string BuildFormatLabel(FormatInfo format)
    {
        var resolution = format.Height is not null
            ? (format.Width is not null ? $"{format.Width}x{format.Height}" : $"{format.Height}p")
            : "audio";
        var codecs = $"v:{format.VCodec ?? "?"} / a:{format.ACodec ?? "?"}";
        var bitrate = format.Tbr ?? format.Abr;
        var bitrateText = bitrate is not null ? $"{bitrate.Value:F0}k" : "?k";
        var ext = format.Ext ?? "?";
        return $"{format.FormatId} | {ext} | {resolution} | {bitrateText} | {codecs}";
    }

    private async Task CompleteInitializationAsync(CancellationToken ct)
    {
        IsDependencyConsentRequired = false;
        SetupMessage = "";

        _settings = await _settingsStore.LoadAsync(ct);
        var configuredOutputDirectory = _settings.DefaultOutputDirectory?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredOutputDirectory) &&
            !Directory.Exists(configuredOutputDirectory))
        {
            var fallbackDirectory = EnsureFallbackOutputDirectory();
            _logger.LogWarning(
                "保存済みの保存先が存在しないため既定値へ戻します: {ConfiguredPath} -> {FallbackPath}",
                configuredOutputDirectory,
                fallbackDirectory);
            _settings = _settings with { DefaultOutputDirectory = fallbackDirectory };
            await _settingsStore.SaveAsync(_settings, ct);
            SettingsMessage = "保存済みの保存先が見つからないため、既定のDownloadsフォルダへ戻しました。";
        }
        ApplySettingsToViewModel(_settings);
        _queue.MaxConcurrentDownloads = _settings.MaxConcurrentDownloads;

        IsReady = true;
        await _queue.LoadAsync(ct);
        UpdateQueueSummary();
    }

    private void ApplySettingsToViewModel(AppSettings settings)
    {
        DefaultNormalizeAudio = settings.DefaultNormalizeAudio;
        NormalizeAudioForCurrent = settings.DefaultNormalizeAudio;
        DefaultUpscaleToFhd = settings.DefaultUpscaleToFhd;
        UpscaleToFhdForCurrent = settings.DefaultUpscaleToFhd;
        DefaultTargetLoudnessLufs = settings.DefaultTargetLoudnessLufs;
        DefaultTargetTruePeakDb = settings.DefaultTargetTruePeakDb;
        DefaultTargetLoudnessRange = settings.DefaultTargetLoudnessRange;
        SyncNormalizationPresetFromValues();
        DefaultMaxConcurrentDownloads = Math.Max(1, settings.MaxConcurrentDownloads);

        var mode = FormatModes.FirstOrDefault(option => option.Key == settings.DefaultFormatModeKey)
                   ?? FormatModes[0];
        DefaultFormatMode = mode;
        SelectedFormatMode = mode;

        DefaultOutputDirectory = string.IsNullOrWhiteSpace(settings.DefaultOutputDirectory)
            ? EnsureFallbackOutputDirectory()
            : settings.DefaultOutputDirectory;
        DefaultOutputTemplate = string.IsNullOrWhiteSpace(settings.DefaultOutputTemplate)
            ? "%(title)s [%(id)s].%(ext)s"
            : settings.DefaultOutputTemplate;
        PreviewPlaybackVolumePercent = settings.DefaultPreviewVolumePercent;
        IsPreviewMuted = PreviewPlaybackVolumePercent == 0;
        SelectedPreviewQualityOption = PreviewQualityOptions.FirstOrDefault(option => option.Key == settings.PreviewQualityModeKey)
            ?? PreviewQualityOptions.First(option => option.Key == PreviewQualityBalanced);
    }

    private async Task<IReadOnlyList<ToolStatus>> RefreshStatusAsync(CancellationToken ct)
    {
        var statuses = await _toolManager.GetStatusAsync(ct);
        foreach (var s in statuses)
        {
            var text = s.IsInstalled ? (s.Version ?? "導入済み") : "未導入";
            if (s.Kind == ToolKind.Ytdlp)
            {
                YtdlpStatus = text;
            }
            else
            {
                FfmpegStatus = text;
            }
        }
        return statuses;
    }

    private void OnQueueItemsChanged(object? sender, IReadOnlyList<DownloadQueueItem> items)
    {
        if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(_ => ApplyQueueSnapshot(items), null);
            return;
        }

        ApplyQueueSnapshot(items);
    }

    private void ApplyQueueSnapshot(IReadOnlyList<DownloadQueueItem> items)
    {
        var byId = QueueItems.ToDictionary(item => item.Id);
        foreach (var item in items)
        {
            if (byId.Remove(item.Id, out var existing))
            {
                existing.Update(item);
            }
            else
            {
                QueueItems.Add(new DownloadQueueItemViewModel(_queue, item));
            }
        }

        foreach (var removed in byId.Values)
        {
            QueueItems.Remove(removed);
        }

        HasQueueItems = QueueItems.Count > 0;
        RebuildQueueBuckets();
        UpdateQueueSummary();
    }

    private void RebuildQueueBuckets()
    {
        var active = QueueItems
            .Where(item => item.Snapshot.Status != DownloadQueueStatus.Completed)
            .ToArray();
        var completed = QueueItems
            .Where(item => item.Snapshot.Status == DownloadQueueStatus.Completed)
            .OrderByDescending(item => item.Snapshot.CompletedAt ?? DateTimeOffset.MinValue)
            .ToArray();

        ReplaceQueueItems(ActiveQueueItems, active);
        ReplaceQueueItems(CompletedQueueItems, completed);

        HasActiveQueueItems = ActiveQueueItems.Count > 0;
        HasCompletedQueueItems = CompletedQueueItems.Count > 0;
    }

    private static void ReplaceQueueItems(
        ObservableCollection<DownloadQueueItemViewModel> target,
        IReadOnlyList<DownloadQueueItemViewModel> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private void UpdateQueueSummary()
    {
        var items = QueueItems.Select(vm => vm.Snapshot).ToArray();
        var active = items.Count(item => item.Status is
            DownloadQueueStatus.Running or DownloadQueueStatus.PostProcessing or DownloadQueueStatus.Normalizing or DownloadQueueStatus.Upscaling);
        var waiting = items.Count(item => item.Status == DownloadQueueStatus.Queued);

        QueueSummary = items.Length == 0
            ? "キューは空です"
            : $"実行中 {active}/{_queue.MaxConcurrentDownloads}  待機 {waiting}  合計 {items.Length}";
    }

    private void ShowToast(string message, bool isError)
    {
        ToastMessage = isError ? $"エラー: {message}" : message;
        IsToastVisible = true;
        IsTransientBusy = true;
        OnPropertyChanged(nameof(IsContextBusy));

        var current = Interlocked.Increment(ref _toastVersion);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2600).ConfigureAwait(false);
            }
            finally
            {
                if (current == _toastVersion)
                {
                    if (_uiContext is not null)
                    {
                        _uiContext.Post(_ =>
                        {
                            IsToastVisible = false;
                            IsTransientBusy = false;
                            OnPropertyChanged(nameof(IsContextBusy));
                        }, null);
                    }
                    else
                    {
                        IsToastVisible = false;
                        IsTransientBusy = false;
                        OnPropertyChanged(nameof(IsContextBusy));
                    }
                }
            }
        });
    }

    public void RefreshQueueLiveIndicators()
    {
        foreach (var item in ActiveQueueItems)
        {
            item.RefreshLiveIndicators();
        }
    }

    private string ResolveOutputDirectory()
    {
        var dir = string.IsNullOrWhiteSpace(DefaultOutputDirectory)
            ? FallbackOutputDirectory
            : DefaultOutputDirectory.Trim();
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string ResolveOutputTemplate() =>
        string.IsNullOrWhiteSpace(DefaultOutputTemplate)
            ? "%(title)s [%(id)s].%(ext)s"
            : DefaultOutputTemplate.Trim();

    private static bool IsNormalizationRangeValid(double value, double min, double max) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value >= min && value <= max;

    private void SyncNormalizationPresetFromValues()
    {
        if (_isApplyingNormalizationPreset)
        {
            return;
        }

        var matched = NormalizationPresets.FirstOrDefault(option =>
            option.TargetLufs is not null &&
            option.TargetTruePeakDb is not null &&
            option.TargetLra is not null &&
            Math.Abs(option.TargetLufs.Value - DefaultTargetLoudnessLufs) < 0.001 &&
            Math.Abs(option.TargetTruePeakDb.Value - DefaultTargetTruePeakDb) < 0.001 &&
            Math.Abs(option.TargetLra.Value - DefaultTargetLoudnessRange) < 0.001);

        _isApplyingNormalizationPreset = true;
        try
        {
            SelectedNormalizationPreset = matched ??
                NormalizationPresets.First(option => option.Key == "custom");
        }
        finally
        {
            _isApplyingNormalizationPreset = false;
        }
    }

    private static string FallbackOutputDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private static string EnsureFallbackOutputDirectory()
    {
        var directory = FallbackOutputDirectory;
        Directory.CreateDirectory(directory);
        return directory;
    }

    private string ResolvePersistedOutputDirectory()
    {
        var persisted = _settings.DefaultOutputDirectory?.Trim();
        return !string.IsNullOrWhiteSpace(persisted) && Directory.Exists(persisted)
            ? persisted
            : EnsureFallbackOutputDirectory();
    }
}

public sealed record FormatModeOption(string Key, string Label);

public sealed record FormatChoiceOption(string FormatId, string DisplayText);

public sealed record NormalizationPresetOption(
    string Key,
    string Label,
    double? TargetLufs,
    double? TargetTruePeakDb,
    double? TargetLra);

public sealed record PreviewQualityOption(string Key, string Label, string Description);
