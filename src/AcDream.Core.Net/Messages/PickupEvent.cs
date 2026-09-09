using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class PickupEvent
{
    public const uint Opcode = 0xF74Au;

    public readonly record struct Parsed(
        uint Guid, ushort InstanceSequence, ushort PositionSequence);

    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 12)
            return null;

        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4));
        if (opcode != Opcode)
            return null;

        uint guid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4));
        ushort instanceSequence = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(8, 2));
        ushort positionSequence = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(10, 2));
        return new Parsed(guid, instanceSequence, positionSequence);
    }
}
