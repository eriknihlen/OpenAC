using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public readonly record struct PlayPhysicsScript(uint Guid, uint ScriptDid)
{
    public const uint Opcode = 0xF754u;
    public const int WireSize = 12;

    public static PlayPhysicsScript? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length != WireSize
            || BinaryPrimitives.ReadUInt32LittleEndian(body) != Opcode)
            return null;

        return new PlayPhysicsScript(
            BinaryPrimitives.ReadUInt32LittleEndian(body[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[8..]));
    }
}
