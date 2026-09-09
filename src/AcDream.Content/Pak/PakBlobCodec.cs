using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace AcDream.Content.Pak;

internal static class PakBlobCodec
{
    internal const int MaximumDecodedBytes = 64 * 1024 * 1024;
    private const int DecodedLengthPrefixSize = sizeof(uint);
    private const int MinimumCompressionBytes = 512;
    private const int MinimumSavingsBytes = 64;
    private const int BrotliQuality = 1;
    private const int BrotliWindow = 22;

    internal readonly record struct Encoded(byte[] Bytes, bool Compressed);

    public static bool TryCompress(
        ReadOnlySpan<byte> decoded,
        out byte[]? rented,
        out int storedLength)
    {
        if (decoded.Length > MaximumDecodedBytes)
            throw new InvalidDataException(
                $"pak payload is {decoded.Length} bytes; maximum is {MaximumDecodedBytes}");

        rented = null;
        storedLength = decoded.Length;
        if (decoded.Length < MinimumCompressionBytes)
            return false;

        int maximum = checked(
            DecodedLengthPrefixSize
            + BrotliEncoder.GetMaxCompressedLength(decoded.Length));
        byte[] candidate = ArrayPool<byte>.Shared.Rent(maximum);
        if (!BrotliEncoder.TryCompress(
                decoded,
                candidate.AsSpan(DecodedLengthPrefixSize, maximum - DecodedLengthPrefixSize),
                out int compressedLength,
                BrotliQuality,
                BrotliWindow))
        {
            ArrayPool<byte>.Shared.Return(candidate);
            return false;
        }

        storedLength = checked(DecodedLengthPrefixSize + compressedLength);
        int requiredSavings = Math.Max(
            MinimumSavingsBytes,
            decoded.Length / 16);
        if (decoded.Length - storedLength < requiredSavings)
        {
            ArrayPool<byte>.Shared.Return(candidate);
            storedLength = decoded.Length;
            return false;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            candidate.AsSpan(0, DecodedLengthPrefixSize),
            checked((uint)decoded.Length));
        rented = candidate;
        return true;
    }

    public static Encoded Encode(byte[] decoded)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        if (!TryCompress(decoded, out byte[]? rented, out int storedLength))
            return new Encoded(decoded, false);

        byte[] buffer = rented
            ?? throw new InvalidOperationException("compressed buffer was not returned");
        try
        {
            return new Encoded(buffer.AsSpan(0, storedLength).ToArray(), true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static byte[] Decode(ReadOnlySpan<byte> stored)
    {
        if (stored.Length < DecodedLengthPrefixSize)
            throw new InvalidDataException(
                "compressed pak blob is missing its decoded-length prefix");

        uint decodedLength = BinaryPrimitives.ReadUInt32LittleEndian(
            stored[..DecodedLengthPrefixSize]);
        if (decodedLength > MaximumDecodedBytes)
            throw new InvalidDataException(
                $"compressed pak blob declares unsupported decoded length {decodedLength}");

        var decoded = new byte[checked((int)decodedLength)];
        if (!BrotliDecoder.TryDecompress(
                stored[DecodedLengthPrefixSize..],
                decoded,
                out int written)
            || written != decoded.Length)
        {
            throw new InvalidDataException(
                $"compressed pak blob did not decode to its declared {decoded.Length} bytes");
        }

        return decoded;
    }
}
