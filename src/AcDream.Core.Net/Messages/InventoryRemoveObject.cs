using System;
using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class InventoryRemoveObject
{
    public const uint Opcode = 0x0024u;

    public readonly record struct Parsed(uint Guid);

    /// <summary>Parse a raw 0x0024 body. Returns null on opcode mismatch / truncation.</summary>
    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 8) return null;           // 4 + 4
        if (BinaryPrimitives.ReadUInt32LittleEndian(body) != Opcode) return null;
        uint guid = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        return new Parsed(guid);
    }
}
