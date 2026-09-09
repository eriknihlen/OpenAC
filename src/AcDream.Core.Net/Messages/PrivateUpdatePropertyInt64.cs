using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class PrivateUpdatePropertyInt64
{
    public const uint Opcode = 0x02CFu;
    public const int BodySize = 17; // opcode(4) + sequence(1) + property(4) + value(8)

    public readonly record struct Parsed(uint Property, long Value);

    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < BodySize) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(body) != Opcode) return null;

        const int propertyOffset = 5; // opcode + one-byte quality sequence
        uint property = BinaryPrimitives.ReadUInt32LittleEndian(body[propertyOffset..]);
        long value = BinaryPrimitives.ReadInt64LittleEndian(body[(propertyOffset + 4)..]);
        return new Parsed(property, value);
    }
}
