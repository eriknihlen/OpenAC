using System.Buffers.Binary;

namespace AcDream.Core.Net.Cryptography;

public static class Hash32
{
    public static uint Calculate(ReadOnlySpan<byte> data)
    {
        int length = data.Length;
        uint checksum = (uint)length << 16;

        int wordAligned = length & ~3;
        for (int i = 0; i < wordAligned; i += 4)
        {
            checksum += BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i));
        }

        // Tail bytes (0, 1, 2, or 3 bytes) with descending shift: first tail
        // byte goes into bits 24..31, second into 16..23, third into 8..15.
        int shift = 24;
        for (int j = wordAligned; j < length; j++)
        {
            checksum += (uint)data[j] << shift;
            shift -= 8;
        }

        return checksum;
    }
}
