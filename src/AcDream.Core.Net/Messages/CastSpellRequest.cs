using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class CastSpellRequest
{
    public const uint GameActionEnvelope = 0xF7B1u;
    public const uint UntargetedSubOpcode = 0x0048u;
    public const uint TargetedSubOpcode   = 0x004Au;

    public static byte[] BuildUntargeted(uint gameActionSequence, uint spellId)
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  gameActionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  UntargetedSubOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), spellId);
        return body;
    }

    public static byte[] BuildTargeted(uint gameActionSequence, uint targetGuid, uint spellId)
    {
        byte[] body = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  gameActionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  TargetedSubOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), targetGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), spellId);
        return body;
    }
}
