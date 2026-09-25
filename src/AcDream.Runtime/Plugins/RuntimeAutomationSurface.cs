using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Player;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.Plugins;
using AcDream.Core.Properties;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using AcDream.Core.World;
using AcDream.Core.CharGen;
using AcDream.Content;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Plugins;

internal sealed class RuntimeAutomationSurface
    : IAutomationSurface, ICharacterInfo, ISpellCatalog, IMagicCommands, IPluginChat,
      ICombatAutomation, IEquipmentAutomation, IItemAutomation,
      ILootAutomation, IFellowshipAutomation, IEnchantmentAutomation,
      IRuntimeCommunicationObserver, IRuntimeEventObserver,
      IWorldObjectAutomation, IRecallAutomation, IAllegianceAutomation,
      IWorldTimeAutomation, ILoginAutomation, INetworkAutomation, IRecoveryAutomation,
      IProjectileAutomation, ISelectionAutomation, IDialogAutomation,
      IWorldLabelAutomation, IScopedWorldLabelSource, ICharacterOptionsAutomation,
      IDisposable
{
    private readonly PluginCommandRegistry _pluginCommands;
    private Action<string, Exception>? _pluginCommandFailed;
    private AcDream.Runtime.Navigation.NavigationChatCommands?
        _navigationCommands;
    private IDisposable? _statusCommand;
    private const int MaximumPluginChatMessages = 512;
    // One period for both: the registry holds a write that failed or was
    // refused back for exactly as long as the next heartbeat would have been,
    // so a note this client cannot write costs one attempt per heartbeat.
    private static readonly double PeerHeartbeatSeconds =
        LocalPluginPeerRegistry.HeartbeatPeriod.TotalSeconds;
    private readonly object _gate = new();
    private readonly AcDream.Runtime.Navigation.RuntimeNavigationAutomation _navigation;
    private readonly AcDream.Runtime.Maps.RuntimeDungeonMapAutomation _dungeonMap = new();
    private readonly AcDream.Core.Plugins.IPluginEventSink? _events;
    private static readonly double PeerCommandPollSeconds =
        LocalPluginPeerRegistry.CommandPollPeriod.TotalSeconds;
    private readonly LocalPluginPeerRegistry _peers;

    /// <summary>
    /// The labels this client answers to. The host starts it with whatever
    /// the player configured; a plugin may replace them, which is why this
    /// is not fixed at birth.
    /// </summary>
    private string[] _peerTags;

    /// <summary>
    /// Broadcast lines this client has taken but not run yet, each with the
    /// instant it is due. The wait is this client's place in the recipients'
    /// order, so several clients taking one line do not all act at once.
    /// </summary>
    private readonly List<PendingPeerCommand> _pendingPeerCommands = [];

    /// <summary>
    /// How far this client's own delivery has read the peers' command rings.
    /// Its own cursor, separate from every plugin's, so a plugin reading
    /// <c>CaptureCommands</c> cannot make the client skip a line or run one
    /// twice.
    /// </summary>
    private long _deliveredCommandSequence;
    private double _peerCommandPollRemaining;
    private double _peerHeartbeatRemaining;
    private long _lastNavigationSequence;
    private PluginGoToState _lastNavigationState;
    private RuntimePortalSnapshot? _lastPublishedPortalSnapshot;
    private long _recallRequestRevision;
    private long _pendingRecallRequestRevision;
    private PluginRecallRequest _lastRecallRequest;
    private long _activationRevision;
    private readonly Dictionary<uint, long> _pendingActivationObjectIds = new();

    private GameRuntime? _runtime;
    private AcDream.Runtime.Gameplay.RuntimeTradeAutomation? _tradeAutomation;
    private AcDream.Runtime.Gameplay.RuntimeVendorAutomation? _vendorAutomation;
    private RuntimeCommunicationState? _communication;
    private RuntimeCharacterState? _character;
    private RuntimeSpellCastState? _cast;
    private Spellbook? _spellbook;
    private MagicCatalog _magicCatalog = MagicCatalog.Empty;
    private IReadOnlyDictionary<uint, string> _skillNames =
        new Dictionary<uint, string>();
    private IReadOnlyDictionary<uint, uint> _skillIcons =
        new Dictionary<uint, uint>();
    private Func<int, string> _speciesName = static _ => string.Empty;
    private Func<uint, string?> _titleName = static _ => null;
    private IChargenPaletteColorSource? _paletteColors;
    private Func<uint, uint, bool>? _equip;
    private Func<uint, bool>? _equipSecondary;
    private Func<bool>? _equipmentBusy;
    private Func<bool>? _requestLogout;
    private Func<bool>? _canRequestLogout;
    private Func<uint, bool>? _useItem;
    private Func<uint, PluginItemCommandResult>? _useWorldObject;
    private Func<uint, uint, bool>? _applyItem;
    private Func<uint, uint, uint, int, bool>? _moveItem;
    private Func<uint, uint, uint, int, bool, bool>? _moveItemJoiningStack;
    private Func<uint, uint, uint, bool>? _mergeItems;
    private Func<uint, uint, bool>? _dropItem;
    private Func<uint, uint, uint, bool>? _giveItem;
    private Func<uint, bool, AcDream.Runtime.Gameplay.RuntimeBackpackPlacementOutcome>? _pickupItem;
    private Func<uint, bool>? _identifyItem;
    private Func<uint, IReadOnlyList<uint>, bool>? _salvageItems;
    private PluginActivationCompletion _lastActivationCompletion;
    private PluginRecallKind? _lastSuccessfulRecallKind;
    private PluginRecallLocation? _lifestoneLocation;
    private PluginRecallLocation? _marketplaceLocation;
    private PluginRecallLocation? _mansionLocation;
    private PluginRecallLocation? _allegianceLocation;
    private long _lifestoneRevision;
    private long _marketplaceRevision;
    private long _mansionRevision;
    private long _allegianceRevision;
    private Func<uint, uint, int, bool>? _sellItem;
    private Func<uint, bool>? _dismissGhost;
    private Func<bool>? _chatInputActive;
    private Func<string, bool>? _composeChat;
    private Func<PluginSelectionAction, bool>? _selectionAction;
    private IReadOnlyList<PluginProjectileDebugSample> _projectileDebugSamples =
        Array.Empty<PluginProjectileDebugSample>();
    private long _projectileDebugSamplesExpireAt;
    // One label set per owner, so a plugin replacing its own set never
    // touches another's. The merged view the drawing side reads is rebuilt
    // only when a set changes, since it is asked for every frame.
    private readonly Dictionary<string, PluginWorldLabel[]> _worldLabels =
        new(StringComparer.Ordinal);
    private PluginWorldLabel[]? _worldLabelsMerged = Array.Empty<PluginWorldLabel>();
    private const string UnscopedWorldLabelOwner = "";
    private IGameRuntimeCommands? _sessionCommands;
    private Func<string, bool>? _submitChatText;
    private IDisposable? _communicationSubscription;
    private readonly List<PluginChatMessage> _chatMessages = [];
    private ulong _pluginChatSequence;
    /// <summary>
    /// Filters installed by plugins. They live on the surface rather than on
    /// the log so they survive a session being replaced.
    /// </summary>
    private readonly ChatSuppressionFilters _chatFilters = new();
    /// <summary>
    /// Interceptors installed by plugins over the lines the player types. On
    /// the surface for the same reason as the filters: they outlive a session.
    /// </summary>
    private readonly ChatInputInterceptors _chatInterceptors = new();
    private IDisposable? _chatFilterInstallation;
    private IDisposable? _runtimeEventSubscription;
    private bool _wasInWorld;
    private Func<uint, bool, bool>? _answerConfirmation;
    private Action<ExternalContainerTransition>? _externalContainerChanged;
    private Action<PluginChatMessage>? _chatReceived;
    private event Action<PluginEquipmentObservation>? _equipmentPlacementObserved;
    private Action<PluginChatLinkClicked>? _chatLinkClicked;
    private SpellTable? _spellCatalogSource;
    private IReadOnlyList<PluginSpellInfo> _allSpells = Array.Empty<PluginSpellInfo>();
    private long _inventoryCompletionRevision;
    private PluginInventoryCompletion _lastInventoryCompletion;
    private readonly Dictionary<(uint Target, uint Spell), TrackedEnchantment>
        _trackedEnchantments = [];
    private long _trackedCastCompletionRevision;
    private bool _disposed;

    /// <summary>
    /// The creature the outstanding plugin-driven swing was armed at, so a
    /// request aimed somewhere else can end it rather than queue behind it.
    /// </summary>
    private uint _armedAttackTarget;

    private IReadOnlyList<PluginSpellInfo> _knownSelfBuffs = Array.Empty<PluginSpellInfo>();
    private IReadOnlyList<PluginSpellInfo> _knownAttackSpells =
        Array.Empty<PluginSpellInfo>();
    private IReadOnlyList<PluginSpellInfo> _knownCombatSpells =
        Array.Empty<PluginSpellInfo>();
    private IReadOnlyList<PluginActiveEnchantment> _enchantments =
        Array.Empty<PluginActiveEnchantment>();

    private static readonly string[] AttributeNames =
        ["Strength", "Endurance", "Quickness", "Coordination", "Focus", "Self"];

    /// <summary>The three pools, in the order a plugin reads them.</summary>
    private static readonly string[] VitalNames =
        ["Health", "Stamina", "Mana"];

    private readonly Func<uint, IReadOnlyList<uint>> _activeSpellIdsForPlayer;

    public RuntimeAutomationSurface()
        : this(events: null)
    {
    }

    internal RuntimeAutomationSurface(
        AcDream.Core.Plugins.IPluginEventSink? events,
        LocalPluginPeerRegistry? peers = null,
        IReadOnlyList<string>? peerTags = null)
    {
        _navigation = new AcDream.Runtime.Navigation.RuntimeNavigationAutomation(() => IsAvailable);
        _activeSpellIdsForPlayer = _ => _enchantments.Select(static enchantment => enchantment.SpellId).ToArray();
        _pluginCommands = new PluginCommandRegistry(ReportPluginCommandFailure);
        _chatInterceptors.InterceptorFaulted = error =>
            ReportPluginCommandFailure("chat-input-interceptor", error);
        _events = events;
        _peers = peers ?? new LocalPluginPeerRegistry(
            AcDream.Platform.ApplicationPathSet.Resolve().PluginPeersDirectory);
        _peerTags = NormalizePeerTags(peerTags);
        if (_events is not null)
            _events.Tick += OnPeerTick;
    }

    internal IPluginCommandRegistry PluginCommands => _pluginCommands;

    /// <summary>
    /// Where a plugin command that threw is reported. Host-shaped rather than
    /// a plugin seam: it lends the surface somewhere to put a line, not
    /// something a plugin can reach. A host with somewhere
    /// better to put it -- a diagnostic stream, a log file -- says so; one
    /// that does not leaves the line on the standard output.
    /// </summary>
    internal void ReportPluginCommandFailuresTo(
        Action<string, Exception> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
            _pluginCommandFailed = report;
    }

    private void ReportPluginCommandFailure(string verb, Exception error)
    {
        Action<string, Exception>? report;
        lock (_gate)
            report = _pluginCommandFailed;
        if (report is null)
        {
            Console.WriteLine(
                $"[PluginCommand:{verb}] {error.GetBaseException().Message}");
            return;
        }
        report(verb, error);
    }

    /// <summary>
    /// Registers the client's own navigation verbs on this surface's command
    /// registry. They are built here rather than by each host so both clients
    /// answer the same words: the two that need something drawn are left out
    /// by a client with nothing to draw on, and say so in plain terms instead
    /// of being missing.
    /// </summary>
    /// <param name="runtime">The session the verbs act on and answer into.</param>
    /// <param name="events">The tick the verbs follow their own walks on.</param>
    /// <param name="toggleGrid">
    /// Shows or hides the navigation grid; null where nothing is drawn.
    /// </param>
    /// <param name="previewRoute">
    /// Draws a route without walking it; null where nothing is drawn.
    /// </param>
    /// <param name="narrate">
    /// Sets who hears a walk's full narration; null where walks are not
    /// planned at all.
    /// </param>
    internal void BindNavigationCommands(
        GameRuntime runtime,
        IEvents? events,
        Func<bool>? toggleGrid,
        Func<uint, bool>? previewRoute,
        Action<Action<string>?>? narrate)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        lock (_gate)
        {
            if (_disposed || _navigationCommands is not null)
                return;
            _navigationCommands =
                new AcDream.Runtime.Navigation.NavigationChatCommands(
                    _navigation,
                    () => runtime.ActionOwner.Selection.SelectedObjectId,
                    // Chat rather than an on-screen notice, so a walk report
                    // and its debug narration can be read back and copied.
                    line => runtime.CommunicationOwner.AddText(
                        line, AcDream.Core.Chat.RetailLogTextType.Default),
                    toggleGrid,
                    previewRoute,
                    narrate)
                .Register(_pluginCommands, events);
        }
    }

    /// <summary>
    /// Registers the client's own <c>/status</c> verb, which answers the one
    /// line describing the session. It is registered here so a console and a
    /// chat box answer the same words rather than each printing its own.
    /// </summary>
    internal void BindStatusCommand(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        lock (_gate)
        {
            if (_disposed || _statusCommand is not null)
                return;
            _statusCommand = _pluginCommands.Register(
                AcDream.Runtime.Chat.RuntimeSessionStatusText.Verb,
                _ => runtime.CommunicationOwner.AddText(
                    AcDream.Runtime.Chat.RuntimeSessionStatusText.For(runtime),
                    AcDream.Core.Chat.RetailLogTextType.Default));
        }
    }

    internal bool TryHandlePluginCommand(string commandLine) =>
        _pluginCommands.TryHandle(commandLine);

    /// <summary>
    /// What the plugins on this surface make of a line the player typed. The
    /// chat route asks before offering the line to plugin verbs or sending it.
    /// </summary>
    internal PluginChatInputDecision InterceptChatInput(string typed) =>
        _chatInterceptors.Decide(typed);

    /// <summary>
    /// Whether a verb is already spoken for on this registry. A front end
    /// with a verb of its own asks before it answers one, so nothing it does
    /// on its own ever shadows a plugin's.
    /// </summary>
    internal bool ClaimsPluginVerb(string verb) =>
        _pluginCommands.IsRegistered(verb);

    public bool IsAvailable
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
            {
                if (_disposed || _character is null || _cast is null)
                    return false;
                runtime = _runtime;
            }
            return runtime is not null
                && runtime.Lifecycle.State == RuntimeLifecycleState.InWorld;
        }
    }

    public ICharacterInfo Character => this;
    public ISpellCatalog Spells => this;
    public IMagicCommands Magic => this;
    public IPluginChat Chat => this;
    public ICombatAutomation Combat => this;
    public IEquipmentAutomation Equipment => this;
    public IItemAutomation Items => this;
    public ILootAutomation Loot => this;
    public IFellowshipAutomation Fellowship => this;
    public IEnchantmentAutomation Enchantments => this;
    public INavigationAutomation Navigation => _navigation;
    public IWorldObjectAutomation Objects => this;
    public IRecallAutomation Recalls => this;
    public IAllegianceAutomation Allegiance => this;
    public IWorldTimeAutomation WorldTime => this;
    public ILoginAutomation Login => this;
    public INetworkAutomation Network => this;
    public IRecoveryAutomation Recovery => this;
    public IProjectileAutomation Projectiles => this;
    public IWorldLabelAutomation Labels => this;
    public IDungeonMapAutomation DungeonMap => _dungeonMap;
    public ISelectionAutomation Selection => this;
    public ITradeAutomation Trade
    {
        get { lock (_gate) return (ITradeAutomation?)_tradeAutomation ?? NoOpAutomationSurface.Instance; }
    }
    public IVendorAutomation Vendor
    {
        get { lock (_gate) return (IVendorAutomation?)_vendorAutomation ?? NoOpAutomationSurface.Instance; }
    }
    public ICharacterOptionsAutomation CharacterOptions => this;

    // ── ICharacterOptionsAutomation ─────────────────────────────────────
    // A change goes through the session's own option command, the one the
    // character options page and the declared-option seeding use, so the
    // client's copy, the immediate send of an option the server saves on its
    // own, and the later save of the whole set are the same on every host.

    IReadOnlyList<string> ICharacterOptionsAutomation.Names =>
        CharacterOptionNames.All;

    bool ICharacterOptionsAutomation.TryGet(string name, out bool value)
    {
        value = false;
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null
            || !IsAvailable
            || !CharacterOptionNames.TryResolve(name, out AcDream.Core.Net.Messages.CharacterOptionId id))
        {
            return false;
        }
        value = runtime.CharacterOwner.Options.GetOptionBit(id);
        return true;
    }

    PluginCharacterOptionResult ICharacterOptionsAutomation.Set(string name, bool value)
    {
        if (!CharacterOptionNames.TryResolve(name, out AcDream.Core.Net.Messages.CharacterOptionId id))
        {
            return new(
                PluginCharacterOptionStatus.UnknownOption,
                $"No character option is called '{name}'.");
        }
        GameRuntime? runtime;
        IGameRuntimeCommands? commands;
        lock (_gate)
        {
            runtime = _runtime;
            commands = _sessionCommands;
        }
        if (runtime is null || commands is null || !IsAvailable)
            return new(PluginCharacterOptionStatus.Unavailable);
        RuntimeCommandResult result = commands.Character.SetSingleOption(
            runtime.Generation,
            (uint)id,
            value);
        return new(result.Status switch
        {
            RuntimeCommandStatus.Accepted => PluginCharacterOptionStatus.Accepted,
            // Both clients decline a change only for an id their option
            // table does not hold, which the name lookup above already
            // answers; there is no other reason to refuse.
            RuntimeCommandStatus.Rejected => PluginCharacterOptionStatus.UnknownOption,
            _ => PluginCharacterOptionStatus.Unavailable,
        });
    }

    PluginBusyState IRecoveryAutomation.CaptureBusyState()
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null)
            return default;
        var state = runtime.ItemInteractionOwner.RuntimeTransactions.CaptureOwnership();
        var inventory = runtime.InventoryOwner.Transactions;
        return new(inventory.BusyCount, inventory.HasPendingRequest,
            state.AwaitingAppraisalId, state.LastUseSourceId, state.LastUseTargetId,
            state.AwaitingItemUseCompletion);
    }

    PluginRecoveryResult IRecoveryAutomation.ClearOneBusyReference()
    {
        GameRuntime? runtime;
        lock (_gate)
        {
            if (_disposed)
                return new(false, Message: "The plugin host is disposed.");
            runtime = _runtime;
        }
        if (runtime is null)
            return new(false, Message: "No game session is bound.");

        InventoryTransactionState transactions =
            runtime.InventoryOwner.Transactions;
        int before = transactions.BusyCount;
        if (before != 0)
        {
            transactions.CompleteUse(0u);
            return new(
                Accepted: true,
                PreviousCount: before,
                CurrentCount: transactions.BusyCount,
                Message: "Cleared one action busy reference.");
        }

        // Nothing is holding an item action, so the thing worth freeing is a
        // description the server never answered: it holds its own reference
        // and the one awaiting slot, and until it is let go every later
        // description is refused.
        if (runtime.ItemInteractionOwner.RuntimeTransactions
            .AbandonAwaitingAppraisal())
        {
            return new(
                Accepted: true,
                PreviousCount: 0,
                CurrentCount: 0,
                Message: "Gave up an unanswered appraisal request.");
        }

        return new(
            Accepted: true,
            PreviousCount: 0,
            CurrentCount: 0,
            Message: "The action busy count was already zero.");
    }

    bool INetworkAutomation.IsAvailable
    {
        get
        {
            lock (_gate)
                return !_disposed;
        }
    }

    IReadOnlyList<PluginNetworkClient> INetworkAutomation.CaptureClients()
    {
        if (_events is null)
            PublishPeerSnapshot();
        ICharacterInfo character = this;
        return _peers.CaptureRemoteClients(character.ObjectId);
    }

    bool INetworkAutomation.TryCaptureSelf(out PluginNetworkClient self)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                self = default;
                return false;
            }
        }
        return TryBuildOwnPeerClient(out self);
    }

    IReadOnlyList<PluginPeerCast> INetworkAutomation.CaptureOwnCasts(
        long afterSequence)
    {
        lock (_gate)
        {
            if (_disposed)
                return Array.Empty<PluginPeerCast>();
        }
        return _peers.CaptureOwnCasts(afterSequence);
    }

    IReadOnlyList<PluginPeerCommand> INetworkAutomation.CaptureOwnCommands(
        long afterSequence)
    {
        lock (_gate)
        {
            if (_disposed)
                return Array.Empty<PluginPeerCommand>();
        }
        return _peers.CaptureOwnCommands(afterSequence);
    }

    bool INetworkAutomation.ImportRemoteClient(PluginNetworkClient client)
    {
        ICharacterInfo character = this;
        uint ownObjectId = character.ObjectId;
        // The same gate an announcement is held to: a client that is not
        // playing has no character to tell an imported one from.
        if (ownObjectId == 0u)
            return false;
        lock (_gate)
        {
            if (_disposed)
                return false;
        }
        return _peers.ImportRemoteClient(client, ownObjectId);
    }

    bool INetworkAutomation.ImportRemoteCast(
        uint casterObjectId,
        uint targetObjectId,
        uint spellId,
        int effectiveSkill,
        double secondsRemaining,
        bool landed)
    {
        ICharacterInfo character = this;
        if (character.ObjectId == 0u)
            return false;
        lock (_gate)
        {
            // Classified in this client's own spell table, exactly as a cast
            // read from a neighbour's note is: a spell nobody here can name
            // is a spell nobody here can act on.
            if (_disposed
                || _spellbook is null
                || !_spellbook.TryGetMetadata(spellId, out _))
            {
                return false;
            }
        }
        return _peers.ImportRemoteCast(
            casterObjectId,
            targetObjectId,
            spellId,
            effectiveSkill,
            secondsRemaining,
            landed);
    }

    bool INetworkAutomation.ImportRemoteCommand(
        uint senderObjectId,
        string line,
        IReadOnlyList<string> tags,
        int delayMilliseconds)
    {
        ICharacterInfo character = this;
        if (character.ObjectId == 0u)
            return false;
        lock (_gate)
        {
            if (_disposed)
                return false;
        }
        return _peers.ImportRemoteCommand(
            senderObjectId,
            tags,
            line ?? string.Empty,
            delayMilliseconds);
    }

    bool INetworkAutomation.AnnounceCastAttempt(
        uint targetObjectId,
        uint spellId,
        int effectiveSkill) =>
        AnnounceCast(
            targetObjectId,
            spellId,
            effectiveSkill,
            durationSeconds: 0d,
            landed: false);

    bool INetworkAutomation.AnnounceCastSuccess(
        uint targetObjectId,
        uint spellId,
        int effectiveSkill,
        double durationSeconds) =>
        AnnounceCast(
            targetObjectId,
            spellId,
            effectiveSkill,
            durationSeconds,
            landed: true);

    /// <summary>
    /// Puts one cast in the note the other clients on this machine read.
    /// What only this client knows is settled here -- who is casting, and
    /// whether the spell is one its own table can name. The rest is the
    /// ring's one rule, which is the same rule a peer's note is read by.
    /// </summary>
    private bool AnnounceCast(
        uint targetObjectId,
        uint spellId,
        int effectiveSkill,
        double durationSeconds,
        bool landed)
    {
        ICharacterInfo character = this;
        uint casterObjectId = character.ObjectId;
        if (casterObjectId == 0u)
            return false;
        lock (_gate)
        {
            // A spell nobody here can name is a spell nobody here can act
            // on, so it never leaves this client either.
            if (_disposed
                || _spellbook is null
                || !_spellbook.TryGetMetadata(spellId, out _))
            {
                return false;
            }
        }

        // Everything else about the cast -- no target, a skill below zero, a
        // duration that is not a finite number of seconds or is longer than a
        // day -- is the ring's one rule, applied in the same place for a cast
        // this client writes and a cast it reads, so the two cannot drift.
        if (!_peers.RecordCast(new LocalPluginCast(
            casterObjectId,
            targetObjectId,
            spellId,
            effectiveSkill,
            durationSeconds,
            landed)))
        {
            return false;
        }
        // A cast is an event on a transport that otherwise only carries
        // state, so the note is rewritten for it as soon as the debounce
        // allows rather than waiting for the next heartbeat.
        if (_peers.IsCastWriteDue())
            PublishPeerSnapshot();
        return true;
    }

    IReadOnlyList<PluginPeerCast> INetworkAutomation.CaptureCasts(
        long afterSequence)
    {
        if (_events is null)
            PublishPeerSnapshot();
        ICharacterInfo character = this;
        IReadOnlyList<PluginPeerCast> casts = _peers.CaptureRemoteCasts(
            afterSequence,
            character.WorldName,
            character.ObjectId);
        if (casts.Count == 0)
            return casts;

        Spellbook? spellbook;
        lock (_gate)
            spellbook = _disposed ? null : _spellbook;
        if (spellbook is null)
            return Array.Empty<PluginPeerCast>();
        // A remote cast is classified in THIS client's spell table, never
        // believed from the note: the id is the only thing worth carrying
        // and everything about the spell is looked up here.
        PluginPeerCast[] known = casts
            .Where(cast => spellbook.TryGetMetadata(cast.SpellId, out _))
            .ToArray();
        return known.Length == 0
            ? Array.Empty<PluginPeerCast>()
            : known;
    }

    bool INetworkAutomation.SetTags(IReadOnlyList<string> tags)
    {
        if (tags is null)
            return false;
        foreach (string tag in tags)
        {
            // A label too long to travel is refused outright rather than cut
            // short: a client answering to half a label is worse than one
            // that says it would not take them.
            if (tag is not null
                && tag.Trim().Length > LocalPluginPeerRegistry.MaximumTagLength)
            {
                return false;
            }
        }
        string[] normalized = NormalizePeerTags(tags);
        lock (_gate)
        {
            if (_disposed)
                return false;
            _peerTags = normalized;
        }
        // The labels live in the note, and the note is what another client
        // reads to decide whether a broadcast is for this one, so the change
        // goes out on the next tick rather than at the next heartbeat.
        PublishPeerSnapshot();
        return true;
    }

    bool INetworkAutomation.BroadcastCommand(
        string line,
        IReadOnlyList<string> tags,
        int delayMilliseconds)
    {
        ICharacterInfo character = this;
        uint senderObjectId = character.ObjectId;
        if (senderObjectId == 0u)
            return false;
        lock (_gate)
        {
            if (_disposed)
                return false;
        }

        // Everything else about the line -- an empty one, one too long or
        // carrying a control character, too many labels or one too long, a
        // delay outside what a stagger may ask for -- is the ring's one
        // rule, applied in the same place for a line this client writes and
        // a line it reads, so the two cannot drift.
        if (!_peers.RecordCommand(new LocalPluginCommand(
            senderObjectId,
            tags ?? Array.Empty<string>(),
            line ?? string.Empty,
            delayMilliseconds)))
        {
            return false;
        }
        // A broadcast is an event on a transport that otherwise only carries
        // state, so the note is rewritten for it as soon as the debounce
        // allows rather than waiting for the next heartbeat.
        if (_peers.IsCommandWriteDue())
            PublishPeerSnapshot();
        return true;
    }

    IReadOnlyList<PluginPeerCommand> INetworkAutomation.CaptureCommands(
        long afterSequence)
    {
        if (_events is null)
            PublishPeerSnapshot();
        IReadOnlyList<LocalPluginPeerCommand> commands = ReadPeerCommands(
            afterSequence);
        if (commands.Count == 0)
            return Array.Empty<PluginPeerCommand>();
        return commands
            .Select(static command => command.Command)
            .ToArray();
    }

    /// <summary>
    /// The broadcast lines the other clients on this computer have asked for
    /// above <paramref name="afterSequence"/>, aimed at labels this client
    /// answers to. One reader for the client's own delivery and for a
    /// plugin's capture, so what a plugin sees and what the client runs are
    /// never two different lists.
    /// </summary>
    private IReadOnlyList<LocalPluginPeerCommand> ReadPeerCommands(
        long afterSequence)
    {
        ICharacterInfo character = this;
        string[] tags;
        lock (_gate)
        {
            if (_disposed)
                return Array.Empty<LocalPluginPeerCommand>();
            tags = _peerTags;
        }
        return _peers.CaptureRemoteCommands(
            afterSequence,
            character.WorldName,
            character.ObjectId,
            tags);
    }

    /// <summary>
    /// The labels as they travel: trimmed, empties dropped, anything longer
    /// than a label may be dropped, repeats ignoring case folded together,
    /// and capped. The same shape the note is written with, so what a plugin
    /// sets and what a peer reads back are the same words.
    /// </summary>
    private static string[] NormalizePeerTags(IReadOnlyList<string>? tags) =>
        (tags ?? Array.Empty<string>())
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Select(static tag => tag.Trim())
            .Where(static tag =>
                tag.Length <= LocalPluginPeerRegistry.MaximumTagLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(128)
            .ToArray();

    bool ILoginAutomation.IsAvailable
    {
        get
        {
            lock (_gate)
                return !_disposed && _runtime is not null;
        }
    }

    uint ILoginAutomation.NextLoginObjectId
    {
        get
        {
            lock (_gate)
                return _runtime?.Session.NextLoginCharacterId ?? 0u;
        }
    }

    IReadOnlyList<PluginLoginCharacter> ILoginAutomation.CaptureRoster()
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || _disposed)
            return Array.Empty<PluginLoginCharacter>();

        IRuntimeCharacterSelectionView view = runtime.Session.CharacterSelection;
        RuntimeCharacterSelectionSnapshot snapshot = view.Snapshot;
        var result = new PluginLoginCharacter[snapshot.RosterCount];
        for (int index = 0; index < result.Length; index++)
        {
            if (!view.TryGetAt(index, out RuntimeCharacterSelectionEntry entry))
                return Array.Empty<PluginLoginCharacter>();
            result[index] = new PluginLoginCharacter(
                entry.CharacterId,
                entry.Name,
                entry.ActiveIndex,
                entry.IsPendingDelete);
        }
        return result;
    }

    bool ILoginAutomation.SetNextLogin(uint characterObjectId)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        return runtime?.Session.TrySetNextLogin(characterObjectId) == true;
    }

    bool ILoginAutomation.ClearNextLogin()
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        return runtime?.Session.ClearNextLogin() == true;
    }

    bool ILoginAutomation.CanRequestLogout
    {
        get
        {
            Func<bool>? canRequestLogout;
            lock (_gate)
                canRequestLogout = _canRequestLogout;
            return IsAvailable && canRequestLogout?.Invoke() == true;
        }
    }

    bool ILoginAutomation.RequestLogout()
    {
        Func<bool>? requestLogout;
        lock (_gate)
            requestLogout = _requestLogout;
        return requestLogout is not null
            && ((ILoginAutomation)this).CanRequestLogout
            && requestLogout();
    }

    PluginWorldTimeSnapshot IWorldTimeAutomation.Snapshot
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            if (runtime is null || !IsAvailable)
                return default;

            double rawTicks = runtime.EnvironmentOwner.WorldTime.NowTicks;
            DerethCalendar calendar = runtime.EnvironmentOwner.WorldTime.Calendar;
            DerethDateTime.Calendar value = calendar.ToCalendar(rawTicks);
            int hour = (int)value.Hour;
            bool isDay = hour is >= 4 and < 12;
            double shiftedTicks = Math.Max(0d, rawTicks)
                + calendar.OriginOffsetTicks;
            double gameTicks = shiftedTicks
                + DerethDateTime.ZeroYear * DerethDateTime.YearTicks;
            double withinHour = shiftedTicks
                - Math.Floor(shiftedTicks / DerethDateTime.HourTicks)
                    * DerethDateTime.HourTicks;
            double untilNight = isDay
                ? ((12 - hour) * DerethDateTime.HourTicks - withinHour) / 60d
                : 0d;
            int dayHour = hour <= 4 ? hour + 16 : hour;
            double untilDay = !isDay
                ? ((20 - dayHour) * DerethDateTime.HourTicks - withinHour) / 60d
                : 0d;
            return new PluginWorldTimeSnapshot(
                true,
                gameTicks,
                value.Year,
                (int)value.Month,
                value.Day,
                hour,
                FormatCalendarName(value.Month.ToString()),
                FormatCalendarName(value.Hour.ToString()),
                isDay,
                Math.Max(0d, untilDay),
                Math.Max(0d, untilNight));
        }
    }

    private static string FormatCalendarName(string value) => value
        .Replace("AndHalf", "-and-Half", StringComparison.Ordinal);

    /// <summary>
    /// Attach to the runtime's gameplay owners unless already attached to
    /// that same runtime. Re-attaching would detach and re-subscribe every
    /// owner, so a host that attached early and a shared binding pass that
    /// attaches late can both ask for it without the second one undoing the
    /// first. Says whether the surface ended up bound: a disposed surface
    /// binds nothing, and a caller reporting which seams it filled must not
    /// count this one when it did not happen.
    /// </summary>
    internal bool EnsureBound(
        GameRuntime runtime, RuntimeCharacterState character, RuntimeSpellCastState cast)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        lock (_gate)
        {
            if (_disposed)
                return false;
            if (ReferenceEquals(_runtime, runtime))
                return true;
        }
        Bind(runtime, character, cast);
        lock (_gate)
            return !_disposed;
    }

    /// <summary>Bind the surface to the runtime's gameplay owners.</summary>
    public void Bind(
        GameRuntime runtime, RuntimeCharacterState character, RuntimeSpellCastState cast)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(cast);

        Spellbook spellbook = character.Spellbook;
        lock (_gate)
        {
            if (_disposed)
                return;
            DetachLocked();
            _runtime = runtime;
            _navigation.Bind(runtime);
            _dungeonMap.Bind(runtime);
            _tradeAutomation = new AcDream.Runtime.Gameplay.RuntimeTradeAutomation(runtime);
            _vendorAutomation = new AcDream.Runtime.Gameplay.RuntimeVendorAutomation(runtime);
            _communication = runtime.CommunicationOwner;
            _communicationSubscription =
                runtime.CommunicationOwner.Events.Subscribe(this);
            _chatFilterInstallation = runtime.CommunicationOwner.Chat.Filters
                .Register(candidate => _chatFilters.ShouldSuppress(candidate));
            _runtimeEventSubscription = runtime.Subscribe(this);
            _wasInWorld =
                runtime.Lifecycle.State == RuntimeLifecycleState.InWorld;
            runtime.CommunicationOwner.LocalPlayerDied += OnLocalPlayerDied;
            _externalContainerChanged = OnExternalContainerChanged;
            runtime.InventoryOwner.ExternalContainers.Changed +=
                _externalContainerChanged;
            runtime.ActionOwner.Transactions.AppraisalReceived +=
                OnAppraisalReceived;
            runtime.ActionOwner.Transactions.UseCompleted +=
                OnUseCompleted;
            _character = character;
            _cast = cast;
            _spellbook = spellbook;
            spellbook.SpellbookChanged += OnSpellbookChanged;
            spellbook.EnchantmentsChanged += OnEnchantmentsChanged;
            runtime.InventoryOwner.Transactions.RequestCompleted +=
                OnInventoryRequestCompleted;
            runtime.InventoryOwner.Transactions.RequestFailed +=
                OnInventoryRequestFailed;
            runtime.InventoryOwner.Objects.ObjectMoved += OnEquipmentObjectMoved;
            runtime.InventoryOwner.Objects.ObjectRemovalClassified +=
                OnEquipmentObjectRemoved;
        }

        RebuildSpellbook();
        RebuildEnchantments();
    }

    public void BindSkillNames(IReadOnlyDictionary<uint, string> skillNames)
    {
        ArgumentNullException.ThrowIfNull(skillNames);
        lock (_gate)
            _skillNames = skillNames;
    }

    public void BindSkillIcons(IReadOnlyDictionary<uint, uint> skillIcons)
    {
        ArgumentNullException.ThrowIfNull(skillIcons);
        lock (_gate)
            _skillIcons = skillIcons;
    }

    public void BindMagicCatalog(MagicCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        lock (_gate)
            _magicCatalog = catalog;
    }

    public void BindSessionCommands(IGameRuntimeCommands commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        lock (_gate)
            _sessionCommands = commands;
        _navigation.BindCommands(commands.Movement, () =>
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime?.Generation ?? RuntimeGenerationToken.Initial;
        });
    }

    /// <summary>The walks plugins ask for through the navigation API.</summary>
    public void BindNavigationWalk(AcDream.Runtime.Navigation.NavigationWalkController walk) =>
        _navigation.BindWalk(walk);

    /// <summary>
    /// Lends the dungeon map the game files, read under <paramref name="contentLock"/>,
    /// so it can tell a sealed cell and flatten a landblock's cells into a plan.
    /// </summary>
    public void BindDungeonMap(AcDream.Core.Content.IDatObjectSource content, object contentLock) =>
        _dungeonMap.BindContent(content, contentLock);

    /// <summary>The runtime navigation this surface hands to plugins; a host binds its walk controller and commands to it.</summary>
    internal AcDream.Runtime.Navigation.RuntimeNavigationAutomation NavigationAutomation => _navigation;

    internal void BindSubmit(Func<string, bool>? submitChatText)
    {
        lock (_gate)
            _submitChatText = submitChatText;
    }

    public void BindSpeciesNameResolver(Func<int, string> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (_gate)
            _speciesName = resolver;
    }

    /// <summary>
    /// Lends the surface the text of a character title, read from the
    /// installed data files. Without it a plugin still sees every title the
    /// character holds, by number, with empty text.
    /// </summary>
    public void BindTitleNameResolver(Func<uint, string?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (_gate)
            _titleName = resolver;
    }

    public void BindPaletteColorResolver(IChargenPaletteColorSource resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (_gate)
            _paletteColors = resolver;
    }


    public void BindEquipment(
        Func<uint, uint, bool> equip,
        Func<bool> isBusy,
        Func<uint, bool>? equipSecondary = null)
    {
        ArgumentNullException.ThrowIfNull(equip);
        ArgumentNullException.ThrowIfNull(isBusy);
        lock (_gate)
        {
            _equip = equip;
            _equipSecondary = equipSecondary;
            _equipmentBusy = isBusy;
        }
    }

    public void BindLogout(
        Func<bool> tryRequestLogout,
        Func<bool> canRequestLogout)
    {
        ArgumentNullException.ThrowIfNull(tryRequestLogout);
        ArgumentNullException.ThrowIfNull(canRequestLogout);
        lock (_gate)
        {
            _requestLogout = tryRequestLogout;
            _canRequestLogout = canRequestLogout;
        }
    }

    public void BindItems(
        Func<uint, bool> useItem,
        Func<uint, uint, bool> applyItem,
        Func<uint, uint, uint, int, bool> moveItem,
        Func<uint, uint, uint, bool> mergeItems,
        Func<uint, uint, bool> dropItem,
        Func<uint, uint, uint, bool> giveItem,
        Func<uint, bool, AcDream.Runtime.Gameplay.RuntimeBackpackPlacementOutcome> pickupItem,
        Func<uint, bool> identifyItem,
        Func<uint, IReadOnlyList<uint>, bool>? salvageItems = null,
        Func<uint, uint, int, bool>? sellItem = null,
        Func<uint, uint, uint, int, bool, bool>? moveItemJoiningStack = null)
    {
        ArgumentNullException.ThrowIfNull(useItem);
        ArgumentNullException.ThrowIfNull(applyItem);
        ArgumentNullException.ThrowIfNull(moveItem);
        ArgumentNullException.ThrowIfNull(mergeItems);
        ArgumentNullException.ThrowIfNull(dropItem);
        ArgumentNullException.ThrowIfNull(giveItem);
        ArgumentNullException.ThrowIfNull(pickupItem);
        ArgumentNullException.ThrowIfNull(identifyItem);
        lock (_gate)
        {
            _useItem = useItem;
            _applyItem = applyItem;
            _moveItem = moveItem;
            _moveItemJoiningStack = moveItemJoiningStack;
            _mergeItems = mergeItems;
            _dropItem = dropItem;
            _giveItem = giveItem;
            _pickupItem = pickupItem;
            _identifyItem = identifyItem;
            _salvageItems = salvageItems;
            _sellItem = sellItem;
        }
    }

    /// <summary>
    /// Wires a walk-then-use route for world objects the plugin does not
    /// own (a vendor, a corpse, a chest, an NPC). DispatchItem falls back
    /// to this when Use/Apply targets an object that is not player-owned,
    /// instead of refusing it outright.
    /// </summary>
    public void BindWorldObjectUse(Func<uint, PluginItemCommandResult> useWorldObject)
    {
        ArgumentNullException.ThrowIfNull(useWorldObject);
        lock (_gate)
            _useWorldObject = useWorldObject;
    }

    /// <summary>
    /// Maps the walk-then-use path's outcome onto the plugin item-command
    /// vocabulary, kept next to BindWorldObjectUse (rather than inline at
    /// the composition call site) so the mapping has one home and one
    /// test.
    /// </summary>
    internal static PluginItemCommandResult MapWorldObjectUseOutcome(
        AutomationUseOutcome outcome) => outcome switch
    {
        AutomationUseOutcome.Started =>
            new PluginItemCommandResult(PluginItemCommandStatus.Started),
        AutomationUseOutcome.Busy =>
            new PluginItemCommandResult(PluginItemCommandStatus.Busy),
        AutomationUseOutcome.NotUseable =>
            new PluginItemCommandResult(
                PluginItemCommandStatus.Refused, "That cannot be used."),
        AutomationUseOutcome.NoRoom =>
            new PluginItemCommandResult(
                PluginItemCommandStatus.Refused,
                "no pack the player has open has room for it"),
        _ => new PluginItemCommandResult(PluginItemCommandStatus.Unavailable),
    };

    public void BindGhostDeletion(Func<uint, bool> dismissGhost)
    {
        ArgumentNullException.ThrowIfNull(dismissGhost);
        lock (_gate)
            _dismissGhost = dismissGhost;
    }

    public void BindSelectionActions(
        Func<PluginSelectionAction, bool> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        lock (_gate)
            _selectionAction = execute;
    }

    public void Unbind()
    {
        lock (_gate)
            DetachLocked();
        _peers.Withdraw();
        _knownSelfBuffs = Array.Empty<PluginSpellInfo>();
        _knownAttackSpells = Array.Empty<PluginSpellInfo>();
        _knownCombatSpells = Array.Empty<PluginSpellInfo>();
        _enchantments = Array.Empty<PluginActiveEnchantment>();
        _timedEnchantments = Array.Empty<PluginActiveEnchantment>();
    }

    /// <summary>
    /// Reports whether the player is typing into the chat entry. Automation
    /// that steers by holding keys asks before it holds any.
    /// </summary>
    public void BindChatInputActive(Func<bool> isActive)
    {
        ArgumentNullException.ThrowIfNull(isActive);
        lock (_gate)
            _chatInputActive = isActive;
    }

    /// <summary>Stages chat text in the entry box without sending it.</summary>
    public void BindChatComposer(Func<string, bool> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        lock (_gate)
            _composeChat = compose;
    }

    private void DetachLocked()
    {
        if (_runtime is { } runtime)
        {
            runtime.InventoryOwner.Objects.ObjectRemovalClassified -=
                OnEquipmentObjectRemoved;
            runtime.InventoryOwner.Objects.ObjectMoved -= OnEquipmentObjectMoved;
            runtime.InventoryOwner.Transactions.RequestFailed -=
                OnInventoryRequestFailed;
            runtime.InventoryOwner.Transactions.RequestCompleted -=
                OnInventoryRequestCompleted;
            if (_externalContainerChanged is not null)
            {
                runtime.InventoryOwner.ExternalContainers.Changed -=
                    _externalContainerChanged;
                _externalContainerChanged = null;
            }
            runtime.ActionOwner.Transactions.AppraisalReceived -=
                OnAppraisalReceived;
            runtime.ActionOwner.Transactions.UseCompleted -=
                OnUseCompleted;
        }
        if (_communication is not null)
            _communication.LocalPlayerDied -= OnLocalPlayerDied;
        _communicationSubscription?.Dispose();
        _communicationSubscription = null;
        _chatFilterInstallation?.Dispose();
        _chatFilterInstallation = null;
        _runtimeEventSubscription?.Dispose();
        _runtimeEventSubscription = null;
        _wasInWorld = false;
        _lastPublishedPortalSnapshot = null;
        _lastRecallRequest = default;
        _recallRequestRevision = 0;
        _pendingRecallRequestRevision = 0;
        _chatMessages.Clear();
        if (_spellbook is not null)
        {
            _spellbook.SpellbookChanged -= OnSpellbookChanged;
            _spellbook.EnchantmentsChanged -= OnEnchantmentsChanged;
        }
        _spellbook = null;
        _character = null;
        _cast = null;
        _vendorAutomation?.Dispose();
        _vendorAutomation = null;
        _tradeAutomation = null;
        _runtime = null;
        _navigation.UnbindRuntime();
        _communication = null;
        _dismissGhost = null;
        _chatInputActive = null;
        _composeChat = null;
        _trackedEnchantments.Clear();
        _trackedCastCompletionRevision = 0;
        // A line waiting out its stagger was asked of the character that has
        // just left the world; running it against whatever session comes
        // next is not what the sender asked for. The cursor stays where it
        // is, so a line already taken is not taken again on the way back in.
        _pendingPeerCommands.Clear();
        _projectileDebugSamples = Array.Empty<PluginProjectileDebugSample>();
        _projectileDebugSamplesExpireAt = 0;
        // The objects the labels hung from are gone with the session.
        _worldLabels.Clear();
        _worldLabelsMerged = Array.Empty<PluginWorldLabel>();
    }

    // Trade/vendor event polling only. The headless host calls this once per
    // session tick; it never publishes peer heartbeats, so a bot run with its
    // own data directory does not write into the machine-default one.
    internal void Poll()
    {
        AcDream.Runtime.Gameplay.RuntimeTradeAutomation? trade;
        AcDream.Runtime.Gameplay.RuntimeVendorAutomation? vendor;
        lock (_gate)
        {
            trade = _tradeAutomation;
            vendor = _vendorAutomation;
        }
        trade?.Poll();
        vendor?.Poll();
    }

    // Driven from the plugin event tick on both clients.
    private void OnPeerTick(double elapsedSeconds)
    {
        Poll();
        _navigation.PublishSnapshotChanged();
        PublishNavigationChange();

        PumpPeerCommands(elapsedSeconds);

        _peerHeartbeatRemaining -= Math.Max(0d, elapsedSeconds);
        // The heartbeat is for state; a cast or a broadcast line is an event
        // and cannot wait for it. The debounce is what keeps a burst from
        // rewriting the whole note once each, and the note carries
        // everything either way, so one write counts as this period's
        // heartbeat too.
        if (_peerHeartbeatRemaining > 0d
            && !_peers.IsCastWriteDue()
            && !_peers.IsCommandWriteDue())
        {
            return;
        }
        _peerHeartbeatRemaining = PeerHeartbeatSeconds;
        PublishPeerSnapshot();
    }

    /// <summary>
    /// Takes in the broadcast lines the other clients have asked this one to
    /// run, and runs the ones whose wait is up. The client does this itself
    /// rather than leaving it to a plugin: a line sent to this client is a
    /// line this client was asked to run, and a machine where it depended on
    /// which plugins happened to be installed would answer a broadcast
    /// differently from one client to the next.
    /// </summary>
    /// <param name="elapsedSeconds">However long this frame or turn took.</param>
    private void PumpPeerCommands(double elapsedSeconds)
    {
        _peerCommandPollRemaining -= Math.Max(0d, elapsedSeconds);
        if (_peerCommandPollRemaining <= 0d)
        {
            _peerCommandPollRemaining = PeerCommandPollSeconds;
            // A character with no object id is not in the world: it has no
            // name to tell its own broadcasts from anybody else's, and a
            // client that is not playing has nothing to run a line with.
            // The same gate an announcement is held to.
            ICharacterInfo character = this;
            if (character.ObjectId != 0u)
                TakeInPeerCommands();
        }
        RunDuePeerCommands();
    }

    /// <summary>
    /// Reads what is new in the peers' command rings and puts each line in
    /// the queue at the instant this client owes it.
    /// </summary>
    private void TakeInPeerCommands()
    {
        IReadOnlyList<LocalPluginPeerCommand> taken;
        try
        {
            taken = ReadPeerCommands(_deliveredCommandSequence);
        }
        catch (IOException)
        {
            // A peer can replace or remove its own note between the folder
            // scan and the read. It will be read again on the next poll.
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        if (taken.Count == 0)
            return;
        DateTimeOffset now = _peers.UtcNow;
        lock (_gate)
        {
            if (_disposed)
                return;
            foreach (LocalPluginPeerCommand command in taken)
            {
                _deliveredCommandSequence = Math.Max(
                    _deliveredCommandSequence, command.Command.Sequence);
                _pendingPeerCommands.Add(new PendingPeerCommand(
                    now + TimeSpan.FromMilliseconds(
                        command.StaggerMilliseconds),
                    command.Command.Line));
            }
            // The same ceiling the reader keeps for unread lines: a burst
            // nobody could have run is bounded rather than growing with the
            // session.
            if (_pendingPeerCommands.Count
                > LocalPluginPeerRegistry.ObservedCommandCapacity)
            {
                _pendingPeerCommands.RemoveRange(
                    0,
                    _pendingPeerCommands.Count
                        - LocalPluginPeerRegistry.ObservedCommandCapacity);
            }
        }
    }

    /// <summary>
    /// Submits every line whose wait is up through this client's own chat
    /// entry, which is the one door a line the player typed comes in by: the
    /// client's own commands are consulted first, then the plugins' chat
    /// interceptors, then the verbs plugins and the client registered, and
    /// what is left goes to a channel, a tell or the server.
    ///
    /// <para>Offering the line to the verb registry alone -- which is what
    /// this did -- meant a broadcast could only ever run a plugin verb. A
    /// broadcast of a client command, of a server command, or of something
    /// to say was taken from the sender, staggered, and then dropped.</para>
    /// </summary>
    private void RunDuePeerCommands()
    {
        List<string>? due = null;
        lock (_gate)
        {
            // Runs on every tick, and on almost all of them there is nothing
            // waiting, so the queue is asked before the clock is.
            if (_disposed || _pendingPeerCommands.Count == 0)
                return;
            DateTimeOffset now = _peers.UtcNow;
            for (int index = _pendingPeerCommands.Count - 1; index >= 0; index--)
            {
                if (_pendingPeerCommands[index].DueAt > now)
                    continue;
                (due ??= []).Add(_pendingPeerCommands[index].Line);
                _pendingPeerCommands.RemoveAt(index);
            }
        }
        if (due is null)
            return;
        // Oldest first: the loop above walked the queue backwards so a line
        // could be taken out of it as it went.
        due.Reverse();
        foreach (string line in due)
            _ = Submit(line);
    }

    /// <summary>A broadcast line this client has taken, and when it is due.</summary>
    private readonly record struct PendingPeerCommand(
        DateTimeOffset DueAt,
        string Line);

    private void PublishNavigationChange()
    {
        AcDream.Core.Plugins.IPluginEventSink? events = _events;
        if (events is null)
            return;
        PluginGoToReport report = _navigation.GoToReport;
        if (report.Revision == 0L
            || report.Revision == _lastNavigationSequence)
            return;
        _lastNavigationSequence = report.Revision;
        _lastNavigationState = report.State;
        events.FireNavigationChanged(report);
    }

    private void PublishPeerSnapshot()
    {
        if (!TryBuildOwnPeerClient(out PluginNetworkClient self))
        {
            _peers.Withdraw();
            return;
        }

        try
        {
            _peers.Publish(self);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// What this client tells the others on this computer about itself. One
    /// builder for the note that is written and for a plugin asking what
    /// that note says, so the two cannot drift.
    /// </summary>
    private bool TryBuildOwnPeerClient(out PluginNetworkClient self)
    {
        self = default;
        if (!IsAvailable)
            return false;

        ICharacterInfo character = this;
        PluginNavigationSnapshot navigation =
            _navigation.Snapshot;
        if (!navigation.IsAvailable || character.ObjectId == 0u)
            return false;

        string[] tags;
        lock (_gate)
            tags = _peerTags;
        self = new PluginNetworkClient(
            _peers.ClientId,
            character.ObjectId,
            character.Name,
            character.WorldName,
            navigation.Position,
            tags,
            character.CurrentHealth,
            character.CurrentMana,
            character.CurrentStamina,
            character.MaxHealth,
            character.MaxMana,
            character.MaxStamina,
            navigation.Position.HeadingDegrees);
        return true;
    }

    private void OnInventoryRequestCompleted(PendingInventoryRequest request)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _lastInventoryCompletion = new PluginInventoryCompletion(
                ++_inventoryCompletionRevision,
                Project(request.Kind),
                request.ItemId,
                0u);
        }
    }

    private void OnInventoryRequestFailed(
        PendingInventoryRequest request,
        uint weenieError)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _lastInventoryCompletion = new PluginInventoryCompletion(
                ++_inventoryCompletionRevision,
                Project(request.Kind),
                request.ItemId,
                weenieError);
        }
    }

    private static PluginInventoryCommandKind Project(InventoryRequestKind kind) =>
        kind switch
        {
            InventoryRequestKind.Pickup => PluginInventoryCommandKind.Pickup,
            InventoryRequestKind.PutInContainer =>
                PluginInventoryCommandKind.PutInContainer,
            InventoryRequestKind.SplitToContainer =>
                PluginInventoryCommandKind.SplitToContainer,
            InventoryRequestKind.Merge => PluginInventoryCommandKind.Merge,
            InventoryRequestKind.Move => PluginInventoryCommandKind.Move,
            InventoryRequestKind.DropToWorld =>
                PluginInventoryCommandKind.DropToWorld,
            InventoryRequestKind.SplitToWorld =>
                PluginInventoryCommandKind.SplitToWorld,
            InventoryRequestKind.Wield => PluginInventoryCommandKind.Wield,
            InventoryRequestKind.Give => PluginInventoryCommandKind.Give,
            _ => PluginInventoryCommandKind.Unknown,
        };

    private void OnSpellbookChanged() => RebuildSpellbook();

    private void OnEnchantmentsChanged() => RebuildEnchantments();

    private void RebuildSpellbook()
    {
        Spellbook? spellbook;
        lock (_gate)
            spellbook = _spellbook;
        if (spellbook is null)
        {
            _knownSelfBuffs = Array.Empty<PluginSpellInfo>();
            _knownAttackSpells = Array.Empty<PluginSpellInfo>();
            _knownCombatSpells = Array.Empty<PluginSpellInfo>();
            return;
        }

        var buffs = new List<PluginSpellInfo>();
        var attacks = new List<PluginSpellInfo>();
        var combat = new List<PluginSpellInfo>();
        foreach (uint spellId in spellbook.LearnedSpells)
        {
            if (!spellbook.TryGetMetadata(spellId, out SpellMetadata meta))
                continue;
            if (meta.IsOffensive || meta.IsDebuff)
                combat.Add(Project(meta));
            if (!meta.IsBeneficial || meta.IsDebuff || meta.IsUntargeted)
            {
                // MT1 intentionally projects direct attacks only. Rings,
                // debuffs, streaks and harm/martyr policy are MT2, but they
                // remain present in TryGet so later policy can inspect them.
                if (meta.IsOffensive
                    && !meta.IsDebuff
                    && !meta.IsBeneficial
                    && !meta.IsSelfTargeted
                    && !meta.IsUntargeted
                    && meta.TargetMask != 0u)
                {
                    attacks.Add(Project(meta));
                }
                continue;
            }
            // A beneficial spell the caster cannot be the target of (the retail
            // target rule refuses it on the caster) is no self buff, however
            // good it sounds; a plugin handed it would ask for it forever.
            if (!RetailSpellTargetPolicy.CanTargetSelf(meta))
                continue;
            buffs.Add(Project(meta));
        }

        buffs.Sort(static (a, b) =>
            a.Family != b.Family
                ? a.Family.CompareTo(b.Family)
                : b.Tier.CompareTo(a.Tier));
        attacks.Sort(static (a, b) =>
        {
            int tier = b.Tier.CompareTo(a.Tier);
            return tier != 0
                ? tier
                : b.Difficulty.CompareTo(a.Difficulty);
        });
        _knownSelfBuffs = buffs;
        _knownAttackSpells = attacks;
        combat.Sort(static (a, b) =>
        {
            int tier = b.Tier.CompareTo(a.Tier);
            return tier != 0
                ? tier
                : string.CompareOrdinal(a.Name, b.Name);
        });
        _knownCombatSpells = combat;
    }

    private double _enchantmentProjectionTime = double.NaN;

    // The clock must match the timestamp source used when receiving effects.
    private double EnchantmentTime => _runtime?.Clock.SimulationTimeSeconds ?? 0d;

    private void RefreshEnchantmentTime()
    {
        double now = _runtime?.Clock.SimulationTimeSeconds ?? 0d;
        if (now != _enchantmentProjectionTime)
            RebuildEnchantments();
    }

    private void RebuildEnchantments()
    {
        Spellbook? spellbook;
        lock (_gate)
            spellbook = _spellbook;
        double now = EnchantmentTime;
        _enchantmentProjectionTime = _runtime?.Clock.SimulationTimeSeconds ?? 0d;
        if (spellbook is null)
        {
            _enchantments = Array.Empty<PluginActiveEnchantment>();
            _timedEnchantments = Array.Empty<PluginActiveEnchantment>();
            return;
        }

        IReadOnlyList<ActiveEnchantmentRecord> active =
            spellbook.EnchantmentsInEffectSnapshot;
        var built = new List<PluginActiveEnchantment>(active.Count);
        foreach (ActiveEnchantmentRecord record in active)
        {
            uint family = 0;
            int tier = 0;
            if (spellbook.TryGetMetadata(record.SpellId, out SpellMetadata meta))
            {
                family = meta.Family;
                tier = meta.Generation;
            }
            built.Add(new PluginActiveEnchantment(
                record.SpellId, family, tier, Remaining(record, now)));
        }
        _enchantments = built;
        _timedEnchantments = spellbook.ActiveEnchantmentSnapshot
            .Where(record => record.Bucket is 1u or 2u && record.Duration > 0d)
            .Select(record =>
            {
                bool known = spellbook.TryGetMetadata(record.SpellId, out SpellMetadata metadata);
                return new PluginActiveEnchantment(
                    record.SpellId, known ? metadata.Family : 0u,
                    known ? metadata.Generation : 0, Remaining(record, now));
            }).ToArray();
    }

    private static double Remaining(ActiveEnchantmentRecord record, double now)
        => record.Duration < 0d
            ? double.PositiveInfinity
            : Math.Max(0d, record.StartTime + record.Duration - now);

    private static PluginSpellInfo Project(SpellMetadata meta) => new(
        meta.SpellId,
        meta.Name,
        meta.Family,
        meta.Generation,
        meta.Difficulty,
        meta.ManaCost,
        meta.Duration,
        SchoolSkillId(meta.SchoolId),
        meta.Description,
        meta.IsSelfTargeted,
        meta.IsBeneficial)
    {
        IsDebuff = meta.IsDebuff,
        IsOffensive = meta.IsOffensive,
        IsFellowship = meta.IsFellowship,
        IsUntargeted = meta.IsUntargeted,
        RequiresTurnTo = meta.Family is not (>= 222u and <= 235u)
            && !meta.IsUntargeted,
        IsProjectile = meta.IsProjectile,
        IsDamageOverTime = (meta.Flags & (uint)SpellFlags.DamageOverTime) != 0,
        RawFlags = meta.Flags,
        SpellType = meta.SpellType,
        TargetMask = meta.TargetMask,
        BaseRangeConstant = meta.BaseRangeConstant,
        BaseRangeModifier = meta.BaseRangeModifier,
        FormulaComponentIds = meta.FormulaComponents,
        IconId = meta.IconId,
        Saying = meta.Saying,
        ComponentSet = new PluginSpellComponentSet(
            meta.ComponentSet.Herb,
            meta.ComponentSet.Powder,
            meta.ComponentSet.Potion,
            meta.ComponentSet.Talisman),
        CasterEffect = meta.CasterEffect,
        TargetEffect = meta.TargetEffect,
        FormulaVersion = meta.FormulaVersion,
        DisplayOrder = meta.SortKey,
        ComponentLoss = meta.ComponentLoss,
    };

    private static uint SchoolSkillId(MagicSchool school) => school switch
    {
        MagicSchool.CreatureEnchantment => 31u,
        MagicSchool.ItemEnchantment => 32u,
        MagicSchool.LifeMagic => 33u,
        MagicSchool.WarMagic => 34u,
        MagicSchool.VoidMagic => 43u,
        _ => 0u,
    };

    // ── ICharacterInfo ────────────────────────────────────────────────────
    public bool IsInWorld => IsAvailable;

    public string Name
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime is null
                ? string.Empty
                : RuntimeCharacterIdentity.Name(runtime);
        }
    }

    public string WorldName
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime is null
                ? string.Empty
                : RuntimeCharacterIdentity.WorldName(runtime);
        }
    }

    /// <summary>
    /// The population the server reported in its login-time world-name
    /// message, or -1 before that message has arrived. The server never
    /// sends an update after login, so this value is fixed for the rest of
    /// the session even as players come and go.
    /// </summary>
    public int ServerPopulation
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime is null
                ? -1
                : RuntimeCharacterIdentity.ServerPopulation(runtime);
        }
    }

    public string AccountName
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime is null
                ? string.Empty
                : RuntimeCharacterIdentity.AccountName(runtime);
        }
    }

    public int CharacterIndex
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime is null
                ? -1
                : RuntimeCharacterIdentity.CharacterIndex(runtime);
        }
    }

    public int Level
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            if (runtime is null)
                return 0;
            return runtime.InventoryOwner.Objects
                .Get(runtime.PlayerIdentity.ServerGuid)?
                .Properties.GetInt((uint)PropertyInt.Level) ?? 0;
        }
    }

    public int MainPackFreeSlots
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            if (runtime is null)
                return 0;
            uint playerId = runtime.PlayerIdentity.ServerGuid;
            int occupied = runtime.InventoryOwner.Objects.Objects.Count(item =>
                item.ContainerId == playerId
                && ClassifyObject(item) is not (
                    PluginObjectClass.Container or PluginObjectClass.Foci)
                && item.CurrentlyEquippedLocation == 0);
            return Math.Max(0, 102 - occupied);
        }
    }

    public uint ObjectId
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime?.Lifecycle.PlayerGuid ?? 0u;
        }
    }

    public uint CurrentHealth => Vital(LocalPlayerState.VitalKind.Health).Current;
    public uint MaxHealth => Vital(LocalPlayerState.VitalKind.Health).Maximum;
    public uint CurrentStamina => Vital(LocalPlayerState.VitalKind.Stamina).Current;
    public uint MaxStamina => Vital(LocalPlayerState.VitalKind.Stamina).Maximum;
    public uint CurrentMana => Vital(LocalPlayerState.VitalKind.Mana).Current;
    public uint MaxMana => Vital(LocalPlayerState.VitalKind.Mana).Maximum;

    public uint BaseHealth => BaseVital(LocalPlayerState.VitalKind.Health);
    public uint BaseStamina => BaseVital(LocalPlayerState.VitalKind.Stamina);
    public uint BaseMana => BaseVital(LocalPlayerState.VitalKind.Mana);
    public int SummoningMastery
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            if (runtime is null)
                return 0;
            uint playerId = runtime.PlayerIdentity.ServerGuid;
            return runtime.InventoryOwner.Objects.Get(playerId)?.Properties.GetInt(
                (uint)PropertyInt.SummoningMastery) ?? 0;
        }
    }

    /// <summary>
    /// The product of every vitae effect on the character, the same factor
    /// the character's own stat arithmetic applies; 1 with none.
    /// </summary>
    public float VitaeMultiplier
    {
        get
        {
            Spellbook? spellbook;
            lock (_gate)
                spellbook = _disposed ? null : _spellbook;
            return spellbook is null
                ? 1f
                : EnchantmentMath.GetVitaeMultiplier(
                    spellbook.ActiveEnchantmentSnapshot);
        }
    }

    public uint CurrentTitleId
    {
        get
        {
            RuntimeCharacterState? character;
            lock (_gate)
                character = _disposed ? null : _character;
            return character?.Titles.DisplayTitleId ?? 0u;
        }
    }

    public IReadOnlyList<PluginCharacterTitle> Titles
    {
        get
        {
            RuntimeCharacterState? character;
            Func<uint, string?> titleName;
            lock (_gate)
            {
                character = _disposed ? null : _character;
                titleName = _titleName;
            }
            if (character is null)
                return Array.Empty<PluginCharacterTitle>();
            IReadOnlyCollection<uint> earned = character.Titles.EarnedTitleIds;
            if (earned.Count == 0)
                return Array.Empty<PluginCharacterTitle>();
            return earned
                .Select(id => new PluginCharacterTitle(id, titleName(id) ?? string.Empty))
                .ToArray();
        }
    }

    private (uint Current, uint Maximum) Vital(LocalPlayerState.VitalKind kind)
    {
        RuntimeCharacterState? character;
        lock (_gate)
            character = _character;
        if (character is null
            || !character.View.TryGetVital((int)kind, out var vital))
        {
            return (0, 0);
        }
        return (vital.Current, vital.Maximum);
    }

    /// <summary>
    /// The maximum with every enchantment layer off: base attributes, no
    /// vital enchantments.
    /// </summary>
    private uint BaseVital(LocalPlayerState.VitalKind kind)
    {
        RuntimeCharacterState? character;
        lock (_gate)
            character = _character;
        return character?.LocalPlayer.GetBaseMaxApprox(kind)
            ?? Vital(kind).Maximum;
    }

    public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments
    {
        get { RefreshEnchantmentTime(); return _enchantments; }
    }

    private IReadOnlyList<PluginActiveEnchantment> _timedEnchantments =
        Array.Empty<PluginActiveEnchantment>();

    public IReadOnlyList<PluginActiveEnchantment> TimedEnchantments
    {
        get { RefreshEnchantmentTime(); return _timedEnchantments; }
    }

    public IReadOnlyList<PluginSkillInfo> Skills
    {
        get
        {
            RuntimeCharacterState? character;
            IReadOnlyDictionary<uint, string> names;
            IReadOnlyDictionary<uint, uint> icons;
            lock (_gate)
            {
                character = _character;
                names = _skillNames;
                icons = _skillIcons;
            }
            if (character is null || names.Count == 0)
                return Array.Empty<PluginSkillInfo>();

            var built = new List<PluginSkillInfo>(names.Count);
            foreach (KeyValuePair<uint, string> pair in names)
            {
                uint iconId = icons.TryGetValue(pair.Key, out uint icon) ? icon : 0u;
                if (TryProjectSkill(character, pair.Key, pair.Value, iconId, out PluginSkillInfo skill))
                    built.Add(skill);
            }
            built.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            return built;
        }
    }

    public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
    {
        RuntimeCharacterState? character;
        IReadOnlyDictionary<uint, string> names;
        IReadOnlyDictionary<uint, uint> icons;
        lock (_gate)
        {
            character = _character;
            names = _skillNames;
            icons = _skillIcons;
        }
        if (character is not null)
        {
            string name = names.TryGetValue(skillId, out string? n) ? n : string.Empty;
            uint iconId = icons.TryGetValue(skillId, out uint icon) ? icon : 0u;
            return TryProjectSkill(character, skillId, name, iconId, out skill);
        }
        skill = default;
        return false;
    }

    private static bool TryProjectSkill(
        RuntimeCharacterState character, uint skillId, string name, uint iconId,
        out PluginSkillInfo skill)
    {
        if (!character.View.TryGetSkill(skillId, out var snapshot))
        {
            skill = default;
            return false;
        }
        uint baseLevel = snapshot.CurrentLevel;
        uint currentLevel = checked((uint)Math.Max(
            0,
            character.LocalPlayer.GetEffectiveSkill(skillId)
                ?? checked((int)baseLevel)));
        skill = new PluginSkillInfo(
            skillId, name, Training(snapshot.Status), currentLevel)
        {
            Base = baseLevel,
            IconId = iconId,
            Ranks = snapshot.Ranks,
            ExperienceSpent = snapshot.Experience,
        };
        return true;
    }

    private static PluginSkillTraining Training(uint status) => status switch
    {
        1 => PluginSkillTraining.Untrained,
        2 => PluginSkillTraining.Trained,
        3 => PluginSkillTraining.Specialized,
        _ => PluginSkillTraining.Unknown,
    };

    public IReadOnlyList<PluginAttributeInfo> Attributes
    {
        get
        {
            RuntimeCharacterState? character;
            lock (_gate)
                character = _character;
            if (character is null)
                return Array.Empty<PluginAttributeInfo>();

            var built = new List<PluginAttributeInfo>(AttributeNames.Length);
            for (int kind = 0; kind < AttributeNames.Length; kind++)
            {
                if (character.View.TryGetAttribute(kind, out var attribute))
                {
                    uint effective = checked((uint)Math.Max(
                        0,
                        character.LocalPlayer.GetEffectiveAttribute(
                            (LocalPlayerState.AttributeKind)kind)
                            ?? checked((int)attribute.Current)));
                    built.Add(new PluginAttributeInfo(
                        kind, AttributeNames[kind], effective)
                    {
                        Base = attribute.Current,
                        Ranks = attribute.Ranks,
                        ExperienceSpent = attribute.Experience,
                    });
                }
            }
            return built;
        }
    }

    public IReadOnlyList<PluginVitalInfo> Vitals
    {
        get
        {
            var built = new List<PluginVitalInfo>(VitalNames.Length);
            for (int kind = 0; kind < VitalNames.Length; kind++)
            {
                if (TryGetVital(kind, out PluginVitalInfo vital))
                    built.Add(vital);
            }
            return built.Count == 0
                ? Array.Empty<PluginVitalInfo>()
                : built;
        }
    }

    public bool TryGetVital(int kind, out PluginVitalInfo vital)
    {
        RuntimeCharacterState? character;
        lock (_gate)
            character = _character;
        if (character is null
            || (uint)kind >= (uint)VitalNames.Length
            || !character.View.TryGetVital(kind, out RuntimeVitalSnapshot snapshot))
        {
            vital = default;
            return false;
        }

        var pool = (LocalPlayerState.VitalKind)kind;
        vital = new PluginVitalInfo(
            kind,
            VitalNames[kind],
            snapshot.Current,
            snapshot.Maximum)
        {
            // The one number a reader cannot work out from the snapshot: the
            // pool at full with the enchantment layers taken off.
            Base = character.LocalPlayer.GetBaseMaxApprox(pool)
                ?? snapshot.Maximum,
            Ranks = snapshot.Ranks,
            ExperienceSpent = snapshot.Experience,
        };
        return true;
    }

    /// <summary>
    /// Spending on one stat, checked here so both clients refuse the same
    /// requests for the same reasons rather than each finding out on the wire.
    /// </summary>
    public PluginAdvancementResult RequestAdvancement(
        PluginAdvancementKind kind,
        uint statId,
        ulong cost)
    {
        GameRuntime? runtime;
        IGameRuntimeCommands? commands;
        RuntimeCharacterState? character;
        lock (_gate)
        {
            runtime = _runtime;
            commands = _sessionCommands;
            character = _character;
        }
        if (runtime is null || commands is null || character is null
            || !IsAvailable)
        {
            return new(
                PluginAdvancementStatus.Unavailable,
                "the character is not in the world");
        }

        if (!TryMapAdvancementKind(kind, out RuntimeAdvancementKind mapped))
        {
            return new(
                PluginAdvancementStatus.Refused,
                $"{kind} is not a kind this client can spend on");
        }

        // The cost is checked before the stat so a caller that got both wrong
        // is told about the one that is cheapest to fix.
        ulong ceiling = mapped == RuntimeAdvancementKind.TrainSkill
            ? PluginAdvancement.MaxSkillCredits
            : PluginAdvancement.MaxExperienceCost;
        if (cost == 0UL || cost > ceiling)
        {
            return new(
                PluginAdvancementStatus.InvalidCost,
                $"a cost of {cost} is outside 1..{ceiling}");
        }

        if (!IsKnownStat(character, mapped, statId))
        {
            return new(
                PluginAdvancementStatus.UnknownStat,
                $"{statId} names no {kind.ToString().ToLowerInvariant()} "
                + "this character has");
        }

        RuntimeCommandResult result = commands.Character.Advance(
            runtime.Generation,
            new RuntimeAdvancementCommand(mapped, statId, cost));
        return result.Status switch
        {
            RuntimeCommandStatus.Accepted =>
                new(PluginAdvancementStatus.Sent),
            RuntimeCommandStatus.Rejected =>
                new(PluginAdvancementStatus.Refused, "the client declined it"),
            _ => new(
                PluginAdvancementStatus.Unavailable,
                result.Status.ToString()),
        };
    }

    private static bool TryMapAdvancementKind(
        PluginAdvancementKind kind,
        out RuntimeAdvancementKind mapped)
    {
        switch (kind)
        {
            case PluginAdvancementKind.Attribute:
                mapped = RuntimeAdvancementKind.Attribute;
                return true;
            case PluginAdvancementKind.Vital:
                mapped = RuntimeAdvancementKind.Vital;
                return true;
            case PluginAdvancementKind.Skill:
                mapped = RuntimeAdvancementKind.Skill;
                return true;
            case PluginAdvancementKind.TrainSkill:
                mapped = RuntimeAdvancementKind.TrainSkill;
                return true;
            default:
                mapped = default;
                return false;
        }
    }

    /// <summary>
    /// Whether this number names a stat the character really has. A skill is
    /// checked against what the server has said the character carries; an
    /// attribute and a pool are checked against the ones that exist.
    /// </summary>
    private static bool IsKnownStat(
        RuntimeCharacterState character,
        RuntimeAdvancementKind kind,
        uint statId)
    {
        if (statId == 0u)
            return false;
        return kind switch
        {
            RuntimeAdvancementKind.Attribute =>
                LocalPlayerState.AttributeIdToKind(statId) is not null,
            // Only the three "at full" numbers may be spent on; the ids for
            // how much is left name the same pools and are not raisable.
            RuntimeAdvancementKind.Vital => statId is 1u or 3u or 5u,
            _ => character.View.TryGetSkill(statId, out _),
        };
    }

    // ── ISpellCatalog ─────────────────────────────────────────────────────
    public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs => _knownSelfBuffs;
    public IReadOnlyList<PluginSpellInfo> KnownAttackSpells => _knownAttackSpells;
    public IReadOnlyList<PluginSpellInfo> KnownCombatSpells => _knownCombatSpells;

    public bool IsKnown(uint spellId)
    {
        Spellbook? spellbook;
        lock (_gate)
            spellbook = _spellbook;
        return spellbook?.LearnedSpells.Contains(spellId) == true;
    }

    // ── Plugin lifecycle projection ───────────────────────────────────────
    // The runtime keeps one root across reconnects, so an in-world edge is
    // the only honest signal that a plugin has a fresh world to work with.
    void IRuntimeEventObserver.OnLifecycle(in RuntimeLifecycleDelta delta) =>
        ApplyInWorld(delta.Current == RuntimeLifecycleState.InWorld);

    // Plugins hear the logoff while the session is still whole, as the
    // contract promises: the session's objects are released right after this,
    // and a plugin that tracks inventory must not read those releases as the
    // player dropping everything. The lifecycle change that follows the
    // teardown then finds the plugins already told.
    void IRuntimeEventObserver.OnLeavingWorld() => ApplyInWorld(false);

    private void ApplyInWorld(bool isInWorld)
    {
        lock (_gate)
        {
            if (_disposed || _wasInWorld == isInWorld)
                return;
            _wasInWorld = isInWorld;

            // Clear pending activations on logoff.
            if (!isInWorld && _pendingActivationObjectIds.Count > 0)
            {
                long revision = ++_activationRevision;
                foreach (uint objId in _pendingActivationObjectIds.Keys)
                {
                    _lastActivationCompletion = new PluginActivationCompletion(
                        revision, objId, PluginActivationOutcome.Interrupted, 0u);
                    _events?.FireActivationCompleted(_lastActivationCompletion);
                }
                _pendingActivationObjectIds.Clear();
            }
        }

        if (isInWorld)
        {
            _events?.FireLoginComplete();
        }
        else
        {
            _events?.FireLogoff();
            // What the character that left announced is not what the next
            // one says.
            _peers.ForgetOwnAnnouncements();
        }
    }

    void IRuntimeEventObserver.OnCommand(in RuntimeCommandDelta delta) { }

    /// <summary>
    /// Maps the runtime's own entity-lifecycle vocabulary onto the plugin's
    /// narrower one: a cell-crossing position update ("Rebucketed") is a
    /// move; a first sighting ("Registered") is a create; anything that
    /// leaves the object table ("Withdrawn"/"Deleted") is a release. A
    /// temporarily hidden entity ("Hidden") is still tracked, so it reports
    /// as an update rather than a release.
    /// </summary>
    void IRuntimeEventObserver.OnEntity(in RuntimeEntityDelta delta)
    {
        AcDream.Core.Plugins.IPluginEventSink? events = _events;
        if (events is null)
            return;
        PluginObjectChangeKind kind = delta.Change switch
        {
            RuntimeEntityChange.Registered => PluginObjectChangeKind.Created,
            RuntimeEntityChange.Rebucketed => PluginObjectChangeKind.Moved,
            RuntimeEntityChange.Withdrawn => PluginObjectChangeKind.Released,
            RuntimeEntityChange.Deleted => PluginObjectChangeKind.Released,
            _ => PluginObjectChangeKind.Updated,
        };
        uint objectId = delta.Entity.Identity.ServerGuid;
        events.FireObjectChanged(new PluginObjectChange(objectId, kind)
        {
            Current = CaptureCurrentObject(objectId),
        });
    }

    /// <summary>
    /// A bulk container-reset ("Cleared") carries no object id and is not
    /// reported; every other inventory change maps directly onto the
    /// plugin's vocabulary.
    /// </summary>
    void IRuntimeEventObserver.OnInventory(in RuntimeInventoryDelta delta)
    {
        if (delta.Change == RuntimeInventoryChange.Cleared)
            return;

        if (delta.Change == RuntimeInventoryChange.Added
            && delta.Item.EquipLocation != 0u)
        {
            PublishInitialEquipmentPlacement(delta.Item);
        }

        AcDream.Core.Plugins.IPluginEventSink? events = _events;
        if (events is null)
            return;
        PluginObjectChangeKind kind = delta.Change switch
        {
            RuntimeInventoryChange.Added => PluginObjectChangeKind.Created,
            RuntimeInventoryChange.Moved => PluginObjectChangeKind.Moved,
            RuntimeInventoryChange.Removed => PluginObjectChangeKind.Released,
            _ => PluginObjectChangeKind.Updated,
        };
        uint objectId = delta.Item.ObjectId;
        events.FireObjectChanged(new PluginObjectChange(objectId, kind)
        {
            Current = CaptureCurrentObject(objectId),
        });
    }

    private void PublishInitialEquipmentPlacement(
        in RuntimeInventoryItemSnapshot item)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null)
            return;

        uint playerId = runtime.PlayerIdentity.ServerGuid;
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        if (playerId == 0u
            || objects.Get(item.ObjectId) is not { } added
            || !IsPlayerOwned(added, playerId, objects))
        {
            return;
        }

        Action<PluginEquipmentObservation>? handlers;
        lock (_gate)
            handlers = _equipmentPlacementObserved;
        handlers?.Invoke(new PluginEquipmentObservation(
            item.ObjectId,
            item.EquipLocation,
            IsRemoval: false)
        {
            IsInitialPlacement = true,
        });
    }

    void IRuntimeEventObserver.OnChat(in RuntimeChatDelta delta) { }
    void IRuntimeEventObserver.OnMovement(in RuntimeMovementDelta delta) { }
    void IRuntimeEventObserver.OnPortal(in RuntimePortalDelta delta)
    {
        long recallRequestRevision;
        lock (_gate)
        {
            if (_lastPublishedPortalSnapshot is { } previous
                && previous.Equals(delta.Portal))
                return;
            _lastPublishedPortalSnapshot = delta.Portal;
            recallRequestRevision = _pendingRecallRequestRevision;
            _pendingRecallRequestRevision = 0;
        }
        _events?.FirePortalTransition(new PluginPortalTransition(
            Revision: 0,
            Generation: delta.Portal.Generation,
            DestinationCell: delta.Portal.DestinationCell,
            IsReady: delta.Portal.IsReady,
            IsMaterialized: delta.Portal.IsMaterialized,
            IsCompleted: delta.Portal.IsCompleted,
            IsCancelled: delta.Portal.IsCancelled)
        {
            RecallRequestRevision = recallRequestRevision,
            Kind = delta.Portal.Kind switch
            {
                RuntimePortalKind.Login => PluginPortalTransitionKind.Login,
                RuntimePortalKind.Portal => PluginPortalTransitionKind.Portal,
                _ => PluginPortalTransitionKind.Unknown,
            },
        });

        // When a portal transition completes or cancels, resolve any
        // pending activation that may be awaiting this transition.
        if (delta.Portal.IsCompleted || delta.Portal.IsCancelled)
        {
            PluginActivationOutcome outcome = delta.Portal.IsCompleted
                ? PluginActivationOutcome.Completed
                : PluginActivationOutcome.Interrupted;
            lock (_gate)
            {
                if (_pendingActivationObjectIds.Count > 0)
                {
                    // Clear all pending activations -- the transition
                    // resolves any outstanding world interaction.
                    long revision = ++_activationRevision;
                    foreach (uint objId in _pendingActivationObjectIds.Keys)
                    {
                        _lastActivationCompletion = new PluginActivationCompletion(
                            revision,
                            objId,
                            outcome,
                            WeenieError: 0u);
                        _events?.FireActivationCompleted(_lastActivationCompletion);
                    }
                    _pendingActivationObjectIds.Clear();
                }

                // When a portal transition completes with a correlated recall
                // request, learn the destination as a recall location.
                if (delta.Portal.IsCompleted && recallRequestRevision > 0
                    && _lastSuccessfulRecallKind is { } kind)
                {
                    GameRuntime? rt = _runtime;
                    if (rt is not null)
                        CaptureRecallLocation(kind, rt, recallRequestRevision, delta.Portal.DestinationCell);
                    _lastSuccessfulRecallKind = null;
                }
            }
        }
    }
    void IRuntimeEventObserver.OnCombat(in RuntimeCombatDelta delta) { }

    private void OnLocalPlayerDied(string deathMessage) =>
        _events?.FireLocalPlayerDied(deathMessage);

    private void OnExternalContainerChanged(ExternalContainerTransition transition)
    {
        AcDream.Core.Plugins.IPluginEventSink? events = _events;
        if (events is null)
            return;
        switch (transition.Kind)
        {
            case ExternalContainerTransitionKind.Opened:
                events.FireContainerOpened(transition.ContainerId);
                break;
            case ExternalContainerTransitionKind.ReplacementRequested:
            case ExternalContainerTransitionKind.Closed:
            case ExternalContainerTransitionKind.Reset:
                if (transition.PreviousContainerId != 0u)
                    events.FireContainerClosed(transition.PreviousContainerId);
                break;
        }
    }

    private void OnUseCompleted(uint _)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null)
            return;
        RuntimeItemUseCompletion completion =
            runtime.ActionOwner.Transactions.LastItemUseCompletion;
        _events?.FireItemUseCompleted(new PluginItemUseCompletion(
            completion.Revision,
            completion.SourceObjectId,
            completion.TargetObjectId,
            completion.WeenieError));
    }

    private void OnAppraisalReceived(uint objectId) =>
        _events?.FireObjectChanged(new PluginObjectChange(
            objectId,
            PluginObjectChangeKind.IdentReceived)
        {
            Current = CaptureCurrentObject(objectId),
        });

    private PluginWorldObject? CaptureCurrentObject(uint objectId)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || objectId == 0u)
            return null;
        runtime.EntityObjects.Entities.TryGetActive(
            objectId,
            out RuntimeEntityRecord? record);
        ClientObject? item = runtime.InventoryOwner.Objects.Get(objectId);
        if (record is null && item is null)
            return null;
        return ProjectWorldObject(
            runtime,
            record,
            item,
            runtime.PlayerIdentity.ServerGuid);
    }

    // ── IDialogAutomation ────────────────────────────────────────────────
    IDialogAutomation IAutomationSurface.Dialogs => this;

    bool IDialogAutomation.Answer(uint contextId, bool accept)
    {
        Func<uint, bool, bool>? answer;
        lock (_gate)
            answer = _answerConfirmation;
        return answer?.Invoke(contextId, accept) ?? false;
    }

    public void BindDialogs(Func<uint, bool, bool> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        lock (_gate)
            _answerConfirmation = answer;
    }

    /// <summary>Called by the host whenever it shows a confirmation dialog.</summary>
    public void RaiseConfirmationRequested(PluginConfirmation confirmation) =>
        _events?.FireConfirmationRequested(confirmation);

    public IReadOnlyList<PluginSpellInfo> All
    {
        get
        {
            Spellbook? spellbook;
            lock (_gate)
                spellbook = _spellbook;
            SpellTable? table = spellbook?.Metadata;
            if (table is null)
                return Array.Empty<PluginSpellInfo>();

            lock (_gate)
            {
                if (ReferenceEquals(_spellCatalogSource, table))
                    return _allSpells;
            }

            var projected = new List<PluginSpellInfo>(table.Count);
            foreach (uint spellId in table.SpellIds)
            {
                if (table.TryGet(spellId, out SpellMetadata meta))
                    projected.Add(Project(meta));
            }
            PluginSpellInfo[] built = projected.ToArray();

            lock (_gate)
            {
                _spellCatalogSource = table;
                _allSpells = built;
            }
            return built;
        }
    }

    public bool TryGet(uint spellId, out PluginSpellInfo info)
    {
        Spellbook? spellbook;
        lock (_gate)
            spellbook = _spellbook;
        if (spellbook is not null
            && spellbook.TryGetMetadata(spellId, out SpellMetadata meta))
        {
            info = Project(meta);
            return true;
        }
        info = default;
        return false;
    }

    public bool TryGetComponent(uint componentId, out PluginSpellComponentInfo info)
    {
        MagicCatalog catalog;
        lock (_gate)
            catalog = _magicCatalog;
        if (catalog.TryGetComponentBySpellComponentId(
                componentId,
                out SpellComponentDescriptor descriptor))
        {
            info = new PluginSpellComponentInfo(
                descriptor.SpellComponentId,
                descriptor.WeenieClassId,
                descriptor.Name,
                descriptor.BurnRate,
                descriptor.GestureId,
                descriptor.GestureSpeed,
                descriptor.IconId,
                descriptor.Category,
                descriptor.Type,
                descriptor.Word);
            return true;
        }
        info = default;
        return false;
    }

    // ── IPluginChat ───────────────────────────────────────────────────────
    public IReadOnlyList<PluginChatMessage> CaptureMessages(ulong afterSequence)
    {
        lock (_gate)
        {
            if (_chatMessages.Count == 0)
                return Array.Empty<PluginChatMessage>();
            var result = new List<PluginChatMessage>();
            foreach (PluginChatMessage message in _chatMessages)
            {
                if (message.Sequence > afterSequence)
                    result.Add(message);
            }
            return result.Count == 0
                ? Array.Empty<PluginChatMessage>()
                : result.ToArray();
        }
    }

    public double GetCooldownRemaining(uint cooldownId)
    {
        Spellbook? spellbook;
        GameRuntime? runtime;
        lock (_gate)
        {
            spellbook = _spellbook;
            runtime = _runtime;
        }
        if (spellbook is null || runtime is null || cooldownId == 0u)
            return 0d;
        return spellbook.OnCooldown(
            cooldownId,
            EnchantmentTime,
            out double remaining)
                ? Math.Max(0d, remaining)
                : 0d;
    }

    public event Action<PluginChatLinkClicked> LinkClicked
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
                _chatLinkClicked += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_gate)
                _chatLinkClicked -= value;
        }
    }

    internal void RaiseChatLinkClicked(PluginChatLinkClicked link)
    {
        Action<PluginChatLinkClicked>? handlers;
        lock (_gate)
            handlers = _chatLinkClicked;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginChatLinkClicked>)handler)(link); }
            catch { /* plugin errors do not propagate out of event dispatch */ }
        }
    }

    public event Action<PluginChatMessage> Received
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
                _chatReceived += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_gate)
                _chatReceived -= value;
        }
    }

    public IDisposable RegisterFilter(Func<PluginChatMessage, bool> suppress)
    {
        ArgumentNullException.ThrowIfNull(suppress);
        return _chatFilters.Register(suppress);
    }

    public IDisposable RegisterInputInterceptor(
        Func<string, PluginChatInputDecision> intercept)
    {
        ArgumentNullException.ThrowIfNull(intercept);
        return _chatInterceptors.Register(intercept);
    }

    public void OnChat(in RuntimeCommunicationEvent delta)
    {
        PluginChatMessage message;
        Action<PluginChatMessage>? handlers;
        lock (_gate)
        {
            if (_disposed || _communication is null)
                return;
            RuntimeChatEntry entry = delta.Entry;
            message = new PluginChatMessage(
                ++_pluginChatSequence,
                entry.SenderGuid,
                entry.Kind,
                entry.Sender,
                entry.Text,
                entry.ChannelName)
            {
                LogTextType = entry.LogTextType,
                CombatKind = entry.CombatKind,
                Received = entry.Received,
                ChannelId = entry.ChannelId,
                DisplayText = entry.DisplayText,
            };
            _chatMessages.Add(message);
            if (_chatMessages.Count > MaximumPluginChatMessages)
            {
                _chatMessages.RemoveRange(
                    0,
                    _chatMessages.Count - MaximumPluginChatMessages);
            }
            handlers = _chatReceived;
        }

        // Raised outside the lock and in arrival order, so a handler is free
        // to call back into the surface.
        Raise(handlers, message);
    }

    private static void Raise(
        Action<PluginChatMessage>? handlers,
        in PluginChatMessage message)
    {
        if (handlers is null)
            return;
        PluginChatMessage copy = message;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginChatMessage>)handler)(copy); }
            catch { /* plugin errors don't propagate out of event dispatch */ }
        }
    }

    public void PostSystemMessage(string text) =>
        PostMessage(text, (int)RetailLogTextType.Default);

    public void PostMessage(string text, int logTextType)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return;
        RuntimeCommunicationState? communication;
        lock (_gate)
            communication = _communication;
        communication?.AddText(
            text,
            RetailLogTextTypeCodec.FromPluginValue(logTextType));
    }

    public bool IsInputActive
    {
        get
        {
            Func<bool>? isActive;
            lock (_gate)
                isActive = _disposed ? null : _chatInputActive;
            return isActive?.Invoke() == true;
        }
    }

    public bool Compose(string text)
    {
        Func<string, bool>? compose;
        lock (_gate)
            compose = _disposed ? null : _composeChat;
        return compose?.Invoke(text) == true;
    }

    public bool Submit(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        Func<string, bool>? submitChatText;
        lock (_gate)
            submitChatText = _submitChatText;
        return submitChatText?.Invoke(text) == true;
    }

    bool ISelectionAutomation.Execute(PluginSelectionAction action)
    {
        Func<PluginSelectionAction, bool>? execute;
        lock (_gate)
            execute = _disposed ? null : _selectionAction;
        return execute?.Invoke(action) == true;
    }

    // ── IProjectileAutomation ───────────────────────────────────────────
    bool IProjectileAutomation.IsAvailable
    {
        get
        {
            lock (_gate)
                return !_disposed && IsAvailable;
        }
    }

    PluginProjectilePathResult IProjectileAutomation.EvaluatePath(
        uint targetObjectId,
        PluginProjectilePathKind kind,
        PluginAttackHeight targetHeight,
        float projectileRadius,
        float stepDistance,
        int maximumCollisionChecks) => EvaluateProjectilePathRequest(
            targetObjectId,
            kind,
            targetHeight,
            projectileRadius,
            stepDistance,
            maximumCollisionChecks,
            captureDiagnostics: false);

    PluginProjectilePathResult IProjectileAutomation.EvaluatePathWithDiagnostics(
        uint targetObjectId,
        PluginProjectilePathKind kind,
        PluginAttackHeight targetHeight,
        float projectileRadius,
        float stepDistance,
        int maximumCollisionChecks) => EvaluateProjectilePathRequest(
            targetObjectId,
            kind,
            targetHeight,
            projectileRadius,
            stepDistance,
            maximumCollisionChecks,
            captureDiagnostics: true);

    void IProjectileAutomation.ShowDebugSamples(
        IReadOnlyList<PluginProjectileDebugSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        const int maximumMarkers = 4096;
        var detached = new List<PluginProjectileDebugSample>(
            Math.Min(samples.Count, maximumMarkers));
        for (int index = 0; index < samples.Count && index < maximumMarkers; index++)
        {
            PluginProjectileDebugSample sample = samples[index];
            if (!float.IsFinite(sample.WorldPosition.X)
                || !float.IsFinite(sample.WorldPosition.Y)
                || !float.IsFinite(sample.WorldPosition.Z)
                || !float.IsFinite(sample.Radius)
                || sample.Radius <= 0f)
            {
                continue;
            }
            detached.Add(sample);
        }
        lock (_gate)
        {
            if (_disposed)
                return;
            _projectileDebugSamples = detached.Count == 0
                ? Array.Empty<PluginProjectileDebugSample>()
                : detached.ToArray();
            _projectileDebugSamplesExpireAt = Environment.TickCount64 + 350;
        }
    }

    internal IReadOnlyList<PluginProjectileDebugSample>
        CaptureProjectileDebugSamples()
    {
        lock (_gate)
        {
            if (_disposed
                || Environment.TickCount64 > _projectileDebugSamplesExpireAt)
            {
                return Array.Empty<PluginProjectileDebugSample>();
            }
            return _projectileDebugSamples;
        }
    }

    // ── IWorldLabelAutomation ───────────────────────────────────────────
    // The same shape as the projectile markers: a plugin pushes a whole
    // set, the surface keeps a detached copy, and whichever host has
    // something to draw with reads it back once a frame. A host with no
    // window keeps the set too and simply never asks for it.

    bool IWorldLabelAutomation.ShowLabels(IReadOnlyList<PluginWorldLabel> labels) =>
        ShowWorldLabels(UnscopedWorldLabelOwner, labels);

    IWorldLabelAutomation IScopedWorldLabelSource.ScopeTo(string ownerId)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        return new OwnedWorldLabels(this, ownerId);
    }

    void IScopedWorldLabelSource.Release(string ownerId)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        lock (_gate)
        {
            if (_worldLabels.Remove(ownerId))
                _worldLabelsMerged = null;
        }
    }

    /// <summary>
    /// Takes one owner's whole label set. A set over the cap is refused
    /// unchanged rather than trimmed, so the plugin learns it asked for too
    /// much; an unusable label inside an acceptable set is dropped quietly,
    /// since one bad entry should not cost the other two hundred.
    /// </summary>
    internal bool ShowWorldLabels(string ownerId, IReadOnlyList<PluginWorldLabel> labels)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(labels);
        if (labels.Count > IWorldLabelAutomation.MaximumLabels)
            return false;

        var detached = new List<PluginWorldLabel>(labels.Count);
        for (int index = 0; index < labels.Count; index++)
        {
            PluginWorldLabel label = labels[index];
            if (label.ObjectId == 0u
                || string.IsNullOrEmpty(label.Text)
                || label.Text.Length > IWorldLabelAutomation.MaximumTextLength
                || !float.IsFinite(label.Color.X)
                || !float.IsFinite(label.Color.Y)
                || !float.IsFinite(label.Color.Z)
                || !float.IsFinite(label.Color.W)
                || !float.IsFinite(label.MaxRange)
                || label.MaxRange <= 0f
                || !float.IsFinite(label.HeightOffset))
            {
                continue;
            }
            detached.Add(label);
        }

        lock (_gate)
        {
            if (_disposed)
                return false;
            if (detached.Count == 0)
                _worldLabels.Remove(ownerId);
            else
                _worldLabels[ownerId] = detached.ToArray();
            _worldLabelsMerged = null;
        }
        return true;
    }

    /// <summary>
    /// Every label every plugin has showing, in the order the owners pushed
    /// them. The array is shared between calls until a set changes, so a
    /// reader iterates it and never holds on to it.
    /// </summary>
    internal IReadOnlyList<PluginWorldLabel> CaptureWorldLabels()
    {
        lock (_gate)
        {
            if (_disposed)
                return Array.Empty<PluginWorldLabel>();
            if (_worldLabelsMerged is { } merged)
                return merged;
            int total = 0;
            foreach (PluginWorldLabel[] set in _worldLabels.Values)
                total += set.Length;
            if (total == 0)
                return _worldLabelsMerged = Array.Empty<PluginWorldLabel>();
            var all = new PluginWorldLabel[total];
            int cursor = 0;
            foreach (PluginWorldLabel[] set in _worldLabels.Values)
            {
                set.CopyTo(all, cursor);
                cursor += set.Length;
            }
            return _worldLabelsMerged = all;
        }
    }

    private sealed class OwnedWorldLabels(RuntimeAutomationSurface surface, string ownerId)
        : IWorldLabelAutomation
    {
        public bool ShowLabels(IReadOnlyList<PluginWorldLabel> labels) =>
            surface.ShowWorldLabels(ownerId, labels);
    }

    private PluginProjectilePathResult EvaluateProjectilePathRequest(
        uint targetObjectId,
        PluginProjectilePathKind kind,
        PluginAttackHeight targetHeight,
        float projectileRadius,
        float stepDistance,
        int maximumCollisionChecks,
        bool captureDiagnostics)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable)
            return new(PluginProjectilePathStatus.Unavailable);
        // The flight is tested against the collision world the session's own
        // bodies move through, so every client answers from the same place.
        PhysicsEngine physics = runtime.EntityObjects.Physics.Engine;
        if (targetObjectId == 0u
            || !float.IsFinite(projectileRadius)
            || projectileRadius <= 0f
            || !float.IsFinite(stepDistance)
            || stepDistance <= 0f
            || maximumCollisionChecks <= 0)
        {
            return new(PluginProjectilePathStatus.InvalidTarget);
        }

        uint localId = runtime.PlayerIdentity.ServerGuid;
        // The target is wherever the rest of the client takes it to be: its
        // body when it has one, and the server's last word about it when it
        // has stood still since it came into view and has none yet.
        if (!runtime.EntityObjects.Entities.TryGetActive(
                localId,
                out RuntimeEntityRecord local)
            || local.PhysicsBody is not { } localBody
            || localBody.CellPosition.ObjCellId == 0u
            || runtime.EntityObjects.Physics.InteractionTargetPosition(
                targetObjectId) is not { } targetPosition)
        {
            return new(PluginProjectilePathStatus.InvalidTarget);
        }

        // A flight through a place whose collision data is not loaded would
        // meet nothing and read as clear. That is no answer, and it is said so.
        uint startCell = localBody.CellPosition.ObjCellId;
        bool worldIsLoaded = (startCell & 0xFFFFu) >= 0x0100u
            ? physics.IsSpawnCellReady(startCell)
            : physics.IsLandblockTerrainResident(startCell);
        if (!worldIsLoaded)
            return new(PluginProjectilePathStatus.Unavailable);

        try
        {
            return EvaluateProjectilePath(
                physics,
                localId,
                localBody,
                targetObjectId,
                targetPosition,
                kind,
                targetHeight,
                projectileRadius,
                stepDistance,
                maximumCollisionChecks,
                captureDiagnostics);
        }
        catch (Exception error)
        {
            return new(
                PluginProjectilePathStatus.Error,
                Notice: error.GetBaseException().Message);
        }
    }

    private static PluginProjectilePathResult EvaluateProjectilePath(
        PhysicsEngine physics,
        uint localObjectId,
        PhysicsBody local,
        uint targetObjectId,
        System.Numerics.Vector3 targetPosition,
        PluginProjectilePathKind kind,
        PluginAttackHeight targetHeight,
        float radius,
        float stepDistance,
        int maximumChecks,
        bool captureDiagnostics)
    {
        System.Numerics.Vector3 baseDelta = targetPosition - local.Position;
        var horizontal = new System.Numerics.Vector2(baseDelta.X, baseDelta.Y);
        float horizontalDistance = horizontal.Length();
        if (!float.IsFinite(horizontalDistance)
            || horizontalDistance <= PhysicsGlobals.EPSILON)
        {
            return new(PluginProjectilePathStatus.InvalidTarget);
        }

        System.Numerics.Vector2 direction = horizontal / horizontalDistance;
        float sourceForward = kind switch
        {
            PluginProjectilePathKind.Arc => 0.44f,
            PluginProjectilePathKind.Missile => 0.61f,
            _ => 0.66f,
        };
        float sourceHeight = kind == PluginProjectilePathKind.Arc ? 1.8f : 1.2f;
        float targetHeightMeters = targetHeight switch
        {
            PluginAttackHeight.Low => 0.3f,
            PluginAttackHeight.High => 1.5f,
            _ => 0.9f,
        };
        var current = local.Position + new System.Numerics.Vector3(
            direction.X * sourceForward,
            direction.Y * sourceForward,
            sourceHeight);
        var destination = targetPosition + new System.Numerics.Vector3(
            0f,
            0f,
            targetHeightMeters);
        System.Numerics.Vector3 delta = destination - current;
        horizontal = new System.Numerics.Vector2(delta.X, delta.Y);
        horizontalDistance = horizontal.Length();
        if (horizontalDistance <= PhysicsGlobals.EPSILON)
            return new(PluginProjectilePathStatus.Clear);
        direction = horizontal / horizontalDistance;

        float speed = kind switch
        {
            PluginProjectilePathKind.Arc => 37.5185f,
            PluginProjectilePathKind.Missile => 46f,
            _ => 100f,
        };
        float totalTime = horizontalDistance / speed;
        float verticalSpeed = kind == PluginProjectilePathKind.Straight
            ? delta.Z / totalTime
            : (delta.Z + 4.9f * totalTime * totalTime) / totalTime;
        var velocity = new System.Numerics.Vector3(
            direction.X * speed,
            direction.Y * speed,
            verticalSpeed);
        float elapsed = 0f;
        uint cellId = local.CellPosition.ObjCellId;
        var probeBody = new PhysicsBody
        {
            State = PhysicsStateFlags.Missile
                | PhysicsStateFlags.Inelastic
                | PhysicsStateFlags.ReportCollisions,
        };
        List<PluginProjectileDebugSample>? debugSamples = captureDiagnostics
            ? new List<PluginProjectileDebugSample>(
                Math.Min(maximumChecks, 512))
            : null;

        for (int check = 1; check <= maximumChecks; check++)
        {
            float remaining = MathF.Max(0f, totalTime - elapsed);
            if (remaining <= PhysicsGlobals.EPSILON)
            {
                return WithProjectileDebugSamples(
                    new(PluginProjectilePathStatus.Clear, check - 1),
                    debugSamples);
            }
            float velocityMagnitude = velocity.Length();
            if (!float.IsFinite(velocityMagnitude)
                || velocityMagnitude <= PhysicsGlobals.EPSILON)
            {
                return WithProjectileDebugSamples(
                    new(
                        PluginProjectilePathStatus.Error,
                        check - 1,
                        Notice: "The projectile trajectory became invalid."),
                    debugSamples);
            }
            float quantum = MathF.Min(remaining, stepDistance / velocityMagnitude);
            System.Numerics.Vector3 next = current + velocity * quantum;
            if (quantum >= remaining - PhysicsGlobals.EPSILON)
                next = destination;

            ResolveResult resolved = physics.ResolveWithTransition(
                current,
                next,
                cellId,
                radius,
                sphereHeight: 0f,
                stepUpHeight: 0f,
                stepDownHeight: 0f,
                isOnGround: false,
                body: probeBody,
                moverFlags: ObjectInfoState.PathClipped,
                movingEntityId: localObjectId,
                localSphereOrigin: System.Numerics.Vector3.Zero,
                designatedTargetId: targetObjectId);
            float requestedDistance = System.Numerics.Vector3.Distance(current, next);
            float deliveredDistance = System.Numerics.Vector3.Distance(
                current,
                resolved.Position);
            bool stopped = !resolved.Ok
                || resolved.CollidedWithEnvironment
                || resolved.LastCollidedObjectId != 0u
                || resolved.CollisionNormalValid
                || deliveredDistance + 0.01f < requestedDistance;
            bool targetHit = resolved.LastCollidedObjectId == targetObjectId;
            debugSamples?.Add(new PluginProjectileDebugSample(
                resolved.Position,
                targetHit || !stopped,
                radius));
            if (targetHit)
            {
                return WithProjectileDebugSamples(
                    new(
                        PluginProjectilePathStatus.Clear,
                        check,
                        targetObjectId),
                    debugSamples);
            }
            if (stopped)
            {
                return WithProjectileDebugSamples(
                    new(
                        PluginProjectilePathStatus.Blocked,
                        check,
                        resolved.LastCollidedObjectId),
                    debugSamples);
            }

            current = resolved.Position;
            cellId = resolved.CellId;
            elapsed += quantum;
            if (kind != PluginProjectilePathKind.Straight)
                velocity.Z -= 9.8f * quantum;
        }

        return WithProjectileDebugSamples(
            new(
                PluginProjectilePathStatus.BudgetExceeded,
                maximumChecks,
                Notice: "The projectile collision-check budget was exhausted."),
            debugSamples);
    }

    private static PluginProjectilePathResult WithProjectileDebugSamples(
        PluginProjectilePathResult result,
        List<PluginProjectileDebugSample>? samples) => samples is null
            ? result
            : result with { DebugSamples = samples.ToArray() };

    internal static PluginNavigationPosition ProjectNavigationPosition(
        Position position) =>
        AcDream.Runtime.Navigation.RuntimeNavigationProjection.Position(position);

    // ── IWorldObjectAutomation ────────────────────────────────────────────
    bool IWorldObjectAutomation.IsAvailable => IsAvailable;

    bool IRecallAutomation.IsAvailable => IsAvailable;

    bool IAllegianceAutomation.IsAvailable => IsAvailable;

    PluginActivationCompletion IWorldObjectAutomation.LastActivationCompletion
    {
        get { lock (_gate) return _lastActivationCompletion; }
    }

    PluginAllegianceSnapshot IAllegianceAutomation.Snapshot
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            if (runtime is null || !IsAvailable)
                return default;
            IRuntimeAllegianceView allegiance = runtime.Allegiance;
            RuntimeAllegianceSnapshot snapshot = allegiance.Snapshot;
            uint self = runtime.PlayerIdentity.ServerGuid;
            return new PluginAllegianceSnapshot(
                snapshot.Revision,
                snapshot.HasProfile,
                snapshot.AllegianceName,
                snapshot.Rank,
                snapshot.TotalMembers,
                snapshot.TotalVassals,
                snapshot.MonarchGuid)
            {
                Monarch = allegiance.TryGetMonarch(
                    out RuntimeAllegianceMemberSnapshot monarch)
                    ? ProjectAllegianceMember(monarch)
                    : null,
                Patron = self != 0u
                    && allegiance.TryGetPatron(
                        self, out RuntimeAllegianceMemberSnapshot patron)
                    ? ProjectAllegianceMember(patron)
                    : null,
                Vassals = self == 0u
                    ? Array.Empty<PluginAllegianceMember>()
                    : allegiance.GetVassals(self)
                        .Select(ProjectAllegianceMember)
                        .ToArray(),
            };
        }
    }

    private static PluginAllegianceMember ProjectAllegianceMember(
        RuntimeAllegianceMemberSnapshot member) => new(
            member.CharacterId,
            member.Name,
            member.Rank,
            member.Level,
            member.HeritageGroup,
            member.Gender,
            member.IsLoggedIn);

    // Swearing is done face to face: the patron has to be a player the
    // client can see standing there, which is the same thing the client's
    // own panel requires before it offers the command at all.
    PluginAllegianceCommandResult IAllegianceAutomation.Swear(uint patronObjectId)
    {
        GameRuntime? runtime;
        IGameRuntimeCommands? commands;
        lock (_gate)
        {
            runtime = _runtime;
            commands = _sessionCommands;
        }
        if (runtime is null || commands is null || !IsAvailable)
            return new(PluginAllegianceCommandStatus.Unavailable);
        if (patronObjectId == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(patronObjectId, out _)
            || ClassifyObject(runtime.InventoryOwner.Objects.Get(patronObjectId))
                != PluginObjectClass.Player)
        {
            return new(
                PluginAllegianceCommandStatus.InvalidTarget,
                "Swearing needs another player the client can see.");
        }
        return ProjectAllegiance(
            commands.Allegiance.Swear(runtime.Generation, patronObjectId));
    }

    // Breaking does not: a patron may be a continent away or logged out, and
    // the tie is still there to break. What it does need is that the target
    // really is in this character's allegiance, which is the list the server
    // sent.
    PluginAllegianceCommandResult IAllegianceAutomation.Break(uint targetObjectId)
    {
        GameRuntime? runtime;
        IGameRuntimeCommands? commands;
        lock (_gate)
        {
            runtime = _runtime;
            commands = _sessionCommands;
        }
        if (runtime is null || commands is null || !IsAvailable)
            return new(PluginAllegianceCommandStatus.Unavailable);
        if (targetObjectId == 0u
            || !runtime.Allegiance.TryGetMember(targetObjectId, out _))
        {
            return new(
                PluginAllegianceCommandStatus.InvalidTarget,
                "Only someone in the character's allegiance can be broken from.");
        }
        return ProjectAllegiance(
            commands.Allegiance.Break(runtime.Generation, targetObjectId));
    }

    // A command the adapter turned down is a statement about the session, not
    // about the target: the target was already checked above, and by the time
    // the adapter sees the command it is only deciding whether this client can
    // send anything at all. Reporting that as a wrong target would send a
    // plugin off looking for a different patron over something that has
    // nothing to do with who was named.
    private static PluginAllegianceCommandResult ProjectAllegiance(
        RuntimeCommandResult result) =>
        result.Status switch
        {
            RuntimeCommandStatus.Accepted =>
                new(PluginAllegianceCommandStatus.Sent),
            RuntimeCommandStatus.Rejected =>
                new(
                    PluginAllegianceCommandStatus.Refused,
                    "The client did not send the command."),
            _ => new(PluginAllegianceCommandStatus.Unavailable),
        };

    PluginRecallRequest IRecallAutomation.LastRequest
    {
        get { lock (_gate) return _lastRecallRequest; }
    }

    IReadOnlyList<PluginRecallLocation> IRecallAutomation.CaptureLocations()
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable)
            return Array.Empty<PluginRecallLocation>();

        var results = new List<PluginRecallLocation>(5);

        // House location from the house owner data.
        AcDream.Core.Net.Messages.CreateObject.ServerPosition? house =
            runtime.HouseOwner.Position;
        AcDream.Core.Physics.Position? position =
            RuntimeWorldObjectProjection.ConvertPosition(house);
        if (position is not null)
        {
            results.Add(new PluginRecallLocation(
                PluginRecallKind.House,
                RuntimeWorldObjectProjection.ProjectNavigationPosition(position.Value),
                "House",
                runtime.HouseOwner.Revision,
                true));
        }

        // Learned recall locations.
        if (_lifestoneLocation is { } ls && ls.IsKnown)
            results.Add(ls);
        if (_marketplaceLocation is { } mp && mp.IsKnown)
            results.Add(mp);
        if (_mansionLocation is { } mn && mn.IsKnown)
            results.Add(mn);
        if (_allegianceLocation is { } al && al.IsKnown)
            results.Add(al);

        return results;
    }

    PluginRecallResult IRecallAutomation.Recall(PluginRecallKind kind)
    {
        GameRuntime? runtime;
        IGameRuntimeCommands? commands;
        lock (_gate)
        {
            runtime = _runtime;
            commands = _sessionCommands;
        }
        if (runtime is null || commands is null || !IsAvailable)
            return new(PluginRecallStatus.Unavailable);

        RuntimePortalCommand command = kind switch
        {
            PluginRecallKind.Lifestone => RuntimePortalCommand.RecallLifestone,
            PluginRecallKind.Marketplace => RuntimePortalCommand.RecallMarketplace,
            PluginRecallKind.House => RuntimePortalCommand.RecallHouse,
            PluginRecallKind.Mansion => RuntimePortalCommand.RecallMansion,
            PluginRecallKind.Allegiance => RuntimePortalCommand.RecallAllegiance,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        RuntimeCommandResult result = commands.Portal.Execute(runtime.Generation, command);
        PluginRecallStatus status = result.Status switch
        {
            RuntimeCommandStatus.Accepted => PluginRecallStatus.Started,
            RuntimeCommandStatus.Unsupported => PluginRecallStatus.Unsupported,
            _ => PluginRecallStatus.Refused,
        };
        lock (_gate)
        {
            _lastRecallRequest = new PluginRecallRequest(
                ++_recallRequestRevision,
                kind,
                status);
            _pendingRecallRequestRevision = status == PluginRecallStatus.Started
                ? _lastRecallRequest.Revision
                : 0;
            if (status == PluginRecallStatus.Started)
                _lastSuccessfulRecallKind = kind;
        }
        return new(status, status == PluginRecallStatus.Refused
            ? result.Status.ToString()
            : null);
    }

    private void CaptureRecallLocation(
        PluginRecallKind kind, GameRuntime runtime, long revision, uint destinationCell)
    {
        // Learn the arrival position after a successful recall.
        // For marketplace, use a known fixed location since the
        // destination is always the same.
        if (kind == PluginRecallKind.Marketplace)
        {
            // Marketplace town center. The cell is 0x0166 in the
            // original client; the position is the standard arrival
            // point after a marketplace recall.
            _marketplaceLocation = new PluginRecallLocation(
                PluginRecallKind.Marketplace,
                new PluginNavigationPosition(
                    CellId: 0x0166u,
                    EastWest: 0.05, NorthSouth: 0.05, Elevation: 0.0,
                    HeadingDegrees: 0.0f,
                    IsOutdoor: true),
                "Marketplace",
                ++_marketplaceRevision,
                true);
            return;
        }

        // For house, use the data already available from the house owner.
        if (kind == PluginRecallKind.House)
        {
            // Already tracked by CaptureLocations() from runtime.HouseOwner.
            return;
        }

        // For mansion, check if the house data has a mansion position.
        if (kind == PluginRecallKind.Mansion)
        {
            AcDream.Core.Net.Messages.CreateObject.ServerPosition? house =
                runtime.HouseOwner.Position;
            AcDream.Core.Physics.Position? position =
                RuntimeWorldObjectProjection.ConvertPosition(house);
            if (position is not null)
            {
                _mansionLocation = new PluginRecallLocation(
                    PluginRecallKind.Mansion,
                    RuntimeWorldObjectProjection.ProjectNavigationPosition(position.Value),
                    "Mansion",
                    ++_mansionRevision,
                    true);
            }
            return;
        }

        // For lifestone, capture the destination cell from the portal transition.
        if (kind == PluginRecallKind.Lifestone && destinationCell != 0u)
        {
            _lifestoneLocation = new PluginRecallLocation(
                PluginRecallKind.Lifestone,
                new PluginNavigationPosition(
                    CellId: destinationCell,
                    EastWest: 0.0, NorthSouth: 0.0, Elevation: 0.0,
                    HeadingDegrees: 0.0f,
                    IsOutdoor: true),
                "Lifestone",
                ++_lifestoneRevision,
                true);
        }

        // For allegiance hometown, capture the destination cell from the portal transition.
        if (kind == PluginRecallKind.Allegiance && destinationCell != 0u)
        {
            _allegianceLocation = new PluginRecallLocation(
                PluginRecallKind.Allegiance,
                new PluginNavigationPosition(
                    CellId: destinationCell,
                    EastWest: 0.0, NorthSouth: 0.0, Elevation: 0.0,
                    HeadingDegrees: 0.0f,
                    IsOutdoor: true),
                "Allegiance Hometown",
                ++_allegianceRevision,
                true);
        }
    }

    uint IWorldObjectAutomation.OpenContainerObjectId
    {
        get
        {
            lock (_gate)
                return _runtime?.InventoryOwner.ExternalContainers
                    .CurrentContainerId ?? 0u;
        }
    }

    IReadOnlyList<PluginWorldObject> IWorldObjectAutomation.CaptureObjects()
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable)
            return Array.Empty<PluginWorldObject>();

        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        uint playerId = runtime.PlayerIdentity.ServerGuid;
        var result = new List<PluginWorldObject>();
        var captured = new HashSet<uint>();
        foreach (RuntimeEntityRecord record in
            runtime.EntityObjects.Entities.ActiveRecords.ToArray())
        {
            ClientObject? item = objects.Get(record.ServerGuid);
            result.Add(ProjectWorldObject(runtime, record, item, playerId));
            captured.Add(record.ServerGuid);
        }
        foreach (ClientObject item in objects.Objects)
        {
            if (!captured.Add(item.ObjectId))
                continue;
            result.Add(ProjectWorldObject(runtime, null, item, playerId));
        }
        result.Sort(static (left, right) => left.ObjectId.CompareTo(right.ObjectId));
        return result;
    }

    bool IWorldObjectAutomation.TryGet(
        uint objectId,
        out PluginWorldObject value)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable || objectId == 0u)
        {
            value = default;
            return false;
        }

        runtime.EntityObjects.Entities.TryGetActive(
            objectId,
            out RuntimeEntityRecord? record);
        ClientObject? item = runtime.InventoryOwner.Objects.Get(objectId);
        if (record is null && item is null)
        {
            value = default;
            return false;
        }
        value = ProjectWorldObject(
            runtime,
            record,
            item,
            runtime.PlayerIdentity.ServerGuid);
        return true;
    }

    bool IWorldObjectAutomation.TryCaptureProperties(
        uint objectId,
        out PluginItemProperties properties)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        ClientObject? item = runtime?.InventoryOwner.Objects.Get(objectId);
        if (runtime is null || !IsAvailable || item is null)
        {
            properties = default;
            return false;
        }
        properties = CaptureProperties(item);
        return true;
    }

    // Any object present in the object table is a valid target here --
    // owned inventory, equipped, landscape, a vendor listing, or an open
    // container's content -- unlike ILootAutomation.Identify, which is
    // deliberately scoped to the currently open corpse/container.
    PluginItemCommandResult IWorldObjectAutomation.Activate(uint objectId)
    {
        GameRuntime? runtime;
        Func<uint, PluginItemCommandResult>? useWorldObject;
        lock (_gate)
        {
            runtime = _runtime;
            useWorldObject = _useWorldObject;
        }
        if (runtime is null || useWorldObject is null || !IsAvailable)
            return new(PluginItemCommandStatus.Unavailable);
        if (objectId == 0u || runtime.InventoryOwner.Objects.Get(objectId) is not { } item)
            return new(PluginItemCommandStatus.InvalidTarget);
        uint playerId = runtime.PlayerIdentity.ServerGuid;
        if (RuntimeWorldObjectProjection.IsPlayerOwned(
                item,
                playerId,
                runtime.InventoryOwner.Objects))
            return new(PluginItemCommandStatus.InvalidTarget,
                "Activate is for world objects; use Items.Use for owned items.");
        PluginItemCommandResult result = useWorldObject(objectId);
        if (result.Accepted)
        {
            lock (_gate)
            {
                if (_pendingActivationObjectIds.TryGetValue(objectId, out long existing))
                {
                    // Already tracking this object; update revision.
                    _pendingActivationObjectIds[objectId] = ++_activationRevision;
                }
                else
                {
                    _pendingActivationObjectIds.Add(objectId, ++_activationRevision);
                }
            }
        }
        return result;
    }

    PluginItemCommandResult IWorldObjectAutomation.Identify(uint objectId)
    {
        GameRuntime? runtime;
        Func<uint, bool>? identify;
        lock (_gate)
        {
            runtime = _runtime;
            identify = _identifyItem;
        }
        if (runtime is null || identify is null || !IsAvailable)
            return new(PluginItemCommandStatus.Unavailable);
        if (objectId == 0u || runtime.InventoryOwner.Objects.Get(objectId) is null)
            return new(PluginItemCommandStatus.InvalidItem);
        // Only another description in flight holds this up. An item action
        // of the caller's own is not one: the two do not share a channel.
        // A description waited on past its own bound is not one either --
        // asked through the owner below, it is given up and this request
        // takes its place.
        if (!runtime.ActionOwner.Transactions.CanBeginAppraisal)
            return new(PluginItemCommandStatus.Busy);
        return identify(objectId)
            ? new(PluginItemCommandStatus.Started)
            : new(PluginItemCommandStatus.Refused);
    }

    private PluginWorldObject ProjectWorldObject(
        GameRuntime runtime,
        RuntimeEntityRecord? record,
        ClientObject? item,
        uint playerId) =>
        RuntimeWorldObjectProjection.Project(
            record,
            item,
            playerId,
            runtime.InventoryOwner.Objects,
            activeSpellIdsForPlayer: _activeSpellIdsForPlayer);

    private static bool HasPropertyData(PropertyBundle properties) =>
        RuntimeWorldObjectProjection.HasPropertyData(properties);

    internal static PluginObjectClass ClassifyObject(ClientObject? item) =>
        RuntimeWorldObjectProjection.ClassifyObject(item);

    private static Position? ConvertPosition(
        AcDream.Core.Net.Messages.CreateObject.ServerPosition? position) =>
        RuntimeWorldObjectProjection.ConvertPosition(position);

    /// <summary>
    /// Whether a cast this session issued is still outstanding. It used to
    /// answer from the inventory transaction count, which an appraisal or a
    /// pickup raises as readily as a cast: an automation that appraises as
    /// it goes then reads as permanently mid-cast, and every rule that
    /// casts is refused for ever.
    /// </summary>
    public bool IsCasting
    {
        get
        {
            RuntimeSpellCastState? cast;
            lock (_gate)
                cast = _cast;
            return cast?.PendingSpellId is not null;
        }
    }

    public PluginCastGate EvaluateGate(uint spellId)
    {
        RuntimeSpellCastState? cast;
        Spellbook? spellbook;
        lock (_gate)
        {
            cast = _cast;
            spellbook = _spellbook;
        }
        if (cast is null || spellbook is null || !IsAvailable)
            return PluginCastGate.Unavailable;
        if (!spellbook.Knows(spellId))
            return PluginCastGate.NotKnown;
        if (IsCasting)
            return PluginCastGate.Busy;

        return cast.EvaluateCastGate(spellId) switch
        {
            SpellCastGate.Unknown => PluginCastGate.NotKnown,
            SpellCastGate.NoTargetNeeded => PluginCastGate.Ready,
            SpellCastGate.TargetCompatible => PluginCastGate.Ready,
            SpellCastGate.NoTargetSelected => PluginCastGate.NoTargetSelected,
            SpellCastGate.TargetIncompatible => PluginCastGate.TargetIncompatible,
            _ => PluginCastGate.Refused,
        };
    }

    public bool Cast(uint spellId) =>
        RequestCast(spellId) == PluginCastRequestResult.Sent;

    public PluginCastRequestResult RequestCast(uint spellId)
    {
        RuntimeSpellCastState? cast;
        lock (_gate)
            cast = _cast;
        if (cast is null)
            return PluginCastRequestResult.Unavailable;
        return cast.Cast(spellId) switch
        {
            CastRequestResult.Sent => PluginCastRequestResult.Sent,
            CastRequestResult.UnknownSpell => PluginCastRequestResult.UnknownSpell,
            CastRequestResult.NoTarget => PluginCastRequestResult.NoTarget,
            CastRequestResult.IncompatibleTarget =>
                PluginCastRequestResult.IncompatibleTarget,
            CastRequestResult.MissingComponents =>
                PluginCastRequestResult.MissingComponents,
            _ => PluginCastRequestResult.Unavailable,
        };
    }

    public PluginCastRequestResult RequestCast(
        uint spellId, uint targetObjectId) =>
        SelectExplicitTarget(targetObjectId)
            ? RequestCast(spellId)
            : PluginCastRequestResult.IncompatibleTarget;

    public bool HasComponents(uint spellId)
    {
        RuntimeSpellCastState? cast;
        lock (_gate)
            cast = _cast;
        return cast is null || cast.HasRequiredComponents(spellId);
    }

    public PluginCastCompletion LastCompletion
    {
        get
        {
            ObserveSuccessfulLocalCast();
            RuntimeSpellCastState? cast;
            lock (_gate)
                cast = _cast;
            RuntimeSpellCastCompletion completion =
                cast?.LastCompletion ?? default;
            return new PluginCastCompletion(
                completion.Revision,
                completion.SpellId,
                completion.TargetObjectId,
                completion.WeenieError);
        }
    }

    public PluginCastGate EvaluateGate(uint spellId, uint targetObjectId)
    {
        if (!SelectExplicitTarget(targetObjectId))
            return PluginCastGate.Refused;
        return EvaluateGate(spellId);
    }

    public bool Cast(uint spellId, uint targetObjectId) =>
        SelectExplicitTarget(targetObjectId) && Cast(spellId);

    // ── IEquipmentAutomation ─────────────────────────────────────────────
    bool IEquipmentAutomation.IsAvailable
    {
        get
        {
            lock (_gate)
                return !_disposed && _equip is not null && IsAvailable;
        }
    }

    bool IEquipmentAutomation.IsBusy
    {
        get
        {
            Func<bool>? busy;
            lock (_gate)
                busy = _equipmentBusy;
            return busy?.Invoke() == true;
        }
    }

    public IReadOnlyList<PluginEquipmentItem> CaptureOwnedEquipment()
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable)
            return Array.Empty<PluginEquipmentItem>();

        uint playerId = runtime.PlayerIdentity.ServerGuid;
        if (playerId == 0u)
            return Array.Empty<PluginEquipmentItem>();
        return BuildOwnedEquipment(runtime.InventoryOwner.Objects, playerId);
    }

    public IReadOnlyList<PluginEquipmentPlacement> CaptureWorldPlacementsInOrder()
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable)
            return Array.Empty<PluginEquipmentPlacement>();
        return runtime.InventoryOwner.Objects.Objects
            .Select(static item => new PluginEquipmentPlacement(
                item.ObjectId, (uint)item.CurrentlyEquippedLocation))
            .ToArray();
    }

    event Action<PluginEquipmentObservation> IEquipmentAutomation.PlacementObserved
    {
        add { lock (_gate) _equipmentPlacementObserved += value; }
        remove { lock (_gate) _equipmentPlacementObserved -= value; }
    }

    private void OnEquipmentObjectMoved(ClientObjectMove move)
    {
        if (move.Origin != ClientObjectMoveOrigin.AuthoritativeResponse)
            return;
        Action<PluginEquipmentObservation>? handlers;
        lock (_gate)
            handlers = _equipmentPlacementObserved;
        handlers?.Invoke(new PluginEquipmentObservation(
            move.ItemId,
            (uint)move.Current.EquipLocation,
            move.Current.EquipLocation == EquipMask.None));
    }

    private void OnEquipmentObjectRemoved(ClientObjectRemoval removal)
    {
        if (removal.Reason != ClientObjectRemovalReason.LogicalDelete)
            return;
        Action<PluginEquipmentObservation>? handlers;
        lock (_gate)
            handlers = _equipmentPlacementObserved;
        handlers?.Invoke(new PluginEquipmentObservation(
            removal.Object.ObjectId, 0u, true));
    }

    /// <summary>
    /// Everything the player owns that can be worn or wielded, in the order
    /// the contract promises: what is equipped first, then by name, then by
    /// object id. "The first wand" means the held one when any wand is held,
    /// and two wands of one name always come back in the same order.
    /// </summary>
    internal static List<PluginEquipmentItem> BuildOwnedEquipment(
        ClientObjectTable objects,
        uint playerId)
    {
        var built = new List<PluginEquipmentItem>();
        foreach (ClientObject item in objects.Objects)
        {
            if (item.ValidLocations == EquipMask.None
                || !IsPlayerOwned(item, playerId, objects))
            {
                continue;
            }
            built.Add(new PluginEquipmentItem(
                item.ObjectId,
                item.Name,
                (uint)item.Type,
                (uint)item.ValidLocations,
                (uint)item.CurrentlyEquippedLocation,
                item.ContainerId,
                item.WielderId,
                item.CombatUse ?? 0,
                item.Properties.GetInt((uint)PropertyInt.DamageType),
                item.Properties.GetInt((uint)PropertyInt.WeaponSkill),
                item.Properties.GetInt((uint)PropertyInt.Damage),
                item.Properties.GetFloat((uint)PropertyFloat.DamageVariance))
            {
                AmmoType = item.AmmoType ?? (uint)Math.Max(
                    0,
                    item.Properties.GetInt((uint)PropertyInt.AmmoType)),
                StackSize = item.StackSize,
                ObjectClass = ClassifyObject(item),
                WeaponType = item.Properties.GetInt(
                    (uint)PropertyInt.WeaponType),
                Cleaving = item.Properties.GetInt(
                    (uint)PropertyInt.Cleaving),
                ImbuedEffect = item.Properties.GetInt(
                    (uint)PropertyInt.ImbuedEffect),
                ResistanceCleaving = item.Properties.GetInt(
                    (uint)PropertyInt.ResistanceModifierType),
                SlayerCreatureType = item.Properties.GetInt(
                    (uint)PropertyInt.SlayerCreatureType),
                CrushingBlow = item.Properties.GetFloat(
                    (uint)PropertyFloat.CriticalMultiplier) > 0d,
                BitingStrike = item.Properties.GetFloat(
                    (uint)PropertyFloat.CriticalFrequency) > 0d,
                ArmorCleaving = item.Properties.GetFloat(
                    (uint)PropertyFloat.IgnoreArmor) > 0d,
            });
        }
        built.Sort(CompareEquipmentOrder);
        return built;
    }

    /// <summary>Equipped first, then by name, then by object id.</summary>
    internal static int CompareEquipmentOrder(
        PluginEquipmentItem left,
        PluginEquipmentItem right)
    {
        int equipped = right.IsEquipped.CompareTo(left.IsEquipped);
        if (equipped != 0)
            return equipped;
        int name = string.CompareOrdinal(left.Name, right.Name);
        return name != 0
            ? name
            : left.ObjectId.CompareTo(right.ObjectId);
    }

    public PluginEquipmentCommandResult Equip(
        uint objectId,
        uint requestedLocation = 0u)
    {
        Func<uint, uint, bool>? equip;
        Func<bool>? busy;
        GameRuntime? runtime;
        lock (_gate)
        {
            equip = _equip;
            busy = _equipmentBusy;
            runtime = _runtime;
        }
        if (equip is null || runtime is null || !IsAvailable)
            return new(PluginEquipmentCommandStatus.Unavailable);
        if (objectId == 0u
            || runtime.InventoryOwner.Objects.Get(objectId) is not { } item
            || item.ValidLocations == EquipMask.None)
        {
            return new(PluginEquipmentCommandStatus.InvalidItem);
        }
        if (item.CurrentlyEquippedLocation != EquipMask.None
            && (requestedLocation == 0u
                || ((uint)item.CurrentlyEquippedLocation & requestedLocation)
                    == requestedLocation))
        {
            return new(PluginEquipmentCommandStatus.AlreadyEquipped);
        }
        if (busy?.Invoke() == true)
            return new(PluginEquipmentCommandStatus.Busy);
        return equip(objectId, requestedLocation)
            ? new(PluginEquipmentCommandStatus.Started)
            : new(PluginEquipmentCommandStatus.Refused);
    }

    public PluginEquipmentCommandResult EquipSecondary(uint objectId)
    {
        Func<uint, bool>? equipSecondary;
        Func<bool>? busy;
        GameRuntime? runtime;
        lock (_gate)
        {
            equipSecondary = _equipSecondary;
            busy = _equipmentBusy;
            runtime = _runtime;
        }
        if (equipSecondary is null || runtime is null || !IsAvailable)
            return new(PluginEquipmentCommandStatus.Unavailable);
        if (objectId == 0u
            || runtime.InventoryOwner.Objects.Get(objectId) is not { } item
            || item.ValidLocations == EquipMask.None)
            return new(PluginEquipmentCommandStatus.InvalidItem);
        if (item.CurrentlyEquippedLocation == EquipMask.Shield)
            return new(PluginEquipmentCommandStatus.AlreadyEquipped);
        if (busy?.Invoke() == true)
            return new(PluginEquipmentCommandStatus.Busy);
        return equipSecondary(objectId)
            ? new(PluginEquipmentCommandStatus.Started)
            : new(PluginEquipmentCommandStatus.Refused);
    }

    // ── IItemAutomation ──────────────────────────────────────────────────
    bool IItemAutomation.IsAvailable
    {
        get
        {
            lock (_gate)
                return !_disposed && _useItem is not null
                    && _applyItem is not null && IsAvailable;
        }
    }

    bool IItemAutomation.IsBusy
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime is not null && IsItemCommandBusy(runtime);
        }
    }

    /// <summary>
    /// Whether a use or an inventory request offered this instant would come
    /// back busy. There are two ways it can: a request of the caller's own is
    /// still in flight and has to finish first, or the short pacing between
    /// two uses has not lapsed yet. Both mean "not yet" rather than "no", and
    /// both have to be visible from outside -- a caller that waits for this
    /// to clear and only then asks must not still be refused.
    /// </summary>
    private static bool IsItemCommandBusy(GameRuntime runtime) =>
        !runtime.InventoryOwner.Transactions.CanBeginRequest
        || !runtime.ItemInteractionOwner.IsUseThrottleReadyForAutomation;

    int IItemAutomation.ActiveOwnedPetCount
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            if (runtime is null || !IsAvailable)
                return 0;
            uint playerId = runtime.PlayerIdentity.ServerGuid;
            int count = 0;
            foreach (ClientObject candidate in runtime.InventoryOwner.Objects.Objects)
            {
                if (candidate.PetOwnerId == playerId
                    && (candidate.Type & ItemType.Creature) != 0)
                {
                    count++;
                }
            }
            return count;
        }
    }

    PluginItemUseCompletion IItemAutomation.LastCompletion
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            RuntimeItemUseCompletion completion =
                runtime?.ActionOwner.Transactions.LastItemUseCompletion ?? default;
            return new PluginItemUseCompletion(
                completion.Revision,
                completion.SourceObjectId,
                completion.TargetObjectId,
                completion.WeenieError);
        }
    }

    PluginInventoryCompletion IItemAutomation.LastInventoryCompletion
    {
        get
        {
            lock (_gate)
                return _lastInventoryCompletion;
        }
    }

    public uint ActiveVendorObjectId
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime?.InventoryOwner.Vendor.VendorId ?? 0u;
        }
    }

    public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems()
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable)
            return Array.Empty<PluginInventoryItem>();

        uint playerId = runtime.PlayerIdentity.ServerGuid;
        if (playerId == 0u)
            return Array.Empty<PluginInventoryItem>();
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        var built = new List<PluginInventoryItem>();
        foreach (ClientObject item in objects.Objects)
        {
            if (!IsPlayerOwned(item, playerId, objects))
                continue;
            built.Add(ProjectInventoryItem(runtime, item));
        }
        built.Sort(static (left, right) =>
        {
            int name = string.CompareOrdinal(left.Name, right.Name);
            return name != 0 ? name : left.ObjectId.CompareTo(right.ObjectId);
        });
        return built;
    }

    public bool TryCaptureProperties(
        uint objectId,
        out PluginItemProperties properties)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable)
        {
            properties = default;
            return false;
        }
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        uint playerId = runtime.PlayerIdentity.ServerGuid;
        if (!TryGetOwned(objects, playerId, objectId, out ClientObject? item))
        {
            properties = default;
            return false;
        }
        properties = CaptureProperties(item!);
        return true;
    }

    public PluginItemCommandResult Use(uint objectId)
        => DispatchItem(objectId, 0u);

    public PluginItemCommandResult Apply(uint objectId, uint targetObjectId)
        => DispatchItem(objectId, targetObjectId);

    public PluginItemCommandResult MoveToContainer(
        uint objectId,
        uint containerObjectId,
        uint amount = 0u,
        int placement = 0)
        => MoveToContainer(
            objectId, containerObjectId, amount, placement, joinStack: false);

    public PluginItemCommandResult MoveToContainer(
        uint objectId,
        uint containerObjectId,
        uint amount,
        int placement,
        bool joinStack)
    {
        Func<uint, uint, uint, int, bool>? plainMove;
        Func<uint, uint, uint, int, bool, bool>? joiningMove;
        GameRuntime? runtime;
        lock (_gate)
        {
            plainMove = _moveItem;
            joiningMove = _moveItemJoiningStack;
            runtime = _runtime;
        }
        Func<uint, uint, uint, int, bool>? move = !joinStack
            ? plainMove
            : joiningMove is null
                ? null
                : (item, container, count, slot) =>
                    joiningMove(item, container, count, slot, true);
        if (runtime is null || move is null || !IsAvailable)
            return new(PluginItemCommandStatus.Unavailable);
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        uint playerId = runtime.PlayerIdentity.ServerGuid;
        if (!TryGetOwned(objects, playerId, objectId, out ClientObject? item))
            return new(PluginItemCommandStatus.InvalidItem);
        if (containerObjectId == 0u
            || objects.Get(containerObjectId) is not { } container
            || (containerObjectId != playerId
                && !IsPlayerOwned(container, playerId, objects)))
        {
            return new(PluginItemCommandStatus.InvalidTarget);
        }
        if (!ValidAmount(item!, amount))
            return new(PluginItemCommandStatus.Refused, "Invalid stack quantity.");
        if (!runtime.InventoryOwner.Transactions.CanBeginRequest)
            return new(PluginItemCommandStatus.Busy);
        return move(objectId, containerObjectId, amount, placement)
            ? new(PluginItemCommandStatus.Started)
            : new(PluginItemCommandStatus.Refused);
    }

    public PluginItemCommandResult Merge(
        uint sourceObjectId,
        uint targetObjectId,
        uint amount = 0u)
    {
        Func<uint, uint, uint, bool>? merge;
        GameRuntime? runtime;
        lock (_gate)
        {
            merge = _mergeItems;
            runtime = _runtime;
        }
        if (runtime is null || merge is null || !IsAvailable)
            return new(PluginItemCommandStatus.Unavailable);
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        uint playerId = runtime.PlayerIdentity.ServerGuid;
        if (!TryGetOwned(objects, playerId, sourceObjectId, out ClientObject? source))
            return new(PluginItemCommandStatus.InvalidItem);
        if (!TryGetOwned(objects, playerId, targetObjectId, out _))
            return new(PluginItemCommandStatus.InvalidTarget);
        if (!ValidAmount(source!, amount))
            return new(PluginItemCommandStatus.Refused, "Invalid stack quantity.");
        if (!runtime.InventoryOwner.Transactions.CanBeginRequest)
            return new(PluginItemCommandStatus.Busy);
        return merge(sourceObjectId, targetObjectId, amount)
            ? new(PluginItemCommandStatus.Started)
            : new(PluginItemCommandStatus.Refused);
    }

    public PluginItemCommandResult Drop(uint objectId, uint amount = 0u)
    {
        Func<uint, uint, bool>? drop;
        GameRuntime? runtime;
        lock (_gate)
        {
            drop = _dropItem;
            runtime = _runtime;
        }
        if (runtime is null || drop is null || !IsAvailable)
            return new(PluginItemCommandStatus.Unavailable);
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        if (!TryGetOwned(
                objects,
                runtime.PlayerIdentity.ServerGuid,
                objectId,
                out ClientObject? item))
        {
            return new(PluginItemCommandStatus.InvalidItem);
        }
        if (!ValidAmount(item!, amount))
            return new(PluginItemCommandStatus.Refused, "Invalid stack quantity.");
        if (!runtime.InventoryOwner.Transactions.CanBeginRequest)
            return new(PluginItemCommandStatus.Busy);
        return drop(objectId, amount)
            ? new(PluginItemCommandStatus.Started)
            : new(PluginItemCommandStatus.Refused);
    }

    public PluginItemCommandResult Give(
        uint objectId,
        uint targetObjectId,
        uint amount = 0u)
    {
        Func<uint, uint, uint, bool>? give;
        GameRuntime? runtime;
        lock (_gate)
        {
            give = _giveItem;
            runtime = _runtime;
        }
        if (runtime is null || give is null || !IsAvailable)
            return new(PluginItemCommandStatus.Unavailable);
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        if (!TryGetOwned(
                objects,
                runtime.PlayerIdentity.ServerGuid,
                objectId,
                out ClientObject? item))
        {
            return new(PluginItemCommandStatus.InvalidItem);
        }
        if (targetObjectId == 0u || objects.Get(targetObjectId) is null)
            return new(PluginItemCommandStatus.InvalidTarget);
        if (!ValidAmount(item!, amount))
            return new(PluginItemCommandStatus.Refused, "Invalid stack quantity.");
        if (!runtime.InventoryOwner.Transactions.CanBeginRequest)
            return new(PluginItemCommandStatus.Busy);
        return give(objectId, targetObjectId, amount)
            ? new(PluginItemCommandStatus.Started)
            : new(PluginItemCommandStatus.Refused);
    }

    public PluginItemCommandResult Salvage(
        uint toolObjectId,
        IReadOnlyList<uint> itemObjectIds)
    {
        Func<uint, IReadOnlyList<uint>, bool>? salvage;
        GameRuntime? runtime;
        lock (_gate)
        {
            salvage = _salvageItems;
            runtime = _runtime;
        }
        if (runtime is null || salvage is null || !IsAvailable)
            return new(PluginItemCommandStatus.Unavailable);
        if (itemObjectIds is null || itemObjectIds.Count == 0)
            return new(PluginItemCommandStatus.InvalidItem);

        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        uint playerId = runtime.PlayerIdentity.ServerGuid;
        if (!TryGetOwned(objects, playerId, toolObjectId, out ClientObject? tool)
            || (tool!.Type & ItemType.TinkeringTool) == 0)
        {
            return new(PluginItemCommandStatus.InvalidTarget);
        }
        foreach (uint itemObjectId in itemObjectIds)
        {
            if (!TryGetOwned(objects, playerId, itemObjectId, out _))
                return new(PluginItemCommandStatus.InvalidItem);
        }
        if (!runtime.InventoryOwner.Transactions.CanBeginRequest)
            return new(PluginItemCommandStatus.Busy);
        return salvage(toolObjectId, itemObjectIds)
            ? new(PluginItemCommandStatus.Started)
            : new(PluginItemCommandStatus.Refused);
    }

    public PluginItemCommandResult Sell(uint objectId, uint amount = 0u)
    {
        Func<uint, uint, int, bool>? sell;
        GameRuntime? runtime;
        lock (_gate)
        {
            sell = _sellItem;
            runtime = _runtime;
        }
        if (runtime is null || sell is null || !IsAvailable)
            return new(PluginItemCommandStatus.Unavailable);
        uint vendorId = runtime.InventoryOwner.Vendor.VendorId;
        if (vendorId == 0u)
            return new(PluginItemCommandStatus.InvalidTarget, "No vendor is open.");

        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        uint playerId = runtime.PlayerIdentity.ServerGuid;
        if (!TryGetOwned(objects, playerId, objectId, out ClientObject? item))
            return new(PluginItemCommandStatus.InvalidItem);
        if (!ValidAmount(item!, amount))
            return new(PluginItemCommandStatus.Refused, "Invalid stack quantity.");
        int quantity = checked((int)(amount == 0u
            ? (uint)Math.Max(1, item!.StackSize)
            : amount));
        int perUnitValue = VendorPricing.PerUnitValue(item!.Value, item.StackSize);
        VendorShopProfile profile = runtime.InventoryOwner.Vendor.Profile;
        VendorSellRejection rejection = VendorSellAcceptability.Evaluate(
            ownedByPlayer: true,
            containedItemCount: objects.GetContents(objectId).Count,
            itemTypeMask: (uint)item.Type,
            perUnitValue,
            profile.MerchandiseItemTypes,
            profile.MerchandiseMinValue,
            profile.MerchandiseMaxValue,
            item.PublicWeenieBitfield ?? 0u);
        if (rejection != VendorSellRejection.None)
        {
            return new PluginItemCommandResult(
                PluginItemCommandStatus.Refused,
                VendorSellAcceptability.MessageFor(rejection));
        }
        if (!runtime.InventoryOwner.Transactions.CanBeginRequest)
            return new(PluginItemCommandStatus.Busy);
        return sell(vendorId, objectId, quantity)
            ? new(PluginItemCommandStatus.Started)
            : new(PluginItemCommandStatus.Refused);
    }

    private PluginItemCommandResult DispatchItem(
        uint objectId,
        uint targetObjectId)
    {
        Func<uint, bool>? use;
        Func<uint, uint, bool>? apply;
        Func<uint, PluginItemCommandResult>? useWorldObject;
        GameRuntime? runtime;
        lock (_gate)
        {
            use = _useItem;
            apply = _applyItem;
            useWorldObject = _useWorldObject;
            runtime = _runtime;
        }
        if (runtime is null || use is null || apply is null || !IsAvailable)
            return new(PluginItemCommandStatus.Unavailable);
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        uint playerId = runtime.PlayerIdentity.ServerGuid;
        if (objectId == 0u || objects.Get(objectId) is not { } item)
            return new(PluginItemCommandStatus.InvalidItem);
        if (!IsPlayerOwned(item, playerId, objects))
        {
            // A landscape object (a vendor, a corpse, a chest, an NPC) the
            // plugin doesn't own can't go through the inventory-only
            // use/apply path below -- walk to it and open it the same way
            // a click on it does.
            if (targetObjectId != 0u || useWorldObject is null)
                return new(PluginItemCommandStatus.InvalidItem);
            return useWorldObject(objectId);
        }
        if (targetObjectId != 0u && objects.Get(targetObjectId) is null)
            return new(PluginItemCommandStatus.InvalidTarget);
        if (targetObjectId == 0u
            && ItemUseability.IsTargeted(item.Useability ?? ItemUseability.Undef))
        {
            // A targeted-use item (a Mana Stone, a lockpick, a tinkering
            // tool used on another item) cannot complete through Use(id)
            // alone -- TryUseItemForAutomation already refuses it for
            // exactly this reason, but that refusal came back as a bare
            // Refused with no notice, indistinguishable from every other
            // kind of refusal. Name the real reason here instead, before
            // ever calling into the owned-item path.
            return new(
                PluginItemCommandStatus.Refused,
                "This item requires a target; call Apply(objectId, targetObjectId) instead.");
        }
        // The owned-item use and apply paths below both take the same pacing
        // between two uses that a click does, and both answer a refusal there
        // in a way the caller cannot read: the use path reports it as a plain
        // refusal, and the apply path reports it as started even though
        // nothing went out. Name it here instead, as the early answer it is,
        // and off the same predicate IsBusy reports.
        if (IsItemCommandBusy(runtime))
            return new(PluginItemCommandStatus.Busy);
        bool started = targetObjectId == 0u
            ? use(objectId)
            : apply(objectId, targetObjectId);
        return started
            ? new(PluginItemCommandStatus.Started)
            : new(PluginItemCommandStatus.Refused);
    }

    private static bool IsPlayerOwned(
        ClientObject item,
        uint playerId,
        ClientObjectTable objects) =>
        RuntimeWorldObjectProjection.IsPlayerOwned(item, playerId, objects);

    /// <summary>
    /// How recent, in simulation seconds, the server's refusal to appraise an
    /// item must be for <see cref="ForgetStaleItem"/> to act on it.
    /// </summary>
    internal const double StaleItemRefusalWindowSeconds = 30d;

    private static bool HoldsAnything(ClientObjectTable objects, uint containerId)
    {
        foreach (ClientObject candidate in objects.Objects)
        {
            if (candidate.ContainerId == containerId)
                return true;
        }
        return false;
    }

    public PluginItemCommandResult ForgetStaleItem(uint objectId)
    {
        Func<uint, bool>? dismiss;
        GameRuntime? runtime;
        lock (_gate)
        {
            dismiss = _dismissGhost;
            runtime = _runtime;
        }
        if (runtime is null || dismiss is null || !IsAvailable)
            return new(PluginItemCommandStatus.Unavailable);
        uint playerId = runtime.PlayerIdentity.ServerGuid;
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        if (objectId == playerId
            || !TryGetOwned(objects, playerId, objectId, out ClientObject? item))
        {
            return new(PluginItemCommandStatus.InvalidItem);
        }
        // The item leaves by the route a server delete takes, and that route
        // is keyed on the server's own record of the object. Without one there
        // is nothing a server delete would have removed either.
        if (!runtime.EntityObjects.Entities.TryGetActive(objectId, out _))
        {
            return new(
                PluginItemCommandStatus.InvalidItem,
                "The client holds no server record of that item.");
        }
        if (item!.CurrentlyEquippedLocation != EquipMask.None
            || item.WielderId != 0u)
        {
            return new(PluginItemCommandStatus.Refused, "That item is equipped.");
        }
        // A pack's listing is only what the server last showed of it; an
        // object can still name the pack as its container without being in
        // that list. Letting go of such a pack would strand those objects
        // under a container that no longer exists, out of every plugin's
        // reach.
        if (objects.GetContents(objectId).Count != 0
            || HoldsAnything(objects, objectId))
        {
            return new(
                PluginItemCommandStatus.Refused,
                "That pack still holds something.");
        }
        // An appraisal of this very item still on its way is the answer the
        // decision rests on: a caller asking a second time is waiting for it,
        // and what an earlier answer said no longer counts until it lands.
        RuntimeInteractionTransactionState appraisals =
            runtime.ActionOwner.Transactions;
        if (appraisals.AwaitingAppraisalId == objectId
            && !appraisals.IsAwaitingAppraisalExpired)
        {
            return new(
                PluginItemCommandStatus.Busy,
                "An appraisal of that item is still awaited.");
        }
        if (!item.LastAppraisalUnsuccessful)
        {
            return new(
                PluginItemCommandStatus.Refused,
                "The server has not refused to appraise that item.");
        }
        // Only a refusal the caller has just seen counts. An old one may be
        // the repeat-request refusal of a real item nobody has asked about
        // since, and nothing else would ever overturn it.
        if (runtime.Clock.SimulationTimeSeconds
                - item.LastAppraisalUnsuccessfulAtSeconds
            > StaleItemRefusalWindowSeconds)
        {
            return new(
                PluginItemCommandStatus.Refused,
                "The server refused to appraise that item too long ago; ask again.");
        }
        if (IsItemCommandBusy(runtime))
            return new(PluginItemCommandStatus.Busy);
        return dismiss(objectId) && objects.Get(objectId) is null
            ? new(PluginItemCommandStatus.Completed)
            : new(PluginItemCommandStatus.Refused);
    }

    private static bool TryGetOwned(
        ClientObjectTable objects,
        uint playerId,
        uint objectId,
        out ClientObject? item)
    {
        item = objectId == 0u ? null : objects.Get(objectId);
        return item is not null && IsPlayerOwned(item, playerId, objects);
    }

    private static bool ValidAmount(ClientObject item, uint amount) =>
        amount == 0u || amount <= (uint)Math.Max(1, item.StackSize);

    // ── ILootAutomation ──────────────────────────────────────────────────
    bool ILootAutomation.IsAvailable
    {
        get
        {
            lock (_gate)
                return !_disposed && _useItem is not null
                    && _pickupItem is not null
                    && _identifyItem is not null
                    && IsAvailable;
        }
    }

    bool ILootAutomation.IsBusy
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime is not null && IsItemCommandBusy(runtime);
        }
    }

    uint ILootAutomation.RequestedContainerId
    {
        get
        {
            lock (_gate)
                return _runtime?.InventoryOwner.ExternalContainers
                    .RequestedContainerId ?? 0u;
        }
    }

    uint ILootAutomation.CurrentContainerId
    {
        get
        {
            lock (_gate)
                return _runtime?.InventoryOwner.ExternalContainers
                    .CurrentContainerId ?? 0u;
        }
    }

    PluginItemUseCompletion ILootAutomation.LastItemUseCompletion =>
        ((IItemAutomation)this).LastCompletion;

    PluginInventoryCompletion ILootAutomation.LastInventoryCompletion
    {
        get
        {
            lock (_gate)
                return _lastInventoryCompletion;
        }
    }

    PluginAppraisalState ILootAutomation.Appraisal
    {
        get
        {
            lock (_gate)
            {
                RuntimeInteractionTransactionState? transactions =
                    _runtime?.ActionOwner.Transactions;
                return transactions is null
                    ? default
                    : new PluginAppraisalState(
                        transactions.Revision,
                        // A wait already past its own bound is reported as
                        // no wait at all: the answer is not coming, and the
                        // next request is what actually lets it go.
                        transactions.IsAwaitingAppraisalExpired
                            ? 0u
                            : transactions.AwaitingAppraisalId,
                        // The plugin-facing completion signal: the object
                        // id of the last appraisal response that actually
                        // completed, regardless of whether the user's
                        // examination window happens to be showing it.
                        // CurrentAppraisalId is presentation only -- it
                        // does not advance for a plugin-originated
                        // response that lands on a different object than
                        // the one the window shows, and mapping it here
                        // stalls any plugin polling for its own Identify
                        // to finish (loot scanners, trackers).
                        transactions.LastCompletedAppraisalId,
                        // The other half of that poll: a request the client
                        // gave up on never produces a completion, so an
                        // asker with no failure signal waits for ever.
                        transactions.IsAwaitingAppraisalExpired
                            ? transactions.AwaitingAppraisalId
                            : transactions.LastAbandonedAppraisalId)
                    {
                        // The answer is recorded on the object before the
                        // slot completes, both in the same inbound event, so
                        // the object's latest outcome is the completion's.
                        CurrentObjectUnsuccessful =
                            transactions.LastCompletedAppraisalId != 0u
                            && _runtime!.InventoryOwner.Objects
                                .Get(transactions.LastCompletedAppraisalId)
                                ?.LastAppraisalUnsuccessful == true,
                    };
            }
        }
    }

    public IReadOnlyList<PluginLootContainer> CaptureCorpses(
        float maximumDistance)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable
            || float.IsNaN(maximumDistance)
            || maximumDistance <= 0f)
        {
            return Array.Empty<PluginLootContainer>();
        }

        ExternalContainerState external =
            runtime.InventoryOwner.ExternalContainers;
        var result = new List<PluginLootContainer>();
        foreach (ClientObject candidate in runtime.InventoryOwner.Objects.Objects)
        {
            if (((PublicWeenieFlags)(candidate.PublicWeenieBitfield ?? 0u)
                    & PublicWeenieFlags.Corpse) == 0
                || candidate.ContainerId != 0u
                || !RuntimeFriendlyTargetQuery.TryGetDistance(
                    runtime,
                    candidate.ObjectId,
                    out float distance)
                || distance > maximumDistance)
            {
                continue;
            }

            Position? corpsePosition =
                runtime.EntityObjects.Entities.TryGetActive(
                    candidate.ObjectId,
                    out RuntimeEntityRecord corpseRecord)
                    ? corpseRecord.PhysicsBody?.CellPosition
                        ?? ConvertPosition(corpseRecord.Snapshot.Position)
                    : null;

            result.Add(new PluginLootContainer(
                candidate.ObjectId,
                candidate.WeenieClassId,
                candidate.Name,
                distance,
                external.HasCorpseBeenOpened(candidate.ObjectId),
                external.RequestedContainerId == candidate.ObjectId,
                external.CurrentContainerId == candidate.ObjectId)
            {
                HasPosition = corpsePosition is not null,
                Position = corpsePosition is { } placed
                    ? ProjectNavigationPosition(placed)
                    : default,
                LongDescription = candidate.Properties.GetString(
                    (uint)PropertyString.LongDesc),
                IsGeneratedRare = candidate.Properties.GetBool(
                    (uint)PropertyBool.CorpseGeneratedRare),
                IsIdentified = candidate.Properties.Strings.ContainsKey(
                    (uint)PropertyString.LongDesc),
                IsAppraisalAnswered = candidate.AppraisalAnswered
                    || candidate.Properties.Strings.ContainsKey(
                        (uint)PropertyString.LongDesc),
            });
        }
        result.Sort(static (left, right) =>
        {
            int distance = left.Distance.CompareTo(right.Distance);
            return distance != 0
                ? distance
                : left.ObjectId.CompareTo(right.ObjectId);
        });
        return result.Count == 0
            ? Array.Empty<PluginLootContainer>()
            : result.ToArray();
    }

    public bool CurrentContentsReady
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate) runtime = _runtime;
            if (runtime is null || !IsAvailable) return false;
            uint root = runtime.InventoryOwner.ExternalContainers.CurrentContainerId;
            if (root == 0u) return false;
            ClientObjectTable objects = runtime.InventoryOwner.Objects;
            foreach (uint id in CaptureContainerIds(objects, root))
            {
                if (objects.Get(id) is not { } item
                    || string.IsNullOrEmpty(item.Name) || item.WeenieClassId == 0u)
                    return false;
            }
            return true;
        }
    }

    public IReadOnlyList<PluginInventoryItem> CaptureCurrentContents()
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable)
            return Array.Empty<PluginInventoryItem>();

        uint root = runtime.InventoryOwner.ExternalContainers.CurrentContainerId;
        if (root == 0u)
            return Array.Empty<PluginInventoryItem>();

        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        var result = new List<PluginInventoryItem>();
        var visited = new HashSet<uint> { root };
        CaptureContainerTree(runtime, objects, root, visited, result);
        return result.Count == 0
            ? Array.Empty<PluginInventoryItem>()
            : result.ToArray();
    }

    bool ILootAutomation.TryCaptureProperties(
        uint objectId,
        out PluginItemProperties properties)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable)
        {
            properties = default;
            return false;
        }

        uint root = runtime.InventoryOwner.ExternalContainers.CurrentContainerId;
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        if (root == 0u
            || !CaptureContainerIds(objects, root).Contains(objectId)
            || objects.Get(objectId) is not { } item)
        {
            properties = default;
            return false;
        }
        properties = CaptureProperties(item);
        return true;
    }

    public PluginItemCommandResult Open(uint containerObjectId)
    {
        GameRuntime? runtime;
        Func<uint, bool>? use;
        Func<uint, PluginItemCommandResult>? useWorldObject;
        lock (_gate)
        {
            runtime = _runtime;
            use = _useItem;
            useWorldObject = _useWorldObject;
        }
        if (runtime is null || use is null || !IsAvailable)
            return new(PluginItemCommandStatus.Unavailable);
        if (containerObjectId == 0u
            || runtime.InventoryOwner.Objects.Get(containerObjectId)
                is not { } container
            || ((PublicWeenieFlags)(container.PublicWeenieBitfield ?? 0u)
                & (PublicWeenieFlags.Corpse | PublicWeenieFlags.Openable)) == 0)
        {
            return new(PluginItemCommandStatus.InvalidTarget);
        }
        // Two ways to be early here, and neither is a verdict on this
        // container: a request of the caller's own is still in flight, or the
        // short pacing between two uses has not lapsed. An open that arrives
        // inside either window has not failed, it is early. Saying so keeps
        // the caller from spending one of the container's attempts -- and
        // arming whatever back-off it pairs with a failure -- on a fifth of a
        // second's wait, which is what stood a looter still between one
        // container and the next after closing the first. IsBusy reports this
        // same predicate, so a caller that waits for it to clear and asks
        // again is then accepted rather than refused a second time.
        if (IsItemCommandBusy(runtime))
            return new(PluginItemCommandStatus.Busy);
        // A corpse or a chest out in the world is reached the same way here as
        // it is through Use on the very same object: walk to it if it is out
        // of reach, and send the open once the character is there. Sending it
        // from wherever the character was standing asked a corpse metres away
        // to hand over its contents, and the server answered nothing.
        if (!IsPlayerOwned(
                container,
                runtime.PlayerIdentity.ServerGuid,
                runtime.InventoryOwner.Objects)
            && useWorldObject is not null)
        {
            return useWorldObject(containerObjectId);
        }
        return use(containerObjectId)
            ? new(PluginItemCommandStatus.Started)
            : new(PluginItemCommandStatus.Refused);
    }

    public PluginItemCommandResult Close(uint containerObjectId)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable)
            return new(PluginItemCommandStatus.Unavailable);
        if (containerObjectId == 0u
            || runtime.InventoryOwner.ExternalContainers.CurrentContainerId
                != containerObjectId
            || runtime.InventoryOwner.Objects.Get(containerObjectId)
                is not { } container
            || ((PublicWeenieFlags)(container.PublicWeenieBitfield ?? 0u)
                & (PublicWeenieFlags.Corpse | PublicWeenieFlags.Openable)) == 0)
        {
            return new(PluginItemCommandStatus.InvalidTarget);
        }
        // Early rather than failed, the same way an open is.
        if (IsItemCommandBusy(runtime))
            return new(PluginItemCommandStatus.Busy);
        return runtime.ItemInteractionOwner.TryUseItemForAutomation(
            containerObjectId)
            ? new(PluginItemCommandStatus.Started)
            : new(PluginItemCommandStatus.Refused);
    }

    public PluginItemCommandResult Identify(uint objectId)
    {
        GameRuntime? runtime;
        Func<uint, bool>? identify;
        lock (_gate)
        {
            runtime = _runtime;
            identify = _identifyItem;
        }
        if (runtime is null || identify is null || !IsAvailable)
            return new(PluginItemCommandStatus.Unavailable);
        uint root = runtime.InventoryOwner.ExternalContainers.CurrentContainerId;
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        ClientObject? item = objectId == 0u ? null : objects.Get(objectId);
        bool corpse = item is not null
            && ((PublicWeenieFlags)(item.PublicWeenieBitfield ?? 0u)
                & PublicWeenieFlags.Corpse) != 0;
        bool currentContent = root != 0u
            && CaptureContainerIds(objects, root).Contains(objectId);
        bool owned = TryGetOwned(objects, runtime.PlayerIdentity.ServerGuid,
            objectId, out _);
        if (item is null || (!corpse && !currentContent && !owned))
        {
            return new(PluginItemCommandStatus.InvalidItem);
        }
        // One description at a time; a pick-up or an open of the caller's
        // own is on another channel and does not hold this up, and neither
        // does a description already waited on past its own bound.
        if (!runtime.ActionOwner.Transactions.CanBeginAppraisal)
            return new(PluginItemCommandStatus.Busy);
        return identify(objectId)
            ? new(PluginItemCommandStatus.Started)
            : new(PluginItemCommandStatus.Refused);
    }

    public PluginItemCommandResult Pickup(uint objectId, bool mainPack = false)
    {
        GameRuntime? runtime;
        Func<uint, bool, AcDream.Runtime.Gameplay.RuntimeBackpackPlacementOutcome>?
            pickup;
        lock (_gate)
        {
            runtime = _runtime;
            pickup = _pickupItem;
        }
        if (runtime is null || pickup is null || !IsAvailable)
            return new(PluginItemCommandStatus.Unavailable);
        uint root = runtime.InventoryOwner.ExternalContainers.CurrentContainerId;
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        if (objectId == 0u
            || root == 0u
            || objects.Get(objectId) is null
            || !CaptureContainerIds(objects, root).Contains(objectId))
        {
            return new(PluginItemCommandStatus.InvalidItem);
        }
        if (!runtime.InventoryOwner.Transactions.CanBeginRequest)
            return new(PluginItemCommandStatus.Busy);
        // The placement has four endings that send nothing, and this used to
        // answer Started for all of them: a plugin looting a corpse into a
        // full pack was told its request was on its way and then waited for
        // a completion that could never arrive.
        return pickup(objectId, mainPack) switch
        {
            AcDream.Runtime.Gameplay.RuntimeBackpackPlacementOutcome.Sent =>
                new(PluginItemCommandStatus.Started),
            AcDream.Runtime.Gameplay.RuntimeBackpackPlacementOutcome.NoRoom =>
                new(
                    PluginItemCommandStatus.Refused,
                    "no pack the player has open has room for it"),
            AcDream.Runtime.Gameplay.RuntimeBackpackPlacementOutcome
                .AlreadyPending =>
                new(PluginItemCommandStatus.Busy),
            AcDream.Runtime.Gameplay.RuntimeBackpackPlacementOutcome
                .NotDispatched =>
                new(
                    PluginItemCommandStatus.Refused,
                    "the merge this would have become did not go out"),
            _ => new(PluginItemCommandStatus.Refused),
        };
    }

    private static HashSet<uint> CaptureContainerIds(
        ClientObjectTable objects,
        uint root)
    {
        var result = new HashSet<uint>();
        var pending = new Stack<uint>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            uint containerId = pending.Pop();
            foreach (uint childId in objects.GetContents(containerId))
            {
                if (!result.Add(childId))
                    continue;
                if (objects.Get(childId) is { } child
                    && (child.ItemsCapacity != 0
                        || child.ContainersCapacity != 0
                        || (child.Type & ItemType.Container) != 0))
                {
                    pending.Push(childId);
                }
            }
        }
        return result;
    }

    private void CaptureContainerTree(
        GameRuntime runtime,
        ClientObjectTable objects,
        uint containerId,
        HashSet<uint> visited,
        List<PluginInventoryItem> result)
    {
        foreach (uint childId in objects.GetContents(containerId))
        {
            if (!visited.Add(childId)
                || objects.Get(childId) is not { } child)
            {
                continue;
            }
            result.Add(ProjectInventoryItem(runtime, child));
            if (child.ItemsCapacity != 0
                || child.ContainersCapacity != 0
                || (child.Type & ItemType.Container) != 0)
            {
                CaptureContainerTree(runtime, objects, childId, visited, result);
            }
        }
    }

    private static PluginItemProperties CaptureProperties(ClientObject item)
    {
        PropertyBundle source = item.Properties;
        return new PluginItemProperties(
            new Dictionary<uint, int>(source.Ints),
            new Dictionary<uint, long>(source.Int64s),
            new Dictionary<uint, bool>(source.Bools),
            new Dictionary<uint, double>(source.Floats),
            new Dictionary<uint, string>(source.Strings),
            new Dictionary<uint, uint>(source.DataIds),
            new Dictionary<uint, uint>(source.InstanceIds))
        {
            WeaponProfile = ClientAppraisalProfileMapper.ToPluginWeaponProfile(
                item.WeaponProfile),
            ArmorProfile = ClientAppraisalProfileMapper.ToPluginArmorProfile(
                item.ArmorProfile,
                source.GetInt((uint)PropertyInt.ArmorLevel)),
        };
    }

    internal PluginInventoryItem ProjectInventoryItem(
        GameRuntime runtime,
        ClientObject item)
    {
        ClientWeaponProfile? weapon = item.WeaponProfile;
        int damage = weapon is { } weaponDamage
            ? ClientAppraisalProfileMapper.NormalizeDamage(weaponDamage.Damage)
            : item.Properties.GetInt((uint)PropertyInt.Damage);
        return new(
            item.ObjectId,
            item.WeenieClassId,
            item.Name,
            (uint)item.Type,
            item.ContainerId,
            item.WielderId,
            (uint)item.ValidLocations,
            (uint)item.CurrentlyEquippedLocation,
            item.Useability ?? 0u,
            item.TargetType ?? 0u,
            item.PublicWeenieBitfield ?? 0u,
            item.StackSize,
            item.Structure,
            item.MaxStructure,
            item.SpellId
                ?? (item.Properties.DataIds.TryGetValue(
                    (uint)PropertyDataId.Spell,
                    out uint itemSpell) ? itemSpell : 0u),
            item.Properties.GetInt((uint)PropertyInt.PetClass),
            item.Properties.GetInt((uint)PropertyInt.SummoningMastery),
            item.Properties.DataIds.TryGetValue(
                (uint)PropertyDataId.ProcSpell,
                out uint procSpell) ? procSpell : 0u,
            item.Properties.GetBool((uint)PropertyBool.ProcSpellSelfTargeted),
            item.Properties.GetFloat((uint)PropertyFloat.ProcSpellRate),
            weapon is { } wp
                ? (int)wp.WeaponSkill
                : item.Properties.GetInt((uint)PropertyInt.WeaponSkill),
            weapon is { } wt
                ? (int)wt.DamageType
                : item.Properties.GetInt((uint)PropertyInt.DamageType),
            damage,
            weapon is { } wv
                ? wv.DamageVariance
                : item.Properties.GetFloat((uint)PropertyFloat.DamageVariance),
            item.Properties.GetInt((uint)PropertyInt.UseRequiresSkill),
            item.Properties.GetInt((uint)PropertyInt.UseRequiresSkillLevel),
            item.Properties.GetInt((uint)PropertyInt.UseRequiresSkillSpec))
        {
            // The shared cooldown comes with the object and again in an
            // appraisal; the table keeps the newest of the two here.
            SharedCooldownId = item.CooldownId ?? 0u,
            CooldownSeconds = item.CooldownDuration ?? 0d,
            CombatUse = item.CombatUse ?? 0,
            ItemSpellcraft = item.Properties.GetInt(
                (uint)PropertyInt.ItemSpellcraft),
            WieldRequirements = item.Properties.GetInt(
                (uint)PropertyInt.WieldRequirements),
            WieldSkillType = item.Properties.GetInt(
                (uint)PropertyInt.WieldSkilltype),
            WieldDifficulty = item.Properties.GetInt(
                (uint)PropertyInt.WieldDifficulty),
            AttackType = item.Properties.GetInt((uint)PropertyInt.AttackType),
            WeaponType = item.Properties.GetInt((uint)PropertyInt.WeaponType),
            BoosterVital = item.Properties.GetInt((uint)PropertyInt.BoosterEnum),
            AmmoType = item.AmmoType ?? (uint)Math.Max(0,
                item.Properties.GetInt((uint)PropertyInt.AmmoType)),
            BoostValue = item.Properties.GetInt((uint)PropertyInt.BoostValue),
            HealKitModifier = item.Properties.GetFloat(
                (uint)PropertyFloat.HealkitMod),
            AppraisedSpellIds = item.AppraisedSpellIds.Count == 0
                ? Array.Empty<uint>()
                : item.AppraisedSpellIds.ToArray(),
            GearDamage = item.Properties.GetInt((uint)PropertyInt.GearDamage),
            GearDamageResistance = item.Properties.GetInt(
                (uint)PropertyInt.GearDamageResist),
            GearCriticalChance = item.Properties.GetInt(
                (uint)PropertyInt.GearCrit),
            GearCriticalResistance = item.Properties.GetInt(
                (uint)PropertyInt.GearCritResist),
            GearCriticalDamage = item.Properties.GetInt(
                (uint)PropertyInt.GearCritDamage),
            GearCriticalDamageResistance = item.Properties.GetInt(
                (uint)PropertyInt.GearCritDamageResist),
            MaximumStackSize = item.StackSizeMax,
            ContainerSlot = item.ContainerSlot,
            ItemsCapacity = item.ItemsCapacity,
            ContainersCapacity = item.ContainersCapacity,
            Burden = item.Burden,
            Value = item.Value,
            ItemCurrentMana = item.Properties.GetInt((uint)PropertyInt.ItemCurMana),
            ItemMaximumMana = item.Properties.GetInt((uint)PropertyInt.ItemMaxMana),
            Workmanship = item.Workmanship,
            SalvageWorkmanship = item.Workmanship,
            NumTimesTinkered = item.Properties.GetInt((uint)PropertyInt.NumTimesTinkered),
            ImbuedEffect = item.Properties.GetInt((uint)PropertyInt.ImbuedEffect),
            ArmorLevel = item.Properties.GetInt((uint)PropertyInt.ArmorLevel),
            MaxDamage = Math.Max(0, damage),
            WandElementalDamageType = item.Properties.GetInt(
                (uint)PropertyInt.DamageType),
            Retained = item.Properties.GetBool((uint)PropertyBool.Retained),
            MaterialType = item.MaterialType ?? 0u,
            ObjectClass = ClassifyObject(item),
            Palettes = ProjectPalettes(runtime, item.ObjectId),
            IconId = item.IconId,
            IconUnderlayId = item.IconUnderlayId,
            IconOverlayId = item.IconOverlayId,
            CoverageMask = item.Priority,
            PluralName = item.PluralName,
            // The live object's own radius when it is out in the world, and
            // otherwise the one its description carried, so an item in a
            // pack reports it too.
            UseRadius = (runtime.EntityObjects.Entities.TryGetActive(
                    item.ObjectId,
                    out RuntimeEntityRecord useRecord)
                    ? useRecord.Snapshot.UseRadius
                    : null)
                ?? item.Header?.UseRadius
                ?? 0f,
            Header = AcDream.Runtime.Gameplay.RuntimeWorldObjectProjection
                .ProjectHeader(item.Header),
            Effects = item.Effects,
        };
    }

    private IReadOnlyList<PluginPaletteInfo> ProjectPalettes(
        GameRuntime runtime,
        uint objectId)
    {
        IChargenPaletteColorSource? colors;
        lock (_gate)
            colors = _paletteColors;
        if (colors is null
            || !runtime.EntityObjects.Entities.TryGetActive(
                objectId,
                out RuntimeEntityRecord record)
            || record.Snapshot.SubPalettes.Count == 0)
        {
            return Array.Empty<PluginPaletteInfo>();
        }

        var result = new PluginPaletteInfo[record.Snapshot.SubPalettes.Count];
        for (int index = 0; index < result.Length; index++)
        {
            var palette = record.Snapshot.SubPalettes[index];
            // The swap's offset and length count blocks of eight colours, so
            // this is the colour in the middle of the recoloured range: the
            // one the macro tools' colour rules compare against. Their
            // formula, length*16 + offset*32 + 8, is a byte position in the
            // palette file (an 8-byte header, then 4 bytes a colour), not a
            // colour number.
            int sampleIndex = (palette.Offset * 8) + (palette.Length * 4);
            _ = colors.TryGetColor(
                palette.SubPaletteId,
                sampleIndex,
                out var rgb);
            result[index] = new PluginPaletteInfo(
                palette.SubPaletteId,
                palette.Offset,
                palette.Length,
                rgb.R,
                rgb.G,
                rgb.B);
        }
        return result;
    }

    // ── IFellowshipAutomation ────────────────────────────────────────────
    public bool IsInFellowship
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime?.Fellowship.Snapshot.IsInFellowship == true;
        }
    }

    public string FellowshipName
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime?.Fellowship.Snapshot.Name ?? string.Empty;
        }
    }

    string IFellowshipAutomation.Name => FellowshipName;

    public uint LeaderObjectId
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime?.Fellowship.Snapshot.LeaderGuid ?? 0u;
        }
    }

    public bool IsOpen
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime?.Fellowship.Snapshot.IsOpen == true;
        }
    }

    public bool IsLocked
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime?.Fellowship.Snapshot.Locked == true;
        }
    }

    public int MemberCount
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime?.Fellowship.Snapshot.MemberCount ?? 0;
        }
    }

    public bool SharesExperience
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime?.Fellowship.Snapshot is { IsInFellowship: true, ShareXp: true };
        }
    }

    public bool SplitsExperienceEvenly
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime?.Fellowship.Snapshot is { IsInFellowship: true, EvenXpSplit: true };
        }
    }

    public IReadOnlyList<PluginFellowMember> CaptureMembers()
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable
            || !runtime.Fellowship.Snapshot.IsInFellowship)
        {
            return Array.Empty<PluginFellowMember>();
        }

        uint self = runtime.PlayerIdentity.ServerGuid;
        var result = new List<PluginFellowMember>();
        foreach (RuntimeFellowMemberSnapshot member
            in runtime.Fellowship.GetMembers())
        {
            if (member.Guid == self
                || !RuntimeFriendlyTargetQuery.TryGetDistance(
                    runtime,
                    member.Guid,
                    out float distance))
            {
                continue;
            }
            result.Add(new PluginFellowMember(
                member.Guid,
                member.Name,
                member.CurrentHealth,
                member.MaxHealth,
                member.CurrentStamina,
                member.MaxStamina,
                member.CurrentMana,
                member.MaxMana,
                distance)
            {
                ShareLoot = member.ShareLoot,
                Level = member.Level,
                VitalsAgeSeconds = member.VitalsAgeSeconds,
            });
        }
        return result.Count == 0
            ? Array.Empty<PluginFellowMember>()
            : result.ToArray();
    }

    public IReadOnlyList<PluginFellowMember> CaptureRoster() =>
        CaptureFellowshipMembers(includeSelf: true);

    public PluginFellowshipCommandResult Create(
        string name,
        bool shareExperience) => InvokeFellowship((commands, generation) =>
            commands.Fellowship.Create(
                generation,
                name,
                shareExperience));

    public PluginFellowshipCommandResult Recruit(uint targetObjectId) =>
        InvokeFellowship((commands, generation) => commands.Fellowship.Recruit(
            generation,
            targetObjectId));

    public PluginFellowshipCommandResult Dismiss(uint targetObjectId) =>
        InvokeFellowship((commands, generation) => commands.Fellowship.Dismiss(
            generation,
            targetObjectId));

    public PluginFellowshipCommandResult Quit(bool disband) =>
        InvokeFellowship((commands, generation) => commands.Fellowship.Quit(
            generation,
            disband));

    public PluginFellowshipCommandResult AssignLeader(uint targetObjectId) =>
        InvokeFellowship((commands, generation) => commands.Fellowship.AssignLeader(
            generation,
            targetObjectId));

    public PluginFellowshipCommandResult SetOpen(bool isOpen) =>
        InvokeFellowship((commands, generation) => commands.Fellowship.SetOpen(
            generation,
            isOpen));

    public PluginFellowshipCommandResult RequestVitals(bool requested) =>
        InvokeFellowship((commands, generation) =>
            commands.Fellowship.RequestVitals(generation, requested));

    private PluginFellowshipCommandResult InvokeFellowship(
        Func<IGameRuntimeCommands, RuntimeGenerationToken, RuntimeCommandResult> invoke)
    {
        IGameRuntimeCommands? commands;
        GameRuntime? runtime;
        lock (_gate)
        {
            commands = _sessionCommands;
            runtime = _runtime;
        }
        if (commands is null || runtime is null || !IsAvailable)
            return new(PluginFellowshipCommandStatus.Unavailable);
        RuntimeCommandResult result = invoke(commands, runtime.Generation);
        return new(result.Status switch
        {
            RuntimeCommandStatus.Accepted => PluginFellowshipCommandStatus.Accepted,
            RuntimeCommandStatus.Rejected => PluginFellowshipCommandStatus.Rejected,
            _ => PluginFellowshipCommandStatus.Unavailable,
        });
    }

    private IReadOnlyList<PluginFellowMember> CaptureFellowshipMembers(bool includeSelf)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable
            || !runtime.Fellowship.Snapshot.IsInFellowship)
        {
            return Array.Empty<PluginFellowMember>();
        }

        uint self = runtime.PlayerIdentity.ServerGuid;
        var result = new List<PluginFellowMember>();
        foreach (RuntimeFellowMemberSnapshot member in runtime.Fellowship.GetMembers())
        {
            if (!includeSelf && member.Guid == self)
                continue;
            float distance = 0f;
            if (member.Guid != self
                && !RuntimeFriendlyTargetQuery.TryGetDistance(
                    runtime,
                    member.Guid,
                    out distance))
            {
                continue;
            }
            result.Add(new PluginFellowMember(
                member.Guid,
                member.Name,
                member.CurrentHealth,
                member.MaxHealth,
                member.CurrentStamina,
                member.MaxStamina,
                member.CurrentMana,
                member.MaxMana,
                distance)
            {
                ShareLoot = member.ShareLoot,
                Level = member.Level,
                VitalsAgeSeconds = member.VitalsAgeSeconds,
            });
        }
        return result.Count == 0
            ? Array.Empty<PluginFellowMember>()
            : result.ToArray();
    }

    // ── IEnchantmentAutomation ──────────────────────────────────────────
    public void ForgetReported(uint targetObjectId = 0u)
    {
        lock (_gate)
        {
            // Consume the old receipt so polling cannot restore a forgotten cast.
            _trackedCastCompletionRevision = _cast?.LastCompletion.Revision ?? 0;
            if (targetObjectId == 0u)
                _trackedEnchantments.Clear();
            else
                foreach (var key in _trackedEnchantments.Keys
                    .Where(key => key.Target == targetObjectId).ToArray())
                    _trackedEnchantments.Remove(key);
        }
    }

    public IReadOnlyList<PluginTrackedEnchantment> Capture(uint targetObjectId)
    {
        if (targetObjectId == 0u)
            return Array.Empty<PluginTrackedEnchantment>();
        ObserveSuccessfulLocalCast();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            PruneTrackedEnchantments(now);
            PluginTrackedEnchantment[] result = _trackedEnchantments
                .Where(pair => pair.Key.Target == targetObjectId)
                .Select(pair => new PluginTrackedEnchantment(
                    pair.Key.Target,
                    pair.Key.Spell,
                    pair.Value.Family,
                    pair.Value.Quality,
                    pair.Value.IsUntargeted,
                    Math.Max(0d, (pair.Value.ExpiresAt - now).TotalSeconds)))
                .OrderBy(static entry => entry.Family)
                .ThenBy(static entry => entry.SpellId)
                .ToArray();
            return result.Length == 0
                ? Array.Empty<PluginTrackedEnchantment>()
                : result;
        }
    }

    public bool ReportCast(
        uint targetObjectId,
        uint spellId,
        double durationSeconds)
    {
        if (targetObjectId == 0u
            || spellId == 0u
            || !double.IsFinite(durationSeconds)
            || durationSeconds <= 0d)
        {
            return false;
        }

        lock (_gate)
        {
            if (_disposed
                || _spellbook is null
                || !_spellbook.TryGetMetadata(spellId, out SpellMetadata metadata))
            {
                return false;
            }
            TrackEnchantment(
                targetObjectId,
                metadata,
                durationSeconds,
                DateTimeOffset.UtcNow);
            return true;
        }
    }

    private void ObserveSuccessfulLocalCast()
    {
        lock (_gate)
        {
            RuntimeSpellCastCompletion completion =
                _cast?.LastCompletion ?? default;
            if (completion.Revision == 0
                || completion.Revision <= _trackedCastCompletionRevision)
            {
                return;
            }
            _trackedCastCompletionRevision = completion.Revision;
            if (!completion.IsSuccess
                || completion.TargetObjectId == 0u
                || _spellbook is null
                || !_spellbook.TryGetMetadata(
                    completion.SpellId,
                    out SpellMetadata metadata)
                || metadata.Duration <= 0f)
            {
                return;
            }
            TrackEnchantment(
                completion.TargetObjectId,
                metadata,
                metadata.Duration,
                DateTimeOffset.UtcNow);
        }
    }

    private void TrackEnchantment(
        uint targetObjectId,
        SpellMetadata metadata,
        double durationSeconds,
        DateTimeOffset now)
    {
        var tracked = new TrackedEnchantment(
            metadata.Family,
            metadata.Difficulty,
            metadata.IsUntargeted,
            now.AddSeconds(durationSeconds));
        (uint Target, uint Spell) key = (targetObjectId, metadata.SpellId);
        if (!_trackedEnchantments.TryGetValue(key, out TrackedEnchantment old)
            || tracked.ExpiresAt > old.ExpiresAt)
        {
            _trackedEnchantments[key] = tracked;
        }
    }

    private void PruneTrackedEnchantments(DateTimeOffset now)
    {
        foreach ((uint Target, uint Spell) key in
            _trackedEnchantments
                .Where(pair => pair.Value.ExpiresAt <= now)
                .Select(static pair => pair.Key)
                .ToArray())
        {
            _trackedEnchantments.Remove(key);
        }
    }

    // ── ICombatAutomation ────────────────────────────────────────────────
    public PluginCombatSnapshot Snapshot
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            if (runtime is null || !IsAvailable)
                return default;

            RuntimeActionSnapshot action = runtime.ActionOwner.View.Snapshot;
            RuntimeCombatAttackSnapshot attack = action.CombatAttack;
            return new PluginCombatSnapshot(
                action.SelectedObjectId,
                Project(action.CombatMode),
                Project(attack.RequestedHeight),
                attack.DesiredPower,
                attack.PowerBarLevel,
                attack.BuildInProgress,
                attack.RequestInProgress,
                attack.ServerResponsePending,
                attack.RepeatAttackInProgress)
            {
                CompletionRevision = attack.CompletionRevision,
                CompletionSequence = attack.CompletionSequence,
                CompletionWeenieError = attack.CompletionWeenieError,
                QualifiedSelfMotionRevision = runtime.ActionOwner.CombatMode
                    .QualifiedSelfMotionRevision,
                QualifiedSelfMotionAgeSeconds = runtime.ActionOwner.CombatMode
                    .QualifiedSelfMotionAgeSeconds(runtime.Clock.SimulationTimeSeconds),
                ServerMode = runtime.ActionOwner.Combat.ServerMode is { } serverMode
                    ? Project(serverMode)
                    : PluginCombatMode.Unknown,
            };
        }
    }

    public IReadOnlyList<PluginCombatTarget> CaptureHostileTargets(
        float maximumDistance)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable)
            return Array.Empty<PluginCombatTarget>();

        IReadOnlyList<RuntimeHostileTargetSnapshot> captured =
            RuntimeHostileTargetQuery.Capture(runtime, maximumDistance, HostileTargetScope.Classified);
        if (captured.Count == 0)
            return Array.Empty<PluginCombatTarget>();

        var projected = new PluginCombatTarget[captured.Count];
        Func<int, string> speciesName;
        lock (_gate)
            speciesName = _speciesName;
        for (int i = 0; i < captured.Count; i++)
        {
            RuntimeHostileTargetSnapshot target = captured[i];
            projected[i] = new PluginCombatTarget(
                target.ObjectId,
                target.Name,
                target.WeenieClassId,
                target.Distance,
                target.RelativeAngleDegrees,
                target.IsHealthKnown,
                target.HealthFraction)
            {
                SpeciesId = target.SpeciesId,
                SpeciesName = speciesName(target.SpeciesId),
                MaximumHealth = target.MaximumHealth,
                HasShield = target.HasShield,
                Incarnation = target.Incarnation,
                HealthRevision = target.HealthRevision,
                SecondsSinceHealthUpdate = target.SecondsSinceHealthUpdate,
                IsDead = target.IsDead,
            };
        }
        return projected;
    }

    public PluginCombatCommandResult EnterDefaultMode()
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable)
            return new(PluginCombatCommandStatus.Unavailable);
        if (runtime.ActionOwner.Combat.CurrentMode != CombatMode.NonCombat)
            return new(PluginCombatCommandStatus.AlreadyReady);

        RuntimeCombatModeRequestResult result = runtime.ActionOwner.CombatMode.Toggle();
        return result.Status switch
        {
            // Parked behind a motion still running: it goes out by itself
            // on the first frame the body is ready, which is what "sent"
            // means to a plugin that then waits for the mode to change.
            RuntimeCombatModeRequestStatus.Sent
                or RuntimeCombatModeRequestStatus.Deferred => new(
                PluginCombatCommandStatus.ModeChangeSent),
            RuntimeCombatModeRequestStatus.Rejected => new(
                PluginCombatCommandStatus.Refused, result.Notice),
            _ => new(PluginCombatCommandStatus.Unavailable, result.Notice),
        };
    }

    public PluginCombatCommandResult EnterMode(PluginCombatMode mode)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable)
            return new(PluginCombatCommandStatus.Unavailable);
        CombatMode requested = mode switch
        {
            PluginCombatMode.Peace => CombatMode.NonCombat,
            PluginCombatMode.Melee => CombatMode.Melee,
            PluginCombatMode.Missile => CombatMode.Missile,
            PluginCombatMode.Magic => CombatMode.Magic,
            _ => (CombatMode)(-1),
        };
        if ((int)requested < 0)
            return new(PluginCombatCommandStatus.Refused, "Invalid combat mode.");
        if (runtime.ActionOwner.Combat.CurrentMode == requested)
            return new(PluginCombatCommandStatus.AlreadyReady);

        RuntimeCombatModeRequestResult result =
            runtime.ActionOwner.CombatMode.Request(requested);
        return result.Status switch
        {
            // Parked behind a motion still running: it goes out by itself
            // on the first frame the body is ready, which is what "sent"
            // means to a plugin that then waits for the mode to change.
            RuntimeCombatModeRequestStatus.Sent
                or RuntimeCombatModeRequestStatus.Deferred => new(
                PluginCombatCommandStatus.ModeChangeSent),
            RuntimeCombatModeRequestStatus.Rejected => new(
                PluginCombatCommandStatus.Refused, result.Notice),
            _ => new(PluginCombatCommandStatus.Unavailable, result.Notice),
        };
    }

    public PluginCombatCommandResult DismissGhostTarget(uint targetObjectId)
    {
        Func<uint, bool>? dismiss;
        lock (_gate)
            dismiss = _dismissGhost;
        if (dismiss is null || !IsAvailable)
            return new(PluginCombatCommandStatus.Unavailable);
        return dismiss(targetObjectId)
            ? new(PluginCombatCommandStatus.Stopped)
            : new(PluginCombatCommandStatus.InvalidTarget);
    }

    public PluginCombatCommandResult BeginPhysicalAttack(
        uint targetObjectId,
        PluginAttackHeight height,
        float power)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable)
            return new(PluginCombatCommandStatus.Unavailable);
        if (!RuntimeHostileTargetQuery.IsHostile(runtime, targetObjectId, HostileTargetScope.Classified))
            return new(PluginCombatCommandStatus.InvalidTarget);
        if (!CombatInputPlanner.SupportsTargetedAttack(
                runtime.ActionOwner.Combat.CurrentMode))
        {
            return new(PluginCombatCommandStatus.WrongMode);
        }

        RuntimeCombatAttackState attack = runtime.ActionOwner.CombatAttack;
        bool outstanding = attack.AttackRequestInProgress
            || attack.AttackServerResponsePending
            || attack.RepeatAttackInProgress;
        if (outstanding)
        {
            // A swing at the same creature is the ordinary pace of a fight:
            // the caller waits for it. A swing at a different one is the
            // caller having moved on -- most often because the first creature
            // is dead -- and waiting there is what leaves a character standing
            // in front of a corpse while everything else closes in. That swing
            // is ended and the new one starts in the same breath.
            if (_armedAttackTarget == targetObjectId)
                return new(PluginCombatCommandStatus.Busy);
            attack.AbortAutomaticAttack();
        }

        runtime.ActionOwner.Selection.Select(
            targetObjectId,
            SelectionChangeSource.Plugin);
        attack.SetDesiredPower(Math.Clamp(power, 0f, 1f));
        attack.PressAttack(Project(height));
        if (!attack.AttackRequestInProgress)
        {
            _armedAttackTarget = 0u;
            return new(
                PluginCombatCommandStatus.Refused,
                DescribeAttackRefusal(runtime, targetObjectId));
        }
        _armedAttackTarget = targetObjectId;
        return new(PluginCombatCommandStatus.Started);
    }

    /// <summary>
    /// Why a swing that was asked for never armed. Without this a caller sees
    /// only "refused" and has to guess whether the monster is the problem,
    /// which is how an attackable monster standing next to the character ends
    /// up written off for a pass.
    /// </summary>
    internal static string DescribeAttackRefusal(
        GameRuntime runtime,
        uint targetObjectId)
    {
        if (runtime.ActionOwner.Selection.SelectedObjectId != targetObjectId)
            return "Something else took the target before the swing armed.";
        if (!RuntimeHostileTargetQuery.IsHostile(
                runtime,
                targetObjectId,
                HostileTargetScope.Selectable))
        {
            return "The target cannot be attacked: it is out of sight or out of play.";
        }
        return "The character is in no position to attack.";
    }

    public PluginCombatCommandResult ReleasePhysicalAttack()
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable)
            return new(PluginCombatCommandStatus.Unavailable);

        RuntimeCombatAttackState attack = runtime.ActionOwner.CombatAttack;
        if (!attack.AttackRequestInProgress)
            return new(PluginCombatCommandStatus.Refused);
        if (attack.ReleaseAttack())
            return new(PluginCombatCommandStatus.Released);
        // The request ended without a swing leaving the client. Saying
        // "released" here would start a wait on a result that is never coming.
        // The server still holding the last swing is a moment to wait out; the
        // creature no longer being attackable is not.
        if (attack.AttackServerResponsePending)
            return new(PluginCombatCommandStatus.Busy);
        uint armed = _armedAttackTarget;
        _armedAttackTarget = 0u;
        return new(
            PluginCombatCommandStatus.Refused,
            DescribeAttackRefusal(runtime, armed));
    }

    public PluginCombatCommandResult AbortPhysicalAttack()
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null)
            return new(PluginCombatCommandStatus.Unavailable);
        _armedAttackTarget = 0u;
        runtime.ActionOwner.CombatAttack.AbortAutomaticAttack();
        return new(PluginCombatCommandStatus.Stopped);
    }

    public IDisposable? AcquireCombatControl()
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        return runtime is not null && IsAvailable
            ? runtime.ActionOwner.AcquireCombatControl()
            : null;
    }

    private bool SelectExplicitTarget(uint targetObjectId)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable || targetObjectId == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(targetObjectId, out _))
        {
            return false;
        }
        runtime.ActionOwner.Selection.Select(
            targetObjectId,
            SelectionChangeSource.Plugin);
        return true;
    }

    private static PluginCombatMode Project(CombatMode mode) => mode switch
    {
        CombatMode.NonCombat => PluginCombatMode.Peace,
        CombatMode.Melee => PluginCombatMode.Melee,
        CombatMode.Missile => PluginCombatMode.Missile,
        CombatMode.Magic => PluginCombatMode.Magic,
        _ => PluginCombatMode.Unknown,
    };

    private static PluginAttackHeight Project(AttackHeight height) => height switch
    {
        AttackHeight.High => PluginAttackHeight.High,
        AttackHeight.Low => PluginAttackHeight.Low,
        _ => PluginAttackHeight.Medium,
    };

    private static AttackHeight Project(PluginAttackHeight height) => height switch
    {
        PluginAttackHeight.High => AttackHeight.High,
        PluginAttackHeight.Low => AttackHeight.Low,
        _ => AttackHeight.Medium,
    };

    private readonly record struct TrackedEnchantment(
        uint Family,
        int Quality,
        bool IsUntargeted,
        DateTimeOffset ExpiresAt);

    public void Dispose()
    {
        if (_events is not null)
            _events.Tick -= OnPeerTick;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _equip = null;
            _equipSecondary = null;
            _equipmentBusy = null;
            _useItem = null;
            _useWorldObject = null;
            _applyItem = null;
            _moveItem = null;
            _moveItemJoiningStack = null;
            _mergeItems = null;
            _dropItem = null;
            _giveItem = null;
            _pickupItem = null;
            _identifyItem = null;
            _salvageItems = null;
            _sellItem = null;
            _selectionAction = null;
            _navigationCommands?.Dispose();
            _navigationCommands = null;
            _statusCommand?.Dispose();
            _statusCommand = null;
            DetachLocked();
        }
        _armedAttackTarget = 0u;
        _knownSelfBuffs = Array.Empty<PluginSpellInfo>();
        _knownAttackSpells = Array.Empty<PluginSpellInfo>();
        _knownCombatSpells = Array.Empty<PluginSpellInfo>();
        _enchantments = Array.Empty<PluginActiveEnchantment>();
        _peers.Dispose();
        _timedEnchantments = Array.Empty<PluginActiveEnchantment>();
    }
}
