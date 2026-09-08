namespace AcDream.Core.Items;

public static class VendorSplitPolicy
{
    public const uint SplitExemptMask = 0x0DC41CB0u;

    public static bool IsSplitExempt(ItemType itemType) =>
        ((uint)itemType & SplitExemptMask) != 0u;

    public static int SeedQuantity(ItemType itemType, int? authoredStackSize) =>
        IsSplitExempt(itemType)
            ? 1
            : authoredStackSize is { } size && size > 0 ? size : 1;

    public static int ResolveAuthoredStackSize(int? descStackSize, int? maxStackSize) =>
        maxStackSize is { } max && max > 0
            ? max
            : descStackSize is { } desc && desc > 0
                ? desc
                : 1;
}
