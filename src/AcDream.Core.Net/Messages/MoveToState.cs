using System.Numerics;
using AcDream.Core.Net.Packets;
using AcDream.Core.Physics;

namespace AcDream.Core.Net.Messages;

public static class MoveToState
{
    public const uint GameActionOpcode  = 0xF7B1u;
    public const uint MoveToStateAction = 0xF61Cu;

    public static byte[] Build(
        uint           gameActionSequence,
        RawMotionState rawMotionState,
        uint           cellId,
        Vector3        position,
        Quaternion     rotation,
        ushort         instanceSequence,
        ushort         serverControlSequence,
        ushort         teleportSequence,
        ushort         forcePositionSequence,
        bool           contact = true,
        bool           standingLongjump = false)
    {
        var w = new PacketWriter(128);

        // --- GameAction envelope ---
        w.WriteUInt32(GameActionOpcode);
        w.WriteUInt32(gameActionSequence);
        w.WriteUInt32(MoveToStateAction);

        RawMotionStatePacker.Pack(w, rawMotionState);

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

        byte trailing = (byte)((standingLongjump ? 0x02 : 0) | (contact ? 0x01 : 0));
        w.WriteByte(trailing);
        w.AlignTo4();

        return w.ToArray();
    }
}
