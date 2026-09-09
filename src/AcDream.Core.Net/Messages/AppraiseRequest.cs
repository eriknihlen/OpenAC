using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class AppraiseRequest
{
    public const uint GameActionEnvelope = 0xF7B1u;
    public const uint SubOpcode = 0x00C8u;

    /// <summary>
    /// Pack an AppraiseRequest body (with envelope) ready to be handed
    /// to <c>WorldSession.SendGameAction</c>.
    /// </summary>
    public static byte[] Build(uint gameActionSequence, uint targetGuid)
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  gameActionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  SubOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), targetGuid);
        return body;
    }
}
