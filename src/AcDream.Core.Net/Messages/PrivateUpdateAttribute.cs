using System;
using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class PrivateUpdateAttribute
{
    public const uint Opcode = 0x02E3u;

    /// <summary>Parsed attribute update.</summary>
    public readonly record struct Parsed(
        byte Sequence,
        uint AttributeId,
        uint Ranks,
        uint Start,
        uint Xp);

    /// <summary>
    /// Parse a raw <c>PrivateUpdateAttribute (0x02E3)</c> body. Returns
    /// <c>null</c> on opcode mismatch or truncation.
    /// </summary>
    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        // 4 (opcode) + 1 (seq) + 4 * 4 (uints) = 21 bytes minimum.
        if (body.Length < 21) return null;
        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (opcode != Opcode) return null;

        int pos = 4;
        byte seq   = body[pos]; pos += 1;
        uint attr  = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        uint ranks = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        uint start = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        uint xp    = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
        return new Parsed(seq, attr, ranks, start, xp);
    }
}
