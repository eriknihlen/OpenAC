using System;
using System.Buffers.Binary;
using System.Text;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class SoulEmoteTests
{
    [Fact]
    public void TryParse_RoundTrips_GuidAndStrings()
    {
        byte[] sender = PackString16L("Caith");
        byte[] text   = PackString16L("dances");
        byte[] body = new byte[4 + 4 + sender.Length + text.Length];
        int pos = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(pos), SoulEmote.Opcode); pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(pos), 0xCAFEF00Du); pos += 4;
        Array.Copy(sender, 0, body, pos, sender.Length); pos += sender.Length;
        Array.Copy(text,   0, body, pos, text.Length);

        var parsed = SoulEmote.TryParse(body);
        Assert.NotNull(parsed);
        Assert.Equal(0xCAFEF00Du, parsed!.Value.SenderGuid);
        Assert.Equal("Caith",  parsed.Value.SenderName);
        Assert.Equal("dances", parsed.Value.Text);
    }

    [Fact]
    public void TryParse_WrongOpcode_ReturnsNull()
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0xBADBEEFu);
        Assert.Null(SoulEmote.TryParse(body));
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
