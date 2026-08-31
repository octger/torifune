using CommunityToolkit.Mvvm.ComponentModel;

namespace Torifune.ViewModels;

/// <summary>プレイリスト内エントリの選択状態を表す。</summary>
public partial class PlaylistEntrySelectionViewModel : ObservableObject
{
    public PlaylistEntrySelectionViewModel(
        string url,
        string title,
        TimeSpan? duration,
        bool isSelected,
        IReadOnlyList<FormatModeOption> formatModes,
        FormatModeOption selectedFormatMode)
    {
        Url = url;
        Title = title;
        Duration = duration;
        _isSelected = isSelected;
        FormatModes = formatModes;
        _selectedFormatMode = selectedFormatMode;
    }

    public string Url { get; }
    public string Title { get; }
    public TimeSpan? Duration { get; }
    public IReadOnlyList<FormatModeOption> FormatModes { get; }

    public string DurationText => Duration is { } d ? d.ToString(@"hh\:mm\:ss") : "--:--:--";

    public bool IsCustomFormatMode => SelectedFormatMode.Key == "select";

    public string FormatSummary
    {
        get
        {
            if (SelectedFormatMode.Key != "select")
            {
                var normalizePart = UseNormalizeOverride
                    ? (NormalizeAudioOverride ? "正規化: ON" : "正規化: OFF")
                    : "正規化: 既定";
                return $"{SelectedFormatMode.Label} / {normalizePart}";
            }

            var formatPart = string.IsNullOrWhiteSpace(SelectedVideoFormatId) || string.IsNullOrWhiteSpace(SelectedAudioFormatId)
                ? "一覧選択: 未設定"
                : $"一覧選択: v={SelectedVideoFormatId} + a={SelectedAudioFormatId}";
            var normalizeSuffix = UseNormalizeOverride
                ? (NormalizeAudioOverride ? " / 正規化: ON" : " / 正規化: OFF")
                : " / 正規化: 既定";
            return formatPart + normalizeSuffix;
        }
    }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomFormatMode))]
    [NotifyPropertyChangedFor(nameof(FormatSummary))]
    private FormatModeOption _selectedFormatMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatSummary))]
    private string? _selectedVideoFormatId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatSummary))]
    private string? _selectedAudioFormatId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatSummary))]
    private bool _useNormalizeOverride;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatSummary))]
    private bool _normalizeAudioOverride;
}
