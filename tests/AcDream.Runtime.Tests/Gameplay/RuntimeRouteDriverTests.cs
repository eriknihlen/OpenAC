using System.Numerics;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeRouteDriverTests
{
    private const float Frame = 1f / 30f;

    /// <summary>How fast the simulated body goes and turns.</summary>
    private static readonly RuntimeRouteTurning Turning = new(
        RunSpeed: 4f,
        RunTurnDegreesPerSecond: 90f);

    [Fact]
    public void AStraightRouteIsRunToItsEnd()
    {
        var body = new SimulatedBody();
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 20f, 0f)]);

        Drive(driver, body, seconds: 10f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Assert.InRange(Vector2.Distance(body.Position, new Vector2(0f, 20f)), 0f, 0.6f);
        Assert.False(body.Travelling);
    }

    [Fact]
    public void ADriveThatTakesOverAMoveUnderWayGoesOnWithoutStartingItAgain()
    {
        var body = new SimulatedBody();
        Drive(new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 30f, 0f)]), body, seconds: 1f);
        Assert.True(body.Travelling);
        Vector3[] legs = [new Vector3(body.Position, 0f), new Vector3(0.5f, 30f, 0f)];

        RuntimeRouteDriveStep taken = new RuntimeRouteDriver(legs, takeOverMoves: true).Advance(body.Sample());
        RuntimeRouteDriveStep started = new RuntimeRouteDriver(legs).Advance(body.Sample());

        Assert.Null(taken.Travel);
        Assert.False(taken.StopTravel);
        Assert.NotNull(started.Travel);
    }

    [Fact]
    public void ALegPointingBehindIsFacedBeforeRunning()
    {
        var body = new SimulatedBody { Heading = 180f };
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 10f, 0f)]);

        RuntimeRouteDriveStep first = driver.Advance(body.Sample());

        Assert.Null(first.Travel);
        Assert.Equal(180f, first.Turn!.Value.Amount, 3);
        body.Apply(first);
        Drive(driver, body, seconds: 10f);
        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
    }

    [Fact]
    public void ACornerIsRunUpToAndTurnedInPlaceWithoutWalking()
    {
        var body = new SimulatedBody();
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 10f, 0f), new Vector3(10f, 10f, 0f)]);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 20f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Assert.DoesNotContain(steps, step => step.Travel is { Pace: RuntimeMovePace.Walk });
        Assert.Contains(steps, step => step.StopTravel && step.Turn is { Direction: RuntimeMoveDirection.TurnRight });
        Assert.InRange(Vector2.Distance(body.Position, new Vector2(10f, 10f)), 0f, 0.6f);
    }

    [Fact]
    public void ACornerWithRoomIsRunAroundAlongAnArcWithoutStopping()
    {
        Vector3[] legs = [new(0f, 0f, 0f), new(0f, 10f, 0f), new(10f, 10f, 0f)];
        var arcs = new List<IReadOnlyList<Vector3>>();
        var body = new SimulatedBody { Turning = Turning };
        var path = new List<Vector2>();
        var driver = new RuntimeRouteDriver(legs, canCutAlong: arc =>
        {
            arcs.Add(arc);
            return true;
        });

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 20f, path);
        List<RuntimeRouteDriveStep> standing = Drive(new RuntimeRouteDriver(legs), new SimulatedBody(), seconds: 20f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Assert.DoesNotContain(steps.SkipLast(1), step => step.StopTravel);
        Assert.DoesNotContain(steps, step => step.Travel is { Pace: RuntimeMovePace.Walk });
        float radius = 4f / (MathF.PI / 2f);
        IReadOnlyList<Vector3> arc = Assert.Single(arcs);
        Assert.Equal(new Vector2(0f, 10f - radius), new Vector2(arc[0].X, arc[0].Y));
        Assert.InRange(Vector2.Distance(new Vector2(arc[^1].X, arc[^1].Y), new Vector2(radius, 10f)), 0f, 0.01f);
        float nearest = path.Min(at => Vector2.Distance(at, new Vector2(0f, 10f)));
        Assert.True(nearest > 0.8f, $"the body came within {nearest:0.00} m of the corner");
        Assert.InRange(Vector2.Distance(body.Position, new Vector2(10f, 10f)), 0f, 0.6f);
        Assert.True(steps.Count < standing.Count, $"cutting the corner took {steps.Count} frames, turning in place {standing.Count}");
        Assert.Equal((1, 0), (driver.CornersRunAround, driver.CornersTurnedInPlace));
    }

    [Fact]
    public void ACornerOnlyAWalksTighterArcPassesIsTurnedInPlaceRatherThanWalked()
    {
        var arcs = new List<IReadOnlyList<Vector3>>();
        var body = new SimulatedBody { Turning = Turning };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 10f, 0f), new Vector3(10f, 10f, 0f)],
            canCutAlong: arc =>
            {
                arcs.Add(arc);
                return Vector2.Distance(new Vector2(arc[0].X, arc[0].Y), new Vector2(0f, 10f)) <= 1.5f;
            });

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 20f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Assert.Single(arcs);
        Assert.Equal((0, 1), (driver.CornersRunAround, driver.CornersTurnedInPlace));
        Assert.DoesNotContain(steps, step => step.Travel is { Pace: RuntimeMovePace.Walk });
        Assert.Contains(steps, step => step.StopTravel && step.Turn is { Direction: RuntimeMoveDirection.TurnRight });
        Assert.InRange(Vector2.Distance(body.Position, new Vector2(10f, 10f)), 0f, 0.6f);
    }

    [Fact]
    public void ACornerNoArcPassesIsTurnedInPlaceWithoutWalkingUpToIt()
    {
        var body = new SimulatedBody { Turning = Turning };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 10f, 0f), new Vector3(10f, 10f, 0f)],
            canCutAlong: _ => false);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 20f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Assert.Equal((0, 1), (driver.CornersRunAround, driver.CornersTurnedInPlace));
        Assert.DoesNotContain(steps, step => step.Travel is { Pace: RuntimeMovePace.Walk });
        Assert.Contains(steps, step => step.StopTravel && step.Turn is { Direction: RuntimeMoveDirection.TurnRight });
    }

    [Fact]
    public void AHairpinIsTurnedInPlaceWithoutAskingWhetherAnArcPasses()
    {
        bool asked = false;
        var body = new SimulatedBody { Turning = Turning };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 10f, 0f), new Vector3(1f, 0f, 0f)],
            canCutAlong: _ => asked = true);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 30f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Assert.False(asked);
        Assert.Contains(steps, step => step.StopTravel && step.Turn is not null);
    }

    [Fact]
    public void SmallDriftIsCorrectedWithoutStopping()
    {
        var body = new SimulatedBody { Heading = 10f };
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 20f, 0f)]);

        RuntimeRouteDriveStep first = driver.Advance(body.Sample());

        Assert.Equal(new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Run, 0f), first.Travel);
        Assert.Equal(RuntimeMoveDirection.TurnLeft, first.Turn!.Value.Direction);
        Assert.Equal(10f, first.Turn!.Value.Amount, 3);
        Assert.False(first.StopTravel);
    }

    [Fact]
    public void ALegTheBodyCannotMoveAlongEndsTheDriveAsBlocked()
    {
        var body = new SimulatedBody { Stuck = true };
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 20f, 0f)]);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 5f);

        Assert.Equal(RuntimeRouteDriveState.Blocked, driver.State);
        Assert.True(steps[^1].StopTravel || !body.Travelling);
    }

    [Fact]
    public void ThePlayerTakingOverEndsTheDrive()
    {
        var body = new SimulatedBody();
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 20f, 0f)]);
        Drive(driver, body, seconds: 1f);

        body.Interrupt();
        RuntimeRouteDriveStep step = driver.Advance(body.Sample());

        Assert.Equal(RuntimeRouteDriveState.Interrupted, driver.State);
        Assert.True(step.IsEmpty);
    }

    [Fact]
    public void PortalSpaceLosesTheDrive()
    {
        var body = new SimulatedBody();
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 20f, 0f)]);
        Drive(driver, body, seconds: 1f);

        body.InPortalSpace = true;
        RuntimeRouteDriveStep step = driver.Advance(body.Sample());

        Assert.Equal(RuntimeRouteDriveState.Lost, driver.State);
        Assert.True(step.StopTravel);
    }

    [Fact]
    public void ALongLegRenewsItsRunBeforeTheClientsLimit()
    {
        var body = new SimulatedBody();
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 200f, 0f)]);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 60f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Assert.True(steps.Count(step => step.Travel is not null) >= 3);
        Assert.True(body.LongestTravelSeconds < 30f);
    }

    [Fact]
    public void AMoveEndedBeforeTheDriveBeganIsNotItsOwn()
    {
        var body = new SimulatedBody();
        body.EndTravelBeforeDrive(RuntimeScriptedMoveState.Blocked);
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 20f, 0f)]);

        RuntimeRouteDriveStep first = driver.Advance(body.Sample());

        Assert.Equal(RuntimeRouteDriveState.Driving, driver.State);
        Assert.NotNull(first.Travel);
    }

    [Fact]
    public void CancelStopsWhatTheDriveBegan()
    {
        var body = new SimulatedBody();
        var driver = new RuntimeRouteDriver([new Vector3(0f, 0f, 0f), new Vector3(0f, 20f, 0f)]);
        Drive(driver, body, seconds: 1f);

        RuntimeRouteDriveStep step = driver.Cancel();

        Assert.True(step.StopTravel && step.StopTurn);
        Assert.Equal(RuntimeRouteDriveState.Interrupted, driver.State);
        Assert.True(driver.Advance(body.Sample()).IsEmpty);
    }

    [Fact]
    public void ALeapIsFacedChargedAndFlownFromItsTakeoffToItsLanding()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 5.5f ? 3f : 0f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 5f, 3f), new Vector3(0f, 6.7f, 0f), new Vector3(0f, 10f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)]);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 30f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Assert.Equal(1, body.Jumps);
        RuntimeRouteDriveStep jump = Assert.Single(steps, step => step.Jump is not null);
        Assert.Equal(0.1f, jump.Jump!.Value, 3);
        Assert.Equal(RuntimeMovePace.Walk, jump.JumpPace);
        Assert.Null(jump.Travel);
        Assert.Equal(0, body.TravelsBegunWhileCharging);
        Assert.InRange(Vector2.Distance(body.Position, new Vector2(0f, 10f)), 0f, 0.6f);
        Assert.Equal(0f, body.Height, 3);
    }

    [Fact]
    public void ALeapIsRunUpToAndChargedOnlyOnceTheBodyStandsStill()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 5.5f ? 3f : 0f, SlideSeconds = 0.15f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 4.9f, 3f), new Vector3(0f, 6.7f, 0f), new Vector3(0f, 10f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)]);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 30f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Assert.Equal(1, body.Jumps);
        Assert.Equal(0, body.JumpsChargedWhileSliding);
        int jumped = steps.FindIndex(step => step.Jump is not null);
        Assert.DoesNotContain(steps.Take(jumped), step => step.Travel is { Pace: RuntimeMovePace.Walk });
    }

    [Fact]
    public void ALeapIsRunUpToAndLetGoOfShortOfItsTakeoffSoTheBodyComesToRestOnIt()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 5.5f ? 3f : 0f, SlideSeconds = 1f, WalkStopMeters = 0.75f, RunStopMeters = 2f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 4.9f, 3f), new Vector3(0f, 6.7f, 0f), new Vector3(0f, 10f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)]);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 30f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Vector2 charged = Assert.Single(body.ChargedAt);
        float offTakeoff = Vector2.Distance(charged, new Vector2(0f, 4.9f));
        Assert.True(offTakeoff <= 0.15f, $"the leap charged {offTakeoff:0.00} m from its takeoff");
        int jumped = steps.FindIndex(step => step.Jump is not null);
        Assert.DoesNotContain(steps.Take(jumped), step => step.Travel is { Pace: RuntimeMovePace.Walk });
    }

    [Fact]
    public void ALeapTooNearToRunUpToIsWalkedUpToAndLetGoOfShortOfItsTakeoff()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 1.6f ? 3f : 0f, SlideSeconds = 1f, WalkStopMeters = 0.75f, RunStopMeters = 2f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 1.2f, 3f), new Vector3(0f, 3f, 0f), new Vector3(0f, 6f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)]);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 30f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Vector2 charged = Assert.Single(body.ChargedAt);
        float offTakeoff = Vector2.Distance(charged, new Vector2(0f, 1.2f));
        Assert.True(offTakeoff <= 0.15f, $"the leap charged {offTakeoff:0.00} m from its takeoff");
        int jumped = steps.FindIndex(step => step.Jump is not null);
        Assert.DoesNotContain(steps.Take(jumped), step => step.Travel is { Pace: RuntimeMovePace.Run });
    }

    [Fact]
    public void ALeapThatLandsAwayFromItsLandingOnTheSameLevelAsksForANewPlan()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 5.5f ? 3f : 0f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 5f, 3f), new Vector3(0f, 12f, 0f), new Vector3(0f, 15f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)]);

        Drive(driver, body, seconds: 30f);

        Assert.Equal(RuntimeRouteDriveState.LandedElsewhere, driver.State);
        Assert.Equal(1, body.Jumps);
        Assert.True(driver.LandingError > RuntimeRouteDriver.LandingRadius);
    }

    [Fact]
    public void ALeapThatLandsOnAnotherLevelEndsTheDriveBlocked()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 5.5f ? 3f : 0f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 5f, 3f), new Vector3(0f, 6.7f, 3f), new Vector3(0f, 10f, 3f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)]);

        Drive(driver, body, seconds: 30f);

        Assert.Equal(RuntimeRouteDriveState.Blocked, driver.State);
        Assert.Equal(1, body.Jumps);
    }

    [Fact]
    public void ALeapThatNeverLeavesTheGroundEndsTheDriveBlocked()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 5.5f ? 3f : 0f, CannotJump = true };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 5f, 3f), new Vector3(0f, 6.7f, 0f), new Vector3(0f, 10f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)]);

        Drive(driver, body, seconds: 30f);

        Assert.Equal(RuntimeRouteDriveState.Blocked, driver.State);
        Assert.False(driver.IsLeaping);
    }

    [Fact]
    public void ALeapLetsGoOfForwardOnceTheBodyHasRisenOffTheGroundSoItStopsWhereItLands()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 5.5f ? 3f : 0f, LeaveGroundSeconds = 0.1f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 4.9f, 3f), new Vector3(0f, 6.7f, 0f), new Vector3(0f, 10f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)]);

        Drive(driver, body, seconds: 30f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Assert.Equal(1, body.Jumps);
        Assert.Equal(0, body.LandingsPressingForward);
        Assert.InRange(Vector2.Distance(body.Position, new Vector2(0f, 10f)), 0f, 0.6f);
    }

    [Fact]
    public void ALeapGoesOnOnlyOnceTheBodyHasSlidToAStopAfterItLands()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 5.5f ? 3f : 0f, LandingSlideSeconds = 0.3f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 4.9f, 3f), new Vector3(0f, 6.9f, 0f), new Vector3(0f, 10f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)]);

        Drive(driver, body, seconds: 30f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Assert.Equal(1, body.Jumps);
        Assert.Equal(0, body.TravelsBegunWhileSliding);
        Assert.InRange(driver.LandingSlide, 0.1f, 0.4f);
    }

    private static List<RuntimeRouteDriveStep> Drive(
        RuntimeRouteDriver driver,
        SimulatedBody body,
        float seconds,
        List<Vector2>? path = null)
    {
        var steps = new List<RuntimeRouteDriveStep>();
        for (float time = 0f; time < seconds && driver.State == RuntimeRouteDriveState.Driving; time += Frame)
        {
            RuntimeRouteDriveStep step = driver.Advance(body.Sample());
            steps.Add(step);
            body.Apply(step);
            body.Integrate(Frame);
            path?.Add(body.Position);
        }
        return steps;
    }

    /// <summary>
    /// A body that carries out scripted moves the way the client does, at fixed
    /// speeds. It stands on <see cref="Floor"/>, and a jump it charges while standing
    /// still leaves the ground at the pace of the move pressed as it leaves.
    /// </summary>
    private sealed class SimulatedBody
    {
        private const float RunSpeed = 4f;
        private const float WalkSpeed = 1.5f;
        private const float TurnSpeed = 90f;
        private const float Gravity = 9.8f;
        private const float FullJumpHeight = 4.2f;

        private long _sequence;
        private RuntimeMoveChannelSnapshot _travel;
        private RuntimeMoveChannelSnapshot _turn;
        private float _turnRemaining;
        private float _chargeLeft = -1f;
        private float _chargePower;
        private RuntimeMovePace? _chargePace;
        private float _slideLeft;
        private float _slideTotal;
        private float _slideSpeed;
        private Vector3 _velocity;
        private float _airHeight;
        private float _leaveGroundLeft;

        public Vector2 Position { get; private set; }

        /// <summary>The height of the ground under a point; level ground at zero when unset.</summary>
        public Func<Vector2, float>? Floor { get; init; }

        public bool CannotJump { get; init; }

        public bool Airborne { get; private set; }

        public int Jumps { get; private set; }

        public float Height => Airborne ? _airHeight : Floor?.Invoke(Position) ?? 0f;

        public float Heading { get; set; }

        public bool Stuck { get; init; }

        public bool InPortalSpace { get; set; }

        public RuntimeRouteTurning? Turning { get; init; }

        public float LongestTravelSeconds { get; private set; }

        /// <summary>How many travels were begun while a jump was charging.</summary>
        public int TravelsBegunWhileCharging { get; private set; }

        /// <summary>How long the body slides on, slowing, after a travel stops.</summary>
        public float SlideSeconds { get; init; }

        /// <summary>How far the driver is told a walk carries the body on as the walk ends.</summary>
        public float WalkStopMeters { get; init; }

        /// <summary>How far the driver is told a run carries the body on as the run ends.</summary>
        public float RunStopMeters { get; init; }

        /// <summary>Where the body stood as each jump began to charge.</summary>
        public List<Vector2> ChargedAt { get; } = [];

        /// <summary>How long after a jump releases the body leaves the ground, taking its speed from the pace pressed then.</summary>
        public float LeaveGroundSeconds { get; init; }

        /// <summary>How long the body slides on, slowing, after it comes down from a jump.</summary>
        public float LandingSlideSeconds { get; init; }

        /// <summary>How many travels were begun while the body still slid.</summary>
        public int TravelsBegunWhileSliding { get; private set; }

        /// <summary>How many jumps were charged while the body still slid.</summary>
        public int JumpsChargedWhileSliding { get; private set; }

        /// <summary>How many times the body came down from a jump with forward still pressed.</summary>
        public int LandingsPressingForward { get; private set; }

        public bool Travelling => _travel.State == RuntimeScriptedMoveState.Moving;

        public RuntimeRouteDriveSample Sample() =>
            new(
                new Vector3(Position, Height),
                Heading,
                new RuntimeScriptedMoveSnapshot(_travel, default, _turn, 0, _chargeLeft >= 0f),
                InPortalSpace,
                Airborne,
                Turning,
                _slideLeft <= 0f && !Airborne,
                WalkStopMeters,
                RunStopMeters);

        public void EndTravelBeforeDrive(RuntimeScriptedMoveState state) =>
            _travel = new RuntimeMoveChannelSnapshot(
                ++_sequence,
                state,
                new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Run, 0f),
                0f,
                0f);

        public void Interrupt()
        {
            if (_travel.State == RuntimeScriptedMoveState.Moving)
                _travel = _travel with { State = RuntimeScriptedMoveState.Interrupted };
            if (_turn.State == RuntimeScriptedMoveState.Moving)
                _turn = _turn with { State = RuntimeScriptedMoveState.Interrupted };
        }

        public void Apply(RuntimeRouteDriveStep step)
        {
            if (step.StopTravel && _travel.State == RuntimeScriptedMoveState.Moving)
            {
                _slideLeft = SlideSeconds;
                _slideTotal = SlideSeconds;
                _slideSpeed = _travel.Request.Pace == RuntimeMovePace.Run ? RunSpeed : WalkSpeed;
                _travel = _travel with { State = RuntimeScriptedMoveState.Stopped };
            }
            if (step.StopTurn && _turn.State == RuntimeScriptedMoveState.Moving)
                _turn = _turn with { State = RuntimeScriptedMoveState.Stopped };
            if (step.Travel is { } travel)
            {
                if (_chargeLeft >= 0f)
                    TravelsBegunWhileCharging++;
                if (_slideLeft > 0f)
                    TravelsBegunWhileSliding++;
                _travel = new RuntimeMoveChannelSnapshot(++_sequence, RuntimeScriptedMoveState.Moving, travel, 0f, 0f);
            }
            if (step.Turn is { } turn)
            {
                _turn = new RuntimeMoveChannelSnapshot(++_sequence, RuntimeScriptedMoveState.Moving, turn, 0f, 0f);
                _turnRemaining = turn.Amount;
            }
            if (step.Jump is { } power && !CannotJump && !Airborne)
            {
                if (_slideLeft > 0f)
                    JumpsChargedWhileSliding++;
                ChargedAt.Add(Position);
                _chargeLeft = power;
                _chargePower = power;
                _chargePace = step.JumpPace;
                Jumps++;
            }
        }

        /// <summary>Takes the jump's speed along the ground from the pace pressed now.</summary>
        private void LeaveGround()
        {
            float pace = _travel.State != RuntimeScriptedMoveState.Moving ? 0f
                : _travel.Request.Pace == RuntimeMovePace.Run ? RunSpeed
                : WalkSpeed;
            float facing = Heading * MathF.PI / 180f;
            _velocity = new Vector3(MathF.Sin(facing) * pace, MathF.Cos(facing) * pace, _velocity.Z);
        }

        public void Integrate(float seconds)
        {
            if (_turn.State == RuntimeScriptedMoveState.Moving)
            {
                float step = MathF.Min(_turnRemaining, TurnSpeed * seconds);
                _turnRemaining -= step;
                float signed = _turn.Request.Direction == RuntimeMoveDirection.TurnRight ? step : -step;
                Heading = (((Heading + signed) % 360f) + 360f) % 360f;
                if (_turnRemaining <= 0f)
                    _turn = _turn with { State = RuntimeScriptedMoveState.Completed, Covered = _turn.Request.Amount };
            }
            if (Airborne)
            {
                if (_leaveGroundLeft > 0f)
                {
                    _leaveGroundLeft -= seconds;
                    if (_leaveGroundLeft > 0f)
                        return;
                    LeaveGround();
                }
                Position += new Vector2(_velocity.X, _velocity.Y) * seconds;
                _velocity.Z -= Gravity * seconds;
                _airHeight += _velocity.Z * seconds;
                float ground = Floor?.Invoke(Position) ?? 0f;
                if (_velocity.Z < 0f && _airHeight <= ground)
                {
                    Airborne = false;
                    if (_travel.State == RuntimeScriptedMoveState.Moving)
                        LandingsPressingForward++;
                    if (LandingSlideSeconds > 0f)
                    {
                        _slideLeft = LandingSlideSeconds;
                        _slideTotal = LandingSlideSeconds;
                        _slideSpeed = new Vector2(_velocity.X, _velocity.Y).Length();
                    }
                }
                if (_travel.State == RuntimeScriptedMoveState.Moving)
                    _travel = _travel with { ElapsedSeconds = _travel.ElapsedSeconds + seconds };
                return;
            }
            if (_chargeLeft >= 0f)
            {
                if (_travel.State == RuntimeScriptedMoveState.Moving)
                    _travel = _travel with { ElapsedSeconds = _travel.ElapsedSeconds + seconds };
                _chargeLeft -= seconds;
                if (_chargeLeft > 0f)
                    return;
                _chargeLeft = -1f;
                if (_chargePace is { } leaveAt)
                {
                    _travel = new RuntimeMoveChannelSnapshot(
                        ++_sequence,
                        RuntimeScriptedMoveState.Moving,
                        new RuntimeMoveRequest(RuntimeMoveDirection.Forward, leaveAt, 0f),
                        0f,
                        0f);
                }
                float rise = MathF.Sqrt(2f * Gravity * MathF.Max(0.35f, FullJumpHeight * _chargePower));
                _velocity = new Vector3(0f, 0f, rise);
                _airHeight = Height;
                Airborne = true;
                _leaveGroundLeft = LeaveGroundSeconds;
                if (_leaveGroundLeft <= 0f)
                    LeaveGround();
                return;
            }
            if (_slideLeft > 0f)
            {
                float slideRadians = Heading * MathF.PI / 180f;
                Position += new Vector2(MathF.Sin(slideRadians), MathF.Cos(slideRadians)) * _slideSpeed * (_slideLeft / _slideTotal) * seconds;
                _slideLeft -= seconds;
            }
            if (_travel.State != RuntimeScriptedMoveState.Moving)
                return;
            float speed = _travel.Request.Pace == RuntimeMovePace.Run ? RunSpeed : WalkSpeed;
            float radians = Heading * MathF.PI / 180f;
            if (!Stuck)
                Position += new Vector2(MathF.Sin(radians), MathF.Cos(radians)) * speed * seconds;
            _travel = _travel with { ElapsedSeconds = _travel.ElapsedSeconds + seconds };
            LongestTravelSeconds = MathF.Max(LongestTravelSeconds, _travel.ElapsedSeconds);
            if (Stuck && _travel.ElapsedSeconds > 1.5f)
                _travel = _travel with { State = RuntimeScriptedMoveState.Blocked };
        }
    }
}
