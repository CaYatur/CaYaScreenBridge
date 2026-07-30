using System.Text;
using CaYaScreenBridge.Core.Diagnostics;

namespace CaYaScreenBridge.Windows.System;

/// <summary>
/// Appends log entries to a daily file and prunes old ones, so a machine that has been running the
/// application for a year does not accumulate an unbounded log directory.
///
/// Writes are batched onto a background timer. The engine logs from paths that can be reached by the
/// input thread, and a synchronous file write there would be exactly the kind of stall that gets a
/// low level hook evicted.
/// </summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    private const int MaxRetainedDays = 7;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly string _directory;
    private readonly Queue<string> _pending = new();
    private readonly object _lock = new();
    private readonly Timer _flushTimer;

    private string _currentFile = string.Empty;
    private DateOnly _currentDate;
    private bool _disposed;

    public FileLogSink(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        RollIfNeeded();
        PruneOldFiles();

        _flushTimer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
    }

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public string CurrentFile => _currentFile;

    public void Write(LogLevel level, string category, string message)
    {
        if (level < MinimumLevel || _disposed)
        {
            return;
        }

        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} [{category}] {message}";

        lock (_lock)
        {
            _pending.Enqueue(line);

            // A pathological logging loop must not be allowed to exhaust memory.
            while (_pending.Count > 5000)
            {
                _pending.Dequeue();
            }
        }
    }

    public void Flush()
    {
        string[] lines;

        lock (_lock)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            lines = _pending.ToArray();
            _pending.Clear();
        }

        try
        {
            RollIfNeeded();

            var builder = new StringBuilder();
            foreach (string line in lines)
            {
                builder.AppendLine(line);
            }

            File.AppendAllText(_currentFile, builder.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Logging is diagnostic only; a locked file must never surface as an application error.
        }
    }

    private void RollIfNeeded()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        if (_currentDate == today && _currentFile.Length > 0)
        {
            return;
        }

        _currentDate = today;
        _currentFile = Path.Combine(_directory, $"bridge-{today:yyyy-MM-dd}.log");
        PruneOldFiles();
    }

    private void PruneOldFiles()
    {
        try
        {
            DateTime cutoff = DateTime.Now.AddDays(-MaxRetainedDays);

            foreach (string file in Directory.EnumerateFiles(_directory, "bridge-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Pruning is housekeeping; failures are not worth surfacing.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _flushTimer.Dispose();
        Flush();
    }
}
