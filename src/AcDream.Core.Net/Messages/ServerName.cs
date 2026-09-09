using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class ServerName
{
    public const uint Opcode = 0xF7E1u;

    public readonly record struct Parsed(
        int CurrentConnections,
        int MaxConnections,
        string WorldName);

    public static Parsed Parse(ReadOnlySpan<byte> body)
    {
        int pos = 0;

        uint opcode = ReadU32(body, ref pos);
        if (opcode != Opcode)
            throw new FormatException($"expected ServerName opcode 0x{Opcode:X4}, got 0x{opcode:X8}");

        int currentConnections = unchecked((int)ReadU32(body, ref pos));
        int maxConnections = unchecked((int)ReadU32(body, ref pos));
        string worldName = StringReader.ReadString16L(body, ref pos);

        return new Parsed(currentConnections, maxConnections, worldName);
    }

    private static uint ReadU32(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 4) throw new FormatException("truncated u32");
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(pos));
        pos += 4;
        return value;
    }
}
