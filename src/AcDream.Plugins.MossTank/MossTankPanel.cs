using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.MossTank.Expressions;

namespace AcDream.Plugins.MossTank;

internal sealed partial class MossTankPanel : IBuffRuleHost
{
    private enum TankTab
    {
        Options,
        Profiles,
        Vitals,
        Monsters,
        Items,
        Consumables,
        Buffs,
        Route,
        Meta,
    }

    private const double CoverageRefreshIntervalSeconds = 1.0;

    private readonly IPluginHost _host;
    private readonly BuffSettings _buffSettings = new();
    private readonly VitalSettings _vitalSettings = new();
    private readonly CombatSettings _combatSettings = new();
    private readonly InventorySettings _inventorySettings = new();
    private readonly NavigationSettings _navigationSettings = new();
    private readonly VtankSettingsProfileSerializer.AllSettings _allSettings;
    private readonly VtankGameInfoDatabase _gameInfo;
    private readonly MossTankProfileStore _profiles;
    private readonly MossTankLootProfileStore _lootProfiles;
    private readonly MossTankRouteProfileStore _routeProfiles;
    private readonly MossTankMetaProfileStore _metaProfiles;
    private readonly MetaViewManager _metaViews;
    private readonly CombatController _combat;
    /// <summary>VTank's one shared wield/mode subroutine, ga.a (ga.cs:1433-1573).</summary>
    private readonly CombatModeGate _combatModeGate;
    private readonly IdlePeaceRule _idlePeace;
    private readonly SummonPetRule _summonPet;
    private readonly MacroScheduler _scheduler;

    private readonly VitalRechargeController _vitalRecharge;

    private readonly VitalRechargeController _vitalHelperRecharge;
    private readonly DispelController _dispel;
    private readonly InventoryMaintenanceController _inventoryMaintenance;
    private readonly CraftingController _crafting;
    private readonly ItemManaRechargeController _itemManaRecharge;
    private readonly LootController _loot;
    private readonly ProfileGiveController _profileGive;
    private readonly NavigationController _navigation;
    private readonly FellowshipManager _fellowshipManager;
    private readonly MossTankExpressionRuntime _expressions;
    private MetaProfile _metaProfile;
    private readonly MetaEngine _meta;


    private bool _fastCastMovementActive;
    private long _fastCastStartCompletionRevision;
    private double _fastCastMovementElapsed;
    private double _randomHelperRemaining;
    private const double TransactionSuspensionWatchdogSeconds =
        SpellCastTracker.WorstCaseBusySeconds;

    private readonly SpellCastTracker _castTracker = new();

    private readonly BuffSelfRule _buffRule;

    private ulong _castTrackerChatSequence;

    private bool _transactionSuspensionHeld;
    private double _transactionSuspensionElapsed;
    private long _pokeMagicRevision;
    private long _pokeItemRevision;
    private PluginCombatMode _pokeCombatMode;
    private bool _pokeEquipmentBusy;
    private string _status = "Idle.";
    private string _vitals = string.Empty;
    private string _coverage = string.Empty;
    private bool _vitalsInitialized;
    private uint _currentHealth;
    private uint _maxHealth;
    private uint _currentStamina;
    private uint _maxStamina;
    private uint _currentMana;
    private uint _maxMana;
    private IReadOnlyList<PluginSpellInfo>? _coverageSpellSnapshot;
    private int _coverageBuffLineCount;
    private double _coverageRefreshRemaining;
    private TankTab _activeTab = TankTab.Options;
    private readonly HashSet<string> _noBuffItemNames =
        new(StringComparer.Ordinal);
    private string _profileNotice =
        "Select an inventory item, then add it to this profile.";
    private string _profileNameDraft = string.Empty;
    private string _profileLifecycleNotice = "Macro settings are stored by character.";
    private string _monsterExpressionDraft = string.Empty;
    private string _monsterEditorNotice = "Add a monster name or expression, or select one in the world.";
    private IReadOnlyList<bool> _monsterFesterColumn = Array.Empty<bool>();
    private IReadOnlyList<bool> _monsterBroadsideColumn = Array.Empty<bool>();
    private IReadOnlyList<bool> _monsterGravityWellColumn = Array.Empty<bool>();
    private IReadOnlyList<bool> _monsterImperilColumn = Array.Empty<bool>();
    private IReadOnlyList<bool> _monsterYieldColumn = Array.Empty<bool>();
    private IReadOnlyList<bool> _monsterVulnerabilityColumn = Array.Empty<bool>();
    private IReadOnlyList<bool> _monsterAttackColumn = Array.Empty<bool>();
    private IReadOnlyList<bool> _monsterRingColumn = Array.Empty<bool>();
    private IReadOnlyList<bool> _monsterStreakColumn = Array.Empty<bool>();
    private IReadOnlyList<bool> _monsterWeakeningColumn = Array.Empty<bool>();
    private IReadOnlyList<bool> _monsterFesteringColumn = Array.Empty<bool>();
    private IReadOnlyList<bool> _monsterCorruptionColumn = Array.Empty<bool>();
    private IReadOnlyList<bool> _monsterDestructiveColumn = Array.Empty<bool>();
    private IReadOnlyList<bool> _monsterCorrosionColumn = Array.Empty<bool>();
    private IReadOnlyList<string> _monsterNameColumn = Array.Empty<string>();
    private IReadOnlyList<string> _monsterPriorityColumn = Array.Empty<string>();
    private IReadOnlyList<string> _monsterDamageColumn = Array.Empty<string>();
    private IReadOnlyList<string> _monsterExtraVulnColumn = Array.Empty<string>();
    private IReadOnlyList<string> _monsterWeaponColumn = Array.Empty<string>();
    private IReadOnlyList<string> _monsterOffhandColumn = Array.Empty<string>();
    private IReadOnlyList<string> _monsterPetDamageColumn = Array.Empty<string>();
    private IReadOnlyList<uint> _monsterMoveUpIconsColumn = Array.Empty<uint>();
    private IReadOnlyList<uint> _monsterMoveDownIconsColumn = Array.Empty<uint>();
    private int _monsterListSelectedRow;
    private static readonly MonsterDamageType[] MonsterDamageCycle =
    [
        MonsterDamageType.Pierce, MonsterDamageType.Bludgeon, MonsterDamageType.Slash,
        MonsterDamageType.Acid, MonsterDamageType.Electric, MonsterDamageType.Cold,
        MonsterDamageType.Fire, MonsterDamageType.Harm, MonsterDamageType.Auto,
        MonsterDamageType.VoidBasic, MonsterDamageType.DrainAuto, MonsterDamageType.Prismatic,
        MonsterDamageType.Random, MonsterDamageType.Fists,
    ];
    private static readonly MonsterDamageType[] MonsterExtraVulnerabilityCycle =
    [
        MonsterDamageType.Pierce, MonsterDamageType.Bludgeon, MonsterDamageType.Slash,
        MonsterDamageType.Acid, MonsterDamageType.Electric, MonsterDamageType.Cold,
        MonsterDamageType.Fire, MonsterDamageType.Auto, MonsterDamageType.None,
    ];
    private static readonly MonsterDamageType[] MonsterPetDamageCycle =
    [
        MonsterDamageType.Pierce, MonsterDamageType.Bludgeon, MonsterDamageType.Slash,
        MonsterDamageType.Acid, MonsterDamageType.Electric, MonsterDamageType.Cold,
        MonsterDamageType.Fire, MonsterDamageType.PlayerAuto, MonsterDamageType.Auto,
        MonsterDamageType.None,
    ];
    private IReadOnlyList<string> _itemRows = Array.Empty<string>();
    private IReadOnlyList<string> _itemBaseNames = Array.Empty<string>();
    private IReadOnlyList<string> _consumableRows = Array.Empty<string>();
    private int _selectedItemRow;
    private int _selectedConsumableRow;
    private readonly Dictionary<string, int> _itemHandedness =
        new(StringComparer.Ordinal);
    private IReadOnlyList<string> _itemHandsColumn = Array.Empty<string>();
    private static readonly string[] HandednessCycle =
        ["Auto", "1-Handed", "2-Handed"];
    private IReadOnlyList<string> _excludedComponentRows = Array.Empty<string>();
    private IReadOnlyList<uint> _excludedComponentIcons = Array.Empty<uint>();
    private int _selectedExcludedComponentRow;
    private bool _buffPickerVisible;
    private bool _buffPickerForBlacklist;
    private string _buffPickerSearchText = string.Empty;
    private int _selectedBuffPickerRow;
    private int _selectedExtraBuffRow;
    private int _selectedBlacklistedBuffRow;
    private bool _lootEditorVisible;
    private bool _advancedOptionsVisible;
    private int _selectedAdvancedOption;
    private readonly bool[] _advancedOptionCategoryEnabled =
        Enumerable.Repeat(true, VtankOptionCatalog.CategoryBits.Length).ToArray();
    private readonly ReadOnlyCollection<bool> _advancedOptionCategoryEnabledView;
    private IReadOnlyList<string> _advancedOptionNames = Array.Empty<string>();
    private IReadOnlyList<string> _advancedOptionValueColumn = Array.Empty<string>();
    private string _advancedOptionSelectedName = string.Empty;
    private string _advancedOptionSelectedDescription = string.Empty;
    private string _advancedOptionValueDraft = string.Empty;
    private string _advancedOptionNotice =
        "All VTank settings are available here.";
    private IReadOnlyList<string> _lootRuleRows = Array.Empty<string>();
    private int _selectedLootRule;
    private string _lootExpressionDraft = "*";
    private string _lootEditorNotice = "Add a rule or select one to edit.";
    private string _lootProfileNameDraft = string.Empty;
    private IReadOnlyList<string> _routeRows = Array.Empty<string>();
    private IReadOnlyList<string> _routeWaypointCounts = Array.Empty<string>();
    private int _selectedRouteWaypoint;
    private string _routeProfileNameDraft = string.Empty;
    private string _routeNotice = "Add the current position or a selected object.";
    private string _routeChatDraft = "/ls";
    private int _routePauseSeconds = 5;
    private RouteRecallKind _routeRecallKind = RouteRecallKind.PrimaryPortalRecall;
    private RouteInsertMode _routeInsertMode = RouteInsertMode.AddToEnd;
    private IReadOnlyList<string> _metaRows = Array.Empty<string>();
    private IReadOnlyList<string> _metaDeleteColumn = Array.Empty<string>();
    private IReadOnlyList<uint> _metaMoveUpIconsColumn = Array.Empty<uint>();
    private IReadOnlyList<uint> _metaMoveDownIconsColumn = Array.Empty<uint>();
    private IReadOnlyList<string> _metaStateColumn = Array.Empty<string>();
    private IReadOnlyList<string> _metaConditionColumn = Array.Empty<string>();
    private IReadOnlyList<string> _metaActionColumn = Array.Empty<string>();
    private int _selectedMetaRule;
    private string _metaProfileNameDraft = string.Empty;
    private string _metaStateDraft = MetaEngine.DefaultState;
    private string _metaConditionTextDraft = string.Empty;
    private string _metaActionTextDraft = string.Empty;
    private string _metaSecondaryTextDraft = string.Empty;
    private MetaConditionKind _metaConditionKind = MetaConditionKind.Always;
    private MetaActionKind _metaActionKind = MetaActionKind.None;
    private int _metaNumber;
    private int _metaSecondaryNumber;
    private string _metaNotice = "Add a rule or select one to edit.";
    private bool _metaEditorVisible;
    private bool _applyingProfileOptions;
    private bool _initialized;
    private bool _firstRunGuidancePending;
    private bool _automationWasAvailable;

    private uint? _selectionBeforePass;

    public MossTankPanel(IPluginHost host)
    {
        _host = host;
        _advancedOptionCategoryEnabledView =
            new ReadOnlyCollection<bool>(_advancedOptionCategoryEnabled);
        _firstRunGuidancePending = NeedsFirstRunGuidance(host);
        _allSettings = new VtankSettingsProfileSerializer.AllSettings
        {
            Combat = _combatSettings,
            Buffs = _buffSettings,
            Vitals = _vitalSettings,
            Inventory = _inventorySettings,
            Navigation = _navigationSettings,
        };
        // e0.cs:53-79 — VTank's official GameInfoDB, read from the profile
        // directory beside the .usd files. Absent means EMPTY, not a guess:
        // acdream does not ship Virindi's embedded defaultinfodb.ugd.
        _gameInfo = VtankGameInfoDatabase.Load(host.VtankProfiles);
        _profiles = new MossTankProfileStore(host);
        _profiles.BindCharacter(host.Automation.Character.Name);
        _profiles.LoadCurrent(_allSettings, _noBuffItemNames, _commandLogTypes);
        _lootProfiles = new MossTankLootProfileStore(host);
        _lootProfiles.BindCharacter(host.Automation.Character.Name);
        if (!_lootProfiles.LoadCurrent(
            _inventorySettings.Loot.Rules,
            _inventorySettings.Loot))
        {
            _lootProfiles.SaveCurrent(
                _inventorySettings.Loot.Rules,
                _inventorySettings.Loot);
        }
        _routeProfiles = new MossTankRouteProfileStore(host);
        _routeProfiles.BindCharacter(host.Automation.Character.Name);
        if (!_routeProfiles.LoadCurrent(_navigationSettings, host.Automation.Spells))
            _routeProfiles.SaveCurrent(_navigationSettings);
        _castTracker.Completed += OnBuffCastOutcome;
        _combat = new CombatController(
            host,
            _combatSettings,
            _vitalSettings,
            _gameInfo,
            _castTracker);
        _combatModeGate = new CombatModeGate(
            host,
            _combatSettings,
            _vitalSettings,
            StopMacroFromGate);
        _combat.BindCombatModeGate(_combatModeGate);
        _buffRule = new BuffSelfRule(host, _buffSettings, this);
        _idlePeace = new IdlePeaceRule(host, _combatSettings);
        _summonPet = new SummonPetRule(
            host,
            _combatSettings,
            () => _combat.HasTarget);
        _vitalRecharge = new VitalRechargeController(
            host,
            _vitalSettings,
            _combatSettings);
        _vitalHelperRecharge = new VitalRechargeController(
            host,
            _vitalSettings,
            _combatSettings);
        _dispel = new DispelController(host, _vitalSettings, _combatSettings);
        _dispel.BindCombatModeGate(_combatModeGate);
        _inventoryMaintenance = new InventoryMaintenanceController(
            host,
            _inventorySettings);
        _crafting = new CraftingController(
            host,
            _inventorySettings,
            _combatSettings);
        _crafting.BindPeaceGate(ReadyToActInPeace);
        _combat.BindAmmunitionCraftRequest(
            _crafting.CanRequest,
            _crafting.Request);
        _itemManaRecharge = new ItemManaRechargeController(
            host,
            _inventorySettings,
            _combatSettings);
        _loot = new LootController(
            host,
            _inventorySettings.Loot);
        _profileGive = new ProfileGiveController(host, _lootProfiles);
        _navigation = new NavigationController(host, _navigationSettings);
        _navigation.BindCombatModeGate(_combatModeGate, _combatSettings);
        _fellowshipManager = new FellowshipManager(host);
        _metaProfiles = new MossTankMetaProfileStore(host);
        _metaViews = new MetaViewManager(host);
        _metaProfiles.BindCharacter(host.Automation.Character.Name);
        _metaProfile = _metaProfiles.LoadCurrent();
        _expressions = new MossTankExpressionRuntime(host);
        _meta = new MetaEngine(
            host,
            _expressions,
            _metaProfile,
            new MetaServices
            {
                IsNavigationRouteEmpty = () => _navigationSettings.Waypoints.Count == 0,
                NeedsBuff = () => _buffRule.HasAnythingDue(),
                DistanceFromAnyRoutePoint = DistanceFromAnyRoutePoint,
                CountMonstersByPriority = CountMonstersByPriority,
                LoadEmbeddedNavigationRoute = LoadEmbeddedNavigationRoute,
                GetOption = GetMetaOption,
                SetOption = SetMetaOption,
                CreateView = _metaViews.Create,
                DestroyView = _metaViews.Destroy,
                DestroyAllViews = _metaViews.DestroyAll,
            });
        RegisterVtankExpressionFunctions();
        _scheduler = MacroRuleTable.Build(this);
        _scheduler.MetaPass = elapsed =>
        {
            if (_combat.Enabled)
                _meta.OnTick(elapsed);
        };
        _scheduler.Log = EmitMacroLog;
        _scheduler.LockStateSuffix = () =>
            $"   I={host.Automation.Items.IsBusy}, N={_navigation.HasActiveAction}, S={host.Automation.Loot.IsBusy}";
        _combatModeGate.Log = EmitMacroLog;
        _combat.Log = EmitMacroLog;
        _initialized = true;
        ApplyPersistedOptionOverrides();
        EnsureDefaultMonsterRule();
        RefreshMonsterEditor();
        RefreshItemEditors();
        RefreshLootEditor();
        RefreshRouteEditor();
        RefreshMetaEditor();
        RefreshAdvancedOptions();
        _automationWasAvailable = host.Automation.IsAvailable;
    }

    public Action ForceBuff => _buffRule.StartForce;
    public Action CancelForceBuff => _buffRule.CancelForce;

    public Action ToggleCombat => ToggleMacro;
    public Action ToggleCombatEnabled => () =>
    {
        _combatSettings.Enabled = !_combatSettings.Enabled;
        SaveProfile();
    };
    public Action ToggleAutoFellowManagement => () => SetMetaOption(
        "AutoFellowManagement",
        ExpressionValue.Boolean(!AutoFellowManagementEnabled));
    public Action ShowOptions => () => SelectTab(TankTab.Options);
    public Action ShowProfiles => () => SelectTab(TankTab.Profiles);
    public Action ShowVitals => () => SelectTab(TankTab.Vitals);
    public Action ShowMonsters => () => SelectTab(TankTab.Monsters);
    public Action ShowItems => () => SelectTab(TankTab.Items);
    public Action ShowConsumables => () => SelectTab(TankTab.Consumables);
    public Action ShowBuffs => () => SelectTab(TankTab.Buffs);
    public Action ShowRoute => () => SelectTab(TankTab.Route);
    public Action ShowMeta => () => SelectTab(TankTab.Meta);

    public bool WindowAvailable => _host.Automation.IsAvailable;

    internal ExpressionValue EvaluateExpression(string source) =>
        _expressions.Evaluate(source);

    internal IReadOnlyCollection<string> ExpressionFunctionNames =>
        _expressions.Functions.Select(static function => function.Name).ToArray();

    public bool OptionsSelected => _activeTab == TankTab.Options;
    public bool ProfilesSelected => _activeTab == TankTab.Profiles;
    public bool VitalsSelected => _activeTab == TankTab.Vitals;
    public bool MonstersSelected => _activeTab == TankTab.Monsters;
    public bool ItemsSelected => _activeTab == TankTab.Items;
    public bool ConsumablesSelected => _activeTab == TankTab.Consumables;
    public bool BuffsSelected => _activeTab == TankTab.Buffs;
    public bool RouteSelected => _activeTab == TankTab.Route;
    public bool MetaSelected => _activeTab == TankTab.Meta;

    public bool OptionsTabEnabled => true;
    public bool ProfilesTabEnabled => true;
    public bool VitalsTabEnabled => true;
    public bool MonstersTabEnabled => true;
    public bool ItemsTabEnabled => true;
    public bool ConsumablesTabEnabled => true;
    public bool BuffsTabEnabled => true;
    public bool RouteTabEnabled => true;
    public bool MetaTabEnabled => true;

    public bool OptionsVisible => OptionsSelected;
    public bool ProfilesVisible => ProfilesSelected;
    public bool VitalsVisible => VitalsSelected;
    public bool MonstersVisible => MonstersSelected;
    public bool ItemsVisible => ItemsSelected;
    public bool ConsumablesVisible => ConsumablesSelected;
    public bool BuffsVisible => BuffsSelected;
    public bool RouteVisible => RouteSelected;
    public bool MetaVisible => MetaSelected;
    public bool LootEditorVisible => _lootEditorVisible;
    public bool AdvancedOptionsVisible => _advancedOptionsVisible;
    public IReadOnlyList<string> AdvancedOptionNames => _advancedOptionNames;
    public IReadOnlyList<string> AdvancedOptionValueColumn => _advancedOptionValueColumn;
    public IReadOnlyList<string> AdvancedOptionCategoryNames { get; } =
        VtankOptionCatalog.CategoryBits
            .Select(bit => VtankOptionCatalog.CategoryNamesByBit.TryGetValue(bit, out string? name)
                ? name
                : $"0x{bit:X}")
            .ToArray();
    public IReadOnlyList<bool> AdvancedOptionCategoryEnabled => _advancedOptionCategoryEnabledView;
    public int SelectedAdvancedOptionCategoryIndex => -1;
    public Action<int> ToggleAdvancedOptionCategoryAt => index =>
    {
        if ((uint)index >= (uint)_advancedOptionCategoryEnabled.Length)
            return;
        _advancedOptionCategoryEnabled[index] = !_advancedOptionCategoryEnabled[index];
        RefreshAdvancedOptions();
        LoadAdvancedOptionDraft();
    };
    public int SelectedAdvancedOptionIndex => _selectedAdvancedOption;
    public string AdvancedOptionName => _advancedOptionSelectedName;
    public string AdvancedOptionValueDraft => _advancedOptionValueDraft;
    public string AdvancedOptionDescription => _advancedOptionSelectedDescription;
    public string AdvancedOptionNotice => _advancedOptionNotice;

    private IReadOnlyList<string> FilteredAdvancedOptionNames()
    {
        int enabledMask = 0;
        for (int i = 0; i < VtankOptionCatalog.CategoryBits.Length; i++)
            if (_advancedOptionCategoryEnabled[i])
                enabledMask |= VtankOptionCatalog.CategoryBits[i];

        return VtankOptionCatalog.Names.Where(name =>
            VtankOptionCatalog.DeclaredType(name) != VtankSettingValueType.String
            && (!VtankDefaultSettingsDatabase.SettingCategoryBitmasks.TryGetValue(name, out int mask)
                || mask == 0
                || (mask & enabledMask) != 0)).ToArray();
    }

    private void RefreshAdvancedOptions()
    {
        _advancedOptionNames = FilteredAdvancedOptionNames();
        _selectedAdvancedOption = ClampRow(_selectedAdvancedOption, _advancedOptionNames.Count);
        _advancedOptionSelectedName = _advancedOptionNames.Count == 0
            ? string.Empty
            : _advancedOptionNames[
                Math.Clamp(_selectedAdvancedOption, 0, _advancedOptionNames.Count - 1)];
        _advancedOptionValueColumn = _advancedOptionNames
            .Select(DisplayAdvancedOptionValue)
            .ToArray();
        _advancedOptionSelectedDescription =
            VtankDefaultSettingsDatabase.SettingDescriptions.TryGetValue(
                _advancedOptionSelectedName, out string? text)
                ? $"{_advancedOptionSelectedName}: {text}"
                : _advancedOptionSelectedName;
    }

    private string DisplayAdvancedOptionValue(string name)
    {
        ExpressionValue value = GetMetaOption(name);
        if (VtankOptionCatalog.DeclaredType(name) == VtankSettingValueType.Enum
            && VtankDefaultSettingsDatabase.SettingEnumValues.TryGetValue(
                name, out IReadOnlyList<VtankEnumValue>? entries))
        {
            int current = value.AsInt32();
            foreach (VtankEnumValue entry in entries)
                if (entry.Value == current)
                    return entry.Label;
        }
        return value.ToDisplayString();
    }

    public Action ShowAdvancedOptions => () =>
    {
        _advancedOptionsVisible = true;
        RefreshAdvancedOptions();
        LoadAdvancedOptionDraft();
    };
    public Action HideAdvancedOptions => () => _advancedOptionsVisible = false;
    public Action ToggleAdvancedOptionsVisible => () =>
    {
        if (_advancedOptionsVisible)
            HideAdvancedOptions();
        else
            ShowAdvancedOptions();
    };
    public Action<int> SelectAdvancedOption => index =>
    {
        _selectedAdvancedOption = ClampRow(index, _advancedOptionNames.Count);
        RefreshAdvancedOptions();
        LoadAdvancedOptionDraft();
    };
    public Action<int> ClickAdvancedOptionValue => index =>
    {
        if ((uint)index >= (uint)_advancedOptionNames.Count)
            return;
        string name = _advancedOptionNames[index];
        switch (VtankOptionCatalog.DeclaredType(name))
        {
            case VtankSettingValueType.Bool:
                FlipAdvancedOptionBool(index, name);
                break;
            case VtankSettingValueType.Enum:
                CycleAdvancedOptionEnum(index, name);
                break;
            default:
                SelectAdvancedOption(index);
                break;
        }
    };
    public Action<string> SetAdvancedOptionValueDraft => value =>
        _advancedOptionValueDraft = value;
    public Action<string> SubmitAdvancedOption => value =>
    {
        _advancedOptionValueDraft = value;
        ApplyAdvancedOptionCore();
    };

