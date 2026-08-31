using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Torifune.Desktop.Diagnostics;

public sealed class DebugConsoleLogStore : INotifyPropertyChanged
{
    private const int MaxLines = 3000;
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s,]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WindowsUserPathPattern = new(
        @"(?<prefix>[A-Za-z]:\\Users\\)[^\\\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex UnixUserPathPattern = new(
        @"(?<prefix>/(?:home|Users)/)[^/\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly StringBuilder _textBuffer = new();

    private string _allText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> Lines { get; } = [];

    public string AllText
    {
        get => _allText;
        private set
        {
            if (_allText == value)
            {
                return;
            }

            _allText = value;
            OnPropertyChanged();
        }
    }

    public DateTimeOffset? LastHeartbeatAt { get; private set; }

    public DateTimeOffset? LastAppendedAt { get; private set; }

    public event EventHandler? LineAdded;

    public void Append(LogLevel level, string category, string message, Exception? exception = null)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] [{category}] {Sanitize(message)}";

        var now = DateTimeOffset.Now;

        Dispatcher.UIThread.Post(() =>
        {
            Lines.Add(line);
            _textBuffer.AppendLine(line);

            if (exception is not null)
            {
                var exLine = $"    EX: {Sanitize(exception.Message)}";
                Lines.Add(exLine);
                _textBuffer.AppendLine(exLine);
            }

            var truncated = false;
            while (Lines.Count > MaxLines)
            {
                Lines.RemoveAt(0);
                truncated = true;
            }

            if (truncated)
            {
                // 行数超過時はバッファを再構築して整合性を保つ。
                _textBuffer.Clear();
                foreach (var existing in Lines)
                {
                    _textBuffer.AppendLine(existing);
                }
            }

            LastAppendedAt = now;
            if (message.Contains("yt-dlp heartbeat:", StringComparison.OrdinalIgnoreCase))
            {
                LastHeartbeatAt = now;
            }

            OnPropertyChanged(nameof(LastAppendedAt));
            OnPropertyChanged(nameof(LastHeartbeatAt));
            AllText = _textBuffer.ToString();

            LineAdded?.Invoke(this, EventArgs.Empty);
        });
    }

    internal static string Sanitize(string value)
    {
        var withoutUrlQuery = UrlPattern.Replace(value, match =>
        {
            var url = match.Value;
            var queryIndex = url.IndexOfAny(['?', '#']);
            return queryIndex >= 0 ? url[..queryIndex] : url;
        });
        var withoutWindowsUser = WindowsUserPathPattern.Replace(
            withoutUrlQuery,
            "${prefix}<user>");
        return UnixUserPathPattern.Replace(withoutWindowsUser, "${prefix}<user>");
    }

    public void Clear()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Lines.Clear();
            _textBuffer.Clear();
            AllText = string.Empty;
            LastHeartbeatAt = null;
            LastAppendedAt = null;
            OnPropertyChanged(nameof(LastHeartbeatAt));
            OnPropertyChanged(nameof(LastAppendedAt));
        });
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
