using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace AcDream.Core.Net.Messages;

public static class VendorRequests
{
    public const uint GameActionEnvelope = 0xF7B1u;
    public const uint BuyOpcode = 0x005Fu;
    public const uint SellOpcode = 0x0060u;

    public static byte[] BuildBuy(
        uint gameActionSequence,
        uint vendorGuid,
        IReadOnlyList<(int Amount, uint ItemGuid)> items,
        uint alternateCurrencyId)
    {
        ArgumentNullException.ThrowIfNull(items);

        int itemCount = items.Count;
        byte[] body = new byte[24 + (itemCount * 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  gameActionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  BuyOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), vendorGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), (uint)itemCount);

        int offset = 20;
        for (int i = 0; i < itemCount; i++)
        {
            (int amount, uint itemGuid) = items[i];
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset), amount);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset + 4), itemGuid);
            offset += 8;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), alternateCurrencyId);
        return body;
    }

    public static byte[] BuildBuy(
        uint gameActionSequence,
        uint vendorGuid,
        int amount,
        uint itemGuid,
        uint alternateCurrencyId)
        => BuildBuy(
            gameActionSequence,
            vendorGuid,
            new[] { (amount, itemGuid) },
            alternateCurrencyId);

    public static byte[] BuildSell(
        uint gameActionSequence,
        uint vendorGuid,
        IReadOnlyList<(int Amount, uint ItemGuid)> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        int itemCount = items.Count;
        byte[] body = new byte[20 + (itemCount * 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  gameActionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  SellOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), vendorGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), (uint)itemCount);

        int offset = 20;
        for (int i = 0; i < itemCount; i++)
        {
            (int amount, uint itemGuid) = items[i];
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset), amount);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset + 4), itemGuid);
            offset += 8;
        }

        return body;
    }
}
