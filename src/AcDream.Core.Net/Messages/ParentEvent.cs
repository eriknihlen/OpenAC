using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class ParentEvent
{
    public const uint Opcode = 0xF749u;

    public readonly record struct Parsed(
        uint ParentGuid,
        uint ChildGuid,
        uint ParentLocation,
        uint PlacementId,
        ushort ParentInstanceSequence,
        ushort ChildPositionSequence);

    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 24)
            return null;

        if (BinaryPrimitives.ReadUInt32LittleEndian(body) != Opcode)
            return null;

        return new Parsed(
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(12, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(16, 4)),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(20, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(22, 2)));
    }
}
