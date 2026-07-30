using System.Collections.Concurrent;
using System.Globalization;

namespace CaYaScreenBridge.Core.Diagnostics;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

public readonly record struct LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message)
{
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Timestamp.LocalDateTime:HH:mm:ss.fff} {Level,-5} [{Category}] {Message}");
}

public interface ILogSink
{
    void Write(LogLevel level, string category, string message);
}

public static class LogSinkExtensions
{
    public static void Debug(this ILogSink sink, string category, string message) =>
        sink.Write(LogLevel.Debug, category, message);

    public static void Info(this ILogSink sink, string category, string message) =>
        sink.Write(LogLevel.Info, category, message);

    public static void Warn(this ILogSink sink, string category, string message) =>
        sink.Write(LogLevel.Warn, category, message);

    public static void Error(this ILogSink sink, string category, string message) =>
        sink.Write(LogLevel.Error, category, message);
}

/// <summary>
/// A bounded in-memory log that the diagnostics page reads. Bounded on purpose: the engine can emit
/// entries from the mouse hook path, and an unbounded list would grow without limit in a process
/// designed to run for weeks.
/// </summary>
public sealed class RingLog : ILogSink
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly int _capacity;

    public RingLog(int capacity = 2000)
    {
        _capacity = Math.Max(64, capacity);
    }

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public event Action<LogEntry>? EntryWritten;

    public void Write(LogLevel level, string category, string message)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var entry = new LogEntry(DateTimeOffset.Now, level, category, message);
        _entries.Enqueue(entry);

        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
        }

        EntryWritten?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot() => _entries.ToArray();

    public string Render() => string.Join(Environment.NewLine, _entries.Select(e => e.ToString()));
}

/// <summary>Fans one log call out to several sinks (memory ring plus rolling file, typically).</summary>
public sealed class CompositeLogSink : ILogSink
{
    private readonly ILogSink[] _sinks;

    public CompositeLogSink(params ILogSink[] sinks) => _sinks = sinks;

    public void Write(LogLevel level, string category, string message)
    {
        foreach (ILogSink sink in _sinks)
        {
            try
            {
                sink.Write(level, category, message);
            }
            catch
            {
                // A failing sink (a locked log file, say) must never take down the caller.
            }
        }
    }
}

public sealed class NullLogSink : ILogSink
{
    public static readonly NullLogSink Instance = new();

    public void Write(LogLevel level, string category, string message)
    {
    }
}
