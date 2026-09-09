namespace AcDream.Core.Items;

public enum VendorSellRejection
{
    /// <summary>Acceptable — stage the drop.</summary>
    None = 0,

    NotOwnedByPlayer,

    WrongType,

    CannotBeSoldHere,

    /// <summary><c>InqAcceptability</c> == 2: per-unit value is exactly zero (<c>pc:005d1ac3</c>).</summary>
    NoValue,

    TooValuable,

    /// <summary>
    /// <c>InqAcceptability</c> == 3: <c>min_value != -1 &amp;&amp; value &lt; min_value</c>
    /// (<c>pc:005d1af2</c>).
    /// </summary>
    TooCheap,
}

public static class VendorSellAcceptability
{
    public const uint NoLimit = uint.MaxValue;

    public static VendorSellRejection Evaluate(
        bool ownedByPlayer,
        int containedItemCount,
        uint itemTypeMask,
        int perUnitValue,
        uint merchandiseItemTypes,
        uint merchandiseMinValue,
        uint merchandiseMaxValue,
        uint publicWeenieBitfield = 0u)
    {
        if (!ownedByPlayer)
            return VendorSellRejection.NotOwnedByPlayer;
        if (containedItemCount > 0)
            return VendorSellRejection.None;

        bool retained = (publicWeenieBitfield & (uint)PublicWeenieFlags.Retained) != 0u;
        if ((itemTypeMask & merchandiseItemTypes) == 0u || retained)
            return VendorSellRejection.WrongType;

        if (perUnitValue == 0)
            return VendorSellRejection.NoValue;

        if (merchandiseMaxValue != NoLimit && perUnitValue > merchandiseMaxValue)
        {
            return (itemTypeMask & (uint)ItemType.PromissoryNote) != 0u
                ? VendorSellRejection.None
                : VendorSellRejection.TooValuable;
        }

        if (merchandiseMinValue != NoLimit && perUnitValue < merchandiseMinValue)
            return VendorSellRejection.TooCheap;

        return VendorSellRejection.None;
    }

    public static string? MessageFor(VendorSellRejection rejection) => rejection switch
    {
        VendorSellRejection.None => null,
        VendorSellRejection.NotOwnedByPlayer => "You can only sell items you are carrying",
        VendorSellRejection.CannotBeSoldHere => "That item cannot be sold here",
        VendorSellRejection.NoValue => "That item has no value and cannot be sold",
        VendorSellRejection.TooCheap => "That item is too cheap to sell here",
        VendorSellRejection.TooValuable => "That item is too valuable to sell here",
        VendorSellRejection.WrongType => "You cannot sell that here",
        _ => "You cannot sell that here",
    };
}
