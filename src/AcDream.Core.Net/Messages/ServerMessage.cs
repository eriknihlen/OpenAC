using System;
using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class ServerMessage
{
    public const uint Opcode = 0xF7E0u;

    public readonly record struct Parsed(string Message, uint ChatType);

    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 8) return null;
        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (opcode != Opcode) return null;

        try
        {
            int pos = 4;
            string message = StringReader.ReadString16L(body, ref pos);
            if (body.Length - pos < 4) return null;
            uint chatType = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
            return new Parsed(message, chatType);
        }
        catch { return null; }
    }
}
