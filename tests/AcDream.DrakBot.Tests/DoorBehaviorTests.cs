using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class DoorBehaviorTests
{
    [Fact]
    public void OpensTheNearestClosedDoorAheadAndLeavesItAloneOnceOpen()
    {
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        var settings = new DoorSettings { Enabled = true, RangeMeters = 4f };
        var doors = new List<PluginNavigationObject>
        {
            new(0x7000_0001u, "Door", surface.Position with { NorthSouth = surface.Position.NorthSouth + 2d / 240d }) { IsDoor = true, IsOpen = false },
            new(0x7000_0002u, "Far Door", surface.Position with { NorthSouth = surface.Position.NorthSouth + 20d / 240d }) { IsDoor = true, IsOpen = false },
            new(0x7000_0003u, "Open Door", surface.Position with { NorthSouth = surface.Position.NorthSouth + 1d / 240d }) { IsDoor = true, IsOpen = true },
        };
        bool walking = true;
        var behavior = new DoorBehavior(() => settings, () => walking, () => doors.ToArray());
        foreach (PluginNavigationObject door in doors)
            surface.ObjectPositions[door.ObjectId] = door.Position;

        BehaviorContext Context() => new(surface, new FakeLogger(), Blackboard.Capture(surface, clock, 25f, 15f));

        Assert.True(behavior.WantsControl(Context().Board, out string reason));
        Assert.Equal("door ahead", reason);
        Assert.Equal(StepResult.Continue, behavior.Execute(Context()).Result);
        Assert.Equal("useobject:1879048193", surface.Commands[^1]);

        // The door swings open: done, and it is not targeted again for a while.
        doors[0] = doors[0] with { IsOpen = true };
        surface.NavObjects[0x7000_0001u] = doors[0];
        clock.Advance(0.5d);
        Assert.Equal(StepResult.Done, behavior.Execute(Context()).Result);
        clock.Advance(0.6d);
        Assert.False(behavior.WantsControl(Context().Board, out _));

        // Not walking: doors are left alone.
        doors[0] = doors[0] with { IsOpen = false };
        walking = false;
        clock.Advance(61d);
        Assert.False(behavior.WantsControl(Context().Board, out _));
    }

    [Fact]
    public void ADoorThatWillNotOpenIsGivenUpOnAfterThreeTries()
    {
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        var settings = new DoorSettings { Enabled = true, RangeMeters = 4f };
        var door = new PluginNavigationObject(0x7000_0001u, "Door", surface.Position with { NorthSouth = surface.Position.NorthSouth + 2d / 240d }) { IsDoor = true, IsOpen = false };
        var behavior = new DoorBehavior(() => settings, () => true, () => [door]);
        surface.NavObjects[door.ObjectId] = door;

        BehaviorContext Context() => new(surface, new FakeLogger(), Blackboard.Capture(surface, clock, 25f, 15f));

        behavior.WantsControl(Context().Board, out _);
        behavior.Execute(Context());
        for (int attempt = 0; attempt < 3; attempt++)
        {
            clock.Advance(3.5d);
            behavior.Execute(Context());
        }
        Assert.Equal(3, surface.Commands.Count(c => c == "useobject:1879048193"));
        Assert.False(behavior.WantsControl(Context().Board, out _));
    }
}
