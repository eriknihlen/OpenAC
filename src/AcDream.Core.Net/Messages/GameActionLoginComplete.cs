using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Messages;

public static class GameActionLoginComplete
{
    public const uint GameActionOpcode = 0xF7B1u;
    public const uint LoginCompleteActionType = 0x000000A1u;

    /// <summary>
    /// Build the body bytes for an outbound <c>GameAction(LoginComplete)</c>.
    /// Layout: opcode(4) + sequence(4) + actionType(4) = 12 bytes total.
    /// </summary>
    public static byte[] Build()
    {
        var w = new PacketWriter(16);
        w.WriteUInt32(GameActionOpcode);
        w.WriteUInt32(0u);
        w.WriteUInt32(LoginCompleteActionType);
        return w.ToArray();
    }
}
