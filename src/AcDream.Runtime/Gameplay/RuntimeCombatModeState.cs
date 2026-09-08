using AcDream.Core.Combat;
using AcDream.Core.Items;

namespace AcDream.Runtime.Gameplay;

public enum RuntimeCombatModeRequestStatus
{
    Inactive,
    Rejected,
    Sent,
}

public readonly record struct RuntimeCombatModeRequestResult(
    RuntimeCombatModeRequestStatus Status,
    CombatMode Mode,
    string? Notice = null);

public interface IRuntimeCombatModeOperations
{
    bool IsInWorld { get; }
    IReadOnlyList<ClientObject> GetOrderedEquipment();
    void NotifyExplicitCombatModeRequest();
    void SendChangeCombatMode(CombatMode mode);
}

public sealed class RuntimeCombatModeState
{
    private readonly CombatState _combat;
    private readonly IRuntimeCombatModeOperations _operations;

    public RuntimeCombatModeState(
        CombatState combat,
        IRuntimeCombatModeOperations operations)
    {
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _operations = operations
            ?? throw new ArgumentNullException(nameof(operations));
    }

    public RuntimeCombatModeRequestResult Toggle()
    {
        if (!_operations.IsInWorld)
        {
            return new RuntimeCombatModeRequestResult(
                RuntimeCombatModeRequestStatus.Inactive,
                _combat.CurrentMode);
        }

        // Every explicit user request supersedes auto-wield settlement,
        // including a request GetDefaultCombatMode later rejects.
        _operations.NotifyExplicitCombatModeRequest();

        CombatMode currentMode = _combat.CurrentMode;
        CombatMode nextMode;
        if (currentMode != CombatMode.NonCombat)
        {
            nextMode = CombatMode.NonCombat;
        }
        else
        {
            DefaultCombatModeDecision decision =
                CombatInputPlanner.GetDefaultCombatModeDecision(
                    _operations.GetOrderedEquipment());
            if (decision.IncompatibleHeldItem is { } held)
            {
                string notice =
                    $"You can't enter combat mode while wielding the {held.GetAppropriateName()}";
                return new RuntimeCombatModeRequestResult(
                    RuntimeCombatModeRequestStatus.Rejected,
                    currentMode,
                    notice);
            }

            nextMode = CombatInputPlanner.ToggleMode(
                currentMode,
                decision.Mode);
        }

        _operations.SendChangeCombatMode(nextMode);
        _combat.SetCombatMode(nextMode);
        return new RuntimeCombatModeRequestResult(
            RuntimeCombatModeRequestStatus.Sent,
            nextMode);
    }

    public RuntimeCombatModeRequestResult Request(CombatMode mode)
    {
        if (!_operations.IsInWorld)
        {
            return new RuntimeCombatModeRequestResult(
                RuntimeCombatModeRequestStatus.Inactive,
                _combat.CurrentMode);
        }
        if (mode is not (CombatMode.NonCombat
            or CombatMode.Melee
            or CombatMode.Missile
            or CombatMode.Magic))
        {
            return new RuntimeCombatModeRequestResult(
                RuntimeCombatModeRequestStatus.Rejected,
                _combat.CurrentMode,
                "Invalid combat mode.");
        }
        if (_combat.CurrentMode == mode)
        {
            return new RuntimeCombatModeRequestResult(
                RuntimeCombatModeRequestStatus.Sent,
                mode);
        }

        _operations.NotifyExplicitCombatModeRequest();
        _operations.SendChangeCombatMode(mode);
        _combat.SetCombatMode(mode);
        return new RuntimeCombatModeRequestResult(
            RuntimeCombatModeRequestStatus.Sent,
            mode);
    }
}
