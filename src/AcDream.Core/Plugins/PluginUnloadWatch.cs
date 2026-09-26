namespace AcDream.Core.Plugins;

/// <summary>
/// Watches the previous copies of reloaded plugins leave memory, for every
/// plugin session in the process at once. It never forces a collection on a
/// thread that runs a game: it looks at each copy once a second from its own
/// timer and lets the runtime collect in its own time. Only near the end of
/// the watch does it ask, from that timer thread, for one background
/// collection that does not stop the game threads, so a quiet process that
/// has had no reason to collect does not report a copy that is merely
/// uncollected yet. What it finds goes back to the session that asked, which
/// reports it on its own tick thread.
/// </summary>
internal sealed class PluginUnloadWatch
{
    /// <summary>How long a previous copy is watched before it is reported still in memory.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    /// <summary>How long before the end of the watch the background collection is asked for.</summary>
    internal static readonly TimeSpan CollectBeforeEnd = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan PollPeriod = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly TimeProvider _time;
    private readonly bool _runTimer;
    private ITimer? _timer;

    internal PluginUnloadWatch(TimeProvider time, bool runTimer)
    {
        _time = time;
        _runTimer = runTimer;
    }

    /// <summary>The one watch every plugin session in the process shares.</summary>
    internal static PluginUnloadWatch Shared { get; } =
        new(TimeProvider.System, runTimer: true);

    /// <summary>
    /// Watches one previous copy. <paramref name="report"/> is called once,
    /// from the watch's own thread, with whether the copy left memory.
    /// </summary>
    internal void Watch(object owner, WeakReference context, Action<bool> report)
    {
        lock (_gate)
        {
            _entries.Add(new Entry(owner, context, report, _time.GetTimestamp()));
            if (_runTimer && _timer is null)
                _timer = _time.CreateTimer(static state => ((PluginUnloadWatch)state!).Poll(), this, PollPeriod, PollPeriod);
        }
    }

    /// <summary>Stops watching everything a session asked about; nothing is reported to it after this.</summary>
    internal void Forget(object owner)
    {
        lock (_gate)
            _entries.RemoveAll(entry => ReferenceEquals(entry.Owner, owner));
    }

    /// <summary>Looks at every watched copy once. The timer calls this; a test calls it directly.</summary>
    internal void Poll()
    {
        List<(Action<bool> Report, bool LeftMemory)> finished = [];
        bool collect = false;
        lock (_gate)
        {
            for (int index = _entries.Count - 1; index >= 0; index--)
            {
                Entry entry = _entries[index];
                TimeSpan watched = _time.GetElapsedTime(entry.Started);
                if (!entry.Context.IsAlive)
                {
                    finished.Add((entry.Report, true));
                    _entries.RemoveAt(index);
                }
                else if (watched >= Window)
                {
                    finished.Add((entry.Report, false));
                    _entries.RemoveAt(index);
                }
                else if (!entry.CollectionAsked && watched >= Window - CollectBeforeEnd)
                {
                    entry.CollectionAsked = true;
                    collect = true;
                }
            }
            if (_entries.Count == 0)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        if (collect)
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
        foreach ((Action<bool> report, bool leftMemory) in finished)
            report(leftMemory);
    }

    private sealed class Entry(object owner, WeakReference context, Action<bool> report, long started)
    {
        internal object Owner { get; } = owner;
        internal WeakReference Context { get; } = context;
        internal Action<bool> Report { get; } = report;
        internal long Started { get; } = started;
        internal bool CollectionAsked { get; set; }
    }
}
