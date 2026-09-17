using AcDream.Content;
using AcDream.Core.Items;
using AcDream.Headless.Hosting;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Navigation;
using AcDream.Runtime.Plugins;

namespace AcDream.Headless.Plugins;

internal sealed class HeadlessPluginHost
    : IPluginHost,
      IPerPluginSessionSettings,
      IGameState,
      IEvents,
      IRuntimeEventObserver,
      IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> EmptySettings =
        new Dictionary<string, string>();

    private readonly GameRuntime _runtime;
    private readonly IDisposable _eventSubscription;
    private readonly object _eventGate = new();
    private readonly List<Subscription> _subscriptions = [];
    private Subscription[] _liveSnapshot = [];
    private readonly RuntimeAutomationSurface _automation;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
        _sessionSettingsByPlugin;
    private readonly object _tickGate = new();
    private Action<double>? _tick;
    private Action? _loginComplete;
    private Action? _logoff;
    private Action<string>? _localPlayerDied;
    private Action<PluginObjectChange>? _objectChanged;
    private Action<uint>? _containerOpened;
    private Action<uint>? _containerClosed;
    private Action<PluginConfirmation>? _confirmationRequested;
    private bool _wasInWorld;
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
        IPluginStorage? storage = null,
        IPluginStorage? vtankProfiles = null,
        IReadOnlyDictionary<string, Dictionary<string, string>>? sessionSettings = null,
        Func<string, bool>? submitChatText = null,
        HeadlessItemAutomation? items = null,
        MagicCatalog? magicCatalog = null,
        HeadlessLogoutAutomation? logout = null,
        Func<uint, bool, bool>? answerConfirmation = null,
        Func<bool>? requestGracefulStop = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Log = logger ?? throw new ArgumentNullException(nameof(logger));
        Commands = commands ?? NoOpPluginCommandRegistry.Instance;
        Storage = storage ?? NoOpPluginStorage.Instance;
        VtankProfiles = vtankProfiles ?? NoOpPluginStorage.Instance;
        _sessionSettingsByPlugin = CopySessionSettings(sessionSettings);
        Window = new HeadlessHostWindow(requestGracefulStop);
        _automation = new RuntimeAutomationSurface();
        _automation.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        _automation.BindRemoteBodiesUnsimulated();
        _automation.BindSubmit(submitChatText);
        if (magicCatalog is not null)
            _automation.BindMagicCatalog(magicCatalog);
        if (items is not null)
        {
            _automation.BindItems(
                items.TryUse,
                items.TryApply,
                items.TryMove,
                items.TryMerge,
                items.TryDrop,
                items.TryGive,
                HeadlessItemAutomation.RefusePickup,
                HeadlessItemAutomation.RefuseIdentify);
            _automation.BindEquipment(items.TryEquip, () => items.EquipmentBusy);
        }
        if (logout is not null)
        {
            _automation.BindLogout(
                logout.TryRequestLogout,
                () => logout.CanRequestLogout);
        }
        if (answerConfirmation is not null)
            _automation.BindDialogs(answerConfirmation);
        _wasInWorld = runtime.Lifecycle.State == RuntimeLifecycleState.InWorld;
        runtime.CommunicationOwner.LocalPlayerDied += OnLocalPlayerDied;
        runtime.InventoryOwner.ExternalContainers.Changed += OnExternalContainerChanged;
        runtime.ActionOwner.Transactions.AppraisalReceived += OnAppraisalReceived;
        _eventSubscription = runtime.Subscribe(this);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
        CopySessionSettings(
            IReadOnlyDictionary<string, Dictionary<string, string>>? source)
    {
        if (source is null || source.Count == 0)
            return new Dictionary<string, IReadOnlyDictionary<string, string>>();
        var copy = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            source.Count,
            StringComparer.Ordinal);
        foreach ((string pluginId, Dictionary<string, string>? perPlugin) in source)
        {
            copy[pluginId] = perPlugin is { Count: > 0 }
                ? new Dictionary<string, string>(perPlugin, StringComparer.Ordinal)
                : EmptySettings;
        }
        return copy;
    }

    public bool HasUi => false;
    public IPluginLogger Log { get; }
    public IPluginCommandRegistry Commands { get; }
    public IPluginStorage Storage { get; }
    public IPluginStorage VtankProfiles { get; }
    public IGameState State => this;
    public IEvents Events => this;
    public ISelectionService Selection => _runtime.ActionOwner.Selection;
    public IAutomationSurface Automation => _automation;

    /// <summary>The runtime navigation behind <see cref="Automation"/>; the session host binds its walk controller and commands to it.</summary>
    internal RuntimeNavigationAutomation NavigationAutomation => _automation.NavigationAutomation;

    public IUiRegistry Ui => NoOpUiRegistry.Instance;

    public IHostWindow Window { get; }

    public IReadOnlyDictionary<string, string> SessionSettings => EmptySettings;

    public IReadOnlyDictionary<string, string> SessionSettingsFor(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _sessionSettingsByPlugin.TryGetValue(
            pluginId,
            out IReadOnlyDictionary<string, string>? settings)
            ? settings
            : EmptySettings;
    }

    public event Action<double> Tick
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _tick += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _tick -= value;
        }
    }

    internal void FireTick(double elapsedSeconds)
    {
        _automation.Poll();
        Action<double>? handlers;
        lock (_tickGate)
            handlers = _tick;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<double>)handler)(elapsedSeconds);
            }
            catch (Exception error)
            {
                // Plugin errors don't propagate out of event dispatch — but
                // they are no longer invisible.
                Log.Warn($"Plugin tick handler threw: {error}");
            }
        }
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
        lock (_tickGate)
        {
            _tick = null;
            _loginComplete = null;
            _logoff = null;
            _localPlayerDied = null;
            _objectChanged = null;
            _containerOpened = null;
            _containerClosed = null;
            _confirmationRequested = null;
        }
        _runtime.CommunicationOwner.LocalPlayerDied -= OnLocalPlayerDied;
        _runtime.InventoryOwner.ExternalContainers.Changed -= OnExternalContainerChanged;
        _runtime.ActionOwner.Transactions.AppraisalReceived -= OnAppraisalReceived;
        _eventSubscription.Dispose();
        _automation.Dispose();
    }

    public void OnEntity(in RuntimeEntityDelta delta)
    {
        RaiseObjectChanged(
            delta.Entity.Identity.ServerGuid,
            delta.Change switch
            {
                RuntimeEntityChange.Registered => PluginObjectChangeKind.Created,
                RuntimeEntityChange.Rebucketed => PluginObjectChangeKind.Moved,
                RuntimeEntityChange.Withdrawn => PluginObjectChangeKind.Released,
                RuntimeEntityChange.Deleted => PluginObjectChangeKind.Released,
                _ => PluginObjectChangeKind.Updated,
            });
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

    public void OnLifecycle(in RuntimeLifecycleDelta delta)
    {
        bool isInWorld = delta.Current == RuntimeLifecycleState.InWorld;
        lock (_eventGate)
        {
            if (_disposed || _wasInWorld == isInWorld)
                return;
            _wasInWorld = isInWorld;
        }

        Action? handlers;
        lock (_tickGate)
            handlers = isInWorld ? _loginComplete : _logoff;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception error)
            {
                Log.Warn($"Plugin lifecycle handler threw: {error}");
            }
        }
    }

    public event Action LoginComplete
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _loginComplete += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _loginComplete -= value;
        }
    }

    public event Action Logoff
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _logoff += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _logoff -= value;
        }
    }

    public event Action<string> LocalPlayerDied
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _localPlayerDied += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _localPlayerDied -= value;
        }
    }

    private void OnLocalPlayerDied(string deathMessage)
    {
        Action<string>? handlers;
        lock (_tickGate)
            handlers = _localPlayerDied;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<string>)handler)(deathMessage);
            }
            catch (Exception error)
            {
                Log.Warn($"Plugin death handler threw: {error}");
            }
        }
    }

    public event Action<PluginObjectChange> ObjectChanged
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _objectChanged += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _objectChanged -= value;
        }
    }

    public event Action<uint> ContainerOpened
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _containerOpened += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _containerOpened -= value;
        }
    }

    public event Action<uint> ContainerClosed
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _containerClosed += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _containerClosed -= value;
        }
    }

    public event Action<PluginConfirmation> ConfirmationRequested
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _confirmationRequested += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _confirmationRequested -= value;
        }
    }

    public void OnCommand(in RuntimeCommandDelta delta) { }

    public void OnInventory(in RuntimeInventoryDelta delta)
    {
        if (delta.Change == RuntimeInventoryChange.Cleared)
            return;
        RaiseObjectChanged(
            delta.Item.ObjectId,
            delta.Change switch
            {
                RuntimeInventoryChange.Added => PluginObjectChangeKind.Created,
                RuntimeInventoryChange.Moved => PluginObjectChangeKind.Moved,
                RuntimeInventoryChange.Removed => PluginObjectChangeKind.Released,
                _ => PluginObjectChangeKind.Updated,
            });
    }

    private void RaiseObjectChanged(uint objectId, PluginObjectChangeKind kind)
    {
        Action<PluginObjectChange>? handlers;
        lock (_tickGate)
            handlers = _objectChanged;
        if (handlers is null)
            return;
        var change = new PluginObjectChange(objectId, kind);
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginObjectChange>)handler)(change); }
            catch (Exception error)
            {
                Log.Warn($"Plugin object-change handler threw: {error}");
            }
        }
    }

    private void OnExternalContainerChanged(
        AcDream.Core.Items.ExternalContainerTransition transition)
    {
        switch (transition.Kind)
        {
            case AcDream.Core.Items.ExternalContainerTransitionKind.Opened:
                RaiseUInt(ref _containerOpened, transition.ContainerId);
                break;
            case AcDream.Core.Items.ExternalContainerTransitionKind.ReplacementRequested:
            case AcDream.Core.Items.ExternalContainerTransitionKind.Closed:
            case AcDream.Core.Items.ExternalContainerTransitionKind.Reset:
                if (transition.PreviousContainerId != 0u)
                    RaiseUInt(ref _containerClosed, transition.PreviousContainerId);
                break;
        }
    }

    private void OnAppraisalReceived(uint objectId) =>
        RaiseObjectChanged(objectId, PluginObjectChangeKind.IdentReceived);

    private void RaiseUInt(ref Action<uint>? field, uint value)
    {
        Action<uint>? handlers;
        lock (_tickGate)
            handlers = field;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<uint>)handler)(value); }
            catch (Exception error)
            {
                Log.Warn($"Plugin container handler threw: {error}");
            }
        }
    }

    /// <summary>Called by the session host whenever it records a confirmation request.</summary>
    internal void RaiseConfirmationRequested(PluginConfirmation confirmation)
    {
        Action<PluginConfirmation>? handlers;
        lock (_tickGate)
            handlers = _confirmationRequested;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginConfirmation>)handler)(confirmation); }
            catch (Exception error)
            {
                Log.Warn($"Plugin confirmation handler threw: {error}");
            }
        }
    }

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
