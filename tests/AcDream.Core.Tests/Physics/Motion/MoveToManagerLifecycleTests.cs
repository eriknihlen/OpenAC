using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MoveToManagerLifecycleTests
{
    [Fact]
    public void FreshManager_MovementTypeIsInvalid()
    {
        var h = new MoveToManagerHarness();
        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
        Assert.False(h.Manager.IsMovingTo());
    }

    [Fact]
    public void FreshManager_ProgressClocksAreFltMax()
    {
        var h = new MoveToManagerHarness();
        Assert.Equal(float.MaxValue, h.Manager.PreviousDistance);
        Assert.Equal(float.MaxValue, h.Manager.OriginalDistance);
    }

    [Fact]
    public void FreshManager_BitfieldFlagsAllClear_ScalarsUntouchedByCtorReset()
    {
        var h = new MoveToManagerHarness();
        Assert.False(h.Manager.Params.CanWalk);
        Assert.False(h.Manager.Params.CanRun);
        Assert.False(h.Manager.Params.MoveTowards);
        Assert.False(h.Manager.Params.CancelMoveTo);
        Assert.Equal(0u, h.Manager.Params.ContextId);
    }

    [Fact]
    public void FreshManager_SoughtPositionAndCurrentTargetAreIdentityFrameAtCellZero()
    {
        var h = new MoveToManagerHarness();
        var expected = new Position(0u, System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity);
        Assert.Equal(expected, h.Manager.SoughtPosition);
        Assert.Equal(expected, h.Manager.CurrentTargetPosition);
        Assert.Equal(0f, MoveToMath.GetHeading(h.Manager.SoughtPosition.Frame.Orientation));
    }

    [Fact]
    public void FreshManager_PendingActionsEmpty()
    {
        var h = new MoveToManagerHarness();
        Assert.Empty(h.Manager.PendingActions);
    }

    [Fact]
    public void Destroy_DrainsPendingActions_ThenReInitializes()
    {
        var h = new MoveToManagerHarness();
        h.Manager.AddMoveToPositionNode();
        h.Manager.AddTurnToHeadingNode(90f);
        Assert.Equal(2, System.Linq.Enumerable.Count(h.Manager.PendingActions));

        h.Manager.Destroy();

        Assert.Empty(h.Manager.PendingActions);
        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void IsMovingTo_TrueAfterMoveToPosition_FalseAfterCancel()
    {
        var h = new MoveToManagerHarness();
        h.Manager.MoveToPosition(new Position(1u, new System.Numerics.Vector3(10f, 0f, 0f), System.Numerics.Quaternion.Identity), new MovementParameters());

        Assert.True(h.Manager.IsMovingTo());

        h.Manager.CancelMoveTo(WeenieError.ActionCancelled);

        Assert.False(h.Manager.IsMovingTo());
    }
}
