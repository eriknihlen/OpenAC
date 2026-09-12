using System.Linq;
using AcDream.Core.Items;

namespace AcDream.Core.Tests.Items;

public sealed class VendorComponentFillTests
{
    private const uint TaperWcid = 0x0000055Cu;
    private const uint ScarabWcid = 0x0000029Au;
    private const uint PeaWcid = 0x000004DFu;

    private const uint TaperGuid = 0x60000101u;
    private const uint ScarabGuid = 0x60000102u;
    private const uint PeaGuid = 0x60000103u;

    private const uint TaperCategory = 5u;
    private const uint ScarabCategory = 0u;
    private const uint PeaCategory = 6u;

    private static readonly VendorShopProfile Shop = new(
        MerchandiseItemTypes: 0x1000u,
        MerchandiseMinValue: 0u,
        MerchandiseMaxValue: 0u,
        DealMagicalItems: false,
        BuyPrice: 0.75f,
        SellPrice: 1.0f,
        AlternateCurrencyWcid: 0u,
        AlternateCurrencyAmount: 0u,
        AlternateCurrencyPluralName: "");

    private static VendorShopItem Stock(
        uint guid,
        uint weenieClassId,
        int listedStack,
        int unitValue) =>
        new(
            ItemGuid: guid,
            StackSize: listedStack,
            WeenieClassId: weenieClassId,
            Name: "component",
            ItemType: 0x1000u,
            IconId: 0u,
            Value: unitValue,
            DescStackSize: 1,
            MaxStackSize: 100);

    private static ComponentFillDesire Desire(
        uint weenieClassId,
        uint category,
        string name,
        int desired,
        int owned) =>
        new(weenieClassId, category, name, desired, owned);

    /// <summary>Stage the desired count minus what the pack already holds.</summary>
    [Fact]
    public void StagesTheShortfallOfEveryStockedComponent()
    {
        ComponentFillPlan plan = VendorComponentFill.Plan(
            [
                Desire(TaperWcid, TaperCategory, "Taper", desired: 20, owned: 12),
                Desire(ScarabWcid, ScarabCategory, "Lead Scarab", desired: 5, owned: 0),
            ],
            VendorComponentFill.AnyCategory,
            maximumPrice: 0,
            [
                Stock(TaperGuid, TaperWcid, VendorComponentFill.UnlimitedStock, 5),
                Stock(ScarabGuid, ScarabWcid, VendorComponentFill.UnlimitedStock, 10),
            ],
            Shop,
            []);

        Assert.Equal(
            [
                new ComponentFillAdd(TaperGuid, 8),
                new ComponentFillAdd(ScarabGuid, 5),
            ],
            plan.Adds);
        Assert.Empty(plan.ShortComponents);
        Assert.False(plan.AbortedOnPrice);
    }

    [Fact]
    public void ComponentsAlreadyCoveredByThePackAreSkipped()
    {
        ComponentFillPlan plan = VendorComponentFill.Plan(
            [Desire(TaperWcid, TaperCategory, "Taper", desired: 20, owned: 20)],
            VendorComponentFill.AnyCategory,
            maximumPrice: 0,
            [Stock(TaperGuid, TaperWcid, VendorComponentFill.UnlimitedStock, 5)],
            Shop,
            []);

        Assert.Empty(plan.Adds);
        Assert.Empty(plan.ShortComponents);
    }

    [Fact]
    public void AComponentTheShopDoesNotCarryIsSkippedAndReported()
    {
        ComponentFillPlan plan = VendorComponentFill.Plan(
            [
                Desire(TaperWcid, TaperCategory, "Taper", desired: 20, owned: 12),
                Desire(PeaWcid, PeaCategory, "Pea", desired: 4, owned: 0),
            ],
            VendorComponentFill.AnyCategory,
            maximumPrice: 0,
            [Stock(TaperGuid, TaperWcid, VendorComponentFill.UnlimitedStock, 5)],
            Shop,
            []);

        Assert.Equal([new ComponentFillAdd(TaperGuid, 8)], plan.Adds);
        Assert.Equal(["Pea"], plan.ShortComponents);
    }

    [Fact]
    public void ShortStockIsStagedDownToWhatTheShopHasAndReported()
    {
        ComponentFillPlan plan = VendorComponentFill.Plan(
            [Desire(TaperWcid, TaperCategory, "Taper", desired: 20, owned: 12)],
            VendorComponentFill.AnyCategory,
            maximumPrice: 0,
            [Stock(TaperGuid, TaperWcid, listedStack: 3, unitValue: 5)],
            Shop,
            []);

        Assert.Equal([new ComponentFillAdd(TaperGuid, 3)], plan.Adds);
        Assert.Equal(["Taper"], plan.ShortComponents);
    }

    [Fact]
    public void OnlyTheRequestedCategoryIsFilled()
    {
        ComponentFillPlan plan = VendorComponentFill.Plan(
            [
                Desire(TaperWcid, TaperCategory, "Taper", desired: 20, owned: 12),
                Desire(ScarabWcid, ScarabCategory, "Lead Scarab", desired: 5, owned: 0),
            ],
            ScarabCategory,
            maximumPrice: 0,
            [
                Stock(TaperGuid, TaperWcid, VendorComponentFill.UnlimitedStock, 5),
                Stock(ScarabGuid, ScarabWcid, VendorComponentFill.UnlimitedStock, 10),
            ],
            Shop,
            []);

        Assert.Equal([new ComponentFillAdd(ScarabGuid, 5)], plan.Adds);
    }

