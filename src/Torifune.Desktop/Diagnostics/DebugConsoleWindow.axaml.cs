using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Torifune.Desktop.Diagnostics;

public partial class DebugConsoleWindow : Window
{
    private readonly DebugConsoleLogStore _store;
    private readonly DispatcherTimer _statusTimer;

    public DebugConsoleWindow()
        : this(ResolveLogStore())
    {
    }

    public DebugConsoleWindow(DebugConsoleLogStore store)
    {
        InitializeComponent();
        _store = store;
        DataContext = store;
        _store.LineAdded += OnLineAdded;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RefreshStatusText();
        _statusTimer.Start();

        RefreshStatusText();

        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _store.LineAdded -= OnLineAdded;
        };
    }

    private void Clear_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _store.Clear();
        RefreshStatusText();
    }

    private void SelectAll_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        LogsTextBox.Focus();
        LogsTextBox.SelectAll();
    }

    private async void CopyAll_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var text = _store.AllText;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        await clipboard.SetTextAsync(text);
    }

    private void OnLineAdded(object? sender, EventArgs e)
    {
        if (LogsTextBox.Text?.Length > 0)
        {
            LogsTextBox.CaretIndex = LogsTextBox.Text.Length;
        }

        RefreshStatusText();
    }

    private void RefreshStatusText()
    {
        HeartbeatStatusTextBlock.Text = $"heartbeat: {FormatElapsed(_store.LastHeartbeatAt)}";
        LastUpdateStatusTextBlock.Text = $"最終更新: {FormatElapsed(_store.LastAppendedAt)}";
    }

    private static string FormatElapsed(DateTimeOffset? timestamp)
    {
        if (timestamp is null)
        {
            return "未受信";
        }

        var elapsed = DateTimeOffset.Now - timestamp.Value;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalSeconds < 1)
        {
            return "今";
        }

        if (elapsed.TotalMinutes < 1)
        {
            return $"{(int)elapsed.TotalSeconds} 秒前";
        }

        if (elapsed.TotalHours < 1)
        {
            return $"{(int)elapsed.TotalMinutes} 分前";
        }

        return $"{(int)elapsed.TotalHours} 時間前";
    }

    private static DebugConsoleLogStore ResolveLogStore()
    {
        if (Avalonia.Application.Current is App app)
        {
            var fromServices = app.Services.GetService(typeof(DebugConsoleLogStore)) as DebugConsoleLogStore;
            if (fromServices is not null)
            {
                return fromServices;
            }
        }

        return new DebugConsoleLogStore();
    }
}
