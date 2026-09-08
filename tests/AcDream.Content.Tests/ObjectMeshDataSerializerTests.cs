using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using AcDream.Content.Pak;
using AcDream.Core.Meshing;
using Chorizite.Core.Lib;
using Chorizite.Core.Render.Enums;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using CullMode = DatReaderWriter.Enums.CullMode;
using StipplingType = DatReaderWriter.Enums.StipplingType;
using EmitterType = DatReaderWriter.Enums.EmitterType;
using ParticleType = DatReaderWriter.Enums.ParticleType;

namespace AcDream.Content.Tests;

public class ObjectMeshDataSerializerTests {
    // ---- fixture builders ---------------------------------------------------

    private static ObjectMeshData EmptyObject() => new() {
        ObjectId = 0x0100_0001u,
        IsSetup = false,
    };

    private static ObjectMeshData VerticesAndIndicesOnly() {
        var data = new ObjectMeshData {
            ObjectId = 0x0100_0002u,
            IsSetup = false,
            Vertices = new[] {
                new VertexPositionNormalTexture(new Vector3(1, 2, 3), new Vector3(0, 0, 1), new Vector2(0, 0)),
                new VertexPositionNormalTexture(new Vector3(4, 5, 6), new Vector3(0, 1, 0), new Vector2(1, 0)),
                new VertexPositionNormalTexture(new Vector3(7, 8, 9), new Vector3(1, 0, 0), new Vector2(1, 1)),
            },
            BoundingBox = new BoundingBox(new Vector3(1, 2, 3), new Vector3(7, 8, 9)),
            SortCenter = new Vector3(4, 5, 6),
            DIDDegrade = 0x11223344,
        };
        data.Batches.Add(new MeshBatchData {
            Indices = new ushort[] { 0, 1, 2 },
            TextureFormat = (64, 64, TextureFormat.RGBA8),
            TextureKey = new TextureKey { SurfaceId = 0x08000001, PaletteId = 0x04000001, Stippling = StipplingType.Both, IsSolid = true },
            TextureIndex = 0,
            TextureData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            UploadPixelFormat = AcDream.Content.UploadPixelFormat.Rgba,
            UploadPixelType = AcDream.Content.UploadPixelType.UnsignedByte,
            CullMode = CullMode.Clockwise,
        });
        return data;
    }

    private static ObjectMeshData MultipleTextureBatchGroups() {
        var data = EmptyObject();
        data.ObjectId = 0x0100_0003u;

        TextureBatchData Batch(uint surfaceId, string tag) => new() {
            Key = new TextureKey { SurfaceId = surfaceId, PaletteId = 1, Stippling = StipplingType.Positive, IsSolid = false },
            TextureData = System.Text.Encoding.ASCII.GetBytes(tag),
            UploadPixelFormat = AcDream.Content.UploadPixelFormat.Rgba,
            UploadPixelType = AcDream.Content.UploadPixelType.UnsignedByte,
            Indices = new List<ushort> { 0, 1, 2, 2, 3, 0 },
            CullMode = CullMode.CounterClockwise,
            Translucency = TranslucencyKind.InvAlpha,
            IsTransparent = true,
            IsAdditive = false,
            HasWrappingUVs = true,
        };

        data.TextureBatches[(32, 32, TextureFormat.RGBA8)] = new List<TextureBatchData> { Batch(1, "a"), Batch(2, "b") };
        data.TextureBatches[(64, 64, TextureFormat.DXT5)] = new List<TextureBatchData> { Batch(3, "c") };
        data.TextureBatches[(16, 16, TextureFormat.A8)] = new List<TextureBatchData> { Batch(4, "d"), Batch(5, "e"), Batch(6, "f") };
        return data;
    }

    private static ObjectMeshData SetupWithParts() {
        var data = EmptyObject();
        data.ObjectId = 0x0200_0001u;
        data.IsSetup = true;
        data.SetupParts.Add((0x0100_0010u, Matrix4x4.CreateTranslation(1, 2, 3)));
        data.SetupParts.Add((0x0100_0011u, Matrix4x4.CreateFromYawPitchRoll(0.1f, 0.2f, 0.3f)));
        return data;
    }

