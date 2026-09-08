using AcDream.Launcher.Core.Installation;

namespace AcDream.Launcher.Core.Tests.Installation;

public sealed class ContentMigrationCatalogTests
{
    [Fact]
    public void RecipeSixToSevenRequiresOneExplicitFullRebuild()
    {
        ContentMigrationPlan plan = ContentMigrationCatalog.Resolve(6, 7);

        Assert.Equal(ContentWorkKind.FullRebuild, plan.Kind);
        Assert.Equal(6u, plan.FromRecipeVersion);
        Assert.Equal(7u, plan.TargetRecipeVersion);
        Assert.Contains("DrawingBSP", plan.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(plan.EffectiveDatIds);
        Assert.Empty(plan.EffectiveLandblocks);
    }

    [Fact]
    public void AnyOlderRecipeToSevenCollapsesToOneFullRebuild()
    {
        ContentMigrationPlan plan = ContentMigrationCatalog.Resolve(1, 7);

        Assert.Equal(ContentWorkKind.FullRebuild, plan.Kind);
        Assert.Equal(7u, plan.TargetRecipeVersion);
        Assert.Contains("pak v2", plan.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DrawingBSP", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecipeSevenToEightRequiresOneExplicitFullRebuild()
    {
        ContentMigrationPlan plan = ContentMigrationCatalog.Resolve(7, 8);

        Assert.Equal(ContentWorkKind.FullRebuild, plan.Kind);
        Assert.Equal(7u, plan.FromRecipeVersion);
        Assert.Equal(8u, plan.TargetRecipeVersion);
        Assert.Contains("CellStruct", plan.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(plan.EffectiveDatIds);
        Assert.Empty(plan.EffectiveLandblocks);
    }

    [Fact]
    public void AnyOlderRecipeToEightCollapsesToOneFullRebuild()
    {
        ContentMigrationPlan plan = ContentMigrationCatalog.Resolve(1, 8);

        Assert.Equal(ContentWorkKind.FullRebuild, plan.Kind);
        Assert.Equal(8u, plan.TargetRecipeVersion);
        Assert.Contains("pak v2", plan.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DrawingBSP", plan.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CellStruct", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecipeEightToNineRequiresOneExplicitFullRebuild()
    {
        ContentMigrationPlan plan = ContentMigrationCatalog.Resolve(8, 9);

        Assert.Equal(ContentWorkKind.FullRebuild, plan.Kind);
        Assert.Equal(8u, plan.FromRecipeVersion);
        Assert.Equal(9u, plan.TargetRecipeVersion);
        Assert.Contains("surface opacity", plan.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(plan.EffectiveDatIds);
        Assert.Empty(plan.EffectiveLandblocks);
    }

    [Fact]
    public void AnyOlderRecipeToNineCollapsesToOneFullRebuild()
    {
        ContentMigrationPlan plan = ContentMigrationCatalog.Resolve(1, 9);

        Assert.Equal(ContentWorkKind.FullRebuild, plan.Kind);
        Assert.Equal(9u, plan.TargetRecipeVersion);
        Assert.Contains("CellStruct", plan.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("surface opacity", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecipeNineToTenRequiresOneExplicitFullRebuild()
    {
        ContentMigrationPlan plan = ContentMigrationCatalog.Resolve(9, 10);

        Assert.Equal(ContentWorkKind.FullRebuild, plan.Kind);
        Assert.Equal(9u, plan.FromRecipeVersion);
        Assert.Equal(10u, plan.TargetRecipeVersion);
        Assert.Contains("SetSurface", plan.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(plan.EffectiveDatIds);
        Assert.Empty(plan.EffectiveLandblocks);
    }

    [Fact]
    public void AnyOlderRecipeToTenCollapsesToOneFullRebuild()
    {
        ContentMigrationPlan plan = ContentMigrationCatalog.Resolve(1, 10);

        Assert.Equal(ContentWorkKind.FullRebuild, plan.Kind);
        Assert.Equal(10u, plan.TargetRecipeVersion);
        Assert.Contains("surface opacity", plan.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SetSurface", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
