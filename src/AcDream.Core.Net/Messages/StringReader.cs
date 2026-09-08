using System;
using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

internal static class StringReader
{
    public static string ReadString16L(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 2) throw new FormatException("truncated String16L length");
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(pos));
        pos += 2;
        if (source.Length - pos < length) throw new FormatException("truncated String16L body");
        string result = Encodings.Windows1252.GetString(source.Slice(pos, length));
        pos += length;
        int recordSize = 2 + length;
        int padding = (4 - (recordSize & 3)) & 3;
        pos += padding;
        return result;
    }
}
