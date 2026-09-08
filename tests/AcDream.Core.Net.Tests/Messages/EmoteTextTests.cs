using System;
using System.Buffers.Binary;
using System.Text;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class EmoteTextTests
{
    [Fact]
    public void TryParse_RoundTrips_GuidAndStrings()
    {
        byte[] sender = PackString16L("Caith");
        byte[] text   = PackString16L("waves at you");
        byte[] body = new byte[4 + 4 + sender.Length + text.Length];
        int pos = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(pos), EmoteText.Opcode);
        pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(pos), 0xDEADBEEFu);
        pos += 4;
        Array.Copy(sender, 0, body, pos, sender.Length); pos += sender.Length;
        Array.Copy(text,   0, body, pos, text.Length);

        var parsed = EmoteText.TryParse(body);
        Assert.NotNull(parsed);
        Assert.Equal(0xDEADBEEFu, parsed!.Value.SenderGuid);
        Assert.Equal("Caith",         parsed.Value.SenderName);
        Assert.Equal("waves at you",  parsed.Value.Text);
    }

    [Fact]
    public void TryParse_WrongOpcode_ReturnsNull()
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0x12345678u);
        Assert.Null(EmoteText.TryParse(body));
    }

    [Fact]
    public void TryParse_TruncatedHeader_ReturnsNull()
    {
        byte[] body = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(body, EmoteText.Opcode);
        Assert.Null(EmoteText.TryParse(body));
    }

    [Fact]
    public void TryParse_PreservesWindows1252_RoundTrip()
    {
        byte[] sender = PackString16L("Élise");
        byte[] text   = PackString16L("waves at you, Café");
        byte[] body = new byte[4 + 4 + sender.Length + text.Length];
        int pos = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(pos), EmoteText.Opcode); pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(pos), 1u); pos += 4;
        Array.Copy(sender, 0, body, pos, sender.Length); pos += sender.Length;
        Array.Copy(text,   0, body, pos, text.Length);

        var parsed = EmoteText.TryParse(body);
        Assert.NotNull(parsed);
        Assert.Equal("Élise",              parsed!.Value.SenderName);
        Assert.Equal("waves at you, Café", parsed.Value.Text);
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
