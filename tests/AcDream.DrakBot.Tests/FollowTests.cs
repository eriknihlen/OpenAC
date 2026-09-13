using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class FollowTests
{
    [Fact]
    public void FollowsTheLeaderWithHysteresisAndHoldsWhenTheyAreGone()
    {
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        var settings = new NavigationSettings { Follow = "leader", FollowStopMeters = 5f, FollowResumeMeters = 8f };
        var behavior = new NavigationBehavior(() => settings);
        surface.Fellows.Add(new PluginFellowMember(0x5000_0009u, "Leader", 100u, 100u, 0u, 0u, 0u, 0u, 12f));
        surface.LeaderObjectId = 0x5000_0009u;
        PluginNavigationPosition LeaderAt(double northMeters) => surface.Position with { NorthSouth = surface.Position.NorthSouth + northMeters / 240d };
        surface.ObjectPositions[0x5000_0009u] = LeaderAt(12d);

        BehaviorContext Context() => new(surface, new FakeLogger(), Blackboard.Capture(surface, clock, 25f, 15f));

        Assert.True(behavior.WantsControl(Context().Board, out string reason));
        Assert.Equal("following leader", reason);
        behavior.Execute(Context());
        Assert.Equal("move:forward", surface.Commands[^1]); // due north, no turn needed

        // Inside the stop distance: stand.
        surface.ObjectPositions[0x5000_0009u] = LeaderAt(4d);
        clock.Advance(0.1d);
        behavior.Execute(Context());
        Assert.Equal("move:clear", surface.Commands[^1]);

        // Six meters is between stop and resume: still standing.
        surface.ObjectPositions[0x5000_0009u] = LeaderAt(6d);
        clock.Advance(0.1d);
        int count = surface.Commands.Count;
        behavior.Execute(Context());
        Assert.Equal(count, surface.Commands.Count);

        // Beyond resume: off again.
        surface.ObjectPositions[0x5000_0009u] = LeaderAt(9d);
        clock.Advance(0.1d);
        behavior.Execute(Context());
        Assert.Equal("move:forward", surface.Commands[^1]);

        // The leader portals away: hold.
        surface.ObjectPositions.Remove(0x5000_0009u);
        clock.Advance(0.1d);
        Assert.Equal(StepResult.Failed, behavior.Execute(Context()).Result);
        Assert.Equal("move:clear", surface.Commands[^1]);
    }
}
