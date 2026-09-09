using System;
using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class TradeRequestsTests
{
    [Fact]
    public void BuildOpenTradeNegotiations_WritesEnvelopeSequenceOpcodePartner()
    {
        byte[] body = TradeRequests.BuildOpenTradeNegotiations(7, 0x50001234u);

        Assert.Equal(16, body.Length);
        Assert.Equal(TradeRequests.GameActionEnvelope,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(0x01F6u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0x50001234u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }

    [Theory]
    [InlineData(0x01F7u)]
    [InlineData(0x01FBu)] // DeclineTrade
    [InlineData(0x0204u)] // ResetTrade
    public void EmptyBodiedActions_WriteEnvelopeSequenceOpcodeOnly(uint opcode)
    {
        byte[] body = opcode switch
        {
            0x01F7u => TradeRequests.BuildCloseTradeNegotiations(3),
            0x01FBu => TradeRequests.BuildDeclineTrade(3),
            _ => TradeRequests.BuildResetTrade(3),
        };

        Assert.Equal(12, body.Length);
        Assert.Equal(TradeRequests.GameActionEnvelope,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(opcode, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
    }

    [Fact]
    public void BuildAddToTrade_WritesItemAndSlot()
    {
        byte[] body = TradeRequests.BuildAddToTrade(5, 0x60000001u, 2u);

        Assert.Equal(20, body.Length);
        Assert.Equal(0x01F8u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0x60000001u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
    }

    [Fact]
    public void BuildAcceptTrade_WritesTradePackFixedFieldsAndTwoEmptyLists()
    {
        byte[] body = TradeRequests.BuildAcceptTrade(
            gameActionSequence: 11,
            partnerGuid: 0x50000B0Bu,
            tradeStamp: 0d,
            tradeStatus: 0u,
            initiatorGuid: 0x5000A0A0u,
            initiatorAccepts: true,
            partnerAccepts: false);

        Assert.Equal(48, body.Length);
        Assert.Equal(0x01FAu, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0x50000B0Bu, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(0d, BinaryPrimitives.ReadDoubleLittleEndian(body.AsSpan(16)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(24)));
        Assert.Equal(0x5000A0A0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(28)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(32)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(36)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(40)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(44)));
    }

    // ── Inbound parsers ─────────────────────────────────────────────────────

    [Fact]
    public void ParseRegisterTrade_ReadsInitiatorPartnerStamp()
    {
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x50000001u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0x50000002u);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), 0uL);

        var parsed = GameEvents.ParseRegisterTrade(payload);
        Assert.NotNull(parsed);
        Assert.Equal(0x50000001u, parsed!.Value.Initiator);
        Assert.Equal(0x50000002u, parsed.Value.Partner);
        Assert.Equal(0uL, parsed.Value.Stamp);
        Assert.Null(GameEvents.ParseRegisterTrade(payload.AsSpan(0, 12)));
    }

    [Fact]
    public void ParseAddToTrade_ReadsGuidSideSlot()
    {
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x60000009u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 2u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 0u);

        var parsed = GameEvents.ParseAddToTrade(payload);
        Assert.NotNull(parsed);
        Assert.Equal(0x60000009u, parsed!.Value.ItemGuid);
        Assert.Equal(2u, parsed.Value.Side);
        Assert.Equal(0u, parsed.Value.SlotIndex);
        Assert.Null(GameEvents.ParseAddToTrade(payload.AsSpan(0, 8)));
    }

    [Fact]
    public void ParseTradeFailure_ReadsGuidAndReason()
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x60000042u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0x426u);

        var parsed = GameEvents.ParseTradeFailure(payload);
        Assert.NotNull(parsed);
        Assert.Equal(0x60000042u, parsed!.Value.ItemGuid);
        Assert.Equal(0x426u, parsed.Value.Reason);
        Assert.Null(GameEvents.ParseTradeFailure(payload.AsSpan(0, 4)));
    }

    [Fact]
    public void SingleGuidParsers_ReadTheGuid()
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x50000C0Cu);

        Assert.Equal(0x50000C0Cu, GameEvents.ParseAcceptTrade(payload));
        Assert.Equal(0x50000C0Cu, GameEvents.ParseDeclineTrade(payload));
        Assert.Equal(0x50000C0Cu, GameEvents.ParseResetTrade(payload));
        Assert.Equal(0x50000C0Cu, GameEvents.ParseCloseTrade(payload));
    }
}
