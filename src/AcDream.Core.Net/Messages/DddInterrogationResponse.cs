using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Messages;

public static class DddInterrogationResponse
{
    public const uint Opcode = 0xF7E6u;
    public const uint EnglishLanguage = 1u;

    public static byte[] Build()
    {
        var w = new PacketWriter(16);
        w.WriteUInt32(Opcode);
        w.WriteUInt32(EnglishLanguage);
        w.WriteUInt32(0u);  // empty TaggedIterationList vec
        return w.ToArray();
    }
}
