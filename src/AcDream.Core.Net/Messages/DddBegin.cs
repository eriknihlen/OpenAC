using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class DddBegin
{
    public const uint Opcode = 0xF7E7;
    public readonly record struct Parsed(uint ExpectedBytes, uint IterationCount)
    {
        public bool RequiresUpdate => ExpectedBytes != 0 || IterationCount != 0;
    }

    public static Parsed Parse(ReadOnlySpan<byte> message)
    {
        if (message.Length < 12 || Read(message, 0) != Opcode)
            throw new InvalidDataException("Invalid data-update announcement.");
        uint bytes = Read(message, 4);
        uint iterations = Read(message, 8);
        int offset = 12;
        if (iterations > (uint)((message.Length - offset) / 20))
            throw new InvalidDataException("Truncated data-update announcement.");
        for (uint i = 0; i < iterations; i++)
        {
            offset += 12;
            SkipIds(message, ref offset);
            SkipIds(message, ref offset);
        }
        if (offset != message.Length)
            throw new InvalidDataException("Invalid data-update announcement length.");
        return new Parsed(bytes, iterations);
    }

    private static void SkipIds(ReadOnlySpan<byte> message, ref int offset)
    {
        if (message.Length - offset < 4)
            throw new InvalidDataException("Truncated data-update item list.");
        uint count = Read(message, offset);
        offset += 4;
        if (count > (uint)((message.Length - offset) / 4))
            throw new InvalidDataException("Truncated data-update item list.");
        offset += (int)count * 4;
    }

    private static uint Read(ReadOnlySpan<byte> message, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(message[offset..]);
}
