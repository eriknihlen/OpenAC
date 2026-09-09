using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class TradeRequests
{
    public const uint GameActionEnvelope = 0xF7B1u;
    public const uint OpenTradeNegotiationsOpcode = 0x01F6u;
    public const uint CloseTradeNegotiationsOpcode = 0x01F7u;
    public const uint AddToTradeOpcode = 0x01F8u;
    public const uint AcceptTradeOpcode = 0x01FAu;
    public const uint DeclineTradeOpcode = 0x01FBu;
    public const uint ResetTradeOpcode = 0x0204u;

    public static byte[] BuildOpenTradeNegotiations(
        uint gameActionSequence, uint partnerGuid)
    {
        byte[] body = new byte[16];
        WriteHeader(body, gameActionSequence, OpenTradeNegotiationsOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), partnerGuid);
        return body;
    }

    public static byte[] BuildCloseTradeNegotiations(uint gameActionSequence)
        => BuildEmpty(gameActionSequence, CloseTradeNegotiationsOpcode);

    public static byte[] BuildAddToTrade(
        uint gameActionSequence, uint itemGuid, uint tradeSlot = 0u)
    {
        byte[] body = new byte[20];
        WriteHeader(body, gameActionSequence, AddToTradeOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), itemGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), tradeSlot);
        return body;
    }

    public static byte[] BuildAcceptTrade(
        uint gameActionSequence,
        uint partnerGuid,
        double tradeStamp,
        uint tradeStatus,
        uint initiatorGuid,
        bool initiatorAccepts,
        bool partnerAccepts)
    {
        byte[] body = new byte[48];
        WriteHeader(body, gameActionSequence, AcceptTradeOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), partnerGuid);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(16), tradeStamp);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), tradeStatus);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(28), initiatorGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(32), initiatorAccepts ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(36), partnerAccepts ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(40), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(44), 0u);
        return body;
    }

    public static byte[] BuildDeclineTrade(uint gameActionSequence)
        => BuildEmpty(gameActionSequence, DeclineTradeOpcode);

    public static byte[] BuildResetTrade(uint gameActionSequence)
        => BuildEmpty(gameActionSequence, ResetTradeOpcode);

    private static byte[] BuildEmpty(uint gameActionSequence, uint opcode)
    {
        byte[] body = new byte[12];
        WriteHeader(body, gameActionSequence, opcode);
        return body;
    }

    private static void WriteHeader(byte[] body, uint sequence, uint opcode)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(body, GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), opcode);
    }
}
