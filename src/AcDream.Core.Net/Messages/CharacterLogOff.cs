using System.Buffers.Binary;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Messages;

public static class CharacterLogOff
{
    public const uint Opcode = 0xF653u;

    public static byte[] BuildRequestBody(uint characterId)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt32(Opcode);
        writer.WriteUInt32(characterId);
        return writer.ToArray();
    }

    public static bool IsConfirmation(ReadOnlySpan<byte> body) =>
        body.Length == sizeof(uint) &&
        BinaryPrimitives.ReadUInt32LittleEndian(body) == Opcode;
}
