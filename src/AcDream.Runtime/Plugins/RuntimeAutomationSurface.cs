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
      IWorldObjectAutomation, IWorldTimeAutomation,
      ILoginAutomation, INetworkAutomation, IRecoveryAutomation,
      IProjectileAutomation, ISelectionAutomation, IDialogAutomation, IDisposable
{
    private readonly PluginCommandRegistry _pluginCommands;
    private const int MaximumPluginChatMessages = 512;
    private const double PeerHeartbeatSeconds = 5d;
    private readonly object _gate = new();
    private readonly AcDream.Runtime.Navigation.RuntimeNavigationAutomation _navigation;
    private readonly IEvents? _events;
    private readonly WorldEvents? _pluginEvents;
    private readonly LocalPluginPeerRegistry _peers;
    private readonly string[] _peerTags;
    private double _peerHeartbeatRemaining;

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
    private IChargenPaletteColorSource? _paletteColors;
    private Func<uint, uint, bool>? _equip;
    private Func<bool>? _equipmentBusy;
    private Func<bool>? _requestLogout;
    private Func<bool>? _canRequestLogout;
    private Func<uint, bool>? _useItem;
    private Func<uint, PluginItemCommandResult>? _useWorldObject;
    private Func<uint, uint, bool>? _applyItem;
    private Func<uint, uint, uint, int, bool>? _moveItem;
    private Func<uint, uint, uint, bool>? _mergeItems;
    private Func<uint, uint, bool>? _dropItem;
    private Func<uint, uint, uint, bool>? _giveItem;
    private Func<uint, bool, bool>? _pickupItem;
    private Func<uint, bool>? _identifyItem;
    private Func<uint, IReadOnlyList<uint>, bool>? _salvageItems;
    private Func<uint, uint, int, bool>? _sellItem;
    private Func<uint, bool>? _dismissGhost;
    private Func<PluginSelectionAction, bool>? _selectionAction;
    private PhysicsEngine? _projectilePhysics;
    private IReadOnlyList<PluginProjectileDebugSample> _projectileDebugSamples =
        Array.Empty<PluginProjectileDebugSample>();
    private long _projectileDebugSamplesExpireAt;
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
    private IDisposable? _chatFilterInstallation;
    private IDisposable? _runtimeEventSubscription;
    private bool _wasInWorld;
    private Func<uint, bool, bool>? _answerConfirmation;
    private Action<ExternalContainerTransition>? _externalContainerChanged;
    private Action<PluginChatMessage>? _chatReceived;
    private SpellTable? _spellCatalogSource;
    private IReadOnlyList<PluginSpellInfo> _allSpells = Array.Empty<PluginSpellInfo>();
    private long _inventoryCompletionRevision;
    private PluginInventoryCompletion _lastInventoryCompletion;
    private readonly Dictionary<(uint Target, uint Spell), TrackedEnchantment>
        _trackedEnchantments = [];
    private long _trackedCastCompletionRevision;
    private bool _disposed;
    private bool _remoteBodiesUnsimulated;

    private IReadOnlyList<PluginSpellInfo> _knownSelfBuffs = Array.Empty<PluginSpellInfo>();
    private IReadOnlyList<PluginSpellInfo> _knownAttackSpells =
        Array.Empty<PluginSpellInfo>();
    private IReadOnlyList<PluginSpellInfo> _knownCombatSpells =
        Array.Empty<PluginSpellInfo>();
    private IReadOnlyList<PluginActiveEnchantment> _enchantments =
        Array.Empty<PluginActiveEnchantment>();

    private static readonly string[] AttributeNames =
        ["Strength", "Endurance", "Quickness", "Coordination", "Focus", "Self"];

    private readonly Func<uint, IReadOnlyList<uint>> _activeSpellIdsForPlayer;

    public RuntimeAutomationSurface()
        : this(events: null)
    {
    }

    internal RuntimeAutomationSurface(
        IEvents? events,
        LocalPluginPeerRegistry? peers = null,
        IReadOnlyList<string>? peerTags = null)
    {
        _navigation = new AcDream.Runtime.Navigation.RuntimeNavigationAutomation(() => IsAvailable);
        _activeSpellIdsForPlayer = _ => _enchantments.Select(static enchantment => enchantment.SpellId).ToArray();
        _pluginCommands = new PluginCommandRegistry((verb, error) =>
            Console.WriteLine(
                $"[PluginCommand:{verb}] {error.GetBaseException().Message}"));
        _events = events;
        _pluginEvents = events as WorldEvents;
        _peers = peers ?? new LocalPluginPeerRegistry(Path.Combine(
            AcDream.Platform.ApplicationPathSet.Resolve().DataDirectory,
            "plugin-peers"));
        _peerTags = (peerTags ?? Array.Empty<string>())
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Select(static tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(128)
            .ToArray();
        if (_events is not null)
            _events.Tick += OnPeerTick;
    }

    internal IPluginCommandRegistry PluginCommands => _pluginCommands;

    internal bool TryHandlePluginCommand(string commandLine) =>
        _pluginCommands.TryHandle(commandLine);

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
    public IWorldTimeAutomation WorldTime => this;
    public ILoginAutomation Login => this;
    public INetworkAutomation Network => this;
    public IRecoveryAutomation Recovery => this;
    public IProjectileAutomation Projectiles => this;
    public ISelectionAutomation Selection => this;
    public ITradeAutomation Trade
    {
        get { lock (_gate) return (ITradeAutomation?)_tradeAutomation ?? NoOpAutomationSurface.Instance; }
    }
    public IVendorAutomation Vendor
    {
        get { lock (_gate) return (IVendorAutomation?)_vendorAutomation ?? NoOpAutomationSurface.Instance; }
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
        transactions.CompleteUse(0u);
        return new(
            Accepted: true,
            PreviousCount: before,
            CurrentCount: transactions.BusyCount,
            Message: before == 0
                ? "The action busy count was already zero."
                : "Cleared one action busy reference.");
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
        return _peers.CaptureRemoteClients();
    }

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
            _character = character;
            _cast = cast;
            _spellbook = spellbook;
            spellbook.SpellbookChanged += OnSpellbookChanged;
            spellbook.EnchantmentsChanged += OnEnchantmentsChanged;
            runtime.InventoryOwner.Transactions.RequestCompleted +=
                OnInventoryRequestCompleted;
            runtime.InventoryOwner.Transactions.RequestFailed +=
                OnInventoryRequestFailed;
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

    public void BindPaletteColorResolver(IChargenPaletteColorSource resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (_gate)
            _paletteColors = resolver;
    }

    // A host that never moves a remote entity's PhysicsBody reads its position from the snapshot instead.
    internal void BindRemoteBodiesUnsimulated()
    {
        lock (_gate)
            _remoteBodiesUnsimulated = true;
        _navigation.BindRemoteBodiesUnsimulated();
    }

    public void BindEquipment(
        Func<uint, uint, bool> equip,
        Func<bool> isBusy)
    {
        ArgumentNullException.ThrowIfNull(equip);
        ArgumentNullException.ThrowIfNull(isBusy);
        lock (_gate)
        {
            _equip = equip;
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
        Func<uint, bool, bool> pickupItem,
        Func<uint, bool> identifyItem,
        Func<uint, IReadOnlyList<uint>, bool>? salvageItems = null,
        Func<uint, uint, int, bool>? sellItem = null)
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
        _ => new PluginItemCommandResult(PluginItemCommandStatus.Unavailable),
    };

    public void BindGhostDeletion(Func<uint, bool> dismissGhost)
    {
        ArgumentNullException.ThrowIfNull(dismissGhost);
        lock (_gate)
            _dismissGhost = dismissGhost;
    }

    public void BindProjectileCollision(PhysicsEngine physics)
    {
        ArgumentNullException.ThrowIfNull(physics);
        lock (_gate)
            _projectilePhysics = physics;
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
    }

    private void DetachLocked()
    {
        if (_runtime is { } runtime)
        {
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
        _trackedEnchantments.Clear();
        _trackedCastCompletionRevision = 0;
        _projectileDebugSamples = Array.Empty<PluginProjectileDebugSample>();
        _projectileDebugSamplesExpireAt = 0;
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

    // Graphical host only, driven from the plugin event tick.
    private void OnPeerTick(double elapsedSeconds)
    {
        Poll();

        _peerHeartbeatRemaining -= Math.Max(0d, elapsedSeconds);
        if (_peerHeartbeatRemaining > 0d)
            return;
        _peerHeartbeatRemaining = PeerHeartbeatSeconds;
        PublishPeerSnapshot();
    }

    private void PublishPeerSnapshot()
    {
        if (!IsAvailable)
        {
            _peers.Withdraw();
            return;
        }

        ICharacterInfo character = this;
        PluginNavigationSnapshot navigation =
            _navigation.Snapshot;
        if (!navigation.IsAvailable || character.ObjectId == 0u)
        {
            _peers.Withdraw();
            return;
        }

        try
        {
            _peers.Publish(new PluginNetworkClient(
                _peers.ClientId,
                character.ObjectId,
                character.Name,
                character.WorldName,
                navigation.Position,
                _peerTags,
                character.CurrentHealth,
                character.CurrentMana,
                character.CurrentStamina,
                character.MaxHealth,
                character.MaxMana,
                character.MaxStamina,
                navigation.Position.HeadingDegrees));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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

    private void RebuildEnchantments()
    {
        Spellbook? spellbook;
        lock (_gate)
            spellbook = _spellbook;
        if (spellbook is null)
        {
            _enchantments = Array.Empty<PluginActiveEnchantment>();
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
                record.SpellId, family, tier, record.Duration));
        }
        _enchantments = built;
    }

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

    public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => _enchantments;

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
                    });
                }
            }
            return built;
        }
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
    void IRuntimeEventObserver.OnLifecycle(in RuntimeLifecycleDelta delta)
    {
        bool isInWorld = delta.Current == RuntimeLifecycleState.InWorld;
        lock (_gate)
        {
            if (_disposed || _wasInWorld == isInWorld)
                return;
            _wasInWorld = isInWorld;
        }

        if (isInWorld)
            _pluginEvents?.FireLoginComplete();
        else
            _pluginEvents?.FireLogoff();
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
        WorldEvents? events = _pluginEvents;
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
        events.FireObjectChanged(new PluginObjectChange(
            delta.Entity.Identity.ServerGuid,
            kind));
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
        WorldEvents? events = _pluginEvents;
        if (events is null)
            return;
        PluginObjectChangeKind kind = delta.Change switch
        {
            RuntimeInventoryChange.Added => PluginObjectChangeKind.Created,
            RuntimeInventoryChange.Moved => PluginObjectChangeKind.Moved,
            RuntimeInventoryChange.Removed => PluginObjectChangeKind.Released,
            _ => PluginObjectChangeKind.Updated,
        };
        events.FireObjectChanged(new PluginObjectChange(
            delta.Item.ObjectId,
            kind));
    }

    void IRuntimeEventObserver.OnChat(in RuntimeChatDelta delta) { }
    void IRuntimeEventObserver.OnMovement(in RuntimeMovementDelta delta) { }
    void IRuntimeEventObserver.OnPortal(in RuntimePortalDelta delta) { }
    void IRuntimeEventObserver.OnCombat(in RuntimeCombatDelta delta) { }

    private void OnLocalPlayerDied(string deathMessage) =>
        _pluginEvents?.FireLocalPlayerDied(deathMessage);

    private void OnExternalContainerChanged(ExternalContainerTransition transition)
    {
        WorldEvents? events = _pluginEvents;
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

    private void OnAppraisalReceived(uint objectId) =>
        _pluginEvents?.FireObjectChanged(new PluginObjectChange(
            objectId,
            PluginObjectChangeKind.IdentReceived));

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
        _pluginEvents?.FireConfirmationRequested(confirmation);

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
            runtime.Clock.SimulationTimeSeconds,
            out double remaining)
                ? Math.Max(0d, remaining)
                : 0d;
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
                return !_disposed && _projectilePhysics is not null && IsAvailable;
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
        PhysicsEngine? physics;
        lock (_gate)
        {
            runtime = _runtime;
            physics = _projectilePhysics;
        }
        if (runtime is null || physics is null || !IsAvailable)
            return new(PluginProjectilePathStatus.Unavailable);
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
        if (!runtime.EntityObjects.Entities.TryGetActive(
                localId,
                out RuntimeEntityRecord local)
            || !runtime.EntityObjects.Entities.TryGetActive(
                targetObjectId,
                out RuntimeEntityRecord target)
            || local.PhysicsBody is not { } localBody
            || target.PhysicsBody is not { } targetBody
            || localBody.CellPosition.ObjCellId == 0u)
        {
            return new(PluginProjectilePathStatus.InvalidTarget);
        }

        try
        {
            return EvaluateProjectilePath(
                physics,
                localId,
                localBody,
                targetObjectId,
                targetBody,
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
        PhysicsBody target,
        PluginProjectilePathKind kind,
        PluginAttackHeight targetHeight,
        float radius,
        float stepDistance,
        int maximumChecks,
        bool captureDiagnostics)
    {
        System.Numerics.Vector3 baseDelta = target.Position - local.Position;
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
        var destination = target.Position + new System.Numerics.Vector3(
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
        if (!runtime.InventoryOwner.Transactions.CanBeginRequest)
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
            activeSpellIdsForPlayer: _activeSpellIdsForPlayer,
            remoteBodiesUnsimulated: _remoteBodiesUnsimulated);

    private static bool HasPropertyData(PropertyBundle properties) =>
        RuntimeWorldObjectProjection.HasPropertyData(properties);

    internal static PluginObjectClass ClassifyObject(ClientObject? item) =>
        RuntimeWorldObjectProjection.ClassifyObject(item);

    private static Position? ConvertPosition(
        AcDream.Core.Net.Messages.CreateObject.ServerPosition? position) =>
        RuntimeWorldObjectProjection.ConvertPosition(position);

    public bool IsCasting
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime is not null
                && runtime.InventoryOwner.Transactions.BusyCount > 0;
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
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
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
                StackSize = Math.Max(1, item.StackSize),
                WeaponType = item.Properties.GetInt(
                    (uint)PropertyInt.WeaponType),
            });
        }
        built.Sort(static (left, right) =>
        {
            int equipped = right.IsEquipped.CompareTo(left.IsEquipped);
            if (equipped != 0)
                return equipped;
            int name = string.CompareOrdinal(left.Name, right.Name);
            return name != 0
                ? name
                : left.ObjectId.CompareTo(right.ObjectId);
        });
        return built;
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
            return runtime is not null
                && !runtime.InventoryOwner.Transactions.CanBeginRequest;
        }
    }

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
    {
        Func<uint, uint, uint, int, bool>? move;
        GameRuntime? runtime;
        lock (_gate)
        {
            move = _moveItem;
            runtime = _runtime;
        }
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
        if (!runtime.InventoryOwner.Transactions.CanBeginRequest)
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
            return runtime is not null
                && !runtime.InventoryOwner.Transactions.CanBeginRequest;
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
                        transactions.AwaitingAppraisalId,
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
                        transactions.LastCompletedAppraisalId);
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

            result.Add(new PluginLootContainer(
                candidate.ObjectId,
                candidate.WeenieClassId,
                candidate.Name,
                distance,
                external.HasCorpseBeenOpened(candidate.ObjectId),
                external.RequestedContainerId == candidate.ObjectId,
                external.CurrentContainerId == candidate.ObjectId)
            {
                LongDescription = candidate.Properties.GetString(
                    (uint)PropertyString.LongDesc),
                IsGeneratedRare = candidate.Properties.GetBool(
                    (uint)PropertyBool.CorpseGeneratedRare),
                IsIdentified = candidate.Properties.Strings.ContainsKey(
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
        lock (_gate)
        {
            runtime = _runtime;
            use = _useItem;
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
        if (!runtime.InventoryOwner.Transactions.CanBeginRequest)
            return new(PluginItemCommandStatus.Busy);
        return use(containerObjectId)
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
        if (item is null || (!corpse && !currentContent))
        {
            return new(PluginItemCommandStatus.InvalidItem);
        }
        if (!runtime.InventoryOwner.Transactions.CanBeginRequest)
            return new(PluginItemCommandStatus.Busy);
        return identify(objectId)
            ? new(PluginItemCommandStatus.Started)
            : new(PluginItemCommandStatus.Refused);
    }

    public PluginItemCommandResult Pickup(uint objectId, bool mainPack = false)
    {
        GameRuntime? runtime;
        Func<uint, bool, bool>? pickup;
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
        return pickup(objectId, mainPack)
            ? new(PluginItemCommandStatus.Started)
            : new(PluginItemCommandStatus.Refused);
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
            weapon is { } wd
                ? ClientAppraisalProfileMapper.NormalizeDamage(wd.Damage)
                : item.Properties.GetInt((uint)PropertyInt.Damage),
            weapon is { } wv
                ? wv.DamageVariance
                : item.Properties.GetFloat((uint)PropertyFloat.DamageVariance),
            item.Properties.GetInt((uint)PropertyInt.UseRequiresSkill),
            item.Properties.GetInt((uint)PropertyInt.UseRequiresSkillLevel),
            item.Properties.GetInt((uint)PropertyInt.UseRequiresSkillSpec))
        {
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
            MaterialType = item.MaterialType ?? 0u,
            ObjectClass = ClassifyObject(item),
            Palettes = ProjectPalettes(runtime, item.ObjectId),
            IconId = item.IconId,
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
            int sampleIndex = (palette.Length * 16) + (palette.Offset * 32) + 8;
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
            });
        }
        return result.Count == 0
            ? Array.Empty<PluginFellowMember>()
            : result.ToArray();
    }

    // ── IEnchantmentAutomation ──────────────────────────────────────────
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
            RuntimeHostileTargetQuery.Capture(runtime, maximumDistance);
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
            RuntimeCombatModeRequestStatus.Sent => new(
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
            RuntimeCombatModeRequestStatus.Sent => new(
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
        if (!RuntimeHostileTargetQuery.IsHostile(runtime, targetObjectId))
            return new(PluginCombatCommandStatus.InvalidTarget);
        if (!CombatInputPlanner.SupportsTargetedAttack(
                runtime.ActionOwner.Combat.CurrentMode))
        {
            return new(PluginCombatCommandStatus.WrongMode);
        }

        RuntimeCombatAttackState attack = runtime.ActionOwner.CombatAttack;
        if (attack.AttackRequestInProgress
            || attack.AttackServerResponsePending
            || attack.RepeatAttackInProgress)
        {
            return new(PluginCombatCommandStatus.Busy);
        }

        runtime.ActionOwner.Selection.Select(
            targetObjectId,
            SelectionChangeSource.Plugin);
        attack.SetDesiredPower(Math.Clamp(power, 0f, 1f));
        attack.PressAttack(Project(height));
        return attack.AttackRequestInProgress
            ? new(PluginCombatCommandStatus.Started)
            : new(PluginCombatCommandStatus.Refused);
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
        attack.ReleaseAttack();
        return new(PluginCombatCommandStatus.Released);
    }

    public PluginCombatCommandResult AbortPhysicalAttack()
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null)
            return new(PluginCombatCommandStatus.Unavailable);
        runtime.ActionOwner.CombatAttack.AbortAutomaticAttack();
        return new(PluginCombatCommandStatus.Stopped);
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
            _equipmentBusy = null;
            _useItem = null;
            _useWorldObject = null;
            _applyItem = null;
            _moveItem = null;
            _mergeItems = null;
            _dropItem = null;
            _giveItem = null;
            _pickupItem = null;
            _identifyItem = null;
            _salvageItems = null;
            _sellItem = null;
            _selectionAction = null;
            DetachLocked();
        }
        _knownSelfBuffs = Array.Empty<PluginSpellInfo>();
        _knownAttackSpells = Array.Empty<PluginSpellInfo>();
        _knownCombatSpells = Array.Empty<PluginSpellInfo>();
        _enchantments = Array.Empty<PluginActiveEnchantment>();
        _peers.Dispose();
    }
}
