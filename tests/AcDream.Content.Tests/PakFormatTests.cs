using System.IO;
using AcDream.Content.Pak;

namespace AcDream.Content.Tests;

public class PakFormatTests {
    [Fact]
    public void Header_Size_Is64Bytes() {
        Assert.Equal(64, PakHeader.Size);
    }

    [Fact]
    public void FormatVersion_StaysAtTwo_AcrossTheS5C4RecipeBump() {
        Assert.Equal(2u, PakFormat.CurrentFormatVersion);
        Assert.Equal(10u, PakFormat.CurrentBakeToolVersion);
    }

    [Fact]
    public void TocEntry_Size_Is24Bytes() {
        Assert.Equal(24, PakTocEntry.Size);
    }

    [Fact]
    public void Header_Magic_IsAcpkLittleEndian() {
        Assert.Equal(0x4B504341u, PakHeader.MagicValue);
    }

    [Fact]
    public void Header_WriteThenRead_RoundTripsEveryField() {
        var header = new PakHeader {
            FormatVersion = 1,
            PortalIteration = 111,
            CellIteration = 222,
            HighResIteration = 333,
            LanguageIteration = 444,
            TocOffset = 0x1_0000_0002UL,
            TocCount = 777,
            BakeToolVersion = 1,
        };

        using var ms = new MemoryStream();
        header.WriteTo(ms);
        Assert.Equal(PakHeader.Size, ms.Length);

        ms.Position = 0;
        var readBack = PakHeader.ReadFrom(ms);

        Assert.Equal(PakHeader.MagicValue, readBack.Magic);
        Assert.Equal(header.FormatVersion, readBack.FormatVersion);
        Assert.Equal(header.PortalIteration, readBack.PortalIteration);
        Assert.Equal(header.CellIteration, readBack.CellIteration);
        Assert.Equal(header.HighResIteration, readBack.HighResIteration);
        Assert.Equal(header.LanguageIteration, readBack.LanguageIteration);
        Assert.Equal(header.TocOffset, readBack.TocOffset);
        Assert.Equal(header.TocCount, readBack.TocCount);
        Assert.Equal(header.BakeToolVersion, readBack.BakeToolVersion);
    }

    [Fact]
    public void Header_ReadFrom_Span_RoundTrips() {
        var header = new PakHeader {
            FormatVersion = 1,
            PortalIteration = 5,
            CellIteration = 6,
            HighResIteration = 7,
            LanguageIteration = 8,
            TocOffset = 999,
            TocCount = 3,
            BakeToolVersion = 1,
        };
        Span<byte> buf = stackalloc byte[PakHeader.Size];
        header.WriteTo(buf);
        var readBack = PakHeader.ReadFrom((ReadOnlySpan<byte>)buf);
        Assert.Equal(header.CellIteration, readBack.CellIteration);
        Assert.Equal(header.TocOffset, readBack.TocOffset);
    }

    [Fact]
    public void Header_ReservedBytes_AreZero() {
        var header = new PakHeader { FormatVersion = 1, BakeToolVersion = 1 };
        Span<byte> buf = stackalloc byte[PakHeader.Size];
        header.WriteTo(buf);
        // offset 40, length 24 per the normative layout
        for (int i = 40; i < 64; i++) {
            Assert.Equal(0, buf[i]);
        }
    }

    [Fact]
    public void Header_FieldOffsets_MatchNormativeLayout() {
        var header = new PakHeader {
            FormatVersion = 0x11111111,
            PortalIteration = 0x22222222,
            CellIteration = 0x33333333,
            HighResIteration = 0x44444444,
            LanguageIteration = 0x55555555,
            TocOffset = 0x6666666677777777UL,
            TocCount = 0x88888888,
            BakeToolVersion = 0x99999999,
        };
        Span<byte> buf = stackalloc byte[PakHeader.Size];
        header.WriteTo(buf);

        Assert.Equal(PakHeader.MagicValue, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]));
        Assert.Equal(0x11111111u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf[4..8]));
        Assert.Equal(0x22222222u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf[8..12]));
        Assert.Equal(0x33333333u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf[12..16]));
        Assert.Equal(0x44444444u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf[16..20]));
        Assert.Equal(0x55555555u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf[20..24]));
        Assert.Equal(0x6666666677777777UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buf[24..32]));
        Assert.Equal(0x88888888u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf[32..36]));
        Assert.Equal(0x99999999u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf[36..40]));
    }

    [Fact]
    public void TocEntry_WriteThenRead_RoundTrips() {
        var entry = new PakTocEntry {
            Key = PakKey.Compose(PakAssetType.GfxObjMesh, 0x010002B4u),
            Offset = 0x4000,
            Length = 12345,
            Crc32 = 0xDEADBEEF,
        };
        Span<byte> buf = stackalloc byte[PakTocEntry.Size];
        entry.WriteTo(buf);
        var readBack = PakTocEntry.ReadFrom((ReadOnlySpan<byte>)buf);

        Assert.Equal(entry.Key, readBack.Key);
        Assert.Equal(entry.Offset, readBack.Offset);
        Assert.Equal(entry.Length, readBack.Length);
        Assert.Equal(entry.Crc32, readBack.Crc32);
    }

    [Fact]
    public void TocEntry_FieldOffsets_MatchNormativeLayout() {
        var entry = new PakTocEntry {
            Key = 0x1111111122222222UL,
            Offset = 0x3333333344444444UL,
            Length = 0x55555555,
            Crc32 = 0x66666666,
        };
        Span<byte> buf = stackalloc byte[PakTocEntry.Size];
        entry.WriteTo(buf);

        Assert.Equal(0x1111111122222222UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buf[0..8]));
        Assert.Equal(0x3333333344444444UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buf[8..16]));
        Assert.Equal(0x55555555u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf[16..20]));
        Assert.Equal(0x66666666u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf[20..24]));
    }
}
