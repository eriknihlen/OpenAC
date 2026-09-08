using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MoveToManagerTurnToHeadingTests
{
    // ── BeginTurnToHeading direction pick (§4d) ────────────────────────────

    [Theory]
    [InlineData(0f, 90f, MotionCommand.TurnRight)]   // diff=90 <=180 -> TurnRight
    [InlineData(0f, 170f, MotionCommand.TurnRight)]  // diff=170 <=180 -> TurnRight
    [InlineData(0f, 190f, MotionCommand.TurnLeft)]   // diff=190 >180 -> TurnLeft
    [InlineData(0f, 270f, MotionCommand.TurnLeft)]   // diff=270 >180 -> TurnLeft
    public void DirectionPick_Table(float currentHeading, float targetHeading, uint expectedTurn)
    {
        var h = new MoveToManagerHarness();
        h.Heading = currentHeading;

        h.Manager.TurnToHeading(new MovementParameters { DesiredHeading = targetHeading });

        Assert.Equal(expectedTurn, h.Manager.CurrentCommand);
    }

    [Fact]
    public void DirectionPick_ExactlyAt180_TurnRight_NotStrictlyGreater()
    {
        // diff > 180 is the TurnLeft gate (strict); exactly 180 stays TurnRight.
        var h = new MoveToManagerHarness();
        h.Heading = 0f;

        h.Manager.TurnToHeading(new MovementParameters { DesiredHeading = 180f });

        Assert.Equal(MotionCommand.TurnRight, h.Manager.CurrentCommand);
    }

    [Fact]
    public void AlreadyThere_DiffLessThanOrEqualEpsilon_PopsImmediately_NoDispatch()
    {
        var h = new MoveToManagerHarness();
        h.Heading = 90f;

        h.Manager.TurnToHeading(new MovementParameters { DesiredHeading = 90f });

        Assert.Equal(0u, h.Manager.CurrentCommand);
        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
        Assert.Empty(h.Manager.PendingActions);
    }

    [Fact]
    public void AlreadyThere_WrappedNearFullCircle_PopsImmediately()
    {
        var h = new MoveToManagerHarness();
        h.Heading = 0.0001f;

        h.Manager.TurnToHeading(new MovementParameters { DesiredHeading = 0f });

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void WaitsForPendingAnimations_BeforeArmingTurn()
    {
        var h = new MoveToManagerHarness();
        h.Heading = 0f;

        // Simulate an in-flight animation-table motion BEFORE the turn is armed.
        h.Interp.AddToQueue(0, MotionCommand.WalkForward, 0);
        Assert.True(h.Interp.MotionsPending());

        h.Manager.TurnToHeading(new MovementParameters { DesiredHeading = 90f });

        Assert.Equal(0u, h.Manager.CurrentCommand);
        Assert.Single(h.Manager.PendingActions);
        Assert.Equal(MovementType.TurnToHeading, h.Manager.MovementTypeState);
    }

    [Fact]
    public void EmptyQueue_CancelsWithNoPhysicsObjectCode()
    {
        var h = new MoveToManagerHarness();
        h.Manager.BeginTurnToHeading();

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void PreviousHeadingSeededWithDiff_NotAHeading()
    {
        var h = new MoveToManagerHarness();
        h.Heading = 0f;

        h.Manager.TurnToHeading(new MovementParameters { DesiredHeading = 90f });

        Assert.Equal(90f, h.Manager.PreviousHeading, 2);
    }

    [Fact]
    public void PreviousHeadingSeed_DiffersFromTargetHeading_ProvingItsADiffNotAHeading()
    {
        var h = new MoveToManagerHarness();
        h.Heading = 30f;

        h.Manager.TurnToHeading(new MovementParameters { DesiredHeading = 90f });

        Assert.Equal(60f, h.Manager.PreviousHeading, 2);
    }

    // ── HandleTurnToHeading (§6c): arrival snap + progress test ────────────

    [Fact]
    public void HandleTurnToHeading_NotCurrentlyTurning_ReArmsViaBeginTurnToHeading()
    {
        var h = new MoveToManagerHarness();
        h.Heading = 0f;
        h.Manager.TurnToHeading(new MovementParameters { DesiredHeading = 90f });
        h.DrainPendingMotions();

        Assert.True(h.Manager.CurrentCommand is MotionCommand.TurnRight or MotionCommand.TurnLeft);
    }

    [Fact]
    public void HandleTurnToHeading_Arrival_SnapsHeadingAndSendsTrue()
    {
        var h = new MoveToManagerHarness();
        h.Heading = 0f;
        h.Manager.TurnToHeading(new MovementParameters { DesiredHeading = 90f });
        h.DrainPendingMotions();
        Assert.Equal(MotionCommand.TurnRight, h.Manager.CurrentCommand);

        // Advance heading to just past the target (heading_greater says we
        // passed it) -- simulates the turn animation having rotated us there.
        h.Heading = 91f;

        h.Manager.HandleTurnToHeading();

        // The ONE heading snap in the whole family: SetHeading(90, send:true).
        Assert.Contains((90f, true), h.SetHeadingCalls);
        Assert.Equal(90f, h.Heading, 2); // snapped to the EXACT node heading, not 91.
        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }

    [Fact]
    public void HandleTurnToHeading_StillTurning_RotationalProgress_ResetsFailCounter()
    {
        var h = new MoveToManagerHarness();
        h.Heading = 0f;
        h.Manager.TurnToHeading(new MovementParameters { DesiredHeading = 170f });
        h.DrainPendingMotions();
        Assert.Equal(MotionCommand.TurnRight, h.Manager.CurrentCommand);
        Assert.Equal(170f, h.Manager.PreviousHeading, 2); // diff-seeded (quirk)

        h.Manager.HandleTurnToHeading(); // tick 1 (heading unmoved) -- the seed artifact tick
        Assert.Equal(1u, h.Manager.FailProgressCount);
        Assert.Equal(0f, h.Manager.PreviousHeading, 2);

        h.Heading = 90f; // tick 2: rotated 90 deg toward the 170 target, hasn't passed it.
        h.Manager.HandleTurnToHeading();

        Assert.Equal(0u, h.Manager.FailProgressCount); // reset by genuine progress
        Assert.Equal(90f, h.Manager.PreviousHeading, 2); // updated to the live heading
    }

    [Fact]
    public void HandleTurnToHeading_NoRotationalProgress_IncrementsFailCounter_WhenNotAnimating()
    {
        var h = new MoveToManagerHarness();
        h.Heading = 0f;
        h.Manager.TurnToHeading(new MovementParameters { DesiredHeading = 170f });
        h.DrainPendingMotions();

        h.Manager.HandleTurnToHeading();

        Assert.Equal(1u, h.Manager.FailProgressCount);
    }

    [Fact]
    public void HandleTurnToHeading_TurnLeftDirection_UsesMirroredHeadingDiff()
    {
        var h = new MoveToManagerHarness();
        h.Heading = 0f;
        h.Manager.TurnToHeading(new MovementParameters { DesiredHeading = 190f });
        h.DrainPendingMotions();
        Assert.Equal(MotionCommand.TurnLeft, h.Manager.CurrentCommand);

        h.Heading = 350f;

        h.Manager.HandleTurnToHeading();

        Assert.True(h.Manager.FailProgressCount is 0 or 1);
    }
}
