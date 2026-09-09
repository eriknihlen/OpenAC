using System;

namespace AcDream.Core.Items;

public static class VendorPricing
{
    public static int PerUnitValue(int stackTotalValue, int? descStackSize) =>
        descStackSize is { } size && size > 0
            ? stackTotalValue / size
            : stackTotalValue;

    public static int BuyPrice(int perUnitValue, uint itemType, float buyRate, int quantity)
    {
        float rate = itemType == (uint)ItemType.PromissoryNote ? 1f : buyRate;

        double raw = (double)rate * perUnitValue * quantity;
        int floored = (int)Math.Floor(raw + 0.1);

        if (floored == 0) return 1;
        if (floored >= 0) return floored;
        return -1;
    }

    public static int SellPrice(int perUnitValue, uint itemType, float sellRate, int quantity)
    {
        float rate = itemType == (uint)ItemType.PromissoryNote ? 1.15f : sellRate;

        double raw = (double)rate * perUnitValue * quantity;
        int ceiled = (int)Math.Ceiling(raw - 0.1);

        if (ceiled == 0) return 1;
        if (ceiled > 0) return ceiled;
        return -1;
    }
}
