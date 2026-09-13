using AcDream.DrakBot.Inventory;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class InventoryTidyTests
{
    private const uint Self = 0x50000001u;

    private static PluginInventoryItem Item(uint id, string name, uint container, int stack = 1, int maxStack = 1, int slots = 0, PluginObjectClass objectClass = PluginObjectClass.Misc) => new(
        id, 0u, name, 0u, container, 0u, 0u, 0u, 0u, 0u, 0u, stack, 0, 0, 0u, 0, 0, 0u,
        false, 0d, 0, 0, 0, 0d, 0, 0, 0)
    {
        MaximumStackSize = maxStack,
        ItemsCapacity = slots,
        ObjectClass = objectClass,
    };

    private static (FakeAutomationSurface Surface, TickClock Clock) Build()
    {
        var surface = new FakeAutomationSurface();
        surface.ObjectId = Self;
        return (surface, new TickClock());
    }

    private static Blackboard Board(FakeAutomationSurface surface, TickClock clock) =>
        Blackboard.Capture(surface, clock, 25f, 15f);

    [Fact]
    public void PartialStacksMergeSmallestOntoLargestOnePerHalfSecond()
    {
        var tidy = new InventoryTidy(() => new InventorySettings { AutoStack = true, AutoCram = false });
        (FakeAutomationSurface surface, TickClock clock) = Build();
        surface.OwnedItems.Add(Item(11, "Arrow", Self, stack: 100, maxStack: 250));
        surface.OwnedItems.Add(Item(12, "Arrow", Self, stack: 30, maxStack: 250));
        surface.OwnedItems.Add(Item(13, "Arrow", Self, stack: 250, maxStack: 250)); // full: left alone
        surface.OwnedItems.Add(Item(14, "Arrow", Self, stack: 60, maxStack: 250));

        tidy.Tick(surface, Board(surface, clock));
        Assert.Equal(["merge:12>11"], surface.Commands);

        // Too soon for another, then the next pair once the interval passes.
        clock.Advance(0.2d);
        tidy.Tick(surface, Board(surface, clock));
        Assert.Single(surface.Commands);
        clock.Advance(0.5d);
        tidy.Tick(surface, Board(surface, clock));
        Assert.Equal(["merge:12>11", "merge:14>11"], surface.Commands);
    }

    [Fact]
    public void AMergeThatNeverLandsIsBackedOff()
    {
        var tidy = new InventoryTidy(() => new InventorySettings { AutoStack = true, AutoCram = false });
        (FakeAutomationSurface surface, TickClock clock) = Build();
        surface.OwnedItems.Add(Item(11, "Pyreal", Self, stack: 100));
        surface.OwnedItems.Add(Item(12, "Pyreal", Self, stack: 30));

        tidy.Tick(surface, Board(surface, clock));
        Assert.Single(surface.Commands);
        // Still two stacks well past the grace window: a confirmed failure, retried after the first backoff.
        clock.Advance(11d);
        tidy.Tick(surface, Board(surface, clock));
        Assert.Single(surface.Commands);
        clock.Advance(6d);
        tidy.Tick(surface, Board(surface, clock));
        Assert.Equal(2, surface.Commands.Count);
    }

    [Fact]
    public void LooseItemsAreCrammedIntoTheFullestSidePackWithRoom()
    {
        var tidy = new InventoryTidy(() => new InventorySettings { AutoStack = false, AutoCram = true });
        (FakeAutomationSurface surface, TickClock clock) = Build();
        surface.OwnedItems.Add(Item(21, "Pack", Self, slots: 24, objectClass: PluginObjectClass.Container));
        surface.OwnedItems.Add(Item(22, "Sack", Self, slots: 4, objectClass: PluginObjectClass.Container));
        surface.OwnedItems.Add(Item(31, "Gem", 22u));
        surface.OwnedItems.Add(Item(32, "Gem", 22u));
        surface.OwnedItems.Add(Item(41, "Foci of Strife", Self, objectClass: PluginObjectClass.Foci));
        surface.OwnedItems.Add(Item(42, "Silver Key", Self));

        tidy.Tick(surface, Board(surface, clock));
        Assert.Equal(["move:42>22"], surface.Commands); // the sack has exactly two free slots and is fuller than the pack

        // Nothing moves while a corpse is open.
        surface.OwnedItems.Add(Item(43, "Bronze Key", Self));
        surface.CurrentContainerId = 0x7000u;
        clock.Advance(1d);
        tidy.Tick(surface, Board(surface, clock));
        Assert.Single(surface.Commands);
    }

    [Fact]
    public void NothingIsCrammedWithoutASidePackWithRoom()
    {
        var tidy = new InventoryTidy(() => new InventorySettings { AutoStack = false, AutoCram = true });
        (FakeAutomationSurface surface, TickClock clock) = Build();
        surface.OwnedItems.Add(Item(22, "Sack", Self, slots: 2, objectClass: PluginObjectClass.Container));
        surface.OwnedItems.Add(Item(31, "Gem", 22u));
        surface.OwnedItems.Add(Item(42, "Silver Key", Self));

        tidy.Tick(surface, Board(surface, clock));
        Assert.Empty(surface.Commands);
    }
}
