using System;
using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class SetState
{
    public const uint Opcode = 0xF74Bu;

    public readonly record struct Parsed(
        uint Guid,
        uint PhysicsState,
        ushort InstanceSequence,
        ushort StateSequence);

    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 16) return null;
        try
        {
            uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4));
            if (opcode != Opcode) return null;

            uint guid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4));
            uint state = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(8, 4));
            ushort instSeq = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(12, 2));
            ushort stateSeq = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(14, 2));

            return new Parsed(guid, state, instSeq, stateSeq);
        }
        catch
        {
            return null;
        }
    }
}
