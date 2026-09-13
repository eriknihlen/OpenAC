using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Loot.Utl;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class SalvageBehaviorTests
{
    private static PluginInventoryItem Item(uint id, string name, PluginObjectClass objectClass, int structure = 0, int maxStructure = 0, float workmanship = 0f, uint material = 0u) => new(
        id, 0u, name, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 1, structure, maxStructure, 0u, 0, 0, 0u,
        false, 0d, 0, 0, 0, 0d, 0, 0, 0)
    {
        ObjectClass = objectClass,
        Workmanship = workmanship,
        MaterialType = material,
    };

    private static PluginInventoryItem Bag(uint id, uint material, int units, float workmanship) =>
        Item(id, "Salvage", PluginObjectClass.Salvage, units, 100, workmanship, material);

    private static (FakeAutomationSurface Surface, SalvageBehavior Behavior, TickClock Clock) Build(SalvageSettings settings, SalvageCombineSettings? combine = null)
    {
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        surface.OwnedItems.Add(Item(1, "Ust", PluginObjectClass.Ust));
        return (surface, new SalvageBehavior(() => settings, () => combine, surface.CaptureOwnedItems), clock);
    }

    private static BehaviorContext Context(FakeAutomationSurface surface, TickClock clock) =>
        new(surface, new FakeLogger(), Blackboard.Capture(surface, clock, 25f, 15f));

    [Fact]
    public void QueuedItemsGoIntoOneSalvageRequestAndAreForgottenWhenTheyLeaveThePack()
    {
        (FakeAutomationSurface surface, SalvageBehavior behavior, TickClock clock) = Build(new SalvageSettings { CombineBags = false });
        surface.OwnedItems.Add(Item(11, "Dagger", PluginObjectClass.MeleeWeapon));
        surface.OwnedItems.Add(Item(12, "Coat", PluginObjectClass.Armor));
        behavior.Enqueue(11);
        behavior.Enqueue(12);
        behavior.Enqueue(13); // never picked up: skipped

        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Contains("to salvage", reason);
        Assert.Equal(StepResult.Continue, behavior.Execute(Context(surface, clock)).Result);
        Assert.Equal(["salvage:1:11,12"], surface.Commands);

        surface.OwnedItems.RemoveAll(item => item.ObjectId is 11 or 12);
        Assert.Equal(StepResult.Done, behavior.Execute(Context(surface, clock)).Result);
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
    }

    [Fact]
    public void AnItemStillThereAfterTheWaitIsRetriedThreeTimesThenDropped()
    {
        (FakeAutomationSurface surface, SalvageBehavior behavior, TickClock clock) = Build(new SalvageSettings { CombineBags = false });
        surface.OwnedItems.Add(Item(11, "Dagger", PluginObjectClass.MeleeWeapon));
        behavior.Enqueue(11);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Assert.True(behavior.WantsControl(Context(surface, clock).Board, out _));
            behavior.Execute(Context(surface, clock));
            clock.Advance(7d);
            Assert.Equal(StepResult.Failed, behavior.Execute(Context(surface, clock)).Result);
            clock.Advance(3d);
        }
        Assert.Equal(3, surface.Commands.Count);
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
    }

    [Fact]
    public void UnderFullBagsOfOneMaterialAndBandAreMergedOnTheSweep()
    {
        (FakeAutomationSurface surface, SalvageBehavior behavior, TickClock clock) = Build(new SalvageSettings());
        surface.OwnedItems.Add(Bag(21, material: 60, units: 40, workmanship: 5.2f));
        surface.OwnedItems.Add(Bag(22, material: 60, units: 30, workmanship: 6.4f));
        surface.OwnedItems.Add(Bag(23, material: 60, units: 30, workmanship: 7.5f)); // other band
        surface.OwnedItems.Add(Bag(24, material: 61, units: 30, workmanship: 5f)); // other material
        surface.OwnedItems.Add(Bag(25, material: 60, units: 100, workmanship: 5f)); // full

        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Contains("sweep", reason);
        behavior.Execute(Context(surface, clock));
        Assert.Equal(["salvage:1:21,22"], surface.Commands);

        surface.OwnedItems.RemoveAll(item => item.ObjectId == 22);
        Assert.Equal(StepResult.Done, behavior.Execute(Context(surface, clock)).Result);
        // No sweep again for half a minute, and none without a pair to merge.
        surface.OwnedItems.Add(Bag(26, material: 61, units: 20, workmanship: 4f));
        clock.Advance(10d);
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
        clock.Advance(25d);
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out _));
        behavior.Execute(Context(surface, clock));
        Assert.Equal("salvage:1:24,26", surface.Commands[^1]);
        surface.OwnedItems.RemoveAll(item => item.ObjectId == 26);
        behavior.Execute(Context(surface, clock));
        clock.Advance(30d);
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
    }

    [Fact]
    public void TheProfilesCombineBandsDecideWhatMerges()
    {
        var combine = new SalvageCombineSettings { DefaultBands = "1-10" };
        (FakeAutomationSurface surface, SalvageBehavior behavior, TickClock clock) = Build(new SalvageSettings(), combine);
        surface.OwnedItems.Add(Bag(21, material: 60, units: 40, workmanship: 3f));
        surface.OwnedItems.Add(Bag(23, material: 60, units: 30, workmanship: 9f));

        behavior.Execute(Context(surface, clock));
        Assert.Equal(["salvage:1:21,23"], surface.Commands);
    }

    [Fact]
    public void NothingHappensWithHostilesAboutOrWithoutAnUst()
    {
        (FakeAutomationSurface surface, SalvageBehavior behavior, TickClock clock) = Build(new SalvageSettings { CombineBags = false });
        surface.OwnedItems.Add(Item(11, "Dagger", PluginObjectClass.MeleeWeapon));
        behavior.Enqueue(11);
        surface.Hostiles.Add(new PluginCombatTarget(1u, "Drudge", 0u, 6f, 0f, true, 1f));
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));

        surface.Hostiles.Clear();
        surface.OwnedItems.RemoveAll(item => item.ObjectId == 1);
        Assert.Equal(StepResult.Failed, behavior.Execute(Context(surface, clock)).Result);
        Assert.Empty(surface.Commands);
        Assert.Equal(0, behavior.Queued);
    }
}
