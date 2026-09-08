using System.Buffers.Binary;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Messages;

public static class CharacterDelete
{
    public const uint Opcode = 0xF655u;

    public static byte[] BuildRequestBody(string accountName, uint characterSlot)
    {
        ArgumentNullException.ThrowIfNull(accountName);
        var w = new PacketWriter(32);
        w.WriteUInt32(Opcode);
        w.WriteString16L(accountName);
        w.WriteUInt32(characterSlot);
        return w.ToArray();
    }

    public static bool IsAcknowledgement(ReadOnlySpan<byte> body) =>
        body.Length == sizeof(uint) &&
        BinaryPrimitives.ReadUInt32LittleEndian(body) == Opcode;
}
