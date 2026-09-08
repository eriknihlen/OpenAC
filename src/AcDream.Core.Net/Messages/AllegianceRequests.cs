using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class AllegianceRequests
{
    public const uint GameActionEnvelope = 0xF7B1u;
    public const uint SwearOpcode = 0x001Du;
    public const uint BreakOpcode = 0x001Eu;

    public const uint AllegianceUpdateRequestOpcode = 0x001Fu;

    /// <summary>Pledge yourself to the given patron.</summary>
    public static byte[] BuildSwear(uint gameActionSequence, uint patronGuid)
    {
        return Build(gameActionSequence, SwearOpcode, patronGuid);
    }

    public static byte[] BuildBreak(uint gameActionSequence, uint targetGuid)
    {
        return Build(gameActionSequence, BreakOpcode, targetGuid);
    }

    public static byte[] BuildKick(uint gameActionSequence, uint vassalGuid)
    {
        return Build(gameActionSequence, BreakOpcode, vassalGuid);
    }

    public static byte[] BuildAllegianceUpdateRequest(uint gameActionSequence, bool on)
    {
        return Build(gameActionSequence, AllegianceUpdateRequestOpcode, on ? 1u : 0u);
    }

    private static byte[] Build(uint seq, uint sub, uint targetGuid)
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  sub);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), targetGuid);
        return body;
    }
}
