using System.Numerics;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Messages;

public static class JumpAction
{
    public const uint GameActionOpcode = 0xF7B1u;
    public const uint JumpOpcode       = 0xF61Bu;

    public static byte[] Build(
        uint       gameActionSequence,
        float      extent,
        Vector3    velocity,
        uint       cellId,
        Vector3    position,
        Quaternion rotation,
        ushort     instanceSequence,
        ushort     serverControlSequence,
        ushort     teleportSequence,
        ushort     forcePositionSequence)
    {
        var w = new PacketWriter(80);

        w.WriteUInt32(GameActionOpcode);
        w.WriteUInt32(gameActionSequence);
        w.WriteUInt32(JumpOpcode);

        w.WriteFloat(extent);
        w.WriteFloat(velocity.X);
        w.WriteFloat(velocity.Y);
        w.WriteFloat(velocity.Z);

        w.WriteUInt32(cellId);
        w.WriteFloat(position.X);
        w.WriteFloat(position.Y);
        w.WriteFloat(position.Z);
        // Quaternion wire order: W, X, Y, Z
        w.WriteFloat(rotation.W);
        w.WriteFloat(rotation.X);
        w.WriteFloat(rotation.Y);
        w.WriteFloat(rotation.Z);

        w.WriteUInt16(instanceSequence);
        w.WriteUInt16(serverControlSequence);
        w.WriteUInt16(teleportSequence);
        w.WriteUInt16(forcePositionSequence);

        w.AlignTo4();

        return w.ToArray();
    }
}
