using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AcDream.Tests.Fixtures.PluginIcons;

/// <summary>Real 64x64 PNGs (correct CRC32 and zlib IDAT) plus a builder for every header rule
/// refusal, shared between the launcher and the client's independent parity checks (L-317, L-303).</summary>
internal static class PngTestData
{
    internal const int Extent = 64;

    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private const int IhdrChunkEnd = 8 + 8 + 13 + 4;

    internal static byte[] Valid() => Build(Extent, Extent);

    internal static byte[] OverMaximumBytes()
    {
        byte[] valid = Valid();
        var result = new byte[Math.Max(64 * 1024 + 1, valid.Length + 1)];
        valid.CopyTo(result, 0);
        return result;
    }

    internal static byte[] NoSignature()
    {
        byte[] bytes = Valid();
        bytes[0] = 0;
        return bytes;
    }

    internal static byte[] FirstChunkWrongType()
    {
        byte[] bytes = Valid();
        "IHDX"u8.CopyTo(bytes.AsSpan(12, 4));
        return bytes;
    }

    internal static byte[] FirstChunkWrongLength()
    {
        byte[] bytes = Valid();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 12);
        return bytes;
    }

    internal static byte[] WrongDimensions() => Build(32, 32);

    internal static byte[] ChunkLengthOverflow()
    {
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x8000_0000);
        "abcd"u8.CopyTo(header.AsSpan(4));
        return InsertAfterIhdr(header);
    }

    internal static byte[] ChunkRunsPastEnd()
    {
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, 100_000);
        "abcd"u8.CopyTo(header.AsSpan(4));
        return InsertAfterIhdr(header);
    }

    internal static byte[] AnimatedControl() => InsertAfterIhdr(ChunkBytes("acTL", new byte[8]));

    internal static byte[] MissingIend()
    {
        byte[] bytes = Valid();
        return bytes[..^12];
    }

    internal static byte[] TrailingBytesAfterIend()
    {
        byte[] bytes = Valid();
        var result = new byte[bytes.Length + 1];
        bytes.CopyTo(result, 0);
        return result;
    }

    internal static byte[] InflateBomb() => Build(Extent, Extent, rawOverride: new byte[4_000_000]);

    /// <summary>A 64x64 PNG with the given header fields and a zero-filled pixel stream of
    /// <paramref name="rawLength"/> bytes.</summary>
    internal static byte[] WithHeader(
        byte bitDepth,
        byte colorType,
        int rawLength,
        byte compression = 0,
        byte filter = 0,
        byte interlace = 0) =>
        Build(Extent, Extent, new byte[rawLength], (bitDepth, colorType, compression, filter, interlace));

    private static byte[] InsertAfterIhdr(byte[] bytes)
    {
        byte[] valid = Valid();
        var result = new byte[valid.Length + bytes.Length];
        valid.AsSpan(0, IhdrChunkEnd).CopyTo(result);
        bytes.CopyTo(result.AsSpan(IhdrChunkEnd));
        valid.AsSpan(IhdrChunkEnd).CopyTo(result.AsSpan(IhdrChunkEnd + bytes.Length));
        return result;
    }

    private static byte[] Build(
        int width,
        int height,
        byte[]? rawOverride = null,
        (byte BitDepth, byte ColorType, byte Compression, byte Filter, byte Interlace)? header = null)
    {
        byte[] raw = rawOverride ?? BuildRawScanlines(width, height);
        byte[] idat = Deflate(raw);
        byte[] ihdr = BuildIhdr(width, height);
        if (header is { } h)
        {
            ihdr[8] = h.BitDepth;
            ihdr[9] = h.ColorType;
            ihdr[10] = h.Compression;
            ihdr[11] = h.Filter;
            ihdr[12] = h.Interlace;
        }

        using var stream = new MemoryStream();
        stream.Write(Signature);
        stream.Write(ChunkBytes("IHDR", ihdr));
        stream.Write(ChunkBytes("IDAT", idat));
        stream.Write(ChunkBytes("IEND", []));
        return stream.ToArray();
    }

    private static byte[] BuildIhdr(int width, int height)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), (uint)height);
        data[8] = 8; // bit depth
        data[9] = 6; // color type: truecolor with alpha
        data[10] = 0; // compression method
        data[11] = 0; // filter method
        data[12] = 0; // interlace method
        return data;
    }

    private static byte[] BuildRawScanlines(int width, int height)
    {
        int stride = 1 + (width * 4);
        var raw = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            for (int x = 0; x < width * 4; x++)
            {
                raw[rowStart + 1 + x] = (byte)((x + y) % 256);
            }
        }

        return raw;
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return output.ToArray();
    }

    private static byte[] ChunkBytes(string type, byte[] data)
    {
        var chunk = new byte[8 + data.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, chunk.AsSpan(4, 4));
        data.CopyTo(chunk.AsSpan(8));
        uint crc = Crc32(chunk.AsSpan(4, 4 + data.Length));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), crc);
        return chunk;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in bytes)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }
}
