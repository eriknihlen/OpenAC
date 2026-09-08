using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MoveToManagerHandleUpdateTargetTests
{
    private const uint TargetId = 0x50004444u;

    private static MoveToManagerHarness ArmMoveToObject(float ownRadius = 0.5f, float ownHeight = 2f)
    {
        var h = new MoveToManagerHarness { OwnRadius = ownRadius, OwnHeight = ownHeight };
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 0f;
        h.Manager.MoveToObject(TargetId, TargetId, radius: 1f, height: 2f, new MovementParameters());
        return h;
    }

    [Fact]
    public void IgnoresUpdate_ForADifferentTarget()
    {
        var h = ArmMoveToObject();
        var wrongTargetPos = new Position(1u, new Vector3(5f, 5f, 0f), Quaternion.Identity);

        h.Manager.HandleUpdateTarget(new TargetInfo(0x59999999u, TargetStatus.Ok, wrongTargetPos, wrongTargetPos));

        Assert.False(h.Manager.Initialized);
        Assert.Empty(h.Manager.PendingActions);
    }

    [Fact]
    public void FirstCallback_NonOkStatus_CancelsAsNoObject()
    {
        var h = ArmMoveToObject();
        var pos = new Position(1u, Vector3.Zero, Quaternion.Identity);

        h.Manager.HandleUpdateTarget(new TargetInfo(TargetId, TargetStatus.ExitWorld, pos, pos));

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void FirstCallback_OkStatus_BuildsNodePlan_SetsInitialized()
    {
        var h = ArmMoveToObject();
        var target = new Position(1u, new Vector3(10f, 0f, 0f), Quaternion.Identity);

        h.Manager.HandleUpdateTarget(new TargetInfo(TargetId, TargetStatus.Ok, target, target));

        Assert.True(h.Manager.Initialized);
        Assert.NotEmpty(h.Manager.PendingActions);
    }

    [Fact]
    public void FirstCallback_OrdinaryTarget_DoesNotFireMoveToComplete()
    {
        var h = ArmMoveToObject();
        var target = new Position(1u, new Vector3(10f, 0f, 0f), Quaternion.Identity);

        h.Manager.HandleUpdateTarget(new TargetInfo(TargetId, TargetStatus.Ok, target, target));

        Assert.Empty(h.MoveToCompleteCalls);
    }

    [Fact]
    public void Retarget_NonOkStatus_CancelsAsObjectGone()
    {
        var h = ArmMoveToObject();
        var target = new Position(1u, new Vector3(10f, 0f, 0f), Quaternion.Identity);
        h.Manager.HandleUpdateTarget(new TargetInfo(TargetId, TargetStatus.Ok, target, target));
        Assert.True(h.Manager.Initialized);

        h.Manager.HandleUpdateTarget(new TargetInfo(TargetId, TargetStatus.ExitWorld, target, target));

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void Retarget_UpdatesPositions_ResetsProgressClock_DoesNotRequeueNodes()
    {
        var h = ArmMoveToObject();
        var target1 = new Position(1u, new Vector3(10f, 0f, 0f), Quaternion.Identity);
        h.Manager.HandleUpdateTarget(new TargetInfo(TargetId, TargetStatus.Ok, target1, target1));
        int nodeCountAfterFirst = System.Linq.Enumerable.Count(h.Manager.PendingActions);
        Assert.True(nodeCountAfterFirst > 0);

        h.Advance(3.0);

        var target2 = new Position(1u, new Vector3(20f, 5f, 0f), Quaternion.Identity);
        var interp2 = new Position(1u, new Vector3(19f, 5f, 0f), Quaternion.Identity);
        h.Manager.HandleUpdateTarget(new TargetInfo(TargetId, TargetStatus.Ok, target2, interp2));

        Assert.Equal(target2, h.Manager.CurrentTargetPosition);
        Assert.Equal(interp2, h.Manager.SoughtPosition);
        Assert.Equal(float.MaxValue, h.Manager.PreviousDistance);
        Assert.Equal(float.MaxValue, h.Manager.OriginalDistance);

        Assert.Equal(nodeCountAfterFirst, System.Linq.Enumerable.Count(h.Manager.PendingActions));
    }

    [Fact]
    public void Retarget_TurnToObject_GetsNoRetargetHandling_HeadingFrozen()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 0f;
        h.Manager.TurnToObject(TargetId, TargetId, new MovementParameters());

        var target1 = new Position(1u, new Vector3(10f, 0f, 0f), Quaternion.Identity); // heading 90
        h.Manager.TurnToObject_Internal(target1);
        Assert.True(h.Manager.Initialized);
        var soughtAfterFirst = h.Manager.SoughtPosition;

        var target2 = new Position(1u, new Vector3(0f, 10f, 0f), Quaternion.Identity); // heading 0
        h.Manager.HandleUpdateTarget(new TargetInfo(TargetId, TargetStatus.Ok, target2, target2));

        Assert.Equal(soughtAfterFirst, h.Manager.SoughtPosition); // untouched
    }

    [Fact]
    public void NoPhysicsObj_CancelsWithNoPhysicsObjectCode()
    {
        var h = ArmMoveToObject();
        h.Manager.HasPhysicsObj = false;

        var target = new Position(1u, new Vector3(10f, 0f, 0f), Quaternion.Identity);
        h.Manager.HandleUpdateTarget(new TargetInfo(TargetId, TargetStatus.Ok, target, target));

        Assert.False(h.Manager.IsMovingTo());
    }
}
