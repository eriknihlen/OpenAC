using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class CombatController
{
    private const float PowerReleaseEpsilon = 0.005f;

    private readonly IPluginHost _host;
    private readonly CombatSettings _settings;
    private readonly VitalSettings _vitalSettings;
    private readonly DebuffTracker _debuffs = new();
    private readonly CombatFailureTracker _failures = new();
    private readonly PetAutomation _pets = new();
    private IReadOnlyList<PluginCombatTarget> _targets =
        Array.Empty<PluginCombatTarget>();
    private IReadOnlyList<PluginSpellInfo>? _combatSpellSnapshot;
    private DebuffSpellCatalog _debuffCatalog =
        DebuffSpellCatalog.Build(Array.Empty<PluginSpellInfo>());
    private AttackSpellCatalog _attackCatalog =
        AttackSpellCatalog.Build(Array.Empty<PluginSpellInfo>());
    private double _now;
    private double _untilScan;
    private uint _targetId;
    private ResolvedMonsterRule _targetRule;
    private string _targetName = string.Empty;
    private float _targetDistance;
    private string _targetText = "Target  —";
    private string _modeText = "Mode  Unknown";
    private PluginCombatMode _lastMode = PluginCombatMode.Unknown;
    private bool _paused;
    private long _observedPhysicalCompletion;
    private long _observedAttackCastCompletion;
    private uint _pendingPhysicalTarget;
    private uint _pendingAttackSpell;
    private uint _pendingAttackTarget;
    private PendingItemDebuff? _pendingItemDebuff;
    private ulong _observedChatSequence;
    private long _observedItemCompletion;
    private bool _combatPolicySuspended;
    private bool _approachMovementOwned;
    private bool _breakableTurnOwned;
    private int _dropToPeaceModeRetries;
    private Func<string, int, bool>? _requestAmmunitionCraft;
    private Func<string, int, bool>? _canCraftAmmunition;
    private int _randomDamageIndex;
    private long _observedJiggleCastCompletion;
    private bool _selectionJiggleActive;
    private bool _selectionJigglePreviousPlayer;
    private double _nextSelectionJiggleAt;

    private static readonly MonsterDamageType[] RandomDamageCycle =
    [
        MonsterDamageType.Pierce,
        MonsterDamageType.Bludgeon,
        MonsterDamageType.Slash,
        MonsterDamageType.Acid,
        MonsterDamageType.Electric,
        MonsterDamageType.Cold,
        MonsterDamageType.Fire,
    ];

    public CombatController(
        IPluginHost host,
        CombatSettings settings,
        VitalSettings? vitalSettings = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _vitalSettings = vitalSettings ?? new VitalSettings();
    }

    public bool Enabled { get; private set; }
    public string Status { get; private set; } = "Combat off";
    public string TargetText => _targetText;
    public string ModeText => _modeText;
    public bool HasTarget => _targetId != 0u;

    public string ButtonText => Enabled ? "Stop Macro" : "Run Macro";

    public void BindAmmunitionCraftRequest(
        Func<string, int, bool> canCraft,
        Func<string, int, bool> request)
    {
        _canCraftAmmunition = canCraft
            ?? throw new ArgumentNullException(nameof(canCraft));
        _requestAmmunitionCraft = request
            ?? throw new ArgumentNullException(nameof(request));
    }

    public void ClearActionLocks()
    {
        _host.Automation.Combat.AbortPhysicalAttack();
        StopApproachMovement();
        StopBreakableTurnMovement();
        StopSelectionJiggle();
        _pendingPhysicalTarget = 0u;
        _pendingAttackSpell = 0u;
        _pendingAttackTarget = 0u;
        ClearPendingItemDebuff();
        _debuffs.ClearPending();
        _dropToPeaceModeRetries = 0;
        _untilScan = 0d;
        if (Enabled)
            Status = "Action locks cleared";
    }

    public bool RecordFakeImperil(uint targetObjectId)
    {
        if (targetObjectId == 0u)
            return false;
        _debuffs.RecordFakeImperil(targetObjectId, _now);
        return true;
    }

    public void Toggle()
    {
        if (Enabled)
        {
            Disable("Macro stopped");
            _host.Automation.Chat.PostSystemMessage("[MossTank] Macro stopped.");
            return;
        }

        if (!_host.Automation.IsAvailable)
        {
            Status = "Not in world";
            return;
        }

        Enabled = true;
        _paused = false;
        _combatPolicySuspended = !_settings.Enabled;
        _untilScan = 0d;
        Status = _settings.Enabled ? "Scanning for targets" : "Combat disabled";
        _host.Automation.Chat.PostSystemMessage("[MossTank] Macro started.");
    }

    public void SetPaused(bool paused)
    {
        if (_paused == paused)
            return;
        _paused = paused;
        if (paused && Enabled)
        {
            _host.Automation.Combat.AbortPhysicalAttack();
            StopApproachMovement();
            StopBreakableTurnMovement();
            Status = "Paused for buffing";
        }
        else if (Enabled)
        {
            Status = "Scanning for targets";
            _untilScan = 0d;
        }
    }

    public bool EquipOneStepForMonster(string monsterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monsterName);
        var target = new PluginCombatTarget(
            0u,
            monsterName.Trim(),
            0u,
            0f,
            0f,
            false,
            1f);
        _targetName = target.Name;
        _targetRule = _settings.ResolveRule(target);
        return !TickEquipment();
    }

    public void OnTick(double elapsedSeconds, bool navigationEnabled = true)
    {
        if (!Enabled)
            return;
        if (!_host.Automation.IsAvailable)
        {
            Disable("Session ended");
            return;
        }
        if (_paused)
            return;
        if (!_settings.Enabled)
        {
            if (_combatPolicySuspended)
                return;
            _combatPolicySuspended = true;
            if (_targetId != 0u || _pendingPhysicalTarget != 0u)
                _host.Automation.Combat.AbortPhysicalAttack();
            _pendingPhysicalTarget = 0u;
            _pendingAttackSpell = 0u;
            _pendingAttackTarget = 0u;
            ClearPendingItemDebuff();
            _debuffs.Reset();
            _failures.Reset();
            ClearTarget();
            Status = "Combat disabled";
            return;
        }
        if (_combatPolicySuspended)
        {
            _combatPolicySuspended = false;
            _untilScan = 0d;
            Status = "Scanning for targets";
        }

        _now += Math.Max(0d, elapsedSeconds);
        PluginCastCompletion castCompletion =
            _host.Automation.Magic.LastCompletion;
        ObserveSelectionJiggle(castCompletion);
        TickSelectionJiggle();
        DebuffCompletion completion = _debuffs.Observe(
            castCompletion,
            _now);
        if (completion.Completed && !completion.Succeeded)
        {
            Status = $"{completion.SpellName} failed (0x{completion.WeenieError:X})";
        }
        _debuffs.ExpirePending(_now);

        PluginCombatSnapshot current = _host.Automation.Combat.Snapshot;
        ObserveItemDebuffReceipts();
        ObserveAttackReceipts(current, castCompletion);
        if (current.Mode != _lastMode)
        {
            _lastMode = current.Mode;
            _modeText = $"Mode  {current.Mode}";
        }

        _untilScan -= Math.Max(0d, elapsedSeconds);
        if (_untilScan <= 0d)
        {
            double acquisitionRange = navigationEnabled
                ? Math.Max(_settings.MaximumRange, _settings.ApproachDistance)
                : _settings.MaximumRange;
            _targets = _host.Automation.Combat.CaptureHostileTargets(
                (float)acquisitionRange);
            foreach (uint ghost in _failures.ObserveTargets(
                _targets,
                _now,
                _settings))
            {
                DismissGhost(ghost);
            }
            _debuffs.RetainTargets(
                _targets.Select(static target => target.ObjectId).ToHashSet());
            _untilScan = Math.Max(0.05d, _settings.ScanIntervalSeconds);
            RefreshTarget();
        }

        if (_pendingItemDebuff is not null)
        {
            TickPendingItemDebuff(current);
            return;
        }

        if (_targetId == 0u)
        {
            StopApproachMovement();
            Status = "Waiting for a target";
            return;
        }

        if (_targetDistance > _settings.MaximumRange)
        {
            if (navigationEnabled
                && _settings.ApproachDistance > _settings.MaximumRange
                && _targetDistance <= _settings.ApproachDistance
                && TickApproach())
            {
                return;
            }

            StopApproachMovement();
            Status = $"{_targetName} is out of attack range";
            return;
        }
        StopApproachMovement();

        if (_pets.Tick(
                _host.Automation.Items,
                _host.Automation.Character,
                _targets,
                _settings,
                _now,
                out string petStatus))
        {
            Status = petStatus;
            return;
        }

        PluginCombatSnapshot combat = _host.Automation.Combat.Snapshot;
        if (TickDebuffs(combat))
            return;

        if (TickEquipment())
            return;

        combat = _host.Automation.Combat.Snapshot;
        if (combat.Mode is PluginCombatMode.Unknown or PluginCombatMode.Peace)
        {
            PluginCombatCommandResult mode =
                _host.Automation.Combat.EnterDefaultMode();
            Status = mode.Status == PluginCombatCommandStatus.Refused
                ? mode.Notice ?? "Cannot enter combat mode"
                : "Entering combat mode";
            return;
        }

        if (!_targetRule.Actions.Attacks)
        {
            Status = $"Debuffs complete for {_targetName}";
            return;
        }

        if (combat.Mode == PluginCombatMode.Magic)
        {
            TickMagic();
            return;
        }

        if (combat.Mode is not (PluginCombatMode.Melee or PluginCombatMode.Missile))
        {
            Status = $"Unsupported mode: {combat.Mode}";
            return;
        }

        TickPhysical(combat);
    }

    private void TickPhysical(PluginCombatSnapshot combat)
    {
        if (combat.ServerResponsePending || combat.RepeatAttackInProgress)
        {
            Status = $"Attacking {_targetName}";
            return;
        }

        if (combat.RequestInProgress)
        {
            if (combat.BuildInProgress
                && combat.PowerBarLevel + PowerReleaseEpsilon
                    >= combat.DesiredPower)
            {
                PluginCombatCommandResult release =
                    _host.Automation.Combat.ReleasePhysicalAttack();
                Status = release.Status == PluginCombatCommandStatus.Released
                    ? $"Attacking {_targetName}"
                    : $"Attack release: {release.Status}";
            }
            else
            {
                Status = $"Charging {combat.PowerBarLevel * 100f:0}%";
            }
            return;
        }

        IReadOnlyList<PluginInventoryItem> inventory =
            _host.Automation.Items.CaptureOwnedItems();
        MonsterRuleActions physicalActions = ResolvePhysicalActions(
            _targetRule.Actions,
            FindTarget(_targetId),
            inventory);
        if (combat.Mode == PluginCombatMode.Missile
            && !ProjectilePathIsClear(
                _targetId,
                PluginProjectilePathKind.Missile,
                _settings.AttackHeight,
                out PluginProjectilePathResult missilePath))
        {
            Status = ProjectileStatus(missilePath, _targetName);
            return;
        }
        float desiredPower = AutoAttackPower.Resolve(
            physicalActions,
            _settings,
            _host.Automation.Character,
            inventory);
        PluginCombatCommandResult begin =
            _host.Automation.Combat.BeginPhysicalAttack(
                _targetId,
                _settings.AttackHeight,
                desiredPower);
        Status = begin.Status switch
        {
            PluginCombatCommandStatus.Started => $"Charging {_targetName}",
            PluginCombatCommandStatus.Busy => $"Waiting on {_targetName}",
            PluginCombatCommandStatus.InvalidTarget => "Target disappeared",
            PluginCombatCommandStatus.WrongMode => "Waiting for combat mode",
            _ => $"Attack refused: {begin.Status}",
        };
        if (begin.Status == PluginCombatCommandStatus.InvalidTarget)
        {
            _host.Automation.Combat.AbortPhysicalAttack();
            ClearTarget();
        }
        else if (begin.Status == PluginCombatCommandStatus.Started)
        {
            _pendingPhysicalTarget = _targetId;
            _failures.BeginAttack(
                _targetId,
                FindTarget(_targetId).HealthRevision);
        }
    }

    private void TickMagic()
    {
        IMagicCommands magic = _host.Automation.Magic;
        if (magic.IsCasting)
        {
            Status = $"Casting at {_targetName}";
            return;
        }

        RefreshSpellCatalogs();
        string? projectileRefusal = null;
        MonsterRuleActions attackActions = ResolveRandomDamage(
            _targetRule.Actions);
        IReadOnlyList<AttackSpellChoice> choices = _attackCatalog.Candidates(
            attackActions,
            _settings,
            FindTarget(_targetId),
            CountNearbyRingTargets(),
            _host.Automation.Character);
        foreach (AttackSpellChoice choice in choices)
        {
            if (!CanCastHuntSpell(choice.Spell, FindTarget(_targetId)))
                continue;
            if (choice.Spell.IsProjectile
                && !ProjectilePathIsClear(
                    _targetId,
                    choice.Shape == AttackSpellShape.Arc
                        ? PluginProjectilePathKind.Arc
                        : PluginProjectilePathKind.Straight,
                    _settings.AttackHeight,
                    out PluginProjectilePathResult spellPath))
            {
                projectileRefusal = ProjectileStatus(spellPath, _targetName);
                Status = projectileRefusal;
                continue;
            }
            if (!choice.CastWithoutTarget
                && !ReadyForBreakableTurn(choice.Spell, _targetId))
            {
                return;
            }
            PluginCastGate gate = choice.CastWithoutTarget
                ? magic.EvaluateGate(choice.Spell.SpellId)
                : magic.EvaluateGate(choice.Spell.SpellId, _targetId);
            if (gate != PluginCastGate.Ready)
            {
                continue;
            }
            bool dispatched = choice.CastWithoutTarget
                ? magic.Cast(choice.Spell.SpellId)
                : magic.Cast(choice.Spell.SpellId, _targetId);
            if (!dispatched)
            {
                if (_failures.RecordSpellDidNotStart(_targetId, _settings))
                    DismissGhost(_targetId);
                continue;
            }

            Status = choice.Shape == AttackSpellShape.Ring
                ? $"{choice.Spell.Name} around {_targetName}"
                : $"{choice.Spell.Name} → {_targetName}";
            if (!choice.CastWithoutTarget)
            {
                _pendingAttackSpell = choice.Spell.SpellId;
                _pendingAttackTarget = _targetId;
                _failures.BeginAttack(
                    _targetId,
                    FindTarget(_targetId).HealthRevision);
            }
            return;
        }

        IReadOnlyList<PluginSpellInfo> fallback =
            _host.Automation.Spells.KnownAttackSpells;
        if (choices.Count == 0
            && attackActions.DamageType == MonsterDamageType.Auto)
        {
            foreach (PluginSpellInfo spell in fallback)
            {
                if (!CanCastHuntSpell(spell, FindTarget(_targetId)))
                    continue;
                if (spell.IsProjectile
                    && !ProjectilePathIsClear(
                        _targetId,
                        PluginProjectilePathKind.Straight,
                        _settings.AttackHeight,
                        out PluginProjectilePathResult fallbackPath))
                {
                    projectileRefusal = ProjectileStatus(
                        fallbackPath,
                        _targetName);
                    Status = projectileRefusal;
                    continue;
                }
                if (!ReadyForBreakableTurn(spell, _targetId))
                    return;
                if (magic.EvaluateGate(spell.SpellId, _targetId)
                        != PluginCastGate.Ready)
                {
                    continue;
                }
                if (!magic.Cast(spell.SpellId, _targetId))
                {
                    if (_failures.RecordSpellDidNotStart(_targetId, _settings))
                        DismissGhost(_targetId);
                    continue;
                }
                _pendingAttackSpell = spell.SpellId;
                _pendingAttackTarget = _targetId;
                _failures.BeginAttack(
                    _targetId,
                    FindTarget(_targetId).HealthRevision);
                Status = $"{spell.Name} → {_targetName}";
                return;
            }
        }

        Status = projectileRefusal
            ?? (choices.Count == 0 && fallback.Count == 0
                ? "No direct attack spell known"
                : "No usable attack spell");
    }

    private MonsterRuleActions ResolveRandomDamage(MonsterRuleActions actions)
    {
        if (actions.DamageType != MonsterDamageType.Random)
            return actions;

        MonsterDamageType damage = RandomDamageCycle[_randomDamageIndex];
        _randomDamageIndex = (_randomDamageIndex + 1) % RandomDamageCycle.Length;
        return actions with { DamageType = damage };
    }

    private bool CanCastHuntSpell(
        in PluginSpellInfo spell,
        in PluginCombatTarget target)
    {
        if (SpellComponentPolicy.UsesBlacklistedComponent(
                _host.Automation.Spells,
                spell,
                _settings.BlacklistedSpellComponents))
        {
            return false;
        }
        if (spell.School == 0u
            || !_host.Automation.Character.TryGetSkill(
                spell.School,
                out PluginSkillInfo skill))
        {
            return true;
        }
        if (skill.Current < spell.Difficulty
            + _settings.HuntSkillExcessOverDifficulty)
        {
            return false;
        }

        float maximumRange = spell.BaseRangeConstant
            + (spell.BaseRangeModifier * skill.Current)
            - (float)_settings.SpellRangeFudge;
        return maximumRange <= 0f
            || target.ObjectId == 0u
            || target.Distance <= MathF.Min(75f, maximumRange);
    }

    private int CountNearbyRingTargets()
    {
        int count = 0;
        foreach (PluginCombatTarget target in _targets)
        {
            if (target.Distance > _settings.RingDistance)
                continue;
            ResolvedMonsterRule resolved = _settings.ResolveRule(target);
            if (resolved.Priority >= 0 && resolved.Actions.UsesRing)
                count++;
        }
        return count;
    }

    private bool TickEquipment()
    {
        MonsterRuleActions actions = _targetRule.Actions;
        bool primaryRequiresWeapon = actions.UsesPrimaryAttack
            || actions.UsesRing;
        if (!primaryRequiresWeapon && !_settings.SwitchWandsToDebuff)
            return false;
        IEquipmentAutomation equipment = _host.Automation.Equipment;
        if (!equipment.IsAvailable)
        {
            return false;
        }
        if (equipment.IsBusy)
        {
            Status = "Switching equipment";
            return true;
        }

        IReadOnlyList<PluginEquipmentItem> items =
            equipment.CaptureOwnedEquipment();
        uint desiredWeapon = ResolveEquipmentObjectId(
            actions.WeaponObjectId,
            actions.WeaponName,
            items);
        if (desiredWeapon == 0u && actions.DamageType == MonsterDamageType.Auto)
        {
            desiredWeapon = SelectAutomaticWeapon(
                items,
                VtankDamageDatabase.Preferences(FindTarget(_targetId)),
                _settings);
        }
        else if (desiredWeapon == 0u)
        {
            desiredWeapon = SelectAutomaticWeapon(
                items,
                actions.DamageType,
                _settings);
        }

        if (TryEquipIfNeeded(equipment, items, desiredWeapon, "weapon"))
            return true;
        if (TickAmmunition(
                equipment,
                items,
                desiredWeapon,
                actions.DamageType))
        {
            return true;
        }
        if (TryEquipIfNeeded(
            equipment,
            items,
            ResolveEquipmentObjectId(
                actions.OffhandObjectId,
                actions.OffhandName,
                items),
            "offhand"))
        {
            return true;
        }
        return false;
    }

    private bool TickAmmunition(
        IEquipmentAutomation equipment,
        IReadOnlyList<PluginEquipmentItem> equipmentItems,
        uint desiredWeapon,
        MonsterDamageType configuredDamage)
    {
        PluginEquipmentItem launcher = equipmentItems.FirstOrDefault(
            item => item.ObjectId == desiredWeapon);
        int launcherType = VtankAmmunitionDatabase.LauncherType(
            launcher.AmmoType);
        if (launcherType == 0)
            return false;

        MonsterDamageType damage = configuredDamage;
        VtankPrismaticAmmoPolicy prismatic =
            VtankPrismaticAmmoPolicy.NoPrismatic;
        if (damage == MonsterDamageType.Auto)
        {
            damage = VtankDamageDatabase.Preferences(
                FindTarget(_targetId)).FirstOrDefault();
            prismatic = VtankPrismaticAmmoPolicy.Any;
        }
        else if (damage == MonsterDamageType.Prismatic)
        {
            prismatic = VtankPrismaticAmmoPolicy.ForcePrismatic;
        }
        if (damage is MonsterDamageType.None
            or MonsterDamageType.VoidBasic
            or MonsterDamageType.DrainAuto
            or MonsterDamageType.Harm
            or MonsterDamageType.Nether)
        {
            return false;
        }

        IReadOnlyList<PluginInventoryItem> inventory =
            _host.Automation.Items.CaptureOwnedItems();
        var counts = inventory
            .GroupBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Sum(item => Math.Max(1, item.StackSize)),
                StringComparer.OrdinalIgnoreCase);
        var craftable = new Dictionary<string, bool>(
            StringComparer.OrdinalIgnoreCase);
        bool IsAvailable(string name)
        {
            if (counts.GetValueOrDefault(name) >= 1)
                return true;
            if (craftable.TryGetValue(name, out bool cached))
                return cached;
            bool value = _canCraftAmmunition?.Invoke(name, 1) == true;
            craftable[name] = value;
            return value;
        }

        VtankAmmunitionOption? selected = VtankAmmunitionDatabase.Select(
            launcherType,
            damage,
            prismatic,
            _settings.UseSpecialAmmo,
            _host.Automation.Character,
            IsAvailable);
        if (selected is not { } option)
        {
            Status = $"No {damage} ammunition is available";
            return true;
        }

        PluginEquipmentItem currentAmmo = equipmentItems.FirstOrDefault(
            static item => item.CombatUse == 3 && item.IsEquipped);
        if (string.Equals(
                currentAmmo.Name,
                option.Name,
                StringComparison.Ordinal))
            return false;

        PluginEquipmentItem desiredAmmo = equipmentItems.FirstOrDefault(
            item => item.Name.Equals(option.Name, StringComparison.Ordinal)
                && item.StackSize > 0);
        if (desiredAmmo.ObjectId != 0u)
            return TryEquipIfNeeded(
                equipment,
                equipmentItems,
                desiredAmmo.ObjectId,
                "ammunition");

        if (_requestAmmunitionCraft?.Invoke(option.Name, 1) == true)
        {
            Status = "Crafting " + option.Name;
            return true;
        }
        Status = $"Waiting to craft {option.Name}";
        return true;
    }

    private static MonsterRuleActions ResolvePhysicalActions(
        MonsterRuleActions actions,
        in PluginCombatTarget target,
        IReadOnlyList<PluginInventoryItem> inventory)
    {
        if (actions.DamageType != MonsterDamageType.Auto)
            return actions;

        IReadOnlyList<MonsterDamageType> preferences =
            VtankDamageDatabase.Preferences(target);
        foreach (MonsterDamageType damage in preferences)
        {
            int mask = RawDamageType(damage);
            if (mask == 0)
                continue;
            foreach (PluginInventoryItem item in inventory)
            {
                if (item.IsEquipped && (item.DamageType & mask) != 0)
                    return actions with { DamageType = damage };
            }
        }
        return preferences.Count == 0
            ? actions
            : actions with { DamageType = preferences[0] };
    }

    private bool TryEquipIfNeeded(
        IEquipmentAutomation equipment,
        IReadOnlyList<PluginEquipmentItem> items,
        uint objectId,
        string role)
    {
        if (objectId == 0u)
            return false;

        PluginEquipmentItem? desired = null;
        foreach (PluginEquipmentItem item in items)
        {
            if (item.ObjectId == objectId)
            {
                desired = item;
                break;
            }
        }
        if (desired is not { } selected || selected.IsEquipped)
            return false;

        PluginCombatMode mode = _host.Automation.Combat.Snapshot.Mode;
        if (mode != PluginCombatMode.Peace)
        {
            _dropToPeaceModeRetries++;
            if (_dropToPeaceModeRetries
                >= _vitalSettings.DropToPeaceModeRetryCount)
            {
                _dropToPeaceModeRetries = 0;
                PluginEquipmentItem? recovery = SelectRecoveryCaster(items);
                if (recovery is not { } caster)
                {
                    const string error = "You must add at least one wand to "
                        + "your Items profile.";
                    Disable(error);
                    _host.Automation.Chat.PostSystemMessage(
                        "[MossTank] " + error);
                    return true;
                }

                PluginItemCommandResult use =
                    _host.Automation.Items.Use(caster.ObjectId);
                Status = use.Status == PluginItemCommandStatus.Started
                    ? "Warning: stuck combat state; using " + caster.Name
                        + " to clear it"
                    : "Combat-state recovery with " + caster.Name + ": "
                        + use.Status;
                return true;
            }

            PluginCombatCommandResult peace =
                _host.Automation.Combat.EnterMode(PluginCombatMode.Peace);
            Status = peace.Status == PluginCombatCommandStatus.Refused
                ? peace.Notice ?? "Cannot enter peace mode to equip "
                    + selected.Name
                : "Entering peace mode to equip " + selected.Name;
            return true;
        }

        _dropToPeaceModeRetries = 0;

        PluginEquipmentCommandResult result = equipment.Equip(objectId);
        if (result.Status is PluginEquipmentCommandStatus.Started
            or PluginEquipmentCommandStatus.Busy)
        {
            Status = $"Equipping {selected.Name}";
            return true;
        }
        if (result.Status == PluginEquipmentCommandStatus.Refused)
            Status = $"Cannot equip {role}: {selected.Name}";
        return false;
    }

    private PluginEquipmentItem? SelectRecoveryCaster(
        IReadOnlyList<PluginEquipmentItem> items)
    {
        const uint casterItemType = 0x00008000u;
        foreach (PluginEquipmentItem item in items)
        {
            if (item.ItemType != casterItemType)
                continue;
            if (_settings.CombatItemObjectIds.Contains(item.ObjectId)
                || _settings.CombatItemNames.Contains(item.Name))
            {
                return item;
            }
        }
        return null;
    }

    private static uint SelectAutomaticWeapon(
        IReadOnlyList<PluginEquipmentItem> items,
        MonsterDamageType damageType,
        CombatSettings settings)
    {
        const uint weaponReadyMask = 0x03500000u;
        int rawDamage = RawDamageType(damageType);
        PluginEquipmentItem? best = null;
        foreach (PluginEquipmentItem item in items)
        {
            if (!settings.CombatItemObjectIds.Contains(item.ObjectId)
                && !settings.CombatItemNames.Contains(item.Name))
            {
                continue;
            }
            if ((item.ValidLocations & weaponReadyMask) == 0u
                || rawDamage == 0
                || (item.DamageType & rawDamage) == 0)
            {
                continue;
            }
            if (best is null
                || item.Damage > best.Value.Damage
                || (item.Damage == best.Value.Damage
                    && item.IsEquipped
                    && !best.Value.IsEquipped))
            {
                best = item;
            }
        }
        return best?.ObjectId ?? 0u;
    }

    private static uint SelectAutomaticWeapon(
        IReadOnlyList<PluginEquipmentItem> items,
        IReadOnlyList<MonsterDamageType> preferences,
        CombatSettings settings)
    {
        foreach (MonsterDamageType damage in preferences)
        {
            uint objectId = SelectAutomaticWeapon(items, damage, settings);
            if (objectId != 0u)
                return objectId;
        }
        return 0u;
    }

    private static int RawDamageType(MonsterDamageType damageType) =>
        damageType switch
        {
            MonsterDamageType.Slash => 0x0001,
            MonsterDamageType.Pierce => 0x0002,
            MonsterDamageType.Bludgeon => 0x0004,
            MonsterDamageType.Cold => 0x0008,
            MonsterDamageType.Fire => 0x0010,
            MonsterDamageType.Acid => 0x0020,
            MonsterDamageType.Electric => 0x0040,
            MonsterDamageType.Nether => 0x0400,
            _ => 0,
        };

    private bool TickDebuffs(PluginCombatSnapshot combat)
    {
        if (_debuffs.HasPending)
        {
            Status = _host.Automation.Magic.IsCasting
                ? $"Casting {_debuffs.PendingName}"
                : $"Waiting for {_debuffs.PendingName}";
            return true;
        }

        RefreshSpellCatalogs();
        IReadOnlyList<PluginInventoryItem> items =
            _host.Automation.Items.CaptureOwnedItems();

        foreach (RuleCandidate candidate in DebuffScope())
        {
            MonsterRuleActions actions = ResolveAutomaticActions(
                candidate.Rule.Actions,
                candidate.Target,
                items);
            IReadOnlyList<CombatDebuffSource> choices =
                CombatItemDebuffPlanner.Candidates(
                actions,
                _settings,
                _host.Automation.Character,
                _host.Automation.Spells,
                items,
                (identity, spell) => _debuffs.IsDue(
                    candidate.Target.ObjectId,
                    identity,
                    spell,
                    _now,
                    _settings.DebuffPrecastSeconds));

            foreach (CombatDebuffSource choice in choices)
            {
                if (SpellComponentPolicy.UsesBlacklistedComponent(
                        _host.Automation.Spells,
                        choice.Spell,
                        _settings.BlacklistedSpellComponents))
                {
                    continue;
                }
                if (!ReadyForBreakableTurn(
                        choice.Spell,
                        candidate.Target.ObjectId))
                {
                    return true;
                }
                if (choice.Spell.IsProjectile
                    && !ProjectilePathIsClear(
                        candidate.Target.ObjectId,
                        choice.Spell.Name.Contains(
                            " Arc",
                            StringComparison.OrdinalIgnoreCase)
                            ? PluginProjectilePathKind.Arc
                            : choice.Kind is CombatDebuffSourceKind.Grenade
                                or CombatDebuffSourceKind.ProcWeapon
                                ? PluginProjectilePathKind.Missile
                                : PluginProjectilePathKind.Straight,
                        PluginAttackHeight.Medium,
                        out PluginProjectilePathResult debuffPath))
                {
                    Status = ProjectileStatus(
                        debuffPath,
                        candidate.Target.Name);
                    if (_settings.AllowDebuffFallback)
                        continue;
                    return true;
                }
                if (choice.Kind != CombatDebuffSourceKind.LearnedSpell)
                {
                    DebuffStartResult itemResult = TryStartItemDebuff(
                        choice,
                        candidate.Target,
                        combat,
                        items,
                        ResolveInventoryObjectId(
                            actions.OffhandObjectId,
                            actions.OffhandName,
                            items));
                    if (itemResult == DebuffStartResult.Handled)
                        return true;
                    continue;
                }

                if (combat.Mode != PluginCombatMode.Magic)
                {
                    EnterDebuffMode(PluginCombatMode.Magic);
                    return true;
                }
                PluginCastGate gate = _host.Automation.Magic.EvaluateGate(
                    choice.Spell.SpellId,
                    candidate.Target.ObjectId);
                if (gate == PluginCastGate.Busy)
                {
                    Status = "Waiting to debuff";
                    return true;
                }
                if (gate != PluginCastGate.Ready
                    || !_host.Automation.Magic.Cast(
                        choice.Spell.SpellId,
                        candidate.Target.ObjectId))
                {
                    continue;
                }

                _debuffs.Begin(
                    candidate.Target.ObjectId,
                    choice.Identity,
                    choice.Spell,
                    _now,
                    _host.Automation.Magic.LastCompletion.Revision);
                string targetName = string.IsNullOrWhiteSpace(candidate.Target.Name)
                    ? $"0x{candidate.Target.ObjectId:X8}"
                    : candidate.Target.Name;
                Status = $"{choice.Spell.Name} → {targetName}";
                return true;
            }
        }

        return false;
    }

    private bool ProjectilePathIsClear(
        uint targetObjectId,
        PluginProjectilePathKind kind,
        PluginAttackHeight height,
        out PluginProjectilePathResult result)
    {
        if (!_settings.UseProjectileAwareness)
        {
            result = new(PluginProjectilePathStatus.Clear);
            return true;
        }
        result = _settings.ShowCollisionDebug
            ? _host.Automation.Projectiles.EvaluatePathWithDiagnostics(
                targetObjectId,
                kind,
                height,
                (float)_settings.CollisionProjectileRadius,
                (float)_settings.CollisionStepDistance,
                _settings.MaximumCollisionChecksPerTick)
            : _host.Automation.Projectiles.EvaluatePath(
                targetObjectId,
                kind,
                height,
                (float)_settings.CollisionProjectileRadius,
                (float)_settings.CollisionStepDistance,
                _settings.MaximumCollisionChecksPerTick);
        if (_settings.ShowCollisionDebug && result.DebugSamples.Count > 0)
        {
            _host.Automation.Projectiles.ShowDebugSamples(result.DebugSamples);
            _host.Log.Info(
                $"MossTank collision {kind}: {result.Status}, "
                + $"{result.DebugSamples.Count} marker(s), "
                + $"{result.CollisionChecks} check(s)");
        }
        return result.IsClear;
    }

    private static string ProjectileStatus(
        in PluginProjectilePathResult result,
        string targetName)
    {
        string target = string.IsNullOrWhiteSpace(targetName)
            ? "target"
            : targetName;
        return result.Status switch
        {
            PluginProjectilePathStatus.Blocked when result.BlockingObjectId != 0u =>
                $"Projectile path to {target} blocked by 0x{result.BlockingObjectId:X8}",
            PluginProjectilePathStatus.Blocked =>
                $"Projectile path to {target} is blocked",
            PluginProjectilePathStatus.Unavailable =>
                "Projectile collision data is unavailable",
            PluginProjectilePathStatus.BudgetExceeded =>
                "Projectile collision-check budget exhausted",
            PluginProjectilePathStatus.InvalidTarget =>
                $"Cannot resolve projectile path to {target}",
            PluginProjectilePathStatus.Error =>
                result.Notice ?? "Projectile collision check failed",
            _ => $"Cannot fire at {target}",
        };
    }

    private MonsterRuleActions ResolveAutomaticActions(
        MonsterRuleActions actions,
        in PluginCombatTarget target,
        IReadOnlyList<PluginInventoryItem> inventory)
    {
        if (actions.DamageType != MonsterDamageType.Auto)
            return actions;

        IReadOnlyList<MonsterDamageType> preferences =
            VtankDamageDatabase.Preferences(target);
        const uint weaponReadyMask = 0x03500000u;
        foreach (MonsterDamageType damage in preferences)
        {
            int rawDamage = RawDamageType(damage);
            foreach (PluginInventoryItem item in inventory)
            {
                bool profiled = _settings.CombatItemObjectIds.Contains(
                        item.ObjectId)
                    || _settings.CombatItemNames.Contains(item.Name);
                if (profiled
                    && (item.ValidLocations & weaponReadyMask) != 0u
                    && (item.DamageType & rawDamage) != 0)
                {
                    return actions with { DamageType = damage };
                }
            }
        }

        ICharacterInfo character = _host.Automation.Character;
        if (IsTrained(character, 34u))
        {
            return preferences.Count == 0
                ? actions
                : actions with { DamageType = preferences[0] };
        }
        if (IsTrained(character, 43u))
            return actions with { DamageType = MonsterDamageType.VoidBasic };
        if (IsTrained(character, 33u))
            return actions with { DamageType = MonsterDamageType.DrainAuto };
        return preferences.Count == 0
            ? actions
            : actions with { DamageType = preferences[0] };
    }

    private static bool IsTrained(ICharacterInfo character, uint skillId) =>
        character.TryGetSkill(skillId, out PluginSkillInfo skill)
        && skill.Training is PluginSkillTraining.Trained
            or PluginSkillTraining.Specialized;

    private DebuffStartResult TryStartItemDebuff(
        CombatDebuffSource source,
        PluginCombatTarget target,
        PluginCombatSnapshot combat,
        IReadOnlyList<PluginInventoryItem> inventory,
        uint desiredOffhand)
    {
        IEquipmentAutomation equipment = _host.Automation.Equipment;
        if (!equipment.IsAvailable)
            return DebuffStartResult.Skipped;
        if (equipment.IsBusy)
        {
            Status = $"Equipping {ItemName(source.ItemObjectId, inventory)}";
            return DebuffStartResult.Handled;
        }

        PluginInventoryItem item = default;
        bool found = false;
        foreach (PluginInventoryItem candidate in inventory)
        {
            if (candidate.ObjectId == source.ItemObjectId)
            {
                item = candidate;
                found = true;
                break;
            }
        }
        if (!found)
            return DebuffStartResult.Skipped;

        if (source.Kind == CombatDebuffSourceKind.Grenade
            && desiredOffhand != 0u)
        {
            IReadOnlyList<PluginEquipmentItem> equipmentItems =
                equipment.CaptureOwnedEquipment();
            PluginEquipmentItem? offhand = null;
            foreach (PluginEquipmentItem candidate in equipmentItems)
            {
                if (candidate.ObjectId == desiredOffhand)
                {
                    offhand = candidate;
                    break;
                }
            }
            if (offhand is { IsEquipped: false } selectedOffhand)
            {
                PluginEquipmentCommandResult offhandResult =
                    equipment.Equip(selectedOffhand.ObjectId);
                if (offhandResult.Accepted
                    || offhandResult.Status == PluginEquipmentCommandStatus.Busy)
                {
                    Status = $"Equipping {selectedOffhand.Name}";
                    return DebuffStartResult.Handled;
                }
                return DebuffStartResult.Skipped;
            }
        }

        if (!item.IsEquipped)
        {
            PluginEquipmentCommandResult equip = equipment.Equip(item.ObjectId);
            if (equip.Status is PluginEquipmentCommandStatus.Started
                or PluginEquipmentCommandStatus.Busy)
            {
                Status = $"Equipping {item.Name}";
                return DebuffStartResult.Handled;
            }
            return DebuffStartResult.Skipped;
        }

        PluginCombatMode desiredMode = source.Kind switch
        {
            CombatDebuffSourceKind.CasterItem => PluginCombatMode.Magic,
            CombatDebuffSourceKind.Grenade => PluginCombatMode.Missile,
            _ when (item.ItemType & 0x00000100u) != 0u =>
                PluginCombatMode.Missile,
            _ => PluginCombatMode.Melee,
        };
        if (combat.Mode != desiredMode)
        {
            EnterDebuffMode(desiredMode);
            return DebuffStartResult.Handled;
        }

        string targetName = string.IsNullOrWhiteSpace(target.Name)
            ? $"0x{target.ObjectId:X8}"
            : target.Name;
        if (source.Kind == CombatDebuffSourceKind.CasterItem)
        {
            IItemAutomation itemCommands = _host.Automation.Items;
            if (!itemCommands.IsAvailable || itemCommands.IsBusy)
            {
                Status = $"Waiting to use {item.Name}";
                return DebuffStartResult.Handled;
            }
            PluginItemCommandResult apply = itemCommands.Apply(
                item.ObjectId,
                target.ObjectId);
            if (!apply.Accepted)
                return DebuffStartResult.Skipped;
            _pendingItemDebuff = new PendingItemDebuff(
                source,
                target.ObjectId,
                targetName,
                item.Name,
                _now,
                itemCommands.LastCompletion.Revision,
                combat.CompletionRevision,
                desiredMode,
                0f);
            Status = $"{source.Spell.Name} via {item.Name} → {targetName}";
            return DebuffStartResult.Handled;
        }

        if (combat.RequestInProgress
            || combat.ServerResponsePending
            || combat.RepeatAttackInProgress)
        {
            Status = $"Waiting to fire {item.Name}";
            return DebuffStartResult.Handled;
        }
        float power = desiredMode == PluginCombatMode.Missile ? 1f : 0f;
        PluginCombatCommandResult begin =
            _host.Automation.Combat.BeginPhysicalAttack(
                target.ObjectId,
                PluginAttackHeight.Medium,
                power);
        if (begin.Status != PluginCombatCommandStatus.Started)
            return begin.Status == PluginCombatCommandStatus.Busy
                ? DebuffStartResult.Handled
                : DebuffStartResult.Skipped;
        _pendingItemDebuff = new PendingItemDebuff(
            source,
            target.ObjectId,
            targetName,
            item.Name,
            _now,
            _host.Automation.Items.LastCompletion.Revision,
            combat.CompletionRevision,
            desiredMode,
            power);
        Status = $"Charging {item.Name} for {targetName}";
        return DebuffStartResult.Handled;
    }

    private static uint ResolveEquipmentObjectId(
        uint sessionObjectId,
        string durableName,
        IReadOnlyList<PluginEquipmentItem> items)
    {
        if (sessionObjectId != 0u
            && items.Any(item => item.ObjectId == sessionObjectId))
        {
            return sessionObjectId;
        }
        if (string.IsNullOrWhiteSpace(durableName))
            return 0u;
        foreach (PluginEquipmentItem item in items)
        {
            if (item.Name.Equals(durableName, StringComparison.Ordinal))
                return item.ObjectId;
        }
        return 0u;
    }

    private static uint ResolveInventoryObjectId(
        uint sessionObjectId,
        string durableName,
        IReadOnlyList<PluginInventoryItem> items)
    {
        if (sessionObjectId != 0u
            && items.Any(item => item.ObjectId == sessionObjectId))
        {
            return sessionObjectId;
        }
        if (string.IsNullOrWhiteSpace(durableName))
            return 0u;
        foreach (PluginInventoryItem item in items)
        {
            if (item.Name.Equals(durableName, StringComparison.Ordinal))
                return item.ObjectId;
        }
        return 0u;
    }

    private void EnterDebuffMode(PluginCombatMode mode)
    {
        PluginCombatCommandResult result =
            _host.Automation.Combat.EnterMode(mode);
        Status = result.Status == PluginCombatCommandStatus.Refused
            ? result.Notice ?? $"Cannot enter {mode} mode"
            : $"Entering {mode} mode";
    }

    private void ClearPendingItemDebuff()
    {
        _pendingItemDebuff = null;
    }

    private void TickPendingItemDebuff(PluginCombatSnapshot combat)
    {
        if (_pendingItemDebuff is not { } pending)
            return;
        if (_now - pending.DispatchedAt >= 15d)
        {
            _host.Automation.Combat.AbortPhysicalAttack();
            Status = $"{pending.Source.Spell.Name} timed out";
            ClearPendingItemDebuff();
            return;
        }
        if (pending.Source.Kind == CombatDebuffSourceKind.CasterItem)
        {
            TickWandCastRecovery(pending);
            Status = $"Waiting for {pending.Source.Spell.Name}";
            return;
        }

        if (combat.RequestInProgress)
        {
            if (combat.BuildInProgress
                && combat.PowerBarLevel + PowerReleaseEpsilon
                    >= pending.Power)
            {
                PluginCombatCommandResult release =
                    _host.Automation.Combat.ReleasePhysicalAttack();
                Status = release.Status == PluginCombatCommandStatus.Released
                    ? $"Firing {pending.ItemName}"
                    : $"Attack release: {release.Status}";
            }
            else
            {
                Status = $"Charging {pending.ItemName}";
            }
            return;
        }
        if (combat.ServerResponsePending || combat.RepeatAttackInProgress)
        {
            Status = $"Waiting for {pending.Source.Spell.Name}";
            return;
        }
        if (combat.CompletionRevision > pending.PhysicalCompletionRevision)
        {
            pending.PhysicalCompletionRevision = combat.CompletionRevision;
            pending.AttackCompletedAt ??= _now;
            if (combat.CompletionWeenieError != 0u)
            {
                Status = $"{pending.ItemName} failed (0x{combat.CompletionWeenieError:X})";
                ClearPendingItemDebuff();
                return;
            }
        }
        if (pending.AttackCompletedAt is { } completed
            && _now - completed >= 1d)
        {
            ClearPendingItemDebuff();
            Status = $"Retrying {pending.Source.Spell.Name}";
            return;
        }
        Status = $"Waiting for {pending.Source.Spell.Name}";
    }

    private void TickWandCastRecovery(PendingItemDebuff pending)
    {
        double age = _now - pending.DispatchedAt;
        INavigationAutomation movement = _host.Automation.Navigation;
        if (_settings.JumpOutWandCasting
            && !pending.RecoverySent
            && age >= 0.2d)
        {
            _ = movement.SetMovementIntent(new PluginMovementIntent(Jump: true));
            _ = movement.ClearMovementIntent();
            pending.RecoverySent = true;
            return;
        }
        if (!_settings.DoJiggle || _settings.JumpOutWandCasting)
            return;
    }

    private void ObserveItemDebuffReceipts()
    {
        foreach (PluginChatMessage message in
            _host.Automation.Chat.CaptureMessages(_observedChatSequence))
        {
            _observedChatSequence = Math.Max(
                _observedChatSequence,
                message.Sequence);
            if (_pendingItemDebuff is not { } pending
                || !IsMatchingCastLine(message.Text, pending.Source.Spell.Name))
            {
                continue;
            }
            _debuffs.RecordApplied(
                pending.TargetObjectId,
                pending.Source.Identity,
                pending.Source.Spell,
                _now);
            _failures.RecordSuccessfulAttack(
                pending.TargetObjectId,
                _now,
                _settings);
            Status = $"{pending.Source.Spell.Name} applied to {pending.TargetName}";
            if (!_settings.JumpOutWandCasting)
                StartSelectionJiggle(pending.Source.Spell);
            ClearPendingItemDebuff();
        }

        PluginItemUseCompletion itemCompletion =
            _host.Automation.Items.LastCompletion;
        if (itemCompletion.Revision <= _observedItemCompletion)
            return;
        _observedItemCompletion = itemCompletion.Revision;
        if (_pendingItemDebuff is not { } itemPending
            || itemPending.Source.Kind != CombatDebuffSourceKind.CasterItem
            || itemCompletion.SourceObjectId != itemPending.Source.ItemObjectId
            || itemCompletion.TargetObjectId != itemPending.TargetObjectId
            || itemCompletion.IsSuccess)
        {
            return;
        }
        Status = $"{itemPending.ItemName} failed (0x{itemCompletion.WeenieError:X})";
        ClearPendingItemDebuff();
    }

    private static bool IsMatchingCastLine(string text, string spellName) =>
        text.StartsWith($"You cast {spellName} on ", StringComparison.Ordinal);

    private void ObserveSelectionJiggle(in PluginCastCompletion completion)
    {
        if (_host.Automation.Magic.IsCasting)
        {
            StopSelectionJiggle();
            return;
        }
        if (completion.Revision <= _observedJiggleCastCompletion)
            return;
        _observedJiggleCastCompletion = completion.Revision;
        if (completion.IsSuccess
            && _host.Automation.Spells.TryGet(
                completion.SpellId,
                out PluginSpellInfo spell))
        {
            StartSelectionJiggle(spell);
        }
    }

    private void StartSelectionJiggle(in PluginSpellInfo spell)
    {
        if (!_settings.DoJiggle
            || (IsVtankInstantCast(spell)
                && spell.School is 34u or 43u))
        {
            return;
        }
        ISelectionAutomation selection = _host.Automation.Selection;
        if (!selection.Execute(PluginSelectionAction.PreviousSelection))
            return;
        _selectionJiggleActive = true;
        _selectionJigglePreviousPlayer = false;
        _nextSelectionJiggleAt = _now;
    }

    private void TickSelectionJiggle()
    {
        if (!_selectionJiggleActive || _now < _nextSelectionJiggleAt)
            return;
        ISelectionAutomation selection = _host.Automation.Selection;
        int pulses = 0;
        do
        {
            PluginSelectionAction action = _selectionJigglePreviousPlayer
                ? PluginSelectionAction.PreviousPlayer
                : PluginSelectionAction.NextPlayer;
            if (!selection.Execute(action))
            {
                StopSelectionJiggle();
                return;
            }
            _selectionJigglePreviousPlayer = !_selectionJigglePreviousPlayer;
            _nextSelectionJiggleAt += 0.131d;
        }
        while (_now >= _nextSelectionJiggleAt && ++pulses < 8);
    }

    private void StopSelectionJiggle()
    {
        _selectionJiggleActive = false;
        _selectionJigglePreviousPlayer = false;
        _nextSelectionJiggleAt = 0d;
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

    private static string ItemName(
        uint objectId,
        IReadOnlyList<PluginInventoryItem> inventory)
    {
        foreach (PluginInventoryItem item in inventory)
        {
            if (item.ObjectId == objectId)
                return item.Name;
        }
        return $"0x{objectId:X8}";
    }

    private void RefreshSpellCatalogs()
    {
        IReadOnlyList<PluginSpellInfo> spells =
            _host.Automation.Spells.KnownCombatSpells;
        if (ReferenceEquals(spells, _combatSpellSnapshot))
            return;
        _combatSpellSnapshot = spells;
        _debuffCatalog = DebuffSpellCatalog.Build(spells);
        _attackCatalog = AttackSpellCatalog.Build(spells);
    }

    private IReadOnlyList<RuleCandidate> DebuffScope()
    {
        if (_settings.DebuffEachFirst == DebuffEachFirst.One)
        {
            return _targetId == 0u
                ? Array.Empty<RuleCandidate>()
                : [new RuleCandidate(
                    FindTarget(_targetId),
                    _targetRule)];
        }

        var candidates = new List<RuleCandidate>();
        foreach (PluginCombatTarget target in _targets)
        {
            if (target.Distance < _settings.MinimumRange)
                continue;
            if (_failures.Reason(target.ObjectId, _now)
                != CombatSuppressionReason.None)
            {
                continue;
            }
            ResolvedMonsterRule rule = _settings.ResolveRule(target);
            if (rule.Priority < 0)
                continue;
            if (_settings.DebuffEachFirst == DebuffEachFirst.Priority
                && rule.Priority != _targetRule.Priority)
            {
                continue;
            }
            candidates.Add(new RuleCandidate(target, rule));
        }
        candidates.Sort(static (left, right) =>
        {
            int priority = right.Rule.Priority.CompareTo(left.Rule.Priority);
            if (priority != 0)
                return priority;
            int distance = left.Target.Distance.CompareTo(right.Target.Distance);
            return distance != 0
                ? distance
                : left.Target.ObjectId.CompareTo(right.Target.ObjectId);
        });
        return candidates;
    }

    private PluginCombatTarget FindTarget(uint objectId)
    {
        foreach (PluginCombatTarget target in _targets)
        {
            if (target.ObjectId == objectId)
                return target;
        }
        return default;
    }

    private void RefreshTarget()
    {
        if (_targetId != 0u
            && _failures.Reason(_targetId, _now)
                != CombatSuppressionReason.None)
        {
            _host.Automation.Combat.AbortPhysicalAttack();
            ClearTarget();
        }

        PluginCombatSnapshot combat = _host.Automation.Combat.Snapshot;
        bool actionInFlight = combat.BuildInProgress
            || combat.RequestInProgress
            || combat.ServerResponsePending
            || combat.RepeatAttackInProgress
            || _host.Automation.Magic.IsCasting;
        if (_targetId != 0u && actionInFlight)
        {
            if (TryFind(_targetId, out PluginCombatTarget active))
                SetTarget(active, _settings.ResolveRule(active));
            else
            {
                _host.Automation.Combat.AbortPhysicalAttack();
                ClearTarget();
            }
            return;
        }

        int highestPriority = -1;
        var candidates = new List<RuleCandidate>();
        foreach (PluginCombatTarget target in _targets)
        {
            if (_failures.Reason(target.ObjectId, _now)
                != CombatSuppressionReason.None)
            {
                continue;
            }
            ResolvedMonsterRule resolved = _settings.ResolveRule(target);
            int priority = resolved.Priority;
            if (priority < 0)
                continue;
            if (priority > highestPriority)
            {
                highestPriority = priority;
                candidates.Clear();
            }
            if (priority == highestPriority)
                candidates.Add(new RuleCandidate(target, resolved));
        }

        if (candidates.Count == 0)
        {
            if (_targetId != 0u)
                _host.Automation.Combat.AbortPhysicalAttack();
            ClearTarget();
            return;
        }

        // Target Lock gives a manually selected valid monster first refusal,
        // but never lets it beat a higher-priority monster rule.
        uint selected = _host.Automation.Combat.Snapshot.SelectedObjectId;
        if (_settings.TargetLock && selected != 0u)
        {
            foreach (RuleCandidate candidate in candidates)
            {
                if (candidate.Target.ObjectId == selected)
                {
                    SetTarget(candidate.Target, candidate.Rule);
                    return;
                }
            }
        }

        if (_targetId != 0u)
        {
            foreach (RuleCandidate candidate in candidates)
            {
                if (candidate.Target.ObjectId == _targetId)
                {
                    SetTarget(candidate.Target, candidate.Rule);
                    return;
                }
            }
        }

        IEnumerable<RuleCandidate> ranked = candidates;
        if (_settings.SelectionMethod == TargetSelectionMethod.Both)
        {
            RuleCandidate[] near = candidates
                .Where(candidate =>
                    candidate.Target.Distance <= _settings.TargetSelectAngleRange)
                .ToArray();
            ranked = near.Length > 0 ? near : candidates;
        }

        RuleCandidate chosen = _settings.SelectionMethod switch
        {
            TargetSelectionMethod.Angle => ranked
                .OrderBy(candidate =>
                    MathF.Abs(candidate.Target.RelativeAngleDegrees))
                .ThenBy(candidate => candidate.Target.Distance)
                .First(),
            TargetSelectionMethod.Both
                when ranked is RuleCandidate[] { Length: > 0 } near => near
                    .OrderBy(candidate =>
                        MathF.Abs(candidate.Target.RelativeAngleDegrees))
                    .ThenBy(candidate => candidate.Target.Distance)
                    .First(),
            _ => ranked
                .OrderBy(candidate => candidate.Target.Distance)
                .ThenBy(candidate =>
                    MathF.Abs(candidate.Target.RelativeAngleDegrees))
                .First(),
        };
        SetTarget(chosen.Target, chosen.Rule);
    }

    private bool TryFind(uint objectId, out PluginCombatTarget found)
    {
        foreach (PluginCombatTarget target in _targets)
        {
            if (target.ObjectId == objectId)
            {
                found = target;
                return true;
            }
        }
        found = default;
        return false;
    }

    private void SetTarget(
        PluginCombatTarget target,
        ResolvedMonsterRule resolved)
    {
        _targetId = target.ObjectId;
        _targetRule = resolved;
        _targetName = string.IsNullOrWhiteSpace(target.Name)
            ? $"0x{target.ObjectId:X8}"
            : target.Name;
        _targetDistance = target.Distance;
        _targetText = $"Target  {_targetName}  {_targetDistance:0.0}m";
        _failures.BeginEngagement(_targetId, _now);
    }

    private void ClearTarget()
    {
        StopApproachMovement();
        StopBreakableTurnMovement();
        StopSelectionJiggle();
        _targetId = 0u;
        _targetRule = default;
        _targetName = string.Empty;
        _targetDistance = 0f;
        _targetText = "Target  —";
    }

    private void Disable(string status)
    {
        _host.Automation.Combat.AbortPhysicalAttack();
        StopApproachMovement();
        StopBreakableTurnMovement();
        Enabled = false;
        _paused = false;
        _combatPolicySuspended = false;
        _targets = Array.Empty<PluginCombatTarget>();
        _combatSpellSnapshot = null;
        _debuffCatalog = DebuffSpellCatalog.Build(Array.Empty<PluginSpellInfo>());
        _attackCatalog = AttackSpellCatalog.Build(Array.Empty<PluginSpellInfo>());
        _debuffs.Reset();
        _failures.Reset();
        _observedPhysicalCompletion = 0;
        _observedAttackCastCompletion = 0;
        _pendingPhysicalTarget = 0u;
        _pendingAttackSpell = 0u;
        _pendingAttackTarget = 0u;
        ClearPendingItemDebuff();
        _observedChatSequence = 0u;
        _observedItemCompletion = 0;
        _dropToPeaceModeRetries = 0;
        _randomDamageIndex = 0;
        _observedJiggleCastCompletion = 0;
        ClearTarget();
        Status = status;
    }

    private bool TickApproach()
    {
        INavigationAutomation navigation = _host.Automation.Navigation;
        PluginNavigationSnapshot self = navigation.Snapshot;
        if (!self.IsAvailable || self.IsPortalSpace
            || !navigation.TryGetObject(
                _targetId,
                out PluginNavigationObject target))
        {
            return false;
        }

        float desired = NavigationController.DesiredHeading(
            self.Position,
            target.Position);
        float delta = NavigationController.SignedHeadingDelta(
            self.Position.HeadingDegrees,
            desired);
        float absolute = MathF.Abs(delta);
        bool turnRight = delta > 4f;
        bool turnLeft = delta < -4f;
        bool forward = absolute <= 4f
            || (_targetDistance > 5f ? absolute <= 45f : absolute <= 15f);
        PluginNavigationCommandStatus result = navigation.SetMovementIntent(
            new PluginMovementIntent(
                Forward: forward,
                TurnLeft: turnLeft,
                TurnRight: turnRight,
                Run: true));
        _approachMovementOwned =
            result == PluginNavigationCommandStatus.Accepted;
        if (_approachMovementOwned)
            Status = $"Approaching {_targetName} ({_targetDistance:0.0}m)";
        return _approachMovementOwned;
    }

    private bool ReadyForBreakableTurn(
        in PluginSpellInfo spell,
        uint targetObjectId)
    {
        if (!_settings.UseBreakableTurnTo
            || !spell.RequiresTurnTo
            || targetObjectId == 0u
            || targetObjectId == _host.Automation.Character.ObjectId)
        {
            StopBreakableTurnMovement();
            return true;
        }

        INavigationAutomation navigation = _host.Automation.Navigation;
        PluginNavigationSnapshot self = navigation.Snapshot;
        if (!self.IsAvailable
            || self.IsPortalSpace
            || !navigation.TryGetObject(
                targetObjectId,
                out PluginNavigationObject target))
        {
            StopBreakableTurnMovement();
            return true;
        }

        float desired = NavigationController.DesiredHeading(
            self.Position,
            target.Position);
        float delta = NavigationController.SignedHeadingDelta(
            self.Position.HeadingDegrees,
            desired);
        if (MathF.Abs(delta) <= 2f)
        {
            StopBreakableTurnMovement();
            return true;
        }

        PluginNavigationCommandStatus result = navigation.SetMovementIntent(
            new PluginMovementIntent(
                TurnLeft: delta < 0f,
                TurnRight: delta > 0f));
        if (result != PluginNavigationCommandStatus.Accepted)
        {
            StopBreakableTurnMovement();
            return true;
        }
        _breakableTurnOwned = true;
        Status = $"Turning to {_targetName} ({delta:+0.0;-0.0}°)";
        return false;
    }

    private void StopBreakableTurnMovement()
    {
        if (!_breakableTurnOwned)
            return;
        _host.Automation.Navigation.ClearMovementIntent();
        _breakableTurnOwned = false;
    }

    private void StopApproachMovement()
    {
        if (!_approachMovementOwned)
            return;
        _ = _host.Automation.Navigation.ClearMovementIntent();
        _approachMovementOwned = false;
    }

    private readonly record struct RuleCandidate(
        PluginCombatTarget Target,
        ResolvedMonsterRule Rule);

    private enum DebuffStartResult
    {
        Skipped,
        Handled,
    }

    private sealed class PendingItemDebuff(
        CombatDebuffSource source,
        uint targetObjectId,
        string targetName,
        string itemName,
        double dispatchedAt,
        long itemCompletionRevision,
        long physicalCompletionRevision,
        PluginCombatMode mode,
        float power)
    {
        public CombatDebuffSource Source { get; } = source;
        public uint TargetObjectId { get; } = targetObjectId;
        public string TargetName { get; } = targetName;
        public string ItemName { get; } = itemName;
        public double DispatchedAt { get; } = dispatchedAt;
        public long ItemCompletionRevision { get; } = itemCompletionRevision;
        public long PhysicalCompletionRevision { get; set; } =
            physicalCompletionRevision;
        public PluginCombatMode Mode { get; } = mode;
        public float Power { get; } = power;
        public double? AttackCompletedAt { get; set; }
        public bool RecoverySent { get; set; }
        public int RecoveryStage { get; set; }
    }

    private void ObserveAttackReceipts(
        PluginCombatSnapshot combat,
        PluginCastCompletion cast)
    {
        if (combat.CompletionRevision > _observedPhysicalCompletion)
        {
            _observedPhysicalCompletion = combat.CompletionRevision;
            if (_pendingPhysicalTarget != 0u
                && combat.CompletionWeenieError == 0u)
            {
                _failures.RecordSuccessfulAttack(
                    _pendingPhysicalTarget,
                    _now,
                    _settings);
            }
            _pendingPhysicalTarget = 0u;
        }

        if (cast.Revision <= _observedAttackCastCompletion)
            return;
        _observedAttackCastCompletion = cast.Revision;
        if (_pendingAttackSpell == cast.SpellId
            && _pendingAttackTarget == cast.TargetObjectId
            && cast.IsSuccess)
        {
            _failures.RecordSuccessfulAttack(
                _pendingAttackTarget,
                _now,
                _settings);
        }
        if (_pendingAttackSpell == cast.SpellId)
        {
            _pendingAttackSpell = 0u;
            _pendingAttackTarget = 0u;
        }
    }

    private void DismissGhost(uint objectId)
    {
        PluginCombatCommandResult result =
            _host.Automation.Combat.DismissGhostTarget(objectId);
        string suffix = result.Accepted ? "deleted" : "ignored";
        _host.Automation.Chat.PostSystemMessage(
            $"[MossTank] Ghost target 0x{objectId:X8} {suffix}.");
        if (_targetId == objectId)
        {
            _host.Automation.Combat.AbortPhysicalAttack();
            ClearTarget();
        }
    }
}
