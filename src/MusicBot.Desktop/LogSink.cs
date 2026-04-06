using Serilog.Core;
using Serilog.Events;

namespace MusicBot.Desktop;

/// <summary>
/// Serilog sink that forwards log entries to the WPF log viewer window via a static event.
/// Thread-safe; fires on whatever thread Serilog calls Emit from.
/// </summary>
public sealed class LogSink : ILogEventSink
{
    public static event Action<LogEntry>? OnEntry;

    public void Emit(LogEvent logEvent)
    {
        OnEntry?.Invoke(new LogEntry(
            logEvent.Level,
            logEvent.RenderMessage(),
            logEvent.Timestamp.LocalDateTime,
            logEvent.Exception));
    }
}

public sealed record LogEntry(
    LogEventLevel Level,
    string        Message,
    DateTime      Timestamp,
    Exception?    Exception = null);
