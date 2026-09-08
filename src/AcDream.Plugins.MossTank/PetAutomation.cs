using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal enum PetAutomationActionKind
{
    None,
    Refill,
    Summon,
}

internal readonly record struct PetAutomationChoice(
    PetAutomationActionKind Kind,
    PluginInventoryItem Device,
    PluginInventoryItem Tool,
    PluginCombatTarget Target,
    MonsterDamageType DamageType)
{
    public static PetAutomationChoice None => default;
}

internal sealed class PetAutomation
{
    private const double RetailPetCooldownSeconds = 45d;
    private const double RefusalRetrySeconds = 1d;

    private long _observedCompletionRevision;
    private uint _pendingSourceId;
    private PetAutomationActionKind _pendingKind;
    private double _nextSummonAt;
    private double _nextRefillAt;

    public bool Tick(
        IItemAutomation automation,
        ICharacterInfo character,
        IReadOnlyList<PluginCombatTarget> targets,
        CombatSettings settings,
        double now,
        out string status)
    {
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(settings);

        ObserveCompletion(automation.LastCompletion, now, out string? completion);
        if (completion is not null)
            status = completion;
        else
            status = string.Empty;

        if (_pendingSourceId != 0u)
        {
            status = _pendingKind == PetAutomationActionKind.Refill
                ? "Refilling combat pet"
                : "Summoning combat pet";
            return true;
        }
        if (!settings.SummonPets || !automation.IsAvailable)
            return false;
        if (automation.IsBusy)
        {
            status = "Waiting to use combat pet";
            return true;
        }

        IReadOnlyList<PluginInventoryItem> items = automation.CaptureOwnedItems();
        PetAutomationChoice choice = Select(
            items,
            targets,
            character,
            settings,
            automation.ActiveOwnedPetCount,
            now >= _nextRefillAt,
            now >= _nextSummonAt);
        if (choice.Kind == PetAutomationActionKind.None)
            return false;

        PluginItemCommandResult result = choice.Kind == PetAutomationActionKind.Refill
            ? automation.Apply(choice.Tool.ObjectId, choice.Device.ObjectId)
            : automation.Use(choice.Device.ObjectId);
        if (result.Status == PluginItemCommandStatus.Started)
        {
            _pendingSourceId = choice.Kind == PetAutomationActionKind.Refill
                ? choice.Tool.ObjectId
                : choice.Device.ObjectId;
            _pendingKind = choice.Kind;
            status = choice.Kind == PetAutomationActionKind.Refill
                ? $"Refilling {choice.Device.Name}"
                : $"Summoning {choice.Device.Name} for {choice.Target.Name}";
            return true;
        }

        if (choice.Kind == PetAutomationActionKind.Refill)
            _nextRefillAt = now + RefusalRetrySeconds;
        else
            _nextSummonAt = now + RefusalRetrySeconds;
        status = result.Notice
            ?? $"Combat pet action refused: {result.Status}";
        return true;
    }

    internal static PetAutomationChoice Select(
        IReadOnlyList<PluginInventoryItem> items,
        IReadOnlyList<PluginCombatTarget> targets,
        ICharacterInfo character,
        CombatSettings settings,
        int activeOwnedPetCount,
        bool allowRefill,
        bool allowSummon)
    {
        if (!settings.SummonPets || activeOwnedPetCount > 0)
            return PetAutomationChoice.None;

        float range = (float)(settings.PetRangeMode == PetRangeMode.Custom
            ? settings.PetCustomRange
            : settings.MaximumRange);
        int density = Math.Max(1, settings.PetMonsterDensity);
        var eligible = new List<(PluginCombatTarget Target, ResolvedMonsterRule Rule)>();
        foreach (PluginCombatTarget target in targets)
        {
            if (target.Distance > range)
                continue;
            ResolvedMonsterRule rule = settings.ResolveRule(target);
            if (rule.Priority < 0
                || rule.Actions.PetDamageType == MonsterDamageType.None)
            {
                continue;
            }
            eligible.Add((target, rule));
        }
        if (eligible.Count < density)
            return PetAutomationChoice.None;

        eligible.Sort(static (left, right) =>
        {
            int priority = right.Rule.Priority.CompareTo(left.Rule.Priority);
            return priority != 0
                ? priority
                : left.Target.Distance.CompareTo(right.Target.Distance);
        });
        (PluginCombatTarget selectedTarget, ResolvedMonsterRule targetRule) = eligible[0];
        MonsterDamageType desired = ResolveDesiredDamage(targetRule.Actions);

        PluginInventoryItem? device = SelectDevice(
            items,
            character,
            desired,
            settings,
            allowFallback: targetRule.Actions.PetDamageType
                == MonsterDamageType.PlayerAuto);
        if (device is not { } selected)
            return PetAutomationChoice.None;

        int refillThreshold = Math.Max(0, settings.PetRefillCountNormal);
        if (allowRefill
            && selected.MaximumStructure > 0
            && selected.Structure <= refillThreshold
            && selected.Structure < selected.MaximumStructure
            && FindSpirit(items) is { } spirit)
        {
            return new PetAutomationChoice(
                PetAutomationActionKind.Refill,
                selected,
                spirit,
                selectedTarget,
                desired);
        }
        if (!allowSummon || selected.Structure <= 0)
            return PetAutomationChoice.None;
        return new PetAutomationChoice(
            PetAutomationActionKind.Summon,
            selected,
            default,
            selectedTarget,
            desired);
    }

