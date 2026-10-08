using System.Globalization;

namespace MusicAura.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public readonly record struct LogEntry(DateTime Time, LogLevel Level, string Source, string Message)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Time:HH:mm:ss.fff} {LevelTag(Level)} [{Source}] {Message}");

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Debug => "D",
        LogLevel.Info => "I",
        LogLevel.Warning => "W",
        _ => "E",
    };
}

/// <summary>
/// Small thread-safe logger that keeps the most recent entries in memory so the UI can
/// show a diagnostics view, and forwards every entry to an optional sink (logcat, console).
/// </summary>
public sealed class AuraLog
{
    private readonly object _lock = new();
    private readonly LogEntry[] _ring;
    private int _next;
    private int _count;

    public AuraLog(int capacity = 500)
    {
        _ring = new LogEntry[capacity];
    }

    /// <summary>Called for every entry, on the thread that logged it.</summary>
    public Action<LogEntry>? Sink { get; set; }

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public void Debug(string source, string message) => Write(LogLevel.Debug, source, message);

    public void Info(string source, string message) => Write(LogLevel.Info, source, message);

    public void Warn(string source, string message) => Write(LogLevel.Warning, source, message);

    public void Error(string source, string message, Exception? ex = null) =>
        Write(LogLevel.Error, source, ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    public void Write(LogLevel level, string source, string message)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var entry = new LogEntry(DateTime.Now, level, source, message);
        lock (_lock)
        {
            _ring[_next] = entry;
            _next = (_next + 1) % _ring.Length;
            _count = Math.Min(_count + 1, _ring.Length);
        }

        try
        {
            Sink?.Invoke(entry);
        }
        catch
        {
            // A broken log sink must never take the audio engine down.
        }
    }

    public IReadOnlyList<LogEntry> Snapshot(int max = int.MaxValue)
    {
        lock (_lock)
        {
            var n = Math.Min(max, _count);
            var result = new LogEntry[n];
            var start = (_next - n + _ring.Length) % _ring.Length;
            for (var i = 0; i < n; i++)
            {
                result[i] = _ring[(start + i) % _ring.Length];
            }

            return result;
        }
    }
}
