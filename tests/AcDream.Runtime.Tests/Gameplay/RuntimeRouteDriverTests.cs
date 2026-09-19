using System.Numerics;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed partial class RuntimeRouteDriverTests
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

    /// <summary>
    /// Beside a trap the body walks, and its turns are asked for at a walk too. Live, a turn
    /// asked for at a run as a walk ended flipped the client's hold to run, and the rest of
    /// the walk went out at a run, into the trap.
    /// </summary>
    [Fact]
    public void BesideATrapTheBodyWalksAndTurnsAtAWalk()
    {
        var body = new SimulatedBody();
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 10f, 0f), new Vector3(10f, 10f, 0f)],
            carefulAt: _ => true);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 40f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Assert.DoesNotContain(steps, step => step.Travel is { Pace: RuntimeMovePace.Run });
        Assert.Contains(steps, step => step.Travel is { Pace: RuntimeMovePace.Walk });
        Assert.Contains(steps, step => step.StopTravel && step.Turn is { Direction: RuntimeMoveDirection.TurnRight });
        Assert.DoesNotContain(steps, step => step.Turn is { Pace: RuntimeMovePace.Run });
    }

    /// <summary>
    /// A body moves off from a turn in place only once the arc it then goes round bows out
    /// from its leg by no more than a hand's breadth: at a run that is a third of the angle a
    /// walk may have left, and where its speeds are not known the old thirty degrees stand.
    /// Live, a body moving off at a run with thirty degrees still to turn swung 0.7 m wide in
    /// a lane between traps.
    /// </summary>
    [Fact]
    public void ABodyMovesOffFromATurnOnlyWhenItsArcStaysInItsLane()
    {
        var human = new RuntimeRouteTurning(RunSpeed: 11f, RunTurnDegreesPerSecond: 129f, WalkSpeed: 3.12f, WalkTurnDegreesPerSecond: 86f);

        float run = RuntimeRouteDriver.MoveOffDegrees(human, RuntimeMovePace.Run);
        float walk = RuntimeRouteDriver.MoveOffDegrees(human, RuntimeMovePace.Walk);

        Assert.InRange(run, 10f, 16f);
        Assert.InRange(walk, 18f, 26f);
        Assert.True(run < walk);
        Assert.Equal(RuntimeRouteDriver.TurnInPlaceDegrees, RuntimeRouteDriver.MoveOffDegrees(null, RuntimeMovePace.Run));
        // Speeds known for a run alone leave a walk at the old angle.
        Assert.Equal(RuntimeRouteDriver.TurnInPlaceDegrees, RuntimeRouteDriver.MoveOffDegrees(new RuntimeRouteTurning(11f, 129f), RuntimeMovePace.Walk));
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

    /// <summary>
    /// A takeoff farther than the shortest run-up but nearer than a run carries the body on is
    /// walked up to too: a run begun there is let go of at once and slides past the takeoff, a
    /// stutter step that leaves the jump from the wrong spot. Live, rocks in Deewain's jump
    /// puzzle were approached this way.
    /// </summary>
    [Theory]
    [InlineData(1.6f)]
    [InlineData(1.9f)]
    public void ALeapNearerThanARunCarriesTheBodyIsWalkedUpToWithoutAStutterStep(float takeoff)
    {
        var body = new SimulatedBody { Floor = at => at.Y < takeoff + 0.4f ? 3f : 0f, SlideSeconds = 1f, WalkStopMeters = 0.75f, RunStopMeters = 2f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, takeoff, 3f), new Vector3(0f, takeoff + 1.8f, 0f), new Vector3(0f, takeoff + 5f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)]);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 30f);

        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
        Vector2 charged = Assert.Single(body.ChargedAt);
        float offTakeoff = Vector2.Distance(charged, new Vector2(0f, takeoff));
        Assert.True(offTakeoff <= 0.15f, $"the leap charged {offTakeoff:0.00} m from its takeoff");
        int jumped = steps.FindIndex(step => step.Jump is not null);
        Assert.DoesNotContain(steps.Take(jumped), step => step.Travel is { Pace: RuntimeMovePace.Run });
    }

    /// <summary>
    /// A body already past the line through its takeoff, but off to its side, is not at the
    /// takeoff: it walks back to it before it charges. Live on Doriathazaar's jump puzzle a leap
    /// came down 1.4 m from its landing, beyond the next takeoff, and the next leap charged from
    /// 0.91 m off and fell short of a rock 20 m on.
    /// </summary>
    [Fact]
    public void ABodyPastItsTakeoffButOffToItsSideWalksToItBeforeCharging()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 0.2f ? 3f : 0f, SlideSeconds = 1f, WalkStopMeters = 0.75f, RunStopMeters = 2f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(-0.9f, -3f, 3f), new Vector3(-0.9f, -0.5f, 3f), new Vector3(-0.9f, 1.5f, 0f), new Vector3(-0.9f, 4f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)]);

        Drive(driver, body, seconds: 30f);

        Vector2 charged = Assert.Single(body.ChargedAt);
        float offTakeoff = Vector2.Distance(charged, new Vector2(-0.9f, -0.5f));
        Assert.True(offTakeoff <= RuntimeRouteDriver.TakeoffRadius, $"the leap charged {offTakeoff:0.00} m from its takeoff");
        Assert.Equal(RuntimeRouteDriveState.Arrived, driver.State);
    }

    /// <summary>
    /// A leap is aimed again from exactly where the body stands as it charges, and flown at the
    /// power and pace that aim gives rather than those the route planned from its takeoff.
    /// </summary>
    [Fact]
    public void ALeapIsFlownAsAimedAgainFromWhereTheBodyCharges()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 5.5f ? 3f : 0f };
        var asked = new List<(Vector3 Standing, Vector3 Landing, bool Run)>();
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 5f, 3f), new Vector3(0f, 6.7f, 0f), new Vector3(0f, 10f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)],
            aimLeapFrom: (standing, landing, run, _) =>
            {
                asked.Add((standing, landing, run));
                return new RuntimeLeapAim(0.3f, Run: true);
            });

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 30f);

        RuntimeRouteDriveStep jump = Assert.Single(steps, step => step.Jump is not null);
        Assert.Equal(0.3f, jump.Jump!.Value, 3);
        Assert.Equal(RuntimeMovePace.Run, jump.JumpPace);
        (Vector3 standing, Vector3 landing, bool planned) = asked[^1];
        Assert.Equal(new Vector2(standing.X, standing.Y), Assert.Single(body.ChargedAt));
        Assert.Equal(new Vector3(0f, 6.7f, 0f), landing);
        Assert.False(planned);
        Assert.Equal(new RuntimeLeapAim(0.3f, true), driver.Approach.Aimed);
        Assert.Equal(0.1f, driver.Approach.PlannedPower, 3);
    }

    /// <summary>
    /// A body standing a little off its takeoff, where a leap aimed from there is kept, jumps from
    /// there: a walk begun so near is let go of before it settles and slides on past the takeoff.
    /// </summary>
    [Fact]
    public void ABodyStandingNearItsTakeoffWhereALeapIsKeptJumpsWithoutWalking()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 0.2f ? 3f : 0f, SlideSeconds = 1f, WalkStopMeters = 0.75f, RunStopMeters = 2f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, -3f, 3f), new Vector3(0f, -0.6f, 3f), new Vector3(0f, 1.5f, 0f), new Vector3(0f, 4f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)],
            aimLeapFrom: static (_, _, run, _) => new RuntimeLeapAim(0.12f, run));

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 30f);

        Assert.Equal(Vector2.Zero, Assert.Single(body.ChargedAt));
        int jumped = steps.FindIndex(step => step.Jump is not null);
        Assert.DoesNotContain(steps.Take(jumped), step => step.Travel is not null);
        Assert.Equal(0.12f, steps[jumped].Jump!.Value, 3);
    }

    /// <summary>Where no leap from where the body stands is kept, it walks to the takeoff and flies the leap the route planned.</summary>
    [Fact]
    public void ABodyStandingWhereNoLeapIsKeptWalksToItsTakeoffBeforeJumping()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 0.2f ? 3f : 0f, SlideSeconds = 1f, WalkStopMeters = 0.75f, RunStopMeters = 2f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(-0.9f, -3f, 3f), new Vector3(-0.9f, -0.5f, 3f), new Vector3(-0.9f, 1.5f, 0f), new Vector3(-0.9f, 4f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)],
            // As a real aim is: kept from the takeoff and a little way off it, not from a metre off.
            aimLeapFrom: static (standing, _, run, _) =>
                Vector2.Distance(new Vector2(standing.X, standing.Y), new Vector2(-0.9f, -0.5f)) <= RuntimeRouteDriver.TakeoffRadius
                    ? new RuntimeLeapAim(0.1f, run)
                    : null);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 30f);

        float offTakeoff = Vector2.Distance(Assert.Single(body.ChargedAt), new Vector2(-0.9f, -0.5f));
        Assert.True(offTakeoff <= RuntimeRouteDriver.TakeoffRadius, $"the leap charged {offTakeoff:0.00} m from its takeoff");
        Assert.Equal(0.1f, Assert.Single(steps, step => step.Jump is not null).Jump!.Value, 3);
    }

    /// <summary>
    /// A body that came to rest off its takeoff, where no leap aimed from there is kept, goes back to
    /// the takeoff before it jumps rather than fly the planned leap from the wrong spot: a walk up
    /// to a takeoff can stutter a metre past it.
    /// </summary>
    [Fact]
    public void ABodyThatStutteredPastItsTakeoffWhereNoLeapIsKeptGoesBackBeforeJumping()
    {
        // The walk slides on 1.4 m, well past what the driver is told it carries.
        var body = new SimulatedBody { Floor = at => at.Y < 5.5f ? 3f : 0f, SlideSeconds = 1.9f, WalkStopMeters = 0.41f, RunStopMeters = 0.8f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 3.8f, 3f), new Vector3(0f, 4.6f, 3f), new Vector3(0f, 6.7f, 0f), new Vector3(0f, 10f, 0f)],
            [new RuntimeRouteLeap(3, 0.1f, Run: false)],
            aimLeapFrom: static (standing, _, run, _) =>
                Vector2.Distance(new Vector2(standing.X, standing.Y), new Vector2(0f, 4.6f)) <= RuntimeRouteDriver.TakeoffRadius
                    ? new RuntimeLeapAim(0.1f, run)
                    : null);

        Drive(driver, body, seconds: 60f);

        Assert.True(driver.Approach.Adjustments >= 1, "the body never went back to its takeoff");
        foreach (Vector2 charged in body.ChargedAt)
        {
            float offTakeoff = Vector2.Distance(charged, new Vector2(0f, 4.6f));
            Assert.True(offTakeoff <= RuntimeRouteDriver.TakeoffRadius,
                $"the leap charged {offTakeoff:0.00} m from its takeoff after {driver.Approach.Adjustments} adjustments");
        }
        Assert.True(
            body.ChargedAt.Count > 0 || driver.State == RuntimeRouteDriveState.Blocked,
            "the body neither jumped from its takeoff nor asked for the walk to plan again");
    }

    /// <summary>
    /// Inside its takeoff radius but a step off its takeoff, a body where no leap aimed from there is
    /// kept still goes back before it jumps: live, a leap flown from 0.29 m off where none was kept
    /// came down beyond its rock and fell.
    /// </summary>
    [Fact]
    public void ABodyAStepOffItsTakeoffWhereNoLeapIsKeptGoesBackEvenInsideTheTakeoffRadius()
    {
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 3.8f, 3f), new Vector3(0f, 4.6f, 3f), new Vector3(0f, 6.7f, 0f), new Vector3(0f, 10f, 0f)],
            [new RuntimeRouteLeap(3, 0.1f, Run: false)],
            aimLeapFrom: static (_, _, _, _) => null);
        var near = new Vector3(0.25f, 4.6f, 3f);
        var moves = new RuntimeScriptedMoveSnapshot(default, default, default, 0, false);

        // Arrived at the takeoff leg standing 0.25 m to its side, still.
        var steps = new List<RuntimeRouteDriveStep>();
        for (int frame = 0; frame < 5; frame++)
            steps.Add(driver.Advance(new RuntimeRouteDriveSample(near, 0f, moves, false, Still: true)));

        Assert.DoesNotContain(steps, step => step.Jump is not null);
        Assert.Equal(1, driver.Approach.Adjustments);
        Assert.Contains(steps, step => step.Turn is not null || step.Travel is not null);
    }

    /// <summary>
    /// A body that keeps no leap from where it stands and cannot get back to its takeoff plans
    /// again rather than fly the planned leap from the wrong spot: live, a body whose walk back
    /// slid along a rock's edge each time flew from 3.13 m off its takeoff and fell.
    /// </summary>
    [Fact]
    public void ABodyThatCannotGetBackToItsTakeoffPlansAgainRatherThanFlyFromWhereItStands()
    {
        // Every walk up to the takeoff slides 1.4 m past it, so the body never comes to rest
        // near enough for the planned leap to hold, as one sliding along a rock's edge does.
        var body = new SimulatedBody { Floor = at => at.Y < 5.5f ? 3f : 0f, SlideSeconds = 1.9f, WalkStopMeters = 0.41f, RunStopMeters = 0.8f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 3.8f, 3f), new Vector3(0f, 4.6f, 3f), new Vector3(0f, 6.7f, 0f), new Vector3(0f, 10f, 0f)],
            [new RuntimeRouteLeap(3, 0.1f, Run: false)],
            aimLeapFrom: static (_, _, _, _) => null);

        Drive(driver, body, seconds: 60f);

        Assert.Equal(RuntimeRouteDriveState.Blocked, driver.State);
        Assert.Equal(RuntimeRouteDriver.MostTakeoffAdjustments, driver.Approach.Adjustments);
        Assert.Empty(body.ChargedAt);
    }

    /// <summary>
    /// A body standing still on the floor it would walk across to a leap's takeoff tries the leap
    /// from where it stands first, and where one is kept there takes it without walking: hopping
    /// across roofs, it walked to the spot the route planned each leap from.
    /// </summary>
    [Fact]
    public void ALeapKeptFromWhereTheBodyStandsIsTakenWithoutWalkingToTheTakeoff()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 5.5f ? 3f : 0f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(1f, 3f, 3f), new Vector3(0f, 5f, 3f), new Vector3(0f, 8f, 0f), new Vector3(0f, 10f, 0f)],
            [new RuntimeRouteLeap(3, 0.1f, Run: false)],
            aimLeapFrom: static (_, _, run, _) => new RuntimeLeapAim(0.4f, run),
            sameFloor: static (_, _) => true);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 30f);

        int jumped = steps.FindIndex(step => step.Jump is not null);
        Assert.True(jumped >= 0);
        Assert.DoesNotContain(steps.Take(jumped), step => step.Travel is not null);
        Assert.Equal(Vector2.Zero, Assert.Single(body.ChargedAt));
        Assert.InRange(driver.Approach.WalkSkipped, 5.2f, 5.5f);
    }

    /// <summary>A body standing on other floor than the takeoff, as below the roof the route climbs to first, walks the route.</summary>
    [Fact]
    public void ALeapIsNotTriedFromFloorOtherThanItsTakeoffs()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 5.5f ? 3f : 0f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 5f, 3f), new Vector3(0f, 8f, 0f), new Vector3(0f, 10f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)],
            aimLeapFrom: static (_, _, run, _) => new RuntimeLeapAim(0.4f, run),
            sameFloor: static (_, _) => false);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 30f);

        int jumped = steps.FindIndex(step => step.Jump is not null);
        Assert.Contains(steps.Take(jumped), step => step.Travel is not null);
    }

    /// <summary>
    /// Where a leap comes down on the next leap's takeoff, a body sent back to that takeoff walks to
    /// it and takes the next leap; it never steps back onto the leap it already flew. Live it did,
    /// and walked back to the leap before's own takeoff over and over.
    /// </summary>
    [Fact]
    public void ABodySentBackToATakeoffTheLeapBeforeCameDownOnNeverFliesThatLeapAgain()
    {
        var body = new SimulatedBody { SlideSeconds = 1f, WalkStopMeters = 0.41f, RunStopMeters = 0.8f };
        Vector2[] takeoffs = [new(0f, 3f), new(0f, 4.7f)];
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 3f, 0f), new Vector3(0f, 4.7f, 0f), new Vector3(0f, 6.4f, 0f), new Vector3(0f, 9f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false), new RuntimeRouteLeap(3, 0.1f, Run: false)],
            aimLeapFrom: (standing, _, run, _) => takeoffs.Any(takeoff => Vector2.Distance(new Vector2(standing.X, standing.Y), takeoff) <= 0.05f)
                ? new RuntimeLeapAim(0.1f, run)
                : null,
            sameFloor: static (_, _) => true);
        int highest = 0;

        for (float time = 0f; time < 60f && driver.State == RuntimeRouteDriveState.Driving; time += Frame)
        {
            RuntimeRouteDriveStep step = driver.Advance(body.Sample());
            Assert.True(driver.LegIndex >= highest, $"the drive went back from leg {highest} to leg {driver.LegIndex}");
            highest = driver.LegIndex;
            body.Apply(step);
            body.Integrate(Frame);
        }

        // It either takes both leaps or asks for the walk to plan again; what it never does is
        // step back onto the leap it already flew, which the loop above holds it to.
        Assert.True(
            driver.State is RuntimeRouteDriveState.Arrived or RuntimeRouteDriveState.Blocked,
            $"the drive ended {driver.State}");
        Assert.True(
            body.Jumps == (driver.State == RuntimeRouteDriveState.Arrived ? 2 : 0),
            $"the drive ended {driver.State} after {body.Jumps} jumps");
    }

    /// <summary>
    /// A leap says how its takeoff was come up to, so narration can show a stutter step: the
    /// pace, the moves begun on the way, how far short the last was let go, the turns that faced
    /// the landing, and how far from the takeoff the jump charged.
    /// </summary>
    [Fact]
    public void ALeapSaysHowItsTakeoffWasComeUpTo()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 5.5f ? 3f : 0f, SlideSeconds = 1f, WalkStopMeters = 0.75f, RunStopMeters = 2f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 3f), new Vector3(0f, 4.9f, 3f), new Vector3(1f, 6.7f, 0f), new Vector3(1f, 10f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)]);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 30f);
        int jumped = steps.FindIndex(step => step.Jump is not null);
        Assert.True(jumped >= 0);

        RuntimeLeapApproach approach = driver.Approach;
        Assert.Equal(1, approach.Leg);
        Assert.Equal(RuntimeMovePace.Run, approach.Pace);
        Assert.Equal(1, approach.MovesBegun);
        Assert.InRange(approach.LetGoMeters, 1.5f, 2.6f);
        Assert.True(approach.Turns >= 1, "the leap bends from the run up, so it turns to face its landing");
        Assert.InRange(approach.TurnedDegrees, 25f, 35f);
        Assert.InRange(approach.TakeoffError, 0f, 0.15f);
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
    internal sealed class SimulatedBodyProbe : SimulatedBody { }

    internal class SimulatedBody
    {
        private const float RunSpeed = 4f;
        private const float WalkSpeed = 1.5f;
        private const float TurnSpeed = 90f;
        private const float Gravity = 9.8f;
        private const float FullJumpHeight = 4.2f;

        private long _sequence;
        private RuntimeMoveChannelSnapshot _travel;
        private RuntimeMoveChannelSnapshot _strafe;
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
                new RuntimeScriptedMoveSnapshot(_travel, _strafe, _turn, 0, _chargeLeft >= 0f),
                InPortalSpace,
                Airborne,
                Turning,
                _slideLeft <= 0f && !Airborne && _strafe.State != RuntimeScriptedMoveState.Moving,
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
            if (_strafe.State == RuntimeScriptedMoveState.Moving)
                _strafe = _strafe with { State = RuntimeScriptedMoveState.Interrupted };
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
                // The client puts a move on the channel its direction belongs to, so a sidestep
                // runs on the strafe channel beside a walk and a stopped walk never stops it.
                if (travel.Channel == RuntimeMoveChannel.Strafe)
                    _strafe = new RuntimeMoveChannelSnapshot(++_sequence, RuntimeScriptedMoveState.Moving, travel, 0f, 0f);
                else
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
            // A sidestep runs on its own channel, square to the body's heading, and goes on
            // beside a walk rather than in place of one.
            if (_strafe.State == RuntimeScriptedMoveState.Moving)
            {
                float sideways = Heading * MathF.PI / 180f;
                Vector2 aside = _strafe.Request.Direction == RuntimeMoveDirection.StrafeLeft
                    ? new Vector2(-MathF.Cos(sideways), MathF.Sin(sideways))
                    : new Vector2(MathF.Cos(sideways), -MathF.Sin(sideways));
                float stepSpeed = _strafe.Request.Pace == RuntimeMovePace.Run ? RunSpeed : WalkSpeed;
                float stepped = stepSpeed * seconds;
                if (_strafe.Request.Amount > 0f)
                    stepped = MathF.Min(stepped, _strafe.Request.Amount - _strafe.Covered);
                if (!Stuck)
                    Position += aside * stepped;
                _strafe = _strafe with
                {
                    Covered = _strafe.Covered + stepped,
                    ElapsedSeconds = _strafe.ElapsedSeconds + seconds,
                };
                if (_strafe.Request.Amount > 0f && _strafe.Covered >= _strafe.Request.Amount)
                    _strafe = _strafe with { State = RuntimeScriptedMoveState.Completed };
            }
            if (_travel.State != RuntimeScriptedMoveState.Moving)
                return;
            float speed = _travel.Request.Pace == RuntimeMovePace.Run ? RunSpeed : WalkSpeed;
            float radians = Heading * MathF.PI / 180f;
            // A move asked for a distance stops once it has covered it.
            var along = _travel.Request.Direction == RuntimeMoveDirection.Backward
                ? new Vector2(-MathF.Sin(radians), -MathF.Cos(radians))
                : new Vector2(MathF.Sin(radians), MathF.Cos(radians));
            float moved = speed * seconds;
            if (_travel.Request.Amount > 0f)
                moved = MathF.Min(moved, _travel.Request.Amount - _travel.Covered);
            if (!Stuck)
                Position += along * moved;
            _travel = _travel with { Covered = _travel.Covered + moved };
            if (_travel.Request.Amount > 0f && _travel.Covered >= _travel.Request.Amount)
                _travel = _travel with { State = RuntimeScriptedMoveState.Completed };
            _travel = _travel with { ElapsedSeconds = _travel.ElapsedSeconds + seconds };
            LongestTravelSeconds = MathF.Max(LongestTravelSeconds, _travel.ElapsedSeconds);
            if (Stuck && _travel.ElapsedSeconds > 1.5f)
                _travel = _travel with { State = RuntimeScriptedMoveState.Blocked };
        }
    }
}
