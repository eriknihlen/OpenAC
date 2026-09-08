using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MoveToManagerStickyAndCancelTests
{

    [Fact]
    public void StickyArrival_ReadsRadiusHeightTlidBeforeCleanUp_ThenCallsStickTo()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 0f;

        var p = new MovementParameters { Sticky = true };
        h.Manager.MoveToObject(0x50005555u, 0x50005555u, radius: 1.5f, height: 2.5f, p);
        Assert.True(h.Manager.Params.Sticky); // preserved by MoveToObject (§3b)

        var target = new Position(1u, new Vector3(0.1f, 0f, 0f), Quaternion.Identity); // inside default dto -> arrives instantly
        h.Manager.HandleUpdateTarget(new TargetInfo(0x50005555u, TargetStatus.Ok, target, target));

        Assert.Single(h.StickToCalls);
        Assert.Equal((0x50005555u, 1.5f, 2.5f), h.StickToCalls[0]);
        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void NonStickyArrival_NoStickToCall_JustCleanUpAndStop()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 0f;

        var p = new MovementParameters { Sticky = false };
        h.Manager.MoveToObject(0x50005555u, 0x50005555u, radius: 1.5f, height: 2.5f, p);

        var target = new Position(1u, new Vector3(0.1f, 0f, 0f), Quaternion.Identity);
        h.Manager.HandleUpdateTarget(new TargetInfo(0x50005555u, TargetStatus.Ok, target, target));

        Assert.Empty(h.StickToCalls);
        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void MoveToPosition_NeverSticks_EvenIfRequested()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 0f;

        var p = new MovementParameters { Sticky = true, DistanceToObject = 0.6f, UseSpheres = false };
        h.Manager.MoveToPosition(new Position(1u, new Vector3(0.1f, 0f, 0f), Quaternion.Identity), p);

        Assert.Empty(h.StickToCalls);
        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void StickyHandoff_UsesSoughtRadiusHeight_NotOwnRadiusHeight()
    {
        var h = new MoveToManagerHarness { OwnRadius = 99f, OwnHeight = 99f };
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);

        var p = new MovementParameters { Sticky = true };
        h.Manager.MoveToObject(0x50006666u, 0x50006666u, radius: 3f, height: 4f, p);

        var target = new Position(1u, new Vector3(0.1f, 0f, 0f), Quaternion.Identity);
        h.Manager.HandleUpdateTarget(new TargetInfo(0x50006666u, TargetStatus.Ok, target, target));

        Assert.Equal((0x50006666u, 3f, 4f), h.StickToCalls[0]); // the TARGET's radius/height, not the mover's own.
    }


    [Fact]
    public void CancelMoveTo_OnInvalidState_IsANoOp()
    {
        var h = new MoveToManagerHarness();
        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);

        h.Manager.CancelMoveTo(WeenieError.ActionCancelled);

        Assert.Equal(0, h.StopCompletelyCalls);
        Assert.Equal(0, h.ClearTargetCalls);
    }

    [Fact]
    public void CancelMoveTo_DrainsPendingActions()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), new MovementParameters());
        Assert.NotEmpty(h.Manager.PendingActions);

        h.Manager.CancelMoveTo(WeenieError.ActionCancelled);

        Assert.Empty(h.Manager.PendingActions);
        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void CancelMoveTo_ErrorArgument_NeverReadInBody_SameBehaviorForAnyCode()
    {
        var h1 = new MoveToManagerHarness();
        h1.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h1.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), new MovementParameters());
        h1.Manager.CancelMoveTo(WeenieError.YouChargedTooFar);

        var h2 = new MoveToManagerHarness();
        h2.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h2.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), new MovementParameters());
        h2.Manager.CancelMoveTo(WeenieError.NoObject);

        Assert.Equal(h1.Manager.MovementTypeState, h2.Manager.MovementTypeState);
        Assert.Equal(h1.StopCompletelyCalls, h2.StopCompletelyCalls);
    }

    [Fact]
    public void CancelMoveTo_ClearsTarget_ForObjectMoves()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Manager.MoveToObject(0x50007777u, 0x50007777u, 1f, 2f, new MovementParameters());
        Assert.Single(h.SetTargetCalls);

        h.Manager.CancelMoveTo(WeenieError.ActionCancelled);

        Assert.Equal(1, h.ClearTargetCalls);
    }

    [Fact]
    public void CancelMoveTo_DoesNotClearTarget_ForPositionMoves()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), new MovementParameters());

        h.Manager.CancelMoveTo(WeenieError.ActionCancelled);

        Assert.Equal(0, h.ClearTargetCalls);
    }

    [Fact]
    public void Reentrancy_StopCompletelyCallback_ReenteringCancelMoveTo_NoOps()
    {
        var interp = new MotionInterpreter();
        var body = new PhysicsBody { TransientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable | TransientStateFlags.Active };
        interp.PhysicsObj = body;

        Position worldPosition = new(1u, Vector3.Zero, Quaternion.Identity);
        int stopCompletelyCalls = 0;
        int reentrantCancelCalls = 0;
        MoveToManager? mgr = null;

        mgr = new MoveToManager(
            interp,
            stopCompletely: () =>
            {
                stopCompletely_body();
            },
            getPosition: () => worldPosition,
            getHeading: () => 0f,
            setHeading: (h, send) => { },
            getOwnRadius: () => 0.5f,
            getOwnHeight: () => 2f,
            contact: () => true,
            isInterpolating: () => false,
            getVelocity: () => Vector3.Zero,
            getSelfId: () => 0x50000001u,
            setTarget: (a, b, c, d) => { },
            clearTarget: () => { },
            getTargetQuantum: () => 0.0,
            setTargetQuantum: q => { });

        void stopCompletely_body()
        {
            stopCompletelyCalls++;
            reentrantCancelCalls++;
            mgr!.CancelMoveTo(WeenieError.ActionCancelled);
        }

        mgr.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), new MovementParameters());
        int stopCallsAfterArm = stopCompletelyCalls;

        mgr.CancelMoveTo(WeenieError.ActionCancelled);

        Assert.Equal(stopCallsAfterArm + 1, stopCompletelyCalls);
        Assert.Equal(2, reentrantCancelCalls);
        Assert.Equal(MovementType.Invalid, mgr.MovementTypeState);
    }

    [Fact]
    public void CleanUpAndCallWeenie_FiresMoveToComplete_WithGivenError()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), new MovementParameters());

        h.Manager.CleanUpAndCallWeenie(WeenieError.None);

        Assert.Single(h.MoveToCompleteCalls);
        Assert.Equal(WeenieError.None, h.MoveToCompleteCalls[0]);
        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void CleanUpAndCallWeenie_Ordering_MovementTypeAlreadyInvalid_WhenStopCompletelyFires()
    {
        var interp = new MotionInterpreter();
        var body = new PhysicsBody { TransientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable | TransientStateFlags.Active };
        interp.PhysicsObj = body;
        Position worldPosition = new(1u, Vector3.Zero, Quaternion.Identity);
        MovementType? stateDuringStopCompletely = null;
        MoveToManager? mgr = null;

        mgr = new MoveToManager(
            interp,
            stopCompletely: () => stateDuringStopCompletely = mgr!.MovementTypeState,
            getPosition: () => worldPosition,
            getHeading: () => 0f,
            setHeading: (h, send) => { },
            getOwnRadius: () => 0.5f,
            getOwnHeight: () => 2f,
            contact: () => true,
            isInterpolating: () => false,
            getVelocity: () => Vector3.Zero,
            getSelfId: () => 0x50000001u,
            setTarget: (a, b, c, d) => { },
            clearTarget: () => { },
            getTargetQuantum: () => 0.0,
            setTargetQuantum: q => { });

        mgr.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), new MovementParameters());

        mgr.CleanUpAndCallWeenie(WeenieError.None);

        Assert.Equal(MovementType.Invalid, stateDuringStopCompletely);
    }

    [Fact]
    public void CleanUp_StopsCurrentAndAuxCommands_ClearsTargetForObjectMoves_ThenReinitializes()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Manager.MoveToObject(0x50008888u, 0x50008888u, 1f, 2f, new MovementParameters());
        var target = new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity);
        h.Manager.HandleUpdateTarget(new TargetInfo(0x50008888u, TargetStatus.Ok, target, target));
        Assert.NotEqual(0u, h.Manager.CurrentCommand);

        h.Manager.CleanUp();

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
        Assert.Equal(1, h.ClearTargetCalls);
        Assert.Equal(0u, h.Manager.CurrentCommand);
    }
}
