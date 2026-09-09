using AcDream.Core.Combat;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeCombatTargetStateTests
{
    [Fact]
    public void DeadSelectedTarget_AutoTargetEnabled_SelectsClosestReplacement()
    {
        var combat = new CombatState();
        combat.SetCombatMode(CombatMode.Melee);
        var selection = new SelectionState();
        selection.Select(0x50000001u, SelectionChangeSource.World);
        int calls = 0;
        using var controller = new RuntimeCombatTargetState(
            combat,
            selection,
            new Operations(true, () =>
            {
                calls++;
                selection.Select(0x50000002u, SelectionChangeSource.System);
                return 0x50000002u;
            }));

        controller.OnMotionApplied(0x50000001u, MotionCommand.Dead);

        Assert.Equal(1, calls);
        Assert.Equal(0x50000002u, selection.SelectedObjectId);
    }

    [Fact]
    public void DeadSelectedTarget_AutoTargetDisabled_LeavesSelectionClear()
    {
        var combat = new CombatState();
        combat.SetCombatMode(CombatMode.Melee);
        var selection = new SelectionState();
        selection.Select(0x50000001u, SelectionChangeSource.World);
        int calls = 0;
        using var controller = new RuntimeCombatTargetState(
            combat,
            selection,
            new Operations(false, () => { calls++; return null; }));

        controller.OnMotionApplied(0x50000001u, MotionCommand.Dead);

        Assert.Equal(0, calls);
        Assert.Null(selection.SelectedObjectId);
    }

    [Fact]
    public void DeadSelectedTarget_NonCombatMode_DoesNotAutoTarget()
    {
        var combat = new CombatState();
        var selection = new SelectionState();
        selection.Select(0x50000001u, SelectionChangeSource.World);
        int calls = 0;
        using var controller = new RuntimeCombatTargetState(
            combat,
            selection,
            new Operations(true, () => { calls++; return null; }));

        controller.OnMotionApplied(0x50000001u, MotionCommand.Dead);

        Assert.Equal(0, calls);
        Assert.Null(selection.SelectedObjectId);
    }

    [Fact]
    public void DeadUnselectedObject_DoesNotDisturbCurrentTarget()
    {
        var combat = new CombatState();
        combat.SetCombatMode(CombatMode.Missile);
        var selection = new SelectionState();
        selection.Select(0x50000002u, SelectionChangeSource.World);
        using var controller = new RuntimeCombatTargetState(
            combat,
            selection,
            new Operations(
                true,
                () => throw new InvalidOperationException()));

        controller.OnMotionApplied(0x50000001u, MotionCommand.Dead);

        Assert.Equal(0x50000002u, selection.SelectedObjectId);
    }

    [Fact]
    public void SessionReset_DoesNotAcquireTargetFromDepartingWorld()
    {
        var combat = new CombatState();
        combat.SetCombatMode(CombatMode.Missile);
        var selection = new SelectionState();
        selection.Select(0x50000002u, SelectionChangeSource.World);
        int calls = 0;
        using var controller = new RuntimeCombatTargetState(
            combat,
            selection,
            new Operations(true, () => { calls++; return null; }));

        selection.Reset();

        Assert.Equal(0, calls);
        Assert.Null(selection.SelectedObjectId);
    }

    private sealed class Operations(
        bool autoTarget,
        Func<uint?> selectClosestTarget)
        : IRuntimeCombatTargetOperations
    {
        public bool AutoTarget { get; } = autoTarget;
        public uint? SelectClosestTarget() => selectClosestTarget();
    }
}
