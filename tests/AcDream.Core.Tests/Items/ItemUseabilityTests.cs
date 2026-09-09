using AcDream.Core.Items;

namespace AcDream.Core.Tests.Items;

public sealed class ItemUseabilityTests
{
    [Fact]
    public void TargetedUse_hasHighTargetBits()
    {
        const uint healthKit = 0x000A0008u;

        Assert.True(ItemUseability.IsTargeted(healthKit));
        Assert.True(ItemUseability.AllowsSelfTarget(healthKit));
        Assert.Equal(ItemUseability.Contained, ItemUseability.SourceFlags(healthKit));
        Assert.Equal(ItemUseability.Self | ItemUseability.Contained, ItemUseability.TargetFlags(healthKit));
    }

    [Fact]
    public void DirectContainedUse_isNotTargeted()
    {
        Assert.False(ItemUseability.IsTargeted(ItemUseability.Contained));
        Assert.True(ItemUseability.IsDirectUseable(ItemUseability.Contained));
    }

    [Fact]
    public void UseableNo_isNotDirectUseable()
    {
        Assert.False(ItemUseability.IsDirectUseable(ItemUseability.No));
    }

    [Theory]
    [InlineData(ItemUseability.Undef, true)]
    [InlineData(ItemUseability.No, false)]
    [InlineData(ItemUseability.Contained, true)]
    [InlineData(ItemUseability.NeverWalk, true)]
    [InlineData(ItemUseability.No | ItemUseability.Contained, false)]
    public void IsUseable_matchesRetailLowNoBit(uint useability, bool expected)
    {
        Assert.Equal(expected, ItemUseability.IsUseable(useability));
    }

    [Fact]
    public void Undef_isAZeroValuedDirectUse()
    {
        Assert.True(ItemUseability.IsDirectUseable(ItemUseability.Undef));
    }

    [Fact]
    public void LeastLimitedSourceUse_matchesRetailPriorityAndIgnoresObjSelf()
    {
        Assert.Equal(ItemUseability.Remote,
            ItemUseability.LeastLimitedSourceUse(
                ItemUseability.Remote | ItemUseability.Contained | ItemUseability.Wielded));
        Assert.Equal(ItemUseability.Wielded,
            ItemUseability.LeastLimitedSourceUse(ItemUseability.Wielded | ItemUseability.Self));
        Assert.Equal(ItemUseability.Undef,
            ItemUseability.LeastLimitedSourceUse(ItemUseability.ObjSelf));
    }
}
