using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class AttackTargetRequest
{
    public const uint GameActionEnvelope = 0xF7B1u;
    public const uint TargetedMeleeAttackOpcode = 0x0008u;
    public const uint TargetedMissileAttackOpcode = 0x000Au;
    public const uint CancelAttackOpcode = 0x01B7u;

    /// <summary>Build the wire body for a targeted melee attack.</summary>
    public static byte[] BuildMelee(
        uint gameActionSequence,
        uint targetGuid,
        uint attackHeight,
        float powerLevel)
    {
        byte[] body = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(body,             GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),   gameActionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),   TargetedMeleeAttackOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12),  targetGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16),  attackHeight);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(20),  powerLevel);
        return body;
    }

    /// <summary>Build the wire body for a targeted missile attack.</summary>
    public static byte[] BuildMissile(
        uint gameActionSequence,
        uint targetGuid,
        uint attackHeight,
        float accuracyLevel)
    {
        byte[] body = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(body,             GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),   gameActionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),   TargetedMissileAttackOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12),  targetGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16),  attackHeight);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(20),  accuracyLevel);
        return body;
    }

    public static byte[] BuildCancel(uint gameActionSequence)
    {
        byte[] body = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(body,           GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), gameActionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), CancelAttackOpcode);
        return body;
    }
}
