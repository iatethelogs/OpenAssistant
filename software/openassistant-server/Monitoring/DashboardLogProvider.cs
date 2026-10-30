using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OpenAssistant.Server.Monitoring;

/// <summary>Журнал процесса доступен локальной панели. Системная консоль остаётся чистой.</summary>
public sealed class DashboardLogProvider : ILoggerProvider
{
    public sealed record Entry(DateTimeOffset Time, string Level, string Category, string Message);
    private readonly ConcurrentQueue<Entry> _entries = new();
    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
    public Entry[] Recent(int max = 300) => _entries.Reverse().Take(Math.Clamp(max, 1, 1000)).Reverse().ToArray();
    private void Add(LogLevel level, string category, string message)
    {
        _entries.Enqueue(new Entry(DateTimeOffset.UtcNow, level.ToString(), category, message));
        while (_entries.Count > 1000) _entries.TryDequeue(out _);
    }
    public void Dispose() { }
    private sealed class Sink(DashboardLogProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information && level != LogLevel.None;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            string message = formatter(state, exception);
            if (exception is not null) message += " | " + exception.Message;
            owner.Add(level, category, message.Length > 2000 ? message[..2000] : message);
        }
    }
}
