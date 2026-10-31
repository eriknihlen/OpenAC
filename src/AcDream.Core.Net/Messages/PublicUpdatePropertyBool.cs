using System;
using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

/// <summary>
/// 0x02D2 — one true/false property changed on a visible object. The value
/// travels as a 32-bit word; anything but zero is true.
/// </summary>
public static class PublicUpdatePropertyBool
{
    public const uint Opcode = 0x02D2u;

    public readonly record struct Parsed(uint Guid, uint Property, bool Value);

    /// <summary>Parse a raw 0x02D2 body. Returns null on opcode mismatch / truncation.</summary>
    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 17) return null;          // 4 + 1 + 4 + 4 + 4
        if (BinaryPrimitives.ReadUInt32LittleEndian(body) != Opcode) return null;
        int pos = 4;
        pos += 1;                                   // sequence byte (not honored)
        uint guid = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        uint prop = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
        return new Parsed(guid, prop, value != 0u);
    }
}
