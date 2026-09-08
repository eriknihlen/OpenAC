using System;
using System.Buffers.Binary;
using System.Text;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class PlayerKilledTests
{
    [Fact]
    public void TryParse_RoundTrips_AllThreeFields()
    {
        byte[] msg = PackString16L("Test");
        byte[] body = new byte[4 + msg.Length + 8];
        int pos = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(pos), PlayerKilled.Opcode); pos += 4;
        Array.Copy(msg, 0, body, pos, msg.Length); pos += msg.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(pos), 0x12345678u); pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(pos), 0x90ABCDEFu);

        var parsed = PlayerKilled.TryParse(body);
        Assert.NotNull(parsed);
        Assert.Equal("Test",       parsed!.Value.DeathMessage);
        Assert.Equal(0x12345678u,  parsed.Value.VictimGuid);
        Assert.Equal(0x90ABCDEFu,  parsed.Value.KillerGuid);
    }

    [Fact]
    public void TryParse_WrongOpcode_ReturnsNull()
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0xDEADBEEFu);
        Assert.Null(PlayerKilled.TryParse(body));
    }

    [Fact]
    public void TryParse_TruncatedAfterMessage_ReturnsNull()
    {
        byte[] msg = PackString16L("died");
        byte[] body = new byte[4 + msg.Length + 4];   // only one guid present
        BinaryPrimitives.WriteUInt32LittleEndian(body, PlayerKilled.Opcode);
        Array.Copy(msg, 0, body, 4, msg.Length);
        Assert.Null(PlayerKilled.TryParse(body));
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
