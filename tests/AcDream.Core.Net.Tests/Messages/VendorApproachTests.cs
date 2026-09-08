using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class VendorApproachTests
{
    [Fact]
    public void TryParse_RepresentativeMultiItemVendor_FieldOrderAndItemsCorrect()
    {
        var w = new AceWireWriter();
        w.Write(0x40000123u)              // vendor guid
         .Write(0x00000042u)              // MerchandiseItemTypes (arbitrary bitmask)
         .Write(10u)                      // MerchandiseMinValue
         .Write(99999u)                   // MerchandiseMaxValue
         .Write(1u)                       // DealMagicalItems = true
         .Write(0.75f)                    // BuyPrice rate
         .Write(1.25f)                    // SellPrice rate
         .Write(0x34000001u)              // AlternateCurrencyWcid
         .Write(57u)                      // AlternateCurrencyAmount
         .WriteString16L("Trade Notes")   // AlternateCurrencyPluralName
         .Write(2u);

        WritePackedItemHeader(w, stackSize: 3, itemGuid: 0x50001001u);
        WriteMinimalPwdBody(w, weenieFlags: 0x00000100u, name: "Dusty Tome",
            weenieClassId: 7u, iconId: 8u, itemType: (uint)ItemType.Writable,
            ammoType: 42);

        // Item 1: unlimited stack (-1), plain body (no optional tail —
        // already 4-aligned on its own). If item 0's trailing align were
        // missing or wrong, this item's packed dword / guid / name would
        // all read as garbage or the parse would throw.
        WritePackedItemHeader(w, stackSize: -1, itemGuid: 0x50001002u);
        WriteMinimalPwdBody(w, weenieFlags: 0u, name: "Iron Key",
            weenieClassId: 55u, iconId: 66u, itemType: (uint)ItemType.Key);

        byte[] payload = w.ToArray();

        var parsed = VendorApproach.TryParse(payload);

        Assert.NotNull(parsed);
        var p = parsed!.Value;

        Assert.Equal(0x40000123u, p.VendorGuid);
        Assert.Equal(0x00000042u, p.Profile.MerchandiseItemTypes);
        Assert.Equal(10u, p.Profile.MerchandiseMinValue);
        Assert.Equal(99999u, p.Profile.MerchandiseMaxValue);
        Assert.True(p.Profile.DealMagicalItems);
        Assert.Equal(0.75f, p.Profile.BuyPrice);
        Assert.Equal(1.25f, p.Profile.SellPrice);
        Assert.Equal(0x34000001u, p.Profile.AlternateCurrencyWcid);
        Assert.Equal(57u, p.Profile.AlternateCurrencyAmount);
        Assert.Equal("Trade Notes", p.Profile.AlternateCurrencyPluralName);

        Assert.Equal(2, p.Items.Count);

        Assert.Equal(3, p.Items[0].StackSize);
        Assert.Equal(0x50001001u, p.Items[0].ItemGuid);
        Assert.Equal("Dusty Tome", p.Items[0].Desc.Name);
        Assert.Equal((ushort)42, p.Items[0].Desc.AmmoType);

        Assert.Equal(-1, p.Items[1].StackSize);
        Assert.Equal(0x50001002u, p.Items[1].ItemGuid);
        Assert.Equal("Iron Key", p.Items[1].Desc.Name);
        Assert.Equal((uint)ItemType.Key, p.Items[1].Desc.ItemType);
    }

    [Fact]
    public void TryParse_EmptyItemList_ReturnsEmptyItemsWithValidProfile()
    {
        var w = new AceWireWriter();
        w.Write(0x40000200u)             // vendor guid
         .Write(0u)                      // MerchandiseItemTypes
         .Write(0u)                      // MerchandiseMinValue
         .Write(0xFFFFFFFFu)
         .Write(0u)                      // DealMagicalItems = false
         .Write(1.0f)                    // BuyPrice
         .Write(1.0f)                    // SellPrice
         .Write(0u)                      // AlternateCurrencyWcid (pyreal vendor)
         .Write(0u)                      // AlternateCurrencyAmount
         .WriteString16L("")             // AlternateCurrencyPluralName (empty for pyreal vendor)
         .Write(0u);

        var parsed = VendorApproach.TryParse(w.ToArray());

        Assert.NotNull(parsed);
        Assert.Equal(0x40000200u, parsed!.Value.VendorGuid);
        Assert.False(parsed.Value.Profile.DealMagicalItems);
        Assert.Equal(0xFFFFFFFFu, parsed.Value.Profile.MerchandiseMaxValue);
        Assert.Empty(parsed.Value.Items);
    }

    [Fact]
    public void TryParse_TruncatedProfileMidField_ReturnsNull()
    {
        var w = new AceWireWriter();
        w.Write(0x40000300u)
         .Write(0u).Write(0u).Write(0u).Write(0u)
         .Write(1.0f).Write(1.0f)                   // buy/sell rate
         .Write(0u).Write(0u)
         .Write((ushort)5);

        Assert.Null(VendorApproach.TryParse(w.ToArray()));
    }

    [Fact]
    public void TryParse_ItemCountExceedsActualBytes_ReturnsNull()
    {
        // itemCount says 2 but only one item's bytes are present. The
        // second item's packed stack-size dword read runs out of buffer —
        // this is OUR OWN per-item framing read (not part of the shared
        // PublicWeenieDescParser's internal swallow), so it must fail the
        // whole parse rather than degrade.
        var w = new AceWireWriter();
        WriteMinimalProfilePrefix(w, vendorGuid: 0x40000400u);
        w.Write(2u);
        WritePackedItemHeader(w, stackSize: 1, itemGuid: 0x50002001u);
        WriteMinimalPwdBody(w, weenieFlags: 0u, name: "Solo Item",
            weenieClassId: 1u, iconId: 1u, itemType: (uint)ItemType.Misc);

        Assert.Null(VendorApproach.TryParse(w.ToArray()));
    }

    [Fact]
    public void TryParse_TruncatedMidItemPrefix_ReturnsNull()
    {
        // The item's packed stack-size dword is present but its guid is
        // cut off entirely. This does NOT reach a guid read that fails --
        // TryParse's own minimum-size guard (`(long)itemCount * 12 >
        // payload.Length - pos`, the smallest possible per-item size:
        // packed(4) + guid(4) + weenieFlags(4)) rejects the whole parse
        // right after itemCount is read (remaining=4 bytes here, the packed
        // dword only; 1 * 12 = 12 > 4), before the per-item loop that would
        // read the packed dword/guid ever runs.
        var w = new AceWireWriter();
        WriteMinimalProfilePrefix(w, vendorGuid: 0x40000500u);
        w.Write(1u);
        w.Write(0xFF000001u); // packed dword (stackSize=1) written; guid is NOT written

        Assert.Null(VendorApproach.TryParse(w.ToArray()));
    }

    [Fact]
    public void TryParse_TruncatedMidItemPwdTail_DegradesGracefully()
    {
        var w = new AceWireWriter();
        WriteMinimalProfilePrefix(w, vendorGuid: 0x40000600u);
        w.Write(1u);
        WritePackedItemHeader(w, stackSize: 4, itemGuid: 0x50002100u);

        w.Write(0x00000018u)              // weenieFlags: Value | Useability
         .WriteString16L("Cut Short")
         .WritePackedDword(9u)            // weenieClassId
         .WritePackedDword(10u)           // iconId
         .Write((uint)ItemType.Misc)      // itemType
         .Write(0u)                       // objectDescriptionFlags
         .Align();
        w.Write(777u);                    // Value — this must survive the cut
        int truncateAt = w.Length;
        w.Write(1u);                      // Useability — this must NOT survive the cut

        byte[] payload = w.ToArray();
        byte[] truncated = payload[..truncateAt];

        var parsed = VendorApproach.TryParse(truncated);

        Assert.NotNull(parsed);
        var item = Assert.Single(parsed!.Value.Items);
        Assert.Equal("Cut Short", item.Desc.Name);
        Assert.Equal(777, item.Desc.Value);
        Assert.Null(item.Desc.Useability);
    }

    // ---- shared fixture helpers -------------------------------------------

    private static void WriteMinimalProfilePrefix(AceWireWriter w, uint vendorGuid)
    {
        w.Write(vendorGuid)
         .Write(0u).Write(0u).Write(0xFFFFFFFFu)
         .Write(0u)
         .Write(1.0f).Write(1.0f)
         .Write(0u).Write(0u)
         .WriteString16L("");
    }

    private static void WritePackedItemHeader(AceWireWriter w, int stackSize, uint itemGuid)
    {
        uint packed = ((uint)stackSize & 0xFFFFFFu) | 0xFF000000u;
        w.Write(packed).Write(itemGuid);
    }

    private static void WriteMinimalPwdBody(
        AceWireWriter w, uint weenieFlags, string name, uint weenieClassId,
        uint iconId, uint itemType, ushort? ammoType = null)
    {
        w.Write(weenieFlags)
         .WriteString16L(name)
         .WritePackedDword(weenieClassId)
         .WritePackedDword(iconId)
         .Write(itemType)
         .Write(0u)          // objectDescriptionFlags
         .Align();

        if ((weenieFlags & 0x00000100u) != 0)   // AmmoType u16
            w.Write(ammoType ?? (ushort)0);

        w.Align();
    }
}
