using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot;

public enum BotLogLevel
{
    /// <summary>Warnings and errors only.</summary>
    Quiet = 0,
    /// <summary>What the bot decided: behavior changes, targets, steps, casts.</summary>
    Info,
    /// <summary>Why it decided it: the checks behind each decision, a few lines a second.</summary>
    Debug,
    /// <summary>Everything, every tick: steering, distances, arbitration. Heavy.</summary>
    Trace,
}

public readonly record struct BotLogEntry(double Seconds, DateTime At, BotLogLevel Level, string Text)
{
    public string Prefix => Level switch
    {
        BotLogLevel.Quiet => "WRN",
        BotLogLevel.Info => "INF",
        BotLogLevel.Debug => "DBG",
        _ => "TRC",
    };
}

/// <summary>
/// The bot's log: everything goes into a ring the Log window and
/// <c>/drakbot log dump</c> read back, and everything at or above the
/// level also goes to the host's log (the client's log file). Warnings and
/// errors always go through. Debug and trace lines carry a prefix so they
/// read as the bot's in a shared file.
/// </summary>
public sealed class BotLog(IPluginLogger inner, Func<double>? clock = null) : IPluginLogger
{
    public const int Capacity = 4000;

    private readonly object _gate = new();
    private readonly Queue<BotLogEntry> _entries = new(Capacity);
    private readonly Func<double> _clock = clock ?? (() => 0d);
    private long _sequence;

    public BotLogLevel Level { get; set; } = BotLogLevel.Debug;

    /// <summary>Bumps with every line, so a viewer knows when to refresh.</summary>
    public long Sequence => Interlocked.Read(ref _sequence);

    public void Info(string message) => Write(BotLogLevel.Info, message, Level >= BotLogLevel.Info, inner.Info);

    public void Warn(string message) => Write(BotLogLevel.Quiet, "warn: " + message, true, _ => inner.Warn(message));

    public void Error(string message, Exception? exception = null) =>
        Write(BotLogLevel.Quiet, "error: " + message + (exception is null ? string.Empty : $" ({exception.GetType().Name}: {exception.Message})"), true, _ => inner.Error(message, exception));

    public void Debug(string message) => Write(BotLogLevel.Debug, message, Level >= BotLogLevel.Debug, text => inner.Info("[dbg] " + text));

    public void Trace(string message) => Write(BotLogLevel.Trace, message, Level >= BotLogLevel.Trace, text => inner.Info("[trc] " + text));

    public bool IsEnabled(BotLogLevel level) => level == BotLogLevel.Quiet || Level >= level;

    /// <summary>The ring's contents, oldest first.</summary>
    public IReadOnlyList<BotLogEntry> Snapshot()
    {
        lock (_gate)
            return _entries.ToArray();
    }

    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
        Interlocked.Increment(ref _sequence);
    }

    /// <summary>The ring as text, one line each, for a dump file or the clipboard.</summary>
    public string Dump()
    {
        var text = new System.Text.StringBuilder();
        foreach (BotLogEntry entry in Snapshot())
            text.Append(entry.At.ToString("HH:mm:ss.fff")).Append(' ').Append(entry.Prefix).Append(' ').Append(entry.Text).Append('\n');
        return text.ToString();
    }

    private void Write(BotLogLevel level, string text, bool forward, Action<string> sink)
    {
        // Trace lines are kept in the ring only when trace is on; the ring
        // would otherwise be all steering noise.
        if (level == BotLogLevel.Trace && Level < BotLogLevel.Trace)
            return;
        var entry = new BotLogEntry(_clock(), DateTime.Now, level, text);
        lock (_gate)
        {
            if (_entries.Count >= Capacity)
                _entries.Dequeue();
            _entries.Enqueue(entry);
        }
        Interlocked.Increment(ref _sequence);
        if (forward)
            sink(text);
    }
}

/// <summary>Debug and trace on any logger: a plain host logger takes only Info and above.</summary>
public static class BotLogExtensions
{
    public static void Debug(this IPluginLogger log, string message)
    {
        if (log is BotLog bot)
            bot.Debug(message);
    }

    public static void Trace(this IPluginLogger log, string message)
    {
        if (log is BotLog bot)
            bot.Trace(message);
    }

    /// <summary>Whether a trace line would be kept, so callers can skip building the string.</summary>
    public static bool Traces(this IPluginLogger log) => log is BotLog { Level: >= BotLogLevel.Trace };

    public static bool Debugs(this IPluginLogger log) => log is BotLog { Level: >= BotLogLevel.Debug };
}

/// <summary>A logger for hosts without one, so a controller built in tests still has a ring.</summary>
public sealed class NoOpPluginLogger : IPluginLogger
{
    public static NoOpPluginLogger Instance { get; } = new();

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message, Exception? exception = null)
    {
    }
}
