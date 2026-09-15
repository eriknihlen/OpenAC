using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Spells;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Behaviors;

/// <summary>
/// Keeps the configured self buffs up. A buff is due when no enchantment of
/// its family is active or the active one is about to expire. One cast per
/// step; the engine re-arbitrates between casts so a fight can interrupt.
/// Weapon auras are self-casts that land on the wielded weapon; armor
/// spells are cast on each equipped piece and judged by the client's record
/// of what the character landed on it, since item enchantments are not in
/// the player's own registry.
/// </summary>
public sealed class SelfBuffBehavior(
    SpellSelector spells,
    CastTracker casts,
    Func<BuffSettings> settings,
    IAutomationSurface? surface = null) : IBehavior
{
    private bool _forceRebuff;
    private readonly HashSet<uint> _forcedFamiliesDone = [];
    private uint _pendingFamily;
    private uint _pendingTarget;

    public string Name => "buffs";

    /// <summary>Recast every configured buff once, regardless of time remaining.</summary>
    public void ForceRebuff()
    {
        _forceRebuff = true;
        _forcedFamiliesDone.Clear();
    }

    public bool IsForceRebuffPending => _forceRebuff;

    /// <summary>Drops a forced rebuff that has not finished; what is already recast stays.</summary>
    public void CancelForceRebuff()
    {
        _forceRebuff = false;
        _forcedFamiliesDone.Clear();
    }

    public BehaviorPriority Priority => BehaviorPriority.Buffing;

    public bool WantsControl(Blackboard board, out string reason)
    {
        reason = string.Empty;
        BuffSettings buffs = settings();
        if (!buffs.Enabled)
            return false;
        if (casts.HasPendingRequest)
        {
            reason = "finishing cast";
            return true;
        }
        if (TryNextDue(board, buffs, out PluginSpellInfo spell))
        {
            reason = $"{spell.Name} due";
            return true;
        }
        return false;
    }

    public BehaviorStep Execute(BehaviorContext context)
    {
        Blackboard board = context.Board;
        BuffSettings buffs = settings();

        CastOutcome? outcome = casts.Poll();
        if (casts.HasPendingRequest)
            return BehaviorStep.Continue;
        if (outcome is not null)
        {
            if (outcome == CastOutcome.Succeeded && _forceRebuff)
                _forcedFamiliesDone.Add(_pendingTarget == 0u ? _pendingFamily : FamilyOn(_pendingFamily, _pendingTarget));
            return BehaviorStep.Done;
        }
        if (board.IsActionPending)
            return BehaviorStep.Continue;

        if (!TryNextDue(board, buffs, out PluginSpellInfo spell, out uint target))
            return BehaviorStep.Done;
        _pendingFamily = spell.Family;
        _pendingTarget = target;
        PluginCastRequestResult result = casts.Request(spell.SpellId, target);
        return result == PluginCastRequestResult.Sent
            ? BehaviorStep.Continue
            : BehaviorStep.Fail($"{spell.Name}: {result}");
    }

    public void Interrupt(BehaviorContext context) => casts.Clear();

    /// <summary>
    /// One line per configured buff: what would be cast and whether it is
    /// due, or what stands in the way. For the /drakbot spells command.
    /// </summary>
    public IEnumerable<string> Describe(Blackboard board)
    {
        BuffSettings buffs = settings();
        IReadOnlyList<PluginActiveEnchantment> registry = board.Enchantments;
        IReadOnlyList<PluginTrackedEnchantment> landed = surface?.Enchantments.Capture(board.SelfId) ?? [];
        foreach (string name in buffs.Spells)
        {
            string why = spells.Explain(name);
            if (!spells.TryBestSelfBuff(name, out PluginSpellInfo spell) && !spells.TryBestKnown(name, out spell))
            {
                yield return $"{name}: {why}";
                continue;
            }
            double remaining = double.NaN;
            foreach (PluginActiveEnchantment active in registry)
            {
                if (active.Family == spell.Family && (double.IsNaN(remaining) || active.SecondsRemaining > remaining))
                    remaining = active.SecondsRemaining;
            }
            foreach (PluginTrackedEnchantment tracked in landed)
            {
                if (tracked.Family == spell.Family && (double.IsNaN(remaining) || tracked.SecondsRemaining > remaining))
                    remaining = tracked.SecondsRemaining;
            }
            string state = double.IsNaN(remaining)
                ? "not up: due"
                : remaining > buffs.RebuffWhenRemainingSeconds
                    ? $"up, {remaining / 60d:0.0} min left"
                    : $"up, {remaining:0}s left: due";
            yield return $"{name}: {spell.Name}, {state}{(casts.IsOnCooldown(spell.SpellId) ? ", cooling down" : string.Empty)}";
        }
    }

    /// <summary>Whether any configured buff is missing or expiring, for a meta's NeedToBuff.</summary>
    public bool NeedsAnyBuff(Blackboard board) => TryNextDue(board, settings(), out _, out _);

    internal bool TryNextDue(Blackboard board, BuffSettings buffs, out PluginSpellInfo spell) =>
        TryNextDue(board, buffs, out spell, out _);

    /// <summary>
    /// The first configured buff that is missing or expiring and can be
    /// cast now, with the item it is cast on (zero for the character).
    /// </summary>
    internal bool TryNextDue(Blackboard board, BuffSettings buffs, out PluginSpellInfo spell, out uint target)
    {
        target = 0u;
        foreach (string name in buffs.Spells)
        {
            if (TryDueSelf(board, buffs, name, out spell))
                return true;
        }
        if (buffs.BuffWeapon)
        {
            foreach (string name in buffs.WeaponSpells)
            {
                if (TryDueSelf(board, buffs, name, out spell))
                    return true;
            }
        }
        if (buffs.BuffArmor && surface is not null && TryDueArmor(board, buffs, out spell, out target))
            return true;
        // Nothing left to force: the pass is over.
        _forceRebuff = false;
        spell = default;
        return false;
    }

    private bool TryDueSelf(Blackboard board, BuffSettings buffs, string name, out PluginSpellInfo spell)
    {
        spell = default;
        if (!spells.TryBestSelfBuff(name, out PluginSpellInfo candidate) && !spells.TryBestKnown(name, out candidate))
            return false;
        if (casts.IsOnCooldown(candidate.SpellId))
            return false;
        bool forced = _forceRebuff && !_forcedFamiliesDone.Contains(candidate.Family);
        if (!forced && IsCovered(board.Enchantments, candidate, buffs.RebuffWhenRemainingSeconds))
            return false;
        // An aura the registry does not list may still be on record as landed on the character.
        if (!forced && surface is not null
            && IsCovered(surface.Enchantments.Capture(board.SelfId), candidate, buffs.RebuffWhenRemainingSeconds))
            return false;
        spell = candidate;
        return true;
    }

    private bool TryDueArmor(Blackboard board, BuffSettings buffs, out PluginSpellInfo spell, out uint target)
    {
        spell = default;
        target = 0u;
        foreach (PluginInventoryItem item in surface!.Items.CaptureOwnedItems())
        {
            if (!item.IsEquipped || item.ObjectClass != PluginObjectClass.Armor)
                continue;
            IReadOnlyList<PluginTrackedEnchantment>? landed = null;
            foreach (string name in buffs.ArmorSpells)
            {
                if (!spells.TryBestKnown(name, out PluginSpellInfo candidate) || casts.IsOnCooldown(candidate.SpellId))
                    continue;
                bool forced = _forceRebuff && !_forcedFamiliesDone.Contains(FamilyOn(candidate.Family, item.ObjectId));
                landed ??= surface.Enchantments.Capture(item.ObjectId);
                if (!forced && IsCovered(landed, candidate, buffs.RebuffWhenRemainingSeconds))
                    continue;
                spell = candidate;
                target = item.ObjectId;
                return true;
            }
        }
        return false;
    }

    /// <summary>A forced pass tracks armor spells per piece, not per family.</summary>
    private static uint FamilyOn(uint family, uint objectId) => unchecked(family * 2654435761u ^ objectId);

    private static bool IsCovered(
        IReadOnlyList<PluginTrackedEnchantment> landed,
        in PluginSpellInfo spell,
        double rebuffWhenRemaining)
    {
        foreach (PluginTrackedEnchantment enchantment in landed)
        {
            if (enchantment.Family == spell.Family && enchantment.SecondsRemaining > rebuffWhenRemaining)
                return true;
        }
        return false;
    }

    private static bool IsCovered(
        IReadOnlyList<PluginActiveEnchantment> active,
        in PluginSpellInfo spell,
        double rebuffWhenRemaining)
    {
        foreach (PluginActiveEnchantment enchantment in active)
        {
            if (enchantment.Family != spell.Family)
                continue;
            // A higher tier already in force outranks what we can cast.
            if (enchantment.Tier > spell.Tier)
                return true;
            if (enchantment.SecondsRemaining > rebuffWhenRemaining)
                return true;
        }
        return false;
    }
}
