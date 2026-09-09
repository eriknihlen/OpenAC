using System.Buffers.Binary;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class ObjDescEventTests
{
    [Fact]
    public void TryParse_RejectsWrongOpcode()
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0xF745u);
        Assert.Null(ObjDescEvent.TryParse(body));
    }

    [Fact]
    public void TryParse_RejectsTruncatedBody()
    {
        Assert.Null(ObjDescEvent.TryParse(new byte[3]));
    }

    [Fact]
    public void TryParse_SynthesizedBody_ExtractsGuidAndModelData()
    {
        var bytes = new List<byte>();
        AppendU32(bytes, ObjDescEvent.Opcode);
        AppendU32(bytes, 0x50000001u); // target guid

        // ModelData header: marker, subPalCount, texCount, animPartCount.
        bytes.Add(0x11);
        bytes.Add(3); // subPalCount
        bytes.Add(4); // texChangeCount
        bytes.Add(0); // animPartCount

        // BasePaletteId (palette type prefix stripped before packing).
        AppendPackedDword(bytes, 0x0400007Eu, 0x04000000u);

        // SubPalettes — three skin/hair-style overlays at varied offsets.
        AppendPackedDword(bytes, 0x04001FE3u, 0x04000000u);
        bytes.Add(24); bytes.Add(8);
        AppendPackedDword(bytes, 0x040002BAu, 0x04000000u);
        bytes.Add(0); bytes.Add(24);
        AppendPackedDword(bytes, 0x040002BCu, 0x04000000u);
        bytes.Add(32); bytes.Add(8);

        // TextureChanges — four part textures.
        for (byte partIdx = 0; partIdx < 4; partIdx++)
        {
            bytes.Add(partIdx);
            AppendPackedDword(bytes, 0x05000100u + partIdx, 0x05000000u);
            AppendPackedDword(bytes, 0x05000200u + partIdx, 0x05000000u);
        }

        // 4-byte align after AnimPartChanges (none here, so just align).
        while (bytes.Count % 4 != 0) bytes.Add(0);

        // Trailing PhysicsTimestampPack: two u16 values.
        AppendU16(bytes, 0x5678);
        AppendU16(bytes, 0xDEF0);

        var parsed = ObjDescEvent.TryParse(bytes.ToArray());

        Assert.NotNull(parsed);
        Assert.Equal(0x50000001u, parsed!.Value.Guid);
        Assert.Equal((ushort)0x5678, parsed.Value.InstanceSequence);
        Assert.Equal((ushort)0xDEF0, parsed.Value.ObjDescSequence);

        var md = parsed.Value.ModelData;
        Assert.Equal(0x0400007Eu, md.BasePaletteId);
        Assert.Equal(3, md.SubPalettes.Count);
        Assert.Equal(0x04001FE3u, md.SubPalettes[0].SubPaletteId);
        Assert.Equal(24, md.SubPalettes[0].Offset);
        Assert.Equal(8, md.SubPalettes[0].Length);
        Assert.Equal(0x040002BAu, md.SubPalettes[1].SubPaletteId);
        Assert.Equal(0, md.SubPalettes[1].Offset);
        Assert.Equal(24, md.SubPalettes[1].Length);

        Assert.Equal(4, md.TextureChanges.Count);
        Assert.Equal(0, md.TextureChanges[0].PartIndex);
        Assert.Equal(0x05000100u, md.TextureChanges[0].OldTexture);
        Assert.Equal(0x05000200u, md.TextureChanges[0].NewTexture);
        Assert.Equal(3, md.TextureChanges[3].PartIndex);

        Assert.Empty(md.AnimPartChanges);

        Assert.Null(ObjDescEvent.TryParse(bytes.ToArray()[..^1]));
        Assert.Null(ObjDescEvent.TryParse([.. bytes, 0]));
    }

    [Fact]
    public void ReadModelData_SameOutputFromBothCallers()
    {
        // Bare ModelData block — used as a substring in both messages.
        var modelDataBytes = new List<byte>();
        modelDataBytes.Add(0x11);
        modelDataBytes.Add(1); // subPalCount
        modelDataBytes.Add(0); // texCount
        modelDataBytes.Add(0); // animPartCount
        AppendPackedDword(modelDataBytes, 0x0400007Eu, 0x04000000u);
        AppendPackedDword(modelDataBytes, 0x04001084u, 0x04000000u);
        modelDataBytes.Add(80); modelDataBytes.Add(12);
        while (modelDataBytes.Count % 4 != 0) modelDataBytes.Add(0);

        ReadOnlySpan<byte> span = modelDataBytes.ToArray();
        int pos = 0;
        var md = CreateObject.ReadModelData(span, ref pos);

        Assert.Equal(0x0400007Eu, md.BasePaletteId);
        Assert.Single(md.SubPalettes);
        Assert.Equal(0x04001084u, md.SubPalettes[0].SubPaletteId);
        Assert.Equal(80, md.SubPalettes[0].Offset);
        Assert.Equal(12, md.SubPalettes[0].Length);
    }

    private static void AppendU32(List<byte> dest, uint value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, value);
        dest.AddRange(tmp.ToArray());
    }

    private static void AppendU16(List<byte> dest, ushort value)
    {
        Span<byte> tmp = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(tmp, value);
        dest.AddRange(tmp.ToArray());
    }

    private static void AppendPackedDword(List<byte> dest, uint value, uint knownType)
    {
        uint packed = (value & 0xFF000000u) == knownType ? (value & ~knownType) : value;
        if (packed <= 0x7FFFu)
        {
            Span<byte> tmp = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(tmp, (ushort)packed);
            dest.AddRange(tmp.ToArray());
        }
        else
        {
            ushort high = (ushort)((packed >> 16) | 0x8000);
            ushort low  = (ushort)(packed & 0xFFFFu);
            Span<byte> tmp = stackalloc byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(tmp, high);
            BinaryPrimitives.WriteUInt16LittleEndian(tmp.Slice(2), low);
            dest.AddRange(tmp.ToArray());
        }
    }
}
