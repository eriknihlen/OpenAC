using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MoveToManagerBeginMoveForwardTests
{
    [Fact]
    public void FarFromTarget_CanRunCanWalk_DispatchesRunForward()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f; // already facing target — no aux turn needed

        var p = new MovementParameters { DistanceToObject = 0.6f, WalkRunThreshhold = 15f };
        // Distance far beyond threshold+dto -> Run.
        h.Manager.MoveToPosition(new Position(1u, new Vector3(100f, 0f, 0f), Quaternion.Identity), p);

        Assert.Equal(MotionCommand.RunForward, h.ForwardCommand);
        Assert.Equal(HoldKey.Run, h.Manager.Params.HoldKeyToApply);
        Assert.Equal(MotionCommand.WalkForward, h.Manager.CurrentCommand);
    }

    [Fact]
    public void WithinWalkRunThreshold_DemotesToWalk()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;

        var p = new MovementParameters { DistanceToObject = 0.6f, WalkRunThreshhold = 15f };
        // dist - dto = 10 - 0.6 = 9.4 <= 15 -> walk.
        h.Manager.MoveToPosition(new Position(1u, new Vector3(10f, 0f, 0f), Quaternion.Identity), p);

        Assert.Equal(MotionCommand.WalkForward, h.ForwardCommand);
        Assert.Equal(HoldKey.None, h.Manager.Params.HoldKeyToApply);
    }

    [Fact]
    public void CanCharge_FastPathWins_RunsEvenWhenClose()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;

        var p = new MovementParameters { DistanceToObject = 0.6f, WalkRunThreshhold = 15f, CanCharge = true };
        h.Manager.MoveToPosition(new Position(1u, new Vector3(2f, 0f, 0f), Quaternion.Identity), p);

        Assert.Equal(MotionCommand.RunForward, h.ForwardCommand);
        Assert.Equal(HoldKey.Run, h.Manager.Params.HoldKeyToApply);
    }

    [Fact]
    public void HoldKeyWriteBack_ToStoredParams_NotJustLocalCopy()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;

        var p = new MovementParameters { DistanceToObject = 0.6f, WalkRunThreshhold = 15f, HoldKeyToApply = HoldKey.Invalid };
        h.Manager.MoveToPosition(new Position(1u, new Vector3(100f, 0f, 0f), Quaternion.Identity), p);

        Assert.Equal(HoldKey.Run, h.Manager.Params.HoldKeyToApply);
    }

    [Fact]
    public void ProgressClockSeeded_PreviousAndOriginalEqualCurrentDistance()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;
        h.CurTime = 5.0;

        var p = new MovementParameters { DistanceToObject = 0.6f, UseSpheres = false };
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), p);

        Assert.Equal(20f, h.Manager.PreviousDistance, 2);
        Assert.Equal(20f, h.Manager.OriginalDistance, 2);
        Assert.Equal(5.0, h.Manager.PreviousDistanceTime, 3);
        Assert.Equal(5.0, h.Manager.OriginalDistanceTime, 3);
    }

    [Fact]
    public void ProgressClockSeeded_UseSpheresDefault_UsesCylinderDistance()
    {
        var h = new MoveToManagerHarness { OwnRadius = 0.5f };
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;
        h.CurTime = 5.0;

        var p = new MovementParameters { DistanceToObject = 0.6f }; // UseSpheres=true (default)
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), p);

        Assert.Equal(19.5f, h.Manager.PreviousDistance, 2);
        Assert.Equal(19.5f, h.Manager.OriginalDistance, 2);
    }

    [Fact]
    public void CancelMoveToBit_ClearedOnLocalParams_DoesNotSelfCancel()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;

        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), new MovementParameters());

        Assert.True(h.Manager.IsMovingTo());
    }

    [Fact]
    public void NoPhysicsObj_CancelsWithNoPhysicsObjectCode()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), new MovementParameters());
        Assert.True(h.Manager.IsMovingTo());

        h.Manager.HasPhysicsObj = false;
        h.Manager.BeginMoveForward();

        Assert.False(h.Manager.IsMovingTo());
    }
}
