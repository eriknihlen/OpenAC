using System.Numerics;
using AcDream.Core.Navigation;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

/// <summary>
/// What a body does about the spot a leap was planned from: how it steps onto it, how near it
/// has to stand before the leap the route planned counts as still holding, and what it does when
/// no leap aimed from where it stands is kept at all.
/// </summary>
public sealed partial class RuntimeRouteDriverTests
{
    private static Vector2 Flat(Vector3 point) => new(point.X, point.Y);

    /// <summary>
    /// A sidestep onto a takeoff runs on a strafe channel of its own, so the leg it steps onto
    /// counts as reached only once the step ends: the drive asks for nothing else while it runs.
    /// Watching the travel channel instead saw the step end on the frame after it began, and the
    /// leg's own turn and travel moved the body straight back off the spot.
    /// </summary>
    [Fact]
    public void ASidestepOntoATakeoffRunsToItsEndBeforeAnythingElseIsAsked()
    {
        var takeoff = new Vector2(0f, 0.5f);
        var body = new SimulatedBody();
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 0.5f, 0f), new Vector3(0f, 4f, 0f)],
            [new RuntimeRouteLeap(2, 0.2f, Run: false)],
            aimLeapFrom: (standing, _, run, _) => Vector2.Distance(Flat(standing), takeoff) <= 0.2f
                ? new RuntimeLeapAim(0.2f, run)
                : null,
            sameFloor: static (_, _) => true);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 20f);

        int stepped = steps.FindIndex(step =>
            step.Travel?.Direction is RuntimeMoveDirection.StrafeLeft or RuntimeMoveDirection.StrafeRight);
        Assert.True(stepped >= 0, "the takeoff a walk cannot reach was never stepped onto");
        int jumped = steps.FindIndex(step => step.Jump is not null);
        Assert.True(jumped > stepped, "the leap never followed the step onto its takeoff");
        // Nothing travels over the step: only the turn that faces the landing comes between it
        // and the jump, and that is asked once the step has run to its end.
        Assert.DoesNotContain(steps.GetRange(stepped + 1, jumped - stepped - 1), step => step.Travel is not null);
        Assert.InRange(Vector2.Distance(Assert.Single(body.ChargedAt), takeoff), 0f, 0.1f);
    }

    /// <summary>
    /// A body that came to rest past its takeoff steps back onto it sideways when the gap is one
    /// a forward move overshoots. Asked to walk any distance at all this client moves
    /// <see cref="RuntimeRouteDriver.ShortestWalkMeters"/>, so walking back a third of a metre
    /// carried the body clean over the takeoff and off the far side of it.
    /// </summary>
    [Fact]
    public void AWalkBackToATakeoffStepsSidewaysForAGapAWalkWouldOvershoot()
    {
        var takeoff = new Vector2(0f, 3f);
        var body = new SimulatedBody { SlideSeconds = 1f, WalkStopMeters = 0.41f, RunStopMeters = 0.8f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 3f, 0f), new Vector3(0f, 6f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)],
            aimLeapFrom: (standing, _, run, _) => Vector2.Distance(Flat(standing), takeoff) <= 0.05f
                ? new RuntimeLeapAim(0.1f, run)
                : null,
            sameFloor: static (_, _) => true);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 60f);

        Assert.Contains(
            steps,
            step => step.Travel?.Direction is RuntimeMoveDirection.StrafeLeft or RuntimeMoveDirection.StrafeRight);
        Assert.True(driver.Approach.Sidesteps > 0, "the walk back took no sidestep");
    }

    /// <summary>
    /// A leap whose aim is refused from where the body stands is never flown from off its takeoff,
    /// however many times the body has tried to get back to it. The leap the route planned holds
    /// only from that takeoff: live, one flown from 0.29 m off came down beyond its rock and fell
    /// 160 m, and the drive taking it up to a third of a metre off flew exactly that leap.
    /// </summary>
    [Fact]
    public void ALeapWhoseAimIsRefusedIsNeverFlownFromOffItsTakeoff()
    {
        var takeoff = new Vector2(0f, 3f);
        var body = new SimulatedBody { SlideSeconds = 1f, WalkStopMeters = 0.41f, RunStopMeters = 0.8f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 3f, 0f), new Vector3(0f, 6f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)],
            aimLeapFrom: static (_, _, _, _) => null,
            sameFloor: static (_, _) => true);

        Drive(driver, body, seconds: 60f);

        Assert.NotEqual(RuntimeRouteDriveState.Driving, driver.State);
        Assert.All(
            body.ChargedAt,
            at => Assert.InRange(Vector2.Distance(at, takeoff), 0f, RuntimeRouteDriver.NoAimTakeoffRadius));
    }

    /// <summary>
    /// A leap counts as aimed from the takeoff the route planned it from only inside the slack the
    /// planner checks its own leaps through, and never inside the wider radius that says a body has
    /// arrived at a takeoff. The planner keeps a leap onto a landing narrower than the body without
    /// checking that it still lands when it leaves a little off, so saying yes from a third of a
    /// metre away kept a leap that holds from the takeoff alone.
    /// </summary>
    [Fact]
    public void ALeapIsAimedAsFromItsTakeoffOnlyInsideThePlannersOwnSlack()
    {
        var takeoff = new Vector2(0f, 3f);
        var asked = new List<(float Off, bool AtItsTakeoff)>();
        var body = new SimulatedBody { SlideSeconds = 1f, WalkStopMeters = 0.41f, RunStopMeters = 0.8f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 3f, 0f), new Vector3(0f, 6f, 0f)],
            [new RuntimeRouteLeap(2, 0.1f, Run: false)],
            aimLeapFrom: (standing, _, run, atItsTakeoff) =>
            {
                asked.Add((Vector2.Distance(Flat(standing), takeoff), atItsTakeoff));
                return new RuntimeLeapAim(0.1f, run);
            },
            sameFloor: static (_, _) => true);

        Drive(driver, body, seconds: 60f);

        Assert.NotEmpty(asked);
        Assert.Equal(NavLeapFinder.TakeoffSlack, RuntimeRouteDriver.AtItsTakeoffRadius);
        Assert.All(asked, ask => Assert.Equal(ask.Off <= NavLeapFinder.TakeoffSlack, ask.AtItsTakeoff));
    }

    /// <summary>
    /// A body standing near a takeoff but on another floor walks to the takeoff rather than taking
    /// the leap from where it stands, as one anywhere on the takeoff's own floor may. A floor a
    /// step away in plan can lie below or above this one, and a leap kept from there would carry
    /// the body off that floor in place of the walk.
    /// </summary>
    [Fact]
    public void ALeapIsNotAimedFromNearItsTakeoffOnAnotherFloor()
    {
        var body = new SimulatedBody { Floor = at => at.Y < 0.6f ? 0f : 3f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 1.2f, 3f), new Vector3(0f, 5f, 3f)],
            [new RuntimeRouteLeap(2, 0.2f, Run: false)],
            aimLeapFrom: static (_, _, run, _) => new RuntimeLeapAim(0.2f, run),
            sameFloor: static (standing, spot) => MathF.Abs(standing.Z - spot.Z) < 0.5f);

        List<RuntimeRouteDriveStep> steps = Drive(driver, body, seconds: 20f);

        int jumped = steps.FindIndex(step => step.Jump is not null);
        Assert.True(jumped >= 0, "the leap was never taken");
        Assert.Contains(steps.Take(jumped), step => step.Travel is not null);
    }

    /// <summary>
    /// A jump that never leaves the ground came down nowhere, so there is no landing to measure.
    /// Saying a leap flew as its charge begins made a drive that ended on the ground report a
    /// landing it never had, out of whatever the leap before left behind.
    /// </summary>
    [Fact]
    public void AJumpThatNeverLeavesTheGroundCameDownNowhere()
    {
        var body = new SimulatedBody { CannotJump = true };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 3f, 0f), new Vector3(0f, 6f, 0f)],
            [new RuntimeRouteLeap(2, 0.2f, Run: false)],
            aimLeapFrom: static (_, _, run, _) => new RuntimeLeapAim(0.2f, run),
            sameFloor: static (_, _) => true);

        Drive(driver, body, seconds: 30f);

        Assert.Equal(RuntimeRouteDriveState.Blocked, driver.State);
        Assert.True(driver.LeapFlew, "the leap never charged");
        Assert.False(driver.LeapLanded, "a jump still on the ground was said to have come down");
    }

    /// <summary>
    /// A leap aimed at another spot of its landing's floor says where it was flown from and what it
    /// was flown at, so the flight can be replayed against what the body did. Replaying it against
    /// the landing the route planned, from the height of the takeoff it never charged at, described
    /// a leap nobody took.
    /// </summary>
    [Fact]
    public void ALeapAimedOnwardSaysWhereItWasFlownFromAndWhatAt()
    {
        var onward = new Vector3(0f, 5.5f, 0f);
        var body = new SimulatedBody { Floor = at => at.Y < 3.5f ? 2f : 0f };
        var driver = new RuntimeRouteDriver(
            [new Vector3(0f, 0f, 2f), new Vector3(0f, 3f, 2f), new Vector3(0f, 6f, 0f)],
            [new RuntimeRouteLeap(2, 0.2f, Run: false)],
            aimLeapFrom: static (_, _, _, _) => null,
            aimOnward: (_, _, run) => (new RuntimeLeapAim(0.3f, run), onward),
            sameFloor: static (_, _) => true);

        Drive(driver, body, seconds: 30f);

        Assert.Equal(onward, driver.Approach.FlownAt);
        Assert.True(driver.Approach.AimedOnward, "the leap was not said to have been aimed onward");
        Assert.Equal(Assert.Single(body.ChargedAt), Flat(driver.Approach.ChargedFrom));
        Assert.Equal(2f, driver.Approach.ChargedFrom.Z, 3);
    }
}
