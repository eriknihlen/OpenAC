using System;
using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class SoulEmote
{
    public const uint Opcode = 0x01E2u;

    public readonly record struct Parsed(
        uint SenderGuid,
        string SenderName,
        string Text);

    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 8) return null;
        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (opcode != Opcode) return null;

        try
        {
            int pos = 4;
            uint senderGuid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
            pos += 4;
            string senderName = StringReader.ReadString16L(body, ref pos);
            string text       = StringReader.ReadString16L(body, ref pos);
            return new Parsed(senderGuid, senderName, text);
        }
        catch { return null; }
    }
}
