using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

public sealed class WorldEvents : IEvents
{
    private readonly object _lock = new();
    private readonly Dictionary<uint, WorldEntitySnapshot> _current = new();
    private readonly List<Subscription> _subscriptions = new();
    private Subscription[] _liveSnapshot = Array.Empty<Subscription>();
    private Action<double>? _tick;

    private sealed class Subscription(Action<WorldEntitySnapshot> handler)
    {
        public Action<WorldEntitySnapshot> Handler { get; } = handler;
        public Queue<WorldEntitySnapshot> Pending { get; } = new();
        public bool Replaying { get; set; } = true;
        public bool Active { get; set; } = true;
    }

    public void FireEntitySpawned(WorldEntitySnapshot snapshot)
    {
        Subscription[] toNotify;
        lock (_lock)
        {
            _current[snapshot.Id] = snapshot;
            for (int i = 0; i < _subscriptions.Count; i++)
            {
                Subscription subscription = _subscriptions[i];
                if (subscription.Active && subscription.Replaying)
                    subscription.Pending.Enqueue(snapshot);
            }
            toNotify = _liveSnapshot;
        }

        for (int i = 0; i < toNotify.Length; i++)
        {
            try { toNotify[i].Handler(snapshot); }
            catch { /* plugin errors don't propagate out of event dispatch */ }
        }
    }

    public void UpsertCurrent(WorldEntitySnapshot snapshot)
    {
        lock (_lock)
            _current[snapshot.Id] = snapshot;
    }

    public bool ForgetEntity(uint id)
    {
        lock (_lock)
            return _current.Remove(id);
    }

    public void ClearCurrent()
    {
        lock (_lock)
        {
            _current.Clear();
            for (int i = 0; i < _subscriptions.Count; i++)
            {
                Subscription subscription = _subscriptions[i];
                if (subscription.Replaying)
                    subscription.Pending.Clear();
            }
        }
    }

    public event Action<double> Tick
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _tick += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _tick -= value;
        }
    }

    public void FireTick(double elapsedSeconds)
    {
        Action<double>? handlers;
        lock (_lock)
            handlers = _tick;
        if (handlers is null)
            return;

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<double>)handler)(elapsedSeconds); }
            catch { /* plugin errors don't propagate out of event dispatch */ }
        }
    }

    public event Action<WorldEntitySnapshot> EntitySpawned
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            var subscription = new Subscription(value);
            WorldEntitySnapshot[] replay;
            lock (_lock)
            {
                _subscriptions.Add(subscription);
                replay = _current.Values.ToArray();
            }

            // Replay outside the lock. Live events that arrive meanwhile queue
            // behind this snapshot and drain before the subscription joins the
            // direct multicast, preserving one monotonic delivery order.
            foreach (var s in replay)
            {
                lock (_lock)
                {
                    if (!subscription.Active)
                        return;
                    if (!_current.TryGetValue(s.Id, out WorldEntitySnapshot current)
                        || current != s)
                    {
                        continue;
                    }
                }

                try { subscription.Handler(s); }
                catch { /* plugin errors don't propagate out of += */ }
            }

            while (true)
            {
                WorldEntitySnapshot pending;
                lock (_lock)
                {
                    if (!subscription.Active)
                        return;
                    if (!subscription.Pending.TryDequeue(out pending))
                    {
                        subscription.Replaying = false;
                        RebuildLiveSnapshotLocked();
                        return;
                    }
                }

                try { subscription.Handler(pending); }
                catch { /* plugin errors don't propagate out of += */ }
            }
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
            {
                for (int i = _subscriptions.Count - 1; i >= 0; i--)
                {
                    Subscription subscription = _subscriptions[i];
                    if (subscription.Handler != value)
                        continue;

                    subscription.Active = false;
                    subscription.Pending.Clear();
                    _subscriptions.RemoveAt(i);
                    if (!subscription.Replaying)
                        RebuildLiveSnapshotLocked();
                    break;
                }
            }
        }
    }

    private void RebuildLiveSnapshotLocked()
    {
        int count = 0;
        for (int i = 0; i < _subscriptions.Count; i++)
        {
            Subscription subscription = _subscriptions[i];
            if (subscription.Active && !subscription.Replaying)
                count++;
        }

        if (count == 0)
        {
            _liveSnapshot = Array.Empty<Subscription>();
            return;
        }

        var rebuilt = new Subscription[count];
        int write = 0;
        for (int i = 0; i < _subscriptions.Count; i++)
        {
            Subscription subscription = _subscriptions[i];
            if (subscription.Active && !subscription.Replaying)
                rebuilt[write++] = subscription;
        }
        _liveSnapshot = rebuilt;
    }
}