    private static ParticleEmitter BuildEmitter(uint id) => new() {
        Id = id,
        DataCategory = 0x2A,
        Unknown = 7,
        EmitterType = EmitterType.BirthratePerSec,
        ParticleType = ParticleType.Explode,
        GfxObjId = new QualifiedDataId<GfxObj> { DataId = 0x0100_0099u },
        HwGfxObjId = new QualifiedDataId<GfxObj> { DataId = 0x0100_009Au },
        Birthrate = 2.5,
        MaxParticles = 40,
        InitialParticles = 5,
        TotalParticles = 100,
        TotalSeconds = 3.0,
        Lifespan = 1.5,
        LifespanRand = 0.25,
        OffsetDir = new Vector3(0, 0, 1),
        MinOffset = 0.1f,
        MaxOffset = 0.5f,
        A = new Vector3(1, 0, 0),
        MinA = 0.9f,
        MaxA = 1.1f,
        B = new Vector3(0, 1, 0),
        MinB = 0.8f,
        MaxB = 1.2f,
        C = new Vector3(0, 0, 1),
        MinC = 0.7f,
        MaxC = 1.3f,
        StartScale = 0.5f,
        FinalScale = 1.5f,
        ScaleRand = 0.05f,
        StartTrans = 1f,
        FinalTrans = 0f,
        TransRand = 0.1f,
        IsParentLocal = true,
    };

    private static ObjectMeshData WithEmitters() {
        var data = EmptyObject();
        data.ObjectId = 0x0200_0002u;
        data.IsSetup = true;
        data.ParticleEmitters.Add(new StagedEmitter {
            Emitter = BuildEmitter(0x2A00_0001u),
            PartIndex = 3,
            Offset = Matrix4x4.CreateTranslation(10, 20, 30),
        });
        data.ParticleEmitters.Add(new StagedEmitter {
            Emitter = BuildEmitter(0x2A00_0002u),
            PartIndex = 0,
            Offset = Matrix4x4.Identity,
        });
        return data;
    }

    private static ObjectMeshData WithNullableFieldsPresent() {
        var data = EmptyObject();
        data.ObjectId = 0x0300_0001u;
        data.SelectionSphere = new Sphere { Origin = new Vector3(1, 1, 1), Radius = 2.5f };
        data.Batches.Add(new MeshBatchData {
            Indices = new ushort[] { 0 },
            UploadPixelFormat = AcDream.Content.UploadPixelFormat.Rgba,
            UploadPixelType = AcDream.Content.UploadPixelType.UnsignedByte,
        });
        return data;
    }

    private static ObjectMeshData WithNullableFieldsAbsent() {
        var data = EmptyObject();
        data.ObjectId = 0x0300_0002u;
        data.SelectionSphere = null;
        data.Batches.Add(new MeshBatchData {
            Indices = new ushort[] { 0 },
            UploadPixelFormat = null,
            UploadPixelType = null,
        });
        return data;
    }

