using System;
using System.Buffers.Binary;
using System.Text;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class ServerMessageTests
{
    [Fact]
    public void TryParse_RoundTrips_MessageAndChatType()
    {
        byte[] msg = PackString16L("The server will reset in 5 minutes.");
        byte[] body = new byte[4 + msg.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, ServerMessage.Opcode);
        Array.Copy(msg, 0, body, 4, msg.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4 + msg.Length), 5u);  // System

        var parsed = ServerMessage.TryParse(body);
        Assert.NotNull(parsed);
        Assert.Equal("The server will reset in 5 minutes.", parsed!.Value.Message);
        Assert.Equal(5u, parsed.Value.ChatType);
    }

    [Fact]
    public void TryParse_PreservesEmbeddedCommandResponseNewlines()
    {
        const string text = "@acecommands\n@help\n@teleport";
        byte[] msg = PackString16L(text);
        byte[] body = new byte[4 + msg.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, ServerMessage.Opcode);
        Array.Copy(msg, 0, body, 4, msg.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4 + msg.Length), 0u);

        ServerMessage.Parsed parsed = Assert.IsType<ServerMessage.Parsed>(
            ServerMessage.TryParse(body));

        Assert.Equal(text, parsed.Message);
    }

    [Fact]
    public void TryParse_WrongOpcode_ReturnsNull()
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0xDEADBEEFu);
        Assert.Null(ServerMessage.TryParse(body));
    }

    [Fact]
    public void TryParse_TruncatedChatType_ReturnsNull()
    {
        byte[] msg = PackString16L("hi");
        byte[] body = new byte[4 + msg.Length + 2];   // 2 bytes short
        BinaryPrimitives.WriteUInt32LittleEndian(body, ServerMessage.Opcode);
        Array.Copy(msg, 0, body, 4, msg.Length);
        Assert.Null(ServerMessage.TryParse(body));
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
