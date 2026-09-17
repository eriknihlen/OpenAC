using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

public sealed class WorldEvents : IEvents
{
    private readonly object _lock = new();
    private readonly Dictionary<uint, WorldEntitySnapshot> _current = new();
    private readonly List<Subscription> _subscriptions = new();
    private Subscription[] _liveSnapshot = Array.Empty<Subscription>();
    private Action<double>? _tick;
    private Action? _loginComplete;
    private Action? _logoff;
    private Action<string>? _localPlayerDied;
    private Action<PluginObjectChange>? _objectChanged;
    private Action<uint>? _containerOpened;
    private Action<uint>? _containerClosed;
    private Action<PluginConfirmation>? _confirmationRequested;

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
            catch { /* plugin errors do not propagate out of event dispatch */ }
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

    public event Action LoginComplete
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _loginComplete += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _loginComplete -= value;
        }
    }

    public event Action Logoff
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _logoff += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _logoff -= value;
        }
    }

    public event Action<string> LocalPlayerDied
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _localPlayerDied += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _localPlayerDied -= value;
        }
    }

    public event Action<PluginObjectChange> ObjectChanged
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _objectChanged += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _objectChanged -= value;
        }
    }

    public event Action<uint> ContainerOpened
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _containerOpened += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _containerOpened -= value;
        }
    }

    public event Action<uint> ContainerClosed
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _containerClosed += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _containerClosed -= value;
        }
    }

    public event Action<PluginConfirmation> ConfirmationRequested
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _confirmationRequested += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _confirmationRequested -= value;
        }
    }

    public void FireLoginComplete()
    {
        Action? handlers;
        lock (_lock)
            handlers = _loginComplete;
        Fire(handlers);
    }

    public void FireLogoff()
    {
        Action? handlers;
        lock (_lock)
            handlers = _logoff;
        Fire(handlers);
    }

    public void FireLocalPlayerDied(string deathMessage)
    {
        ArgumentNullException.ThrowIfNull(deathMessage);
        Action<string>? handlers;
        lock (_lock)
            handlers = _localPlayerDied;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<string>)handler)(deathMessage); }
            catch { /* plugin errors do not propagate out of event dispatch */ }
        }
    }

    public void FireObjectChanged(PluginObjectChange change)
    {
        Action<PluginObjectChange>? handlers;
        lock (_lock)
            handlers = _objectChanged;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginObjectChange>)handler)(change); }
            catch { /* plugin errors do not propagate out of event dispatch */ }
        }
    }

    public void FireContainerOpened(uint containerObjectId)
    {
        Action<uint>? handlers;
        lock (_lock)
            handlers = _containerOpened;
        FireUInt(handlers, containerObjectId);
    }

    public void FireContainerClosed(uint containerObjectId)
    {
        Action<uint>? handlers;
        lock (_lock)
            handlers = _containerClosed;
        FireUInt(handlers, containerObjectId);
    }

    public void FireConfirmationRequested(PluginConfirmation confirmation)
    {
        Action<PluginConfirmation>? handlers;
        lock (_lock)
            handlers = _confirmationRequested;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginConfirmation>)handler)(confirmation); }
            catch { /* plugin errors do not propagate out of event dispatch */ }
        }
    }

    private static void FireUInt(Action<uint>? handlers, uint value)
    {
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<uint>)handler)(value); }
            catch { /* plugin errors do not propagate out of event dispatch */ }
        }
    }

    private static void Fire(Action? handlers)
    {
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action)handler)(); }
            catch { /* plugin errors do not propagate out of event dispatch */ }
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
            catch { /* plugin errors do not propagate out of event dispatch */ }
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
                catch { /* plugin errors do not propagate out of += */ }
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
                catch { /* plugin errors do not propagate out of += */ }
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
