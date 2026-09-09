using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public readonly record struct PlayPhysicsScriptType(
    uint Guid,
    uint RawScriptType,
    float Intensity)
{
    public const uint Opcode = 0xF755u;
    public const int WireSize = 16;

    public static PlayPhysicsScriptType? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length != WireSize
            || BinaryPrimitives.ReadUInt32LittleEndian(body) != Opcode)
            return null;

        return new PlayPhysicsScriptType(
            BinaryPrimitives.ReadUInt32LittleEndian(body[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[8..]),
            BinaryPrimitives.ReadSingleLittleEndian(body[12..]));
    }
}
