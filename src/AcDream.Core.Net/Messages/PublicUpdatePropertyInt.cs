using System;
using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class PublicUpdatePropertyInt
{
    public const uint Opcode = 0x02CEu;

    public readonly record struct Parsed(uint Guid, uint Property, int Value);

    /// <summary>Parse a raw 0x02CE body. Returns null on opcode mismatch / truncation.</summary>
    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 17) return null;          // 4 + 1 + 4 + 4 + 4
        if (BinaryPrimitives.ReadUInt32LittleEndian(body) != Opcode) return null;
        int pos = 4;
        pos += 1;                                   // sequence byte (not honored)
        uint guid = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        uint prop = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        int value = BinaryPrimitives.ReadInt32LittleEndian(body[pos..]);
        return new Parsed(guid, prop, value);
    }
}
