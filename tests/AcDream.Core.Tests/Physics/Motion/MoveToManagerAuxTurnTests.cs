using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MoveToManagerAuxTurnTests
{
    private static MoveToManagerHarness ArmMoving(float initialHeading, Vector3 targetXY)
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = initialHeading;

        var p = new MovementParameters { DistanceToObject = 0.6f, UseSpheres = false };
        h.Manager.MoveToPosition(new Position(1u, targetXY, Quaternion.Identity), p);

        h.DrainPendingMotions();

        return h;
    }

    [Fact]
    public void WithinDeadband_NoAuxTurnIssued()
    {
        // Target due east (heading 90); mover already facing 85 -> diff 5,
        // inside [0,20] deadband.
        var h = ArmMoving(initialHeading: 90f, targetXY: new Vector3(20f, 0f, 0f));
        h.Heading = 85f;

        h.Manager.HandleMoveToPosition();

        Assert.Equal(0u, h.Manager.AuxCommand);
    }

    [Fact]
    public void JustOutsideDeadband_Positive_IssuesTurnRight()
    {
        var h = ArmMoving(initialHeading: 90f, targetXY: new Vector3(20f, 0f, 0f));
        h.Heading = 40f; // diff = 90-40 = 50 -> outside [0,20]∪[340,360)

        h.Manager.HandleMoveToPosition();

        Assert.Equal(MotionCommand.TurnRight, h.Manager.AuxCommand);
    }

    [Fact]
    public void DiffAtOrAbove180_IssuesTurnLeft()
    {
        var h = ArmMoving(initialHeading: 90f, targetXY: new Vector3(20f, 0f, 0f));
        h.Heading = 300f; // diff = 90-300 = -210 -> +360 = 150... need >=180 for TurnLeft; pick 260.
        h.Heading = 260f; // diff = 90-260=-170 -> +360=190 (>=180) -> TurnLeft

        h.Manager.HandleMoveToPosition();

        Assert.Equal(MotionCommand.TurnLeft, h.Manager.AuxCommand);
    }

    [Fact]
    public void DeadbandUpperBoundary_340_NoTurn()
    {
        var h = ArmMoving(initialHeading: 0f, targetXY: new Vector3(0f, 20f, 0f)); // target heading 0
        h.Heading = 20f; // diff = 0-20=-20 -> +360=340 -> boundary INCLUSIVE (diff >= 340)

        h.Manager.HandleMoveToPosition();

        Assert.Equal(0u, h.Manager.AuxCommand);
    }

    [Fact]
    public void DeadbandLowerBoundary_20_NoTurn()
    {
        var h = ArmMoving(initialHeading: 0f, targetXY: new Vector3(0f, 20f, 0f)); // target heading 0
        h.Heading = -20f % 360f; // normalize below
        h.Heading = 340f; // diff = 0-340 = -340 -> +360=20 -> boundary INCLUSIVE (diff <= 20)

        h.Manager.HandleMoveToPosition();

        Assert.Equal(0u, h.Manager.AuxCommand);
    }

    [Fact]
    public void NoRedundantReissue_SameDirectionTwice_DoesNotRedispatch()
    {
        var h = ArmMoving(initialHeading: 90f, targetXY: new Vector3(20f, 0f, 0f));
        h.Heading = 40f; // outside deadband -> TurnRight

        h.Manager.HandleMoveToPosition();
        uint firstAux = h.Manager.AuxCommand;
        Assert.Equal(MotionCommand.TurnRight, firstAux);

        h.DrainPendingMotions();

        int stopCallsBefore = h.StopCompletelyCalls;

        // Second tick, still outside deadband, same direction -> _DoMotion
        // should NOT be re-issued (turn != AuxCommand test fails since
        // AuxCommand already equals TurnRight) — assert AuxCommand is
        // unchanged (still TurnRight) as the observable proxy.
        h.Manager.HandleMoveToPosition();

        Assert.Equal(MotionCommand.TurnRight, h.Manager.AuxCommand);
        Assert.Equal(stopCallsBefore, h.StopCompletelyCalls);
    }

    [Fact]
    public void AnimatingMotionsPending_StopsAuxTurn_DoesNotStartNew()
    {
        var h = ArmMoving(initialHeading: 90f, targetXY: new Vector3(20f, 0f, 0f));
        h.Heading = 40f;
        h.Manager.HandleMoveToPosition();
        Assert.Equal(MotionCommand.TurnRight, h.Manager.AuxCommand);

        // Simulate an animation-table motion still pending by queueing a
        // node onto the REAL MotionInterpreter's pending_motions.
        h.Interp.AddToQueue(0, MotionCommand.WalkForward, 0);
        Assert.True(h.Interp.MotionsPending());

        h.Manager.HandleMoveToPosition();

        Assert.Equal(0u, h.Manager.AuxCommand);
    }
}
