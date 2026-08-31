using System;
using Microsoft.Extensions.Logging;

namespace Torifune.Desktop.Diagnostics;

public sealed class DebugConsoleLoggerProvider : ILoggerProvider
{
    private readonly DebugConsoleLogStore _store;

    public DebugConsoleLoggerProvider(DebugConsoleLogStore store)
    {
        _store = store;
    }

    public ILogger CreateLogger(string categoryName) => new DebugConsoleLogger(_store, categoryName);

    public void Dispose()
    {
    }

    private sealed class DebugConsoleLogger : ILogger
    {
        private readonly DebugConsoleLogStore _store;
        private readonly string _category;

        public DebugConsoleLogger(DebugConsoleLogStore store, string category)
        {
            _store = store;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
            {
                return;
            }

            _store.Append(logLevel, _category, message, exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
