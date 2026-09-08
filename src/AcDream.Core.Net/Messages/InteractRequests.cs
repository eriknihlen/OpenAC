using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class InteractRequests
{
    public const uint GameActionEnvelope = 0xF7B1u;
    public const uint UseOpcode = 0x0036u;
    public const uint UseWithTargetOpcode = 0x0035u;
    public const uint TeleToLifestoneOpcode = 0x0063u;
    public const uint PutItemInContainerOpcode = 0x0019u;

    public static byte[] BuildUse(uint gameActionSequence, uint targetGuid)
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  gameActionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  UseOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), targetGuid);
        return body;
    }

    public static byte[] BuildUseWithTarget(
        uint gameActionSequence, uint sourceGuid, uint targetGuid)
    {
        byte[] body = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  gameActionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  UseWithTargetOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), sourceGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), targetGuid);
        return body;
    }

    /// <summary>
    /// Teleport to your lifestone. No target guid — just tells the
    /// server "recall me." Fails if you haven't tied to a lifestone
    /// (server responds with a WeenieError).
    /// </summary>
    public static byte[] BuildTeleToLifestone(uint gameActionSequence)
    {
        byte[] body = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  gameActionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  TeleToLifestoneOpcode);
        return body;
    }

    public static byte[] BuildPickUp(
        uint gameActionSequence, uint itemGuid, uint containerGuid, int placement)
    {
        byte[] body = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  gameActionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  PutItemInContainerOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), itemGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), containerGuid);
        BinaryPrimitives.WriteInt32LittleEndian (body.AsSpan(20), placement);
        return body;
    }
}
