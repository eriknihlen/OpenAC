using AcDream.DrakBot.Combat;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class AmmoCrafterTests
{
    private static PluginInventoryItem Item(uint id, string name, int stack = 1) => new(
        id, 0u, name, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, stack, 0, 0, 0u, 0, 0, 0u,
        false, 0d, 0, 0, 0, 0d, 0, 0, 0);

    private static FakeAutomationSurface Surface(PluginSkillTraining fletching)
    {
        var surface = new FakeAutomationSurface();
        surface.Skills = [new PluginSkillInfo(37u, "Fletching", fletching, 200u)];
        return surface;
    }

    [Fact]
    public void TheBestRecipeTheCharacterCanFletchWins()
    {
        FakeAutomationSurface surface = Surface(PluginSkillTraining.Trained);
        surface.OwnedItems.Add(Item(11, "Wrapped Bundle of Arrowshafts", 5));
        surface.OwnedItems.Add(Item(12, "Wrapped Bundle of Arrowheads", 5));
        surface.OwnedItems.Add(Item(13, "Wrapped Bundle of Deadly Prismatic Arrowheads", 5)); // needs specialized
        surface.OwnedItems.Add(Item(14, "Wrapped Bundle of Greater Prismatic Arrowheads", 5));
        surface.OwnedItems.Add(Item(15, "Wrapped Bundle of Quarrelheads", 5)); // wrong weapon

        AmmoCrafter.Recipe? recipe = AmmoCrafter.BestCraftable(surface.OwnedItems, AmmoCrafter.WeaponCategory.Bow, PluginSkillTraining.Trained);
        Assert.Equal("Greater Prismatic Arrow", recipe?.Output);
        recipe = AmmoCrafter.BestCraftable(surface.OwnedItems, AmmoCrafter.WeaponCategory.Bow, PluginSkillTraining.Specialized);
        Assert.Equal("Deadly Prismatic Arrow", recipe?.Output);
        Assert.Null(AmmoCrafter.BestCraftable(surface.OwnedItems, AmmoCrafter.WeaponCategory.Bow, PluginSkillTraining.Untrained));
        Assert.Null(AmmoCrafter.BestCraftable(surface.OwnedItems, AmmoCrafter.WeaponCategory.Atlatl, PluginSkillTraining.Specialized));
        Assert.Equal(AmmoCrafter.WeaponCategory.Crossbow, AmmoCrafter.CategoryOf("Heavy Crossbow"));
        Assert.Equal(AmmoCrafter.WeaponCategory.Atlatl, AmmoCrafter.CategoryOf("Royal Atlatl"));
        Assert.Equal(AmmoCrafter.WeaponCategory.Bow, AmmoCrafter.CategoryOf("Yumi"));
    }

    [Fact]
    public void APlainBundleOfHeadsNeedsAPlainBundleOfShaftsAndTheWrappedPairComesFirst()
    {
        // The game has two sizes of the recipe and they do not mix: a plain
        // bundle of heads with a wrapped bundle of shafts makes nothing,
        // which is what a character once carried. With the plain shafts
        // beside it the hundred is made; with both pairs, the thousand.
        FakeAutomationSurface surface = Surface(PluginSkillTraining.Specialized);
        surface.OwnedItems.Add(Item(11, "Wrapped Bundle of Arrowshafts", 5));
        surface.OwnedItems.Add(Item(12, "Bundle of Deadly Prismatic Arrowheads", 5));
        Assert.Null(AmmoCrafter.BestCraftable(surface.OwnedItems, AmmoCrafter.WeaponCategory.Bow, PluginSkillTraining.Specialized));

        surface.OwnedItems.Add(Item(13, "Bundle of Arrowshafts", 5));
        AmmoCrafter.Recipe? recipe = AmmoCrafter.BestCraftable(surface.OwnedItems, AmmoCrafter.WeaponCategory.Bow, PluginSkillTraining.Specialized);
        Assert.Equal("Bundle of Deadly Prismatic Arrowheads", recipe?.Heads);
        Assert.Equal("Bundle of Arrowshafts", recipe?.Shafts);

        surface.OwnedItems.Add(Item(14, "Wrapped Bundle of Deadly Prismatic Arrowheads", 5));
        recipe = AmmoCrafter.BestCraftable(surface.OwnedItems, AmmoCrafter.WeaponCategory.Bow, PluginSkillTraining.Specialized);
        Assert.Equal("Wrapped Bundle of Deadly Prismatic Arrowheads", recipe?.Heads);
        Assert.Equal("Wrapped Bundle of Arrowshafts", recipe?.Shafts);
    }

    [Fact]
    public void TwoCombinesAreMadeEachConfirmedByTheOutputTurningUp()
    {
        FakeAutomationSurface surface = Surface(PluginSkillTraining.Trained);
        surface.OwnedItems.Add(Item(11, "Wrapped Bundle of Quarrelshafts", 5));
        surface.OwnedItems.Add(Item(12, "Wrapped Bundle of Broad Quarrelheads", 5));
        var crafter = new AmmoCrafter();

        Assert.Equal(AmmoCrafter.Verdict.Crafting, crafter.Tick(surface, AmmoCrafter.WeaponCategory.Crossbow, 0d, out _));
        Assert.Equal(["apply:12@11"], surface.Commands);
        // No answer yet: a retry after two seconds, no sooner.
        Assert.Equal(AmmoCrafter.Verdict.Crafting, crafter.Tick(surface, AmmoCrafter.WeaponCategory.Crossbow, 1d, out _));
        Assert.Single(surface.Commands);
        Assert.Equal(AmmoCrafter.Verdict.Crafting, crafter.Tick(surface, AmmoCrafter.WeaponCategory.Crossbow, 2.5d, out _));
        Assert.Equal(2, surface.Commands.Count);

        surface.OwnedItems.Add(Item(20, "Broad Head Quarrel", 100));
        Assert.Equal(AmmoCrafter.Verdict.Crafting, crafter.Tick(surface, AmmoCrafter.WeaponCategory.Crossbow, 3d, out string detail));
        Assert.Contains("2/2", detail);
        Assert.Equal(3, surface.Commands.Count); // the second combine goes out at once

        surface.OwnedItems[2] = Item(20, "Broad Head Quarrel", 200);
        Assert.Equal(AmmoCrafter.Verdict.Crafted, crafter.Tick(surface, AmmoCrafter.WeaponCategory.Crossbow, 4d, out detail));
        Assert.Contains("Broad Head Quarrel", detail);
        Assert.False(crafter.IsCrafting);
    }

    [Fact]
    public void AFailedUseCompletionOrTimeoutGivesUp()
    {
        FakeAutomationSurface surface = Surface(PluginSkillTraining.Specialized);
        surface.OwnedItems.Add(Item(11, "Wrapped Bundle of Atlatl Dartshafts", 1));
        surface.OwnedItems.Add(Item(12, "Wrapped Bundle of Lethal Prismatic Atlatl Dart Heads", 1));
        var crafter = new AmmoCrafter();

        crafter.Tick(surface, AmmoCrafter.WeaponCategory.Atlatl, 0d, out _);
        surface.LastItemUseCompletion = new PluginItemUseCompletion(1, 12u, 11u, 0x1Fu);
        Assert.Equal(AmmoCrafter.Verdict.Failed, crafter.Tick(surface, AmmoCrafter.WeaponCategory.Atlatl, 1d, out _));

        crafter.Tick(surface, AmmoCrafter.WeaponCategory.Atlatl, 10d, out _);
        Assert.Equal(AmmoCrafter.Verdict.Failed, crafter.Tick(surface, AmmoCrafter.WeaponCategory.Atlatl, 26d, out string detail));
        Assert.Contains("timed out", detail);
    }
}
