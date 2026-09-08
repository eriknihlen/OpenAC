using System;
using System.Buffers.Binary;
using System.Text;

namespace AcDream.Core.Net.Messages;

public static class HearSpeech
{
    public const uint LocalOpcode  = 0x02BBu;
    public const uint RangedOpcode = 0x02BCu;

    public readonly record struct Parsed(
        string Text,
        string SenderName,
        uint SenderGuid,
        uint ChatType,
        bool IsRanged,
        float Range);

    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 16) return null;

        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body);
        bool isRanged;
        if (opcode == LocalOpcode)       isRanged = false;
        else if (opcode == RangedOpcode) isRanged = true;
        else return null;

        int pos = 4;
        try
        {
            string text   = ReadString16L(body, ref pos);
            string sender = ReadString16L(body, ref pos);

            int tailSize = isRanged ? 12 : 8;
            if (body.Length - pos < tailSize) return null;

            uint senderGuid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));  pos += 4;

            float range = 0f;
            if (isRanged)
            {
                range = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
                pos += 4;
            }

            uint chatType = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));  pos += 4;
            return new Parsed(text, sender, senderGuid, chatType, isRanged, range);
        }
        catch { return null; }
    }

    private static string ReadString16L(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 2) throw new FormatException("truncated String16L length");
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(pos));
        pos += 2;
        if (source.Length - pos < length) throw new FormatException("truncated String16L body");
        string result = Encoding.GetEncoding(1252).GetString(source.Slice(pos, length));
        pos += length;
        int recordSize = 2 + length;
        int padding = (4 - (recordSize & 3)) & 3;
        pos += padding;
        return result;
    }
}