    private static MonsterDamageType ResolveDesiredDamage(
        MonsterRuleActions actions)
    {
        if (actions.PetDamageType != MonsterDamageType.PlayerAuto)
            return actions.PetDamageType;
        return actions.DamageType is
            MonsterDamageType.Bludgeon or MonsterDamageType.Acid
            or MonsterDamageType.Fire or MonsterDamageType.Cold
            or MonsterDamageType.Electric
            ? actions.DamageType
            : MonsterDamageType.Auto;
    }

    private static PluginInventoryItem? SelectDevice(
        IReadOnlyList<PluginInventoryItem> items,
        ICharacterInfo character,
        MonsterDamageType desired,
        CombatSettings settings,
        bool allowFallback)
    {
        PluginInventoryItem? exact = null;
        PluginInventoryItem? fallback = null;
        foreach (PluginInventoryItem item in items)
        {
            if (!settings.CombatItemObjectIds.Contains(item.ObjectId)
                && !settings.CombatItemNames.Contains(item.Name))
            {
                continue;
            }
            if (!item.IsPetDevice || !CanUse(item, character))
                continue;
            MonsterDamageType damage = PetDeviceCatalog.DamageType(
                item.WeenieClassId);
            if (fallback is null || Better(item, fallback.Value))
                fallback = item;
            if (desired != MonsterDamageType.Auto && damage != desired)
                continue;
            if (exact is null || Better(item, exact.Value))
                exact = item;
        }
        if (exact is not null)
            return exact;
        return desired == MonsterDamageType.Auto || allowFallback
            ? fallback
            : null;
    }

    private static bool CanUse(
        in PluginInventoryItem item,
        ICharacterInfo character)
    {
        if (item.SummoningMastery != 0
            && item.SummoningMastery != character.SummoningMastery)
        {
            return false;
        }
        if (item.UseRequiresSkill == 0)
            return true;
        if (!character.TryGetSkill((uint)item.UseRequiresSkill, out PluginSkillInfo skill)
            || skill.Current < item.UseRequiresSkillLevel)
        {
            return false;
        }
        return item.UseRequiresSkillSpecialized == 0
            || skill.Training == PluginSkillTraining.Specialized;
    }

    private static bool Better(
        in PluginInventoryItem candidate,
        in PluginInventoryItem incumbent)
    {
        int candidateRating = candidate.GearDamage
            + candidate.GearCriticalChance
            + candidate.GearCriticalDamage;
        int incumbentRating = incumbent.GearDamage
            + incumbent.GearCriticalChance
            + incumbent.GearCriticalDamage;
        if (candidate.UseRequiresSkillLevel != incumbent.UseRequiresSkillLevel)
            return candidate.UseRequiresSkillLevel > incumbent.UseRequiresSkillLevel;
        if (candidateRating != incumbentRating)
            return candidateRating > incumbentRating;
        if (candidate.Structure != incumbent.Structure)
            return candidate.Structure > incumbent.Structure;
        return candidate.ObjectId < incumbent.ObjectId;
    }

    private static PluginInventoryItem? FindSpirit(
        IReadOnlyList<PluginInventoryItem> items)
    {
        foreach (PluginInventoryItem item in items)
        {
            if (item.WeenieClassId == PetDeviceCatalog.EncapsulatedSpiritWeenieClassId
                && item.StackSize > 0)
            {
                return item;
            }
        }
        return null;
    }

    private void ObserveCompletion(
        PluginItemUseCompletion completion,
        double now,
        out string? status)
    {
        status = null;
        if (completion.Revision == 0
            || completion.Revision == _observedCompletionRevision)
        {
            return;
        }
        _observedCompletionRevision = completion.Revision;
        if (_pendingSourceId == 0u
            || completion.SourceObjectId != _pendingSourceId)
        {
            return;
        }

        PetAutomationActionKind completed = _pendingKind;
        _pendingSourceId = 0u;
        _pendingKind = PetAutomationActionKind.None;
        if (completed == PetAutomationActionKind.Summon)
            _nextSummonAt = now + RetailPetCooldownSeconds;
        else
            _nextRefillAt = now + RefusalRetrySeconds;
        status = completion.IsSuccess
            ? completed == PetAutomationActionKind.Summon
                ? "Combat pet summoned"
                : "Combat pet refilled"
            : $"Combat pet failed (0x{completion.WeenieError:X})";
    }
}