    /// <summary>What the buy list already holds counts against the shortfall.</summary>
    [Fact]
    public void AlreadyStagedQuantityCountsAgainstTheShortfall()
    {
        ComponentFillPlan plan = VendorComponentFill.Plan(
            [Desire(TaperWcid, TaperCategory, "Taper", desired: 20, owned: 12)],
            VendorComponentFill.AnyCategory,
            maximumPrice: 0,
            [Stock(TaperGuid, TaperWcid, VendorComponentFill.UnlimitedStock, 5)],
            Shop,
            [new VendorStagingEntry(TaperGuid, 6)]);

        Assert.Equal([new ComponentFillAdd(TaperGuid, 2)], plan.Adds);
    }

    [Fact]
    public void AShortfallAlreadyFullyStagedAddsNothing()
    {
        ComponentFillPlan plan = VendorComponentFill.Plan(
            [Desire(TaperWcid, TaperCategory, "Taper", desired: 20, owned: 12)],
            VendorComponentFill.AnyCategory,
            maximumPrice: 0,
            [Stock(TaperGuid, TaperWcid, VendorComponentFill.UnlimitedStock, 5)],
            Shop,
            [new VendorStagingEntry(TaperGuid, 8)]);

        Assert.Empty(plan.Adds);
    }

    /// <summary>
    /// The ceiling is tested before each row, so the buy that crossed it is
    /// dropped and the walk stops there.
    /// </summary>
    [Fact]
    public void ThePriceCeilingRollsBackTheBuyThatCrossedItAndStops()
    {
        ComponentFillPlan plan = VendorComponentFill.Plan(
            [
                Desire(TaperWcid, TaperCategory, "Taper", desired: 10, owned: 0),
                Desire(ScarabWcid, ScarabCategory, "Lead Scarab", desired: 10, owned: 0),
                Desire(PeaWcid, PeaCategory, "Pea", desired: 10, owned: 0),
            ],
            VendorComponentFill.AnyCategory,
            maximumPrice: 60,
            [
                Stock(TaperGuid, TaperWcid, VendorComponentFill.UnlimitedStock, 5),
                Stock(ScarabGuid, ScarabWcid, VendorComponentFill.UnlimitedStock, 5),
                Stock(PeaGuid, PeaWcid, VendorComponentFill.UnlimitedStock, 5),
            ],
            Shop,
            []);

        // 50 spent after the tapers is still under 60, so the scarabs go on
        // too and reach 100; the peas row then finds the ceiling met.
        Assert.Equal(
            [
                new ComponentFillAdd(TaperGuid, 10),
                new ComponentFillAdd(ScarabGuid, 10),
            ],
            plan.Adds);
        Assert.True(plan.AbortedOnPrice);
    }

    [Fact]
    public void NoCeilingMeansNoAbort()
    {
        ComponentFillPlan plan = VendorComponentFill.Plan(
            [
                Desire(TaperWcid, TaperCategory, "Taper", desired: 10, owned: 0),
                Desire(ScarabWcid, ScarabCategory, "Lead Scarab", desired: 10, owned: 0),
            ],
            VendorComponentFill.AnyCategory,
            maximumPrice: 0,
            [
                Stock(TaperGuid, TaperWcid, VendorComponentFill.UnlimitedStock, 5000),
                Stock(ScarabGuid, ScarabWcid, VendorComponentFill.UnlimitedStock, 5000),
            ],
            Shop,
            []);

        Assert.Equal(2, plan.Adds.Count);
        Assert.False(plan.AbortedOnPrice);
    }

    [Fact]
    public void ShortComponentsReadAsOneSentence()
    {
        Assert.Equal(
            "There was not enough: Taper, Pea",
            VendorComponentFill.FormatShortComponents(["Taper", "Pea"]));
        Assert.Equal(
            "There was not enough: Taper",
            VendorComponentFill.FormatShortComponents(["Taper"]));
    }

    [Fact]
    public void AnEmptyComponentBookStagesNothing()
    {
        ComponentFillPlan plan = VendorComponentFill.Plan(
            [],
            VendorComponentFill.AnyCategory,
            maximumPrice: 0,
            [Stock(TaperGuid, TaperWcid, VendorComponentFill.UnlimitedStock, 5)],
            Shop,
            []);

        Assert.Empty(plan.Adds);
        Assert.Empty(plan.ShortComponents);
        Assert.False(plan.AbortedOnPrice);
    }

    [Fact]
    public void ThePlanPricesEachBuyWithTheShopSellRate()
    {
        // A doubled sell rate halves how many 5-pyreal tapers fit under a 60 cap.
        VendorShopProfile dear = Shop with { SellPrice = 2.0f };
        ComponentFillPlan plan = VendorComponentFill.Plan(
            [
                Desire(TaperWcid, TaperCategory, "Taper", desired: 10, owned: 0),
                Desire(ScarabWcid, ScarabCategory, "Lead Scarab", desired: 10, owned: 0),
            ],
            VendorComponentFill.AnyCategory,
            maximumPrice: 60,
            [
                Stock(TaperGuid, TaperWcid, VendorComponentFill.UnlimitedStock, 5),
                Stock(ScarabGuid, ScarabWcid, VendorComponentFill.UnlimitedStock, 5),
            ],
            dear,
            []);

        // The tapers alone cost 100, so the scarabs row finds the ceiling met
        // and the tapers are rolled back.
        Assert.Single(plan.Adds);
        Assert.True(plan.AbortedOnPrice);
        Assert.Equal(TaperGuid, plan.Adds.Single().ItemGuid);
    }
}
