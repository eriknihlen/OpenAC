using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MoveToManagerArrivalAndProgressTests
{

    [Fact]
    public void CheckProgressMade_WithinOneSecondWindow_AlwaysTrue_TooSoonToJudge()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), new MovementParameters { UseSpheres = false });

        h.Advance(0.5); // < 1.0s since PreviousDistanceTime

        Assert.True(h.Manager.CheckProgressMade(currentDistance: 19.9f));
    }

    [Fact]
    public void CheckProgressMade_ExactlyOneSecond_StillTooSoon_Inclusive()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), new MovementParameters { UseSpheres = false });

        h.Advance(1.0); // elapsed <= 1.0 -> still true (§5b: "elapsed <= 1.0 -> return 1")

        Assert.True(h.Manager.CheckProgressMade(currentDistance: 19.9f));
    }

    [Fact]
    public void CheckProgressMade_AfterWindow_SufficientIncrementalAndOverallRate_True()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), new MovementParameters { UseSpheres = false });

        // PreviousDistance/OriginalDistance seeded to 20 at t=0.
        h.Advance(2.0); // 2s elapsed

        bool progress = h.Manager.CheckProgressMade(currentDistance: 18f);

        Assert.True(progress);
        Assert.Equal(0u, h.Manager.FailProgressCount);
        Assert.Equal(18f, h.Manager.PreviousDistance, 2);
    }

    [Fact]
    public void CheckProgressMade_InsufficientIncrementalRate_False_CheckpointDoesNotAdvance()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), new MovementParameters { UseSpheres = false });

        h.Advance(2.0);

        bool progress = h.Manager.CheckProgressMade(currentDistance: 19.9f);

        Assert.False(progress);
        Assert.Equal(20f, h.Manager.PreviousDistance, 2); // unchanged
    }

    [Fact]
    public void CheckProgressMade_GoodIncrementalButBadOverallRate_False()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), new MovementParameters { UseSpheres = false });

        h.Advance(2.0);
        Assert.True(h.Manager.CheckProgressMade(19f)); // incremental 0.5/s since t=0 — overall also 0.5/s here, still passes.

        h.Advance(200.0);
        bool progress = h.Manager.CheckProgressMade(18.9f);

        Assert.False(progress);
    }

    [Fact]
    public void CheckProgressMade_MovingAway_UsesOpeningDistance()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;

        // pure-away move: MoveTowards=false, MoveAway=true, MinDistance large
        // so get_command picks WalkForward+movingAway.
        var p = new MovementParameters { MoveTowards = false, MoveAway = true, MinDistance = 50f, UseSpheres = false };
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), p);
        Assert.True(h.Manager.MovingAway);

        h.Advance(2.0);
        bool progress = h.Manager.CheckProgressMade(25f);

        Assert.True(progress);
    }


    [Fact]
    public void HandleMoveToPosition_Chase_ArrivesWhenDistLessOrEqualDto()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;

        var p = new MovementParameters { DistanceToObject = 5f, UseSpheres = false };
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), p);
        h.DrainPendingMotions();

        h.WorldPosition = new Position(1u, new Vector3(16f, 0f, 0f), Quaternion.Identity); // dist=4 <= dto(5)
        h.Advance(2.0);

        h.Manager.HandleMoveToPosition();

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void HandleMoveToPosition_Chase_NotArrivedYet_StaysActive()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;

        var p = new MovementParameters { DistanceToObject = 0.6f, UseSpheres = false };
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), p);
        h.DrainPendingMotions();

        h.WorldPosition = new Position(1u, new Vector3(5f, 0f, 0f), Quaternion.Identity); // dist=15, still far
        h.Advance(2.0);

        h.Manager.HandleMoveToPosition();

        Assert.Equal(MovementType.MoveToPosition, h.Manager.MovementTypeState);
    }

    [Fact]
    public void HandleMoveToPosition_Flee_ArrivesWhenDistGreaterOrEqualMinDistance()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;

        var p = new MovementParameters { MoveTowards = false, MoveAway = true, MinDistance = 10f, UseSpheres = false };
        h.Manager.MoveToPosition(new Position(1u, new Vector3(5f, 0f, 0f), Quaternion.Identity), p);
        Assert.True(h.Manager.MovingAway);
        h.DrainPendingMotions();

        // Mover has fled to distance 12 (>= MinDistance 10) -> arrived.
        h.WorldPosition = new Position(1u, new Vector3(-7f, 0f, 0f), Quaternion.Identity); // dist to (5,0,0) = 12
        h.Advance(2.0);

        h.Manager.HandleMoveToPosition();

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    // ── Fail-distance (§6b, WeenieError.YouChargedTooFar) ──────────────────

    [Fact]
    public void HandleMoveToPosition_ProgressMadeButOverFailDistance_CancelsAsYouChargedTooFar()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;

        var p = new MovementParameters { DistanceToObject = 0.6f, FailDistance = 5f, UseSpheres = false };
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), p);
        h.DrainPendingMotions();

        h.WorldPosition = new Position(1u, new Vector3(8f, 0f, 0f), Quaternion.Identity);
        h.Advance(2.0);

        h.Manager.HandleMoveToPosition();

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void HandleMoveToPosition_NoProgressButWithinFailDistance_StaysActive_NoCancel()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;

        var p = new MovementParameters { DistanceToObject = 0.6f, FailDistance = 100f, UseSpheres = false };
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), p);
        h.DrainPendingMotions();

        h.WorldPosition = new Position(1u, new Vector3(0f, 1f, 0f), Quaternion.Identity); // 1 unit from start, well under FailDistance
        h.Advance(2.0);

        h.Manager.HandleMoveToPosition();

        Assert.Equal(MovementType.MoveToPosition, h.Manager.MovementTypeState);
    }

    // ── FailProgressCount write-only bookkeeping (§8, do-not-invent) ───────

    [Fact]
    public void FailProgressCount_IncrementsOnStall_ButNoGiveUpThresholdExists()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;

        var p = new MovementParameters { DistanceToObject = 0.6f, FailDistance = float.MaxValue, UseSpheres = false };
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), p);
        h.DrainPendingMotions();

        for (int i = 0; i < 20; i++)
        {
            h.Advance(2.0);
            h.Manager.HandleMoveToPosition();
        }

        Assert.True(h.Manager.FailProgressCount >= 20);
        Assert.Equal(MovementType.MoveToPosition, h.Manager.MovementTypeState); // still active, no give-up
    }

    [Fact]
    public void FailProgressCount_NotIncremented_WhenInterpolating()
    {
        var h = new MoveToManagerHarness { IsInterpolatingValue = true };
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;

        var p = new MovementParameters { DistanceToObject = 0.6f, FailDistance = float.MaxValue, UseSpheres = false };
        h.Manager.MoveToPosition(new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity), p);
        h.DrainPendingMotions();

        h.Advance(2.0);
        h.Manager.HandleMoveToPosition();

        Assert.Equal(0u, h.Manager.FailProgressCount);
    }
}
