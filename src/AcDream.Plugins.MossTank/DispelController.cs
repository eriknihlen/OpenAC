using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class DispelController
{
    private const uint EradicateLifeMagicSelf =
        (uint)SpellId.EradicateLifeMagicSelf;
    private const double ActionTimeoutSeconds = 15d;
    private const float AllyDispelRangeMeters = 50f;
    private const uint CreatureEnchantmentSkill = 31u;
    private const uint ArcaneLoreSkill = 14u;
    private const uint DispelProtectionSpell = 3179u;

    private static readonly string[] HighDifficultyItems =
    [
        "Rune of Dispel",
        "Society Gem of Dispelling",
        "Black Market Gem of Dispelling",
    ];

    private static readonly string[] NormalDifficultyItems =
    [
        "Rune of Dispel",
        "Chocolate Gromnie",
        "Condensed Dispel Potion",
        "Gem of Stillness",
    ];

    private readonly IPluginHost _host;
    private readonly VitalSettings _settings;

    /// <summary>
    /// The Items profile, which is what <c>af.cs:84</c>'s
    /// <c>PluginCore.PC.ec</c> scan reads.
    /// </summary>
    private readonly CombatSettings _combatSettings;

    private Pending? _pending;
    private double _pendingSeconds;
    private double _retryDelay;

    public DispelController(
        IPluginHost host,
        VitalSettings settings,
        CombatSettings? combatSettings = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _combatSettings = combatSettings ?? new CombatSettings();
    }

    public string Status { get; private set; } = "Dispel idle";

    private CombatModeGate? _gate;

    internal void BindCombatModeGate(CombatModeGate gate) =>
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    public bool Tick(double elapsedSeconds, bool canAct)
    {
        double elapsed = Math.Max(0d, elapsedSeconds);
        _retryDelay = Math.Max(0d, _retryDelay - elapsed);
        if (ObservePending(elapsed))
            return true;

        IAutomationSurface automation = _host.Automation;
        if (!canAct
            || _retryDelay > 0d
            || !_host.Automation.IsAvailable
            || (!_settings.CastDispelSelf
                && !_settings.UseDispelItems
                && !_settings.UseDispelDrum)
            || automation.Magic.IsCasting
            || automation.Items.IsBusy)
        {
            return false;
        }

        if (_settings.CastDispelSelf
            && TryStartSelfDispel(automation))
        {
            return true;
        }
        if (_settings.UseDispelItems
            && TrySelectDispelItem(automation, out PluginInventoryItem item))
        {
            long revision = automation.Items.LastCompletion.Revision;
            PluginItemCommandResult result = automation.Items.Use(item.ObjectId);
            if (result.Accepted)
            {
                _pending = new Pending(
                    DispelSource.Item,
                    item.ObjectId,
                    item.Name,
                    revision);
                _pendingSeconds = 0d;
                Status = $"Using {item.Name}";
                return true;
            }
            Status = $"Waiting to use {item.Name}";
            return result.Status == PluginItemCommandStatus.Busy;
        }
        if (_settings.UseDispelDrum && TryStartAllyDispel(automation))
            return true;

        Status = "Dispel idle";
        return false;
    }

    public void Reset()
    {
        _pending = null;
        _pendingSeconds = 0d;
        _retryDelay = 0d;
        Status = "Dispel idle";
    }

    private bool TryStartSelfDispel(IAutomationSurface automation)
    {
        if (!automation.Spells.TryGet(
                EradicateLifeMagicSelf,
                out PluginSpellInfo spell)
            || !automation.Spells.IsKnown(EradicateLifeMagicSelf)
            || !HasVulnerabilityAtOrBelow(automation, spell.Difficulty)
            || !automation.Items.CaptureOwnedItems().Any(static item =>
                item.StackSize > 0
                && item.Name.Equals("Chorizite", StringComparison.Ordinal)))
        {
            return false;
        }

        if (_gate is not null
            && !_gate.TryPrepare(PluginCombatMode.Magic))
        {
            Status = _gate.Status;
            return true;
        }
        if (_gate is null && automation.Combat.Snapshot.Mode != PluginCombatMode.Magic)
        {
            PluginCombatCommandResult mode = automation.Combat.EnterMode(
                PluginCombatMode.Magic);
            Status = mode.Accepted
                ? "Switching to Magic for self dispel"
                : "Waiting for Magic mode to self dispel";
            return true;
        }

        uint target = automation.Character.ObjectId;
        PluginCastGate gate = automation.Magic.EvaluateGate(
            EradicateLifeMagicSelf,
            target);
        if (gate != PluginCastGate.Ready)
        {
            Status = "Waiting to cast Eradicate Life Magic Self";
            return true;
        }

        long revision = automation.Magic.LastCompletion.Revision;
        if (!automation.Magic.Cast(EradicateLifeMagicSelf, target))
        {
            Status = "Self dispel was refused";
            _retryDelay = 0.25d;
            return true;
        }
        _pending = new Pending(
            DispelSource.Spell,
            EradicateLifeMagicSelf,
            spell.Name,
            revision);
        _pendingSeconds = 0d;
        Status = $"Casting {spell.Name}";
        return true;
    }

    private bool TrySelectDispelItem(
        IAutomationSurface automation,
        out PluginInventoryItem selected)
    {
        selected = default;
        IReadOnlyList<PluginInventoryItem> inventory =
            automation.Items.CaptureOwnedItems();
        if (HasVulnerabilityAtOrBelow(automation, 400)
            && TryFind(inventory, HighDifficultyItems, out selected))
        {
            return true;
        }
        return HasVulnerabilityAtOrBelow(automation, 350)
            && TryFind(inventory, NormalDifficultyItems, out selected);
    }

    private static bool TryFind(
        IReadOnlyList<PluginInventoryItem> inventory,
        IEnumerable<string> names,
        out PluginInventoryItem selected)
    {
        foreach (string name in names)
        {
            foreach (PluginInventoryItem item in inventory)
            {
                if (item.StackSize > 0
                    && item.Name.Equals(name, StringComparison.Ordinal))
                {
                    selected = item;
                    return true;
                }
            }
        }
        selected = default;
        return false;
    }

    private bool TryStartAllyDispel(IAutomationSurface automation)
    {
        // af.cs:79-82 — `if (m_a.o.n.b(ActionLockType.ItemUse)) return false;`.
        if (automation.Items.IsBusy)
            return false;
        if (!automation.Fellowship.IsInFellowship
            || !TrySelectAwakener(automation, _combatSettings, out PluginInventoryItem drum)
            || !TrySelectAlly(automation, out PluginFellowMember target))
        {
            return false;
        }

        if (_gate is not null)
        {
            if (!_gate.TryPrepare(
                    PluginCombatMode.Magic,
                    overrideItemId: drum.ObjectId,
                    autoSelect: false))
            {
                Status = _gate.Status;
                return true;
            }
        }
        else if (automation.Combat.Snapshot.Mode != PluginCombatMode.Magic)
        {
            PluginCombatCommandResult mode = automation.Combat.EnterMode(
                PluginCombatMode.Magic);
            Status = mode.Accepted
                ? "Switching to Magic for ally dispel"
                : "Waiting for Magic mode to dispel ally";
            return true;
        }

        long revision = automation.Items.LastCompletion.Revision;
        PluginItemCommandResult result = automation.Items.Apply(
            drum.ObjectId,
            target.ObjectId);
        if (!result.Accepted)
        {
            Status = $"Waiting to use {drum.Name} on {target.Name}";
            return result.Status == PluginItemCommandStatus.Busy;
        }
        _pending = new Pending(
            DispelSource.AllyItem,
            drum.ObjectId,
            $"{drum.Name} on {target.Name}",
            revision);
        _pendingSeconds = 0d;
        Status = $"Using {drum.Name} on {target.Name}";
        return true;
    }

    private static bool TrySelectAwakener(
        IAutomationSurface automation,
        CombatSettings combatSettings,
        out PluginInventoryItem selected)
    {
        selected = default;
        if (!automation.Character.TryGetSkill(
                CreatureEnchantmentSkill,
                out PluginSkillInfo creature)
            || !automation.Character.TryGetSkill(
                ArcaneLoreSkill,
                out PluginSkillInfo arcane))
        {
            return false;
        }

        PluginInventoryItem drum = default;
        foreach (PluginInventoryItem item in automation.Items.CaptureOwnedItems())
        {
            if (item.Name is not ("Awakener" or "Attenuated Awakener"))
                continue;
            if (!combatSettings.CombatItemNames.Contains(item.Name))
                continue;
            if (item.ContainerObjectId != automation.Character.ObjectId
                && item.WielderObjectId != automation.Character.ObjectId)
            {
                continue;
            }
            drum = item;
            break;   // af.cs:89
        }
        if (drum.ObjectId == 0u)
            return false;

        bool valid = drum.Name switch
        {
            "Awakener" => creature.Training == PluginSkillTraining.Specialized
                && arcane.Current >= 110u,
            _ => creature.Training
                    is PluginSkillTraining.Trained
                        or PluginSkillTraining.Specialized
                && arcane.Current >= 110u,
        };
        if (!valid)
            return false;
        selected = drum;
        return true;
    }

    private static bool TrySelectAlly(
        IAutomationSurface automation,
        out PluginFellowMember selected)
    {
        selected = default;
        int highestScore = 0;
        foreach (PluginFellowMember member in automation.Fellowship.CaptureMembers())
        {
            if (member.ObjectId == automation.Character.ObjectId
                || member.Distance > AllyDispelRangeMeters)
            {
                continue;
            }
            IReadOnlyList<PluginTrackedEnchantment> tracked =
                automation.Enchantments.Capture(member.ObjectId);
            if (tracked.Any(static enchantment =>
                    enchantment.SpellId == DispelProtectionSpell
                    && enchantment.SecondsRemaining > 0d))
            {
                continue;
            }

            var qualities = new Dictionary<MonsterDamageType, int>();
            foreach (PluginTrackedEnchantment enchantment in tracked)
            {
                if (enchantment.SecondsRemaining <= 0d
                    || enchantment.IsUntargeted
                    || !automation.Spells.TryGet(
                        enchantment.SpellId,
                        out PluginSpellInfo spell)
                    || spell.Difficulty > 350
                    || !DebuffSpellCatalog.TryClassify(
                        spell,
                        out DebuffIdentity identity,
                        out _)
                    || identity.Flag != MonsterActionFlags.Vulnerability
                    || identity.DamageType == MonsterDamageType.Auto)
                {
                    continue;
                }
                int quality = enchantment.Quality;
                if (!qualities.TryGetValue(identity.DamageType, out int old)
                    || quality > old)
                {
                    qualities[identity.DamageType] = quality;
                }
            }

            int score = qualities.Values.Where(static quality => quality > 250).Sum();
            if (score <= highestScore)
                continue;
            highestScore = score;
            selected = member;
        }
        return selected.ObjectId != 0u;
    }

    private static bool HasVulnerabilityAtOrBelow(
        IAutomationSurface automation,
        int maximumDifficulty)
    {
        foreach (PluginActiveEnchantment active
            in automation.Character.ActiveEnchantments)
        {
            if (active.SecondsRemaining < 0d
                || !automation.Spells.TryGet(active.SpellId, out PluginSpellInfo spell)
                || spell.Difficulty > maximumDifficulty
                || spell.IsUntargeted
                || !DebuffSpellCatalog.TryClassify(
                    spell,
                    out DebuffIdentity identity,
                    out _)
                || identity.Flag != MonsterActionFlags.Vulnerability)
            {
                continue;
            }
            return true;
        }
        return false;
    }

    private bool ObservePending(double elapsedSeconds)
    {
        if (_pending is not { } pending)
            return false;
        _pendingSeconds += elapsedSeconds;

        if (pending.Source == DispelSource.Spell)
        {
            PluginCastCompletion completion = _host.Automation.Magic.LastCompletion;
            if (completion.Revision > pending.Revision)
            {
                pending.Revision = completion.Revision;
                if (completion.SpellId == pending.ObjectId)
                    return Finish(completion.IsSuccess, completion.WeenieError);
            }
        }
        else
        {
            PluginItemUseCompletion completion = _host.Automation.Items.LastCompletion;
            if (completion.Revision > pending.Revision)
            {
                pending.Revision = completion.Revision;
                if (completion.SourceObjectId == pending.ObjectId)
                    return Finish(completion.IsSuccess, completion.WeenieError);
            }
        }

        if (_pendingSeconds < ActionTimeoutSeconds)
            return true;
        Status = $"Dispel timed out: {pending.Name}";
        ClearPending();
        return true;
    }

    private bool Finish(bool succeeded, uint weenieError)
    {
        string name = _pending?.Name ?? "dispel";
        Status = succeeded
            ? $"Dispel completed: {name}"
            : $"Dispel failed (0x{weenieError:X}): {name}";
        ClearPending();
        return true;
    }

    private void ClearPending()
    {
        _pending = null;
        _pendingSeconds = 0d;
        _retryDelay = 0.25d;
    }

    private enum DispelSource
    {
        Spell,
        Item,
        AllyItem,
    }

    private sealed class Pending(
        DispelSource source,
        uint objectId,
        string name,
        long revision)
    {
        public DispelSource Source { get; } = source;
        public uint ObjectId { get; } = objectId;
        public string Name { get; } = name;
        public long Revision { get; set; } = revision;
    }
}
