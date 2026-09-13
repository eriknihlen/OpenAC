using AcDream.Bot.Profiles;
using AcDream.Bot.Spells;
using AcDream.Plugin.Abstractions;

namespace AcDream.Bot.Behaviors;

/// <summary>
/// Keeps the character alive: heals, revitalizes and restores mana when a
/// vital drops below its threshold, with a healing-kit fallback when no heal
/// spell can be cast.
/// </summary>
public sealed class VitalRechargeBehavior(
    SpellSelector spells,
    CastTracker casts,
    Func<VitalSettings> settings) : IBehavior
{
    private const double KitRetrySeconds = 3d;
    private double _kitRetryAfter = double.NegativeInfinity;

    public string Name => "vitals";

    public BehaviorPriority Priority => BehaviorPriority.Survival;

    public bool WantsControl(Blackboard board, out string reason)
    {
        VitalSettings vitals = settings();
        if (board.Vitals.HealthFraction < vitals.HealBelow)
        {
            reason = $"health {board.Vitals.HealthFraction:P0}";
            return true;
        }
        if (board.Vitals.StaminaFraction < vitals.StaminaBelow)
        {
            reason = $"stamina {board.Vitals.StaminaFraction:P0}";
            return true;
        }
        if (board.Vitals.ManaFraction < vitals.ManaBelow)
        {
            reason = $"mana {board.Vitals.ManaFraction:P0}";
            return true;
        }
        reason = string.Empty;
        return false;
    }

    public BehaviorStep Execute(BehaviorContext context)
    {
        Blackboard board = context.Board;
        VitalSettings vitals = settings();

        CastOutcome? outcome = casts.Poll();
        if (casts.HasPendingRequest)
            return BehaviorStep.Continue;
        if (outcome is CastOutcome.Succeeded)
            return BehaviorStep.Done;
        if (board.IsActionPending)
            return BehaviorStep.Continue;

        if (board.Vitals.HealthFraction < vitals.HealBelow)
        {
            if (TryCast(vitals.HealSpell, out PluginCastRequestResult result))
                return BehaviorStep.Continue;
            if (vitals.UseHealingKits && TryUseHealingKit(context))
                return BehaviorStep.Continue;
            return BehaviorStep.Fail($"no way to heal ({result})");
        }
        if (board.Vitals.StaminaFraction < vitals.StaminaBelow)
        {
            return TryCast(vitals.StaminaSpell, out PluginCastRequestResult result)
                ? BehaviorStep.Continue
                : BehaviorStep.Fail($"cannot cast {vitals.StaminaSpell} ({result})");
        }
        if (board.Vitals.ManaFraction < vitals.ManaBelow)
        {
            return TryCast(vitals.ManaSpell, out PluginCastRequestResult result)
                ? BehaviorStep.Continue
                : BehaviorStep.Fail($"cannot cast {vitals.ManaSpell} ({result})");
        }
        return BehaviorStep.Done;
    }

    public void Interrupt(BehaviorContext context) => casts.Clear();

    private bool TryCast(string spellName, out PluginCastRequestResult result)
    {
        result = PluginCastRequestResult.UnknownSpell;
        if (!spells.TryBestKnown(spellName, out PluginSpellInfo spell))
            return false;
        if (casts.IsOnCooldown(spell.SpellId))
        {
            result = PluginCastRequestResult.Unavailable;
            return false;
        }
        result = casts.Request(spell.SpellId);
        return result == PluginCastRequestResult.Sent;
    }

    private bool TryUseHealingKit(BehaviorContext context)
    {
        if (context.Board.Now < _kitRetryAfter)
            return false;
        IItemAutomation items = context.Surface.Items;
        if (!items.IsAvailable || items.IsBusy)
            return false;
        foreach (PluginInventoryItem item in items.CaptureOwnedItems())
        {
            if (item.ObjectClass != PluginObjectClass.HealingKit)
                continue;
            PluginItemCommandResult result = items.Apply(item.ObjectId, context.Board.SelfId);
            _kitRetryAfter = context.Board.Now + KitRetrySeconds;
            if (result.Accepted)
                return true;
        }
        return false;
    }
}
