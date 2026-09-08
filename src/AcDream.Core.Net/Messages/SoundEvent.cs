using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public readonly record struct SoundEvent(uint Guid, uint SoundType, float Volume)
{
    public const uint Opcode = 0xF750u;
    public const int WireSize = 16;

    public static SoundEvent? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < WireSize
            || BinaryPrimitives.ReadUInt32LittleEndian(body) != Opcode)
            return null;

        return new SoundEvent(
            BinaryPrimitives.ReadUInt32LittleEndian(body[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[8..]),
            BinaryPrimitives.ReadSingleLittleEndian(body[12..]));
    }
}
