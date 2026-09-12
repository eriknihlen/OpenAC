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
    private bool _targetWillinglyLost;
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

    /// <summary>
    /// Marks the next emptying of the selection as one the player asked for,
    /// so it is answered by leaving the selection empty instead of picking a
    /// fresh target. Exactly one such change consumes the mark.
    /// </summary>
    public void NotifyTargetWillinglyLost() => _targetWillinglyLost = true;

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
        if (transition.SelectedObjectId is not null)
            return;

        // A deselect the player asked for stands as given: consume the mark
        // and leave the selection empty, otherwise the target is picked
        // straight back up and the key press appears to do nothing. A session
        // reset drops the mark along with the rest of the session's state.
        if (_targetWillinglyLost
            || transition.Reason == SelectionChangeReason.SessionReset)
        {
            _targetWillinglyLost = false;
            return;
        }

        if (!_operations.AutoTarget
            || !CombatInputPlanner.SupportsTargetedAttack(_combat.CurrentMode))
            return;

        _operations.SelectClosestTarget();
    }
}
