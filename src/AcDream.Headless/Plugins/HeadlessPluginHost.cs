using AcDream.Plugin.Abstractions;
using AcDream.Runtime;

namespace AcDream.Headless.Plugins;

internal sealed class HeadlessPluginHost
    : IPluginHost,
      IGameState,
      IEvents,
      IRuntimeEventObserver,
      IDisposable
{
    private readonly GameRuntime _runtime;
    private readonly IDisposable _eventSubscription;
    private readonly object _eventGate = new();
    private readonly List<Subscription> _subscriptions = [];
    private Subscription[] _liveSnapshot = [];
    private bool _disposed;

    private readonly record struct ReplayEntity(
        RuntimeEntityIdentity Identity,
        WorldEntitySnapshot Snapshot);

    private sealed class Subscription(Action<WorldEntitySnapshot> handler)
    {
        internal Action<WorldEntitySnapshot> Handler { get; } = handler;
        internal Queue<ReplayEntity> Pending { get; } = new();
        internal HashSet<RuntimeEntityIdentity> Delivered { get; } = [];
        internal bool Replaying { get; set; } = true;
        internal bool Active { get; set; } = true;
    }

    internal HeadlessPluginHost(
        GameRuntime runtime,
        IPluginLogger logger,
        IPluginCommandRegistry? commands = null,
        IPluginStorage? vtankProfiles = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Log = logger ?? throw new ArgumentNullException(nameof(logger));
        Commands = commands ?? NoOpPluginCommandRegistry.Instance;
        VtankProfiles = vtankProfiles ?? NoOpPluginStorage.Instance;
        _eventSubscription = runtime.Subscribe(this);
    }

    public bool HasUi => false;
    public IPluginLogger Log { get; }
    public IPluginCommandRegistry Commands { get; }
    public IPluginStorage VtankProfiles { get; }
    public IGameState State => this;
    public IEvents Events => this;
    public ISelectionService Selection => _runtime.ActionOwner.Selection;
    public IAutomationSurface Automation => NoOpAutomationSurface.Instance;

    public IUiRegistry Ui => NoOpUiRegistry.Instance;

    public event Action<double> Tick
    {
        add { _ = value; }
        remove { _ = value; }
    }

    internal Action? ReplayCapturedForTest { get; set; }

    public IReadOnlyList<WorldEntitySnapshot> Entities
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var visitor = new SnapshotVisitor(_runtime);
            _runtime.Entities.Visit(visitor);
            return visitor.Items.Select(static item => item.Snapshot).ToArray();
        }
    }

    public IReadOnlyList<ContractSnapshot> Contracts
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return AcDream.Runtime.Gameplay.ContractPluginProjection.Project(
                _runtime.ContractsOwner.View,
                catalog: null,
                now: DateTime.UtcNow);
        }
    }

    public event Action<WorldEntitySnapshot> EntitySpawned
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            ObjectDisposedException.ThrowIf(_disposed, this);
            var subscription = new Subscription(value);
            lock (_eventGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _subscriptions.Add(subscription);
            }

            var visitor = new SnapshotVisitor(
                _runtime,
                ReplayCapturedForTest);
            _runtime.Entities.Visit(visitor);
            ReplayEntity[] replay = visitor.Items.ToArray();

            foreach (ReplayEntity item in replay)
            {
                lock (_eventGate)
                {
                    if (!subscription.Active)
                        return;
                    if (!_runtime.Entities.TryGet(
                            item.Identity.ServerGuid,
                            out RuntimeEntitySnapshot current)
                        || current.Identity != item.Identity
                        || !subscription.Delivered.Add(item.Identity))
                    {
                        continue;
                    }
                }

                Invoke(subscription.Handler, item.Snapshot);
            }

            while (true)
            {
                ReplayEntity pending;
                lock (_eventGate)
                {
                    if (!subscription.Active)
                        return;
                    if (!subscription.Pending.TryDequeue(out pending))
                    {
                        subscription.Replaying = false;
                        subscription.Delivered.Clear();
                        RebuildLiveSnapshotLocked();
                        return;
                    }
                    if (!subscription.Delivered.Add(pending.Identity))
                        continue;
                }

                Invoke(subscription.Handler, pending.Snapshot);
            }
        }
        remove
        {
            if (value is null)
                return;
            lock (_eventGate)
            {
                for (int index = _subscriptions.Count - 1; index >= 0; index--)
                {
                    Subscription subscription = _subscriptions[index];
                    if (subscription.Handler != value)
                        continue;
                    subscription.Active = false;
                    subscription.Pending.Clear();
                    subscription.Delivered.Clear();
                    _subscriptions.RemoveAt(index);
                    if (!subscription.Replaying)
                        RebuildLiveSnapshotLocked();
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        lock (_eventGate)
        {
            _disposed = true;
            foreach (Subscription subscription in _subscriptions)
            {
                subscription.Active = false;
                subscription.Pending.Clear();
                subscription.Delivered.Clear();
            }
            _subscriptions.Clear();
            _liveSnapshot = [];
        }
        _eventSubscription.Dispose();
    }

    public void OnEntity(in RuntimeEntityDelta delta)
    {
        if (delta.Change != RuntimeEntityChange.Registered)
            return;
        Subscription[] toNotify;
        var pending = new ReplayEntity(
            delta.Entity.Identity,
            Convert(_runtime, delta.Entity));
        lock (_eventGate)
        {
            if (_disposed)
                return;
            foreach (Subscription subscription in _subscriptions)
            {
                if (subscription.Active && subscription.Replaying)
                    subscription.Pending.Enqueue(pending);
            }
            toNotify = _liveSnapshot;
        }
        if (toNotify.Length == 0)
            return;

        foreach (Subscription subscription in toNotify)
            Invoke(subscription.Handler, pending.Snapshot);
    }

    public void OnLifecycle(in RuntimeLifecycleDelta delta) { }
    public void OnCommand(in RuntimeCommandDelta delta) { }
    public void OnInventory(in RuntimeInventoryDelta delta) { }
    public void OnChat(in RuntimeChatDelta delta) { }
    public void OnMovement(in RuntimeMovementDelta delta) { }
    public void OnPortal(in RuntimePortalDelta delta) { }
    public void OnCombat(in RuntimeCombatDelta delta) { }

    private static WorldEntitySnapshot Convert(
        GameRuntime runtime,
        in RuntimeEntitySnapshot entity)
    {
        uint sourceId = runtime.EntityObjects.Entities.TryGetActive(
            entity.Identity.ServerGuid,
            out AcDream.Runtime.Entities.RuntimeEntityRecord record)
            ? record.Snapshot.SetupTableId ?? 0u
            : 0u;
        return new WorldEntitySnapshot(
            entity.Identity.LocalEntityId,
            sourceId,
            entity.Position?.Frame.Origin ?? default,
            entity.Position?.Frame.Orientation
                ?? System.Numerics.Quaternion.Identity);
    }

    private static void Invoke(
        Action<WorldEntitySnapshot> handler,
        WorldEntitySnapshot snapshot)
    {
        try { handler(snapshot); }
        catch { }
    }

    private sealed class SnapshotVisitor(
        GameRuntime runtime,
        Action? captureBarrier = null)
        : IRuntimeEntityVisitor
    {
        private Action? _captureBarrier = captureBarrier;

        internal List<ReplayEntity> Items { get; } =
            new(runtime.Entities.Count);

        public void Visit(in RuntimeEntitySnapshot entity)
        {
            Items.Add(new ReplayEntity(
                entity.Identity,
                Convert(runtime, entity)));
            Interlocked.Exchange(ref _captureBarrier, null)?.Invoke();
        }
    }

    private void RebuildLiveSnapshotLocked()
    {
        _liveSnapshot = _subscriptions
            .Where(static subscription =>
                subscription.Active && !subscription.Replaying)
            .ToArray();
    }
}
