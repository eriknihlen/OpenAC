using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MoveToManagerNodePlanTests
{
    // ── MoveToPosition (§3c): [TurnToHeading(face)] → [MoveToPosition] ────

    [Fact]
    public void MoveToPosition_NeedsMotion_QueuesTurnThenMove()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 0f;

        // Target due east (+X) -> heading 90. Far enough that get_command
        // says motion is needed (default DistanceToObject=0.6).
        h.Manager.MoveToPosition(new Position(1u, new Vector3(10f, 0f, 0f), Quaternion.Identity), new MovementParameters());

        var nodes = h.Manager.PendingActions.ToList();
        Assert.Equal(2, nodes.Count);
        Assert.Equal(MovementType.TurnToHeading, nodes[0].Type);
        Assert.Equal(90f, nodes[0].Heading, 2);
        Assert.Equal(MovementType.MoveToPosition, nodes[1].Type);
    }

    [Fact]
    public void MoveToPosition_AlreadyInRange_NoMotionNodesQueued()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);

        // Target within default DistanceToObject (0.6) -> get_command idles.
        h.Manager.MoveToPosition(new Position(1u, new Vector3(0.1f, 0f, 0f), Quaternion.Identity), new MovementParameters());

        Assert.Empty(h.Manager.PendingActions);
    }

    [Fact]
    public void MoveToPosition_UseFinalHeading_AppendsAbsoluteFinalTurnNode()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);

        var p = new MovementParameters { UseFinalHeading = true, DesiredHeading = 270f };
        h.Manager.MoveToPosition(new Position(1u, new Vector3(10f, 0f, 0f), Quaternion.Identity), p);

        var nodes = h.Manager.PendingActions.ToList();
        // Turn-to-face(90) -> MoveToPosition -> Turn-to-final(270, ABSOLUTE).
        Assert.Equal(3, nodes.Count);
        Assert.Equal(MovementType.TurnToHeading, nodes[2].Type);
        Assert.Equal(270f, nodes[2].Heading, 2);
    }

    [Fact]
    public void MoveToPosition_UseFinalHeadingOnly_NoMotionNeeded_QueuesOnlyFinalTurn()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);

        var p = new MovementParameters { UseFinalHeading = true, DesiredHeading = 45f };
        // Already in range -> no move/turn-to-face nodes, only the final turn.
        h.Manager.MoveToPosition(new Position(1u, new Vector3(0.1f, 0f, 0f), Quaternion.Identity), p);

        var nodes = h.Manager.PendingActions.ToList();
        Assert.Single(nodes);
        Assert.Equal(MovementType.TurnToHeading, nodes[0].Type);
        Assert.Equal(45f, nodes[0].Heading, 2);
    }

    [Fact]
    public void MoveToPosition_ClearsStickyBit_EvenIfArgumentRequestedIt()
    {
        var h = new MoveToManagerHarness();
        var p = new MovementParameters { Sticky = true };
        h.Manager.MoveToPosition(new Position(1u, new Vector3(0.1f, 0f, 0f), Quaternion.Identity), p);

        Assert.False(h.Manager.Params.Sticky);
    }

    [Fact]
    public void MoveToPosition_MovementTypeAndStartingPositionSet()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(2u, new Vector3(1f, 2f, 3f), Quaternion.Identity);

        h.Manager.MoveToPosition(new Position(2u, new Vector3(1f, 12f, 3f), Quaternion.Identity), new MovementParameters());

        Assert.Equal(MovementType.MoveToPosition, h.Manager.MovementTypeState);
        Assert.Equal(h.WorldPosition, h.Manager.StartingPosition);
    }

    // ── TurnToHeading (§3e): ONE node, immediate BeginNextNode ─────────────

    [Fact]
    public void TurnToHeading_QueuesExactlyOneNode_WithDesiredHeading()
    {
        var h = new MoveToManagerHarness();
        h.Heading = 0f;

        h.Manager.TurnToHeading(new MovementParameters { DesiredHeading = 123f });

        var nodes = h.Manager.PendingActions.ToList();
        Assert.Equal(MovementType.TurnToHeading, h.Manager.MovementTypeState);
        Assert.NotEqual(0u, h.Manager.CurrentCommand);
        Assert.Single(nodes);
        Assert.Equal(123f, nodes[0].Heading, 2);
    }

    [Fact]
    public void TurnToHeading_ClearsStickyBit()
    {
        var h = new MoveToManagerHarness();
        h.Manager.TurnToHeading(new MovementParameters { Sticky = true, DesiredHeading = 45f });
        Assert.False(h.Manager.Params.Sticky);
    }

    [Fact]
    public void TurnToHeading_AlreadyFacingTarget_BeginNextNodeCompletesImmediately()
    {
        var h = new MoveToManagerHarness();
        h.Heading = 90f;

        h.Manager.TurnToHeading(new MovementParameters { DesiredHeading = 90f });

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
        Assert.Empty(h.Manager.PendingActions);
        Assert.True(h.StopCompletelyCalls >= 1);
    }

    // ── MoveToObject deferred start (§3b + §6f via HandleUpdateTarget) ─────

    [Fact]
    public void MoveToObject_NoNodesQueuedUntilTargetCallback()
    {
        var h = new MoveToManagerHarness();
        h.Manager.MoveToObject(objectId: 0x50002222u, topLevelId: 0x50002222u, radius: 1f, height: 2f, new MovementParameters());

        Assert.Empty(h.Manager.PendingActions);
        Assert.Equal(MovementType.MoveToObject, h.Manager.MovementTypeState);
        Assert.False(h.Manager.Initialized);
        Assert.Single(h.SetTargetCalls);
        Assert.Equal((0u, 0x50002222u, 0.5f, 0.0), h.SetTargetCalls[0]);
    }

    [Fact]
    public void MoveToObject_PreservesStickyBit_UnlikePositionMoves()
    {
        var h = new MoveToManagerHarness();
        var p = new MovementParameters { Sticky = true };
        h.Manager.MoveToObject(0x50002222u, 0x50002222u, 1f, 2f, p);

        Assert.True(h.Manager.Params.Sticky);
    }

    [Fact]
    public void MoveToObject_FirstTargetCallback_BuildsNodePlanViaInternal()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 0f;

        h.Manager.MoveToObject(0x50002222u, 0x50002222u, radius: 0.5f, height: 2f, new MovementParameters());

        var target = new Position(1u, new Vector3(10f, 0f, 0f), Quaternion.Identity);
        h.Manager.HandleUpdateTarget(new TargetInfo(0x50002222u, TargetStatus.Ok, target, target));

        Assert.True(h.Manager.Initialized);
        var nodes = h.Manager.PendingActions.ToList();
        Assert.Equal(2, nodes.Count);
        Assert.Equal(MovementType.TurnToHeading, nodes[0].Type);
        Assert.Equal(90f, nodes[0].Heading, 2);
        Assert.Equal(MovementType.MoveToPosition, nodes[1].Type);
    }

    [Fact]
    public void MoveToObject_UseFinalHeading_RelativeToInterpolatedHeading()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 0f;

        var p = new MovementParameters { UseFinalHeading = true, DesiredHeading = 10f };
        h.Manager.MoveToObject(0x50002222u, 0x50002222u, 0.5f, 2f, p);

        var target = new Position(1u, new Vector3(10f, 0f, 0f), Quaternion.Identity); // heading 90
        h.Manager.HandleUpdateTarget(new TargetInfo(0x50002222u, TargetStatus.Ok, target, target));

        var nodes = h.Manager.PendingActions.ToList();
        Assert.Equal(3, nodes.Count);
        // RELATIVE: iHeading(90) + desired(10) = 100 -- unlike MoveToPosition's absolute form.
        Assert.Equal(100f, nodes[2].Heading, 2);
    }

    [Fact]
    public void MoveToObject_SelfTarget_CleansUpImmediately_NoSetTarget()
    {
        var h = new MoveToManagerHarness();
        h.Manager.MoveToObject(h.SelfId, h.SelfId, 1f, 2f, new MovementParameters());

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
        Assert.Empty(h.SetTargetCalls);
    }

    // ── TurnToObject deferred start (§3d + §6g) ────────────────────────────

    [Fact]
    public void TurnToObject_NoNodesQueuedUntilTargetCallback()
    {
        var h = new MoveToManagerHarness();
        h.Manager.TurnToObject(0x50003333u, 0x50003333u, new MovementParameters());

        Assert.Empty(h.Manager.PendingActions);
        Assert.False(h.Manager.Initialized);
        Assert.Single(h.SetTargetCalls);
    }

    [Fact]
    public void TurnToObject_FirstCallback_QueuesExactlyOneTurnNode_FacingObject()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 0f; // already facing north — target due EAST forces an actual turn to queue.

        h.Manager.TurnToObject(0x50003333u, 0x50003333u, new MovementParameters { DesiredHeading = 200f });

        var target = new Position(1u, new Vector3(10f, 0f, 0f), Quaternion.Identity); // heading 90 (east)
        h.Manager.TurnToObject_Internal(target);

        var nodes = h.Manager.PendingActions.ToList();
        Assert.Single(nodes);
        Assert.Equal(MovementType.TurnToHeading, nodes[0].Type);
        Assert.Equal(90f, nodes[0].Heading, 2); // face-the-object (east), NOT 200
        Assert.True(h.Manager.Initialized);
    }

    [Fact]
    public void TurnToObject_FirstCallback_AlreadyFacingObject_CompletesImmediately()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 0f;

        h.Manager.TurnToObject(0x50003333u, 0x50003333u, new MovementParameters());
        var target = new Position(1u, new Vector3(0f, 10f, 0f), Quaternion.Identity); // heading 0 (north) — already facing it.
        h.Manager.TurnToObject_Internal(target);

        Assert.Empty(h.Manager.PendingActions);
        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void TurnToObject_SelfTarget_CleansUpImmediately()
    {
        var h = new MoveToManagerHarness();
        h.Manager.TurnToObject(h.SelfId, h.SelfId, new MovementParameters());

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
        Assert.Empty(h.SetTargetCalls);
    }

    [Fact]
    public void TurnToObject_StopCompletelyOnlyWhenBitSet()
    {
        var h1 = new MoveToManagerHarness();
        h1.Manager.TurnToObject(0x50003333u, 0x50003333u, new MovementParameters { StopCompletelyFlag = false });
        Assert.Equal(0, h1.StopCompletelyCalls);

        var h2 = new MoveToManagerHarness();
        h2.Manager.TurnToObject(0x50003333u, 0x50003333u, new MovementParameters { StopCompletelyFlag = true });
        Assert.Equal(1, h2.StopCompletelyCalls);
    }
}