    private static ObjectMeshData WithEdgeLines() {
        var data = EmptyObject();
        data.ObjectId = 0x0400_0001u;
        data.EdgeLines = new[] {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0),
            new Vector3(1, 0, 0), new Vector3(1, 1, 0),
        };
        return data;
    }

    private static ObjectMeshData WithNestedEnvCellGeometry() {
        var data = EmptyObject();
        data.ObjectId = 0x0D00_0001_0000_0100u | (1UL << 32);
        data.IsSetup = true;
        data.EnvCellGeometry = VerticesAndIndicesOnly();
        return data;
    }

    private static ObjectMeshData WithCellShellSubset() {
        var data = EmptyObject();
        data.ObjectId = 0x0700_0001u;
        data.TextureBatches[(64, 64, TextureFormat.RGBA8)] = new List<TextureBatchData> {
            new() {
                Key = new TextureKey { SurfaceId = 0x08000BFFu, PaletteId = 0, Stippling = StipplingType.None, IsSolid = false },
                TextureData = new byte[] { 1, 2, 3, 4 },
                Indices = new List<ushort> { 0, 1, 2 },
                CullMode = CullMode.Clockwise,
                Translucency = TranslucencyKind.Opaque,
                MaterialState = new RetailSetSurfaceMaterialState(
                    RetailSetSurfaceBlend.InverseAdditive,
                    RetailSetSurfaceAlphaTest.Paletted,
                    FogEnabled: false),
                IsTransparent = false,
                IsAdditive = false,
                HasWrappingUVs = false,
                SourceSurfaceIndex = 7,
                RetailSurfaceMask = 0b0000_1101,
                RawSurfaceType = 0x11u,
                SurfaceOpacity = 0.25f,
                IsCellShell = true,
            },
        };
        return data;
    }

    private static ObjectMeshData WithGfxObjNeutralDefaults() {
        var data = EmptyObject();
        data.ObjectId = 0x0700_0002u;
        data.TextureBatches[(32, 32, TextureFormat.RGBA8)] = new List<TextureBatchData> {
            new() {
                Key = new TextureKey { SurfaceId = 0x08000001u, PaletteId = 0, Stippling = StipplingType.Positive, IsSolid = true },
                TextureData = new byte[] { 9, 9, 9, 9 },
                Indices = new List<ushort> { 0, 1, 2 },
                CullMode = CullMode.None,
            },
        };
        return data;
    }

    public static IEnumerable<object[]> AllFixtures() {
        yield return new object[] { EmptyObject() };
        yield return new object[] { VerticesAndIndicesOnly() };
        yield return new object[] { MultipleTextureBatchGroups() };
        yield return new object[] { SetupWithParts() };
        yield return new object[] { WithEmitters() };
        yield return new object[] { WithNullableFieldsPresent() };
        yield return new object[] { WithNullableFieldsAbsent() };
        yield return new object[] { WithEdgeLines() };
        yield return new object[] { WithNestedEnvCellGeometry() };
        yield return new object[] { WithCellShellSubset() };
        yield return new object[] { WithGfxObjNeutralDefaults() };
    }

    [Fact]
    public void WithGfxObjNeutralDefaults_Fixture_HasClassLevelNeutralDefaults() {
        TextureBatchData batch = Assert.Single(Assert.Single(WithGfxObjNeutralDefaults().TextureBatches).Value);
        Assert.Equal(-1, batch.SourceSurfaceIndex);
        Assert.Equal((byte)0, batch.RetailSurfaceMask);
        Assert.Equal(0u, batch.RawSurfaceType);
        Assert.False(batch.IsCellShell);
    }

    // ---- round-trip tests ----------------------------------------------------

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void RoundTrip_PreservesEveryField(ObjectMeshData original) {
        using var ms = new MemoryStream();
        ObjectMeshDataSerializer.Write(original, ms);
        var bytes = ms.ToArray();

        var readBack = ObjectMeshDataSerializer.Read(bytes);

        ObjectMeshDataEquality.AssertEqual(original, readBack);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0.75f)]
    [InlineData(0.5f)]
    [InlineData(0.25f)]
    [InlineData(0f)]
    public void SurfaceOpacity_RoundTripsBitExactlyAndDeterministically(float opacity) {
        ObjectMeshData data = WithCellShellSubset();
        TextureBatchData batch = Assert.Single(Assert.Single(data.TextureBatches).Value);
        batch.SurfaceOpacity = opacity;

        using var first = new MemoryStream();
        ObjectMeshDataSerializer.Write(data, first);
        using var second = new MemoryStream();
        ObjectMeshDataSerializer.Write(data, second);

        Assert.Equal(first.ToArray(), second.ToArray());
        TextureBatchData read = Assert.Single(Assert.Single(
            ObjectMeshDataSerializer.Read(first.ToArray()).TextureBatches).Value);
        Assert.Equal(
            BitConverter.SingleToInt32Bits(opacity),
            BitConverter.SingleToInt32Bits(read.SurfaceOpacity));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, true)]
    [InlineData(2, 1, true)]
    [InlineData(3, 2, false)]
    [InlineData(4, 0, true)]
    [InlineData(5, 1, false)]
    [InlineData(6, 2, true)]
    public void SetSurfaceState_RoundTripsEveryPackedFieldDeterministically(
        int blendValue,
        int alphaTestValue,
        bool fogEnabled) {
        ObjectMeshData data = WithCellShellSubset();
        TextureBatchData batch = Assert.Single(Assert.Single(data.TextureBatches).Value);
        batch.MaterialState = new RetailSetSurfaceMaterialState(
            (RetailSetSurfaceBlend)blendValue,
            (RetailSetSurfaceAlphaTest)alphaTestValue,
            fogEnabled);

        using var first = new MemoryStream();
        ObjectMeshDataSerializer.Write(data, first);
        using var second = new MemoryStream();
        ObjectMeshDataSerializer.Write(data, second);

        Assert.Equal(first.ToArray(), second.ToArray());
        TextureBatchData read = Assert.Single(Assert.Single(
            ObjectMeshDataSerializer.Read(first.ToArray()).TextureBatches).Value);
        Assert.Equal(batch.MaterialState, read.MaterialState);
        Assert.Equal(batch.MaterialState.ToPackedByte(), read.MaterialState.ToPackedByte());
    }

    // ---- determinism -----------------------------------------------------

    [Fact]
    public void Serialize_SameInstanceTwice_ByteIdentical() {
        var data = MultipleTextureBatchGroups();

        using var ms1 = new MemoryStream();
        ObjectMeshDataSerializer.Write(data, ms1);

        using var ms2 = new MemoryStream();
        ObjectMeshDataSerializer.Write(data, ms2);

        Assert.Equal(ms1.ToArray(), ms2.ToArray());
    }

    [Fact]
    public void Serialize_DictionaryInsertedInDifferentOrders_ByteIdentical() {
        TextureBatchData Batch(uint surfaceId) => new() {
            Key = new TextureKey { SurfaceId = surfaceId, PaletteId = 1, Stippling = StipplingType.None, IsSolid = false },
            TextureData = new byte[] { (byte)surfaceId },
            Indices = new List<ushort> { 0, 1, 2 },
            CullMode = CullMode.None,
        };

        var a = EmptyObject();
        a.ObjectId = 0x0500_0001u;
        a.TextureBatches[(32, 32, TextureFormat.RGBA8)] = new List<TextureBatchData> { Batch(1) };
        a.TextureBatches[(64, 64, TextureFormat.DXT5)] = new List<TextureBatchData> { Batch(2) };
        a.TextureBatches[(16, 16, TextureFormat.A8)] = new List<TextureBatchData> { Batch(3) };

        var b = EmptyObject();
        b.ObjectId = 0x0500_0001u;
        b.TextureBatches[(16, 16, TextureFormat.A8)] = new List<TextureBatchData> { Batch(3) };
        b.TextureBatches[(32, 32, TextureFormat.RGBA8)] = new List<TextureBatchData> { Batch(1) };
        b.TextureBatches[(64, 64, TextureFormat.DXT5)] = new List<TextureBatchData> { Batch(2) };

        using var msA = new MemoryStream();
        ObjectMeshDataSerializer.Write(a, msA);
        using var msB = new MemoryStream();
        ObjectMeshDataSerializer.Write(b, msB);

        Assert.Equal(msA.ToArray(), msB.ToArray());
    }

    [Fact]
    public void Write_SortsTextureBatchesByWidthHeightFormatKeyTuple() {
        // Insert in scrambled order; the serialized bytes must reflect the
        // KEY-sorted order (Width, Height, Format), not insertion order.
        var data = EmptyObject();
        data.ObjectId = 0x0600_0001u;

        TextureBatchData Batch(byte[] marker) => new() {
            Key = default,
            TextureData = marker,
            Indices = new List<ushort>(),
            CullMode = CullMode.None,
        };

        byte[] markerA = { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA };
        byte[] markerB = { 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB };
        byte[] markerC = { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC };

        data.TextureBatches[(100, 1, TextureFormat.RGBA8)] = new List<TextureBatchData> { Batch(markerC) };
        data.TextureBatches[(1, 1, TextureFormat.RGBA8)] = new List<TextureBatchData> { Batch(markerA) };
        data.TextureBatches[(1, 100, TextureFormat.RGBA8)] = new List<TextureBatchData> { Batch(markerB) };

        using var ms = new MemoryStream();
        ObjectMeshDataSerializer.Write(data, ms);
        var bytes = ms.ToArray();

        // markerA (width=1,height=1) < markerB (width=1,height=100) <
        // markerC (width=100,height=1) in ascending (Width, Height) order.
        int iA = IndexOfSequence(bytes, markerA);
        int iB = IndexOfSequence(bytes, markerB);
        int iC = IndexOfSequence(bytes, markerC);
        Assert.True(iA >= 0 && iB >= 0 && iC >= 0, "all three markers must appear in the stream");
        Assert.True(iA < iB, $"markerA (width=1,height=1) must precede markerB (width=1,height=100): iA={iA} iB={iB}");
        Assert.True(iB < iC, $"markerB (width=1,height=100) must precede markerC (width=100,height=1): iB={iB} iC={iC}");
    }


    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(46)]
    [InlineData(60)]
    public void Read_TruncatedTail_ThrowsDeterministically(int bytesRemoved) {
        var data = WithCellShellSubset();
        using var full = new MemoryStream();
        ObjectMeshDataSerializer.Write(data, full);
        byte[] fullBytes = full.ToArray();

        byte[] truncated = fullBytes[..^bytesRemoved];

        Assert.Throws<EndOfStreamException>(() => ObjectMeshDataSerializer.Read(truncated));
    }

    [Fact]
    public void Read_EmptyStream_ThrowsDeterministically() {
        Assert.Throws<EndOfStreamException>(() => ObjectMeshDataSerializer.Read(Array.Empty<byte>()));
    }

    private static int IndexOfSequence(byte[] haystack, byte[] needle) {
        for (int i = 0; i <= haystack.Length - needle.Length; i++) {
            bool match = true;
            for (int j = 0; j < needle.Length; j++) {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }
}
