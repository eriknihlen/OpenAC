using System;
using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class VendorRequestsTests
{
    [Fact]
    public void BuildBuy_SingleItem_WritesEnvelopeSequenceOpcodeVendorCountAndItem()
    {
        byte[] body = VendorRequests.BuildBuy(
            gameActionSequence: 9,
            vendorGuid: 0x40001000u,
            amount: 1,
            itemGuid: 0x50002000u,
            alternateCurrencyId: 0u);

        Assert.Equal(32, body.Length);
        Assert.Equal(VendorRequests.GameActionEnvelope,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0)));
        Assert.Equal(9u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(VendorRequests.BuyOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0x005Fu, VendorRequests.BuyOpcode);
        Assert.Equal(0x40001000u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(1u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
        Assert.Equal(1,
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(20)));
        Assert.Equal(0x50002000u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(24)));
        Assert.Equal(0u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(28)));
    }

    [Fact]
    public void BuildBuy_StackedItem_WritesTheSplitSliderQuantityAsAPlainPositiveAmount()
    {
        byte[] body = VendorRequests.BuildBuy(
            gameActionSequence: 1,
            vendorGuid: 0x40001000u,
            amount: 25,
            itemGuid: 0x50002001u,
            alternateCurrencyId: 0u);

        Assert.Equal(25,
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(20)));
    }

    [Fact]
    public void BuildBuy_AlternateCurrencyVendor_WritesTheVendorsTradeWcidTrailing()
    {
        byte[] body = VendorRequests.BuildBuy(
            gameActionSequence: 1,
            vendorGuid: 0x40001000u,
            amount: 1,
            itemGuid: 0x50002000u,
            alternateCurrencyId: 0x12345678u);

        Assert.Equal(0x12345678u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(28)));
    }

    [Fact]
    public void BuildBuy_ListOverload_MultipleItems_WritesEachAmountGuidPairInOrder()
    {
        byte[] body = VendorRequests.BuildBuy(
            gameActionSequence: 4,
            vendorGuid: 0x40001000u,
            items: new (int Amount, uint ItemGuid)[]
            {
                (1, 0x50002000u),
                (10, 0x50002001u),
            },
            alternateCurrencyId: 0u);

        // 24 + 2*8 = 40.
        Assert.Equal(40, body.Length);
        Assert.Equal(2u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
        Assert.Equal(1,
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(20)));
        Assert.Equal(0x50002000u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(24)));
        Assert.Equal(10,
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(28)));
        Assert.Equal(0x50002001u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(32)));
        Assert.Equal(0u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(36)));
    }

    [Fact]
    public void BuildBuy_SingleItemOverload_MatchesTheGeneralListOverload()
    {
        byte[] viaSingle = VendorRequests.BuildBuy(
            gameActionSequence: 3,
            vendorGuid: 0x40001000u,
            amount: 5,
            itemGuid: 0x50002000u,
            alternateCurrencyId: 7u);
        byte[] viaList = VendorRequests.BuildBuy(
            gameActionSequence: 3,
            vendorGuid: 0x40001000u,
            items: new (int Amount, uint ItemGuid)[] { (5, 0x50002000u) },
            alternateCurrencyId: 7u);

        Assert.Equal(viaList, viaSingle);
    }


    [Fact]
    public void BuildSell_SingleItem_WritesEnvelopeSequenceOpcodeVendorCountAndItemWithNoTrailer()
    {
        byte[] body = VendorRequests.BuildSell(
            gameActionSequence: 9,
            vendorGuid: 0x40001000u,
            items: new (int Amount, uint ItemGuid)[] { (1, 0x50002000u) });

        // envelope(4) + seq(4) + opcode(4) + vendorGuid(4) + itemCount(4)
        // + 1*(amount(4)+guid(4)) = 28. Note: 4 bytes SHORTER than the
        // equivalent single-item Buy payload (32) — Sell has no trailing
        // alternateCurrencyId.
        Assert.Equal(28, body.Length);
        Assert.Equal(VendorRequests.GameActionEnvelope,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0)));
        Assert.Equal(9u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(VendorRequests.SellOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0x0060u, VendorRequests.SellOpcode);
        Assert.Equal(0x40001000u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(1u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
        Assert.Equal(1,
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(20)));
        Assert.Equal(0x50002000u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(24)));
    }

    [Fact]
    public void BuildSell_MultipleItems_WritesEachAmountGuidPairInOrderWithNoTrailer()
    {
        byte[] body = VendorRequests.BuildSell(
            gameActionSequence: 4,
            vendorGuid: 0x40001000u,
            items: new (int Amount, uint ItemGuid)[]
            {
                (1, 0x50002000u),
                (10, 0x50002001u),
            });

        // 20 + 2*8 = 36.
        Assert.Equal(36, body.Length);
        Assert.Equal(2u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
        Assert.Equal(1,
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(20)));
        Assert.Equal(0x50002000u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(24)));
        Assert.Equal(10,
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(28)));
        Assert.Equal(0x50002001u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(32)));
    }

    [Fact]
    public void BuildSell_EmptyList_WritesAZeroCountAndNoItemPairs()
    {
        byte[] body = VendorRequests.BuildSell(
            gameActionSequence: 1,
            vendorGuid: 0x40001000u,
            items: Array.Empty<(int Amount, uint ItemGuid)>());

        Assert.Equal(20, body.Length);
        Assert.Equal(0u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
    }
}
