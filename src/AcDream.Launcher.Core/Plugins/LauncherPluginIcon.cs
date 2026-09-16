using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Plugins;

/// <summary>The header rules for a plugin's optional <c>icon.png</c> (L-317). Checks only the PNG
/// container, so a valid header can still hide corrupt pixel data; that is left to whichever side
/// actually decodes the file.</summary>
public static class LauncherPluginIcon
{
    public const string FileName = "icon.png";
    public const int Extent = 64;
    public const int MaximumBytes = 64 * 1024;

    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static void Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes)
        {
            throw new LauncherUpdateException(
                $"The plugin icon is larger than {MaximumBytes} bytes.");
        }

        if (bytes.Length < Signature.Length
            || !bytes[..Signature.Length].SequenceEqual(Signature))
        {
            throw new LauncherUpdateException("The plugin icon is not a PNG file.");
        }

        long offset = Signature.Length;
        (long length, string type, long dataOffset) = ReadChunkHeader(bytes, offset);
        if (type != "IHDR" || length != 13)
        {
            throw new LauncherUpdateException("The plugin icon's PNG header is malformed.");
        }

        ReadOnlySpan<byte> ihdr = bytes.Slice((int)dataOffset, 13);
        uint width = BinaryPrimitives.ReadUInt32BigEndian(ihdr);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(ihdr[4..]);
        byte bitDepth = ihdr[8];
        byte colorType = ihdr[9];
        byte interlace = ihdr[12];
        if (width != Extent || height != Extent)
        {
            throw new LauncherUpdateException(
                $"The plugin icon must be exactly {Extent}x{Extent} pixels.");
        }

        // Only the PNG specification's combinations, so the inflate bound below is a real 64x64 size.
        if (!IsAllowedDepth(colorType, bitDepth) || ihdr[10] != 0 || ihdr[11] != 0 || interlace > 1)
        {
            throw new LauncherUpdateException("The plugin icon's PNG header is malformed.");
        }

        offset = dataOffset + length + 4;
        using var idat = new MemoryStream();
        bool sawEnd = false;
        while (!sawEnd)
        {
            if (offset >= bytes.Length)
            {
                throw new LauncherUpdateException("The plugin icon has no IEND chunk.");
            }

            (length, type, dataOffset) = ReadChunkHeader(bytes, offset);
            if (type == "acTL")
            {
                throw new LauncherUpdateException("The plugin icon must not be animated.");
            }

            if (type == "IDAT")
            {
                idat.Write(bytes.Slice((int)dataOffset, (int)length));
            }

            offset = dataOffset + length + 4;
            sawEnd = type == "IEND";
        }

        if (offset != bytes.Length)
        {
            throw new LauncherUpdateException("The plugin icon has data after its IEND chunk.");
        }

        long expected = ExpectedRawSize(width, height, colorType, bitDepth, interlace);
        ValidateInflatedSize(idat.ToArray(), expected);
    }

    private static (long Length, string Type, long DataOffset) ReadChunkHeader(
        ReadOnlySpan<byte> bytes,
        long offset)
    {
        if (offset + 8 > bytes.Length)
        {
            throw new LauncherUpdateException("The plugin icon's PNG data is truncated.");
        }

        uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice((int)offset, 4));
        if (declaredLength > int.MaxValue)
        {
            throw new LauncherUpdateException("The plugin icon's PNG chunk length is invalid.");
        }

        long length = declaredLength;
        long dataOffset = offset + 8;
        if (dataOffset + length + 4 > bytes.Length)
        {
            throw new LauncherUpdateException("The plugin icon's PNG data is truncated.");
        }

        string type = Encoding.ASCII.GetString(bytes.Slice((int)offset + 4, 4));
        return (length, type, dataOffset);
    }

    private static bool IsAllowedDepth(byte colorType, byte bitDepth) => colorType switch
    {
        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        2 or 4 or 6 => bitDepth is 8 or 16,
        _ => false,
    };

    private static long ExpectedRawSize(
        uint width,
        uint height,
        byte colorType,
        byte bitDepth,
        byte interlace)
    {
        int channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new LauncherUpdateException("The plugin icon's PNG header is malformed."),
        };
        int bitsPerPixel = channels * bitDepth;

        if (interlace == 0)
        {
            return ScanlineBytes(width, bitsPerPixel) * height;
        }

        long total = 0;
        foreach ((int xStart, int yStart, int xStep, int yStep) in Adam7Passes)
        {
            long passWidth = width > xStart ? (width - xStart + xStep - 1) / xStep : 0;
            long passHeight = height > yStart ? (height - yStart + yStep - 1) / yStep : 0;
            if (passWidth > 0 && passHeight > 0)
            {
                total += ScanlineBytes((uint)passWidth, bitsPerPixel) * passHeight;
            }
        }

        return total;
    }

    private static readonly (int XStart, int YStart, int XStep, int YStep)[] Adam7Passes =
    [
        (0, 0, 8, 8),
        (4, 0, 8, 8),
        (0, 4, 4, 8),
        (2, 0, 4, 4),
        (0, 2, 2, 4),
        (1, 0, 2, 2),
        (0, 1, 1, 2),
    ];

    private static long ScanlineBytes(uint width, int bitsPerPixel) =>
        1 + (((long)width * bitsPerPixel) + 7) / 8;

    private static void ValidateInflatedSize(byte[] idatPayload, long expected)
    {
        using var compressed = new MemoryStream(idatPayload);
        var buffer = new byte[expected + 1];
        long total = 0;
        try
        {
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            int read;
            while (total < buffer.Length
                && (read = zlib.Read(buffer, (int)total, (int)(buffer.Length - total))) > 0)
            {
                total += read;
            }
        }
        catch (InvalidDataException)
        {
            throw new LauncherUpdateException("The plugin icon's image data is invalid.");
        }

        if (total > expected)
        {
            throw new LauncherUpdateException(
                "The plugin icon's image data is larger than its declared size.");
        }
    }
}
