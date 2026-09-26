using AcDream.Content;
using AcDream.Core.Items;
using AcDream.Headless.Hosting;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Navigation;
using AcDream.Runtime.Plugins;

namespace AcDream.Headless.Plugins;

internal sealed class HeadlessPluginHost
    : IPluginHost,
      IPerPluginSessionSettings,
      IGameState,
      AcDream.Core.Plugins.IPluginEventSink,
      IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> EmptySettings =
        new Dictionary<string, string>();

    private readonly GameRuntime _runtime;
    private readonly RuntimeWorldEntityProjection _worldEntities;
    private readonly object _eventGate = new();
    private readonly RuntimeAutomationSurface _automation;
    // The one snapshot both clients answer a plugin from, so the settings a
    // session was started with read the same whichever client is running.
    private readonly PluginSessionSettings _sessionSettings;
    private readonly object _tickGate = new();
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
    private bool _disposed;

    internal HeadlessPluginHost(
        GameRuntime runtime,
        IPluginLogger logger,
        IPluginStorage? storage = null,
        IPluginStorage? vtankProfiles = null,
        PluginSessionSettings? sessionSettings = null,
        Func<string, bool>? submitChatText = null,
        MagicCatalog? magicCatalog = null,
        HeadlessLogoutAutomation? logout = null,
        Func<uint, bool, bool>? answerConfirmation = null,
        Func<bool>? requestGracefulStop = null,
        AcDream.Content.IDatReaderWriter? content = null,
        object? contentLock = null,
        IGameRuntimeCommands? sessionCommands = null,
        NavigationWalkController? navigationWalk = null,
        Action<string, Exception>? pluginCommandFailed = null,
        string? peerDirectory = null,
        IReadOnlyList<string>? pluginTags = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Log = logger ?? throw new ArgumentNullException(nameof(logger));
        Storage = storage ?? NoOpPluginStorage.Instance;
        VtankProfiles = vtankProfiles ?? NoOpPluginStorage.Instance;
        _sessionSettings = sessionSettings ?? PluginSessionSettings.Empty;
        Window = new HeadlessHostWindow(requestGracefulStop);
        // One factory, shared with the windowed host. The surface announces
        // this client to the other clients on this machine off the tick it is
        // built with, so the tick, the folder they find each other in and the
        // words this client answers to all have to be passed here; none of
        // them can be bound later.
        _automation = RuntimeAutomationBindings.CreateSurface(
            HeadlessAutomationCapabilities.BuildSurfaceInputs(
                new HeadlessSurfaceInputParts
                {
                    Events = this,
                    PeerDirectory = peerDirectory
                        ?? AcDream.Platform.ApplicationPathSet.Resolve()
                            .PluginPeersDirectory,
                    PluginTags = pluginTags,
                }));
        // One registry, the surface's own, exactly as the windowed host does
        // it: the verbs a plugin registers and the verbs the client registers
        // for itself live together, so a line typed anywhere finds all of them.
        Commands = _automation.PluginCommands;
        if (pluginCommandFailed is not null)
            _automation.ReportPluginCommandFailuresTo(pluginCommandFailed);
        // One wiring, shared with the windowed host: what this host can lend
        // the plugin surface goes in the capability record, and the runtime
        // fills the rest.
        RuntimeAutomationBindings.Apply(
            _automation,
            runtime,
            HeadlessAutomationCapabilities.Build(new HeadlessAutomationParts
            {
                Runtime = runtime,
                Warn = Log.Warn,
                Content = content,
                ContentLock = contentLock,
                MagicCatalog = magicCatalog,
                SubmitChatText = submitChatText,
                SessionCommands = sessionCommands,
                NavigationWalk = navigationWalk,
                Logout = logout,
                AnswerConfirmation = answerConfirmation,
                Events = this,
            }));
        // Which runtime happening becomes which plugin event is the shared
        // surface's job, on both clients: it watches the runtime and raises
        // them here. Watching the runtime a second time from this host would
        // mean a plugin hearing every event twice.
        // The one producer of what a plugin sees in the world, shared with
        // the client that has a window.
        _worldEntities = new RuntimeWorldEntityProjection(runtime, Log.Warn);
    }

    public bool HasUi => false;

    public IPluginLogger Log { get; }
    public IPluginCommandRegistry Commands { get; }
    public IPluginStorage Storage { get; }
    public IPluginStorage VtankProfiles { get; }

    /// <summary>
    /// The session's loot classifier directory, the same one the graphical
    /// client keeps: a plugin that publishes its loot rules loads headless
    /// exactly as it does with a window, and another plugin can ask it for
    /// verdicts by id.
    /// </summary>
    public IPluginLootClassifierRegistry LootClassifiers { get; } =
        new AcDream.Core.Plugins.PluginLootClassifierRegistry();
    public IGameState State => this;
    public IEvents Events => this;
    public ISelectionService Selection => _runtime.ActionOwner.Selection;
    public IAutomationSurface Automation => _automation;

    /// <summary>The runtime navigation behind <see cref="Automation"/>; the session host binds its walk controller and commands to it.</summary>
    internal RuntimeNavigationAutomation NavigationAutomation => _automation.NavigationAutomation;

    /// <summary>
    /// Offers a typed line to the one command registry, so the chat route can
    /// reach the verbs plugins and the client itself registered.
    /// </summary>
    internal bool TryHandlePluginCommand(string commandLine) =>
        _automation.TryHandlePluginCommand(commandLine);

    /// <summary>
    /// What the plugins on the one surface make of a typed line, so the chat
    /// route can ask before offering it to verbs or sending it.
    /// </summary>
    internal PluginChatInputDecision InterceptChatInput(string typed) =>
        _automation.InterceptChatInput(typed);

    /// <summary>
    /// Whether a verb is already spoken for on that one registry. A front end
    /// with a verb of its own asks before answering it.
    /// </summary>
    internal bool ClaimsPluginVerb(string verb) =>
        _automation.ClaimsPluginVerb(verb);

    public IUiRegistry Ui => NoOpUiRegistry.Instance;

    public IHostWindow Window { get; }

    public IReadOnlyDictionary<string, string> SessionSettings => EmptySettings;

    public IReadOnlyDictionary<string, string> SessionSettingsFor(string pluginId) =>
        _sessionSettings.SessionSettingsFor(pluginId);

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
        // The surface polls its own owners off this tick, as the first thing
        // subscribed to it, which is what the windowed client does too. It is
        // not polled separately here: two polls a tick on one client and one
        // on the other is a difference in its own right.
        Action<double>? handlers;
        lock (_tickGate)
            handlers = _tick;
        if (handlers is null)
            return;
        // Walked in place rather than copied out: the tick runs about 67
        // times a second on every session, and a copied handler list costs
        // an array per tick that grows with every subscriber.
        foreach (Action<double> handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler(elapsedSeconds);
            }
            catch (Exception error)
            {
                // Plugin errors don't propagate out of event dispatch — but
                // they are no longer invisible.
                Log.Warn($"Plugin tick handler threw: {error}");
            }
        }
    }

    internal Action? ReplayCapturedForTest
    {
        get => _worldEntities.ReplayCapturedForTest;
        set => _worldEntities.ReplayCapturedForTest = value;
    }

    public IReadOnlyList<WorldEntitySnapshot> Entities
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _worldEntities.Entities;
        }
    }

    public IReadOnlyList<ContractSnapshot> Contracts
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _runtime.ContractsOwner.ProjectForPlugins();
        }
    }

    public event Action<WorldEntitySnapshot> EntitySpawned
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            ObjectDisposedException.ThrowIf(_disposed, this);
            _worldEntities.Subscribe(value);
        }
        remove
        {
            if (value is null)
                return;
            _worldEntities.Unsubscribe(value);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        lock (_eventGate)
            _disposed = true;
        _worldEntities.Dispose();
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
        _automation.Dispose();
    }

    /// <summary>
    /// The character has arrived in the world. Raised by the shared surface,
    /// which is what decides that a lifecycle change means this.
    /// </summary>
    public void FireLoginComplete() => FireLifecycle(arrived: true);

    /// <summary>The character has left the world.</summary>
    public void FireLogoff() => FireLifecycle(arrived: false);

    private void FireLifecycle(bool arrived)
    {
        Action? handlers;
        lock (_tickGate)
            handlers = arrived ? _loginComplete : _logoff;
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

    /// <summary>The character died, with the message the server sent.</summary>
    /// <param name="deathMessage">What the server said about the death.</param>
    public void FireLocalPlayerDied(string deathMessage)
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

    public event Action<PluginPortalTransition> PortalTransition
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _portalTransition += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _portalTransition -= value;
        }
    }

    public event Action<PluginItemUseCompletion> ItemUseCompleted
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _itemUseCompleted += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _itemUseCompleted -= value;
        }
    }

    public event Action<PluginGoToReport> NavigationChanged
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _navigationChanged += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _navigationChanged -= value;
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

    public event Action<PluginActivationCompletion> ActivationCompleted
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _activationCompleted += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _activationCompleted -= value;
        }
    }

    /// <summary>
    /// An object appeared, moved, changed or went away. Which runtime
    /// happening that was -- an object arriving, an inventory move, an
    /// answered description -- is decided by the shared surface, so both
    /// clients report the same kind for the same happening.
    /// </summary>
    /// <param name="change">Which object, and what happened to it.</param>
    public void FireObjectChanged(PluginObjectChange change)
    {
        Action<PluginObjectChange>? handlers;
        lock (_tickGate)
        {
            // Stamped here, the same way the windowed client's events object
            // stamps it: a plugin orders and de-duplicates object changes by
            // this revision, so an unstamped change on one client is a
            // plugin that silently works on one client only.
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
            catch (Exception error)
            {
                Log.Warn($"Plugin object-change handler threw: {error}");
            }
        }
    }

    /// <summary>
    /// The character went through a portal, or a login placed it in the
    /// world. The shared surface decides what a portal change means and
    /// raises it once; this stamps the revision the same way the windowed
    /// client's events object does, so a plugin can order transitions
    /// identically on either client.
    /// </summary>
    /// <param name="transition">Where the character went, and how far along.</param>
    public void FirePortalTransition(PluginPortalTransition transition)
    {
        Action<PluginPortalTransition>? handlers;
        lock (_tickGate)
        {
            transition = transition with
            {
                Revision = ++_portalTransitionRevision,
            };
            handlers = _portalTransition;
        }
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginPortalTransition>)handler)(transition); }
            catch (Exception error)
            {
                Log.Warn($"Plugin portal-transition handler threw: {error}");
            }
        }
    }

    /// <summary>
    /// A use the character started on an object finished. Raised by the
    /// shared surface off the runtime's own completion, so both clients
    /// report the same result under the same revision.
    /// </summary>
    /// <param name="completion">Which use finished, and with what result.</param>
    public void FireItemUseCompleted(PluginItemUseCompletion completion)
    {
        Action<PluginItemUseCompletion>? handlers;
        lock (_tickGate)
            handlers = _itemUseCompleted;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginItemUseCompletion>)handler)(completion); }
            catch (Exception error)
            {
                Log.Warn($"Plugin item-use handler threw: {error}");
            }
        }
    }

    /// <summary>
    /// An activation the character started on an object completed, failed or
    /// was interrupted. The correlation between the activation and what ended
    /// it belongs to the shared surface, so it is the same on both clients.
    /// </summary>
    /// <param name="completion">Which activation ended, and how.</param>
    public void FireActivationCompleted(PluginActivationCompletion completion)
    {
        Action<PluginActivationCompletion>? handlers;
        lock (_tickGate)
            handlers = _activationCompleted;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginActivationCompletion>)handler)(completion); }
            catch (Exception error)
            {
                Log.Warn($"Plugin activation-completion handler threw: {error}");
            }
        }
    }

    /// <summary>
    /// The walk the client is driving reached a new state. The states and
    /// their order are decided by the shared runtime owner, so a plugin sees
    /// the same walk here as it does on the windowed client.
    /// </summary>
    /// <param name="report">Where the walk stands now.</param>
    public void FireNavigationChanged(PluginGoToReport report)
    {
        Action<PluginGoToReport>? handlers;
        lock (_tickGate)
            handlers = _navigationChanged;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginGoToReport>)handler)(report); }
            catch (Exception error)
            {
                Log.Warn($"Plugin navigation handler threw: {error}");
            }
        }
    }
    /// <summary>A container the character can see inside was opened.</summary>
    /// <param name="containerObjectId">The container that opened.</param>
    public void FireContainerOpened(uint containerObjectId)
    {
        Action<uint>? handlers;
        lock (_tickGate)
            handlers = _containerOpened;
        FireContainer(handlers, containerObjectId);
    }

    /// <summary>A container the character could see inside was closed.</summary>
    /// <param name="containerObjectId">The container that closed.</param>
    public void FireContainerClosed(uint containerObjectId)
    {
        Action<uint>? handlers;
        lock (_tickGate)
            handlers = _containerClosed;
        FireContainer(handlers, containerObjectId);
    }

    private void FireContainer(Action<uint>? handlers, uint value)
    {
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

    /// <summary>
    /// Called by the session host whenever it records a confirmation
    /// request. It goes through the shared surface rather than straight to
    /// the handlers, so this client raises it by the same road as the one
    /// with a window.
    /// </summary>
    internal void RaiseConfirmationRequested(PluginConfirmation confirmation) =>
        _automation.RaiseConfirmationRequested(confirmation);

    /// <summary>The player is being asked to accept or decline something.</summary>
    /// <param name="confirmation">What is being asked, and under which id.</param>
    public void FireConfirmationRequested(PluginConfirmation confirmation)
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
}
