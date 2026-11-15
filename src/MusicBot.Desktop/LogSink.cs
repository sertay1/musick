using Serilog.Core;
using Serilog.Events;

namespace MusicBot.Desktop;

/// <summary>
/// Serilog sink that forwards log entries to the WPF log viewer window via a static event.
/// Thread-safe; fires on whatever thread Serilog calls Emit from.
/// </summary>
public sealed class LogSink : ILogEventSink
{
    private static readonly object Sync = new();
    private static readonly Queue<LogEntry> Buffer = new();
    private static long _version;
    public static long Version => Interlocked.Read(ref _version);
    public static LogEntry[] Snapshot() { lock (Sync) return Buffer.ToArray(); }
    public static event Action<LogEntry>? OnEntry;

    public void Emit(LogEvent logEvent)
    {
        var entry = new LogEntry(
            logEvent.Level,
            logEvent.RenderMessage(),
            logEvent.Timestamp.LocalDateTime,
            logEvent.Exception);
        lock (Sync)
        {
            Buffer.Enqueue(entry);
            while (Buffer.Count > 2000) Buffer.Dequeue();
            Interlocked.Increment(ref _version);
        }
        OnEntry?.Invoke(entry);
    }
}

public sealed record LogEntry(
    LogEventLevel Level,
    string        Message,
    DateTime      Timestamp,
    Exception?    Exception = null);
