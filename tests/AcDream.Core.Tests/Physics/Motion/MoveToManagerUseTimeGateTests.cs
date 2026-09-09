using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MoveToManagerUseTimeGateTests
{
    [Fact]
    public void NoNodeQueued_UseTimeIsANoOp()
    {
        var h = new MoveToManagerHarness();
        // Fresh manager: no active move, no nodes.
        h.Manager.UseTime();

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void NotGrounded_ContactFalse_UseTimeDoesNothing_EvenWithNodesQueued()
    {
        var h = new MoveToManagerHarness { ContactValue = false };
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f; // face the target so BeginMoveForward runs (no turn-to-face node needed)
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), new MovementParameters());
        h.DrainPendingMotions();
        uint commandBefore = h.Manager.CurrentCommand;

        h.WorldPosition = new Position(1u, new Vector3(10f, 0f, 0f), Quaternion.Identity);
        h.Advance(5.0);
        h.Manager.UseTime();

        Assert.Equal(commandBefore, h.Manager.CurrentCommand);
        Assert.Equal(MovementType.MoveToPosition, h.Manager.MovementTypeState);
    }

    [Fact]
    public void Grounded_MoveToPositionNode_DispatchesToHandleMoveToPosition()
    {
        var h = new MoveToManagerHarness { ContactValue = true };
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f; // face the target so BeginMoveForward runs (no turn-to-face node needed)
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), new MovementParameters { DistanceToObject = 0.6f, UseSpheres = false });
        h.DrainPendingMotions();

        h.WorldPosition = new Position(1u, new Vector3(19.7f, 0f, 0f), Quaternion.Identity);
        h.Advance(2.0);

        h.Manager.UseTime();

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void Grounded_TurnToHeadingNode_DispatchesToHandleTurnToHeading()
    {
        var h = new MoveToManagerHarness { ContactValue = true };
        h.Heading = 0f;
        h.Manager.TurnToHeading(new MovementParameters { DesiredHeading = 90f });
        h.DrainPendingMotions();
        Assert.Equal(MotionCommand.TurnRight, h.Manager.CurrentCommand);

        h.Heading = 91f; // "passed" the target
        h.Manager.UseTime();

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void ObjectMove_UninitializedType6_StallsUntilFirstTargetCallback()
    {
        var h = new MoveToManagerHarness { ContactValue = true };
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Manager.MoveToObject(0x5000AAAAu, 0x5000AAAAu, 1f, 2f, new MovementParameters());

        Assert.False(h.Manager.Initialized);
        Assert.Empty(h.Manager.PendingActions); // gate 2 alone already stalls it

        h.Manager.UseTime();

        Assert.Equal(MovementType.MoveToObject, h.Manager.MovementTypeState);
        Assert.False(h.Manager.Initialized);
    }

    [Fact]
    public void ObjectMove_Initialized_PassesGate3_ProcessesNormally()
    {
        var h = new MoveToManagerHarness { ContactValue = true };
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f; // face the target so the internal node plan skips the turn-to-face step
        h.Manager.MoveToObject(0x5000BBBBu, 0x5000BBBBu, radius: 0.5f, height: 2f, new MovementParameters { UseSpheres = false });

        var target = new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity);
        h.Manager.HandleUpdateTarget(new TargetInfo(0x5000BBBBu, TargetStatus.Ok, target, target));
        Assert.True(h.Manager.Initialized);
        h.DrainPendingMotions();

        h.WorldPosition = new Position(1u, new Vector3(19.5f, 0f, 0f), Quaternion.Identity); // within DistanceToObject default 0.6
        h.Advance(2.0);

        h.Manager.UseTime();

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void NonObjectMove_TopLevelIdZero_Gate3AlwaysPasses_RegardlessOfInitialized()
    {
        // Gate 3: (top_level_object_id == 0 || movement_type == Invalid) ||
        // initialized. Position/heading moves never set TopLevelObjectId,
        // so the FIRST disjunct alone always satisfies gate 3 -- Initialized
        // staying false (as it does for MoveToPosition/TurnToHeading, per
        // §3c/§3e's notes) never blocks them.
        var h = new MoveToManagerHarness { ContactValue = true };
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), new MovementParameters { DistanceToObject = 0.6f, UseSpheres = false });
        h.DrainPendingMotions();

        Assert.Equal(0u, h.Manager.TopLevelObjectId);
        Assert.False(h.Manager.Initialized);

        h.WorldPosition = new Position(1u, new Vector3(19.7f, 0f, 0f), Quaternion.Identity);
        h.Advance(2.0);
        h.Manager.UseTime();

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState); // gate 3 passed via the first disjunct.
    }
}
