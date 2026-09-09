using System.Buffers.Binary;
using System.Numerics;

namespace AcDream.Core.Net.Messages;

public static class VectorUpdate
{
    public const uint Opcode = 0xF74Eu;

    public readonly record struct Parsed(
        uint Guid,
        Vector3 Velocity,
        Vector3 Omega,
        ushort InstanceSequence,
        ushort VectorSequence);

    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4 + 32) return null;
        try
        {
            uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4));
            if (opcode != Opcode) return null;

            uint guid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4));

            float vx = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(body.Slice(8, 4)));
            float vy = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(body.Slice(12, 4)));
            float vz = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(body.Slice(16, 4)));

            float ox = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(body.Slice(20, 4)));
            float oy = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(body.Slice(24, 4)));
            float oz = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(body.Slice(28, 4)));

            ushort instSeq = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(32, 2));
            ushort vecSeq = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(34, 2));

            return new Parsed(guid, new Vector3(vx, vy, vz), new Vector3(ox, oy, oz), instSeq, vecSeq);
        }
        catch
        {
            return null;
        }
    }
}
