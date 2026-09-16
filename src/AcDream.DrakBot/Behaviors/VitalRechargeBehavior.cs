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
    IFellowshipAutomation? fellowship = null,
    Func<string>? wandName = null) : IBehavior
{
    private const double KitRetrySeconds = 3d;
    private readonly MagicModeGate _magic = new(wandName ?? (() => string.Empty));
    private double _kitRetryAfter = double.NegativeInfinity;

    /// <summary>
    /// How long a vital that could not be topped up - the spell unknown,
    /// no kit, the cast refused - is left alone before it is tried again.
    /// Without it a melee character at low mana with no mana spell asked
    /// for control sixty times a second and nothing else ever ran.
    /// </summary>
    public const double FailRetrySeconds = 15d;
    /// <summary>The same failure again doubles the wait, up to this: no components is not going to change in a hurry, and every try interrupts the walk.</summary>
    private const double FailRetryCeilingSeconds = 300d;

    private enum Vital { Health, Stamina, Mana, Fellow }

    private readonly double[] _retryAfter = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
    private readonly double[] _retryWait = [FailRetrySeconds, FailRetrySeconds, FailRetrySeconds, FailRetrySeconds];
    private readonly string?[] _lastFailure = new string?[4];

    public string Name => "vitals";

    private const double PendingWaitSeconds = 8d;
    private double _pendingSince = double.NaN;

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
        int slot = (int)vital;
        bool repeated = string.Equals(_lastFailure[slot], reason, StringComparison.Ordinal);
        _retryWait[slot] = repeated ? Math.Min(_retryWait[slot] * 2d, FailRetryCeilingSeconds) : FailRetrySeconds;
        _retryAfter[slot] = board.Now + _retryWait[slot];
        _lastFailure[slot] = reason;
        return repeated ? BehaviorStep.Done : BehaviorStep.Fail($"{reason}; not tried again for {FailRetrySeconds:0}s, then less and less often");
    }

    private void Recovered(Vital vital)
    {
        _lastFailure[(int)vital] = null;
        _retryWait[(int)vital] = FailRetrySeconds;
    }

    public BehaviorStep Execute(BehaviorContext context)
    {
        Blackboard board = context.Board;
        VitalSettings vitals = settings();

        CastOutcome? outcome = casts.Poll();
        if (casts.HasPendingRequest)
            return BehaviorStep.Continue;
        if (outcome is CastOutcome.Succeeded)
        {
            // A cast the host calls a success that moved the vital by
            // nothing worth the name - a heal of a hundred on a hundred
            // thousand, or a spell that was never a heal - is not cast
            // again at once: that stood the bot still casting for ever.
            if (_castVital is { } vital && FractionOf(vital, board) - _fractionAtCast < NoEffectFraction)
            {
                // The spell is put aside, not the vital: a kit may still
                // do what the spell could not, and gets the next tick.
                _castVital = null;
                _spellRestedUntil[(int)vital] = board.Now + NoEffectRestSeconds;
                return BehaviorStep.Fail($"{_castName} did next to nothing for {vital.ToString().ToLowerInvariant()}; not cast again for {NoEffectRestSeconds / 60d:0} min");
            }
            _castVital = null;
            return BehaviorStep.Done;
        }
        if (board.IsActionPending)
        {
            // A hand that never comes free is not waited on for ever: the
            // engine's watchdog clears the stuck action at ten seconds,
            // and the tick is given back before that so nothing else
            // starves meanwhile.
            if (double.IsNaN(_pendingSince))
                _pendingSince = board.Now;
            if (board.Now - _pendingSince < PendingWaitSeconds)
                return BehaviorStep.Continue;
            _pendingSince = double.NaN;
            return BehaviorStep.Fail("the client has had an action pending too long to wait on");
        }
        _pendingSince = double.NaN;

        if (Wanted(Vital.Health, board) && board.Vitals.HealthFraction < HealBelow(board, vitals))
        {
            if (TryCast(context, Vital.Health, vitals.HealSpell, 0u, out PluginCastRequestResult result, out BehaviorStep step, out bool sent))
            {
                if (sent)
                {
                    NoteCast(Vital.Health, vitals.HealSpell, board);
                    Recovered(Vital.Health);
                }
                return step;
            }
            if (vitals.UseHealingKits && TryUseKit(context, Vital.Health))
            {
                Recovered(Vital.Health);
                return BehaviorStep.Continue;
            }
            return GiveUp(Vital.Health, board, $"no way to heal ({result})");
        }
        if (Wanted(Vital.Stamina, board) && board.Vitals.StaminaFraction < StaminaBelow(board, vitals))
        {
            if (!TryCast(context, Vital.Stamina, vitals.StaminaSpell, 0u, out PluginCastRequestResult result, out BehaviorStep step, out bool sent))
            {
                if (vitals.UseHealingKits && TryUseKit(context, Vital.Stamina))
                {
                    Recovered(Vital.Stamina);
                    return BehaviorStep.Continue;
                }
                return GiveUp(Vital.Stamina, board, $"cannot cast {vitals.StaminaSpell} ({result})");
            }
            if (sent)
            {
                NoteCast(Vital.Stamina, vitals.StaminaSpell, board);
                Recovered(Vital.Stamina);
            }
            return step;
        }
        if (Wanted(Vital.Mana, board) && board.Vitals.ManaFraction < ManaBelow(board, vitals))
        {
            if (!TryCast(context, Vital.Mana, vitals.ManaSpell, 0u, out PluginCastRequestResult result, out BehaviorStep step, out bool sent))
            {
                if (vitals.UseHealingKits && TryUseKit(context, Vital.Mana))
                {
                    Recovered(Vital.Mana);
                    return BehaviorStep.Continue;
                }
                return GiveUp(Vital.Mana, board, $"cannot cast {vitals.ManaSpell} ({result})");
            }
            if (sent)
            {
                NoteCast(Vital.Mana, vitals.ManaSpell, board);
                Recovered(Vital.Mana);
            }
            return step;
        }
        if (Wanted(Vital.Fellow, board) && TryFindHurtFellow(board, vitals, out PluginFellowMember fellow))
        {
            _castVital = null;
            if (!TryCast(context, Vital.Fellow, vitals.HealOtherSpell, fellow.ObjectId, out PluginCastRequestResult result, out BehaviorStep step, out bool sent))
                return GiveUp(Vital.Fellow, board, $"cannot cast {vitals.HealOtherSpell} on {fellow.Name} ({result})");
            if (sent)
                Recovered(Vital.Fellow);
            return step;
        }
        return BehaviorStep.Done;
    }

    public void Interrupt(BehaviorContext context)
    {
        casts.Clear();
        _castVital = null;
        _magic.Reset();
    }

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

    /// <summary>The least a cast must raise its vital by, as a fraction of the maximum, to have been worth casting.</summary>
    public const double NoEffectFraction = 0.01d;
    /// <summary>How long a vital spell that did next to nothing is put aside; a kit takes its place meanwhile.</summary>
    public const double NoEffectRestSeconds = 300d;
    private Vital? _castVital;
    private string _castName = string.Empty;
    /// <summary>Until when each vital's spell - any tier of it - is put aside for doing next to nothing.</summary>
    private readonly double[] _spellRestedUntil = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
    private double _fractionAtCast;

    private void NoteCast(Vital vital, string name, Blackboard board)
    {
        _castVital = vital;
        _castName = name;
        _fractionAtCast = FractionOf(vital, board);
    }

    private static double FractionOf(Vital vital, Blackboard board) => vital switch
    {
        Vital.Health => board.Vitals.HealthFraction,
        Vital.Stamina => board.Vitals.StaminaFraction,
        Vital.Mana => board.Vitals.ManaFraction,
        _ => 1d,
    };

    /// <summary>
    /// Casts the best known tier of a vital spell, once a caster is in
    /// hand and the character in magic mode. False with a result when it
    /// cannot be cast at all; true with <paramref name="step"/> set when
    /// the cast went out, or the gate is still getting ready.
    /// </summary>
    private bool TryCast(BehaviorContext context, Vital vital, string spellName, uint target, out PluginCastRequestResult result, out BehaviorStep step, out bool sent)
    {
        step = BehaviorStep.Continue;
        sent = false;
        result = PluginCastRequestResult.UnknownSpell;
        if (!spells.TryBestKnown(spellName, out PluginSpellInfo spell))
            return false;
        if (casts.IsOnCooldown(spell.SpellId) || context.Board.Now < _spellRestedUntil[(int)vital])
        {
            result = PluginCastRequestResult.Unavailable;
            return false;
        }
        if (!_magic.TryEnsure(context, out step))
            return true;
        result = casts.Request(spell.SpellId, target);
        if (result != PluginCastRequestResult.Sent)
            return false;
        sent = true;
        return true;
    }

    // The vital a kit restores, as the item's booster names it (the
    // second-attribute ids); an old health kit may leave it unset.
    private const int BoosterHealth = 2;
    private const int BoosterStamina = 4;
    private const int BoosterMana = 6;

    private static bool KitRestores(in PluginInventoryItem item, Vital vital) => vital switch
    {
        Vital.Health => item.BoosterVital is BoosterHealth or 0,
        Vital.Stamina => item.BoosterVital == BoosterStamina,
        Vital.Mana => item.BoosterVital == BoosterMana,
        _ => false,
    };

    /// <summary>Applies the first kit in the pack that restores the vital - a healing kit, or the stamina and mana kinds.</summary>
    private bool TryUseKit(BehaviorContext context, Vital vital)
    {
        if (context.Board.Now < _kitRetryAfter)
            return false;
        IItemAutomation items = context.Surface.Items;
        if (!items.IsAvailable || items.IsBusy)
            return false;
        foreach (PluginInventoryItem item in items.CaptureOwnedItems())
        {
            if (item.ObjectClass != PluginObjectClass.HealingKit || !KitRestores(item, vital))
                continue;
            PluginItemCommandResult result = items.Apply(item.ObjectId, context.Board.SelfId);
            _kitRetryAfter = context.Board.Now + KitRetrySeconds;
            if (result.Accepted)
                return true;
        }
        return false;
    }
}
