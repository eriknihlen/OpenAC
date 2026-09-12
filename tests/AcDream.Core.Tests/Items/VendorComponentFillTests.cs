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

    /// <summary>
    /// Exactly-covered is the ordinary case and matches the original: it
    /// stages a zero quantity, which merges as a no-op.
    /// </summary>
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
        Assert.Empty(plan.ShortComponents);
    }

    /// <summary>
    /// Over-staging is the one deliberate departure. The shortfall is 8 and
    /// 15 are already staged; we stage nothing more and say nothing. The
    /// original subtracts these in unsigned arithmetic, so the remainder
    /// wraps to a huge number, fails the availability compare, and stages the
    /// whole available amount a second time while reporting a shortfall that
    /// is not real. Only this strict over-stage case differs.
    /// </summary>
    [Fact]
    public void AShortfallAlreadyOverStagedAddsNothingAndReportsNothing()
    {
        ComponentFillPlan plan = VendorComponentFill.Plan(
            [Desire(TaperWcid, TaperCategory, "Taper", desired: 20, owned: 12)],
            VendorComponentFill.AnyCategory,
            maximumPrice: 0,
            [Stock(TaperGuid, TaperWcid, VendorComponentFill.UnlimitedStock, 5)],
            Shop,
            [new VendorStagingEntry(TaperGuid, 15)]);

        Assert.Empty(plan.Adds);
        Assert.Empty(plan.ShortComponents);
        Assert.False(plan.AbortedOnPrice);
    }

    /// <summary>
    /// The category words the command understands and the numbering the
    /// component catalog groups rows by are the same seven values, so a
    /// named category fills the category the player meant. The catalog reads
    /// its number straight out of the game data, so this pins our half.
    /// </summary>
    [Fact]
    public void TheSevenCategoriesAreNumberedInTableOrder()
    {
        Assert.Equal(0u, VendorComponentFill.ScarabCategory);
        Assert.Equal(1u, VendorComponentFill.HerbCategory);
        Assert.Equal(2u, VendorComponentFill.PowderedGemCategory);
        Assert.Equal(3u, VendorComponentFill.AlchemicalSubstanceCategory);
        Assert.Equal(4u, VendorComponentFill.TalismanCategory);
        Assert.Equal(5u, VendorComponentFill.TaperCategory);
        Assert.Equal(6u, VendorComponentFill.PeaCategory);
        Assert.Equal(7u, VendorComponentFill.CategoryCount);
        Assert.Equal(8u, VendorComponentFill.AnyCategory);
    }

    /// <summary>A component with no resolvable name is never named as short.</summary>
    [Fact]
    public void ANamelessComponentIsSkippedWithoutNamingABlank()
    {
        ComponentFillPlan plan = VendorComponentFill.Plan(
            [
                Desire(PeaWcid, PeaCategory, "", desired: 4, owned: 0),
                Desire(ScarabWcid, ScarabCategory, "Lead Scarab", desired: 4, owned: 0),
            ],
            VendorComponentFill.AnyCategory,
            maximumPrice: 0,
            [Stock(TaperGuid, TaperWcid, VendorComponentFill.UnlimitedStock, 5)],
            Shop,
            []);

        Assert.Empty(plan.Adds);
        Assert.Equal(["Lead Scarab"], plan.ShortComponents);
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

    private const uint PlayerGuid = 0x50000001u;

    private static ClientObjectTable PackWith(params (uint Guid, uint Wcid, int Stack)[] items)
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = PlayerGuid });
        foreach ((uint guid, uint wcid, int stack) in items)
        {
            objects.AddOrUpdate(new ClientObject
            {
                ObjectId = guid,
                WeenieClassId = wcid,
                ContainerId = PlayerGuid,
                StackSize = stack,
            });
        }
        return objects;
    }

    private static Func<uint, ComponentDescription?> Catalog(
        params (uint Wcid, uint Category, string Name)[] entries) =>
        wcid =>
        {
            foreach ((uint entryWcid, uint category, string name) in entries)
            {
                if (entryWcid == wcid)
                    return new ComponentDescription(category, name);
            }
            return null;
        };

    [Fact]
    public void BuildDesiresSumsEveryCarriedStackOfEachComponent()
    {
        IReadOnlyList<ComponentFillDesire> desires = VendorComponentFill.BuildDesires(
            new Dictionary<uint, uint> { [TaperWcid] = 20u },
            PackWith(
                (0x60000201u, TaperWcid, 7),
                (0x60000202u, TaperWcid, 5),
                (0x60000203u, ScarabWcid, 99)),
            PlayerGuid,
            Catalog((TaperWcid, TaperCategory, "Prismatic Taper")),
            []);

        ComponentFillDesire desire = Assert.Single(desires);
        Assert.Equal(TaperWcid, desire.WeenieClassId);
        Assert.Equal(TaperCategory, desire.Category);
        Assert.Equal("Prismatic Taper", desire.Name);
        Assert.Equal(20, desire.Desired);
        Assert.Equal(12, desire.Owned);
    }

    [Fact]
    public void BuildDesiresIgnoresItemsThePlayerIsNotCarrying()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = PlayerGuid });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x60000301u,
            WeenieClassId = TaperWcid,
            StackSize = 40,
        });

        IReadOnlyList<ComponentFillDesire> desires = VendorComponentFill.BuildDesires(
            new Dictionary<uint, uint> { [TaperWcid] = 20u },
            objects,
            PlayerGuid,
            Catalog((TaperWcid, TaperCategory, "Prismatic Taper")),
            []);

        Assert.Equal(0, Assert.Single(desires).Owned);
    }

    [Fact]
    public void BuildDesiresDropsRowsAskingForNone()
    {
        IReadOnlyList<ComponentFillDesire> desires = VendorComponentFill.BuildDesires(
            new Dictionary<uint, uint> { [TaperWcid] = 0u, [ScarabWcid] = 3u },
            PackWith(),
            PlayerGuid,
            Catalog(
                (TaperWcid, TaperCategory, "Prismatic Taper"),
                (ScarabWcid, ScarabCategory, "Lead Scarab")),
            []);

        Assert.Equal(ScarabWcid, Assert.Single(desires).WeenieClassId);
    }

    [Fact]
    public void BuildDesiresOrdersByCategoryThenName()
    {
        IReadOnlyList<ComponentFillDesire> desires = VendorComponentFill.BuildDesires(
            new Dictionary<uint, uint>
            {
                [TaperWcid] = 1u,
                [PeaWcid] = 1u,
                [ScarabWcid] = 1u,
                [0x00000999u] = 1u,
            },
            PackWith(),
            PlayerGuid,
            Catalog(
                (TaperWcid, TaperCategory, "Prismatic Taper"),
                (PeaWcid, PeaCategory, "Pea"),
                (ScarabWcid, ScarabCategory, "Lead Scarab"),
                (0x00000999u, ScarabCategory, "Amber Scarab")),
            []);

        Assert.Equal(
            ["Amber Scarab", "Lead Scarab", "Prismatic Taper", "Pea"],
            desires.Select(desire => desire.Name));
    }

    /// <summary>
    /// A component the catalog does not know still gets a name from the
    /// shop listing, so the shortfall report never reads as a blank.
    /// </summary>
    [Fact]
    public void BuildDesiresNamesAnUncatalogedComponentFromTheShopListing()
    {
        IReadOnlyList<ComponentFillDesire> desires = VendorComponentFill.BuildDesires(
            new Dictionary<uint, uint> { [PeaWcid] = 4u },
            PackWith(),
            PlayerGuid,
            Catalog(),
            [Stock(PeaGuid, PeaWcid, VendorComponentFill.UnlimitedStock, 5) with
            {
                Name = "Pea",
            }]);

        ComponentFillDesire desire = Assert.Single(desires);
        Assert.Equal("Pea", desire.Name);
        Assert.Equal(VendorComponentFill.AnyCategory, desire.Category);
    }

    [Fact]
    public void BuildDesiresLeavesAComponentNothingCanNameNameless()
    {
        IReadOnlyList<ComponentFillDesire> desires = VendorComponentFill.BuildDesires(
            new Dictionary<uint, uint> { [PeaWcid] = 4u },
            PackWith(),
            PlayerGuid,
            Catalog(),
            []);

        Assert.Equal(string.Empty, Assert.Single(desires).Name);
    }
}
