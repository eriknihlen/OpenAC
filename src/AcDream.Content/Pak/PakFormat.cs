using System;
using System.Buffers.Binary;
using System.IO;

namespace AcDream.Content.Pak;

public static class PakFormat {
    public const uint CurrentFormatVersion = 2;

    public const uint CurrentBakeToolVersion = 10;
}

public struct PakHeader {
    public const int Size = 64;
    public const uint MagicValue = 0x4B504341u; // 'ACPK' little-endian

    public uint Magic { get; private set; } = MagicValue;

    public uint FormatVersion;
    public uint PortalIteration;
    public uint CellIteration;
    public uint HighResIteration;
    public uint LanguageIteration;
    public ulong TocOffset;
    public uint TocCount;
    public uint BakeToolVersion;

    public PakHeader() { }

    public void WriteTo(Span<byte> dest) {
        if (dest.Length < Size) throw new ArgumentException($"destination must be at least {Size} bytes", nameof(dest));
        BinaryPrimitives.WriteUInt32LittleEndian(dest[0..4], MagicValue);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[4..8], FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[8..12], PortalIteration);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[12..16], CellIteration);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[16..20], HighResIteration);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[20..24], LanguageIteration);
        BinaryPrimitives.WriteUInt64LittleEndian(dest[24..32], TocOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[32..36], TocCount);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[36..40], BakeToolVersion);
        dest[40..64].Clear(); // reserved, zero
    }

    public void WriteTo(Stream stream) {
        Span<byte> buf = stackalloc byte[Size];
        WriteTo(buf);
        stream.Write(buf);
    }

    public static PakHeader ReadFrom(ReadOnlySpan<byte> src) {
        if (src.Length < Size) throw new ArgumentException($"source must be at least {Size} bytes", nameof(src));
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(src[0..4]);
        if (magic != MagicValue) {
            throw new InvalidDataException($"pak header magic mismatch: expected 0x{MagicValue:X8}, got 0x{magic:X8}");
        }
        return new PakHeader {
            FormatVersion = BinaryPrimitives.ReadUInt32LittleEndian(src[4..8]),
            PortalIteration = BinaryPrimitives.ReadUInt32LittleEndian(src[8..12]),
            CellIteration = BinaryPrimitives.ReadUInt32LittleEndian(src[12..16]),
            HighResIteration = BinaryPrimitives.ReadUInt32LittleEndian(src[16..20]),
            LanguageIteration = BinaryPrimitives.ReadUInt32LittleEndian(src[20..24]),
            TocOffset = BinaryPrimitives.ReadUInt64LittleEndian(src[24..32]),
            TocCount = BinaryPrimitives.ReadUInt32LittleEndian(src[32..36]),
            BakeToolVersion = BinaryPrimitives.ReadUInt32LittleEndian(src[36..40]),
        };
    }

    public static PakHeader ReadFrom(Stream stream) {
        Span<byte> buf = stackalloc byte[Size];
        stream.ReadExactly(buf);
        return ReadFrom((ReadOnlySpan<byte>)buf);
    }
}

public struct PakTocEntry {
    public const int Size = 24;
    public const uint CompressionFlag = 0x8000_0000u;
    public const uint StoredLengthMask = 0x7FFF_FFFFu;

    public ulong Key;
    public ulong Offset;
    public uint Length;
    public uint Crc32;

    public readonly bool IsCompressed => (Length & CompressionFlag) != 0;

    public readonly uint StoredLength => Length & StoredLengthMask;

    public void WriteTo(Span<byte> dest) {
        if (dest.Length < Size) throw new ArgumentException($"destination must be at least {Size} bytes", nameof(dest));
        BinaryPrimitives.WriteUInt64LittleEndian(dest[0..8], Key);
        BinaryPrimitives.WriteUInt64LittleEndian(dest[8..16], Offset);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[16..20], Length);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[20..24], Crc32);
    }

    public void WriteTo(Stream stream) {
        Span<byte> buf = stackalloc byte[Size];
        WriteTo(buf);
        stream.Write(buf);
    }

    public static PakTocEntry ReadFrom(ReadOnlySpan<byte> src) {
        if (src.Length < Size) throw new ArgumentException($"source must be at least {Size} bytes", nameof(src));
        return new PakTocEntry {
            Key = BinaryPrimitives.ReadUInt64LittleEndian(src[0..8]),
            Offset = BinaryPrimitives.ReadUInt64LittleEndian(src[8..16]),
            Length = BinaryPrimitives.ReadUInt32LittleEndian(src[16..20]),
            Crc32 = BinaryPrimitives.ReadUInt32LittleEndian(src[20..24]),
        };
    }
}
