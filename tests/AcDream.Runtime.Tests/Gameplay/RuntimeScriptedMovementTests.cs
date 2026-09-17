using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeScriptedMovementTests
{
    [Fact]
    public void AWalkThatEndsIsLetGoOfAtAWalkSoTheBodyStopsWithoutLurchingIntoARun()
    {
        var movement = new RuntimeScriptedMovement();
        Assert.True(movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Walk, 0f)));
        Assert.True(movement.Advance(Sample(0d)) is { Forward: true, Run: false });

        Assert.True(movement.Stop(RuntimeMoveChannel.Travel));

        Assert.True(movement.Advance(Sample(0.1d)) is { Forward: false, Run: false, IsPersistentCommand: true });
        Assert.Null(movement.Advance(Sample(0.2d)));
    }

    [Fact]
    public void ARunThatEndsIsLetGoOfAtARun()
    {
        var movement = new RuntimeScriptedMovement();
        Assert.True(movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Run, 0f)));
        Assert.True(movement.Advance(Sample(0d)) is { Forward: true, Run: true });

        Assert.True(movement.Stop(RuntimeMoveChannel.Travel));

        Assert.True(movement.Advance(Sample(0.1d)) is { Forward: false, Run: true, IsPersistentCommand: true });
    }

    [Fact]
    public void AMoveHoldsItsDirectionAndCompletesAtItsDistance()
    {
        var movement = new RuntimeScriptedMovement();
        Assert.True(movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Walk, 5f)));

        Assert.True(movement.Advance(Sample(0d)) is { Forward: true, Run: false, IsPersistentCommand: true });
        movement.Advance(Sample(1d, north: 2f));
        MovementInput? arriving = movement.Advance(Sample(2d, north: 5f));

        Assert.Equal(RuntimeScriptedMoveState.Completed, movement.Snapshot.Travel.State);
        Assert.Equal(5f, movement.Snapshot.Travel.Covered, 3);
        Assert.True(arriving is { Forward: false, IsPersistentCommand: true });
        Assert.Null(movement.Advance(Sample(2.1d, north: 5f)));
    }

    [Theory]
    [InlineData(5.2f, 5f)]
    [InlineData(5.3f, 5.5f)]
    public void ADistanceEndsWithinHalfAStepOfItsAmount(float amount, float expected)
    {
        RuntimeScriptedMovement movement = Moving(RuntimeMoveDirection.Forward, amount);
        movement.Advance(Sample(0d));

        for (int step = 1; step <= 20 && movement.Snapshot.Travel.State == RuntimeScriptedMoveState.Moving; step++)
            movement.Advance(Sample(step / 30d, north: step * 0.5f));

        Assert.Equal(RuntimeScriptedMoveState.Completed, movement.Snapshot.Travel.State);
        Assert.Equal(expected, movement.Snapshot.Travel.Covered, 3);
    }

    [Fact]
    public void ATravelMoveCountsOnlyMovementAlongItsFacing()
    {
        RuntimeScriptedMovement movement = Moving(RuntimeMoveDirection.Forward, 0f);

        movement.Advance(Sample(0d, heading: 90f));
        movement.Advance(Sample(0.5d, east: 2f, north: 3f, heading: 90f));

        Assert.Equal(2f, movement.Snapshot.Travel.Covered, 3);
    }

    [Fact]
    public void AStrafeCountsOnlySidewaysMovement()
    {
        RuntimeScriptedMovement movement = Moving(RuntimeMoveDirection.StrafeLeft, 0f);

        movement.Advance(Sample(0d));
        movement.Advance(Sample(0.5d, east: -1.5f, north: 4f));

        Assert.Equal(1.5f, movement.Snapshot.Strafe.Covered, 3);
    }

    [Fact]
    public void RunningInACircleCountsThePathAndKeepsGoing()
    {
        const float radius = 3f;
        var movement = new RuntimeScriptedMovement();
        Assert.True(movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Run, 0f)));
        Assert.True(movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.TurnRight, RuntimeMovePace.Run, 0f)));

        movement.Advance(Sample(0d));
        for (int step = 1; step <= 36; step++)
        {
            float heading = step * 10f;
            float radians = heading * MathF.PI / 180f;
            movement.Advance(Sample(
                step * 0.2d,
                east: radius - (radius * MathF.Cos(radians)),
                north: radius * MathF.Sin(radians),
                heading: heading % 360f));
        }

        Assert.Equal(RuntimeScriptedMoveState.Moving, movement.Snapshot.Travel.State);
        Assert.InRange(movement.Snapshot.Travel.Covered, 18f, 19f);
        Assert.Equal(360f, movement.Snapshot.Turn.Covered, 1);
    }

    [Fact]
    public void AMoveThatStopsMakingProgressIsBlocked()
    {
        RuntimeScriptedMovement movement = Moving(RuntimeMoveDirection.Forward, 20f);

        movement.Advance(Sample(0d));
        movement.Advance(Sample(1.6d, north: 0.1f));

        Assert.Equal(RuntimeScriptedMoveState.Blocked, movement.Snapshot.Travel.State);
    }

    [Fact]
    public void AMoveMakingProgressKeepsGoing()
    {
        RuntimeScriptedMovement movement = Moving(RuntimeMoveDirection.Forward, 20f);

        movement.Advance(Sample(0d));
        for (int step = 1; step <= 4; step++)
            movement.Advance(Sample(step * 1.5d, north: step * 2f));

        Assert.Equal(RuntimeScriptedMoveState.Moving, movement.Snapshot.Travel.State);
    }

    [Fact]
    public void AMoveWithoutAnAmountEndsAtTheTimeLimit()
    {
        RuntimeScriptedMovement movement = Moving(RuntimeMoveDirection.StrafeLeft, 0f);

        movement.Advance(Sample(0d));
        for (int step = 1; step <= 20; step++)
            movement.Advance(Sample(step * 1.5d, east: -step * 3f));

        Assert.Equal(RuntimeScriptedMoveState.TimeLimit, movement.Snapshot.Strafe.State);
    }

    [Fact]
    public void AMoveTooSlowForItsDistanceEndsAtItsTimeLimit()
    {
        RuntimeScriptedMovement movement = Moving(RuntimeMoveDirection.Backward, 3f);

        movement.Advance(Sample(0d));
        for (int step = 1; step <= 4; step++)
            movement.Advance(Sample(step * 1.5d, north: -step * 0.5f));

        Assert.Equal(RuntimeScriptedMoveState.TimeLimit, movement.Snapshot.Travel.State);
    }

    [Fact]
    public void AMoveForSecondsCompletesWhenItsTimeIsUp()
    {
        RuntimeScriptedMovement movement = Moving(RuntimeMoveDirection.Forward, 20f, RuntimeMoveUnit.Seconds);

        movement.Advance(Sample(0d));
        for (int step = 1; step <= 39; step++)
            movement.Advance(Sample(step * 0.5d, north: step * 5f));
        Assert.Equal(RuntimeScriptedMoveState.Moving, movement.Snapshot.Travel.State);

        movement.Advance(Sample(20d, north: 200f));

        Assert.Equal(RuntimeScriptedMoveState.Completed, movement.Snapshot.Travel.State);
        Assert.Equal(200f, movement.Snapshot.Travel.Covered, 3);
        Assert.Equal(20f, movement.Snapshot.Travel.ElapsedSeconds, 3);
    }

    [Fact]
    public void AMoveForSecondsCanOutlastTheLimitForAMoveWithoutAnAmount()
    {
        RuntimeScriptedMovement movement = Moving(RuntimeMoveDirection.Forward, 60f, RuntimeMoveUnit.Seconds);

        movement.Advance(Sample(0d));
        for (int step = 1; step <= 90; step++)
            movement.Advance(Sample(step * 0.5d, north: step * 5f));

        Assert.Equal(RuntimeScriptedMoveState.Moving, movement.Snapshot.Travel.State);
    }

    [Fact]
    public void MovesOnDifferentChannelsAreHeldTogether()
    {
        var movement = new RuntimeScriptedMovement();
        Assert.True(movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Run, 0f)));
        Assert.True(movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.StrafeLeft, RuntimeMovePace.Run, 0f)));
        Assert.True(movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.TurnRight, RuntimeMovePace.Run, 0f)));

        Assert.True(movement.Advance(Sample(0d)) is
        {
            Forward: true,
            StrafeLeft: true,
            TurnRight: true,
            Backward: false,
            StrafeRight: false,
            TurnLeft: false,
            Run: true,
        });
    }

    [Fact]
    public void ANewMoveReplacesOnlyTheMoveOnItsOwnChannel()
    {
        var movement = new RuntimeScriptedMovement();
        movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Run, 0f));
        movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.TurnLeft, RuntimeMovePace.Run, 0f));

        Assert.True(movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.Backward, RuntimeMovePace.Walk, 3f)));

        RuntimeScriptedMoveSnapshot snapshot = movement.Snapshot;
        Assert.Equal(3, snapshot.Travel.Sequence);
        Assert.Equal(RuntimeMoveDirection.Backward, snapshot.Travel.Request.Direction);
        Assert.Equal(2, snapshot.Turn.Sequence);
        Assert.Equal(RuntimeScriptedMoveState.Moving, snapshot.Turn.State);
        Assert.Equal(RuntimeScriptedMoveState.None, snapshot.Strafe.State);
    }

    [Fact]
    public void EndingOneMoveKeepsTheOthersHeld()
    {
        var movement = new RuntimeScriptedMovement();
        movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Run, 0f));
        movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.TurnRight, RuntimeMovePace.Run, 30f));

        movement.Advance(Sample(0d));
        MovementInput? input = movement.Advance(Sample(0.1d, north: 1f, heading: 35f), out float? landed);

        Assert.Equal(RuntimeScriptedMoveState.Completed, movement.Snapshot.Turn.State);
        Assert.True(input is { Forward: true, TurnRight: false });
        Assert.Equal(30f, landed!.Value, 3);
    }

    [Fact]
    public void ATurnLandsExactlyOnItsAngleAcrossNorth()
    {
        RuntimeScriptedMovement movement = Moving(RuntimeMoveDirection.TurnRight, 90f);

        Assert.True(movement.Advance(Sample(0d, heading: 300f)) is { TurnRight: true });
        movement.Advance(Sample(0.5d, heading: 350f), out float? midway);
        movement.Advance(Sample(1d, heading: 36f), out float? landed);

        Assert.Null(midway);
        Assert.Equal(RuntimeScriptedMoveState.Completed, movement.Snapshot.Turn.State);
        Assert.Equal(90f, movement.Snapshot.Turn.Covered, 3);
        Assert.Equal(30f, landed!.Value, 3);
    }

    [Fact]
    public void ALeftTurnLandsExactlyOnItsAngle()
    {
        RuntimeScriptedMovement movement = Moving(RuntimeMoveDirection.TurnLeft, 5f);

        movement.Advance(Sample(0d, heading: 100f));
        movement.Advance(Sample(1d / 30d, heading: 95.5f));
        movement.Advance(Sample(2d / 30d, heading: 91f), out float? landed);

        Assert.Equal(95f, landed!.Value, 3);
    }

    [Fact]
    public void TurningTheWrongWayIsNotProgress()
    {
        RuntimeScriptedMovement movement = Moving(RuntimeMoveDirection.TurnLeft, 45f);

        movement.Advance(Sample(0d, heading: 90f));
        movement.Advance(Sample(0.5d, heading: 120f));

        Assert.Equal(0f, movement.Snapshot.Turn.Covered);
    }

    [Fact]
    public void StopEndsEveryMoveAndReleasesOnce()
    {
        var movement = new RuntimeScriptedMovement();
        movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.Backward, RuntimeMovePace.Run, 0f));
        movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.TurnLeft, RuntimeMovePace.Run, 0f));
        movement.Advance(Sample(0d));

        Assert.True(movement.Stop());

        Assert.Equal(RuntimeScriptedMoveState.Stopped, movement.Snapshot.Travel.State);
        Assert.Equal(RuntimeScriptedMoveState.Stopped, movement.Snapshot.Turn.State);
        Assert.True(movement.Advance(Sample(0.1d)) is { Backward: false, TurnLeft: false, IsPersistentCommand: true });
        Assert.Null(movement.Advance(Sample(0.2d)));
        Assert.False(movement.Stop());
    }

    [Fact]
    public void StoppingOneChannelKeepsTheOthersHeld()
    {
        var movement = new RuntimeScriptedMovement();
        movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Run, 0f));
        movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.StrafeRight, RuntimeMovePace.Run, 0f));
        movement.Advance(Sample(0d));

        Assert.True(movement.Stop(RuntimeMoveChannel.Strafe));
        Assert.False(movement.Stop(RuntimeMoveChannel.Turn));

        Assert.Equal(RuntimeScriptedMoveState.Stopped, movement.Snapshot.Strafe.State);
        Assert.True(movement.Advance(Sample(0.1d)) is { Forward: true, StrafeRight: false });
    }

    [Fact]
    public void ThePlayersOwnMovementInterruptsEveryMove()
    {
        var movement = new RuntimeScriptedMovement();
        movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Run, 10f));
        movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.TurnLeft, RuntimeMovePace.Run, 0f));

        movement.Advance(Sample(0d));
        movement.Advance(Sample(0.5d, manual: true));

        Assert.Equal(RuntimeScriptedMoveState.Interrupted, movement.Snapshot.Travel.State);
        Assert.Equal(RuntimeScriptedMoveState.Interrupted, movement.Snapshot.Turn.State);
    }

    [Fact]
    public void PortalSpaceLosesEveryMoveAndTheJump()
    {
        var movement = new RuntimeScriptedMovement();
        movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Run, 10f));
        movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.StrafeLeft, RuntimeMovePace.Run, 0f));
        movement.BeginJump(1f);

        movement.Advance(Sample(0d, portal: true));

        Assert.Equal(RuntimeScriptedMoveState.Lost, movement.Snapshot.Travel.State);
        Assert.Equal(RuntimeScriptedMoveState.Lost, movement.Snapshot.Strafe.State);
        Assert.False(movement.Snapshot.JumpCharging);
    }

    [Fact]
    public void AJumpIsHeldForItsChargeThenReleased()
    {
        var movement = new RuntimeScriptedMovement();
        Assert.True(movement.BeginJump(0.5f));

        Assert.True(movement.Advance(Sample(10d)) is { Jump: true });
        Assert.True(movement.Advance(Sample(10.4d)) is { Jump: true });
        Assert.True(movement.Advance(Sample(10.5d)) is { Jump: false, IsPersistentCommand: true });
        Assert.False(movement.Snapshot.JumpCharging);
        Assert.Null(movement.Advance(Sample(10.6d)));
    }

    [Fact]
    public void AJumpLeftAtAPaceChargesStandingAndPressesForwardAsItReleases()
    {
        var movement = new RuntimeScriptedMovement();
        Assert.True(movement.BeginJump(0.5f, RuntimeMovePace.Run));

        Assert.True(movement.Advance(Sample(10d)) is { Jump: true, Forward: false });
        Assert.True(movement.Advance(Sample(10.4d)) is { Jump: true, Forward: false });
        Assert.True(movement.Advance(Sample(10.5d)) is { Jump: false, Forward: true, Run: true });
        Assert.Equal(RuntimeScriptedMoveState.Moving, movement.Snapshot.Travel.State);
        Assert.Equal(RuntimeMovePace.Run, movement.Snapshot.Travel.Request.Pace);
    }

    [Fact]
    public void AJumpDuringMovesKeepsThemHeld()
    {
        var movement = new RuntimeScriptedMovement();
        movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Run, 0f));
        movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.StrafeLeft, RuntimeMovePace.Run, 0f));

        movement.BeginJump(1f);

        Assert.True(movement.Advance(Sample(0d)) is { Forward: true, StrafeLeft: true, Jump: true });
    }

    [Fact]
    public void ThePaceComesFromTheTravelMoveThenTheStrafeThenTheTurn()
    {
        var movement = new RuntimeScriptedMovement();

        movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.TurnLeft, RuntimeMovePace.Walk, 0f));
        Assert.True(movement.Advance(Sample(0d)) is { Run: false });

        movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.StrafeLeft, RuntimeMovePace.Run, 0f));
        Assert.True(movement.Advance(Sample(0.1d)) is { Run: true });

        movement.Begin(new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Walk, 0f));
        Assert.True(movement.Advance(Sample(0.2d)) is { Run: false });
    }

    [Theory]
    [InlineData(RuntimeMoveDirection.Forward, 500f, RuntimeMoveUnit.MetersOrDegrees, true)]
    [InlineData(RuntimeMoveDirection.TurnLeft, 3600f, RuntimeMoveUnit.MetersOrDegrees, true)]
    [InlineData(RuntimeMoveDirection.Backward, 300f, RuntimeMoveUnit.Seconds, true)]
    [InlineData(RuntimeMoveDirection.Forward, -1f, RuntimeMoveUnit.MetersOrDegrees, false)]
    [InlineData(RuntimeMoveDirection.Forward, 501f, RuntimeMoveUnit.MetersOrDegrees, false)]
    [InlineData(RuntimeMoveDirection.StrafeLeft, float.NaN, RuntimeMoveUnit.MetersOrDegrees, false)]
    [InlineData(RuntimeMoveDirection.TurnRight, 3601f, RuntimeMoveUnit.MetersOrDegrees, false)]
    [InlineData(RuntimeMoveDirection.Forward, 301f, RuntimeMoveUnit.Seconds, false)]
    [InlineData((RuntimeMoveDirection)99, 1f, RuntimeMoveUnit.MetersOrDegrees, false)]
    [InlineData(RuntimeMoveDirection.Forward, 1f, (RuntimeMoveUnit)7, false)]
    public void AMoveIsPossibleOnlyWithinItsLimits(
        RuntimeMoveDirection direction,
        float amount,
        RuntimeMoveUnit unit,
        bool possible)
    {
        Assert.Equal(possible, new RuntimeScriptedMovement().Begin(
            new RuntimeMoveRequest(direction, RuntimeMovePace.Run, amount, unit)));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.5f)]
    public void AnImpossibleJumpIsRefused(float power)
    {
        Assert.False(new RuntimeScriptedMovement().BeginJump(power));
    }

    [Fact]
    public void AStepTooLongForOneFrameIsNotProgress()
    {
        RuntimeScriptedMovement movement = Moving(RuntimeMoveDirection.Forward, 0f);

        movement.Advance(Sample(0d));
        movement.Advance(Sample(0.1d, north: 50f));

        Assert.Equal(0f, movement.Snapshot.Travel.Covered);
    }

    [Fact]
    public void TheFramesOwnInputPassesThroughWhenNothingIsScripted()
    {
        using var state = new RuntimeLocalPlayerMovementState();
        var source = new RuntimeScriptedMovementInputSource(state, new FixedInput { Input = new MovementInput(Forward: true) });

        Assert.True(source.Capture().Forward);
        Assert.False(state.BeginMove(new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Run, 1f)));
    }

    [Fact]
    public void AScriptedMoveTakesTheFrameUntilThePlayerMoves()
    {
        using var state = new RuntimeLocalPlayerMovementState();
        PlayerMovementController controller = Controller();
        state.Controller = controller;
        var frame = new FixedInput();
        var source = new RuntimeScriptedMovementInputSource(state, frame);

        Assert.True(state.BeginMove(new RuntimeMoveRequest(RuntimeMoveDirection.StrafeLeft, RuntimeMovePace.Run, 0f)));
        Assert.True(source.Capture() is { StrafeLeft: true, IsPersistentCommand: true });

        frame.Input = new MovementInput(Forward: true);
        source.Capture();

        Assert.Equal(RuntimeScriptedMoveState.Interrupted, state.ScriptedMove.Strafe.State);
        Assert.Equal(RuntimeScriptedMoveState.Interrupted, state.Snapshot.ScriptedMove.Strafe.State);
    }

    [Fact]
    public void ATurnLandsTheBodyOnItsAngle()
    {
        using var state = new RuntimeLocalPlayerMovementState();
        PlayerMovementController controller = Controller();
        state.Controller = controller;
        var source = new RuntimeScriptedMovementInputSource(state, new FixedInput());
        controller.Yaw = MoveToMath.YawFromHeading(10f);

        Assert.True(state.BeginMove(new RuntimeMoveRequest(RuntimeMoveDirection.TurnRight, RuntimeMovePace.Run, 20f)));
        source.Capture();
        controller.Yaw = MoveToMath.YawFromHeading(34f);

        Assert.True(source.Capture() is { TurnRight: false });
        Assert.Equal(RuntimeScriptedMoveState.Completed, state.ScriptedMove.Turn.State);
        Assert.Equal(30f, MoveToMath.HeadingFromYaw(controller.Yaw), 2);
    }

    [Fact]
    public void ATurnEndingChangesTheMovementRevision()
    {
        using var state = new RuntimeLocalPlayerMovementState();
        PlayerMovementController controller = Controller();
        state.Controller = controller;
        var source = new RuntimeScriptedMovementInputSource(state, new FixedInput());
        controller.Yaw = MoveToMath.YawFromHeading(0f);

        Assert.True(state.BeginMove(new RuntimeMoveRequest(RuntimeMoveDirection.TurnRight, RuntimeMovePace.Run, 5f)));
        source.Capture();
        long before = state.Revision;
        controller.Yaw = MoveToMath.YawFromHeading(9f);
        source.Capture();

        Assert.Equal(RuntimeScriptedMoveState.Completed, state.ScriptedMove.Turn.State);
        Assert.True(state.Revision > before);
    }

    private static PlayerMovementController Controller()
    {
        var controller = new PlayerMovementController(new PhysicsEngine());
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001u, new Vector3(96f, 96f, 50f));
        return controller;
    }

    private static RuntimeScriptedMovement Moving(
        RuntimeMoveDirection direction,
        float amount,
        RuntimeMoveUnit unit = RuntimeMoveUnit.MetersOrDegrees)
    {
        var movement = new RuntimeScriptedMovement();
        Assert.True(movement.Begin(new RuntimeMoveRequest(direction, RuntimeMovePace.Run, amount, unit)));
        return movement;
    }

    private static RuntimeScriptedMoveSample Sample(
        double time,
        float east = 0f,
        float north = 0f,
        float heading = 0f,
        bool portal = false,
        bool manual = false) =>
        new(new Vector3(east, north, 0f), heading, time, portal, manual);

    private sealed class FixedInput : IRuntimeMovementInputSource
    {
        public MovementInput Input { get; set; }

        public MovementInput Capture() => Input;
    }
}