    public string BuffStatus => _status;
    public string CombatButtonText => _combat.ButtonText;
    public bool CombatMacroRunning => _combat.Enabled;
    public string CombatStatus => _combat.Status;
    public string CombatTarget => _combat.TargetText;
    public string CombatMode => _combat.ModeText;
    /// <summary>
    /// One status line over the two recharge owners (rows 4 and 11). The self
    /// half speaks first because it outranks the helper half in the rule list;
    /// the helper's line shows only while the self half is idle, which is what
    /// the single shared instance used to display.
    /// </summary>
    public string VitalStatus =>
        _vitalRecharge.Status == VitalRechargeController.IdleStatus
            ? _vitalHelperRecharge.Status
            : _vitalRecharge.Status;
    public string DispelStatus => _dispel.Status;
    public string InventoryMaintenanceStatus => _inventoryMaintenance.Status;
    public string CraftingStatus => _crafting.Status;
    public string ItemManaRechargeStatus => _itemManaRecharge.Status;
    public string LootStatus => _loot.Status;
    public bool CombatEnabled => _combatSettings.Enabled;
    public bool BuffingEnabled => _buffSettings.Enabled;
    public bool IdlePeaceModeEnabled => _combatSettings.IdlePeaceMode;
    public bool IdleBuffTopoffEnabled => _buffSettings.IdleBuffTopoff;
    public bool ManaChargesWhenOffEnabled =>
        _inventorySettings.ManaChargesWhenOff;
    public bool AutoFellowManagementEnabled =>
        _combatSettings.AutoFellowManagement;
    public string FellowshipManagerStatus => _fellowshipManager.Status;
    public bool FastCastBuffsEnabled => _buffSettings.FastCastBuffs;
    public bool DontShootAtWallsEnabled => _combatSettings.UseProjectileAwareness;
    public bool DebuffFallbackEnabled => _combatSettings.AllowDebuffFallback;
    public bool AutoStackEnabled => _inventorySettings.AutoStack;
    public bool AutoCramEnabled => _inventorySettings.AutoCram;
    public bool AutoCraftItemsEnabled => _inventorySettings.AutoCraftItems;
    public bool CastDispelSelfEnabled => _vitalSettings.CastDispelSelf;
    public bool UseDispelItemsEnabled => _vitalSettings.UseDispelItems;
    public bool RefillWornManaEnabled => _inventorySettings.RefillWornMana;
    public float RefillWornManaValue => _inventorySettings.RefillWornManaPercent / 100f;
    public string RefillWornManaText =>
        $"Refill worn mana below {_inventorySettings.RefillWornManaPercent}%";
    public string ItemProfileText => ProfileText(
        "Weapons / Wands / Shields / Pets",
        _combatSettings.CombatItemNames);
    public string ConsumableProfileText => ProfileText(
        "Gems / Food / Kits / Potions / Charges / Grenades / Lockpicks",
        _combatSettings.ConsumableNames);
    public string ProfileNotice => _profileNotice;
    public Action AddSelectedItem => () => AddSelectedProfileItem(noBuffs: false);
    public Action AddSelectedItemNoBuffs => () => AddSelectedProfileItem(noBuffs: true);
    public Action AddSelectedConsumable => AddSelectedConsumableCore;
    public Action AddAllPeas => AddAllPeasCore;
    public IReadOnlyList<string> ItemRows => _itemRows;
    public IReadOnlyList<string> ConsumableRows => _consumableRows;
    public int SelectedItemRowIndex => _selectedItemRow;
    public int SelectedConsumableRowIndex => _selectedConsumableRow;
    public Action<int> SelectItemRow => index =>
        _selectedItemRow = ClampRow(index, _itemRows.Count);
    public Action<int> SelectConsumableRow => index =>
    {
        _selectedConsumableRow = ClampRow(index, _consumableRows.Count);
        RemoveSelectedConsumableCore();
    };
    public Action RemoveSelectedItem => RemoveSelectedItemCore;
    public Action RemoveSelectedConsumable => RemoveSelectedConsumableCore;

    public IReadOnlyList<string> ItemNameColumn => _itemRows;
    public IReadOnlyList<string> ItemHandsColumn => _itemHandsColumn;
    public Action<int> DeleteItemRowAt => DeleteItemRowAtCore;
    public Action<int> CycleItemHandsAt => CycleItemHandsAtCore;

    public IReadOnlyList<string> ExcludedComponentRows => _excludedComponentRows;
    public IReadOnlyList<uint> ExcludedComponentIcons => _excludedComponentIcons;
    public int SelectedExcludedComponentIndex => _selectedExcludedComponentRow;
    public Action<int> SelectExcludedComponentRow => index =>
        _selectedExcludedComponentRow = ClampRow(index, _excludedComponentRows.Count);
    public Action<int> DeleteExcludedComponentAt => DeleteExcludedComponentAtCore;
    public Action AddSelectedComponent => AddSelectedComponentCore;
    public Action ToggleAutoStack => () =>
    {
        _inventorySettings.AutoStack = !_inventorySettings.AutoStack;
        _inventoryMaintenance.Reset();
        SaveProfile();
    };
    public Action ToggleAutoCram => () =>
    {
        _inventorySettings.AutoCram = !_inventorySettings.AutoCram;
        _inventoryMaintenance.Reset();
        SaveProfile();
    };
    public Action ToggleAutoCraftItems => () =>
    {
        _inventorySettings.AutoCraftItems = !_inventorySettings.AutoCraftItems;
        _crafting.Reset();
        SaveProfile();
    };
    public Action ToggleFastCastBuffs => () => SetMetaOption(
        "FastCastBuffs",
        ExpressionValue.Boolean(!_buffSettings.FastCastBuffs));
    public Action ToggleDontShootAtWalls => () => SetMetaOption(
        "UseProjectileAwareness",
        ExpressionValue.Boolean(!_combatSettings.UseProjectileAwareness));
    public Action ToggleDebuffFallback => () => SetMetaOption(
        "AllowDebuffFallback",
        ExpressionValue.Boolean(!_combatSettings.AllowDebuffFallback));
    public Action ToggleCastDispelSelf => () => SetMetaOption(
        "CastDispelSelf",
        ExpressionValue.Boolean(!_vitalSettings.CastDispelSelf));
    public Action ToggleUseDispelItems => () => SetMetaOption(
        "UseDispelItems",
        ExpressionValue.Boolean(!_vitalSettings.UseDispelItems));
    public Action ToggleRefillWornMana => () =>
    {
        _inventorySettings.RefillWornMana = !_inventorySettings.RefillWornMana;
        _itemManaRecharge.Reset();
        SaveProfile();
    };
    public Action<float> SetRefillWornMana => value =>
    {
        _inventorySettings.RefillWornManaPercent = Math.Clamp(
            (int)MathF.Round(value * 100f),
            0,
            99);
        SaveProfile();
    };

    // ── Loot profile / editor ────────────────────────────────────────────
    public bool LootEnabled => _inventorySettings.Loot.Enabled;
    public bool LootPriorityBoostEnabled =>
        _inventorySettings.Loot.PriorityBoost;
    public bool LootAllCorpsesEnabled =>
        _inventorySettings.Loot.LootAllCorpses;
    public bool LootFellowCorpsesEnabled =>
        _inventorySettings.Loot.LootFellowCorpses;
    public bool LootOnlyRareCorpsesEnabled =>
        _inventorySettings.Loot.LootOnlyRareCorpses;
    public bool ReadUnknownScrollsEnabled =>
        _inventorySettings.Loot.ReadUnknownScrolls;
    public IReadOnlyList<string> LootProfileNames =>
        _lootProfiles.AvailableNames;
    public string LootProfileName => _lootProfiles.Selected;
    public string LootProfileNameDraft => _lootProfileNameDraft;
    public IReadOnlyList<string> LootClassifierNames
    {
        get
        {
            var names = new List<string> { "VTClassic" };
            names.AddRange(_host.LootClassifiers.Available.Select(FormatClassifier));
            string selected = SelectedLootClassifier;
            if (!names.Contains(selected, StringComparer.Ordinal))
                names.Add(selected);
            return names;
        }
    }
    public string SelectedLootClassifier
    {
        get
        {
            string id = _inventorySettings.Loot.ExternalClassifierId;
            if (string.IsNullOrWhiteSpace(id))
                return "VTClassic";
            foreach (PluginLootClassifierInfo info in
                     _host.LootClassifiers.Available)
            {
                if (string.Equals(info.Id, id, StringComparison.OrdinalIgnoreCase))
                    return FormatClassifier(info);
            }
            return $"Unavailable [{id}]";
        }
    }
    public string LootRangeText =>
        $"Corpse range {_inventorySettings.Loot.CorpseApproachRange:0}m";
    public IReadOnlyList<string> LootRuleRows => _lootRuleRows;
    public int SelectedLootRuleIndex => _selectedLootRule;
    public string LootExpressionDraft => _lootExpressionDraft;
    public string LootEditorNotice => _lootEditorNotice;
    public IReadOnlyList<string> LootActionNames => Enum.GetNames<LootAction>();
    public string SelectedLootAction => SelectedLootRule?.Action.ToString()
        ?? LootAction.Keep.ToString();
    public string LootPriorityText =>
        $"Priority {SelectedLootRule?.Priority ?? 0}";
    public string LootKeepCountText =>
        $"Keep up to {SelectedLootRule?.KeepCount ?? 1}";
    public Action ToggleLooting => () =>
    {
        _inventorySettings.Loot.Enabled = !_inventorySettings.Loot.Enabled;
        _loot.Reset();
        SaveProfile();
    };
    public Action ToggleLootPriorityBoost => () =>
    {
        _inventorySettings.Loot.PriorityBoost =
            !_inventorySettings.Loot.PriorityBoost;
        SaveProfile();
    };
    public Action ToggleLootAllCorpses => () =>
    {
        _inventorySettings.Loot.LootAllCorpses =
            !_inventorySettings.Loot.LootAllCorpses;
        SaveProfile();
    };
    public Action ToggleLootFellowCorpses => () =>
    {
        _inventorySettings.Loot.LootFellowCorpses =
            !_inventorySettings.Loot.LootFellowCorpses;
        SaveProfile();
    };
    public Action ToggleLootOnlyRareCorpses => () =>
    {
        _inventorySettings.Loot.LootOnlyRareCorpses =
            !_inventorySettings.Loot.LootOnlyRareCorpses;
        SaveProfile();
    };
    public Action ToggleReadUnknownScrolls => () =>
    {
        _inventorySettings.Loot.ReadUnknownScrolls =
            !_inventorySettings.Loot.ReadUnknownScrolls;
        SaveProfile();
    };
    public Action ShowLootEditor => () =>
    {
        _activeTab = TankTab.Profiles;
        _lootEditorVisible = true;
        RefreshLootEditor();
    };
    public Action ToggleLootEditorVisible => () =>
    {
        if (_lootEditorVisible)
            CloseLootEditor();
        else
            ShowLootEditor();
    };
    public Action<string> SelectLootProfile => SelectLootProfileCore;
    public Action<string> SelectLootClassifier => value =>
    {
        string? id = ResolveClassifierId(value);
        if (id is null)
        {
            _profileLifecycleNotice = $"Loot engine '{value}' is unavailable.";
            return;
        }
        _inventorySettings.Loot.ExternalClassifierId = id;
        _loot.Reset();
        SaveProfile();
        _profileLifecycleNotice = id.Length == 0
            ? "Loot engine set to VTClassic."
            : $"Loot engine set to {SelectedLootClassifier}.";
    };
    public Action<string> SetLootProfileNameDraft => value =>
        _lootProfileNameDraft = value;
    public Action<string> CreateNamedLootProfile => value =>
    {
        _lootProfileNameDraft = value;
        CreateLootProfileCore(copyCurrent: false);
    };
    public Action CreateLootProfile => () =>
        CreateLootProfileCore(copyCurrent: false);
    public Action CopyLootProfile => () =>
        CreateLootProfileCore(copyCurrent: true);
    public Action ClearLootProfile => ClearLootProfileCore;
    public Action DeleteLootProfile => DeleteLootProfileCore;
    public Action CloseLootEditor => () => _lootEditorVisible = false;
    public Action<int> SelectLootRule => SelectLootRuleCore;
    public Action<string> SetLootExpressionDraft => value =>
        _lootExpressionDraft = value;
    public Action<string> ApplyLootExpression => value =>
    {
        _lootExpressionDraft = value;
        ApplyLootExpressionCore();
    };
    public Action ApplyLootRule => ApplyLootExpressionCore;
    public Action AddLootRule => AddLootRuleCore;
    public Action RemoveLootRule => RemoveLootRuleCore;
    public Action MoveLootRuleUp => () => MoveLootRule(-1);
    public Action MoveLootRuleDown => () => MoveLootRule(1);
    public Action<string> SelectLootAction => SelectLootActionCore;
    public Action LootPriorityDown => () => UpdateSelectedLootRule(rule =>
        rule.Priority = Math.Max(-1000, rule.Priority - 1));
    public Action LootPriorityUp => () => UpdateSelectedLootRule(rule =>
        rule.Priority = Math.Min(1000, rule.Priority + 1));
    public Action LootKeepCountDown => () => UpdateSelectedLootRule(rule =>
        rule.KeepCount = Math.Max(0, rule.KeepCount - 1));
    public Action LootKeepCountUp => () => UpdateSelectedLootRule(rule =>
        rule.KeepCount = Math.Min(100000, rule.KeepCount + 1));
    public Action LootRangeDown => () =>
    {
        _inventorySettings.Loot.CorpseApproachRange = Math.Max(
            2f,
            _inventorySettings.Loot.CorpseApproachRange - 2f);
        SaveProfile();
    };
    public Action LootRangeUp => () =>
    {
        _inventorySettings.Loot.CorpseApproachRange = Math.Min(
            100f,
            _inventorySettings.Loot.CorpseApproachRange + 2f);
        SaveProfile();
    };

