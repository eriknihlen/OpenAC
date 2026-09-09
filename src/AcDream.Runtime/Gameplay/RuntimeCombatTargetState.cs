using AcDream.Core.Combat;
using AcDream.Core.Physics;
using AcDream.Core.Selection;

namespace AcDream.Runtime.Gameplay;

public interface IRuntimeCombatTargetOperations
{
    bool AutoTarget { get; }
    uint? SelectClosestTarget();
}

public sealed class RuntimeCombatTargetState : IDisposable
{
    private readonly CombatState _combat;
    private readonly SelectionState _selection;
    private readonly IRuntimeCombatTargetOperations _operations;
    private bool _disposed;
    public bool IsDisposed => _disposed;

    public RuntimeCombatTargetState(
        CombatState combat,
        SelectionState selection,
        IRuntimeCombatTargetOperations operations)
    {
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _operations = operations
            ?? throw new ArgumentNullException(nameof(operations));

        _selection.Changed += OnSelectionChanged;
    }

    public void OnMotionApplied(uint objectId, uint currentMotion)
    {
        if (currentMotion != MotionCommand.Dead
            || _selection.SelectedObjectId != objectId)
            return;

        _selection.Clear(
            SelectionChangeSource.System,
            SelectionChangeReason.CombatTargetDied);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _selection.Changed -= OnSelectionChanged;
    }

    private void OnSelectionChanged(SelectionTransition transition)
    {
        if (transition.SelectedObjectId is not null
            || transition.Reason == SelectionChangeReason.SessionReset
            || !_operations.AutoTarget
            || !CombatInputPlanner.SupportsTargetedAttack(_combat.CurrentMode))
            return;

        _operations.SelectClosestTarget();
    }
}
