using System;
using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class PrivateUpdateVital
{
    public const uint FullOpcode    = 0x02E7u;
    public const uint CurrentOpcode = 0x02E9u;

    /// <summary>Parsed full-update message.</summary>
    public readonly record struct ParsedFull(
        byte Sequence,
        uint VitalId,
        uint Ranks,
        uint Start,
        uint Xp,
        uint Current);

    public readonly record struct ParsedCurrent(
        byte Sequence,
        uint VitalId,
        uint Current);

    /// <summary>
    /// Parse a raw <c>PrivateUpdateVital (0x02E7)</c> body. Returns
    /// <c>null</c> if opcode mismatch or truncated.
    /// </summary>
    public static ParsedFull? TryParseFull(ReadOnlySpan<byte> body)
    {
        // 4 (opcode) + 1 (seq) + 5 * 4 (uints) = 25 bytes minimum.
        if (body.Length < 25) return null;
        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (opcode != FullOpcode) return null;

        int pos = 4;
        byte seq      = body[pos]; pos += 1;
        uint vital    = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        uint ranks    = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        uint start    = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        uint xp       = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        uint current  = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
        return new ParsedFull(seq, vital, ranks, start, xp, current);
    }

    /// <summary>
    /// Parse a raw <c>PrivateUpdateVitalCurrent (0x02E9)</c> body. Returns
    /// <c>null</c> if opcode mismatch or truncated.
    /// </summary>
    public static ParsedCurrent? TryParseCurrent(ReadOnlySpan<byte> body)
    {
        // 4 (opcode) + 1 (seq) + 2 * 4 (uints) = 13 bytes minimum.
        if (body.Length < 13) return null;
        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (opcode != CurrentOpcode) return null;

        int pos = 4;
        byte seq     = body[pos]; pos += 1;
        uint vital   = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        uint current = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
        return new ParsedCurrent(seq, vital, current);
    }
}
