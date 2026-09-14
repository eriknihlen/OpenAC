using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class WalkerTests
{
    private static PluginNavigationPosition At(double east, double north, float heading = 0f) =>
        new(0x0001_0100u, east, north, 0d, heading, IsOutdoor: true);

    [Fact]
    public void SteersWithTheTurnKeysWhileRunning()
    {
        var surface = new FakeAutomationSurface();
        var walker = new Walker();

        // 10 degrees off to the right: run and hold turn-right.
        Assert.Null(walker.Toward(surface, At(0d, 0d, heading: 0f), 10f, now: 0d));
        Assert.Equal(["move:forward+turnright"], surface.Commands);
        Assert.True(walker.IsMoving);

        // Still off by 4: inside the engage band but outside the release band, keep steering.
        Assert.Null(walker.Toward(surface, At(0d, 0d, heading: 6f), 10f, now: 0.1d));
        Assert.Equal(["move:forward+turnright"], surface.Commands);

        // Within 2 degrees: release the key, keep running.
        Assert.Null(walker.Toward(surface, At(0d, 0d, heading: 9f), 10f, now: 0.2d));
        Assert.Equal(["move:forward+turnright", "move:forward"], surface.Commands);

        // A small drift the other way is inside the engage band: no chatter.
        Assert.Null(walker.Toward(surface, At(0d, 0d, heading: 14f), 10f, now: 0.3d));
        Assert.Equal(["move:forward+turnright", "move:forward"], surface.Commands);

        // Past it: steer left.
        Assert.Null(walker.Toward(surface, At(0d, 0d, heading: 20f), 10f, now: 0.4d));
        Assert.Equal("move:forward+turnleft", surface.Commands[^1]);
    }

    [Fact]
    public void ALargeErrorStopsAndTurnsInPlace()
    {
        var surface = new FakeAutomationSurface();
        var walker = new Walker();

        Assert.Null(walker.Toward(surface, At(0d, 0d, heading: 0f), 0f, now: 0d));
        Assert.Equal(["move:forward"], surface.Commands);

        // The waypoint is now 90 degrees away: let go of the run and turn in place with the key.
        Assert.Null(walker.Toward(surface, At(0d, 0d, heading: 0f), 90f, now: 0.1d));
        Assert.Equal(["move:forward", "move:turnright"], surface.Commands);
        Assert.False(walker.IsMoving);

        // Facing it: the keys are released and the run starts again from a clean slate.
        Assert.Null(walker.Toward(surface, surface.Position with { HeadingDegrees = 90f }, 90f, now: 0.2d));
        Assert.Equal(["move:forward", "move:turnright", "move:clear", "move:forward"], surface.Commands);
    }

    [Fact]
    public void TheRunResumesOnlyOnceTheTurnIsWellUnderWay()
    {
        var surface = new FakeAutomationSurface();
        var walker = new Walker();

        // 30 degrees off: turn in place. Then, the turn only part way (15 left),
        // still under the enter angle but over the resume angle: keep turning.
        Assert.Null(walker.Toward(surface, At(0d, 0d, heading: 0f), 30f, now: 0d));
        Assert.Equal(["move:turnright"], surface.Commands);
        Assert.Null(walker.Toward(surface, At(0d, 0d, heading: 15f), 30f, now: 0.1d));
        Assert.Equal(["move:turnright"], surface.Commands);
        Assert.False(walker.IsMoving);

        // Under the resume angle: run, steering out the last of it.
        Assert.Null(walker.Toward(surface, At(0d, 0d, heading: 22f), 30f, now: 0.2d));
        Assert.Equal("move:forward+turnright", surface.Commands[^1]);
    }

    [Fact]
    public void TimeSpentTurningDoesNotCountAsStalled()
    {
        var surface = new FakeAutomationSurface();
        var walker = new Walker();

        Assert.Null(walker.Toward(surface, At(0d, 0d), 0f, now: 0d));
        // Turn in place for longer than the stall window.
        Assert.Null(walker.Toward(surface, At(0d, 0d), 120f, now: 1d));
        Assert.Null(walker.Toward(surface, At(0d, 0d, heading: 120f), 120f, now: 4d));
        // Running again: the window restarts here, so nothing is stuck yet...
        Assert.Null(walker.Toward(surface, At(0d, 0d, heading: 120f), 120f, now: 4.5d));
        // ...until a full window passes without progress: the keys are pressed afresh first...
        Assert.Null(walker.Toward(surface, At(0d, 0d, heading: 120f), 120f, now: 7.5d));
        Assert.Equal(["move:clear", "move:forward"], surface.Commands[^2..]);
        // ...and only a second window without progress backs up.
        Assert.Null(walker.Toward(surface, At(0d, 0d, heading: 120f), 120f, now: 8d));
        Assert.Null(walker.Toward(surface, At(0d, 0d, heading: 120f), 120f, now: 9d));
        Assert.Equal(StuckRecovery.BackUp, walker.Toward(surface, At(0d, 0d, heading: 120f), 120f, now: 11.1d));
    }

    [Fact]
    public void ATurnTheHostIgnoresIsPressedAgainThenCountsAsAStall()
    {
        var surface = new FakeAutomationSurface();
        var walker = new Walker();

        // Turn in place toward 120, but the heading never moves.
        Assert.Null(walker.Toward(surface, At(0d, 0d), 120f, now: 0d));
        Assert.Equal(["move:turnright"], surface.Commands);
        Assert.Null(walker.Toward(surface, At(0d, 0d), 120f, now: 1d));
        Assert.Equal(["move:turnright"], surface.Commands);
        // After the turn-stall window the key is let go and pressed again...
        Assert.Null(walker.Toward(surface, At(0d, 0d), 120f, now: 2.1d));
        Assert.Equal(["move:turnright", "move:clear", "move:turnright"], surface.Commands);
        // ...and a second dead window is a stall for the caller to act on.
        Assert.Null(walker.Toward(surface, At(0d, 0d), 120f, now: 3d));
        Assert.Equal(StuckRecovery.BackUp, walker.Toward(surface, At(0d, 0d), 120f, now: 4.2d));

        // A turn that is making progress is left alone.
        var moving = new Walker();
        Assert.Null(moving.Toward(surface, At(0d, 0d), 120f, now: 0d));
        Assert.Null(moving.Toward(surface, At(0d, 0d, heading: 40f), 120f, now: 2.1d));
        Assert.Null(moving.Toward(surface, At(0d, 0d, heading: 80f), 120f, now: 4.2d));
        Assert.Equal("move:turnright", surface.Commands[^1]);
    }

    [Fact]
    public void RecoveriesRunTheirCourseThenReleaseTheKeysAndNeverJump()
    {
        var surface = new FakeAutomationSurface();
        var walker = new Walker();

        walker.BeginRecovery(surface, StuckRecovery.BackUp, now: 0d);
        Assert.Equal(["move:back"], surface.Commands);
        Assert.True(walker.ContinueRecovery(surface, now: 0.3d));
        Assert.False(walker.ContinueRecovery(surface, now: Walker.RecoveryDurationSeconds + 0.01d));
        Assert.Equal(["move:back", "move:clear"], surface.Commands);

        walker.BeginRecovery(surface, StuckRecovery.StrafeLeft, now: 1d);
        Assert.Equal("move:left", surface.Commands[^1]);
        walker.BeginRecovery(surface, StuckRecovery.StrafeRight, now: 2d);
        Assert.Equal("move:right", surface.Commands[^1]);
        Assert.DoesNotContain(surface.Commands, command => command.Contains("jump", StringComparison.Ordinal));
    }

    [Fact]
    public void ResetReleasesTheKeys()
    {
        var surface = new FakeAutomationSurface();
        var walker = new Walker();

        walker.Toward(surface, At(0d, 0d), 90f, now: 0d);
        Assert.Equal(["move:turnright"], surface.Commands);
        walker.Reset(surface);
        Assert.Equal("move:clear", surface.Commands[^1]);
        Assert.False(walker.IsMoving);

        // The turn is asked for again after a reset, not treated as already held.
        walker.Toward(surface, At(0d, 0d, heading: 0f), 90f, now: 0.1d);
        Assert.Equal("move:turnright", surface.Commands[^1]);
    }
}
