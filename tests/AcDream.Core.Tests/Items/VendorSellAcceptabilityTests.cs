using AcDream.Core.Items;

namespace AcDream.Core.Tests.Items;

public sealed class VendorSellAcceptabilityTests
{
    private const uint Armor = (uint)ItemType.Armor;
    private const uint Weapon = (uint)ItemType.Weapon;
    private const uint NoLimit = VendorSellAcceptability.NoLimit;

    [Fact]
    public void NotOwnedByPlayerIsRejectedBeforeAnyOtherCheck()
    {
        VendorSellRejection rejection = VendorSellAcceptability.Evaluate(
            ownedByPlayer: false,
            containedItemCount: 0,
            itemTypeMask: Armor,
            perUnitValue: 100,
            merchandiseItemTypes: Armor,
            merchandiseMinValue: 0u,
            merchandiseMaxValue: NoLimit);

        Assert.Equal(VendorSellRejection.NotOwnedByPlayer, rejection);
    }

    [Fact]
    public void ANonEmptyContainerBypassesTheTypeAndValueFilterEntirely()
    {
        VendorSellRejection rejection = VendorSellAcceptability.Evaluate(
            ownedByPlayer: true,
            containedItemCount: 3,
            itemTypeMask: Weapon,
            perUnitValue: 0,
            merchandiseItemTypes: Armor,
            merchandiseMinValue: 0u,
            merchandiseMaxValue: NoLimit);

        Assert.Equal(VendorSellRejection.None, rejection);
    }

    [Fact]
    public void AWrongItemTypeIsRejected()
    {
        VendorSellRejection rejection = VendorSellAcceptability.Evaluate(
            ownedByPlayer: true,
            containedItemCount: 0,
            itemTypeMask: Weapon,
            perUnitValue: 100,
            merchandiseItemTypes: Armor,
            merchandiseMinValue: 0u,
            merchandiseMaxValue: NoLimit);

        Assert.Equal(VendorSellRejection.WrongType, rejection);
    }

    [Fact]
    public void ZeroPerUnitValueIsRejectedAsNoValue()
    {
        VendorSellRejection rejection = VendorSellAcceptability.Evaluate(
            ownedByPlayer: true,
            containedItemCount: 0,
            itemTypeMask: Armor,
            perUnitValue: 0,
            merchandiseItemTypes: Armor,
            merchandiseMinValue: 0u,
            merchandiseMaxValue: NoLimit);

        Assert.Equal(VendorSellRejection.NoValue, rejection);
    }

    [Fact]
    public void AboveTheAuthoredMaxValueIsRejectedAsTooValuable()
    {
        VendorSellRejection rejection = VendorSellAcceptability.Evaluate(
            ownedByPlayer: true,
            containedItemCount: 0,
            itemTypeMask: Armor,
            perUnitValue: 1001,
            merchandiseItemTypes: Armor,
            merchandiseMinValue: 0u,
            merchandiseMaxValue: 1000u);

        Assert.Equal(VendorSellRejection.TooValuable, rejection);
    }

    [Fact]
    public void ExactlyAtTheAuthoredMaxValueIsAcceptable()
    {
        VendorSellRejection rejection = VendorSellAcceptability.Evaluate(
            ownedByPlayer: true,
            containedItemCount: 0,
            itemTypeMask: Armor,
            perUnitValue: 1000,
            merchandiseItemTypes: Armor,
            merchandiseMinValue: 0u,
            merchandiseMaxValue: 1000u);

        Assert.Equal(VendorSellRejection.None, rejection);
    }

    [Fact]
    public void BelowTheAuthoredMinValueIsRejectedAsTooCheap()
    {
        VendorSellRejection rejection = VendorSellAcceptability.Evaluate(
            ownedByPlayer: true,
            containedItemCount: 0,
            itemTypeMask: Armor,
            perUnitValue: 4,
            merchandiseItemTypes: Armor,
            merchandiseMinValue: 5u,
            merchandiseMaxValue: NoLimit);

        Assert.Equal(VendorSellRejection.TooCheap, rejection);
    }

