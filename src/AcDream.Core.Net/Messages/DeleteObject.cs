using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class DeleteObject
{
    public const uint Opcode = 0xF747u;

    /// <summary>A true object-destruction event for one exact incarnation.</summary>
    public readonly record struct Parsed(uint Guid, ushort InstanceSequence);

    /// <summary>
    /// Parse a 0xF747 body. <paramref name="body"/> must start with the
    /// 4-byte opcode, matching every other parser in this namespace.
    /// PickupEvent has a distinct parser and runtime event because it advances
    /// POSITION_TS and retains the logical object.
    /// </summary>
    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 10)
            return null;

        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4));
        if (opcode != Opcode)
            return null;

        uint guid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4));
        ushort instanceSequence = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(8, 2));
        return new Parsed(guid, instanceSequence);
    }
}
