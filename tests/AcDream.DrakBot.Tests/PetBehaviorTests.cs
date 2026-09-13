using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class PetBehaviorTests
{
    private static PluginInventoryItem Essence(uint id, string name, int charges, int max) => new(
        id, 0u, name, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 1, charges, max, 0u, 7, 0, 0u,
        false, 0d, 0, 0, 0, 0d, 0, 0, 0);

    private static PluginInventoryItem Spirit(uint id) => new(
        id, 0u, "Encapsulated Spirit", 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 1, 0, 0, 0u, 0, 0, 0u,
        false, 0d, 0, 0, 0, 0d, 0, 0, 0);

    private static (FakeAutomationSurface Surface, PetBehavior Behavior, TickClock Clock) Build(PetSettings settings)
    {
        var surface = new FakeAutomationSurface();
        surface.Name = "Drakkon";
        surface.SummoningMastery = 1;
        var clock = new TickClock();
        return (surface, new PetBehavior(() => settings), clock);
    }

    private static BehaviorContext Context(FakeAutomationSurface surface, TickClock clock) =>
        new(surface, new FakeLogger(), Blackboard.Capture(surface, clock, 25f, 15f));

    [Fact]
    public void SummonsFromTheFirstChargedEssenceWhenHostilesCrowdIn()
    {
        var settings = new PetSettings { Enabled = true, Devices = ["Golem Essence"], MinimumHostiles = 2, RangeMeters = 20f };
        (FakeAutomationSurface surface, PetBehavior behavior, TickClock clock) = Build(settings);
        surface.OwnedItems.Add(Essence(11, "Frozen Golem Essence", charges: 3, max: 5));
        surface.Hostiles.Add(new PluginCombatTarget(1u, "Drudge", 0u, 6f, 0f, true, 1f));

        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _)); // one hostile is not enough
        surface.Hostiles.Add(new PluginCombatTarget(2u, "Drudge", 0u, 9f, 0f, true, 1f));
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Contains("summoning", reason);

        behavior.Execute(Context(surface, clock));
        Assert.Equal(["use:11"], surface.Commands);

        // The pet stands beside the character: no second summon.
        surface.WorldObjects.Add(new PluginWorldObject(0x9000_0001u, 0u, "Drakkon's Frozen Golem", PluginObjectClass.Monster, 0u, 0u, 0u));
        clock.Advance(30d);
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out _));
        Assert.Equal(StepResult.Done, behavior.Execute(Context(surface, clock)).Result);
        Assert.Single(surface.Commands);
    }

    [Fact]
    public void AnEmptyEssenceIsRefilledFromASpirit()
    {
        var settings = new PetSettings { Enabled = true, Devices = ["Golem Essence"], MinimumHostiles = 1 };
        (FakeAutomationSurface surface, PetBehavior behavior, TickClock clock) = Build(settings);
        surface.OwnedItems.Add(Essence(11, "Frozen Golem Essence", charges: 0, max: 5));
        surface.OwnedItems.Add(Spirit(12));
        surface.Hostiles.Add(new PluginCombatTarget(1u, "Drudge", 0u, 6f, 0f, true, 1f));

        behavior.Execute(Context(surface, clock));
        Assert.Equal(["apply:12@11"], surface.Commands);
    }
}