    [Fact]
    public void NoLimitSentinelDisablesBothMaxAndMinChecks()
    {
        VendorSellRejection rejection = VendorSellAcceptability.Evaluate(
            ownedByPlayer: true,
            containedItemCount: 0,
            itemTypeMask: Armor,
            perUnitValue: int.MaxValue - 1,
            merchandiseItemTypes: Armor,
            merchandiseMinValue: NoLimit,
            merchandiseMaxValue: NoLimit);

        Assert.Equal(VendorSellRejection.None, rejection);
    }

    [Fact]
    public void PromissoryNoteAboveMaxValueIsExemptFromTooValuable()
    {
        VendorSellRejection rejection = VendorSellAcceptability.Evaluate(
            ownedByPlayer: true,
            containedItemCount: 0,
            itemTypeMask: (uint)ItemType.PromissoryNote,
            perUnitValue: 999_999,
            merchandiseItemTypes: (uint)ItemType.PromissoryNote,
            merchandiseMinValue: 0u,
            merchandiseMaxValue: 1000u);

        Assert.Equal(VendorSellRejection.None, rejection);
    }

    /// <summary>An ordinary (non-note) item above max value is still rejected — the exemption is note-specific.</summary>
    [Fact]
    public void NonPromissoryNoteAboveMaxValueIsStillTooValuable()
    {
        VendorSellRejection rejection = VendorSellAcceptability.Evaluate(
            ownedByPlayer: true,
            containedItemCount: 0,
            itemTypeMask: Armor,
            perUnitValue: 999_999,
            merchandiseItemTypes: Armor,
            merchandiseMinValue: 0u,
            merchandiseMaxValue: 1000u);

        Assert.Equal(VendorSellRejection.TooValuable, rejection);
    }

    [Fact]
    public void RetainedItemIsRejectedAsWrongTypeEvenWhenTheTypeMaskMatches()
    {
        VendorSellRejection rejection = VendorSellAcceptability.Evaluate(
            ownedByPlayer: true,
            containedItemCount: 0,
            itemTypeMask: Armor,
            perUnitValue: 100,
            merchandiseItemTypes: Armor,
            merchandiseMinValue: 0u,
            merchandiseMaxValue: NoLimit,
            publicWeenieBitfield: (uint)PublicWeenieFlags.Retained);

        Assert.Equal(VendorSellRejection.WrongType, rejection);
    }

    /// <summary>An item with OTHER bitfield bits set (not Retained) is unaffected.</summary>
    [Fact]
    public void NonRetainedBitfieldDoesNotAffectAcceptability()
    {
        VendorSellRejection rejection = VendorSellAcceptability.Evaluate(
            ownedByPlayer: true,
            containedItemCount: 0,
            itemTypeMask: Armor,
            perUnitValue: 100,
            merchandiseItemTypes: Armor,
            merchandiseMinValue: 0u,
            merchandiseMaxValue: NoLimit,
            publicWeenieBitfield: (uint)PublicWeenieFlags.Stuck);

        Assert.Equal(VendorSellRejection.None, rejection);
    }

    [Fact]
    public void AnOrdinaryAcceptableItemReturnsNone()
    {
        VendorSellRejection rejection = VendorSellAcceptability.Evaluate(
            ownedByPlayer: true,
            containedItemCount: 0,
            itemTypeMask: Armor,
            perUnitValue: 500,
            merchandiseItemTypes: Armor,
            merchandiseMinValue: 1u,
            merchandiseMaxValue: 10_000u);

        Assert.Equal(VendorSellRejection.None, rejection);
    }


    [Theory]
    [InlineData(VendorSellRejection.NotOwnedByPlayer, "You can only sell items you are carrying")]
    [InlineData(VendorSellRejection.CannotBeSoldHere, "That item cannot be sold here")]
    [InlineData(VendorSellRejection.NoValue, "That item has no value and cannot be sold")]
    [InlineData(VendorSellRejection.TooCheap, "That item is too cheap to sell here")]
    [InlineData(VendorSellRejection.TooValuable, "That item is too valuable to sell here")]
    [InlineData(VendorSellRejection.WrongType, "You cannot sell that here")]
    public void MessageForReturnsRetailsExactString(VendorSellRejection rejection, string expected)
    {
        Assert.Equal(expected, VendorSellAcceptability.MessageFor(rejection));
    }

    [Fact]
    public void MessageForAcceptableReturnsNull()
    {
        Assert.Null(VendorSellAcceptability.MessageFor(VendorSellRejection.None));
    }
}
