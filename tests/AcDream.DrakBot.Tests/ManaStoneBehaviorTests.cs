using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class ManaStoneBehaviorTests
{
    private static PluginInventoryItem Item(uint id, string name, PluginObjectClass objectClass, int mana, int maxMana, bool worn = false) => new(
        id, 0u, name, 0u, 0u, 0u, 0u, worn ? 0x10u : 0u, 0u, 0u, 0u, 1, 0, 0, 0u, 0, 0, 0u,
        false, 0d, 0, 0, 0, 0d, 0, 0, 0)
    {
        ItemCurrentMana = mana,
        ItemMaximumMana = maxMana,
        ObjectClass = objectClass,
    };

    private static (FakeAutomationSurface Surface, ManaStoneBehavior Behavior, TickClock Clock) Build(ManaStoneSettings settings)
    {
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        return (surface, new ManaStoneBehavior(() => settings), clock);
    }

    private static BehaviorContext Context(FakeAutomationSurface surface, TickClock clock) =>
        new(surface, new FakeLogger(), Blackboard.Capture(surface, clock, 25f, 15f));

    [Fact]
    public void AChargedStoneIsUsedOnTheCharacterWhenAWornItemRunsLow()
    {
        var settings = new ManaStoneSettings { Enabled = true };
        (FakeAutomationSurface surface, ManaStoneBehavior behavior, TickClock clock) = Build(settings);
        surface.OwnedItems.Add(Item(11, "Coat", PluginObjectClass.Armor, mana: 100, maxMana: 1000, worn: true));
        surface.OwnedItems.Add(Item(12, "Mana Stone", PluginObjectClass.ManaStone, mana: 0, maxMana: 5000));
        surface.OwnedItems.Add(Item(13, "Mana Stone", PluginObjectClass.ManaStone, mana: 3000, maxMana: 5000));

        clock.Advance(1d);
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out _));
        Assert.Equal(StepResult.Continue, behavior.Execute(Context(surface, clock)).Result);
        Assert.Equal(StepResult.Continue, behavior.Execute(Context(surface, clock)).Result);
        Assert.Equal([$"apply:13@{surface.Character.ObjectId}"], surface.Commands);

        // One recharge every five minutes at most, even if the item still reads low.
        clock.Advance(30d);
        behavior.Execute(Context(surface, clock));
        behavior.Execute(Context(surface, clock));
        Assert.Single(surface.Commands);
    }

    [Fact]
    public void LootWithEnoughManaIsDrainedIntoAnEmptyStone()
    {
        var settings = new ManaStoneSettings { Enabled = true, TapThresholdMana = 2500 };
        (FakeAutomationSurface surface, ManaStoneBehavior behavior, TickClock clock) = Build(settings);
        surface.OwnedItems.Add(Item(11, "Coat", PluginObjectClass.Armor, mana: 900, maxMana: 1000, worn: true));
        surface.OwnedItems.Add(Item(12, "Mana Stone", PluginObjectClass.ManaStone, mana: 0, maxMana: 5000));
        surface.OwnedItems.Add(Item(14, "Sceptre", PluginObjectClass.WandStaffOrb, mana: 4000, maxMana: 4000));
        surface.OwnedItems.Add(Item(15, "Gauntlets", PluginObjectClass.Armor, mana: 1200, maxMana: 3000));
        surface.OwnedItems.Add(Item(16, "Helm", PluginObjectClass.Armor, mana: 2700, maxMana: 3000));

        clock.Advance(1d);
        behavior.Execute(Context(surface, clock));
        behavior.Execute(Context(surface, clock));
        Assert.Equal(["apply:12@16"], surface.Commands);
    }

    [Fact]
    public void NothingHappensWithHostilesAboutOrTappingOff()
    {
        var settings = new ManaStoneSettings { Enabled = true, TapThresholdMana = 0 };
        (FakeAutomationSurface surface, ManaStoneBehavior behavior, TickClock clock) = Build(settings);
        surface.OwnedItems.Add(Item(12, "Mana Stone", PluginObjectClass.ManaStone, mana: 0, maxMana: 5000));
        surface.OwnedItems.Add(Item(16, "Helm", PluginObjectClass.Armor, mana: 2700, maxMana: 3000));

        clock.Advance(1d);
        Assert.Equal(StepResult.Done, behavior.Execute(Context(surface, clock)).Result); // threshold 0: no draining
        Assert.Empty(surface.Commands);

        surface.Hostiles.Add(new PluginCombatTarget(1u, "Drudge", 0u, 6f, 0f, true, 1f));
        clock.Advance(1d);
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
    }
}
