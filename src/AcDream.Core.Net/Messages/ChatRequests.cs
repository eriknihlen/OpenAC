using System;
using System.Buffers.Binary;
using System.Text;

namespace AcDream.Core.Net.Messages;

public static class ChatRequests
{
    public const uint GameActionEnvelope = 0xF7B1u;
    public const uint TalkOpcode        = 0x0015u;
    public const uint TellOpcode        = 0x005Du;
    public const uint TalkDirectOpcode  = 0x0032u;
    public const uint ChatChannelOpcode = 0x0147u;

    /// <summary>Send a local /say message (heard by anyone within ~20m).</summary>
    public static byte[] BuildTalk(uint gameActionSequence, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        byte[] msg = PackString16L(message);
        byte[] body = new byte[12 + msg.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body,           GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), gameActionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), TalkOpcode);
        Array.Copy(msg, 0, body, 12, msg.Length);
        return body;
    }

    public static byte[] BuildTell(uint gameActionSequence, string targetName, string message)
    {
        ArgumentNullException.ThrowIfNull(targetName);
        ArgumentNullException.ThrowIfNull(message);
        byte[] msg  = PackString16L(message);
        byte[] name = PackString16L(targetName);
        byte[] body = new byte[12 + msg.Length + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body,           GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), gameActionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), TellOpcode);
        Array.Copy(msg,  0, body, 12, msg.Length);
        Array.Copy(name, 0, body, 12 + msg.Length, name.Length);
        return body;
    }

    /// <summary>
    /// Direct speech aimed at one already-identified object rather than at a
    /// name. The server resolves the id against the objects around us, so this
    /// reaches creatures and NPCs that a by-name tell cannot address.
    /// </summary>
    public static byte[] BuildTalkDirect(uint gameActionSequence, uint targetGuid, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        byte[] msg = PackString16L(message);
        byte[] body = new byte[16 + msg.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body,           GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), gameActionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), TalkDirectOpcode);
        Array.Copy(msg, 0, body, 12, msg.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(
            body.AsSpan(12 + msg.Length),
            targetGuid);
        return body;
    }

    public static byte[] BuildChatChannel(uint gameActionSequence, uint channelId, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        byte[] msg = PackString16L(message);
        byte[] body = new byte[16 + msg.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  gameActionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  ChatChannelOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), channelId);
        Array.Copy(msg, 0, body, 16, msg.Length);
        return body;
    }

    private static byte[] PackString16L(string s)
    {
        byte[] data = Encoding.GetEncoding(1252).GetBytes(s);
        if (data.Length > ushort.MaxValue)
            throw new ArgumentException("String too long for 16-bit length prefix.", nameof(s));

        int recordSize = 2 + data.Length;
        int padding = (4 - (recordSize & 3)) & 3;
        byte[] result = new byte[recordSize + padding];
        BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)data.Length);
        Array.Copy(data, 0, result, 2, data.Length);
        // trailing bytes are already zero from new[]
        return result;
    }
}