    // ── Navigation / route profiles ──────────────────────────────────────
    public bool NavigationEnabled => _navigationSettings.Enabled;
    public bool NavigationPriorityEnabled => _navigationSettings.Priority;
    public bool FollowAroundCornersEnabled =>
        _navigationSettings.FollowAroundCorners;
    public bool OpenDoorsEnabled => _navigationSettings.OpenDoors;
    public string NavigationStatus => _navigation.Status;
    public IReadOnlyList<string> RouteRows => _routeRows;
    public int SelectedRouteWaypointIndex => _selectedRouteWaypoint;
    public IReadOnlyList<string> RouteWaypointTextColumn => _routeRows;
    public IReadOnlyList<string> RouteWaypointCountColumn => _routeWaypointCounts;
    public IReadOnlyList<string> RouteWaypointFillerColumn => Array.Empty<string>();
    public Action<int> RouteWaypointFillerClick => static _ => { };
    public Action<int> DeleteRouteWaypointAt => DeleteRouteWaypointAtCore;
    public Action SelectNearestRouteWaypoint => SelectNearestRouteWaypointCore;
    public IReadOnlyList<string> RouteModeNames =>
        ["Circular", "Linear", "Follow", "Once"];
    public string SelectedRouteMode => _navigationSettings.Mode == RouteMode.Target
        ? "Follow"
        : _navigationSettings.Mode.ToString();
    public IReadOnlyList<string> RouteRecallNames
    {
        get
        {
            RouteRecallKind[] values = Enum.GetValues<RouteRecallKind>();
            var names = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
                names[i] = RouteWaypoint.RecallShortCaption(values[i]);
            return names;
        }
    }
    public string SelectedRouteRecall => RouteWaypoint.RecallShortCaption(_routeRecallKind);
    public IReadOnlyList<string> RouteProfileNames =>
        _routeProfiles.AvailableNames;
    public string SelectedRouteProfile => _routeProfiles.Selected;
    public string RouteProfileNameDraft => _routeProfileNameDraft;
    public string RouteNotice => _routeNotice;
    public string RouteChatDraft => _routeChatDraft;
    public string RoutePauseSecondsFieldText =>
        _routePauseSeconds.ToString(CultureInfo.InvariantCulture);
    public Action<string> SetRoutePauseSecondsText => value =>
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
            _routePauseSeconds = Math.Clamp(seconds, 0, 3600);
    };
    public string RouteMinimumDistanceText => string.Create(
        CultureInfo.InvariantCulture,
        $"Follow/Nav Min Distance: {_navigationSettings.MinimumDistanceMeters:0.0}m");
    public string RouteFollowTargetText =>
        _navigationSettings.FollowTargetObjectId == 0u
            ? "Follow target: [None]"
            : $"Follow target: {_navigationSettings.FollowTargetName}";
    public IReadOnlyList<string> RouteInsertModeNames { get; } =
        ["Add to End", "Insert Above", "Insert Below"];
    public string SelectedRouteInsertMode => _routeInsertMode switch
    {
        RouteInsertMode.InsertAbove => "Insert Above",
        RouteInsertMode.InsertBelow => "Insert Below",
        _ => "Add to End",
    };
    public Action<string> SelectRouteInsertMode => value =>
        _routeInsertMode = value switch
        {
            "Insert Above" => RouteInsertMode.InsertAbove,
            "Insert Below" => RouteInsertMode.InsertBelow,
            _ => RouteInsertMode.AddToEnd,
        };

    public Action ToggleNavigation => () =>
    {
        _navigationSettings.Enabled = !_navigationSettings.Enabled;
        _navigation.Reset();
        SaveRouteProfile();
    };
    public Action ToggleNavigationPriority => () =>
    {
        _navigationSettings.Priority = !_navigationSettings.Priority;
        SaveRouteProfile();
    };
    public Action ToggleFollowAroundCorners => () =>
    {
        _navigationSettings.FollowAroundCorners =
            !_navigationSettings.FollowAroundCorners;
        _navigation.Reset();
        SaveRouteProfile();
    };
    public Action ToggleOpenDoors => () =>
    {
        _navigationSettings.OpenDoors = !_navigationSettings.OpenDoors;
        _navigation.Reset();
        SaveRouteProfile();
    };
    public Action<string> SelectRouteMode => value =>
    {
        string normalized = string.Equals(value, "Follow", StringComparison.OrdinalIgnoreCase)
            ? nameof(RouteMode.Target)
            : value;
        if (!Enum.TryParse(normalized, ignoreCase: true, out RouteMode mode))
            return;
        _navigationSettings.Mode = mode;
        if (mode == RouteMode.Target)
            CaptureFollowTarget();
        _navigation.Reset();
        RefreshRouteEditor();
        SaveRouteProfile();
    };
    public Action<string> SelectRouteRecall => value =>
    {
        foreach (RouteRecallKind kind in Enum.GetValues<RouteRecallKind>())
        {
            if (RouteWaypoint.RecallShortCaption(kind).Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                _routeRecallKind = kind;
                return;
            }
        }
    };
    public Action<int> SelectRouteWaypoint => index =>
        _selectedRouteWaypoint = ClampRow(index, _navigationSettings.Waypoints.Count);
    public Action AddRoutePoint => AddRoutePointCore;
    public Action AddRouteUseSelected => () =>
        AddSelectedObjectWaypoint(RouteWaypointType.UseNpc);
    public Action AddRouteOpenVendor => () =>
        AddSelectedObjectWaypoint(RouteWaypointType.OpenVendor);
    public Action AddRoutePortal => () =>
        AddSelectedObjectWaypoint(RouteWaypointType.PortalByName);
    public Action AddRouteRecall => AddRouteRecallCore;
    public Action AddRoutePause => AddRoutePauseCore;
    public Action AddRouteChat => AddRouteChatCore;
    public Action AddRouteCheckpoint => AddRouteCheckpointCore;
    public Action AddRouteJump => AddRouteJumpCore;
    public Action RemoveRouteWaypoint => RemoveRouteWaypointCore;
    public Action MoveRouteWaypointUp => () => MoveRouteWaypoint(-1);
    public Action MoveRouteWaypointDown => () => MoveRouteWaypoint(1);
    public Action RouteMinimumDistanceDown => () =>
    {
        _navigationSettings.MinimumDistanceMeters = Math.Max(
            0.5d,
            _navigationSettings.MinimumDistanceMeters - 0.5d);
        SaveRouteProfile();
    };
    public Action RouteMinimumDistanceUp => () =>
    {
        _navigationSettings.MinimumDistanceMeters = Math.Min(
            50d,
            _navigationSettings.MinimumDistanceMeters + 0.5d);
        SaveRouteProfile();
    };
    public Action<string> SetRouteChatDraft => value =>
        _routeChatDraft = value;
    public Action<string> SetRouteProfileNameDraft => value =>
        _routeProfileNameDraft = value;
    public Action<string> SelectRouteProfile => SelectRouteProfileCore;
    public Action<string> CreateNamedRouteProfile => value =>
    {
        _routeProfileNameDraft = value;
        CreateRouteProfileCore(copyCurrent: false);
    };
    public Action CreateRouteProfile => () =>
        CreateRouteProfileCore(copyCurrent: false);
    public Action CopyRouteProfile => () =>
        CreateRouteProfileCore(copyCurrent: true);
    public Action ClearRouteProfile => ClearRouteProfileCore;
    public Action DeleteRouteProfile => DeleteRouteProfileCore;
    public Action SetFollowTarget => CaptureFollowTarget;

    // ── Meta profile / editor ────────────────────────────────────────────
    public bool MetaEnabled => _meta.Enabled;
    public string MetaState => _meta.CurrentState;
    public string MetaStateText => $"State: {MetaState}";
    public string MetaStatus => _meta.Status;
    public IReadOnlyList<string> MetaRows => _metaRows;
    public int SelectedMetaRuleIndex => _selectedMetaRule;

    public IReadOnlyList<string> MetaDeleteColumn => _metaDeleteColumn;
    public IReadOnlyList<uint> MetaMoveUpIcons => _metaMoveUpIconsColumn;
    public IReadOnlyList<uint> MetaMoveDownIcons => _metaMoveDownIconsColumn;
    public IReadOnlyList<string> MetaStateColumn => _metaStateColumn;
    public IReadOnlyList<string> MetaConditionColumn => _metaConditionColumn;
    public IReadOnlyList<string> MetaActionColumn => _metaActionColumn;
    public Action<int> DeleteMetaRuleAt => row =>
    {
        SelectMetaRuleCore(row);
        RemoveMetaRuleCore();
    };
    public Action<int> MoveMetaRuleUpAt => row =>
    {
        SelectMetaRuleCore(row);
        MoveMetaRule(-1);
    };
    public Action<int> MoveMetaRuleDownAt => row =>
    {
        SelectMetaRuleCore(row);
        MoveMetaRule(1);
    };
    public IReadOnlyList<string> MetaConditionNames =>
        Enum.GetNames<MetaConditionKind>();
    public IReadOnlyList<string> MetaActionNames => Enum.GetNames<MetaActionKind>();
    public string SelectedMetaCondition => _metaConditionKind.ToString();
    public string SelectedMetaAction => _metaActionKind.ToString();
    public string MetaStateDraft => _metaStateDraft;
    public string MetaConditionTextDraft => _metaConditionTextDraft;
    public string MetaActionTextDraft => _metaActionTextDraft;
    public string MetaSecondaryTextDraft => _metaSecondaryTextDraft;
    public string MetaNumberText => _metaNumber.ToString(CultureInfo.InvariantCulture);
    public string MetaNumberLabel => $"N: {MetaNumberText}";
    public string MetaSecondaryNumberText =>
        _metaSecondaryNumber.ToString(CultureInfo.InvariantCulture);
    public string MetaSecondaryNumberLabel => $"N2: {MetaSecondaryNumberText}";
    public string MetaNotice => _metaNotice;
    public IReadOnlyList<string> MetaProfileNames => _metaProfiles.AvailableNames;
    public string SelectedMetaProfile => _metaProfiles.Selected;
    public string MetaProfileNameDraft => _metaProfileNameDraft;
    public Action ToggleMeta => () =>
    {
        _meta.SetEnabled(!_meta.Enabled);
        _combatSettings.MetaState = _meta.CurrentState;
    };
    public Action<int> SelectMetaRule => row =>
    {
        SelectMetaRuleCore(row);
        _metaEditorVisible = true;
    };
    public bool MetaEditorVisible => _metaEditorVisible;
    public Action HideMetaEditor => () => _metaEditorVisible = false;
    public IReadOnlyList<string> MetaCurrentStateNames => _meta.States.ToArray();
    public string SelectedMetaCurrentState => _meta.CurrentState;
    public Action<string> SetMetaCurrentState => value =>
    {
        _meta.Transition(value);
        _combatSettings.MetaState = _meta.CurrentState;
    };
    public Action<string> SelectMetaCondition => value =>
    {
        if (Enum.TryParse(value, ignoreCase: true, out MetaConditionKind parsed))
            _metaConditionKind = parsed;
    };
    public Action<string> SelectMetaAction => value =>
    {
        if (Enum.TryParse(value, ignoreCase: true, out MetaActionKind parsed))
            _metaActionKind = parsed;
    };
    public Action<string> SetMetaStateDraft => value => _metaStateDraft = value;
    public Action<string> SetMetaConditionTextDraft => value =>
        _metaConditionTextDraft = value;
    public Action<string> SetMetaActionTextDraft => value =>
        _metaActionTextDraft = value;
    public Action<string> SetMetaSecondaryTextDraft => value =>
        _metaSecondaryTextDraft = value;
    public Action MetaNumberDown => () => _metaNumber--;
    public Action MetaNumberUp => () => _metaNumber++;
    public Action MetaSecondaryNumberDown => () => _metaSecondaryNumber--;
    public Action MetaSecondaryNumberUp => () => _metaSecondaryNumber++;
    public Action AddMetaRule => AddMetaRuleCore;
    public Action CreateMetaRule => () =>
    {
        AddMetaRuleCore();
        _metaEditorVisible = true;
    };
    public Action ApplyMetaRule => () =>
    {
        ApplyMetaRuleCore();
        _metaEditorVisible = false;
    };
    public Action RemoveMetaRule => RemoveMetaRuleCore;
    public Action MoveMetaRuleUp => () => MoveMetaRule(-1);
    public Action MoveMetaRuleDown => () => MoveMetaRule(1);
    public Action<string> SetMetaProfileNameDraft => value =>
        _metaProfileNameDraft = value;
    public Action<string> SelectMetaProfile => SelectMetaProfileCore;
    public Action<string> CreateNamedMetaProfile => value =>
    {
        _metaProfileNameDraft = value;
        CreateMetaProfileCore(copyCurrent: false);
    };
    public Action CreateMetaProfile => () => CreateMetaProfileCore(copyCurrent: false);
    public Action CopyMetaProfile => () => CreateMetaProfileCore(copyCurrent: true);
    public Action ClearMetaProfile => ClearMetaProfileCore;
    public Action DeleteMetaProfile => DeleteMetaProfileCore;

    // ── Profiles tab ─────────────────────────────────────────────────────
    public IReadOnlyList<string> MacroProfileNames => _profiles.AvailableNames;
    public string SelectedMacroProfile => _profiles.Selected;
    public string ProfileNameDraft => _profileNameDraft;
    public string ProfileLifecycleNotice => ProfileRecoveryNotice
        ?? _profileLifecycleNotice;
    private string? ProfileRecoveryNotice =>
        _profiles.RecoveryNotice
        ?? _lootProfiles.RecoveryNotice
        ?? _routeProfiles.RecoveryNotice
        ?? _metaProfiles.RecoveryNotice;
    public bool MineOnlyEnabled => _profiles.MineOnly;
    public Action<string> SetProfileNameDraft => value =>
        _profileNameDraft = value;
    public Action<string> SelectMacroProfile => SelectProfile;
    public Action<string> CreateNamedProfile => value =>
    {
        _profileNameDraft = value;
        CreateProfileCore(copyCurrent: false);
    };
    public Action CreateProfile => () => CreateProfileCore(copyCurrent: false);
    public Action CopyProfile => () => CreateProfileCore(copyCurrent: true);
    public Action ClearProfile => ClearProfileCore;
    public Action DeleteProfile => DeleteProfileCore;
    public Action ToggleMineOnly => () =>
    {
        string before = _profiles.Selected;
        _profiles.SetMineOnly(!_profiles.MineOnly);
        if (!string.Equals(before, _profiles.Selected, StringComparison.OrdinalIgnoreCase))
            LoadSelectedProfile();
        _profileLifecycleNotice = _profiles.MineOnly
            ? "Showing profiles owned by this character."
            : "Showing profiles from all characters.";
    };

    public string MonsterExpressionDraft => _monsterExpressionDraft;
    public string MonsterEditorNotice => _monsterEditorNotice;
    public string MonsterEquipmentText
    {
        get
        {
            MonsterRuleActions actions = _combatSettings.Rules
                .FirstOrDefault(static r => r.IsDefault)
                ?.Actions ?? new MonsterRuleActions();
            return $"Weapon {ItemDisplayName(actions.WeaponObjectId, actions.WeaponName)}   "
                + $"Offhand {ItemDisplayName(actions.OffhandObjectId, actions.OffhandName)}";
        }
    }
    public int SelectedMonsterListRow => _monsterListSelectedRow;
    public Action<int> SelectMonsterListRow => row => _monsterListSelectedRow = row;
    public Action<string> SetMonsterExpressionDraft => value =>
        _monsterExpressionDraft = value;
    public Action AddMonsterRule => () =>
    {
        string expression = string.IsNullOrWhiteSpace(_monsterExpressionDraft)
            ? "New monster"
            : _monsterExpressionDraft;
        AddMonsterRuleCore(expression);
        _monsterExpressionDraft = string.Empty;
    };
    public Action AddSelectedMonster => AddSelectedMonsterCore;

    public IReadOnlyList<bool> MonsterFesterColumn => _monsterFesterColumn;
    public IReadOnlyList<bool> MonsterBroadsideColumn => _monsterBroadsideColumn;
    public IReadOnlyList<bool> MonsterGravityWellColumn => _monsterGravityWellColumn;
    public IReadOnlyList<bool> MonsterImperilColumn => _monsterImperilColumn;
    public IReadOnlyList<bool> MonsterYieldColumn => _monsterYieldColumn;
    public IReadOnlyList<bool> MonsterVulnerabilityColumn => _monsterVulnerabilityColumn;
    public IReadOnlyList<bool> MonsterAttackColumn => _monsterAttackColumn;
    public IReadOnlyList<bool> MonsterRingColumn => _monsterRingColumn;
    public IReadOnlyList<bool> MonsterStreakColumn => _monsterStreakColumn;
    public IReadOnlyList<bool> MonsterWeakeningColumn => _monsterWeakeningColumn;
    public IReadOnlyList<bool> MonsterFesteringColumn => _monsterFesteringColumn;
    public IReadOnlyList<bool> MonsterCorruptionColumn => _monsterCorruptionColumn;
    public IReadOnlyList<bool> MonsterDestructiveColumn => _monsterDestructiveColumn;
    public IReadOnlyList<bool> MonsterCorrosionColumn => _monsterCorrosionColumn;

    public Action<int> ToggleMonsterFesterAt => row => ToggleMonsterFlagAt(row, MonsterActionFlags.Fester);
    public Action<int> ToggleMonsterBroadsideAt => row => ToggleMonsterFlagAt(row, MonsterActionFlags.Broadside);
    public Action<int> ToggleMonsterGravityWellAt => row => ToggleMonsterFlagAt(row, MonsterActionFlags.GravityWell);
    public Action<int> ToggleMonsterImperilAt => row => ToggleMonsterFlagAt(row, MonsterActionFlags.Imperil);
    public Action<int> ToggleMonsterYieldAt => row => ToggleMonsterFlagAt(row, MonsterActionFlags.Yield);
    public Action<int> ToggleMonsterVulnerabilityAt => row => ToggleMonsterFlagAt(row, MonsterActionFlags.Vulnerability);
    public Action<int> ToggleMonsterAttackAt => row => ToggleMonsterFlagAt(row, MonsterActionFlags.Attack);
    public Action<int> ToggleMonsterRingAt => row => ToggleMonsterFlagAt(row, MonsterActionFlags.Ring);
    public Action<int> ToggleMonsterStreakAt => row => ToggleMonsterFlagAt(row, MonsterActionFlags.Streak);
    public Action<int> ToggleMonsterWeakeningAt => row => ToggleMonsterFlagAt(row, MonsterActionFlags.WeakeningCurse);
    public Action<int> ToggleMonsterFesteringAt => row => ToggleMonsterFlagAt(row, MonsterActionFlags.FesteringCurse);
    public Action<int> ToggleMonsterCorruptionAt => row => ToggleMonsterFlagAt(row, MonsterActionFlags.Corruption);
    public Action<int> ToggleMonsterDestructiveAt => row => ToggleMonsterFlagAt(row, MonsterActionFlags.DestructiveCurse);
    public Action<int> ToggleMonsterCorrosionAt => row => ToggleMonsterFlagAt(row, MonsterActionFlags.Corrosion);

    public IReadOnlyList<string> MonsterNameColumn => _monsterNameColumn;
    public IReadOnlyList<string> MonsterPriorityColumn => _monsterPriorityColumn;
    public IReadOnlyList<string> MonsterDamageColumn => _monsterDamageColumn;
    public IReadOnlyList<string> MonsterExtraVulnColumn => _monsterExtraVulnColumn;
    public IReadOnlyList<string> MonsterWeaponColumn => _monsterWeaponColumn;
    public IReadOnlyList<string> MonsterOffhandColumn => _monsterOffhandColumn;
    public IReadOnlyList<string> MonsterPetDamageColumn => _monsterPetDamageColumn;

    public Action<int> DeleteMonsterRuleAt => DeleteMonsterRuleAtCore;
    public Action<int> CycleMonsterPriorityAt => CycleMonsterPriorityAtCore;
    public Action<int> CycleMonsterDamageAt => row => UpdateMonsterActionsAt(
        row, actions => actions with { DamageType = CycleDamage(actions.DamageType, MonsterDamageCycle) });
    public Action<int> CycleMonsterExtraVulnerabilityAt => row => UpdateMonsterActionsAt(
        row, actions => actions with
        {
            ExtraVulnerability = CycleDamage(actions.ExtraVulnerability, MonsterExtraVulnerabilityCycle),
        });
    public Action<int> CycleMonsterWeaponAt => row => CycleMonsterEquipmentAt(row, offhand: false);
    public Action<int> CycleMonsterOffhandAt => row => CycleMonsterEquipmentAt(row, offhand: true);
    public Action<int> CycleMonsterPetDamageAt => row => UpdateMonsterActionsAt(
        row, actions => actions with { PetDamageType = CycleDamage(actions.PetDamageType, MonsterPetDamageCycle) });

    public IReadOnlyList<uint> MonsterMoveUpIcons => _monsterMoveUpIconsColumn;
    public IReadOnlyList<uint> MonsterMoveDownIcons => _monsterMoveDownIconsColumn;
    public Action<int> MoveMonsterRuleUpAt => row => MoveMonsterRuleAtCore(row, -1);
    public Action<int> MoveMonsterRuleDownAt => row => MoveMonsterRuleAtCore(row, 1);
    public IReadOnlyList<string> MonsterFillerColumn => Array.Empty<string>();
    public Action<int> MonsterFillerClick => static _ => { };

    public string Vitals => _vitals;

    public string Coverage => _coverage;

    // ── settings bindings ─────────────────────────────────────────────────
    // Adjuster buttons rather than typed entry: buttons are a proven primitive
    // in plugin markup, whereas an editable field would need keyboard routing
    // plumbed through to plugin panels first.

    public string NormalHealthText => Percent(_vitalSettings.NormalHealth);
    public string NormalStaminaText => Percent(_vitalSettings.NormalStamina);
    public string NormalManaText => Percent(_vitalSettings.NormalMana);
    public string NoTargetHealthText => Percent(_vitalSettings.NoTargetHealth);
    public string NoTargetStaminaText => Percent(_vitalSettings.NoTargetStamina);
    public string NoTargetManaText => Percent(_vitalSettings.NoTargetMana);
    public string HelperHealthText => Percent(_vitalSettings.HelperHealth);
    public string HelperStaminaText => Percent(_vitalSettings.HelperStamina);
    public string HelperManaText => Percent(_vitalSettings.HelperMana);
    public string VitalUpkeepText =>
        $"Stamina to Mana / Revitalize: {OnOff(_vitalSettings.Enabled)}";
    public string TrainedOnlyText =>
        $"Trained skills only: {OnOff(_buffSettings.BuffTrainedSkillsOnly)}";
    public string AttributesText =>
        $"Buff attributes: {OnOff(_buffSettings.BuffAttributes)}";
    public string ProtectionsText =>
        $"Buff protections: {OnOff(_buffSettings.BuffProtections)}";
    public string AurasText =>
        $"Buff weapon auras: {OnOff(_buffSettings.BuffAuras)}";
    public string BanesText =>
        $"Buff banes (armor): {OnOff(_buffSettings.BuffBanes)}";
    public string RegenerationText =>
        $"Buff regen rates: {OnOff(_buffSettings.BuffRegeneration)}";
    public string OtherText =>
        $"Buff other self-spells: {OnOff(_buffSettings.BuffOther)}";

    public bool VitalUpkeepEnabled => _vitalSettings.Enabled;
    public bool HelpOthersEnabled => _vitalSettings.HelpOthers;

    public IReadOnlyList<string> ExtraBuffRows => Sorted(_buffSettings.ExtraBuffSpellNames);
    public IReadOnlyList<string> BlacklistedBuffFamilyRows =>
        Sorted(_buffSettings.BlacklistedBuffFamilyNames);
    public int SelectedExtraBuffIndex => _selectedExtraBuffRow;
    public int SelectedBlacklistedBuffIndex => _selectedBlacklistedBuffRow;
    public Action<int> DeleteExtraBuffAt => row =>
    {
        _selectedExtraBuffRow = row;
        DeleteFromNamedSet(_buffSettings.ExtraBuffSpellNames, row);
        SaveProfile();
    };
    public Action<int> DeleteBlacklistedBuffFamilyAt => row =>
    {
        _selectedBlacklistedBuffRow = row;
        DeleteFromNamedSet(_buffSettings.BlacklistedBuffFamilyNames, row);
        SaveProfile();
    };
    public Action ShowExtraBuffPicker => () => ShowBuffPickerCore(forBlacklist: false);
    public Action ShowBlacklistedBuffPicker => () => ShowBuffPickerCore(forBlacklist: true);
    public bool BuffPickerVisible => _buffPickerVisible;
    public string BuffPickerSearchText => _buffPickerSearchText;
    public Action<string> SetBuffPickerSearchText => value =>
        _buffPickerSearchText = value;
    public IReadOnlyList<string> BuffPickerRows => _host.Automation.Spells.KnownSelfBuffs
        .Select(static spell => spell.Name)
        .Where(name => string.IsNullOrWhiteSpace(_buffPickerSearchText)
            || name.Contains(_buffPickerSearchText, StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(static name => name, StringComparer.Ordinal)
        .ToArray();
    public int SelectedBuffPickerIndex => _selectedBuffPickerRow;
    public Action<int> SelectBuffPickerRow => index =>
        _selectedBuffPickerRow = index;
    public Action<int> PickBuffAt => PickBuffAtCore;
    public Action HideBuffPicker => () => _buffPickerVisible = false;

    public string TargetMethodText =>
        $"Target selection: {_combatSettings.SelectionMethod}";
    public string TargetLockText =>
        $"Target lock: {OnOff(_combatSettings.TargetLock)}";
    public string AttackRangeText =>
        $"Maximum target range: {_combatSettings.MaximumRange:0}m";
    public string MonsterRangeValueText =>
        _combatSettings.MaximumRange.ToString("0.#", CultureInfo.InvariantCulture);
    public string RingRangeValueText =>
        _combatSettings.RingDistance.ToString("0.#", CultureInfo.InvariantCulture);
    public string ApproachRangeValueText =>
        _combatSettings.ApproachDistance.ToString("0.#", CultureInfo.InvariantCulture);
    public string FollowNavMinimumValueText =>
        _navigationSettings.MinimumDistanceMeters.ToString(
            "0.#",
            CultureInfo.InvariantCulture);
    public string AngleRangeText =>
        $"Angle-selection range: {_combatSettings.TargetSelectAngleRange:0}m";
    public string AttackHeightText =>
        $"Physical attack height: {_combatSettings.AttackHeight}";
    public string AttackPowerText =>
        $"Power / accuracy: {_combatSettings.AttackPower * 100f:0}%";
    public bool TargetLockEnabled => _combatSettings.TargetLock;
    public bool SummonPetsEnabled => _combatSettings.SummonPets;
    public bool CustomPetRangeEnabled =>
        _combatSettings.PetRangeMode == PetRangeMode.Custom;
    public string PetRangeText => _combatSettings.PetRangeMode == PetRangeMode.Custom
        ? $"Pet range: {_combatSettings.PetCustomRange:0}m"
        : $"Pet range: attack ({_combatSettings.MaximumRange:0}m)";
    public string PetCustomRangeValueText =>
        _combatSettings.PetCustomRange.ToString("0.#", CultureInfo.InvariantCulture);
    public string PetDensityText =>
        $"Pet min. monsters: {_combatSettings.PetMonsterDensity}";
    public string PetDensityValueText =>
        _combatSettings.PetMonsterDensity.ToString(CultureInfo.InvariantCulture);
    public float NormalHealthValue => (float)_vitalSettings.NormalHealth;
    public float NormalStaminaValue => (float)_vitalSettings.NormalStamina;
    public float NormalManaValue => (float)_vitalSettings.NormalMana;
    public float NoTargetHealthValue => (float)_vitalSettings.NoTargetHealth;
    public float NoTargetStaminaValue => (float)_vitalSettings.NoTargetStamina;
    public float NoTargetManaValue => (float)_vitalSettings.NoTargetMana;
    public float HelperHealthValue => (float)_vitalSettings.HelperHealth;
    public float HelperStaminaValue => (float)_vitalSettings.HelperStamina;
    public float HelperManaValue => (float)_vitalSettings.HelperMana;
    public float AttackPowerValue => _combatSettings.AttackPower;

    public Action<float> SetNormalHealth => value =>
        UpdateVital(() => _vitalSettings.NormalHealth = Clamp(value));
    public Action<float> SetNormalStamina => value =>
        UpdateVital(() => _vitalSettings.NormalStamina = Clamp(value));
    public Action<float> SetNormalMana => value =>
        UpdateVital(() => _vitalSettings.NormalMana = Clamp(value));
    public Action<float> SetNoTargetHealth => value =>
        UpdateVital(() => _vitalSettings.NoTargetHealth = Clamp(value));
    public Action<float> SetNoTargetStamina => value =>
        UpdateVital(() => _vitalSettings.NoTargetStamina = Clamp(value));
    public Action<float> SetNoTargetMana => value =>
        UpdateVital(() => _vitalSettings.NoTargetMana = Clamp(value));
    public Action<float> SetHelperHealth => value =>
        UpdateVital(() => _vitalSettings.HelperHealth = Clamp(value));
    public Action<float> SetHelperStamina => value =>
        UpdateVital(() => _vitalSettings.HelperStamina = Clamp(value));
    public Action<float> SetHelperMana => value =>
        UpdateVital(() => _vitalSettings.HelperMana = Clamp(value));
    public Action<float> SetAttackPower => value => UpdateProfile(() =>
        _combatSettings.AttackPower = Math.Clamp(value, 0f, 1f));

    public float NormalHealthPercent => NormalHealthValue * 100f;
    public float NormalStaminaPercent => NormalStaminaValue * 100f;
    public float NormalManaPercent => NormalManaValue * 100f;
    public float NoTargetHealthPercent => NoTargetHealthValue * 100f;
    public float NoTargetStaminaPercent => NoTargetStaminaValue * 100f;
    public float NoTargetManaPercent => NoTargetManaValue * 100f;
    public float HelperHealthPercent => HelperHealthValue * 100f;
    public float HelperStaminaPercent => HelperStaminaValue * 100f;
    public float HelperManaPercent => HelperManaValue * 100f;
    public Action<float> SetNormalHealthPercent => value => SetNormalHealth(value / 100f);
    public Action<float> SetNormalStaminaPercent => value => SetNormalStamina(value / 100f);
    public Action<float> SetNormalManaPercent => value => SetNormalMana(value / 100f);
    public Action<float> SetNoTargetHealthPercent => value => SetNoTargetHealth(value / 100f);
    public Action<float> SetNoTargetStaminaPercent => value => SetNoTargetStamina(value / 100f);
    public Action<float> SetNoTargetManaPercent => value => SetNoTargetMana(value / 100f);
    public Action<float> SetHelperHealthPercent => value => SetHelperHealth(value / 100f);
    public Action<float> SetHelperStaminaPercent => value => SetHelperStamina(value / 100f);
    public Action<float> SetHelperManaPercent => value => SetHelperMana(value / 100f);

    public Action ToggleVitalUpkeep => () =>
    {
        _vitalSettings.Enabled = !_vitalSettings.Enabled;
        if (!_vitalSettings.Enabled)
            _vitalRecharge.Reset();
            _vitalHelperRecharge.Reset();
        SaveProfile();
    };
    public Action ToggleBuffing => () => SetMetaOption(
        "EnableBuffing",
        ExpressionValue.Boolean(!_buffSettings.Enabled));
    public Action ToggleIdlePeaceMode => () => SetMetaOption(
        "IdlePeaceMode",
        ExpressionValue.Boolean(!_combatSettings.IdlePeaceMode));
    public Action ToggleIdleBuffTopoff => () => SetMetaOption(
        "IdleBuffTopoff",
        ExpressionValue.Boolean(!_buffSettings.IdleBuffTopoff));
    public Action ToggleManaChargesWhenOff => () => SetMetaOption(
        "ManaChargesWhenOff",
        ExpressionValue.Boolean(!_inventorySettings.ManaChargesWhenOff));
    public Action ToggleHelpOthers => () => UpdateVital(() =>
        _vitalSettings.HelpOthers = !_vitalSettings.HelpOthers);
    public Action CycleTargetMethod => () => UpdateProfile(() =>
        _combatSettings.SelectionMethod = _combatSettings.SelectionMethod switch
        {
            TargetSelectionMethod.Range => TargetSelectionMethod.Angle,
            TargetSelectionMethod.Angle => TargetSelectionMethod.Both,
            _ => TargetSelectionMethod.Range,
        });
    public Action ToggleTargetLock => () => UpdateProfile(() =>
        _combatSettings.TargetLock = !_combatSettings.TargetLock);
    public Action ToggleSummonPets => () => UpdateProfile(() =>
        _combatSettings.SummonPets = !_combatSettings.SummonPets);
    public Action TogglePetRangeMode => () => UpdateProfile(() =>
        _combatSettings.PetRangeMode = _combatSettings.PetRangeMode == PetRangeMode.Custom
            ? PetRangeMode.AttackDistance
            : PetRangeMode.Custom);
    public Action PetRangeDown => () => UpdateProfile(() =>
        _combatSettings.PetCustomRange =
            Math.Max(1f, _combatSettings.PetCustomRange - 1f));
    public Action PetRangeUp => () => UpdateProfile(() =>
        _combatSettings.PetCustomRange =
            Math.Min(100f, _combatSettings.PetCustomRange + 1f));
    public Action<string> SetPetCustomRangeText => value =>
        SetDistanceText(value, 1f, 100f, distance =>
            _combatSettings.PetCustomRange = distance);
    public Action PetDensityDown => () => UpdateProfile(() =>
        _combatSettings.PetMonsterDensity =
            Math.Max(1, _combatSettings.PetMonsterDensity - 1));
    public Action PetDensityUp => () => UpdateProfile(() =>
        _combatSettings.PetMonsterDensity =
            Math.Min(25, _combatSettings.PetMonsterDensity + 1));
    public Action<string> SetPetDensityText => value =>
    {
        if (!int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int density))
        {
            return;
        }
        _combatSettings.PetMonsterDensity = Math.Clamp(density, 1, 25);
        SaveProfile();
    };
    public Action AttackRangeDown => () => UpdateProfile(() =>
        _combatSettings.MaximumRange =
            Math.Max(2f, _combatSettings.MaximumRange - 2f));
    public Action AttackRangeUp => () => UpdateProfile(() =>
        _combatSettings.MaximumRange =
            Math.Min(100f, _combatSettings.MaximumRange + 2f));
    public Action<string> SetMonsterRangeText => value =>
        SetDistanceText(value, 1f, 100f, distance =>
            _combatSettings.MaximumRange = distance);
    public Action<string> SetRingRangeText => value =>
        SetDistanceText(value, 1f, 100f, distance =>
            _combatSettings.RingDistance = distance);
    public Action<string> SetApproachRangeText => value =>
        SetDistanceText(value, 0f, 100f, distance =>
            _combatSettings.ApproachDistance = distance);
    public Action<string> SetFollowNavMinimumText => value =>
        SetDistanceText(value, 0.5f, 50f, distance =>
        {
            _navigationSettings.MinimumDistanceMeters = distance;
            SaveRouteProfile();
        }, saveProfile: false);
    public Action AngleRangeDown => () => UpdateProfile(() =>
        _combatSettings.TargetSelectAngleRange =
            Math.Max(2f, _combatSettings.TargetSelectAngleRange - 2f));
    public Action AngleRangeUp => () => UpdateProfile(() =>
        _combatSettings.TargetSelectAngleRange = Math.Min(
            _combatSettings.MaximumRange,
            _combatSettings.TargetSelectAngleRange + 2f));
    public Action CycleAttackHeight => () => UpdateProfile(() =>
        _combatSettings.AttackHeight = _combatSettings.AttackHeight switch
        {
            PluginAttackHeight.High => PluginAttackHeight.Medium,
            PluginAttackHeight.Medium => PluginAttackHeight.Low,
            _ => PluginAttackHeight.High,
        });
    public Action AttackPowerDown => () => UpdateProfile(() =>
        _combatSettings.AttackPower =
            Math.Max(0f, MathF.Round(_combatSettings.AttackPower - 0.1f, 2)));
    public Action AttackPowerUp => () => UpdateProfile(() =>
        _combatSettings.AttackPower =
            Math.Min(1f, MathF.Round(_combatSettings.AttackPower + 0.1f, 2)));

    private static double Clamp(float value) => Math.Clamp((double)value, 0d, 1d);

    private void UpdateVital(Action update)
    {
        UpdateProfile(update);
    }

    private void UpdateProfile(Action update)
    {
        update();
        SaveProfile();
    }

    private void SetDistanceText(
        string text,
        float minimum,
        float maximum,
        Action<float> apply,
        bool saveProfile = true)
    {
        if (!float.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float distance)
            && !float.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out distance))
        {
            return;
        }
        apply(Math.Clamp(distance, minimum, maximum));
        if (saveProfile)
            SaveProfile();
    }

    private static string Percent(double fraction) =>
        (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string OnOff(bool value) => value ? "on" : "off";

    private static string ProfileText(string heading, IEnumerable<string> names)
    {
        string[] entries = names
            .OrderBy(static name => name, StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        return entries.Length == 0
            ? $"{heading}: [None]"
            : $"{heading}: {string.Join("  |  ", entries)}";
    }

    private void RefreshItemEditors()
    {
        RefreshConsumableCategories();
        string[] baseNames = SortedCombatItemNames();
        _itemBaseNames = baseNames;
        _itemRows = baseNames
            .Select(name => _noBuffItemNames.Contains(name)
                ? name + "   [no buffs]"
                : name)
            .ToArray();
        _itemHandsColumn = baseNames
            .Select(name => HandednessCycle[HandednessIndex(name)])
            .ToArray();
        _consumableRows = _combatSettings.ConsumableNames
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        _selectedItemRow = ClampRow(_selectedItemRow, _itemRows.Count);
        _selectedConsumableRow = ClampRow(
            _selectedConsumableRow,
            _consumableRows.Count);
        _excludedComponentRows = ParseExcludedComponents(
            _buffSettings.BlacklistedSpellComponents);
        var excludedIcons = new uint[_excludedComponentRows.Count];
        for (int i = 0; i < excludedIcons.Length; i++)
            excludedIcons[i] = ResolveComponentIcon(_excludedComponentRows[i]);
        _excludedComponentIcons = excludedIcons;
        _selectedExcludedComponentRow = ClampRow(
            _selectedExcludedComponentRow,
            _excludedComponentRows.Count);
    }

    private int HandednessIndex(string name) =>
        _itemHandedness.TryGetValue(name, out int value) ? value : 0;

    private void DeleteItemRowAtCore(int row)
    {
        string[] names = SortedCombatItemNames();
        if ((uint)row >= (uint)names.Length)
            return;
        string removed = names[row];
        _combatSettings.CombatItemNames.Remove(removed);
        _combatSettings.CombatItemOrder.Remove(removed);
        _noBuffItemNames.Remove(removed);
        _itemHandedness.Remove(removed);
        ClearItemEnchantRows(removed);
        _profileNotice = $"Removed {removed}.";
        RefreshItemEditors();
        SaveProfile();
    }

    private void CycleItemHandsAtCore(int row)
    {
        if ((uint)row >= (uint)_itemBaseNames.Count)
            return;
        string name = _itemBaseNames[row];
        _itemHandedness[name] = (HandednessIndex(name) + 1) % HandednessCycle.Length;
        RefreshItemEditors();
    }

    private const string ExcludedComponentDelimiter = "; ";

    private static IReadOnlyList<string> ParseExcludedComponents(string setting) =>
        string.IsNullOrWhiteSpace(setting)
            ? Array.Empty<string>()
            : setting.Split(
                ';',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private uint ResolveComponentIcon(string name)
    {
        foreach (PluginInventoryItem item in _host.Automation.Items.CaptureOwnedItems())
        {
            if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                return item.IconId;
        }
        return 0u;
    }

    private void AddSelectedComponentCore()
    {
        if (!TryGetSelectedInventoryItem(out PluginInventoryItem item))
        {
            _profileNotice = "Select an owned spell component first.";
            return;
        }
        if (_excludedComponentRows.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
        {
            _profileNotice = "Blacklist entry already exists.";
            return;
        }
        var updated = new List<string>(_excludedComponentRows) { item.Name };
        string joined = string.Join(ExcludedComponentDelimiter, updated);
        _buffSettings.BlacklistedSpellComponents = joined;
        _combatSettings.BlacklistedSpellComponents = joined;
        _profileNotice = $"Added {item.Name}.";
        RefreshItemEditors();
        SaveProfile();
    }

    private void DeleteExcludedComponentAtCore(int row)
    {
        if ((uint)row >= (uint)_excludedComponentRows.Count)
            return;
        var updated = new List<string>(_excludedComponentRows);
        string removed = updated[row];
        updated.RemoveAt(row);
        string joined = string.Join(ExcludedComponentDelimiter, updated);
        _buffSettings.BlacklistedSpellComponents = joined;
        _combatSettings.BlacklistedSpellComponents = joined;
        _profileNotice = $"Removed {removed}.";
        RefreshItemEditors();
        SaveProfile();
    }

    private static string[] Sorted(IEnumerable<string> names) =>
        names.OrderBy(static name => name, StringComparer.Ordinal).ToArray();

    private static void DeleteFromNamedSet(ISet<string> set, int row)
    {
        string[] names = Sorted(set);
        if ((uint)row >= (uint)names.Length)
            return;
        set.Remove(names[row]);
    }

    private void ShowBuffPickerCore(bool forBlacklist)
    {
        _buffPickerForBlacklist = forBlacklist;
        _buffPickerSearchText = string.Empty;
        _selectedBuffPickerRow = 0;
        _buffPickerVisible = true;
    }

    private void PickBuffAtCore(int row)
    {
        string[] rows = BuffPickerRows as string[] ?? BuffPickerRows.ToArray();
        if ((uint)row >= (uint)rows.Length)
            return;
        string name = rows[row];
        if (_buffPickerForBlacklist)
            _buffSettings.BlacklistedBuffFamilyNames.Add(name);
        else
            _buffSettings.ExtraBuffSpellNames.Add(name);
        _buffPickerVisible = false;
        SaveProfile();
    }

    private static int ClampRow(int index, int count) => count == 0
        ? 0
        : Math.Clamp(index, 0, count - 1);

    private string[] SortedCombatItemNames() => _combatSettings.CombatItemNames
        .OrderBy(static name => name, StringComparer.Ordinal)
        .ToArray();

    private void RemoveSelectedItemCore()
    {
        string[] names = SortedCombatItemNames();
        if (names.Length == 0)
        {
            _profileNotice = "The Items profile is empty.";
            return;
        }
        string removed = names[ClampRow(_selectedItemRow, names.Length)];
        _combatSettings.CombatItemNames.Remove(removed);
        _combatSettings.CombatItemOrder.Remove(removed);
        _noBuffItemNames.Remove(removed);
        ClearItemEnchantRows(removed);
        _profileNotice = $"Removed {removed}.";
        RefreshItemEditors();
        SaveProfile();
    }

    private void RemoveSelectedConsumableCore()
    {
        string[] names = _combatSettings.ConsumableNames
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 0)
        {
            _profileNotice = "The Consumables profile is empty.";
            return;
        }
        string removed = names[ClampRow(_selectedConsumableRow, names.Length)];
        _combatSettings.ConsumableNames.Remove(removed);
        _combatSettings.ConsumableCategories.Remove(removed);
        _profileNotice = $"Removed {removed}.";
        RefreshItemEditors();
        SaveProfile();
    }

    private LootRule? SelectedLootRule => _inventorySettings.Loot.Rules.Count == 0
        ? null
        : _inventorySettings.Loot.Rules[Math.Clamp(
            _selectedLootRule,
            0,
            _inventorySettings.Loot.Rules.Count - 1)];

    private static string FormatClassifier(PluginLootClassifierInfo info) =>
        $"{info.DisplayName} [{info.Id}]";

    private string? ResolveClassifierId(string? value)
    {
        if (string.Equals(value?.Trim(), "VTClassic", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        foreach (PluginLootClassifierInfo info in _host.LootClassifiers.Available)
        {
            if (string.Equals(value?.Trim(), FormatClassifier(info),
                    StringComparison.Ordinal)
                || string.Equals(value?.Trim(), info.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                return info.Id;
            }
        }
        return null;
    }

    private void RefreshLootEditor(bool retainDraft = false)
    {
        int count = _inventorySettings.Loot.Rules.Count;
        _selectedLootRule = count == 0
            ? 0
            : Math.Clamp(_selectedLootRule, 0, count - 1);
        if (!retainDraft)
            _lootExpressionDraft = SelectedLootRule?.Expression ?? "*";
        _lootRuleRows = _inventorySettings.Loot.Rules
            .Select((rule, index) =>
                $"{index + 1}. {rule.Action}   P{rule.Priority}   "
                + (rule.VtankRequirements.Count == 0
                    ? rule.Expression
                    : $"[VTClassic: {rule.VtankRequirements.Count} requirements]"))
            .ToArray();
    }

    private void SelectLootRuleCore(int index)
    {
        if (index < 0 || index >= _inventorySettings.Loot.Rules.Count)
            return;
        _selectedLootRule = index;
        _lootExpressionDraft =
            _inventorySettings.Loot.Rules[index].Expression;
        _lootEditorNotice = $"Editing rule {index + 1}.";
        RefreshLootEditor(retainDraft: true);
    }

    private void SelectLootProfileCore(string name)
    {
        SaveProfile();
        if (!_lootProfiles.Select(name))
        {
            _lootEditorNotice = $"Loot profile '{name}' is unavailable.";
            return;
        }
        LoadLootProfile();
        _lootEditorNotice = $"Loaded loot profile {_lootProfiles.Selected}.";
    }

    private void CreateLootProfileCore(bool copyCurrent)
    {
        if (!_lootProfiles.Create(
            _lootProfileNameDraft,
            copyCurrent,
            _inventorySettings.Loot.Rules,
            out string notice,
            _inventorySettings.Loot))
        {
            _lootEditorNotice = notice;
            return;
        }
        _lootProfileNameDraft = string.Empty;
        LoadLootProfile();
        _lootEditorNotice = notice;
    }

    private void ClearLootProfileCore()
    {
        _lootProfiles.ClearCurrent(
            _inventorySettings.Loot.Rules,
            _inventorySettings.Loot);
        _loot.Reset();
        RefreshLootEditor();
        _lootEditorNotice = $"Cleared {_lootProfiles.Selected}.";
    }

    private void DeleteLootProfileCore()
    {
        if (!_lootProfiles.Delete(out string notice))
        {
            _lootEditorNotice = notice;
            return;
        }
        LoadLootProfile();
        _lootEditorNotice = notice;
    }

    private void LoadLootProfile()
    {
        if (!_lootProfiles.LoadCurrent(
            _inventorySettings.Loot.Rules,
            _inventorySettings.Loot))
        {
            _lootProfiles.SaveCurrent(
                _inventorySettings.Loot.Rules,
                _inventorySettings.Loot);
        }
        _loot.Reset();
        RefreshLootEditor();
    }

    private void ApplyLootExpressionCore()
    {
        if (SelectedLootRule is not { } rule)
        {
            _lootEditorNotice = "Add a loot rule first.";
            return;
        }
        try
        {
            _ = LootRuleExpression.Compile(_lootExpressionDraft);
            rule.Expression = _lootExpressionDraft;
            rule.CustomExpression = string.Empty;
            rule.VtankRequirements.Clear();
            _lootEditorNotice = $"Updated {rule.Name}.";
            _loot.Reset();
            RefreshLootEditor();
            SaveProfile();
        }
        catch (FormatException error)
        {
            _lootEditorNotice = error.Message;
        }
    }

    private void AddLootRuleCore()
    {
        var rule = new LootRule
        {
            Name = $"Rule {_inventorySettings.Loot.Rules.Count + 1}",
            Expression = "*",
            Action = LootAction.Keep,
        };
        _inventorySettings.Loot.Rules.Add(rule);
        _selectedLootRule = _inventorySettings.Loot.Rules.Count - 1;
        _lootExpressionDraft = rule.Expression;
        _lootEditorNotice = $"Added {rule.Name}.";
        _loot.Reset();
        RefreshLootEditor();
        SaveProfile();
    }

    private void RemoveLootRuleCore()
    {
        if (SelectedLootRule is not { } rule)
        {
            _lootEditorNotice = "The loot profile is empty.";
            return;
        }
        _inventorySettings.Loot.Rules.RemoveAt(_selectedLootRule);
        _selectedLootRule = Math.Min(
            _selectedLootRule,
            Math.Max(0, _inventorySettings.Loot.Rules.Count - 1));
        _lootEditorNotice = $"Removed {rule.Name}.";
        _loot.Reset();
        RefreshLootEditor();
        SaveProfile();
    }

    private void MoveLootRule(int direction)
    {
        if (SelectedLootRule is not { } rule)
            return;
        int destination = _selectedLootRule + Math.Sign(direction);
        if (destination < 0 || destination >= _inventorySettings.Loot.Rules.Count)
            return;
        _inventorySettings.Loot.Rules.RemoveAt(_selectedLootRule);
        _inventorySettings.Loot.Rules.Insert(destination, rule);
        _selectedLootRule = destination;
        _lootEditorNotice = $"Moved {rule.Name}.";
        _loot.Reset();
        RefreshLootEditor();
        SaveProfile();
    }

    private void SelectLootActionCore(string value)
    {
        if (SelectedLootRule is not { } rule
            || !Enum.TryParse(value, ignoreCase: true, out LootAction action))
        {
            return;
        }
        rule.Action = action;
        _lootEditorNotice = $"{rule.Name}: {action}.";
        _loot.Reset();
        RefreshLootEditor();
        SaveProfile();
    }

    private void UpdateSelectedLootRule(Action<LootRule> update)
    {
        if (SelectedLootRule is not { } rule)
            return;
        update(rule);
        _lootEditorNotice = $"Updated {rule.Name}.";
        _loot.Reset();
        RefreshLootEditor();
        SaveProfile();
    }

    private void RefreshRouteEditor()
    {
        _selectedRouteWaypoint = ClampRow(
            _selectedRouteWaypoint,
            _navigationSettings.Waypoints.Count);
        int active = _navigation.CurrentWaypointIndex;
        _routeRows = _navigationSettings.Waypoints
            .Select((waypoint, index) =>
                $"{(index == active && _navigationSettings.Enabled ? "<<" : "  ")} "
                + waypoint.DisplayText)
            .ToArray();
        var counts = new string[_routeRows.Count];
        for (int i = 0; i < counts.Length; i++)
            counts[i] = (i + 1).ToString(CultureInfo.InvariantCulture);
        _routeWaypointCounts = counts;
    }

    private void AddRoutePointCore()
    {
        PluginNavigationSnapshot snapshot =
            _host.Automation.Navigation.Snapshot;
        if (!snapshot.IsAvailable)
        {
            _routeNotice = "Current position is unavailable.";
            return;
        }
        AddRouteWaypoint(new RouteWaypoint
        {
            Type = RouteWaypointType.Point,
            Position = snapshot.Position,
        });
        _routeNotice = $"Added point {RouteWaypoint.FormatPosition(snapshot.Position)}.";
    }

    private void AddRouteCheckpointCore()
    {
        PluginNavigationSnapshot snapshot =
            _host.Automation.Navigation.Snapshot;
        if (!snapshot.IsAvailable)
        {
            _routeNotice = "Current position is unavailable.";
            return;
        }
        AddRouteWaypoint(new RouteWaypoint
        {
            Type = RouteWaypointType.Checkpoint,
            Position = snapshot.Position,
        });
        _routeNotice = "Added checkpoint.";
    }

    private void AddSelectedObjectWaypoint(RouteWaypointType type)
    {
        uint selected = _host.Selection.SelectedObjectId ?? 0u;
        if (selected == 0u
            || !_host.Automation.Navigation.TryGetObject(
                selected,
                out PluginNavigationObject target))
        {
            _routeNotice = "Select a live portal, NPC, or vendor first.";
            return;
        }
        AddRouteWaypoint(new RouteWaypoint
        {
            Type = type,
            Position = _host.Automation.Navigation.Snapshot.Position,
            ReferencePosition = target.Position,
            ObjectId = target.ObjectId,
            ObjectName = target.Name,
        });
        _routeNotice = $"Added {type}: {target.Name}.";
    }

    private void AddRouteRecallCore()
    {
        string name = RouteWaypoint.RecallDisplayName(_routeRecallKind);
        AddRouteWaypoint(new RouteWaypoint
        {
            Type = RouteWaypointType.Recall,
            Recall = _routeRecallKind,
            RecallSpellId = RouteWaypoint.SpellIdForRecall(_routeRecallKind),
            RecallSpellName = name,
            Position = _host.Automation.Navigation.Snapshot.Position,
        });
        _routeNotice = $"Added {name}.";
    }

    private void AddRoutePauseCore()
    {
        AddRouteWaypoint(new RouteWaypoint
        {
            Type = RouteWaypointType.Pause,
            DurationMilliseconds = _routePauseSeconds * 1000,
            Position = _host.Automation.Navigation.Snapshot.Position,
        });
        _routeNotice = $"Added {_routePauseSeconds}-second pause.";
    }

    private void AddRouteChatCore()
    {
        string text = _routeChatDraft.Trim();
        if (text.Length == 0)
        {
            _routeNotice = "Enter a chat command first.";
            return;
        }
        AddRouteWaypoint(new RouteWaypoint
        {
            Type = RouteWaypointType.ChatCommand,
            Text = text,
            Position = _host.Automation.Navigation.Snapshot.Position,
        });
        _routeNotice = $"Added chat command {text}.";
    }

    private void AddRouteJumpCore()
    {
        PluginNavigationSnapshot snapshot =
            _host.Automation.Navigation.Snapshot;
        if (!snapshot.IsAvailable)
        {
            _routeNotice = "Current heading is unavailable.";
            return;
        }
        AddRouteWaypoint(new RouteWaypoint
        {
            Type = RouteWaypointType.Jump,
            Position = snapshot.Position,
            JumpHeadingDegrees = snapshot.Position.HeadingDegrees,
            JumpRun = true,
            JumpChargeMilliseconds = 1000,
            JumpDirection = RouteJumpDirection.Forward,
        });
        _routeNotice = "Added forward jump.";
    }

    private void AddRouteWaypoint(RouteWaypoint waypoint)
    {
        int count = _navigationSettings.Waypoints.Count;
        int insertion = _routeInsertMode switch
        {
            RouteInsertMode.InsertAbove => Math.Clamp(_selectedRouteWaypoint, 0, count),
            RouteInsertMode.InsertBelow => Math.Min(count, _selectedRouteWaypoint + 1),
            _ => count,
        };
        _navigationSettings.Waypoints.Insert(insertion, waypoint);
        _selectedRouteWaypoint = insertion;
        _navigation.Reset();
        RefreshRouteEditor();
        SaveRouteProfile();
    }

    private void RemoveRouteWaypointCore()
    {
        if (_navigationSettings.Waypoints.Count == 0)
        {
            _routeNotice = "The route is empty.";
            return;
        }
        int index = ClampRow(
            _selectedRouteWaypoint,
            _navigationSettings.Waypoints.Count);
        string removed = _navigationSettings.Waypoints[index].DisplayText;
        _navigationSettings.Waypoints.RemoveAt(index);
        _selectedRouteWaypoint = ClampRow(
            index,
            _navigationSettings.Waypoints.Count);
        _navigation.Reset();
        RefreshRouteEditor();
        SaveRouteProfile();
        _routeNotice = $"Removed {removed}.";
    }

    private void DeleteRouteWaypointAtCore(int row)
    {
        if ((uint)row >= (uint)_navigationSettings.Waypoints.Count)
            return;
        string removed = _navigationSettings.Waypoints[row].DisplayText;
        _navigationSettings.Waypoints.RemoveAt(row);
        _selectedRouteWaypoint = ClampRow(
            _selectedRouteWaypoint,
            _navigationSettings.Waypoints.Count);
        _navigation.Reset();
        RefreshRouteEditor();
        SaveRouteProfile();
        _routeNotice = $"Removed {removed}.";
    }

    private void SelectNearestRouteWaypointCore()
    {
        PluginNavigationSnapshot player = _host.Automation.Navigation.Snapshot;
        if (!player.IsAvailable || _navigationSettings.Waypoints.Count == 0)
        {
            _routeNotice = "Current position is unavailable.";
            return;
        }
        int best = 0;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < _navigationSettings.Waypoints.Count; i++)
        {
            RouteWaypoint waypoint = _navigationSettings.Waypoints[i];
            if (waypoint.Position.CellId == 0u)
                continue;
            double distance = player.Position.HorizontalDistanceMeters(waypoint.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        _selectedRouteWaypoint = best;
        _routeNotice = $"Selected nearest waypoint ({_navigationSettings.Waypoints[best].DisplayText}).";
    }

    private void MoveRouteWaypoint(int direction)
    {
        if (_navigationSettings.Waypoints.Count < 2)
            return;
        int source = ClampRow(
            _selectedRouteWaypoint,
            _navigationSettings.Waypoints.Count);
        int destination = source + Math.Sign(direction);
        if (destination < 0 || destination >= _navigationSettings.Waypoints.Count)
            return;
        RouteWaypoint waypoint = _navigationSettings.Waypoints[source];
        _navigationSettings.Waypoints.RemoveAt(source);
        _navigationSettings.Waypoints.Insert(destination, waypoint);
        _selectedRouteWaypoint = destination;
        _navigation.Reset();
        RefreshRouteEditor();
        SaveRouteProfile();
    }

    private void CaptureFollowTarget()
    {
        uint selected = _host.Selection.SelectedObjectId ?? 0u;
        if (selected == 0u
            || !_host.Automation.Navigation.TryGetObject(
                selected,
                out PluginNavigationObject target))
        {
            _routeNotice = "Select a live object to follow first.";
            return;
        }
        _navigationSettings.FollowTargetObjectId = target.ObjectId;
        _navigationSettings.FollowTargetName = target.Name;
        _navigationSettings.Mode = RouteMode.Target;
        _navigation.Reset();
        RefreshRouteEditor();
        SaveRouteProfile();
        _routeNotice = $"Following {target.Name}.";
    }

    private void SelectRouteProfileCore(string name)
    {
        SaveRouteProfile();
        if (!_routeProfiles.Select(name))
        {
            _routeNotice = $"Route profile '{name}' is unavailable.";
            return;
        }
        LoadRouteProfile();
        _routeNotice = $"Loaded route {_routeProfiles.Selected}.";
    }

    private void CreateRouteProfileCore(bool copyCurrent)
    {
        if (!_routeProfiles.Create(
                _routeProfileNameDraft,
                copyCurrent,
                _navigationSettings,
                out string notice))
        {
            _routeNotice = notice;
            return;
        }
        _routeProfileNameDraft = string.Empty;
        LoadRouteProfile();
        _routeNotice = notice;
    }

    private void ClearRouteProfileCore()
    {
        _routeProfiles.ClearCurrent(_navigationSettings);
        _navigation.Reset();
        RefreshRouteEditor();
        _routeNotice = $"Cleared {_routeProfiles.Selected}.";
    }

    private void DeleteRouteProfileCore()
    {
        if (!_routeProfiles.Delete(out string notice))
        {
            _routeNotice = notice;
            return;
        }
        LoadRouteProfile();
        _routeNotice = notice;
    }

    private void LoadRouteProfile()
    {
        if (!_routeProfiles.LoadCurrent(_navigationSettings, _host.Automation.Spells))
            _routeProfiles.SaveCurrent(_navigationSettings);
        if (_initialized)
            ApplyPersistedOptionOverrides();
        _navigation.Reset();
        RefreshRouteEditor();
    }

    private void SaveRouteProfile() =>
        _routeProfiles.SaveCurrent(_navigationSettings);

    private void SelectTab(TankTab tab)
    {
        _activeTab = tab;
        _lootEditorVisible = false;
        _advancedOptionsVisible = false;
        _buffPickerVisible = false;
        _metaEditorVisible = false;
    }

    private void LoadAdvancedOptionDraft()
    {
        _advancedOptionValueDraft = GetMetaOption(AdvancedOptionName)
            .ToDisplayString();
        _advancedOptionNotice = $"Editing {AdvancedOptionName}.";
    }

    private void ApplyAdvancedOptionCore()
    {
        if (!TryParseOptionValue(
                _advancedOptionValueDraft.Trim(),
                out ExpressionValue value))
        {
            _advancedOptionNotice = "Enter a value first.";
            return;
        }
        string name = AdvancedOptionName;
        try
        {
            if (!SetMetaOption(name, value))
            {
                _advancedOptionNotice = $"{name} is unavailable.";
                return;
            }
            RefreshAdvancedOptions();
            LoadAdvancedOptionDraft();
            _advancedOptionNotice = $"Applied {name}.";
        }
        catch (Exception exception) when (exception is FormatException
            or OverflowException)
        {
            _advancedOptionNotice = exception.Message;
        }
    }

    private void FlipAdvancedOptionBool(int index, string name)
    {
        _selectedAdvancedOption = index;
        ExpressionValue next = ExpressionValue.Boolean(!GetMetaOption(name).IsTruthy);
        _advancedOptionNotice = SetMetaOption(name, next)
            ? $"Set {name} = {next.ToDisplayString()}."
            : $"{name} is unavailable.";
        RefreshAdvancedOptions();
        LoadAdvancedOptionDraft();
    }

    private void CycleAdvancedOptionEnum(int index, string name)
    {
        _selectedAdvancedOption = index;
        if (!VtankDefaultSettingsDatabase.SettingEnumValues.TryGetValue(
                name, out IReadOnlyList<VtankEnumValue>? entries)
            || entries.Count == 0)
        {
            SelectAdvancedOption(index);
            return;
        }
        int current = GetMetaOption(name).AsInt32();
        int currentPosition = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Value == current)
            {
                currentPosition = i;
                break;
            }
        }
        VtankEnumValue nextEntry = entries[(currentPosition + 1) % entries.Count];
        _advancedOptionNotice = SetMetaOption(name, ExpressionValue.Number(nextEntry.Value))
            ? $"Set {name} = {nextEntry.Label}."
            : $"{name} is unavailable.";
        RefreshAdvancedOptions();
        LoadAdvancedOptionDraft();
    }

    /// <summary>VTank: row 0 is always DEFAULT and can never be empty.</summary>
    private void EnsureDefaultMonsterRule()
    {
        if (_combatSettings.Rules.Count == 0)
            _combatSettings.Rules.Add(new MonsterRule("DEFAULT", 0));
    }

    private void RefreshMonsterEditor()
    {
        int count = _combatSettings.Rules.Count;
        var fester = new bool[count];
        var broadside = new bool[count];
        var gravityWell = new bool[count];
        var imperil = new bool[count];
        var yield = new bool[count];
        var vulnerability = new bool[count];
        var attack = new bool[count];
        var ring = new bool[count];
        var streak = new bool[count];
        var weakening = new bool[count];
        var festering = new bool[count];
        var corruption = new bool[count];
        var destructive = new bool[count];
        var corrosion = new bool[count];
        var names = new string[count];
        var priorities = new string[count];
        var damage = new string[count];
        var extraVuln = new string[count];
        var weapon = new string[count];
        var offhand = new string[count];
        var petDamage = new string[count];
        var moveUpIcons = new uint[count];
        var moveDownIcons = new uint[count];
        for (int i = 0; i < count; i++)
        {
            MonsterRule rule = _combatSettings.Rules[i];
            MonsterRuleActions actions = rule.Actions;
            MonsterActionFlags flags = actions.Flags;
            fester[i] = (flags & MonsterActionFlags.Fester) != 0;
            broadside[i] = (flags & MonsterActionFlags.Broadside) != 0;
            gravityWell[i] = (flags & MonsterActionFlags.GravityWell) != 0;
            imperil[i] = (flags & MonsterActionFlags.Imperil) != 0;
            yield[i] = (flags & MonsterActionFlags.Yield) != 0;
            vulnerability[i] = (flags & MonsterActionFlags.Vulnerability) != 0;
            attack[i] = (flags & MonsterActionFlags.Attack) != 0;
            ring[i] = (flags & MonsterActionFlags.Ring) != 0;
            streak[i] = (flags & MonsterActionFlags.Streak) != 0;
            weakening[i] = (flags & MonsterActionFlags.WeakeningCurse) != 0;
            festering[i] = (flags & MonsterActionFlags.FesteringCurse) != 0;
            corruption[i] = (flags & MonsterActionFlags.Corruption) != 0;
            destructive[i] = (flags & MonsterActionFlags.DestructiveCurse) != 0;
            corrosion[i] = (flags & MonsterActionFlags.Corrosion) != 0;
            names[i] = rule.Expression;
            priorities[i] = actions.Priority.ToString(CultureInfo.InvariantCulture);
            damage[i] = DamageTypeDisplay(actions.DamageType);
            extraVuln[i] = DamageTypeDisplay(actions.ExtraVulnerability);
            weapon[i] = ItemDisplayName(actions.WeaponObjectId, actions.WeaponName);
            offhand[i] = ItemDisplayName(actions.OffhandObjectId, actions.OffhandName);
            petDamage[i] = DamageTypeDisplay(actions.PetDamageType);
            moveUpIcons[i] = 0x060028FCu;
            moveDownIcons[i] = 0x060028FDu;
        }
        _monsterFesterColumn = fester;
        _monsterBroadsideColumn = broadside;
        _monsterGravityWellColumn = gravityWell;
        _monsterImperilColumn = imperil;
        _monsterYieldColumn = yield;
        _monsterVulnerabilityColumn = vulnerability;
        _monsterAttackColumn = attack;
        _monsterRingColumn = ring;
        _monsterStreakColumn = streak;
        _monsterWeakeningColumn = weakening;
        _monsterFesteringColumn = festering;
        _monsterCorruptionColumn = corruption;
        _monsterDestructiveColumn = destructive;
        _monsterCorrosionColumn = corrosion;
        _monsterNameColumn = names;
        _monsterPriorityColumn = priorities;
        _monsterDamageColumn = damage;
        _monsterExtraVulnColumn = extraVuln;
        _monsterWeaponColumn = weapon;
        _monsterOffhandColumn = offhand;
        _monsterPetDamageColumn = petDamage;
        _monsterMoveUpIconsColumn = moveUpIcons;
        _monsterMoveDownIconsColumn = moveDownIcons;
    }

    private void ToggleMonsterFlagAt(int row, MonsterActionFlags flag) =>
        UpdateMonsterActionsAt(row, actions => actions with { Flags = actions.Flags ^ flag });

    private void AddMonsterRuleCore(string expression)
    {
        try
        {
            var rule = new MonsterRule(expression, new MonsterRuleActions());
            _combatSettings.Rules.Add(rule);
            _monsterEditorNotice = $"Added {expression}.";
            RefreshMonsterEditor();
            SaveProfile();
        }
        catch (FormatException error)
        {
            _monsterEditorNotice = error.Message;
        }
    }

    private void AddSelectedMonsterCore()
    {
        uint selectedId = _host.Selection.SelectedObjectId ?? 0u;
        PluginCombatTarget selected = default;
        bool found = false;
        foreach (PluginCombatTarget candidate in
            _host.Automation.Combat.CaptureHostileTargets(float.MaxValue))
        {
            if (candidate.ObjectId != selectedId)
                continue;
            selected = candidate;
            found = true;
            break;
        }
        if (!found || string.IsNullOrWhiteSpace(selected.Name))
        {
            _monsterEditorNotice = "Select a monster in the world first.";
            return;
        }
        AddMonsterRuleCore(EscapeMonsterLiteral(selected.Name));
    }

    private static string EscapeMonsterLiteral(string value)
    {
        const string operators = "%/*+-#><=&|()";
        var result = new System.Text.StringBuilder(value.Length + 8);
        foreach (char character in value)
        {
            if (char.IsDigit(character)
                || character == '\\'
                || operators.Contains(character))
            {
                result.Append('\\');
            }
            result.Append(character);
        }
        return result.ToString();
    }

    private void DeleteMonsterRuleAtCore(int row)
    {
        if (row < 0 || row >= _combatSettings.Rules.Count)
            return;
        MonsterRule rule = _combatSettings.Rules[row];
        if (rule.IsDefault)
        {
            _monsterEditorNotice = "DEFAULT cannot be removed.";
            return;
        }
        _combatSettings.Rules.RemoveAt(row);
        _monsterEditorNotice = $"Removed {rule.Expression}.";
        EnsureDefaultMonsterRule();
        RefreshMonsterEditor();
        SaveProfile();
    }

    private void CycleMonsterPriorityAtCore(int row) =>
        UpdateMonsterActionsAt(row, actions =>
        {
            int next = actions.BoundedPriority + 1;
            if (next == 5)
                next = -1;
            return actions with { Priority = next };
        });

    private static MonsterDamageType CycleDamage(
        MonsterDamageType current, MonsterDamageType[] cycle)
    {
        int index = Array.IndexOf(cycle, current);
        int next = index < 0 ? 0 : (index + 1) % cycle.Length;
        return cycle[next];
    }

    private void CycleMonsterEquipmentAt(int row, bool offhand)
    {
        string[] names = SortedCombatItemNames();
        UpdateMonsterActionsAt(row, actions =>
        {
            string currentName = offhand ? actions.OffhandName : actions.WeaponName;
            int currentIndex = string.IsNullOrEmpty(currentName)
                ? -1
                : Array.IndexOf(names, currentName);
            if (names.Length == 0)
                return SetWeaponSlot(actions, offhand, null);
            int nextIndex = currentIndex + 1;
            return nextIndex >= names.Length
                ? SetWeaponSlot(actions, offhand, null)
                : SetWeaponSlot(actions, offhand, names[nextIndex]);
        });
    }

    private MonsterRuleActions SetWeaponSlot(
        MonsterRuleActions actions, bool offhand, string? name)
    {
        uint objectId = 0u;
        if (!string.IsNullOrEmpty(name))
        {
            foreach (PluginInventoryItem item in _host.Automation.Items.CaptureOwnedItems())
            {
                if (string.Equals(item.Name, name, StringComparison.Ordinal))
                {
                    objectId = item.ObjectId;
                    break;
                }
            }
        }
        return offhand
            ? actions with { OffhandObjectId = objectId, OffhandName = name ?? string.Empty }
            : actions with { WeaponObjectId = objectId, WeaponName = name ?? string.Empty };
    }

    private static string DamageTypeDisplay(MonsterDamageType value) => value switch
    {
        MonsterDamageType.Electric => "Lightning",
        MonsterDamageType.VoidBasic or MonsterDamageType.Nether => "Void Basic",
        MonsterDamageType.DrainAuto => "Drain Auto",
        MonsterDamageType.PlayerAuto => "PAuto",
        _ => value.ToString(),
    };

    private string ItemDisplayName(uint objectId, string durableName)
    {
        if (!string.IsNullOrWhiteSpace(durableName))
            return durableName;
        if (objectId == 0u)
            return "<AUTO>";
        foreach (PluginInventoryItem item in
            _host.Automation.Items.CaptureOwnedItems())
        {
            if (item.ObjectId == objectId)
                return item.Name;
        }
        return $"0x{objectId:X8}";
    }

    private void MoveMonsterRuleAtCore(int row, int direction)
    {
        if (row < 0 || row >= _combatSettings.Rules.Count)
            return;
        MonsterRule current = _combatSettings.Rules[row];
        if (current.IsDefault)
            return;
        int destination = row + direction;
        if (destination < 0 || destination >= _combatSettings.Rules.Count)
            return;
        if (_combatSettings.Rules[destination].IsDefault)
            return;
        _combatSettings.Rules.RemoveAt(row);
        _combatSettings.Rules.Insert(destination, current);
        _monsterEditorNotice = $"Moved {current.Expression}.";
        RefreshMonsterEditor();
        SaveProfile();
    }

    private void UpdateMonsterActionsAt(
        int row, Func<MonsterRuleActions, MonsterRuleActions> update)
    {
        if (row < 0 || row >= _combatSettings.Rules.Count)
            return;
        MonsterRule current = _combatSettings.Rules[row];
        _combatSettings.Rules[row] = new MonsterRule(current.Expression, update(current.Actions));
        RefreshMonsterEditor();
        SaveProfile();
    }

    private void AddSelectedProfileItem(bool noBuffs)
    {
        if (!TryGetSelectedInventoryItem(out PluginInventoryItem item))
        {
            _profileNotice = "Select an owned inventory item first.";
            return;
        }
        _combatSettings.CombatItemObjectIds.Add(item.ObjectId);
        if (_combatSettings.CombatItemNames.Add(item.Name))
            _combatSettings.CombatItemOrder.Add(item.Name);
        if (noBuffs)
            _noBuffItemNames.Add(item.Name);
        else
            _noBuffItemNames.Remove(item.Name);
        PopulateItemEnchantRows(item, noBuffs);
        _profileNotice = noBuffs
            ? $"Added {item.Name} (no buffs)."
            : $"Added {item.Name}.";
        RefreshItemEditors();
        SaveProfile();
    }

    private void PopulateItemEnchantRows(in PluginInventoryItem item, bool noBuffs)
    {
        ClearItemEnchantRows(item.Name);
        if (!ItemEnchantDefaults.IsProfileEligible(in item))
            return;
        IReadOnlyList<string> defaults = ItemEnchantDefaults.Rows(in item, noBuffs);
        if (defaults.Count == 0)
        {
            _buffSettings.ItemEnchantRows.Add(
                new BuffItemEnchantRow(item.Name, string.Empty));
            return;
        }
        foreach (string spellName in defaults)
            _buffSettings.ItemEnchantRows.Add(new BuffItemEnchantRow(item.Name, spellName));
    }

    /// <summary><c>eq.b(int itemId)</c> (<c>eq.cs:59-68</c>).</summary>
    private void ClearItemEnchantRows(string itemName)
    {
        for (int i = _buffSettings.ItemEnchantRows.Count - 1; i >= 0; i--)
        {
            if (string.Equals(
                    _buffSettings.ItemEnchantRows[i].ItemName,
                    itemName,
                    StringComparison.Ordinal))
            {
                _buffSettings.ItemEnchantRows.RemoveAt(i);
            }
        }
    }

    private void AddSelectedConsumableCore()
    {
        if (!TryGetSelectedInventoryItem(out PluginInventoryItem item))
        {
            _profileNotice = "Select an owned consumable first.";
            return;
        }
        _combatSettings.ConsumableNames.Add(item.Name);
        _combatSettings.ConsumableCategories[item.Name] =
            ConsumableClassifier.Classify(item);
        _profileNotice = $"Added {item.Name}.";
        RefreshItemEditors();
        SaveProfile();
    }

    private void AddAllPeasCore()
    {
        bool added = _combatSettings.ConsumableNames.Add(
            CraftingPlanner.AllPeas);
        _combatSettings.ConsumableCategories[CraftingPlanner.AllPeas] =
            ConsumableCategory.AllPeas;
        _profileNotice = added
            ? "Added [All Peas]."
            : "[All Peas] is already in this profile.";
        if (added)
        {
            RefreshItemEditors();
            SaveProfile();
        }
    }

    private void RefreshConsumableCategories()
    {
        foreach (string stale in _combatSettings.ConsumableCategories.Keys
            .Where(name => !_combatSettings.ConsumableNames.Contains(name))
            .ToArray())
        {
            _combatSettings.ConsumableCategories.Remove(stale);
        }
        foreach (string name in _combatSettings.ConsumableNames)
        {
            if (!_combatSettings.ConsumableCategories.ContainsKey(name))
            {
                _combatSettings.ConsumableCategories[name] =
                    ConsumableClassifier.ClassifyName(name);
            }
        }
        foreach (PluginInventoryItem item in
            _host.Automation.Items.CaptureOwnedItems())
        {
            if (_combatSettings.ConsumableNames.Contains(item.Name))
            {
                _combatSettings.ConsumableCategories[item.Name] =
                    ConsumableClassifier.Classify(item);
            }
        }
    }

    private bool TryGetSelectedInventoryItem(out PluginInventoryItem selected)
    {
        uint selectedId = _host.Selection.SelectedObjectId ?? 0u;
        if (selectedId != 0u)
        {
            foreach (PluginInventoryItem item in
                _host.Automation.Items.CaptureOwnedItems())
            {
                if (item.ObjectId == selectedId)
                {
                    selected = item;
                    return true;
                }
            }
        }
        selected = default;
        return false;
    }

    private MetaRule? SelectedMetaRule =>
        (uint)_selectedMetaRule < (uint)_metaProfile.Rules.Count
            ? _metaProfile.Rules[_selectedMetaRule]
            : null;

    private void RefreshMetaEditor()
    {
        int count = _metaProfile.Rules.Count;
        var rows = new string[count];
        var deleteColumn = new string[count];
        var moveUpIcons = new uint[count];
        var moveDownIcons = new uint[count];
        var stateColumn = new string[count];
        var conditionColumn = new string[count];
        var actionColumn = new string[count];
        for (int i = 0; i < count; i++)
        {
            MetaRule rule = _metaProfile.Rules[i];
            string conditionText = DescribeMetaCondition(rule.Condition);
            string actionText = DescribeMetaAction(rule.Action);
            rows[i] = $"{rule.State,-16}  {conditionText,-34}  {actionText}";
            deleteColumn[i] = "X";
            moveUpIcons[i] = 0x060028FCu;
            moveDownIcons[i] = 0x060028FDu;
            stateColumn[i] = rule.State;
            conditionColumn[i] = conditionText;
            actionColumn[i] = actionText;
        }
        _metaRows = rows;
        _metaDeleteColumn = deleteColumn;
        _metaMoveUpIconsColumn = moveUpIcons;
        _metaMoveDownIconsColumn = moveDownIcons;
        _metaStateColumn = stateColumn;
        _metaConditionColumn = conditionColumn;
        _metaActionColumn = actionColumn;
        _selectedMetaRule = ClampRow(_selectedMetaRule, _metaProfile.Rules.Count);
        if (SelectedMetaRule is not MetaRule selected)
            return;
        _metaStateDraft = selected.State;
        _metaConditionKind = selected.Condition.Kind;
        _metaConditionTextDraft = selected.Condition.Text;
        _metaActionKind = selected.Action.Kind;
        _metaActionTextDraft = selected.Action.Text;
        _metaSecondaryTextDraft = selected.Action.SecondaryText;
        _metaNumber = checked((int)Math.Clamp(
            selected.Condition.Number,
            int.MinValue,
            int.MaxValue));
        _metaSecondaryNumber = checked((int)Math.Clamp(
            selected.Condition.SecondaryNumber,
            int.MinValue,
            int.MaxValue));
    }

    private void SelectMetaRuleCore(int index)
    {
        _selectedMetaRule = ClampRow(index, _metaProfile.Rules.Count);
        RefreshMetaEditor();
        _metaNotice = SelectedMetaRule is null
            ? "Add a rule or select one to edit."
            : "Editing the selected ordered Meta rule.";
    }

    private void AddMetaRuleCore()
    {
        if (!TryBuildMetaRule(out MetaRule rule, out string error))
        {
            _metaNotice = error;
            return;
        }
        _metaProfile.Rules.Add(rule);
        _selectedMetaRule = _metaProfile.Rules.Count - 1;
        RefreshMetaEditor();
        if (SaveMetaProfile())
            _metaNotice = $"Added rule in {rule.State}.";
    }

    private void ApplyMetaRuleCore()
    {
        if (SelectedMetaRule is not MetaRule current)
        {
            AddMetaRuleCore();
            return;
        }
        if (!TryBuildMetaRule(out MetaRule replacement, out string error))
        {
            _metaNotice = error;
            return;
        }
        replacement.Id = current.Id;
        _metaProfile.Rules[_selectedMetaRule] = replacement;
        RefreshMetaEditor();
        if (SaveMetaProfile())
            _metaNotice = $"Updated rule in {replacement.State}.";
    }

    private bool TryBuildMetaRule(out MetaRule rule, out string error)
    {
        string state = string.IsNullOrWhiteSpace(_metaStateDraft)
            ? MetaEngine.DefaultState
            : _metaStateDraft.Trim();
        try
        {
            if (_metaConditionKind == MetaConditionKind.Expression)
                _ = ExpressionProgram.Compile(_metaConditionTextDraft);
            if (_metaActionKind is MetaActionKind.ExpressionAction
                or MetaActionKind.ChatExpression)
            {
                _ = ExpressionProgram.Compile(_metaActionTextDraft);
            }
            rule = new MetaRule
            {
                State = state,
                Condition = new MetaCondition
                {
                    Kind = _metaConditionKind,
                    Text = _metaConditionTextDraft,
                    Number = _metaNumber,
                    SecondaryNumber = _metaSecondaryNumber,
                },
                Action = new MetaAction
                {
                    Kind = _metaActionKind,
                    Text = _metaActionTextDraft,
                    SecondaryText = _metaSecondaryTextDraft,
                    Number = _metaNumber,
                    SecondaryNumber = _metaSecondaryNumber,
                },
            };
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            rule = new MetaRule();
            error = exception.Message;
            return false;
        }
    }

    private void RemoveMetaRuleCore()
    {
        if (SelectedMetaRule is not MetaRule selected)
            return;
        _metaProfile.Rules.RemoveAt(_selectedMetaRule);
        _selectedMetaRule = Math.Min(
            _selectedMetaRule,
            Math.Max(0, _metaProfile.Rules.Count - 1));
        RefreshMetaEditor();
        if (SaveMetaProfile())
            _metaNotice = $"Removed rule from {selected.State}.";
    }

    private void MoveMetaRule(int direction)
    {
        int destination = _selectedMetaRule + Math.Sign(direction);
        if ((uint)_selectedMetaRule >= (uint)_metaProfile.Rules.Count
            || (uint)destination >= (uint)_metaProfile.Rules.Count)
        {
            return;
        }
        MetaRule rule = _metaProfile.Rules[_selectedMetaRule];
        _metaProfile.Rules.RemoveAt(_selectedMetaRule);
        _metaProfile.Rules.Insert(destination, rule);
        _selectedMetaRule = destination;
        RefreshMetaEditor();
        if (SaveMetaProfile())
            _metaNotice = $"Moved {rule.State} rule.";
    }

    private void SelectMetaProfileCore(string name)
    {
        SaveMetaProfile();
        if (!_metaProfiles.Select(name))
        {
            _metaNotice = $"Meta profile '{name}' is unavailable.";
            return;
        }
        LoadMetaProfile();
        _metaNotice = $"Loaded Meta profile {_metaProfiles.Selected}.";
    }

    private void CreateMetaProfileCore(bool copyCurrent)
    {
        if (!_metaProfiles.Create(
            _metaProfileNameDraft,
            copyCurrent,
            _metaProfile,
            out string notice))
        {
            _metaNotice = notice;
            return;
        }
        _metaProfileNameDraft = string.Empty;
        LoadMetaProfile();
        _metaNotice = notice;
    }

    private void ClearMetaProfileCore()
    {
        _metaProfile = _metaProfiles.ClearCurrent();
        _meta.ReplaceProfile(_metaProfile);
        _selectedMetaRule = 0;
        RefreshMetaEditor();
        _metaNotice = $"Cleared Meta profile {_metaProfiles.Selected}.";
    }

    private void DeleteMetaProfileCore()
    {
        if (!_metaProfiles.Delete(out string notice))
        {
            _metaNotice = notice;
            return;
        }
        LoadMetaProfile();
        _metaNotice = notice;
    }

    private void LoadMetaProfile()
    {
        _metaProfile = _metaProfiles.LoadCurrent();
        _meta.ReplaceProfile(_metaProfile);
        if (_initialized)
            ApplyPersistedOptionOverrides();
        _selectedMetaRule = 0;
        RefreshMetaEditor();
    }

    private bool SaveMetaProfile()
    {
        if (_metaProfiles.SaveCurrent(_metaProfile))
            return true;
        if (_metaProfiles.SaveNotice is { } notice)
            _metaNotice = notice;
        return false;
    }

    private static string DescribeMetaCondition(MetaCondition condition) =>
        condition.Kind switch
        {
            MetaConditionKind.Expression => $"Expression: {condition.Text}",
            MetaConditionKind.ChatMessage or MetaConditionKind.ChatMessageCapture =>
                $"{condition.Kind}: {condition.Text}",
            MetaConditionKind.Always or MetaConditionKind.Never =>
                condition.Kind.ToString(),
            _ => $"{condition.Kind} {condition.Number:0.###}",
        };

    private static string DescribeMetaAction(MetaAction action) => action.Kind switch
    {
        MetaActionKind.SetMetaState or MetaActionKind.CallMetaState
            or MetaActionKind.ChatCommand or MetaActionKind.ExpressionAction
            or MetaActionKind.ChatExpression => $"{action.Kind}: {action.Text}",
        _ => action.Kind.ToString(),
    };

    private double DistanceFromAnyRoutePoint()
    {
        PluginNavigationSnapshot player = _host.Automation.Navigation.Snapshot;
        if (!player.IsAvailable)
            return double.PositiveInfinity;
        double nearest = double.PositiveInfinity;
        foreach (RouteWaypoint waypoint in _navigationSettings.Waypoints)
        {
            if (waypoint.Position.CellId == 0u)
                continue;
            nearest = Math.Min(
                nearest,
                player.Position.HorizontalDistanceMeters(waypoint.Position));
        }
        return nearest;
    }

    private void LoadEmbeddedNavigationRoute(NavigationSettings? route)
    {
        _navigation.Reset();
        if (route is null)
        {
            _routeNotice = "Embedded route rejected: unresolved Nav tag.";
            _host.Log.Warn("MossTank Meta embedded route rejected: unresolved Nav tag.");
            return;
        }
        VtankNavRouteSerializer.Apply(route, _navigationSettings);
        _routeProfiles.SaveCurrent(_navigationSettings);
        _selectedRouteWaypoint = 0;
        RefreshRouteEditor();
        _routeNotice = $"Loaded embedded route ({_navigationSettings.Waypoints.Count} points).";
    }

    private int CountMonstersByPriority(int priority, double distance)
    {
        int count = 0;
        foreach (PluginCombatTarget target in _host.Automation.Combat
            .CaptureHostileTargets(checked((float)distance)))
        {
            if (_combatSettings.ResolveRule(target).Priority == priority)
                count++;
        }
        return count;
    }

    internal bool GetMetaOptionForTest(string name) =>
        GetMetaOption(name).IsTruthy;

    private ExpressionValue GetMetaOption(string name)
    {
        string key = name.Trim();
        return key.ToLowerInvariant() switch
        {
            "enablebuffing" => ExpressionValue.Boolean(_buffSettings.Enabled),
            "enablecombat" => ExpressionValue.Boolean(_combatSettings.Enabled),
            "enablenav" or "enablenavigation" or "enableautonavigator" =>
                ExpressionValue.Boolean(_navigationSettings.Enabled),
            "enablelooting" => ExpressionValue.Boolean(_inventorySettings.Loot.Enabled),
            "enablemeta" => ExpressionValue.Boolean(_meta.Enabled),
            "spelldiffexcessthreshold-hunt" => ExpressionValue.Number(
                _combatSettings.HuntSkillExcessOverDifficulty),
            "spelldiffexcessthreshold-buff" => ExpressionValue.Number(
                _buffSettings.SkillExcessOverDifficulty),
            "arrowheadfletchdiffexcessthreshold" => ExpressionValue.Number(
                _inventorySettings.ArrowheadFletchDifficultyExcess),
            "dohelp" => ExpressionValue.Boolean(_vitalSettings.HelpOthers),
            "monsterrange" => ExpressionValue.Number(_combatSettings.MaximumRange),
            "attackdistance" => ExpressionValue.Number(
                _combatSettings.MaximumRange / 240d),
            "attackminimumdistance" => ExpressionValue.Number(
                _combatSettings.MinimumRange / 240d),
            "approachdistance" => ExpressionValue.Number(
                _combatSettings.ApproachDistance / 240d),
            "ringdistance" => ExpressionValue.Number(_combatSettings.RingDistance / 240d),
            "arcrange" => ExpressionValue.Number(_combatSettings.ArcRange / 240d),
            "targetselectanglerange" => ExpressionValue.Number(
                _combatSettings.TargetSelectAngleRange / 240d),
            "corpseapproachrange-max" => ExpressionValue.Number(
                _inventorySettings.Loot.CorpseApproachRange / 240d),
            "corpseapproachrange-min" => ExpressionValue.Number(
                _inventorySettings.Loot.CorpseMinimumApproachRange / 240d),
            "navclosestoprange" => ExpressionValue.Number(
                _navigationSettings.MinimumDistanceMeters / 240d),
            "navfarstoprange" => ExpressionValue.Number(
                _navigationSettings.MaximumDistanceMeters / 240d),
            "useportaldistance" => ExpressionValue.Number(
                _navigationSettings.PortalUseDistanceMeters / 240d),
            "helperdistancehitp" => ExpressionValue.Number(
                _vitalSettings.HelperHealthDistance / 240d),
            "helperdistancestam" => ExpressionValue.Number(
                _vitalSettings.HelperStaminaDistance / 240d),
            "helperdistancemana" => ExpressionValue.Number(
                _vitalSettings.HelperManaDistance / 240d),
            "minimumringtargets" => ExpressionValue.Number(
                _combatSettings.MinimumRingTargets),
            "defaultmeleeattackheight" => ExpressionValue.Number(
                (int)_combatSettings.AttackHeight),
            "defaultmeleeattackpower" or "attackpower" =>
                ExpressionValue.Number(_combatSettings.AttackPower),
            "targetlock" => ExpressionValue.Boolean(_combatSettings.TargetLock),
            "idlepeacemode" => ExpressionValue.Boolean(
                _combatSettings.IdlePeaceMode),
            "stopmacroondeath" => ExpressionValue.Boolean(
                _combatSettings.StopMacroOnDeath),
            "jumpoutwandcasting" => ExpressionValue.Boolean(
                _combatSettings.JumpOutWandCasting),
            "dojiggle" => ExpressionValue.Boolean(_combatSettings.DoJiggle),
            "randomhelperbuffs" => ExpressionValue.Boolean(
                _buffSettings.RandomHelperBuffs),
            "randomhelperintervalseconds" => ExpressionValue.Number(
                _buffSettings.RandomHelperIntervalSeconds),
            "idlebufftopoff" => ExpressionValue.Boolean(
                _buffSettings.IdleBuffTopoff),
            "idlebufftopofftimeseconds" => ExpressionValue.Number(
                _buffSettings.IdleBuffTopoffSeconds),
            "buffprofile-prots" => ExpressionValue.String(
                _buffSettings.ProtectionElements),
            "buffprofile-banes" => ExpressionValue.String(
                _buffSettings.BaneElements),
            "buffprofile_prots" => ExpressionValue.Number(
                _buffSettings.ProtectionProfileMode),
            "buffprofile_banes" => ExpressionValue.Number(
                _buffSettings.BaneProfileMode),
            "targetselectmethod" => ExpressionValue.Number(
                (int)_combatSettings.SelectionMethod + 1),
            "autoattackpower" => ExpressionValue.Boolean(
                _combatSettings.AutoAttackPower),
            "userecklessness" => ExpressionValue.Boolean(
                _combatSettings.UseRecklessness),
            "debuffeachfirst" => ExpressionValue.Number(
                (int)_combatSettings.DebuffEachFirst),
            "debuffselectionmethod" => ExpressionValue.Number(
                (int)_combatSettings.DebuffSelectionMethod),
            "debuffprecastseconds" => ExpressionValue.Number(
                _combatSettings.DebuffPrecastSeconds),
            "switchwandstodebuff" => ExpressionValue.Boolean(
                _combatSettings.SwitchWandsToDebuff),
            "usearcs" => ExpressionValue.Number((int)_combatSettings.UseArcs),
            "deleteghostmonsters" => ExpressionValue.Boolean(
                _combatSettings.DeleteGhostMonsters),
            "ghostmonsterspellattemptcount" => ExpressionValue.Number(
                _combatSettings.GhostMonsterSpellAttemptCount),
            "blacklistmonsterattemptcount" => ExpressionValue.Number(
                _combatSettings.BlacklistMonsterAttemptCount),
            "blacklistmonstertimeoutseconds" => ExpressionValue.Number(
                _combatSettings.BlacklistMonsterTimeoutSeconds),
            "deleteghostmonstersbyhptracker" => ExpressionValue.Boolean(
                _combatSettings.DeleteGhostMonstersByHealthTracker),
            "ghostdeletehptrackerseconds" => ExpressionValue.Number(
                _combatSettings.GhostDeleteHealthTrackerSeconds),
            "summonpets" => ExpressionValue.Boolean(_combatSettings.SummonPets),
            "petrangemode" => ExpressionValue.Number((int)_combatSettings.PetRangeMode),
            "petcustomrange" => ExpressionValue.Number(
                _combatSettings.PetCustomRange / 240d),
            "petmonsterdensity" => ExpressionValue.Number(
                _combatSettings.PetMonsterDensity),
            "petrefillcount-idle" => ExpressionValue.Number(
                _combatSettings.PetRefillCountIdle),
            "petrefillcount-normal" => ExpressionValue.Number(
                _combatSettings.PetRefillCountNormal),
            "openapproachdoors" or "opendoors" =>
                ExpressionValue.Boolean(_navigationSettings.OpenDoors),
            "dooridrange" => ExpressionValue.Number(
                _navigationSettings.DoorIdentifyRangeMeters / 240d),
            "dooropenrange" => ExpressionValue.Number(
                _navigationSettings.DoorOpenRangeMeters / 240d),
            "doorlockpickdiffexcessthreshold" => ExpressionValue.Number(
                _navigationSettings.DoorLockpickExcessThreshold),
            "navpriorityboost" => ExpressionValue.Boolean(
                _navigationSettings.Priority),
            "followaroundcorners" => ExpressionValue.Boolean(
                _navigationSettings.FollowAroundCorners),
            "autofellowmanagement" => ExpressionValue.Boolean(
                _combatSettings.AutoFellowManagement),
            "enablestack" or "enableautostack" =>
                ExpressionValue.Boolean(_inventorySettings.AutoStack),
            "autostack" => ExpressionValue.Boolean(_inventorySettings.AutoStack),
            "enablecram" or "enableautocram" =>
                ExpressionValue.Boolean(_inventorySettings.AutoCram),
            "autocram" => ExpressionValue.Boolean(_inventorySettings.AutoCram),
            "autocraftitems" => ExpressionValue.Boolean(_inventorySettings.AutoCraftItems),
            "splitpeas" => ExpressionValue.Boolean(_inventorySettings.SplitPeas),
            "spellcompmin-critical" => ExpressionValue.Number(
                _inventorySettings.CriticalComponentMinimum),
            "spellcompmin-normal" => ExpressionValue.Number(
                _inventorySettings.NormalComponentMinimum),
            "spellcompmin-idle" => ExpressionValue.Number(
                _inventorySettings.IdleComponentMinimum),
            "idlecraftcount_healthkits" or "idlecraftcount-healthkits" =>
                ExpressionValue.Number(
                _inventorySettings.IdleHealthKitCount),
            "idlecraftcount_stamkits" or "idlecraftcount-stamkits" =>
                ExpressionValue.Number(
                _inventorySettings.IdleStaminaKitCount),
            "idlecraftcount_manakits" or "idlecraftcount-manakits" =>
                ExpressionValue.Number(
                _inventorySettings.IdleManaKitCount),
            "idlecraftcount_healthfood" or "idlecraftcount-healthfood" =>
                ExpressionValue.Number(
                _inventorySettings.IdleHealthFoodCount),
            "idlecraftcount_stamfood" or "idlecraftcount-stamfood" =>
                ExpressionValue.Number(
                _inventorySettings.IdleStaminaFoodCount),
            "idlecraftcount_manafood" or "idlecraftcount-manafood" =>
                ExpressionValue.Number(
                _inventorySettings.IdleManaFoodCount),
            "refillwornmana" => ExpressionValue.Boolean(
                _inventorySettings.RefillWornMana),
            "manachargeswhenoff" => ExpressionValue.Boolean(
                _inventorySettings.ManaChargesWhenOff),
            "refillwornmana-item-manapercent" => ExpressionValue.Number(
                _inventorySettings.RefillWornManaPercent),
            "readunknownscrolls" => ExpressionValue.Boolean(
                _inventorySettings.Loot.ReadUnknownScrolls),
            "lootallcorpses" => ExpressionValue.Boolean(
                _inventorySettings.Loot.LootAllCorpses),
            "lootfellowcorpses" => ExpressionValue.Boolean(
                _inventorySettings.Loot.LootFellowCorpses),
            "lootpriorityboost" => ExpressionValue.Boolean(
                _inventorySettings.Loot.PriorityBoost),
            "lootonlyrarecorpses" => ExpressionValue.Boolean(
                _inventorySettings.Loot.LootOnlyRareCorpses),
            "combinesalvage" => ExpressionValue.Boolean(
                _inventorySettings.Loot.CombineSalvage),
            "manastonelootcount" => ExpressionValue.Number(
                _inventorySettings.Loot.ManaStoneLootCount),
            "manatankminimummana" => ExpressionValue.Number(
                _inventorySettings.Loot.ManaTankMinimumMana),
            "corpsecachetimeoutminutes" => ExpressionValue.Number(
                _inventorySettings.Loot.CorpseCacheTimeoutMinutes),
            "corpseitemappearancetimeoutseconds" => ExpressionValue.Number(
                _inventorySettings.Loot.CorpseItemAppearanceTimeoutSeconds),
            "corpseitemidtimeoutseconds" => ExpressionValue.Number(
                _inventorySettings.Loot.CorpseItemIdentifyTimeoutSeconds),
            "corpseopentimeoutseconds" => ExpressionValue.Number(
                _inventorySettings.Loot.CorpseOpenTimeoutSeconds),
            "blacklistcorpseopenattemptcount" => ExpressionValue.Number(
                _inventorySettings.Loot.BlacklistCorpseOpenAttemptCount),
            "blacklistcorpseopentimeoutseconds" => ExpressionValue.Number(
                _inventorySettings.Loot.BlacklistCorpseOpenTimeoutSeconds),
            "corpselootitemmaxattempts" => ExpressionValue.Number(
                _inventorySettings.Loot.CorpseLootItemMaxAttempts),
            "minimumhealkitsuccesschance" => ExpressionValue.Number(
                _vitalSettings.MinimumHealKitSuccessChance),
            "usehealersheart" => ExpressionValue.Boolean(
                _vitalSettings.UseHealersHeart),
            "rechargeboosttimeseconds" => ExpressionValue.Number(
                _vitalSettings.RechargeBoostTimeSeconds),
            "rechargeboostamount" => ExpressionValue.Number(
                _vitalSettings.RechargeBoostAmount),
            "clearlevelboostflagoncast" => ExpressionValue.Boolean(
                _vitalSettings.ClearLevelBoostFlagOnCast),
            "whoyougonnacall" => ExpressionValue.Boolean(
                _combatSettings.WhoYouGonnaCall),
            "castdispelself" => ExpressionValue.Boolean(
                _vitalSettings.CastDispelSelf),
            "usedispelitems" => ExpressionValue.Boolean(
                _vitalSettings.UseDispelItems),
            "usedispeldrum" => ExpressionValue.Boolean(
                _vitalSettings.UseDispelDrum),
            "usekitsinmagicmode" => ExpressionValue.Boolean(
                _vitalSettings.UseKitsInMagicMode),
            "gotopeacemodetousekits" => ExpressionValue.Boolean(
                _vitalSettings.GoToPeaceModeToUseKits),
            "staminatohealthmultiplier" => ExpressionValue.Number(
                _vitalSettings.StaminaToHealthMultiplier),
            "manatohealthmultiplier" => ExpressionValue.Number(
                _vitalSettings.ManaToHealthMultiplier),
            "recharge-norm-hitp" => ExpressionValue.Number(
                _vitalSettings.NormalHealth * 100d),
            "recharge-norm-stam" => ExpressionValue.Number(
                _vitalSettings.NormalStamina * 100d),
            "recharge-norm-mana" => ExpressionValue.Number(
                _vitalSettings.NormalMana * 100d),
            "recharge-notarg-hitp" => ExpressionValue.Number(
                _vitalSettings.NoTargetHealth * 100d),
            "recharge-notarg-stam" => ExpressionValue.Number(
                _vitalSettings.NoTargetStamina * 100d),
            "recharge-notarg-mana" => ExpressionValue.Number(
                _vitalSettings.NoTargetMana * 100d),
            "recharge-helper-hitp" => ExpressionValue.Number(
                _vitalSettings.HelperHealth * 100d),
            "recharge-helper-stam" => ExpressionValue.Number(
                _vitalSettings.HelperStamina * 100d),
            "recharge-helper-mana" => ExpressionValue.Number(
                _vitalSettings.HelperMana * 100d),
            "rebufftimeremainingseconds" => ExpressionValue.Number(
                _buffSettings.RebuffWhenUnderSeconds),
            "buffcastrecast_seconds" => ExpressionValue.Number(
                _buffSettings.BuffCastRecastSeconds),
            "buffcastrecastreset_seconds" => ExpressionValue.Number(
                _buffSettings.BuffCastRecastResetSeconds),
            "blacklistedspellcomps" => ExpressionValue.String(
                _buffSettings.BlacklistedSpellComponents),
            "droptopeacemoderetrycount" => ExpressionValue.Number(
                _vitalSettings.DropToPeaceModeRetryCount),
            "fastcastbuffs" => ExpressionValue.Boolean(
                _buffSettings.FastCastBuffs),
            "usebreakableturnto" => ExpressionValue.Boolean(
                _combatSettings.UseBreakableTurnTo),
            "useprojectileawareness" => ExpressionValue.Boolean(
                _combatSettings.UseProjectileAwareness),
            "collisionprojectileradius" => ExpressionValue.Number(
                _combatSettings.CollisionProjectileRadius),
            "collisionstepdistance" => ExpressionValue.Number(
                _combatSettings.CollisionStepDistance),
            "showcollisiondebug" => ExpressionValue.Boolean(
                _combatSettings.ShowCollisionDebug),
            "maximumcollisioncheckspertick" => ExpressionValue.Number(
                _combatSettings.MaximumCollisionChecksPerTick),
            "usespecialammo" => ExpressionValue.Number(
                _combatSettings.UseSpecialAmmo),
            "spellrangefudge" => ExpressionValue.Number(
                _combatSettings.SpellRangeFudge),
            "buffwithuntrained-item" => ExpressionValue.Number(
                _buffSettings.BuffWithUntrainedItemSkill),
            "buffwithuntrained-creature" => ExpressionValue.Number(
                _buffSettings.BuffWithUntrainedCreatureSkill),
            "buffwithuntrained-life" => ExpressionValue.Number(
                _buffSettings.BuffWithUntrainedLifeSkill),
            "allowdebufffallback" => ExpressionValue.Boolean(
                _combatSettings.AllowDebuffFallback),
            "rechargehandlerset" => ExpressionValue.String(
                _vitalSettings.RechargeHandlerSet),
            _ => _combatSettings.DynamicSettings.TryGetValue(key, out MonsterValue value)
                ? ToExpressionValue(value)
                : ToExpressionValue(VtankOptionCatalog.Default(key)),
        };
    }

    private static ExpressionValue ToExpressionValue(MonsterValue value) =>
        value.Kind switch
        {
            MonsterValueKind.Number => ExpressionValue.Number(value.Number),
            MonsterValueKind.Boolean => ExpressionValue.Boolean(value.Boolean),
            _ => ExpressionValue.String(value.Text),
        };

    private double GetDynamicNumber(string name, double fallback) =>
        _combatSettings.DynamicSettings.TryGetValue(name, out MonsterValue value)
            && value.Kind == MonsterValueKind.Number
                ? value.Number
                : fallback;

    private void RegisterVtankExpressionFunctions()
    {
        ExpressionFunctionRegistry functions = _expressions.Registry;
        functions.Register("vtsetmetastate", 1, 1, (_, args) =>
        {
            _meta.Transition(args[0].AsString("vtsetmetastate"));
            _combatSettings.MetaState = _meta.CurrentState;
            return ExpressionValue.One;
        }, "vtsetmetastate[state]");
        functions.Register("vtgetmetastate", 0, 0, (_, _) =>
            ExpressionValue.String(_meta.CurrentState), "vtgetmetastate[]");
        functions.Register("vtgetmeta", 0, 0, (_, _) =>
            ExpressionValue.String(_metaProfiles.Selected), "vtgetmeta[]");
        functions.Register("vtsetsetting", 2, 2, (_, args) =>
        {
            string name = args[0].AsString("vtsetsetting");
            ExpressionValue value = args[1];
            if (value.Kind == ExpressionValueKind.String
                && double.TryParse(
                    value.AsString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double number))
            {
                value = ExpressionValue.Number(number);
            }
            return ExpressionValue.Boolean(SetMetaOption(name, value));
        }, "vtsetsetting[setting,value]");
        functions.Register("vtgetsetting", 1, 1, (_, args) =>
            ExpressionValue.String(GetMetaOption(
                args[0].AsString("vtgetsetting")).ToDisplayString()),
            "vtgetsetting[setting]");
        functions.Register("uboptset", 2, 2, (_, args) =>
            ExpressionValue.Boolean(SetMetaOption(
                args[0].AsString("uboptset"),
                args[1])),
            "uboptset[setting,value]");
        functions.Register("uboptget", 1, 1, (_, args) =>
            GetMetaOption(args[0].AsString("uboptget")),
            "uboptget[setting]");
        functions.Register("actiontrygiveprofile", 2, 2, (_, args) =>
            ExpressionValue.Boolean(_profileGive.TryStart(
                args[0].AsString("actiontrygiveprofile"),
                args[1].AsString("actiontrygiveprofile"))),
            "actiontrygiveprofile[lootprofile,target]");
        functions.Register("vtmacroenabled", 0, 0, (_, _) =>
            ExpressionValue.Boolean(_combat.Enabled),
            "vtmacroenabled[]");
    }

    /// <summary>
    /// The panel's ONE option-write path — every toolbar toggle, the Options
    /// tab and the meta engine's <c>setopt</c> all land here. Internal rather
    /// than private so tests can set an option the way the product does
    /// instead of reaching into settings objects.
    /// </summary>
    internal bool SetMetaOption(string name, ExpressionValue value)
    {
        string canonical = VtankOptionCatalog.IsKnown(name)
            ? VtankOptionCatalog.Canonical(name)
            : name.Trim();
        string key = canonical.ToLowerInvariant();
        switch (key)
        {
            case "enablebuffing":
                _buffSettings.Enabled = value.IsTruthy;
                break;
            case "enablecombat":
                _combatSettings.Enabled = value.IsTruthy;
                break;
            case "enablenav":
            case "enablenavigation":
            case "enableautonavigator":
                _navigationSettings.Enabled = value.IsTruthy;
                break;
            case "enablelooting":
                _inventorySettings.Loot.Enabled = value.IsTruthy;
                break;
            case "enablemeta":
                _meta.SetEnabled(value.IsTruthy);
                break;
            case "spelldiffexcessthreshold-hunt":
                _combatSettings.HuntSkillExcessOverDifficulty = Math.Clamp(
                    value.AsInt32("SpellDiffExcessThreshold-Hunt"), -100, 500);
                break;
            case "arrowheadfletchdiffexcessthreshold":
                _inventorySettings.ArrowheadFletchDifficultyExcess = Math.Clamp(
                    value.AsInt32("ArrowheadFletchDiffExcessThreshold"),
                    -100,
                    500);
                break;
            case "dohelp":
                _vitalSettings.HelpOthers = value.IsTruthy;
                break;
            case "monsterrange":
                _combatSettings.MaximumRange = Math.Clamp(
                    checked((float)value.AsNumber("MonsterRange")), 1f, 100f);
                break;
            case "attackdistance":
                _combatSettings.MaximumRange = Math.Clamp(
                    checked((float)(value.AsNumber("AttackDistance") * 240d)),
                    1f,
                    100f);
                break;
            case "attackminimumdistance":
                _combatSettings.MinimumRange = Math.Clamp(
                    checked((float)(value.AsNumber("AttackMinimumDistance") * 240d)),
                    0f,
                    100f);
                break;
            case "approachdistance":
                _combatSettings.ApproachDistance = Math.Clamp(
                    checked((float)(value.AsNumber("ApproachDistance") * 240d)),
                    0f,
                    100f);
                break;
            case "ringdistance":
                _combatSettings.RingDistance = Math.Clamp(
                    checked((float)(value.AsNumber("RingDistance") * 240d)),
                    1f,
                    100f);
                break;
            case "arcrange":
                _combatSettings.ArcRange = Math.Clamp(
                    checked((float)(value.AsNumber("ArcRange") * 240d)),
                    1f,
                    100f);
                break;
            case "targetselectanglerange":
                _combatSettings.TargetSelectAngleRange = Math.Clamp(
                    checked((float)(value.AsNumber("TargetSelectAngleRange") * 240d)),
                    1f,
                    100f);
                break;
            case "corpseapproachrange-max":
                _inventorySettings.Loot.CorpseApproachRange = Math.Clamp(
                    checked((float)(value.AsNumber("CorpseApproachRange-Max") * 240d)),
                    1f,
                    100f);
                break;
            case "corpseapproachrange-min":
                _inventorySettings.Loot.CorpseMinimumApproachRange = Math.Clamp(
                    checked((float)(value.AsNumber("CorpseApproachRange-Min") * 240d)),
                    0f,
                    100f);
                break;
            case "navclosestoprange":
                _navigationSettings.MinimumDistanceMeters = Math.Clamp(
                    value.AsNumber("NavCloseStopRange") * 240d,
                    0.5d,
                    50d);
                break;
            case "navfarstoprange":
                _navigationSettings.MaximumDistanceMeters = Math.Clamp(
                    value.AsNumber("NavFarStopRange") * 240d,
                    _navigationSettings.MinimumDistanceMeters,
                    240_000_000d);
                break;
            case "useportaldistance":
                _navigationSettings.PortalUseDistanceMeters = Math.Clamp(
                    value.AsNumber("UsePortalDistance") * 240d,
                    0.5d,
                    50d);
                break;
            case "helperdistancehitp":
                _vitalSettings.HelperHealthDistance = Math.Clamp(
                    checked((float)(value.AsNumber("HelperDistanceHitP") * 240d)),
                    1f,
                    100f);
                break;
            case "helperdistancestam":
                _vitalSettings.HelperStaminaDistance = Math.Clamp(
                    checked((float)(value.AsNumber("HelperDistanceStam") * 240d)),
                    1f,
                    100f);
                break;
            case "helperdistancemana":
                _vitalSettings.HelperManaDistance = Math.Clamp(
                    checked((float)(value.AsNumber("HelperDistanceMana") * 240d)),
                    1f,
                    100f);
                break;
            case "minimumringtargets":
                _combatSettings.MinimumRingTargets = Math.Clamp(
                    value.AsInt32("MinimumRingTargets"), 1, 25);
                break;
            case "defaultmeleeattackheight":
                _combatSettings.AttackHeight = (PluginAttackHeight)Math.Clamp(
                    value.AsInt32("DefaultMeleeAttackHeight"), 1, 3);
                break;
            case "defaultmeleeattackpower":
            case "attackpower":
                _combatSettings.AttackPower = Math.Clamp(
                    checked((float)value.AsNumber("AttackPower")), 0f, 1f);
                break;
            case "targetlock":
                _combatSettings.TargetLock = value.IsTruthy;
                break;
            case "idlepeacemode":
                _combatSettings.IdlePeaceMode = value.IsTruthy;
                break;
            case "stopmacroondeath":
                _combatSettings.StopMacroOnDeath = value.IsTruthy;
                break;
            case "jumpoutwandcasting":
                _combatSettings.JumpOutWandCasting = value.IsTruthy;
                break;
            case "dojiggle":
                _combatSettings.DoJiggle = value.IsTruthy;
                break;
            case "randomhelperbuffs":
                _buffSettings.RandomHelperBuffs = value.IsTruthy;
                break;
            case "randomhelperintervalseconds":
                _buffSettings.RandomHelperIntervalSeconds = Math.Clamp(
                    value.AsNumber("RandomHelperIntervalSeconds"), 0.25d, 3600d);
                break;
            case "idlebufftopoff":
                _buffSettings.IdleBuffTopoff = value.IsTruthy;
                break;
            case "idlebufftopofftimeseconds":
                _buffSettings.IdleBuffTopoffSeconds = Math.Clamp(
                    value.AsNumber("IdleBuffTopoffTimeSeconds"),
                    30d,
                    7200d);
                break;
            case "buffprofile-prots":
                _buffSettings.ProtectionElements = NormalizeElementProfile(
                    value.ToDisplayString());
                break;
            case "buffprofile-banes":
                _buffSettings.BaneElements = NormalizeElementProfile(
                    value.ToDisplayString());
                break;
            case "buffprofile_prots":
                _buffSettings.ProtectionProfileMode = Math.Clamp(
                    value.AsInt32("BuffProfile_Prots"), 1, 8);
                break;
            case "buffprofile_banes":
                _buffSettings.BaneProfileMode = Math.Clamp(
                    value.AsInt32("BuffProfile_Banes"), 1, 8);
                break;
            case "targetselectmethod":
                _combatSettings.SelectionMethod = (TargetSelectionMethod)Math.Clamp(
                    value.AsInt32("TargetSelectMethod") - 1, 0, 2);
                break;
            case "autoattackpower":
                _combatSettings.AutoAttackPower = value.IsTruthy;
                break;
            case "userecklessness":
                _combatSettings.UseRecklessness = value.IsTruthy;
                break;
            case "debuffeachfirst":
                _combatSettings.DebuffEachFirst = (DebuffEachFirst)Math.Clamp(
                    value.AsInt32("DebuffEachFirst"), 1, 3);
                break;
            case "debuffselectionmethod":
                _combatSettings.DebuffSelectionMethod =
                    (DebuffSelectionMethod)Math.Clamp(
                        value.AsInt32("DebuffSelectionMethod"), 1, 2);
                break;
            case "debuffprecastseconds":
                _combatSettings.DebuffPrecastSeconds = Math.Clamp(
                    value.AsNumber("DebuffPrecastSeconds"), 0d, 60d);
                break;
            case "switchwandstodebuff":
                _combatSettings.SwitchWandsToDebuff = value.IsTruthy;
                break;
            case "usearcs":
                _combatSettings.UseArcs = (UseArcsMode)Math.Clamp(
                    value.AsInt32("UseArcs"), 1, 3);
                break;
            case "deleteghostmonsters":
                _combatSettings.DeleteGhostMonsters = value.IsTruthy;
                break;
            case "ghostmonsterspellattemptcount":
                _combatSettings.GhostMonsterSpellAttemptCount = Math.Clamp(
                    value.AsInt32("GhostMonsterSpellAttemptCount"), 1, 1000);
                break;
            case "blacklistmonsterattemptcount":
                _combatSettings.BlacklistMonsterAttemptCount = Math.Clamp(
                    value.AsInt32("BlacklistMonsterAttemptCount"), 1, 20);
                break;
            case "blacklistmonstertimeoutseconds":
                _combatSettings.BlacklistMonsterTimeoutSeconds = Math.Clamp(
                    value.AsNumber("BlacklistMonsterTimeoutSeconds"), 1d, 3600d);
                break;
            case "deleteghostmonstersbyhptracker":
                _combatSettings.DeleteGhostMonstersByHealthTracker = value.IsTruthy;
                break;
            case "ghostdeletehptrackerseconds":
                _combatSettings.GhostDeleteHealthTrackerSeconds = Math.Clamp(
                    value.AsNumber("GhostDeleteHPTrackerSeconds"), 1d, 300d);
                break;
            case "summonpets":
                _combatSettings.SummonPets = value.IsTruthy;
                break;
            case "petrangemode":
                _combatSettings.PetRangeMode = (PetRangeMode)Math.Clamp(
                    value.AsInt32("PetRangeMode"), 0, 1);
                break;
            case "petcustomrange":
                _combatSettings.PetCustomRange = Math.Clamp(
                    checked((float)(value.AsNumber("PetCustomRange") * 240d)),
                    1f,
                    100f);
                break;
            case "petmonsterdensity":
                _combatSettings.PetMonsterDensity = Math.Clamp(
                    value.AsInt32("PetMonsterDensity"), 1, 25);
                break;
            case "petrefillcount-idle":
                _combatSettings.PetRefillCountIdle = Math.Clamp(
                    value.AsInt32("PetRefillCount-Idle"), 0, 3);
                break;
            case "petrefillcount-normal":
                _combatSettings.PetRefillCountNormal = Math.Clamp(
                    value.AsInt32("PetRefillCount-Normal"), 0, 3);
                break;
            case "openapproachdoors":
            case "opendoors":
                _navigationSettings.OpenDoors = value.IsTruthy;
                break;
            case "dooridrange":
                _navigationSettings.DoorIdentifyRangeMeters = Math.Clamp(
                    value.AsNumber("DoorIDRange") * 240d, 1d, 100d);
                break;
            case "dooropenrange":
                _navigationSettings.DoorOpenRangeMeters = Math.Clamp(
                    value.AsNumber("DoorOpenRange") * 240d,
                    0.5d,
                    _navigationSettings.DoorIdentifyRangeMeters);
                break;
            case "doorlockpickdiffexcessthreshold":
                _navigationSettings.DoorLockpickExcessThreshold = Math.Clamp(
                    value.AsInt32("DoorLockpickDiffExcessThreshold"), -500, 500);
                break;
            case "navpriorityboost":
                _navigationSettings.Priority = value.IsTruthy;
                break;
            case "followaroundcorners":
                _navigationSettings.FollowAroundCorners = value.IsTruthy;
                break;
            case "autofellowmanagement":
                _combatSettings.AutoFellowManagement = value.IsTruthy;
                break;
            case "enablestack":
            case "enableautostack":
            case "autostack":
                _inventorySettings.AutoStack = value.IsTruthy;
                break;
            case "enablecram":
            case "enableautocram":
            case "autocram":
                _inventorySettings.AutoCram = value.IsTruthy;
                break;
            case "autocraftitems":
                _inventorySettings.AutoCraftItems = value.IsTruthy;
                break;
            case "splitpeas":
                _inventorySettings.SplitPeas = value.IsTruthy;
                break;
            case "spellcompmin-critical":
                _inventorySettings.CriticalComponentMinimum = Math.Clamp(
                    value.AsInt32("SpellCompMin-Critical"), 0, 1000);
                break;
            case "spellcompmin-normal":
                _inventorySettings.NormalComponentMinimum = Math.Clamp(
                    value.AsInt32("SpellCompMin-Normal"), 0, 1000);
                break;
            case "spellcompmin-idle":
                _inventorySettings.IdleComponentMinimum = Math.Clamp(
                    value.AsInt32("SpellCompMin-Idle"), 0, 1000);
                break;
            case "idlecraftcount_healthkits":
            case "idlecraftcount-healthkits":
                _inventorySettings.IdleHealthKitCount = Math.Clamp(
                    value.AsInt32("IdleCraftCount_HealthKits"), 0, 1000);
                break;
            case "idlecraftcount_stamkits":
            case "idlecraftcount-stamkits":
                _inventorySettings.IdleStaminaKitCount = Math.Clamp(
                    value.AsInt32("IdleCraftCount_StamKits"), 0, 1000);
                break;
            case "idlecraftcount_manakits":
            case "idlecraftcount-manakits":
                _inventorySettings.IdleManaKitCount = Math.Clamp(
                    value.AsInt32("IdleCraftCount_ManaKits"), 0, 1000);
                break;
            case "idlecraftcount_healthfood":
            case "idlecraftcount-healthfood":
                _inventorySettings.IdleHealthFoodCount = Math.Clamp(
                    value.AsInt32("IdleCraftCount_HealthFood"), 0, 1000);
                break;
            case "idlecraftcount_stamfood":
            case "idlecraftcount-stamfood":
                _inventorySettings.IdleStaminaFoodCount = Math.Clamp(
                    value.AsInt32("IdleCraftCount_StamFood"), 0, 1000);
                break;
            case "idlecraftcount_manafood":
            case "idlecraftcount-manafood":
                _inventorySettings.IdleManaFoodCount = Math.Clamp(
                    value.AsInt32("IdleCraftCount_ManaFood"), 0, 1000);
                break;
            case "refillwornmana":
                _inventorySettings.RefillWornMana = value.IsTruthy;
                break;
            case "manachargeswhenoff":
                _inventorySettings.ManaChargesWhenOff = value.IsTruthy;
                break;
            case "refillwornmana-item-manapercent":
                _inventorySettings.RefillWornManaPercent = Math.Clamp(
                    value.AsInt32("RefillWornMana-Item-ManaPercent"), 0, 100);
                break;
            case "readunknownscrolls":
                _inventorySettings.Loot.ReadUnknownScrolls = value.IsTruthy;
                break;
            case "lootallcorpses":
                _inventorySettings.Loot.LootAllCorpses = value.IsTruthy;
                break;
            case "lootfellowcorpses":
                _inventorySettings.Loot.LootFellowCorpses = value.IsTruthy;
                break;
            case "lootpriorityboost":
                _inventorySettings.Loot.PriorityBoost = value.IsTruthy;
                break;
            case "lootonlyrarecorpses":
                _inventorySettings.Loot.LootOnlyRareCorpses = value.IsTruthy;
                break;
            case "combinesalvage":
                _inventorySettings.Loot.CombineSalvage = value.IsTruthy;
                break;
            case "manastonelootcount":
                _inventorySettings.Loot.ManaStoneLootCount = Math.Clamp(
                    value.AsInt32("ManaStoneLootCount"), 0, 1000);
                break;
            case "manatankminimummana":
                _inventorySettings.Loot.ManaTankMinimumMana = Math.Clamp(
                    value.AsInt32("ManaTankMinimumMana"), 1, int.MaxValue);
                break;
            case "corpsecachetimeoutminutes":
                _inventorySettings.Loot.CorpseCacheTimeoutMinutes = Math.Clamp(
                    value.AsNumber("CorpseCacheTimeoutMinutes"), 1d, 1440d);
                break;
            case "corpseitemappearancetimeoutseconds":
                _inventorySettings.Loot.CorpseItemAppearanceTimeoutSeconds =
                    Math.Clamp(
                        value.AsNumber("CorpseItemAppearanceTimeoutSeconds"),
                        0d,
                        300d);
                break;
            case "corpseitemidtimeoutseconds":
                _inventorySettings.Loot.CorpseItemIdentifyTimeoutSeconds =
                    Math.Clamp(
                        value.AsNumber("CorpseItemIDTimeoutSeconds"),
                        1d,
                        600d);
                break;
            case "corpseopentimeoutseconds":
                _inventorySettings.Loot.CorpseOpenTimeoutSeconds = Math.Clamp(
                    value.AsNumber("CorpseOpenTimeoutSeconds"), 0.1d, 60d);
                break;
            case "blacklistcorpseopenattemptcount":
                _inventorySettings.Loot.BlacklistCorpseOpenAttemptCount = Math.Clamp(
                    value.AsInt32("BlacklistCorpseOpenAttemptCount"), 1, 1000);
                break;
            case "blacklistcorpseopentimeoutseconds":
                _inventorySettings.Loot.BlacklistCorpseOpenTimeoutSeconds = Math.Clamp(
                    value.AsNumber("BlacklistCorpseOpenTimeoutSeconds"), 1d, 3600d);
                break;
            case "corpselootitemmaxattempts":
                _inventorySettings.Loot.CorpseLootItemMaxAttempts = Math.Clamp(
                    value.AsInt32("CorpseLootItemMaxAttempts"), 1, 1000);
                break;
            case "minimumhealkitsuccesschance":
                _vitalSettings.MinimumHealKitSuccessChance = Math.Clamp(
                    value.AsInt32("MinimumHealKitSuccessChance"), 0, 100);
                break;
            case "usehealersheart":
                _vitalSettings.UseHealersHeart = value.IsTruthy;
                _vitalRecharge.Reset();
                _vitalHelperRecharge.Reset();
                break;
            case "rechargeboosttimeseconds":
                _vitalSettings.RechargeBoostTimeSeconds = Math.Clamp(
                    value.AsNumber("RechargeBoostTimeSeconds"), 0d, 300d);
                break;
            case "rechargeboostamount":
                _vitalSettings.RechargeBoostAmount = Math.Clamp(
                    value.AsInt32("RechargeBoostAmount"), 0, 1000);
                break;
            case "clearlevelboostflagoncast":
                _vitalSettings.ClearLevelBoostFlagOnCast = value.IsTruthy;
                break;
            case "whoyougonnacall":
                _combatSettings.WhoYouGonnaCall = value.IsTruthy;
                break;
            case "castdispelself":
                _vitalSettings.CastDispelSelf = value.IsTruthy;
                _dispel.Reset();
                break;
            case "usedispelitems":
                _vitalSettings.UseDispelItems = value.IsTruthy;
                _dispel.Reset();
                break;
            case "usedispeldrum":
                _vitalSettings.UseDispelDrum = value.IsTruthy;
                _dispel.Reset();
                break;
            case "usekitsinmagicmode":
                _vitalSettings.UseKitsInMagicMode = value.IsTruthy;
                break;
            case "gotopeacemodetousekits":
                _vitalSettings.GoToPeaceModeToUseKits = value.IsTruthy;
                break;
            case "staminatohealthmultiplier":
                _vitalSettings.StaminaToHealthMultiplier = Math.Clamp(
                    value.AsNumber("StaminaToHealthMultiplier"), 0d, 10d);
                break;
            case "manatohealthmultiplier":
                _vitalSettings.ManaToHealthMultiplier = Math.Clamp(
                    value.AsNumber("ManaToHealthMultiplier"), 0d, 10d);
                break;
            case "recharge-norm-hitp":
                _vitalSettings.NormalHealth = Math.Clamp(
                    value.AsNumber("Recharge-Norm-HitP") / 100d, 0d, 1d);
                break;
            case "recharge-norm-stam":
                _vitalSettings.NormalStamina = Math.Clamp(
                    value.AsNumber("Recharge-Norm-Stam") / 100d, 0d, 1d);
                break;
            case "recharge-norm-mana":
                _vitalSettings.NormalMana = Math.Clamp(
                    value.AsNumber("Recharge-Norm-Mana") / 100d, 0d, 1d);
                break;
            case "recharge-notarg-hitp":
                _vitalSettings.NoTargetHealth = Math.Clamp(
                    value.AsNumber("Recharge-NoTarg-HitP") / 100d, 0d, 1d);
                break;
            case "recharge-notarg-stam":
                _vitalSettings.NoTargetStamina = Math.Clamp(
                    value.AsNumber("Recharge-NoTarg-Stam") / 100d, 0d, 1d);
                break;
            case "recharge-notarg-mana":
                _vitalSettings.NoTargetMana = Math.Clamp(
                    value.AsNumber("Recharge-NoTarg-Mana") / 100d, 0d, 1d);
                break;
            case "recharge-helper-hitp":
                _vitalSettings.HelperHealth = Math.Clamp(
                    value.AsNumber("Recharge-Helper-HitP") / 100d, 0d, 1d);
                break;
            case "recharge-helper-stam":
                _vitalSettings.HelperStamina = Math.Clamp(
                    value.AsNumber("Recharge-Helper-Stam") / 100d, 0d, 1d);
                break;
            case "recharge-helper-mana":
                _vitalSettings.HelperMana = Math.Clamp(
                    value.AsNumber("Recharge-Helper-Mana") / 100d, 0d, 1d);
                break;
            case "spelldiffexcessthreshold-buff":
                _buffSettings.SkillExcessOverDifficulty = Math.Clamp(
                    value.AsInt32("SpellDiffExcessThreshold-Buff"), -100, 100);
                break;
            case "rebufftimeremainingseconds":
                _buffSettings.RebuffWhenUnderSeconds = Math.Clamp(
                    value.AsNumber("RebuffTimeRemainingSeconds"), 0d, 3600d);
                break;
            case "buffcastrecast_seconds":
                _buffSettings.BuffCastRecastSeconds = Math.Clamp(
                    value.AsNumber("BuffCastRecast_Seconds"), 0d, 3600d);
                break;
            case "buffcastrecastreset_seconds":
                _buffSettings.BuffCastRecastResetSeconds = Math.Clamp(
                    value.AsNumber("BuffCastRecastReset_Seconds"), 0d, 3600d);
                break;
            case "blacklistedspellcomps":
                _buffSettings.BlacklistedSpellComponents =
                    value.ToDisplayString();
                _combatSettings.BlacklistedSpellComponents =
                    _buffSettings.BlacklistedSpellComponents;
                break;
            case "droptopeacemoderetrycount":
                _vitalSettings.DropToPeaceModeRetryCount = Math.Clamp(
                    value.AsInt32("DropToPeaceModeRetryCount"), 1, 1000);
                break;
            case "fastcastbuffs":
                _buffSettings.FastCastBuffs = value.IsTruthy;
                break;
            case "usebreakableturnto":
                _combatSettings.UseBreakableTurnTo = value.IsTruthy;
                break;
            case "useprojectileawareness":
                _combatSettings.UseProjectileAwareness = value.IsTruthy;
                break;
            case "collisionprojectileradius":
                _combatSettings.CollisionProjectileRadius = Math.Clamp(
                    checked((float)value.AsNumber("CollisionProjectileRadius")),
                    0f,
                    10f);
                break;
            case "collisionstepdistance":
                _combatSettings.CollisionStepDistance = Math.Clamp(
                    checked((float)value.AsNumber("CollisionStepDistance")),
                    0.01f,
                    10f);
                break;
            case "showcollisiondebug":
                _combatSettings.ShowCollisionDebug = value.IsTruthy;
                break;
            case "maximumcollisioncheckspertick":
                _combatSettings.MaximumCollisionChecksPerTick = Math.Clamp(
                    value.AsInt32("MaximumCollisionChecksPerTick"), 1, 100_000);
                break;
            case "usespecialammo":
                _combatSettings.UseSpecialAmmo = Math.Clamp(
                    value.AsInt32("UseSpecialAmmo"), 0, 3);
                break;
            case "spellrangefudge":
                _combatSettings.SpellRangeFudge = Math.Clamp(
                    checked((float)value.AsNumber("SpellRangeFudge")),
                    0f,
                    75f);
                break;
            case "buffwithuntrained-item":
                _buffSettings.BuffWithUntrainedItemSkill = Math.Clamp(
                    value.AsInt32("BuffWithUntrained-Item"), 0, 275);
                break;
            case "buffwithuntrained-creature":
                _buffSettings.BuffWithUntrainedCreatureSkill = Math.Clamp(
                    value.AsInt32("BuffWithUntrained-Creature"), 0, 275);
                break;
            case "buffwithuntrained-life":
                _buffSettings.BuffWithUntrainedLifeSkill = Math.Clamp(
                    value.AsInt32("BuffWithUntrained-Life"), 0, 275);
                break;
            case "allowdebufffallback":
                _combatSettings.AllowDebuffFallback = value.IsTruthy;
                break;
            case "rechargehandlerset":
                _vitalSettings.RechargeHandlerSet = value.ToDisplayString();
                break;
            default:
                break;
        }
        _combatSettings.DynamicSettings[canonical] = ToMonsterValue(value);
        if (!_applyingProfileOptions)
            SaveProfile();
        return true;
    }

    private static MonsterValue ToMonsterValue(ExpressionValue value) =>
        value.Kind switch
        {
            ExpressionValueKind.Boolean => MonsterValue.FromBoolean(value.IsTruthy),
            ExpressionValueKind.Number => MonsterValue.FromNumber(value.AsNumber()),
            _ => MonsterValue.FromText(value.ToDisplayString()),
        };

    private static string NormalizeElementProfile(string value)
    {
        const string order = "ALFCBPS";
        var result = new StringBuilder(order.Length);
        foreach (char element in order)
        {
            if (value.IndexOf(element, StringComparison.OrdinalIgnoreCase) >= 0)
                result.Append(element);
        }
        return result.ToString();
    }

    private void SelectProfile(string name)
    {
        SaveProfile();
        if (!_profiles.Select(name))
        {
            _profileLifecycleNotice = $"Profile '{name}' is unavailable.";
            return;
        }
        LoadSelectedProfile();
        _profileLifecycleNotice = $"Loaded {_profiles.Selected}.";
    }

    private void CreateProfileCore(bool copyCurrent)
    {
        if (!_profiles.Create(
            _profileNameDraft,
            copyCurrent,
            _allSettings,
            _noBuffItemNames,
            _commandLogTypes,
            out string notice))
        {
            _profileLifecycleNotice = notice;
            return;
        }
        _profileNameDraft = string.Empty;
        _profileLifecycleNotice = notice;
        ResetProfileConsumers();
    }

    private void ClearProfileCore()
    {
        _profiles.ClearCurrent(_allSettings, _noBuffItemNames, _commandLogTypes);
        _profileLifecycleNotice = $"Cleared {_profiles.Selected} to VTank defaults.";
        ResetProfileConsumers();
    }

    private void DeleteProfileCore()
    {
        if (!_profiles.Delete(out string notice))
        {
            _profileLifecycleNotice = notice;
            return;
        }
        LoadSelectedProfile();
        _profileLifecycleNotice = notice;
    }

    private void LoadSelectedProfile()
    {
        _profiles.LoadCurrent(_allSettings, _noBuffItemNames, _commandLogTypes);
        LoadLootProfile();
        LoadRouteProfile();
        ApplyPersistedOptionOverrides();
        ResetProfileConsumers();
    }

    private void ResetProfileConsumers()
    {
        _vitalRecharge.Reset();
        _vitalHelperRecharge.Reset();
        _dispel.Reset();
        _inventoryMaintenance.Reset();
        _crafting.Reset();
        _itemManaRecharge.Reset();
        _loot.Reset();
        _navigation.Reset();
        _coverageSpellSnapshot = null;
        _coverageRefreshRemaining = 0d;
        EnsureDefaultMonsterRule();
        RefreshMonsterEditor();
        RefreshItemEditors();
        RefreshLootEditor();
        RefreshRouteEditor();
        RefreshAdvancedOptions();
    }

    private void ClearMossTankActionLocks()
    {
        ClearFastCastMovement();
        _buffRule.ClearCastAttempt();
        _buffRule.ClearRecastLock();
        _randomHelperRemaining = 0d;
        _combat.ClearActionLocks();
        _vitalRecharge.Reset();
        _vitalHelperRecharge.Reset();
        _dispel.Reset();
        _inventoryMaintenance.Reset();
        _crafting.Reset();
        _itemManaRecharge.Reset();
        _loot.Reset();
        _profileGive.Reset();
        _navigation.ClearActionLocks();
    }

    private void FakeImperil()
    {
        uint target = _host.Selection.SelectedObjectId ?? 0u;
        if (target == 0u
            || !_host.Automation.Objects.TryGet(target, out PluginWorldObject value)
            || value.ObjectClass != PluginObjectClass.Monster)
        {
            WriteVtank("Select a monster first.");
            return;
        }
        _combat.RecordFakeImperil(target);
        WriteVtank("Fake cast complete.");
    }

    private void EnsureCharacterProfile()
    {
        string characterName = _host.Automation.Character.Name;
        bool macroChanged = _profiles.BindCharacter(characterName);
        bool lootChanged = _lootProfiles.BindCharacter(characterName);
        bool routeChanged = _routeProfiles.BindCharacter(characterName);
        bool metaChanged = _metaProfiles.BindCharacter(characterName);
        if (!macroChanged && !lootChanged && !routeChanged && !metaChanged)
            return;
        if (macroChanged)
            LoadSelectedProfile();
        else
        {
            if (lootChanged)
                LoadLootProfile();
            if (routeChanged)
                LoadRouteProfile();
            if (metaChanged)
                LoadMetaProfile();
        }
        if (macroChanged && metaChanged)
            LoadMetaProfile();
        _profileLifecycleNotice = $"Loaded {_profiles.Selected} for "
            + (_host.Automation.Character.Name.Length == 0
                ? "this character."
                : _host.Automation.Character.Name + ".");
    }

    private void SaveProfile()
    {
        _profiles.SaveCurrent(_allSettings, _noBuffItemNames, _commandLogTypes);
        _lootProfiles.SaveCurrent(
            _inventorySettings.Loot.Rules,
            _inventorySettings.Loot);
        SaveRouteProfile();
        SaveMetaProfile();
    }

    private void ApplyPersistedOptionOverrides()
    {
        KeyValuePair<string, MonsterValue>[] overrides = _combatSettings
            .DynamicSettings
            .Where(pair => VtankOptionCatalog.IsKnown(pair.Key))
            .ToArray();
        if (overrides.Length == 0)
            return;
        _applyingProfileOptions = true;
        try
        {
            foreach ((string name, MonsterValue value) in overrides)
                SetMetaOption(name, ToExpressionValue(value));
        }
        finally
        {
            _applyingProfileOptions = false;
        }
    }

    // ── the loop ──────────────────────────────────────────────────────────
    private void Announce(string text) =>
        _host.Automation.Chat.PostSystemMessage($"[MossTank] {text}");

    private void Stop(string status)
    {
        ClearFastCastMovement();
        // gj.cs:262-276 / d() - the tracker is forced back to idle whenever
        // the thing it was waiting on stops mattering.
        _buffRule.Stop();
        _status = status;
        RestoreSelection();
        _combatModeGate.Reset();
    }

    private void RestoreSelection()
    {
        if (_selectionBeforePass is { } previous && previous != 0)
            _host.Selection.Select(previous);
        else
            _host.Selection.Clear();
        _selectionBeforePass = null;
    }

    private void OnBuffCastOutcome(SpellCastOutcomeInfo info) =>
        _buffRule.ObserveCastOutcome(info);


    bool IBuffRuleHost.MacroEnabled => _combat.Enabled;

    bool IBuffRuleHost.HasTarget => _combat.HasTarget;

    CombatModeGate IBuffRuleHost.Gate => _combatModeGate;

    SpellCastTracker IBuffRuleHost.CastTracker => _castTracker;

    void IBuffRuleHost.SetStatus(string status) => _status = status;

    void IBuffRuleHost.Announce(string text) => Announce(text);

    void IBuffRuleHost.Log(MacroLogChannel channel, string message) =>
        EmitMacroLog(channel, message);

    void IBuffRuleHost.MirrorToLog(MacroLogChannel channel, string message) =>
        EmitMacroLog(channel, message, chat: false);

    void IBuffRuleHost.CaptureSelection() =>
        _selectionBeforePass ??= _host.Selection.SelectedObjectId;

    void IBuffRuleHost.RestoreSelection() => RestoreSelection();

    void IBuffRuleHost.StopFromBuffRule(string status) => Stop(status);

    void IBuffRuleHost.BeginFastCast(
        IAutomationSurface automation, in PluginSpellInfo spell) =>
        BeginFastCastMovement(automation, spell);
    private void ToggleMacro() => SetMacroRunning(!_combat.Enabled);

    private void SetMacroRunning(bool running)
    {
        if (_combat.Enabled == running)
            return;
        _combat.Toggle();
        if (running || _combat.Enabled)
            return;

        if (_buffRule.IsBursting)
        {
            ClearFastCastMovement();
            _buffRule.StopBurstOnly();
            RestoreSelection();
            _status = "Stopped.";
        }
        _combatModeGate.Reset();
        _summonPet.Reset();
        _vitalRecharge.Reset();
        _vitalHelperRecharge.Reset();
        _dispel.Reset();
        _inventoryMaintenance.Reset();
        _crafting.Reset();
        _loot.Reset();
        _profileGive.Reset();
        _navigation.Reset();
    }

    private void HandleDeath(bool macroRunning)
    {
        if (!macroRunning || !_combatSettings.StopMacroOnDeath)
            return;
        SetMacroRunning(false);
        Announce("Macro stopped because the character died.");
    }

    private bool _wasDeadForMacro;

    private bool ReadyToActInPeace()
    {
        IEquipmentAutomation equipment = _host.Automation.Equipment;
        return _combatModeGate.TryDropToPeace(
            equipment.IsAvailable ? equipment.CaptureOwnedEquipment() : [],
            "an item");
    }

    private void ResetOncePerRunWarnings()
    {
        _combatModeGate.ResetOncePerRunWarnings();
        _navigation.ResetOncePerRunWarnings();
        _buffRule.ResetOncePerRunWarnings();
    }

    public void OnTick(double elapsedSeconds)
    {
        bool automationAvailable = _host.Automation.IsAvailable;
        if (!automationAvailable)
        {
            if (_automationWasAvailable)
                HandleSessionEnded();
            _automationWasAvailable = false;
            RefreshDisplayBindings(elapsedSeconds);
            return;
        }
        if (!_automationWasAvailable)
        {
            _automationWasAvailable = true;
            HandleSessionStarted();
        }

        ObserveFastCastMovement(elapsedSeconds);
        _buffRule.Advance(elapsedSeconds);
        EnsureCharacterProfile();
        ShowFirstRunGuidance();
        ObserveCommandPortalState();
        bool macroRunning = _combat.Enabled;
        bool dead = _host.Automation.IsAvailable
            && _host.Automation.Character.MaxHealth > 0u
            && _host.Automation.Character.CurrentHealth == 0u;
        if (dead && !_wasDeadForMacro)
            HandleDeath(macroRunning);
        _wasDeadForMacro = dead;
        _combatSettings.MetaState = _meta.CurrentState;
        RefreshDisplayBindings(elapsedSeconds);

        bool commandJumpOwnsAction = TickCommandJump(elapsedSeconds);
        bool giveOwnsAction = _profileGive.Tick(
            elapsedSeconds,
            canAct: !_buffRule.IsBursting && !commandJumpOwnsAction);
        _prologueOwnsAction = commandJumpOwnsAction || giveOwnsAction;

        ObserveSchedulerPokes();

        bool schedulerActive = macroRunning
            || _inventorySettings.ManaChargesWhenOff;
        if (schedulerActive && !_scheduler.IsRunning)
        {
            _scheduler.Start();
            _transactionSuspensionHeld = false;
            _transactionSuspensionElapsed = 0d;
            ResetOncePerRunWarnings();
        }
        else if (!schedulerActive && _scheduler.IsRunning)
        {
            _scheduler.Stop();
            _transactionSuspensionHeld = false;
            _transactionSuspensionElapsed = 0d;
        }
        ObserveCastResult(elapsedSeconds);
        ObserveCastSuspension(elapsedSeconds);
        _scheduler.ExternalSuspension = _prologueOwnsAction;
        _scheduler.Advance(elapsedSeconds);
        _combatModeGate.AdvancePass(elapsedSeconds);

        // c406942ef review finding 4: the idle-peace owner's status was never
        // displayed. It is displayed on the pass it actually wins.
        if (_idlePeace.Running && _idlePeace.Status is { } idleStatus)
            _status = idleStatus;

        if (_activeTab == TankTab.Route)
            RefreshRouteEditor();
    }

    private void ObserveSchedulerPokes()
    {
        IAutomationSurface automation = _host.Automation;
        long magic = automation.Magic.LastCompletion.Revision;
        long items = automation.Items.LastCompletion.Revision;
        PluginCombatMode mode = automation.Combat.Snapshot.Mode;
        bool equipping = automation.Equipment.IsBusy;
        if (magic == _pokeMagicRevision
            && items == _pokeItemRevision
            && mode == _pokeCombatMode
            && equipping == _pokeEquipmentBusy)
        {
            return;
        }

        _pokeMagicRevision = magic;
        _pokeItemRevision = items;
        _pokeCombatMode = mode;
        _pokeEquipmentBusy = equipping;
        _scheduler.Poke();
    }

    private void ObserveCastResult(double elapsedSeconds)
    {
        double elapsed = Math.Max(0d, elapsedSeconds);

        _castTracker.ObserveCompletion(_host.Automation.Magic.LastCompletion);
        bool armed = _castTracker.IsBusy;
        foreach (PluginChatMessage message in
            _host.Automation.Chat.CaptureMessages(_castTrackerChatSequence))
        {
            _castTrackerChatSequence = Math.Max(
                _castTrackerChatSequence,
                message.Sequence);
            if (armed)
            {
                _castTracker.ObserveChat(
                    message.Sequence,
                    message.Text,
                    ownSpeech: message.Kind == SpellCastTracker.LocalSpeechChatKind
                        && message.SenderObjectId != 0u
                        && message.SenderObjectId
                            == _host.Automation.Character.ObjectId);
            }
        }
        _castTracker.Advance(elapsed);
    }

    private void ObserveCastSuspension(double elapsedSeconds)
    {
        IAutomationSurface automation = _host.Automation;
        bool inFlight = _castTracker.IsBusy
            || automation.Magic.IsCasting
            || automation.Items.IsBusy;

        if (_transactionSuspensionHeld)
        {
            _transactionSuspensionElapsed += Math.Max(0d, elapsedSeconds);
            if (inFlight
                && _transactionSuspensionElapsed < TransactionSuspensionWatchdogSeconds)
            {
                return;
            }
            _transactionSuspensionHeld = false;
            _transactionSuspensionElapsed = 0d;
            _scheduler.Resume();
            return;
        }

        if (!inFlight)
            return;
        _transactionSuspensionHeld = true;
        _transactionSuspensionElapsed = 0d;
        _scheduler.Suspend();
    }

    internal void PokeScheduler() => _scheduler.Poke();

    private bool TickRandomHelper(double elapsedSeconds, bool canAct)
    {
        _randomHelperRemaining = Math.Max(
            0d,
            _randomHelperRemaining - Math.Max(0d, elapsedSeconds));
        if (!canAct
            || !_buffSettings.RandomHelperBuffs
            || _randomHelperRemaining > 0d
            || !_host.Automation.IsAvailable
            || _host.Automation.Items.IsBusy)
        {
            return false;
        }
        if (_castTracker.IsBusy || _host.Automation.Magic.IsCasting)
            return true;

        PluginNavigationSnapshot navigation =
            _host.Automation.Navigation.Snapshot;
        if (!navigation.IsAvailable)
            return false;

        // ba.cs:102-111 — every Player object other than self inside 0.075
        // landblock units. The trace note's ≈240 m/unit puts that at 18 m.
        PluginWorldObject[] players = _host.Automation.Objects.CaptureObjects()
            .Where(value => value.ObjectClass == PluginObjectClass.Player
                && value.ObjectId != _host.Automation.Character.ObjectId
                && value.HasPosition
                && navigation.Position.HorizontalDistanceMeters(value.Position)
                    < 18d)
            .OrderBy(static value => value.ObjectId)
            .ToArray();
        if (players.Length == 0)
            return false;

        // ba.cs:116 — ONE random target, drawn before the spell loop and kept
        // for whatever the loop settles on.
        PluginWorldObject player = players[_randomHelper.Next(players.Length)];

        // ba.cs:29-40 — the eleven hardcoded Tier-I "Other" stems, in VTank's
        // own order.
        string[] stems =
        [
            "Endurance Other", "Regeneration Other", "Rejuvenation Other",
            "Armor Other", "Blade Protection Other",
            "Bludgeoning Protection Other", "Cold Protection Other",
            "Fire Protection Other", "Lightning Protection Other",
            "Piercing Protection Other", "Acid Protection Other",
        ];

        var castability = new BuffCastability(
            _host.Automation.Spells,
            _host.Automation.Magic,
            _host.Automation.Items.IsAvailable
                ? _host.Automation.Items.CaptureOwnedItems()
                : [],
            _buffSettings.BlacklistedSpellComponents,
            text => Announce(text),
            static (_, _, _) => { });
        for (int attempt = 0; attempt < 100; attempt++)
        {
            string stem = stems[_randomHelper.Next(stems.Length)];
            if (!BuffSelfRule.ResolveBestKnown(
                    _host.Automation,
                    stem,
                    _buffSettings,
                    castability,
                    out PluginSpellInfo spell))
            {
                continue;
            }

            if (!_combatModeGate.TryPrepare(PluginCombatMode.Magic))
            {
                _status = _combatModeGate.Status;
                return true;
            }
            if (_host.Automation.Magic.EvaluateGate(spell.SpellId, player.ObjectId)
                    != PluginCastGate.Ready
                || !_host.Automation.Magic.Cast(spell.SpellId, player.ObjectId))
            {
                continue;
            }

            _randomHelperRemaining = Math.Max(
                0.25d,
                _buffSettings.RandomHelperIntervalSeconds);
            EmitMacroLog(
                MacroLogChannel.SpellCast,
                $"Casting: {spell.Name} on {player.ObjectId} ({player.Name})");
            _host.Log.Info(
                $"MossTank: random helper {spell.Name} -> {player.Name}");
            return true;
        }
        // ba.cs:125 — a hundred draws with nothing to show for them.
        return false;
    }

    /// <summary><c>ba.m_f</c> (<c>ba.cs:20</c>).</summary>
    private readonly Random _randomHelper = new();

    private void ShowFirstRunGuidance()
    {
        if (!_firstRunGuidancePending || !_host.Automation.IsAvailable)
            return;
        _firstRunGuidancePending = false;
        const string guidance = "First run: choose profiles, configure the "
            + "Options tab, then press Run Macro. Minimize with –; MossTank "
            + "keeps running from the right-side plugin shelf. Put VTank "
            + ".nav/.utl/.met files in imports and use /vt nav, /vt loot, or "
            + "/vt meta import <name>.";
        Announce(guidance);
        try
        {
            _host.Storage.WriteText("onboarding/v1.txt", "shown");
        }
        catch (Exception error)
        {
            _host.Log.Warn(
                "MossTank could not persist first-run guidance state: "
                + error.Message);
        }
    }

    private static bool NeedsFirstRunGuidance(IPluginHost host)
    {
        if (!host.Storage.IsAvailable)
            return false;
        try
        {
            return string.IsNullOrWhiteSpace(
                host.Storage.ReadText("onboarding/v1.txt"));
        }
        catch (Exception error)
        {
            host.Log.Warn(
                "MossTank could not read first-run guidance state: "
                + error.Message);
            return false;
        }
    }

    public void Disable()
    {
        if (_combat.Enabled)
            SetMacroRunning(false);
        if (_buffRule.IsBursting)
            Stop("Stopped.");
        _vitalRecharge.Reset();
        _vitalHelperRecharge.Reset();
        _inventoryMaintenance.Reset();
        _crafting.Reset();
        _itemManaRecharge.Reset();
        _loot.Reset();
        _profileGive.Reset();
        _navigation.Reset();
        _fellowshipManager.Reset();
        _meta.SetEnabled(false);
        _metaViews.DestroyAll();
        _expressions.DestroyAuxiliaryViews();
        _expressions.ClearSession();
    }

    private void HandleSessionEnded()
    {
        if (_combat.Enabled)
            _combat.OnTick(0d, navigationEnabled: false);

        ClearFastCastMovement();
        _buffRule.Reset();
        _selectionBeforePass = null;
        _status = "Lost the session.";
        _combatModeGate.Reset();
        ResetSessionScopedControllers();
    }

    private void HandleSessionStarted()
    {
        _meta.ResetSession();
        _expressions.ClearSession();
        _expressions.DestroyAuxiliaryViews();
        _metaViews.DestroyAll();
        ResetCommandSession();
        _buffRule.Reset();
        _randomHelperRemaining = 0d;
        _coverageSpellSnapshot = null;
        _coverageRefreshRemaining = 0d;
        _status = "Idle.";
    }

    private void ResetSessionScopedControllers()
    {
        _summonPet.Reset();
        _vitalRecharge.Reset();
        _vitalHelperRecharge.Reset();
        _dispel.Reset();
        _inventoryMaintenance.Reset();
        _crafting.Reset();
        _itemManaRecharge.Reset();
        _loot.Reset();
        _profileGive.Reset();
        _navigation.Reset();
        _fellowshipManager.Reset();
        _meta.ResetSession();
        _metaViews.DestroyAll();
        _expressions.DestroyAuxiliaryViews();
        _expressions.ClearSession();
        ResetCommandSession();
        _buffRule.Reset();
        _randomHelperRemaining = 0d;
        _coverageSpellSnapshot = null;
        _coverageRefreshRemaining = 0d;
        _combatSettings.MetaState = MetaEngine.DefaultState;
    }

    private void RefreshDisplayBindings(double elapsedSeconds)
    {
        IAutomationSurface automation = _host.Automation;
        if (!automation.IsAvailable)
        {
            _vitals = string.Empty;
            _coverage = string.Empty;
            _vitalsInitialized = false;
            _coverageSpellSnapshot = null;
            _coverageBuffLineCount = 0;
            _coverageRefreshRemaining = 0.0;
            return;
        }

        ICharacterInfo character = automation.Character;
        uint currentHealth = character.CurrentHealth;
        uint maxHealth = character.MaxHealth;
        uint currentStamina = character.CurrentStamina;
        uint maxStamina = character.MaxStamina;
        uint currentMana = character.CurrentMana;
        uint maxMana = character.MaxMana;
        if (!_vitalsInitialized
            || currentHealth != _currentHealth
            || maxHealth != _maxHealth
            || currentStamina != _currentStamina
            || maxStamina != _maxStamina
            || currentMana != _currentMana
            || maxMana != _maxMana)
        {
            _currentHealth = currentHealth;
            _maxHealth = maxHealth;
            _currentStamina = currentStamina;
            _maxStamina = maxStamina;
            _currentMana = currentMana;
            _maxMana = maxMana;
            _vitals = $"Health {currentHealth}/{maxHealth}"
                + $"   Stam {currentStamina}/{maxStamina}"
                + $"   Mana {currentMana}/{maxMana}";
            _vitalsInitialized = true;
        }

        IReadOnlyList<PluginSpellInfo> spells = automation.Spells.KnownSelfBuffs;
        bool spellbookChanged = !ReferenceEquals(spells, _coverageSpellSnapshot);
        _coverageRefreshRemaining -= Math.Max(0.0, elapsedSeconds);
        if (!spellbookChanged && _coverageRefreshRemaining > 0.0)
            return;

        if (spellbookChanged)
        {
            _coverageSpellSnapshot = spells;
            _coverageBuffLineCount = BuffProfile.Build(spells).Count;
        }

        int trained = 0;
        foreach (PluginSkillInfo skill in character.Skills)
        {
            if (skill.Training is PluginSkillTraining.Trained
                or PluginSkillTraining.Specialized)
            {
                trained++;
            }
        }
        _coverage = $"{character.Attributes.Count} attributes, "
            + $"{trained} trained skills, "
            + $"{_coverageBuffLineCount} buff lines";
        _coverageRefreshRemaining = CoverageRefreshIntervalSeconds;
    }

    private void BeginFastCastMovement(
        IAutomationSurface automation,
        in PluginSpellInfo spell)
    {
        if (!_buffSettings.FastCastBuffs || !IsVtankInstantCast(spell))
            return;
        if (spell.School is 34u or 43u)
            return;

        PluginNavigationCommandStatus result = automation.Navigation
            .SetMovementIntent(new PluginMovementIntent(Forward: true));
        if (result != PluginNavigationCommandStatus.Accepted)
            return;
        _fastCastMovementActive = true;
        _fastCastStartCompletionRevision = automation.Magic.LastCompletion.Revision;
        _fastCastMovementElapsed = 0d;
    }

    private void ObserveFastCastMovement(double elapsedSeconds)
    {
        if (!_fastCastMovementActive)
            return;
        _fastCastMovementElapsed += Math.Max(0d, elapsedSeconds);
        IMagicCommands magic = _host.Automation.Magic;
        bool receiptArrived = magic.LastCompletion.Revision
            != _fastCastStartCompletionRevision;
        bool castEnded = _fastCastMovementElapsed >= 0.2d && !magic.IsCasting;
        if (receiptArrived || castEnded || _fastCastMovementElapsed >= 10d)
            ClearFastCastMovement();
    }

    private void ClearFastCastMovement()
    {
        if (!_fastCastMovementActive)
            return;
        _host.Automation.Navigation.ClearMovementIntent();
        _fastCastMovementActive = false;
        _fastCastStartCompletionRevision = 0;
        _fastCastMovementElapsed = 0d;
    }

    private static bool IsVtankInstantCast(in PluginSpellInfo spell)
    {
        if (spell.Difficulty < 50)
            return true;
        if (spell.IsUntargeted
            && !spell.IsFellowship
            && spell.DurationSeconds >= 60f
            && spell.School is 31u or 33u)
        {
            return true;
        }
        return spell.Family is >= 243u and <= 249u or 639u;
    }
}
