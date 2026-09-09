using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Messages;

public static class CharacterEnterWorld
{
    public const uint EnterWorldRequestOpcode = 0xF7C8u;
    public const uint EnterWorldOpcode        = 0xF657u;

    public static byte[] BuildEnterWorldRequestBody()
    {
        var w = new PacketWriter(8);
        w.WriteUInt32(EnterWorldRequestOpcode);
        return w.ToArray();
    }

    public static byte[] BuildEnterWorldBody(uint characterGuid, string accountName)
    {
        ArgumentNullException.ThrowIfNull(accountName);
        var w = new PacketWriter(32);
        w.WriteUInt32(EnterWorldOpcode);
        w.WriteUInt32(characterGuid);
        w.WriteString16L(accountName);
        return w.ToArray();
    }
}
