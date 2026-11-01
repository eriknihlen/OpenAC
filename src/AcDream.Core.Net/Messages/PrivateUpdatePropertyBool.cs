using System;
using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

/// <summary>
/// 0x02D1 — one true/false property changed on the player. The value travels
/// as a 32-bit word; anything but zero is true.
/// </summary>
public static class PrivateUpdatePropertyBool
{
    public const uint Opcode = 0x02D1u;

    public readonly record struct Parsed(uint Property, bool Value);

    /// <summary>Parse a raw 0x02D1 body. Returns null on opcode mismatch / truncation.</summary>
    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 13) return null;          // 4 + 1 + 4 + 4
        if (BinaryPrimitives.ReadUInt32LittleEndian(body) != Opcode) return null;
        int pos = 4;
        pos += 1;                                   // sequence byte (not honored)
        uint prop = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
        return new Parsed(prop, value != 0u);
    }
}
