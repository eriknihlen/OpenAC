using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MoveToManagerEndToEndTableDriveTests
{
    [Fact]
    public void ChaseSequence_TurnFirst_ThenRun_ThenDemoteToWalk_ThenArrive()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 0f; // facing NORTH; target is due EAST -> a turn-to-face node is required first.

        var p = new MovementParameters { DistanceToObject = 0.6f, WalkRunThreshhold = 15f, UseSpheres = false };
        h.Manager.MoveToPosition(new Position(1u, new Vector3(100f, 0f, 0f), Quaternion.Identity), p);

        // Step 1: node plan = [TurnToHeading(90), MoveToPosition]. Heading
        // diff (0->90) is large -> BeginTurnToHeading armed a real turn
        // (not the "already there" early-out).
        Assert.Equal(2, System.Linq.Enumerable.Count(h.Manager.PendingActions));
        Assert.Equal(MotionCommand.TurnRight, h.Manager.CurrentCommand);
        h.DrainPendingMotions();

        h.Heading = 91f; // "passed" 90
        h.Manager.HandleTurnToHeading();
        h.DrainPendingMotions();

        Assert.Equal(MovementType.MoveToPosition, h.Manager.MovementTypeState);
        Assert.Single(h.Manager.PendingActions);
        Assert.Equal(MotionCommand.RunForward, h.ForwardCommand);
        Assert.Equal(HoldKey.Run, h.Manager.Params.HoldKeyToApply);

        h.WorldPosition = new Position(1u, new Vector3(90f, 0f, 0f), Quaternion.Identity); // 10 units from target
        h.Heading = 90f; // already facing it
        var pClose = new MovementParameters { DistanceToObject = 0.6f, WalkRunThreshhold = 15f, UseSpheres = false };
        h.Manager.PerformMovement(new MovementStruct
        {
            Type = MovementType.MoveToPosition,
            Pos = new Position(1u, new Vector3(100f, 0f, 0f), Quaternion.Identity),
            Params = pClose,
        });
        h.DrainPendingMotions();
        h.Manager.BeginNextNode();
        h.DrainPendingMotions();

        Assert.Single(h.Manager.PendingActions);

        // dist=10, dto=0.6 -> dist-dto=9.4 <= 15 -> WALK.
        Assert.Equal(MotionCommand.WalkForward, h.ForwardCommand);
        Assert.Equal(HoldKey.None, h.Manager.Params.HoldKeyToApply);

        // Step 4: arrive.
        h.WorldPosition = new Position(1u, new Vector3(99.7f, 0f, 0f), Quaternion.Identity);
        h.Advance(2.0);
        h.Manager.HandleMoveToPosition();

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
        Assert.Empty(h.Manager.PendingActions);
    }

    [Fact]
    public void FleeSequence_WalksBackward_InsideMinBand_ArrivesWhenFarEnough()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, new Vector3(3f, 0f, 0f), Quaternion.Identity);
        h.Heading = 270f; // facing the threat (target) which is behind at origin -- WalkBackward needs no turn.

        // towards_and_away band: dist(3) inside [MinDistance(5)... wait need
        // dist < min for the inside-band WalkBackward pick]. Use MinDistance
        // 5 with mover at distance 3 from the target (origin) -> inside
        // band -> WalkBackward, no turn node queued (§5d asymmetry).
        var p = new MovementParameters { MoveTowards = true, MoveAway = true, MinDistance = 5f, DistanceToObject = 8f, UseSpheres = false };
        h.Manager.MoveToPosition(new Position(1u, Vector3.Zero, Quaternion.Identity), p);

        Assert.True(h.Manager.MovingAway);
        Assert.Equal(MotionCommand.WalkBackward, h.Manager.CurrentCommand); // pre-adjust id (get_command's own return)
        // adjust_motion normalizes WalkBackward -> WalkForward with a
        // negative BackwardsFactor-scaled speed, dispatched as ForwardCommand.
        Assert.Equal(MotionCommand.WalkForward, h.ForwardCommand);
        Assert.True(h.ForwardSpeed < 0f);
        h.DrainPendingMotions();

        // Flee to distance 6 (>= MinDistance 5) -> arrived.
        h.WorldPosition = new Position(1u, new Vector3(6f, 0f, 0f), Quaternion.Identity);
        h.Advance(2.0);
        h.Manager.HandleMoveToPosition();

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void TurnToObjectSequence_DeferredStart_ThenRetargetIgnored_ThenArrivesOnHeadingPass()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 0f;

        const uint targetId = 0x5000CCCCu;
        h.Manager.TurnToObject(targetId, targetId, new MovementParameters());
        Assert.Empty(h.Manager.PendingActions); // deferred (§3d)

        var target = new Position(1u, new Vector3(10f, 0f, 0f), Quaternion.Identity); // heading 90
        h.Manager.HandleUpdateTarget(new TargetInfo(targetId, TargetStatus.Ok, target, target));

        Assert.True(h.Manager.Initialized);
        Assert.Single(h.Manager.PendingActions);
        Assert.Equal(MotionCommand.TurnRight, h.Manager.CurrentCommand);
        h.DrainPendingMotions();

        // Retarget while running: TurnToObject gets no handling (heading frozen).
        var target2 = new Position(1u, new Vector3(0f, 10f, 0f), Quaternion.Identity); // heading 0
        h.Manager.HandleUpdateTarget(new TargetInfo(targetId, TargetStatus.Ok, target2, target2));
        var soughtBefore = h.Manager.SoughtPosition;
        Assert.Equal(soughtBefore, h.Manager.SoughtPosition); // sanity: unchanged by its own read

        h.Heading = 91f;
        h.Manager.HandleTurnToHeading();

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
        Assert.Equal(90f, h.Heading, 2); // snapped to the frozen heading, unaffected by the retarget.
    }
}
