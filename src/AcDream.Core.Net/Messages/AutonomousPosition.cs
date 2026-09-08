using System.Numerics;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Messages;

public static class AutonomousPosition
{
    public const uint GameActionOpcode         = 0xF7B1u;
    public const uint AutonomousPositionAction = 0xF753u;

    public static byte[] Build(
        uint       gameActionSequence,
        uint       cellId,
        Vector3    position,
        Quaternion rotation,
        ushort     instanceSequence,
        ushort     serverControlSequence,
        ushort     teleportSequence,
        ushort     forcePositionSequence,
        byte       lastContact = 1)
    {
        var w = new PacketWriter(64);

        // --- GameAction envelope ---
        w.WriteUInt32(GameActionOpcode);
        w.WriteUInt32(gameActionSequence);
        w.WriteUInt32(AutonomousPositionAction);

        // --- WorldPosition (32 bytes) ---
        w.WriteUInt32(cellId);
        w.WriteFloat(position.X);
        w.WriteFloat(position.Y);
        w.WriteFloat(position.Z);
        // Quaternion wire order: W, X, Y, Z
        w.WriteFloat(rotation.W);
        w.WriteFloat(rotation.X);
        w.WriteFloat(rotation.Y);
        w.WriteFloat(rotation.Z);

        // --- Sequence numbers ---
        w.WriteUInt16(instanceSequence);
        w.WriteUInt16(serverControlSequence);
        w.WriteUInt16(teleportSequence);
        w.WriteUInt16(forcePositionSequence);

        w.WriteByte(lastContact);
        w.AlignTo4();

        return w.ToArray();
    }
}
