using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Combat;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Spells;

/// <summary>
/// Puts a caster in hand and the character in magic mode before a
/// behavior casts. The client sends a cast in whatever mode it is in, and
/// the server drops one sent from melee, missile or peace mode with a
/// plain use-done that looks like success - a player never meets this,
/// since the casting panel only exists in magic mode - and it will not
/// enter magic mode without a wand, orb or staff wielded. The wand is the
/// one the profile names for the magic style when the character has it,
/// else whatever caster is in hand, else the first in the pack - any
/// caster casts. Each step is asked once and waited
/// for, the way the combat behavior waits for its own mode.
/// </summary>
public sealed class MagicModeGate(Func<string> wandName)
{
    public const double ModeTimeoutSeconds = 4d;
    public const double EquipRetrySeconds = 2d;

    private double _modeRequestedAt = double.NegativeInfinity;
    private double _equipRequestedAt = double.NegativeInfinity;

    /// <summary>
    /// True when a caster is wielded and the character is in magic mode,
    /// so the cast can go out. Otherwise <paramref name="step"/> is what
    /// the behavior returns this tick: a wait while a wield or the mode
    /// change is pending, a failure when there is no caster to wield or
    /// the mode was not confirmed in time.
    /// </summary>
    public bool TryEnsure(BehaviorContext context, out BehaviorStep step)
    {
        Blackboard board = context.Board;
        if (!TryEnsureWand(context, out step))
            return false;
        if (board.Combat.Mode == PluginCombatMode.Magic)
        {
            _modeRequestedAt = double.NegativeInfinity;
            step = BehaviorStep.Continue;
            return true;
        }
        if (!double.IsNegativeInfinity(_modeRequestedAt))
        {
            if (board.Now - _modeRequestedAt <= ModeTimeoutSeconds)
            {
                step = BehaviorStep.Continue;
                return false;
            }
            _modeRequestedAt = double.NegativeInfinity;
            step = BehaviorStep.Fail($"magic mode was not confirmed; the client is in {board.Combat.Mode}");
            return false;
        }
        PluginCombatCommandResult result = context.Surface.Combat.EnterMode(PluginCombatMode.Magic);
        if (!result.Accepted)
        {
            step = BehaviorStep.Fail($"cannot enter magic mode: {result.Status} {result.Notice}".TrimEnd());
            return false;
        }
        // A host that is in magic mode already, or switches on the spot,
        // need not cost a tick; the live client answers a tick or two later.
        if (context.Surface.Combat.Snapshot.Mode == PluginCombatMode.Magic)
        {
            step = BehaviorStep.Continue;
            return true;
        }
        _modeRequestedAt = board.Now;
        step = BehaviorStep.Continue;
        return false;
    }

    private bool TryEnsureWand(BehaviorContext context, out BehaviorStep step)
    {
        step = BehaviorStep.Continue;
        IEquipmentAutomation equipment = context.Surface.Equipment;
        if (!equipment.IsAvailable)
            return true;
        IReadOnlyList<PluginEquipmentItem> owned = equipment.CaptureOwnedEquipment();
        if (owned.Count == 0)
            return true; // a host reporting no items at all is not saying there are none

        PluginEquipmentItem? wand = null;
        string wanted = wandName();
        if (wanted.Length > 0)
        {
            foreach (PluginEquipmentItem item in owned)
            {
                if (item.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                    || (wand is null && item.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)))
                {
                    wand = item;
                    if (item.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                        break;
                }
            }
        }
        if (wand is null)
        {
            foreach (PluginEquipmentItem item in owned)
            {
                if (!WeaponReadiness.IsCaster(item))
                    continue;
                if (item.IsEquipped)
                {
                    wand = item;
                    break;
                }
                wand ??= item;
            }
        }
        if (wand is null)
        {
            step = BehaviorStep.Fail("no wand, orb or staff to cast with");
            return false;
        }
        if (wand.Value.IsEquipped)
        {
            _equipRequestedAt = double.NegativeInfinity;
            return true;
        }
        if (equipment.IsBusy || context.Board.Now - _equipRequestedAt < EquipRetrySeconds)
            return false;
        _equipRequestedAt = context.Board.Now;
        PluginEquipmentCommandResult result = equipment.Equip(wand.Value.ObjectId);
        if (result.Status is PluginEquipmentCommandStatus.Refused or PluginEquipmentCommandStatus.InvalidItem)
        {
            step = BehaviorStep.Fail($"could not wield {wand.Value.Name}: {result.Status} {result.Notice}".TrimEnd());
            return false;
        }
        return false;
    }

    public void Reset()
    {
        _modeRequestedAt = double.NegativeInfinity;
        _equipRequestedAt = double.NegativeInfinity;
    }
}
