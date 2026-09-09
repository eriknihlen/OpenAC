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
using AcDream.App.Runtime;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.App.Plugins;

internal sealed class AppAutomationSurface
    : IAutomationSurface, ICharacterInfo, ISpellCatalog, IMagicCommands, IPluginChat,
      ICombatAutomation, IEquipmentAutomation, IItemAutomation,
      ILootAutomation, IFellowshipAutomation, IEnchantmentAutomation,
      IRuntimeCommunicationObserver,
      INavigationAutomation, IWorldObjectAutomation, IWorldTimeAutomation,
      ILoginAutomation, INetworkAutomation, IRecoveryAutomation,
      IProjectileAutomation, ISelectionAutomation, IDisposable
{
    private readonly PluginCommandRegistry _pluginCommands;
    private const int MaximumPluginChatMessages = 512;
    private const double PeerHeartbeatSeconds = 5d;
    private readonly object _gate = new();
    private readonly IEvents? _events;
    private readonly LocalPluginPeerRegistry _peers;
    private readonly string[] _peerTags;
    private double _peerHeartbeatRemaining;

    private GameRuntime? _runtime;
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
    private Func<uint, bool>? _useItem;
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
    private CurrentGameRuntimeAdapter? _sessionCommands;
    private IDisposable? _communicationSubscription;
    private readonly List<PluginChatMessage> _chatMessages = [];
    private ulong _pluginChatSequence;
    private long _inventoryCompletionRevision;
    private PluginInventoryCompletion _lastInventoryCompletion;
    private readonly Dictionary<(uint Target, uint Spell), TrackedEnchantment>
        _trackedEnchantments = [];
    private long _trackedCastCompletionRevision;
    private bool _disposed;

    private IReadOnlyList<PluginSpellInfo> _knownSelfBuffs = Array.Empty<PluginSpellInfo>();
    private IReadOnlyList<PluginSpellInfo> _knownAttackSpells =
        Array.Empty<PluginSpellInfo>();
    private IReadOnlyList<PluginSpellInfo> _knownCombatSpells =
        Array.Empty<PluginSpellInfo>();
    private IReadOnlyList<PluginActiveEnchantment> _enchantments =
        Array.Empty<PluginActiveEnchantment>();

    private static readonly string[] AttributeNames =
        ["Strength", "Endurance", "Quickness", "Coordination", "Focus", "Self"];

    public AppAutomationSurface()
        : this(events: null)
    {
    }

    internal AppAutomationSurface(
        IEvents? events,
        LocalPluginPeerRegistry? peers = null,
        IReadOnlyList<string>? peerTags = null)
    {
        _pluginCommands = new PluginCommandRegistry((verb, error) =>
            Console.WriteLine(
                $"[PluginCommand:{verb}] {error.GetBaseException().Message}"));
        _events = events;
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
    public INavigationAutomation Navigation => this;
    public IWorldObjectAutomation Objects => this;
    public IWorldTimeAutomation WorldTime => this;
    public ILoginAutomation Login => this;
    public INetworkAutomation Network => this;
    public IRecoveryAutomation Recovery => this;
    public IProjectileAutomation Projectiles => this;
    public ISelectionAutomation Selection => this;

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
            _communication = runtime.CommunicationOwner;
            _communicationSubscription =
                runtime.CommunicationOwner.Events.Subscribe(this);
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

    public void BindSessionCommands(CurrentGameRuntimeAdapter commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        lock (_gate)
            _sessionCommands = commands;
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
        }
        _communicationSubscription?.Dispose();
        _communicationSubscription = null;
        _chatMessages.Clear();
        if (_spellbook is not null)
        {
            _spellbook.SpellbookChanged -= OnSpellbookChanged;
            _spellbook.EnchantmentsChanged -= OnEnchantmentsChanged;
        }
        _spellbook = null;
        _character = null;
        _cast = null;
        _runtime = null;
        _communication = null;
        _dismissGhost = null;
        _trackedEnchantments.Clear();
        _trackedCastCompletionRevision = 0;
        _projectileDebugSamples = Array.Empty<PluginProjectileDebugSample>();
        _projectileDebugSamplesExpireAt = 0;
    }

    private void OnPeerTick(double elapsedSeconds)
    {
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
            ((INavigationAutomation)this).Snapshot;
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
            if (runtime is null)
                return string.Empty;
            uint playerId = runtime.PlayerIdentity.ServerGuid;
            return runtime.InventoryOwner.Objects.Get(playerId)?.Name
                ?? string.Empty;
        }
    }

    public string WorldName
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime?.CharacterSelection.Snapshot.WorldName ?? string.Empty;
        }
    }

    public string AccountName
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            return runtime?.CharacterSelection.Snapshot.AccountName ?? string.Empty;
        }
    }

    public int CharacterIndex
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            if (runtime is null
                || !runtime.CharacterSelection.TryGet(
                    runtime.PlayerIdentity.ServerGuid,
                    out RuntimeCharacterSelectionEntry character))
            {
                return -1;
            }
            return character.ActiveIndex;
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

    public void OnChat(in RuntimeCommunicationEvent delta)
    {
        lock (_gate)
        {
            if (_disposed || _communication is null)
                return;
            RuntimeChatEntry entry = delta.Entry;
            _chatMessages.Add(new PluginChatMessage(
                ++_pluginChatSequence,
                entry.SenderGuid,
                entry.Kind,
                entry.Sender,
                entry.Text,
                entry.ChannelName));
            if (_chatMessages.Count > MaximumPluginChatMessages)
            {
                _chatMessages.RemoveRange(
                    0,
                    _chatMessages.Count - MaximumPluginChatMessages);
            }
        }
    }

    public void PostSystemMessage(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        RuntimeCommunicationState? communication;
        lock (_gate)
            communication = _communication;
        communication?.AddText(text, RetailLogTextType.Default);
    }

    public bool Submit(string text)
    {
        CurrentGameRuntimeAdapter? commands;
        lock (_gate)
            commands = _sessionCommands;
        return commands?.SubmitChatText(text) == true;
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

    // ── INavigationAutomation ─────────────────────────────────────────────
    PluginNavigationSnapshot INavigationAutomation.Snapshot
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            if (runtime is null || !IsAvailable)
                return default;

            RuntimeMovementSnapshot movement = runtime.Movement.Snapshot;
            if (!movement.HasController)
                return default;
            RuntimePortalSnapshot portal = runtime.Portal.Snapshot;
            PluginNavigationPosition livePosition =
                ProjectNavigationPosition(movement.Position);
            PluginNavigationPosition confirmedPosition = livePosition;
            ulong confirmedRevision = 0UL;
            if (runtime.EntityObjects.Entities.TryGetActive(
                    runtime.PlayerIdentity.ServerGuid,
                    out RuntimeEntityRecord localRecord)
                && ConvertPosition(localRecord.Snapshot.Position) is { } accepted)
            {
                confirmedPosition = ProjectNavigationPosition(accepted);
                confirmedRevision = localRecord.PositionAuthorityVersion;
            }
            return new PluginNavigationSnapshot(
                IsAvailable: true,
                IsPortalSpace: portal.Kind != RuntimePortalKind.None
                    && !portal.Completed
                    && !portal.Cancelled,
                LocalObjectId: runtime.PlayerIdentity.ServerGuid,
                Position: livePosition,
                IsMoving: movement.Velocity.LengthSquared() > 0.0001f
                    || movement.HasCommandInput,
                IsAirborne: movement.IsAirborne)
            {
                ConfirmedPosition = confirmedPosition,
                ConfirmedPositionRevision = confirmedRevision,
            };
        }
    }

    public bool TryGetObject(uint objectId, out PluginNavigationObject value)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable || objectId == 0u)
        {
            value = default;
            return false;
        }

        RuntimeMovementSnapshot movement = runtime.Movement.Snapshot;
        if (objectId == runtime.PlayerIdentity.ServerGuid)
        {
            value = new PluginNavigationObject(
                objectId,
                runtime.InventoryOwner.Objects.Get(objectId)?.Name
                    ?? string.Empty,
                ProjectNavigationPosition(movement.Position));
            return movement.HasController;
        }

        if (!runtime.EntityObjects.Entities.TryGetActive(
                objectId,
                out RuntimeEntityRecord record))
        {
            value = default;
            return false;
        }

        Position? position = record.PhysicsBody?.CellPosition
            ?? ConvertPosition(record.Snapshot.Position);
        if (position is not { } current)
        {
            value = default;
            return false;
        }
        value = new PluginNavigationObject(
            objectId,
            runtime.InventoryOwner.Objects.Get(objectId)?.Name
                ?? record.Snapshot.Name
                ?? $"0x{objectId:X8}",
            ProjectNavigationPosition(current));
        value = EnrichNavigationObject(
            value,
            runtime.InventoryOwner.Objects.Get(objectId));
        return true;
    }

    public bool TryFindObject(
        string name,
        in PluginNavigationPosition near,
        double maximumDistanceMeters,
        out PluginNavigationObject value)
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null
            || !IsAvailable
            || string.IsNullOrWhiteSpace(name)
            || !double.IsFinite(maximumDistanceMeters)
            || maximumDistanceMeters < 0d)
        {
            value = default;
            return false;
        }

        double nearestDistance = maximumDistanceMeters;
        PluginNavigationObject nearest = default;
        bool found = false;
        foreach (RuntimeEntityRecord record in runtime.EntityObjects.Entities.ActiveRecords)
        {
            uint objectId = record.ServerGuid;
            string candidateName = runtime.InventoryOwner.Objects.Get(objectId)?.Name
                ?? record.Snapshot.Name
                ?? string.Empty;
            if (!candidateName.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            Position? source = record.PhysicsBody?.CellPosition
                ?? ConvertPosition(record.Snapshot.Position);
            if (source is not { } position)
                continue;
            PluginNavigationPosition candidate = ProjectNavigationPosition(position);
            double distance = near.HorizontalDistanceMeters(candidate);
            if (distance > nearestDistance)
                continue;

            nearestDistance = distance;
            nearest = EnrichNavigationObject(
                new PluginNavigationObject(objectId, candidateName, candidate),
                runtime.InventoryOwner.Objects.Get(objectId));
            found = true;
        }

        value = nearest;
        return found;
    }

    public IReadOnlyList<PluginNavigationObject> CaptureObjects()
    {
        GameRuntime? runtime;
        lock (_gate)
            runtime = _runtime;
        if (runtime is null || !IsAvailable)
            return Array.Empty<PluginNavigationObject>();

        var result = new List<PluginNavigationObject>();
        foreach (RuntimeEntityRecord record in runtime.EntityObjects.Entities.ActiveRecords)
        {
            Position? source = record.PhysicsBody?.CellPosition
                ?? ConvertPosition(record.Snapshot.Position);
            if (source is not { } position)
                continue;
            ClientObject? item = runtime.InventoryOwner.Objects.Get(record.ServerGuid);
            string name = item?.Name
                ?? record.Snapshot.Name
                ?? $"0x{record.ServerGuid:X8}";
            result.Add(EnrichNavigationObject(
                new PluginNavigationObject(
                    record.ServerGuid,
                    name,
                    ProjectNavigationPosition(position)),
                item));
        }
        result.Sort(static (left, right) => left.ObjectId.CompareTo(right.ObjectId));
        return result;
    }

    public PluginNavigationCommandStatus SetMovementIntent(
        in PluginMovementIntent intent)
    {
        CurrentGameRuntimeAdapter? commands;
        lock (_gate)
            commands = _sessionCommands;
        if (commands is null || !IsAvailable)
            return PluginNavigationCommandStatus.Unavailable;
        RuntimeCommandResult result = commands.MovementCommands.SetIntent(
            commands.Generation,
            new MovementInput(
                intent.Forward,
                intent.Backward,
                intent.StrafeLeft,
                intent.StrafeRight,
                intent.TurnLeft,
                intent.TurnRight,
                intent.Run,
                MouseDeltaX: 0f,
                intent.Jump));
        return result.Status == RuntimeCommandStatus.Accepted
            ? PluginNavigationCommandStatus.Accepted
            : PluginNavigationCommandStatus.Rejected;
    }

    public PluginNavigationCommandStatus ClearMovementIntent()
    {
        CurrentGameRuntimeAdapter? commands;
        lock (_gate)
            commands = _sessionCommands;
        if (commands is null || !IsAvailable)
            return PluginNavigationCommandStatus.Unavailable;
        RuntimeCommandResult result = commands.MovementCommands.ClearIntent(
            commands.Generation);
        return result.Status == RuntimeCommandStatus.Accepted
            ? PluginNavigationCommandStatus.Accepted
            : PluginNavigationCommandStatus.Rejected;
    }

    public PluginNavigationCommandStatus FaceHeading(float headingDegrees)
    {
        CurrentGameRuntimeAdapter? commands;
        lock (_gate)
            commands = _sessionCommands;
        if (commands is null || !IsAvailable)
            return PluginNavigationCommandStatus.Unavailable;
        RuntimeCommandResult result =
            commands.MovementCommands.TurnToHeading(
                commands.Generation,
                headingDegrees);
        return result.Status == RuntimeCommandStatus.Accepted
            ? PluginNavigationCommandStatus.Accepted
            : PluginNavigationCommandStatus.Rejected;
    }

    internal static PluginNavigationPosition ProjectNavigationPosition(
        Position position)
    {
        uint cellId = position.ObjCellId;
        uint blockX = (cellId >> 24) & 0xFFu;
        uint blockY = (cellId >> 16) & 0xFFu;
        System.Numerics.Vector3 local = position.Frame.Origin;
        return new PluginNavigationPosition(
            cellId,
            (((double)blockX - 127d) * 192d + local.X - 84d) / 240d,
            (((double)blockY - 127d) * 192d + local.Y - 84d) / 240d,
            local.Z / 240d,
            MoveToMath.GetHeading(position.Frame.Orientation),
            (cellId & 0xFFFFu) is >= 1u and <= 0x40u);
    }

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
        properties = CaptureProperties(item.Properties);
        return true;
    }

    PluginItemCommandResult IWorldObjectAutomation.Identify(uint objectId) =>
        ((ILootAutomation)this).Identify(objectId);

    private PluginWorldObject ProjectWorldObject(
        GameRuntime runtime,
        RuntimeEntityRecord? record,
        ClientObject? item,
        uint playerId)
    {
        uint objectId = record?.ServerGuid ?? item!.ObjectId;
        Position? source = record?.PhysicsBody?.CellPosition
            ?? (record is null ? null : ConvertPosition(record.Snapshot.Position));
        bool owned = item is not null
            && IsPlayerOwned(item, playerId, runtime.InventoryOwner.Objects);
        IReadOnlyList<uint> activeSpells = objectId == playerId
            ? _enchantments.Select(static enchantment => enchantment.SpellId).ToArray()
            : Array.Empty<uint>();
        uint publicFlags = item?.PublicWeenieBitfield ?? 0u;
        return new PluginWorldObject(
            objectId,
            item?.WeenieClassId ?? 0u,
            item?.Name ?? record?.Snapshot.Name ?? $"0x{objectId:X8}",
            ClassifyObject(item),
            (uint)(item?.Type ?? ItemType.None),
            item?.ContainerId ?? 0u,
            item?.WielderId ?? 0u)
        {
            IsOwned = owned,
            IsLandscape = source is not null
                && !owned
                && (item?.ContainerId ?? 0u) == 0u
                && (item?.WielderId ?? 0u) == 0u,
            HasPosition = source is not null,
            Position = source is { } position
                ? ProjectNavigationPosition(position)
                : default,
            HasAppraisalData = item is not null && HasPropertyData(item.Properties),
            LastIdTime = item?.LastAppraisalTimeMs ?? 0,
            IsDoorOpen = (publicFlags & (uint)PublicWeenieFlags.Door) != 0u
                && (item?.Properties.GetBool((uint)PropertyBool.Open) ?? false),
            StackSize = Math.Max(1, item?.StackSize ?? 1),
            ItemsCapacity = item?.ItemsCapacity ?? 0,
            ContainersCapacity = item?.ContainersCapacity ?? 0,
            SpellIds = item?.AppraisedSpellIds.Count > 0
                ? item.AppraisedSpellIds.ToArray()
                : Array.Empty<uint>(),
            ActiveSpellIds = activeSpells,
            IconId = item?.IconId ?? 0u,
        };
    }

    private static bool HasPropertyData(PropertyBundle properties) =>
        properties.Ints.Count != 0
        || properties.Int64s.Count != 0
        || properties.Bools.Count != 0
        || properties.Floats.Count != 0
        || properties.Strings.Count != 0
        || properties.DataIds.Count != 0
        || properties.InstanceIds.Count != 0;

    internal static PluginObjectClass ClassifyObject(ClientObject? item)
    {
        if (item is null)
            return PluginObjectClass.Unknown;
        uint type = (uint)item.Type;
        uint flags = item.PublicWeenieBitfield ?? 0u;
        PluginObjectClass result = type switch
        {
            _ when (type & 0x00000001u) != 0u => PluginObjectClass.MeleeWeapon,
            _ when (type & 0x00000002u) != 0u => PluginObjectClass.Armor,
            _ when (type & 0x00000004u) != 0u => PluginObjectClass.Clothing,
            _ when (type & 0x00000008u) != 0u => PluginObjectClass.Jewelry,
            _ when (type & 0x00000010u) != 0u => PluginObjectClass.Monster,
            _ when (type & 0x00000020u) != 0u => PluginObjectClass.Food,
            _ when (type & 0x00000040u) != 0u => PluginObjectClass.Money,
            _ when (type & 0x00000080u) != 0u => PluginObjectClass.Misc,
            _ when (type & 0x00000100u) != 0u => PluginObjectClass.MissileWeapon,
            _ when (type & 0x00000200u) != 0u => PluginObjectClass.Container,
            _ when (type & 0x00000400u) != 0u => PluginObjectClass.Bundle,
            _ when (type & 0x00000800u) != 0u => PluginObjectClass.Gem,
            _ when (type & 0x00001000u) != 0u => PluginObjectClass.SpellComponent,
            _ when (type & 0x00004000u) != 0u => PluginObjectClass.Key,
            _ when (type & 0x00008000u) != 0u => PluginObjectClass.WandStaffOrb,
            _ when (type & 0x00010000u) != 0u => PluginObjectClass.Portal,
            _ when (type & 0x00040000u) != 0u => PluginObjectClass.TradeNote,
            _ when (type & 0x00080000u) != 0u => PluginObjectClass.ManaStone,
            _ when (type & 0x00100000u) != 0u => PluginObjectClass.Services,
            _ when (type & 0x00200000u) != 0u => PluginObjectClass.Plant,
            _ when (type & 0x00400000u) != 0u => PluginObjectClass.BaseCooking,
            _ when (type & 0x00800000u) != 0u => PluginObjectClass.BaseAlchemy,
            _ when (type & 0x01000000u) != 0u => PluginObjectClass.BaseFletching,
            _ when (type & 0x02000000u) != 0u => PluginObjectClass.CraftedCooking,
            _ when (type & 0x04000000u) != 0u => PluginObjectClass.CraftedAlchemy,
            _ when (type & 0x08000000u) != 0u => PluginObjectClass.CraftedFletching,
            _ when (type & 0x20000000u) != 0u => PluginObjectClass.Ust,
            _ when (type & 0x40000000u) != 0u => PluginObjectClass.Salvage,
            _ => PluginObjectClass.Unknown,
        };

        result = flags switch
        {
            _ when (flags & 0x00000008u) != 0u => PluginObjectClass.Player,
            _ when (flags & 0x00000200u) != 0u => PluginObjectClass.Vendor,
            _ when (flags & 0x00001000u) != 0u => PluginObjectClass.Door,
            _ when (flags & 0x00002000u) != 0u => PluginObjectClass.Corpse,
            _ when (flags & 0x00004000u) != 0u => PluginObjectClass.Lifestone,
            _ when (flags & 0x00008000u) != 0u => PluginObjectClass.Food,
            _ when (flags & 0x00010000u) != 0u => PluginObjectClass.HealingKit,
            _ when (flags & 0x00020000u) != 0u => PluginObjectClass.Lockpick,
            _ when (flags & 0x00040000u) != 0u => PluginObjectClass.Portal,
            _ when (flags & 0x00800000u) != 0u => PluginObjectClass.Foci,
            _ when (flags & 0x00000001u) != 0u => PluginObjectClass.Container,
            _ => result,
        };

        if ((type & 0x00002000u) != 0u && result == PluginObjectClass.Unknown)
        {
            result = (flags & 0x00000002u) != 0u
                ? PluginObjectClass.Journal
                : (flags & 0x00000004u) != 0u
                    ? PluginObjectClass.Sign
                    : (flags & 0x0000000Fu) != 0u
                        ? PluginObjectClass.Book
                        : result;
        }
        if ((type & 0x00002000u) != 0u && item.SpellId is > 0u)
            result = PluginObjectClass.Scroll;
        if (result == PluginObjectClass.Monster && (flags & 0x10u) == 0u)
            result = PluginObjectClass.Npc;
        if (result == PluginObjectClass.Monster && (flags & 0x04000000u) != 0u)
            result = PluginObjectClass.CombatPet;
        return result;
    }

    private static PluginNavigationObject EnrichNavigationObject(
        in PluginNavigationObject value,
        ClientObject? item)
    {
        if (item is null)
            return value;
        bool hasOpen = item.Properties.Bools.TryGetValue(
            (uint)PropertyBool.Open,
            out bool isOpen);
        bool hasLocked = item.Properties.Bools.TryGetValue(
            (uint)PropertyBool.Locked,
            out bool isLocked);
        return value with
        {
            IsDoor = ((PublicWeenieFlags)(item.PublicWeenieBitfield ?? 0u)
                & PublicWeenieFlags.Door) != 0,
            IsOpen = hasOpen && isOpen,
            IsLocked = hasLocked && isLocked,
            HasLockState = hasOpen || hasLocked,
            LockDifficulty = item.Properties.GetInt(
                (uint)PropertyInt.ResistLockpick),
        };
    }

    private static Position? ConvertPosition(
        AcDream.Core.Net.Messages.CreateObject.ServerPosition? position) =>
        position is not { } value
            ? null
            : new Position(
                value.LandblockId,
                new System.Numerics.Vector3(
                    value.PositionX,
                    value.PositionY,
                    value.PositionZ),
                new System.Numerics.Quaternion(
                    value.RotationX,
                    value.RotationY,
                    value.RotationZ,
                    value.RotationW));

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
        PropertyBundle source = item!.Properties;
        properties = new PluginItemProperties(
            new Dictionary<uint, int>(source.Ints),
            new Dictionary<uint, long>(source.Int64s),
            new Dictionary<uint, bool>(source.Bools),
            new Dictionary<uint, double>(source.Floats),
            new Dictionary<uint, string>(source.Strings),
            new Dictionary<uint, uint>(source.DataIds),
            new Dictionary<uint, uint>(source.InstanceIds));
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
        GameRuntime? runtime;
        lock (_gate)
        {
            use = _useItem;
            apply = _applyItem;
            runtime = _runtime;
        }
        if (runtime is null || use is null || apply is null || !IsAvailable)
            return new(PluginItemCommandStatus.Unavailable);
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        uint playerId = runtime.PlayerIdentity.ServerGuid;
        if (objectId == 0u
            || objects.Get(objectId) is not { } item
            || !IsPlayerOwned(item, playerId, objects))
        {
            return new(PluginItemCommandStatus.InvalidItem);
        }
        if (targetObjectId != 0u && objects.Get(targetObjectId) is null)
            return new(PluginItemCommandStatus.InvalidTarget);
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
        ClientObjectTable objects)
    {
        if (item.WielderId == playerId || item.ContainerId == playerId)
            return true;
        uint parentId = item.ContainerId;
        for (int depth = 0; parentId != 0u && depth < 4; depth++)
        {
            ClientObject? parent = objects.Get(parentId);
            if (parent is null)
                return false;
            if (parent.WielderId == playerId || parent.ContainerId == playerId)
                return true;
            parentId = parent.ContainerId;
        }
        return false;
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
                        transactions.CurrentAppraisalId);
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
        properties = CaptureProperties(item.Properties);
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

    private static PluginItemProperties CaptureProperties(PropertyBundle source) =>
        new(
            new Dictionary<uint, int>(source.Ints),
            new Dictionary<uint, long>(source.Int64s),
            new Dictionary<uint, bool>(source.Bools),
            new Dictionary<uint, double>(source.Floats),
            new Dictionary<uint, string>(source.Strings),
            new Dictionary<uint, uint>(source.DataIds),
            new Dictionary<uint, uint>(source.InstanceIds));

    internal PluginInventoryItem ProjectInventoryItem(
        GameRuntime runtime,
        ClientObject item) =>
        new(
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
            item.Properties.GetInt((uint)PropertyInt.WeaponSkill),
            item.Properties.GetInt((uint)PropertyInt.DamageType),
            item.Properties.GetInt((uint)PropertyInt.Damage),
            item.Properties.GetFloat((uint)PropertyFloat.DamageVariance),
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
        bool shareExperience) => InvokeFellowship(commands =>
            commands.FellowshipCommands.Create(
                commands.Generation,
                name,
                shareExperience));

    public PluginFellowshipCommandResult Recruit(uint targetObjectId) =>
        InvokeFellowship(commands => commands.FellowshipCommands.Recruit(
            commands.Generation,
            targetObjectId));

    public PluginFellowshipCommandResult Dismiss(uint targetObjectId) =>
        InvokeFellowship(commands => commands.FellowshipCommands.Dismiss(
            commands.Generation,
            targetObjectId));

    public PluginFellowshipCommandResult Quit(bool disband) =>
        InvokeFellowship(commands => commands.FellowshipCommands.Quit(
            commands.Generation,
            disband));

    public PluginFellowshipCommandResult AssignLeader(uint targetObjectId) =>
        InvokeFellowship(commands => commands.FellowshipCommands.AssignLeader(
            commands.Generation,
            targetObjectId));

    public PluginFellowshipCommandResult SetOpen(bool isOpen) =>
        InvokeFellowship(commands => commands.FellowshipCommands.SetOpen(
            commands.Generation,
            isOpen));

    private PluginFellowshipCommandResult InvokeFellowship(
        Func<CurrentGameRuntimeAdapter, RuntimeCommandResult> invoke)
    {
        CurrentGameRuntimeAdapter? commands;
        lock (_gate)
            commands = _sessionCommands;
        if (commands is null || !IsAvailable)
            return new(PluginFellowshipCommandStatus.Unavailable);
        RuntimeCommandResult result = invoke(commands);
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
