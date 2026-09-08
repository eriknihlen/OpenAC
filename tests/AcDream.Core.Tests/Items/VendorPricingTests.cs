using AcDream.Core.Items;

namespace AcDream.Core.Tests.Items;

public sealed class VendorPricingTests
{
    [Fact]
    public void RateOne_WholeNumberValue_PassesThroughUnchanged()
    {
        Assert.Equal(100, VendorPricing.BuyPrice(100, (uint)ItemType.Misc, 1.0f, 1));
        Assert.Equal(100, VendorPricing.SellPrice(100, (uint)ItemType.Misc, 1.0f, 1));
    }

    [Fact]
    public void FractionalRate_WithStackQuantity_RoundsPerDirection()
    {
        Assert.Equal(55, VendorPricing.BuyPrice(37, (uint)ItemType.Misc, 0.75f, 2));
        Assert.Equal(56, VendorPricing.SellPrice(37, (uint)ItemType.Misc, 0.75f, 2));
    }

    [Fact]
    public void ZeroValue_ClampsToMinimumOne()
    {
        Assert.Equal(1, VendorPricing.BuyPrice(0, (uint)ItemType.Misc, 1.0f, 1));
        Assert.Equal(1, VendorPricing.SellPrice(0, (uint)ItemType.Misc, 1.0f, 1));
    }

    [Fact]
    public void HalfwayRawValue_BuyRoundsDownSellRoundsUp()
    {
        Assert.Equal(20, VendorPricing.BuyPrice(41, (uint)ItemType.Misc, 0.5f, 1));
        Assert.Equal(21, VendorPricing.SellPrice(41, (uint)ItemType.Misc, 0.5f, 1));
    }

    [Fact]
    public void LargeValueWithStackMultiplier_ComputesExactly()
    {
        Assert.Equal(12500, VendorPricing.BuyPrice(1000, (uint)ItemType.Misc, 2.5f, 5));
        Assert.Equal(12500, VendorPricing.SellPrice(1000, (uint)ItemType.Misc, 2.5f, 5));
    }

    [Fact]
    public void PromissoryNote_IgnoresSuppliedRate_UsesHardcodedOverride()
    {
        const uint promissoryNote = (uint)ItemType.PromissoryNote;
        Assert.Equal(100, VendorPricing.BuyPrice(100, promissoryNote, buyRate: 3.0f, quantity: 1));
        Assert.Equal(115, VendorPricing.SellPrice(100, promissoryNote, sellRate: 3.0f, quantity: 1));
    }

    [Fact]
    public void SyntheticNegativeValue_ReturnsRetailSentinelNotClampedToOne()
    {
        Assert.Equal(-1, VendorPricing.BuyPrice(-50, (uint)ItemType.Misc, 1.0f, 1));
        Assert.Equal(-1, VendorPricing.SellPrice(-50, (uint)ItemType.Misc, 1.0f, 1));
    }


    [Fact]
    public void StackOf50Arrows_DividesToThePerArrowValue()
    {
        Assert.Equal(10, VendorPricing.PerUnitValue(500, descStackSize: 50));
    }

    [Fact]
    public void DescStackSizeZeroOrNegative_ReturnsValueUnchanged()
    {
        Assert.Equal(250, VendorPricing.PerUnitValue(250, descStackSize: 0));
        Assert.Equal(250, VendorPricing.PerUnitValue(250, descStackSize: -1));
    }

    [Fact]
    public void DescStackSizeAbsent_ReturnsValueUnchanged()
    {
        Assert.Equal(250, VendorPricing.PerUnitValue(250, descStackSize: null));
    }

    [Fact]
    public void NonExactDivision_TruncatesTowardZero()
    {
        Assert.Equal(33, VendorPricing.PerUnitValue(100, descStackSize: 3));
    }
}
