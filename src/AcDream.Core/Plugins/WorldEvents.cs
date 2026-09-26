using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

public sealed class WorldEvents : IPluginEventSink
{
    private readonly object _lock = new();
    private readonly Action<string>? _report;
    private IPluginWorldEntities? _worldEntities;
    private Action<double>? _tick;
    private Action? _loginComplete;
    private Action? _logoff;
    private Action<string>? _localPlayerDied;
    private Action<PluginObjectChange>? _objectChanged;
    private long _objectChangeRevision;
    private Action<PluginPortalTransition>? _portalTransition;
    private long _portalTransitionRevision;
    private Action<PluginItemUseCompletion>? _itemUseCompleted;
    private Action<PluginGoToReport>? _navigationChanged;
    private Action<uint>? _containerOpened;
    private Action<uint>? _containerClosed;
    private Action<PluginConfirmation>? _confirmationRequested;
    private Action<PluginActivationCompletion>? _activationCompleted;

    /// <summary>
    /// Raises plugin events with nowhere to name a handler that threw.
    /// </summary>
    public WorldEvents()
        : this(null)
    {
    }

    /// <summary>
    /// Raises plugin events, naming a handler that threw.
    /// </summary>
    /// <param name="report">
    /// Where a plugin handler that threw is named. A plugin's failure is its
    /// own and never stops the client telling the next one, but a client that
    /// says nothing about it leaves a plugin author with a handler that
    /// silently stopped running and no idea why.
    /// </param>
    public WorldEvents(Action<string>? report)
    {
        _report = report;
    }

    /// <summary>
    /// Names the producer that raises <see cref="EntitySpawned"/> and
    /// replays what is already there to a handler added late. Called once,
    /// where the host builds its runtime.
    /// </summary>
    public void BindWorldEntities(IPluginWorldEntities worldEntities)
    {
        ArgumentNullException.ThrowIfNull(worldEntities);
        lock (_lock)
        {
            if (_worldEntities is not null)
                throw new InvalidOperationException(
                    "The world objects a plugin watches already have a "
                    + "producer; a second one would mean two orders of the "
                    + "same events.");
            _worldEntities = worldEntities;
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

    public event Action<PluginPortalTransition> PortalTransition
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _portalTransition += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _portalTransition -= value;
        }
    }

    public event Action<PluginItemUseCompletion> ItemUseCompleted
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _itemUseCompleted += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _itemUseCompleted -= value;
        }
    }

    public event Action<PluginGoToReport> NavigationChanged
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _navigationChanged += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _navigationChanged -= value;
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

    public event Action<PluginActivationCompletion> ActivationCompleted
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _activationCompleted += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _activationCompleted -= value;
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
            catch (Exception error) { Report("death", error); }
        }
    }

    public void FireObjectChanged(PluginObjectChange change)
    {
        Action<PluginObjectChange>? handlers;
        lock (_lock)
        {
            change = change with
            {
                Revision = ++_objectChangeRevision,
                ChangedFields = PluginObjectChange.FieldsFor(change.Kind),
            };
            handlers = _objectChanged;
        }
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginObjectChange>)handler)(change); }
            catch (Exception error) { Report("object-change", error); }
        }
    }

    public void FirePortalTransition(PluginPortalTransition transition)
    {
        Action<PluginPortalTransition>? handlers;
        lock (_lock)
        {
            transition = transition with { Revision = ++_portalTransitionRevision };
            handlers = _portalTransition;
        }
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginPortalTransition>)handler)(transition); }
            catch (Exception error) { Report("portal-transition", error); }
        }
    }

    public void FireItemUseCompleted(PluginItemUseCompletion completion)
    {
        Action<PluginItemUseCompletion>? handlers;
        lock (_lock)
            handlers = _itemUseCompleted;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginItemUseCompletion>)handler)(completion); }
            catch (Exception error) { Report("item-use-completed", error); }
        }
    }

    public void FireNavigationChanged(PluginGoToReport report)
    {
        Action<PluginGoToReport>? handlers;
        lock (_lock)
            handlers = _navigationChanged;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginGoToReport>)handler)(report); }
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
            catch (Exception error) { Report("confirmation", error); }
        }
    }

    public void FireActivationCompleted(PluginActivationCompletion completion)
    {
        Action<PluginActivationCompletion>? handlers;
        lock (_lock)
            handlers = _activationCompleted;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginActivationCompletion>)handler)(completion); }
            catch (Exception error) { Report("activation-completed", error); }
        }
    }

    private void FireUInt(Action<uint>? handlers, uint value)
    {
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<uint>)handler)(value); }
            catch (Exception error) { Report("container", error); }
        }
    }

    private void Fire(Action? handlers)
    {
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action)handler)(); }
            catch (Exception error) { Report("lifecycle", error); }
        }
    }

    /// <summary>
    /// Names a plugin handler that threw. One plugin's failure is its own:
    /// it neither reaches the caller that raised the event nor stops the
    /// next handler hearing about the same event.
    /// </summary>
    private void Report(string kindName, Exception error) =>
        _report?.Invoke($"Plugin {kindName} handler threw: {error}");

    public void FireTick(double elapsedSeconds)
    {
        Action<double>? handlers;
        lock (_lock)
            handlers = _tick;
        if (handlers is null)
            return;

        // Walked in place rather than copied out: the tick runs about 67
        // times a second, and a copied handler list costs an array per tick
        // that grows with every subscriber.
        foreach (Action<double> handler in Delegate.EnumerateInvocationList(handlers))
        {
            try { handler(elapsedSeconds); }
            catch (Exception error) { Report("tick", error); }
        }
    }

    /// <summary>
    /// Objects appearing in the world, from the one producer that reads the
    /// runtime's object directory: adding a handler replays what is already
    /// there before it starts receiving new ones.
    /// </summary>
    public event Action<WorldEntitySnapshot> EntitySpawned
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            IPluginWorldEntities? producer;
            lock (_lock)
                producer = _worldEntities;
            producer?.Subscribe(value);
        }
        remove
        {
            if (value is null)
                return;
            IPluginWorldEntities? producer;
            lock (_lock)
                producer = _worldEntities;
            producer?.Unsubscribe(value);
        }
    }
}
