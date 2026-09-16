using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text;
using AcDream.Core.Textures;
using StbImageSharp;

namespace AcDream.Core.Plugins;

public sealed class PluginIconException : Exception
{
    public PluginIconException(string message) : base(message) { }
}

/// <summary>The client's own copy of the <c>icon.png</c> header rules (L-317). Written apart
/// from <c>LauncherPluginIcon</c> because neither project may reference the other (L-303); a
/// parity test pins the two to the same verdicts. Unlike the launcher, this side also decodes
/// the file, so a header that passes here can still fail to decode and fall back to no icon.</summary>
public static class PluginIconFile
{
    public const string FileName = "icon.png";
    public const int Extent = 64;
    public const int MaximumBytes = 64 * 1024;

    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static void ValidateHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes)
            throw new PluginIconException("the icon is larger than 64 KiB");
        if (bytes.Length < Signature.Length || !bytes[..Signature.Length].SequenceEqual(Signature))
            throw new PluginIconException("the icon is not a PNG file");

        long cursor = Signature.Length;
        Chunk ihdr = ReadChunk(bytes, ref cursor);
        if (ihdr.Type != "IHDR" || ihdr.Length != 13)
            throw new PluginIconException("the icon's first chunk is not a well-formed IHDR");

        ReadOnlySpan<byte> fields = bytes.Slice((int)ihdr.DataOffset, 13);
        uint width = BinaryPrimitives.ReadUInt32BigEndian(fields);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(fields[4..]);
        byte bitDepth = fields[8];
        byte colorType = fields[9];
        byte compression = fields[10];
        byte filter = fields[11];
        byte interlace = fields[12];

        if (width != Extent || height != Extent)
            throw new PluginIconException($"the icon must be exactly {Extent}x{Extent} pixels");
        if (compression != 0 || filter != 0 || interlace > 1 || !IsSpecCompliantDepth(colorType, bitDepth))
            throw new PluginIconException("the icon's IHDR is outside the PNG specification");

        using var idat = new MemoryStream();
        bool sawIend = false;
        while (!sawIend)
        {
            if (cursor >= bytes.Length)
                throw new PluginIconException("the icon has no IEND chunk");

            Chunk chunk = ReadChunk(bytes, ref cursor);
            if (chunk.Type == "acTL")
                throw new PluginIconException("the icon must not be animated");
            if (chunk.Type == "IDAT")
                idat.Write(bytes.Slice((int)chunk.DataOffset, (int)chunk.Length));
            sawIend = chunk.Type == "IEND";
        }

        if (cursor != bytes.Length)
            throw new PluginIconException("the icon has data after its IEND chunk");

        RefuseOversizedInflate(idat.ToArray(), RawPixelBytes(width, height, colorType, bitDepth, interlace));
    }

    /// <summary>Decodes <c>icon.png</c> from a plugin's install folder. Never throws: any rule
    /// violation, missing file or decode failure yields <see langword="false"/> so a caller
    /// falls back instead of failing the plugin.</summary>
    public static bool TryLoad(string pluginDirectory, [NotNullWhen(true)] out DecodedTexture? texture)
    {
        texture = null;
        try
        {
            var file = new FileInfo(Path.Combine(pluginDirectory, FileName));
            if (!file.Exists || file.Length > MaximumBytes)
                return false;

            byte[] bytes = File.ReadAllBytes(file.FullName);
            ValidateHeader(bytes);

            ImageResult decoded = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
            if (decoded.Width != Extent || decoded.Height != Extent)
                return false;

            texture = new DecodedTexture(decoded.Data, decoded.Width, decoded.Height);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct Chunk(long Length, string Type, long DataOffset);

    private static Chunk ReadChunk(ReadOnlySpan<byte> bytes, ref long offset)
    {
        if (offset + 8 > bytes.Length)
            throw new PluginIconException("the icon's PNG data is truncated");

        uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice((int)offset, 4));
        if (declaredLength > int.MaxValue)
            throw new PluginIconException("the icon's PNG chunk length is out of range");

        string type = Encoding.ASCII.GetString(bytes.Slice((int)offset + 4, 4));
        long dataOffset = offset + 8;
        long next = dataOffset + declaredLength + 4;
        if (next > bytes.Length)
            throw new PluginIconException("the icon's PNG data is truncated");

        offset = next;
        return new Chunk(declaredLength, type, dataOffset);
    }

    private static bool IsSpecCompliantDepth(byte colorType, byte bitDepth) => colorType switch
    {
        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
        2 => bitDepth is 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        4 => bitDepth is 8 or 16,
        6 => bitDepth is 8 or 16,
        _ => false,
    };

    private static long RawPixelBytes(
        uint width,
        uint height,
        byte colorType,
        byte bitDepth,
        byte interlace)
    {
        int channels = colorType switch
        {
            0 or 3 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => throw new PluginIconException("the icon's IHDR is outside the PNG specification"),
        };
        int bitsPerPixel = channels * bitDepth;

        if (interlace == 0)
            return StrideFor(width, bitsPerPixel) * height;

        long sum = 0;
        foreach ((int startX, int startY, int stepX, int stepY) in Adam7Grid)
        {
            long passWidth = width > startX ? (width - startX + stepX - 1) / stepX : 0;
            long passHeight = height > startY ? (height - startY + stepY - 1) / stepY : 0;
            if (passWidth != 0 && passHeight != 0)
                sum += StrideFor((uint)passWidth, bitsPerPixel) * passHeight;
        }
        return sum;
    }

    private static readonly (int, int, int, int)[] Adam7Grid =
    [
        (0, 0, 8, 8),
        (4, 0, 8, 8),
        (0, 4, 4, 8),
        (2, 0, 4, 4),
        (0, 2, 2, 4),
        (1, 0, 2, 2),
        (0, 1, 1, 2),
    ];

    private static long StrideFor(uint width, int bitsPerPixel) =>
        1 + (((long)width * bitsPerPixel) + 7) / 8;

    private static void RefuseOversizedInflate(byte[] idatPayload, long rawLimit)
    {
        var buffer = new byte[rawLimit + 1];
        int filled = 0;
        try
        {
            using var compressed = new MemoryStream(idatPayload);
            using var inflated = new ZLibStream(compressed, CompressionMode.Decompress);
            int read;
            while (filled < buffer.Length
                && (read = inflated.Read(buffer, filled, buffer.Length - filled)) > 0)
            {
                filled += read;
            }
        }
        catch (InvalidDataException)
        {
            throw new PluginIconException("the icon's image data does not inflate");
        }

        if (filled > rawLimit)
            throw new PluginIconException("the icon's image data is larger than its declared size");
    }
}
