using System.Buffers.Binary;
using System.Collections.Generic;

namespace AcDream.Core.Net.Messages;

public static class VendorApproach
{
    public readonly record struct VendorProfile(
        uint MerchandiseItemTypes,
        uint MerchandiseMinValue,
        uint MerchandiseMaxValue,
        bool DealMagicalItems,
        float BuyPrice,
        float SellPrice,
        uint AlternateCurrencyWcid,
        uint AlternateCurrencyAmount,
        string AlternateCurrencyPluralName);

    public readonly record struct ItemProfile(
        int StackSize,
        uint ItemGuid,
        PublicWeenieDescBody Desc);

    public readonly record struct Parsed(
        uint VendorGuid,
        VendorProfile Profile,
        IReadOnlyList<ItemProfile> Items);

    private const int MaxItems = 8192;

    public static Parsed? TryParse(ReadOnlySpan<byte> payload)
    {
        try
        {
            int pos = 0;

            uint vendorGuid = CreateObject.ReadU32(payload, ref pos);

            uint categories = CreateObject.ReadU32(payload, ref pos);
            uint minValue = CreateObject.ReadU32(payload, ref pos);
            uint maxValue = CreateObject.ReadU32(payload, ref pos);
            bool dealsMagic = CreateObject.ReadU32(payload, ref pos) != 0;

            if (payload.Length - pos < 8) return null;
            float buyPrice = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(pos)); pos += 4;
            float sellPrice = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(pos)); pos += 4;

            uint currencyWcid = CreateObject.ReadU32(payload, ref pos);
            uint currencyAmount = CreateObject.ReadU32(payload, ref pos);
            string currencyName = CreateObject.ReadString16L(payload, ref pos);

            var profile = new VendorProfile(
                categories, minValue, maxValue, dealsMagic,
                buyPrice, sellPrice,
                currencyWcid, currencyAmount, currencyName);

            uint itemCount = CreateObject.ReadU32(payload, ref pos);
            if (itemCount > MaxItems) return null;
            if ((long)itemCount * 12 > payload.Length - pos) return null;

            var items = itemCount == 0
                ? (IReadOnlyList<ItemProfile>)System.Array.Empty<ItemProfile>()
                : new ItemProfile[itemCount];
            for (int i = 0; i < itemCount; i++)
            {
                uint packed = CreateObject.ReadU32(payload, ref pos);
                int stackSize = unchecked((int)(packed << 8)) >> 8;

                uint itemGuid = CreateObject.ReadU32(payload, ref pos);

                var desc = PublicWeenieDescParser.Parse(payload, ref pos);

                CreateObject.AlignTo4(ref pos);

                ((ItemProfile[])items)[i] = new ItemProfile(stackSize, itemGuid, desc);
            }

            return new Parsed(vendorGuid, profile, items);
        }
        catch
        {
            return null;
        }
    }
}
