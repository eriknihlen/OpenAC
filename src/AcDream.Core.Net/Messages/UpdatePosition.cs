using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class UpdatePosition
{
    public const uint Opcode = 0xF748u;

    [Flags]
    public enum PositionFlags : uint
    {
        None              = 0x00,
        HasVelocity       = 0x01,
        HasPlacementID    = 0x02,
        IsGrounded        = 0x04,
        OrientationHasNoW = 0x08,
        OrientationHasNoX = 0x10,
        OrientationHasNoY = 0x20,
        OrientationHasNoZ = 0x40,
    }

    public readonly record struct Parsed(
        uint Guid,
        CreateObject.ServerPosition Position,
        System.Numerics.Vector3? Velocity,
        uint? PlacementId,
        bool IsGrounded,
        ushort InstanceSequence = 0,
        ushort PositionSequence = 0,
        ushort TeleportSequence = 0,
        ushort ForcePositionSequence = 0);

    /// <summary>
    /// Parse a reassembled UpdatePosition body. <paramref name="body"/>
    /// must start with the 4-byte opcode. Returns null on truncation or
    /// wrong opcode.
    /// </summary>
    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        try
        {
            int pos = 0;
            if (body.Length - pos < 4) return null;
            uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
            pos += 4;
            if (opcode != Opcode) return null;

            if (body.Length - pos < 4) return null;
            uint guid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
            pos += 4;

            if (body.Length - pos < 4) return null;
            var flags = (PositionFlags)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
            pos += 4;

            if (body.Length - pos < 16) return null;
            uint cellId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
            float px = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos + 4));
            float py = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos + 8));
            float pz = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos + 12));
            pos += 16;

            float rw = 0f, rx = 0f, ry = 0f, rz = 0f;
            if ((flags & PositionFlags.OrientationHasNoW) == 0)
            {
                if (body.Length - pos < 4) return null;
                rw = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
                pos += 4;
            }
            if ((flags & PositionFlags.OrientationHasNoX) == 0)
            {
                if (body.Length - pos < 4) return null;
                rx = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
                pos += 4;
            }
            if ((flags & PositionFlags.OrientationHasNoY) == 0)
            {
                if (body.Length - pos < 4) return null;
                ry = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
                pos += 4;
            }
            if ((flags & PositionFlags.OrientationHasNoZ) == 0)
            {
                if (body.Length - pos < 4) return null;
                rz = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
                pos += 4;
            }

            System.Numerics.Vector3? velocity = null;
            if ((flags & PositionFlags.HasVelocity) != 0)
            {
                if (body.Length - pos < 12) return null;
                velocity = new System.Numerics.Vector3(
                    BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos + 0)),
                    BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos + 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos + 8)));
                pos += 12;
            }

            uint? placementId = null;
            if ((flags & PositionFlags.HasPlacementID) != 0)
            {
                if (body.Length - pos < 4) return null;
                placementId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
                pos += 4;
            }

            // Four u16 sequence numbers: instance, position, teleport, forcePosition.
            if (body.Length - pos < 8) return null;
            ushort instSeq  = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos));
            ushort posSeq   = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos + 2));
            ushort teleSeq  = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos + 4));
            ushort forceSeq = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos + 6));
            pos += 8;

            var serverPos = new CreateObject.ServerPosition(
                LandblockId: cellId,
                PositionX: px, PositionY: py, PositionZ: pz,
                RotationW: rw, RotationX: rx, RotationY: ry, RotationZ: rz);

            return new Parsed(guid, serverPos, velocity, placementId,
                IsGrounded: (flags & PositionFlags.IsGrounded) != 0,
                instSeq, posSeq, teleSeq, forceSeq);
        }
        catch
        {
            return null;
        }
    }
}
