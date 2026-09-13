using AcDream.Bot.Profiles;
using AcDream.Bot.Spells;
using AcDream.Plugin.Abstractions;

namespace AcDream.Bot.Behaviors;

/// <summary>
/// Keeps the configured self buffs up. A buff is due when no enchantment of
/// its family is active or the active one is about to expire. One cast per
/// step; the engine re-arbitrates between casts so a fight can interrupt.
/// </summary>
public sealed class SelfBuffBehavior(
    SpellSelector spells,
    CastTracker casts,
    Func<BuffSettings> settings) : IBehavior
{
    private bool _forceRebuff;
    private readonly HashSet<uint> _forcedFamiliesDone = [];
    private uint _pendingFamily;

    public string Name => "buffs";

    /// <summary>Recast every configured buff once, regardless of time remaining.</summary>
    public void ForceRebuff()
    {
        _forceRebuff = true;
        _forcedFamiliesDone.Clear();
    }

    public bool IsForceRebuffPending => _forceRebuff;

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
                _forcedFamiliesDone.Add(_pendingFamily);
            return BehaviorStep.Done;
        }
        if (board.IsActionPending)
            return BehaviorStep.Continue;

        if (!TryNextDue(board, buffs, out PluginSpellInfo spell))
            return BehaviorStep.Done;
        _pendingFamily = spell.Family;
        PluginCastRequestResult result = casts.Request(spell.SpellId);
        return result == PluginCastRequestResult.Sent
            ? BehaviorStep.Continue
            : BehaviorStep.Fail($"{spell.Name}: {result}");
    }

    public void Interrupt(BehaviorContext context) => casts.Clear();

    /// <summary>The first configured buff that is missing or expiring and can be cast now.</summary>
    internal bool TryNextDue(Blackboard board, BuffSettings buffs, out PluginSpellInfo spell)
    {
        spell = default;
        foreach (string name in buffs.Spells)
        {
            if (!spells.TryBestSelfBuff(name, out PluginSpellInfo candidate))
                continue;
            if (casts.IsOnCooldown(candidate.SpellId))
                continue;
            bool forced = _forceRebuff && !_forcedFamiliesDone.Contains(candidate.Family);
            if (!forced && IsCovered(board.Enchantments, candidate, buffs.RebuffWhenRemainingSeconds))
                continue;
            spell = candidate;
            return true;
        }
        // Nothing left to force: the pass is over.
        _forceRebuff = false;
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
