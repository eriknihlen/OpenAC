using System;
using System.Buffers.Binary;
using System.Text;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class ChatTests
{
    [Fact]
    public void BuildTalk_EmitsOpcodeAndString16L()
    {
        byte[] body = ChatRequests.BuildTalk(gameActionSequence: 3, message: "hi");

        Assert.Equal(ChatRequests.TalkOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));

        // Verify the string16L starts at offset 12.
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(12));
        Assert.Equal(2, len);
        Assert.Equal("hi", Encoding.ASCII.GetString(body.AsSpan(14, 2)));

        // Record size = 2+2 = 4, no padding needed.
        Assert.Equal(16, body.Length);
    }

    [Fact]
    public void BuildTalk_EmitsPadding_WhenMessageLengthRequiresIt()
    {
        byte[] body = ChatRequests.BuildTalk(gameActionSequence: 3, message: "h");
        // 2+1=3 bytes record → pad 1 byte.
        // Total body = 12 (envelope) + 4 (str16L aligned) = 16.
        Assert.Equal(16, body.Length);
    }

    [Fact]
    public void BuildTell_WritesMessageFirstThenTarget()
    {
        byte[] body = ChatRequests.BuildTell(
            gameActionSequence: 5, targetName: "Alice", message: "hey");

        Assert.Equal(ChatRequests.TellOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));

        int pos = 12;
        ushort len1 = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos));
        Assert.Equal(3, len1);
        Assert.Equal("hey", Encoding.ASCII.GetString(body.AsSpan(pos + 2, 3)));

        // "hey" record = 2+3=5, pad 3 → advance by 8.
        pos += 8;
        ushort len2 = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos));
        Assert.Equal(5, len2);
        Assert.Equal("Alice", Encoding.ASCII.GetString(body.AsSpan(pos + 2, 5)));
    }

    [Fact]
    public void BuildTalkDirect_WritesMessageThenTargetId_Issue50()
    {
        byte[] body = ChatRequests.BuildTalkDirect(
            gameActionSequence: 7, targetGuid: 0x8000ABCDu, message: "hey");

        Assert.Equal(ChatRequests.GameActionEnvelope,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(0x0032u, ChatRequests.TalkDirectOpcode);
        Assert.Equal(ChatRequests.TalkDirectOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));

        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(12));
        Assert.Equal(3, len);
        Assert.Equal("hey", Encoding.ASCII.GetString(body.AsSpan(14, 3)));

        // "hey" record = 2+3=5, padded to 8; the id follows it.
        Assert.Equal(0x8000ABCDu,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20)));
        Assert.Equal(24, body.Length);
    }

    [Fact]
    public void BuildTalkDirect_UnpaddedMessageLeavesNoGapBeforeTheTargetId_Issue50()
    {
        // 2 + 2 = 4 bytes of record need no padding at all.
        byte[] body = ChatRequests.BuildTalkDirect(
            gameActionSequence: 2, targetGuid: 0x12345678u, message: "hi");

        Assert.Equal(20, body.Length);
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(12));
        Assert.Equal(2, len);
        Assert.Equal("hi", Encoding.ASCII.GetString(body.AsSpan(14, 2)));
        Assert.Equal(0x12345678u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
    }

    [Fact]
    public void BuildTalkDirect_PadsTheMessageBeforeTheTargetId_Issue50()
    {
        // 2 + 4 = 6 bytes of record pad out to 8.
        byte[] body = ChatRequests.BuildTalkDirect(
            gameActionSequence: 1, targetGuid: 0x50000001u, message: "abcd");

        Assert.Equal(24, body.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(18)));
        Assert.Equal(0x50000001u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20)));
    }

    [Fact]
    public void BuildChatChannel_IncludesChannelId()
    {
        byte[] body = ChatRequests.BuildChatChannel(
            gameActionSequence: 1, channelId: 42, message: "tell me the good dungeons");

        Assert.Equal(ChatRequests.ChatChannelOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(42u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }

    [Fact]
    public void HearSpeech_TryParse_LocalRoundTrip()
    {
        // Build a 0x02BB message and re-parse it.
        byte[] talkBody = ChatRequests.BuildTalk(gameActionSequence: 0, message: "hello");

        // Now synthesize the inbound HearSpeech format.
        byte[] msg = PackString16L("hello");
        byte[] sender = PackString16L("Alice");
        byte[] inbound = new byte[4 + msg.Length + sender.Length + 8];
        int pos = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(inbound, HearSpeech.LocalOpcode);
        pos += 4;
        Array.Copy(msg, 0, inbound, pos, msg.Length); pos += msg.Length;
        Array.Copy(sender, 0, inbound, pos, sender.Length); pos += sender.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(inbound.AsSpan(pos), 0xCAFEu);    pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(inbound.AsSpan(pos), 0x0B);        pos += 4; // Speech

        var parsed = HearSpeech.TryParse(inbound);
        Assert.NotNull(parsed);
        Assert.Equal("hello", parsed!.Value.Text);
        Assert.Equal("Alice", parsed.Value.SenderName);
        Assert.Equal(0xCAFEu, parsed.Value.SenderGuid);
        Assert.Equal(0x0Bu, parsed.Value.ChatType);
        Assert.False(parsed.Value.IsRanged);
    }

    [Fact]
    public void HearSpeech_TryParse_RangedFlag()
    {
        byte[] msg = PackString16L("X");
        byte[] sender = PackString16L("Y");
        byte[] inbound = new byte[4 + msg.Length + sender.Length + 12];
        BinaryPrimitives.WriteUInt32LittleEndian(inbound, HearSpeech.RangedOpcode);
        int pos = 4;
        Array.Copy(msg, 0, inbound, pos, msg.Length); pos += msg.Length;
        Array.Copy(sender, 0, inbound, pos, sender.Length); pos += sender.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(inbound.AsSpan(pos), 0); pos += 4;
        BinaryPrimitives.WriteSingleLittleEndian(inbound.AsSpan(pos), 60f); pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(inbound.AsSpan(pos), 0x0Bu); pos += 4;

        var parsed = HearSpeech.TryParse(inbound);
        Assert.NotNull(parsed);
        Assert.True(parsed!.Value.IsRanged);
        Assert.Equal(60f, parsed.Value.Range);
        Assert.Equal(0x0Bu, parsed.Value.ChatType);
    }

    [Fact]
    public void HearSpeech_TryParse_WrongOpcode_ReturnsNull()
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0xDEADBEEFu);
        Assert.Null(HearSpeech.TryParse(body));
    }

    [Fact]
    public void HearSpeech_TryParse_PreservesWindows1252_RoundTrip()
    {
        byte[] msg = PackString16L("Café");
        byte[] sender = PackString16L("Élise");
        byte[] inbound = new byte[4 + msg.Length + sender.Length + 8];
        int pos = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(inbound, HearSpeech.LocalOpcode);
        pos += 4;
        Array.Copy(msg, 0, inbound, pos, msg.Length); pos += msg.Length;
        Array.Copy(sender, 0, inbound, pos, sender.Length); pos += sender.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(inbound.AsSpan(pos), 0u); pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(inbound.AsSpan(pos), 0u);

        var parsed = HearSpeech.TryParse(inbound);
        Assert.NotNull(parsed);
        Assert.Equal("Café",  parsed!.Value.Text);
        Assert.Equal("Élise", parsed.Value.SenderName);
    }

    private static byte[] PackString16L(string s)
    {
        byte[] data = Encoding.GetEncoding(1252).GetBytes(s);
        int recordSize = 2 + data.Length;
        int padding = (4 - (recordSize & 3)) & 3;
        byte[] result = new byte[recordSize + padding];
        BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)data.Length);
        Array.Copy(data, 0, result, 2, data.Length);
        return result;
    }
}
