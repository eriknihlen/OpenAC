using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Spells;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Behaviors;

/// <summary>
/// Keeps the character alive: heals, revitalizes and restores mana when a
/// vital drops below its threshold, with a healing-kit fallback when no heal
/// spell can be cast. The thresholds come in two tiers the way RynthAi's do:
/// the fight ones always apply, and with no hostile in range the higher idle
/// ones top the character up before the next fight. Fellows in range are
/// healed too when the profile asks.
/// </summary>
public sealed class VitalRechargeBehavior(
    SpellSelector spells,
    CastTracker casts,
    Func<VitalSettings> settings,
    IFellowshipAutomation? fellowship = null) : IBehavior
{
    private const double KitRetrySeconds = 3d;
    private double _kitRetryAfter = double.NegativeInfinity;

    /// <summary>
    /// How long a vital that could not be topped up - the spell unknown,
    /// no kit, the cast refused - is left alone before it is tried again.
    /// Without it a melee character at low mana with no mana spell asked
    /// for control sixty times a second and nothing else ever ran.
    /// </summary>
    private const double FailRetrySeconds = 15d;

    private enum Vital { Health, Stamina, Mana, Fellow }

    private readonly double[] _retryAfter = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
    private readonly string?[] _lastFailure = new string?[4];

    public string Name => "vitals";

    public BehaviorPriority Priority => BehaviorPriority.Survival;

    public bool WantsControl(Blackboard board, out string reason)
    {
        VitalSettings vitals = settings();
        if (Wanted(Vital.Health, board) && board.Vitals.HealthFraction < HealBelow(board, vitals))
        {
            reason = $"health {board.Vitals.HealthFraction:P0}";
            return true;
        }
        if (Wanted(Vital.Stamina, board) && board.Vitals.StaminaFraction < StaminaBelow(board, vitals))
        {
            reason = $"stamina {board.Vitals.StaminaFraction:P0}";
            return true;
        }
        if (Wanted(Vital.Mana, board) && board.Vitals.ManaFraction < ManaBelow(board, vitals))
        {
            reason = $"mana {board.Vitals.ManaFraction:P0}";
            return true;
        }
        if (Wanted(Vital.Fellow, board) && TryFindHurtFellow(board, vitals, out PluginFellowMember fellow))
        {
            reason = $"{fellow.Name} at {Fraction(fellow):P0}";
            return true;
        }
        reason = string.Empty;
        return false;
    }

    private bool Wanted(Vital vital, Blackboard board) => board.Now >= _retryAfter[(int)vital];

    /// <summary>
    /// A vital that could not be seen to is left alone for a while. The
    /// first time is a failure worth a line; the same failure again, once
    /// the wait is over, is not news and is passed over quietly.
    /// </summary>
    private BehaviorStep GiveUp(Vital vital, Blackboard board, string reason)
    {
        _retryAfter[(int)vital] = board.Now + FailRetrySeconds;
        bool repeated = string.Equals(_lastFailure[(int)vital], reason, StringComparison.Ordinal);
        _lastFailure[(int)vital] = reason;
        return repeated ? BehaviorStep.Done : BehaviorStep.Fail($"{reason}; not tried again for {FailRetrySeconds:0}s");
    }

    private void Recovered(Vital vital) => _lastFailure[(int)vital] = null;

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

        if (Wanted(Vital.Health, board) && board.Vitals.HealthFraction < HealBelow(board, vitals))
        {
            if (TryCast(vitals.HealSpell, 0u, out PluginCastRequestResult result))
            {
                Recovered(Vital.Health);
                return BehaviorStep.Continue;
            }
            if (vitals.UseHealingKits && TryUseHealingKit(context))
            {
                Recovered(Vital.Health);
                return BehaviorStep.Continue;
            }
            return GiveUp(Vital.Health, board, $"no way to heal ({result})");
        }
        if (Wanted(Vital.Stamina, board) && board.Vitals.StaminaFraction < StaminaBelow(board, vitals))
        {
            if (!TryCast(vitals.StaminaSpell, 0u, out PluginCastRequestResult result))
                return GiveUp(Vital.Stamina, board, $"cannot cast {vitals.StaminaSpell} ({result})");
            Recovered(Vital.Stamina);
            return BehaviorStep.Continue;
        }
        if (Wanted(Vital.Mana, board) && board.Vitals.ManaFraction < ManaBelow(board, vitals))
        {
            if (!TryCast(vitals.ManaSpell, 0u, out PluginCastRequestResult result))
                return GiveUp(Vital.Mana, board, $"cannot cast {vitals.ManaSpell} ({result})");
            Recovered(Vital.Mana);
            return BehaviorStep.Continue;
        }
        if (Wanted(Vital.Fellow, board) && TryFindHurtFellow(board, vitals, out PluginFellowMember fellow))
        {
            if (!TryCast(vitals.HealOtherSpell, fellow.ObjectId, out PluginCastRequestResult result))
                return GiveUp(Vital.Fellow, board, $"cannot cast {vitals.HealOtherSpell} on {fellow.Name} ({result})");
            Recovered(Vital.Fellow);
            return BehaviorStep.Continue;
        }
        return BehaviorStep.Done;
    }

    public void Interrupt(BehaviorContext context) => casts.Clear();

    private static bool IsIdle(Blackboard board)
    {
        foreach (PluginCombatTarget hostile in board.Hostiles)
        {
            if (!(hostile.IsHealthKnown && hostile.HealthFraction <= 0f))
                return false;
        }
        return true;
    }

    private static double HealBelow(Blackboard board, VitalSettings vitals) =>
        IsIdle(board) ? Math.Max(vitals.HealBelow, vitals.IdleHealthBelow) : vitals.HealBelow;

    private static double StaminaBelow(Blackboard board, VitalSettings vitals) =>
        IsIdle(board) ? Math.Max(vitals.StaminaBelow, vitals.IdleStaminaBelow) : vitals.StaminaBelow;

    private static double ManaBelow(Blackboard board, VitalSettings vitals) =>
        IsIdle(board) ? Math.Max(vitals.ManaBelow, vitals.IdleManaBelow) : vitals.ManaBelow;

    private static double Fraction(in PluginFellowMember fellow) =>
        fellow.MaxHealth == 0u ? 1d : (double)fellow.CurrentHealth / fellow.MaxHealth;

    /// <summary>The hurt fellow in range with the least health, when fellows are healed at all.</summary>
    private bool TryFindHurtFellow(Blackboard board, VitalSettings vitals, out PluginFellowMember fellow)
    {
        fellow = default;
        if (vitals.HealFellowsBelow <= 0d || fellowship is null || !fellowship.IsInFellowship)
            return false;
        double worst = vitals.HealFellowsBelow;
        bool found = false;
        foreach (PluginFellowMember member in fellowship.CaptureMembers())
        {
            if (member.ObjectId == board.SelfId || member.CurrentHealth == 0u || member.Distance > vitals.HealFellowsRangeMeters)
                continue;
            double fraction = Fraction(member);
            if (fraction >= worst)
                continue;
            worst = fraction;
            fellow = member;
            found = true;
        }
        return found;
    }

    private bool TryCast(string spellName, uint target, out PluginCastRequestResult result)
    {
        result = PluginCastRequestResult.UnknownSpell;
        if (!spells.TryBestKnown(spellName, out PluginSpellInfo spell))
            return false;
        if (casts.IsOnCooldown(spell.SpellId))
        {
            result = PluginCastRequestResult.Unavailable;
            return false;
        }
        result = casts.Request(spell.SpellId, target);
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
