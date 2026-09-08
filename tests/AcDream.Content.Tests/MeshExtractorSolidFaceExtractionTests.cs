using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using AcDream.Content;
using AcDream.Core.Meshing;
using Chorizite.Core.Render.Enums;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib.IO;
using DatReaderWriter.Types;
using Microsoft.Extensions.Logging.Abstractions;
using RetailCullMode = DatReaderWriter.Enums.CullMode;

namespace AcDream.Content.Tests;

public sealed class MeshExtractorSolidFaceExtractionTests
{
    private const uint GfxObjId = 0x01000001u;
    private const uint SolidSurfaceId = 0x08000001u;
    private const uint TexturedSurfaceId = 0x08000002u;
    private const uint SurfaceTextureId = 0x05000001u;
    private const uint RenderSurfaceId = 0x06000001u;

    [Fact]
    public void PrepareMeshData_CarriesAuthoredDrawingBspSphereInsteadOfVertexAabbSphere()
    {
        var authored = new Sphere
        {
            Origin = new Vector3(-0.25f, 0.125f, 0.5f),
            Radius = 3.125f,
        };
        GfxObj gfxObj = BuildQuadGfxObj(SolidSurfaceId, noPos: true);
        gfxObj.DrawingBSP = new DrawingBSPTree
        {
            Root = new DrawingBSPNode { BoundingSphere = authored },
        };
        var dats = new FakeMeshExtractorDats();
        dats.RegisterRootGfxObj(GfxObjId, gfxObj);
        dats.Register(SolidSurfaceId, new Surface
        {
            Type = SurfaceType.Base1Solid,
            ColorValue = new ColorARGB
            {
                Alpha = 255,
                Red = 12,
                Green = 34,
                Blue = 56,
            },
        });
        var extractor = new MeshExtractor(
            dats,
            NullLogger.Instance,
            sideStagedSink: null);

        ObjectMeshData? mesh = extractor.PrepareMeshData(
            GfxObjId,
            isSetup: false);

        Assert.NotNull(mesh);
        Assert.NotNull(mesh!.SelectionSphere);
        Assert.Equal(authored.Origin, mesh.SelectionSphere.Origin);
        Assert.Equal(authored.Radius, mesh.SelectionSphere.Radius);
    }

    [Fact]
    public void PrepareMeshData_NoPosSolidQuad_EmitsSolidBatchWithFourVerticesAndSixIndices()
    {
        var dats = new FakeMeshExtractorDats();
        dats.RegisterRootGfxObj(GfxObjId, BuildQuadGfxObj(SolidSurfaceId, noPos: true));
        var color = new ColorARGB { Alpha = 255, Red = 12, Green = 34, Blue = 56 };
        dats.Register(SolidSurfaceId, new Surface
        {
            Type = SurfaceType.Base1Solid,
            ColorValue = color,
        });

        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        ObjectMeshData? mesh = extractor.PrepareMeshData(GfxObjId, isSetup: false);

        Assert.NotNull(mesh);
        Assert.Equal(4, mesh!.Vertices.Length);
        List<TextureBatchData> batches = mesh.TextureBatches.Values.Single();
        TextureBatchData batch = Assert.Single(batches);
        Assert.Equal(6, batch.Indices.Count);
        Assert.True(batch.Key.IsSolid);

        Assert.Equal((byte)color.Red, batch.TextureData[0]);
        Assert.Equal((byte)color.Green, batch.TextureData[1]);
        Assert.Equal((byte)color.Blue, batch.TextureData[2]);
        Assert.Equal((byte)color.Alpha, batch.TextureData[3]);
    }

    [Fact]
    public void PrepareMeshData_NoPosTexturedQuad_EmitsWithZeroUVsAndIsSolidFalse()
    {
        var dats = new FakeMeshExtractorDats();
        dats.RegisterRootGfxObj(GfxObjId, BuildQuadGfxObj(TexturedSurfaceId, noPos: true));
        dats.Register(TexturedSurfaceId, new Surface
        {
            Type = SurfaceType.Base1Image,
            OrigTextureId = SurfaceTextureId,
        });
        dats.Register(SurfaceTextureId, new SurfaceTexture
        {
            Textures = new List<QualifiedDataId<RenderSurface>> { RenderSurfaceId },
        });
        dats.Register(RenderSurfaceId, new RenderSurface
        {
            Width = 1,
            Height = 1,
            Format = PixelFormat.PFID_A8R8G8B8,
            SourceData = new byte[] { 10, 20, 30, 255 },
        });

        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        ObjectMeshData? mesh = extractor.PrepareMeshData(GfxObjId, isSetup: false);

        Assert.NotNull(mesh);
        Assert.Equal(4, mesh!.Vertices.Length);
        Assert.All(mesh.Vertices, v => Assert.Equal(Vector2.Zero, v.UV));

        List<TextureBatchData> batches = mesh.TextureBatches.Values.Single();
        TextureBatchData batch = Assert.Single(batches);
        Assert.Equal(6, batch.Indices.Count);
        Assert.False(batch.Key.IsSolid);
    }

    [Theory]
    [InlineData(PixelFormat.PFID_DXT1, TextureFormat.DXT1, 8)]
    [InlineData(PixelFormat.PFID_DXT3, TextureFormat.DXT3, 16)]
    [InlineData(PixelFormat.PFID_DXT5, TextureFormat.DXT5, 16)]
    public void PrepareMeshData_UneditedDxtSurface_PreservesNativeBlocks(
        PixelFormat sourceFormat,
        TextureFormat expectedFormat,
        int sourceBytes)
    {
        var dats = new FakeMeshExtractorDats();
        byte[] blocks = Enumerable.Range(0, sourceBytes).Select(i => (byte)i).ToArray();
        RegisterTexturedQuad(dats, SurfaceType.Base1Image, sourceFormat, blocks);

        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);
        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareMeshData(GfxObjId, isSetup: false));

        KeyValuePair<(int Width, int Height, TextureFormat Format), List<TextureBatchData>> group =
            Assert.Single(mesh.TextureBatches);
        Assert.Equal(expectedFormat, group.Key.Format);
        TextureBatchData batch = Assert.Single(group.Value);
        Assert.Same(blocks, batch.TextureData);
        Assert.Null(batch.UploadPixelFormat);
        Assert.Null(batch.UploadPixelType);
    }

    [Fact]
    public void PrepareMeshData_DxtClipMap_DecodesForSurfaceLocalAlphaEdit()
    {
        var dats = new FakeMeshExtractorDats();
        RegisterTexturedQuad(
            dats,
            SurfaceType.Base1Image | SurfaceType.Base1ClipMap,
            PixelFormat.PFID_DXT1,
            new byte[8]);

        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);
        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareMeshData(GfxObjId, isSetup: false));

        KeyValuePair<(int Width, int Height, TextureFormat Format), List<TextureBatchData>> group =
            Assert.Single(mesh.TextureBatches);
        Assert.Equal(TextureFormat.RGBA8, group.Key.Format);
        Assert.Equal(4 * 4 * 4, Assert.Single(group.Value).TextureData.Length);
    }

    [Fact]
    public void PrepareMeshData_TranslucentDxt_DecodesForAlphaScale()
    {
        var dats = new FakeMeshExtractorDats();
        RegisterTexturedQuad(
            dats,
            SurfaceType.Base1Image | SurfaceType.Translucent,
            PixelFormat.PFID_DXT1,
            new byte[8],
            translucency: 0.25f);

        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);
        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareMeshData(GfxObjId, isSetup: false));

        KeyValuePair<(int Width, int Height, TextureFormat Format), List<TextureBatchData>> group =
            Assert.Single(mesh.TextureBatches);
        Assert.Equal(TextureFormat.RGBA8, group.Key.Format);
        TextureBatchData batch = Assert.Single(group.Value);
        Assert.Equal(4 * 4 * 4, batch.TextureData.Length);
        Assert.Equal(0.75f, batch.SurfaceOpacity);
    }

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(0.25f, 0.75f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.75f, 0.25f)]
    [InlineData(1f, 0f)]
    public void PrepareCellStructMeshData_CarriesAuthoredSurfaceOpacity(
        float translucency,
        float expectedOpacity)
    {
        var dats = new FakeMeshExtractorDats();
        RegisterCellTexturedSurface(
            dats,
            SurfaceType.Base1Image | SurfaceType.Translucent,
            translucency);
        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareCellStructMeshData(
                id: 1,
                BuildQuadCellStruct(RetailCullMode.Landblock),
                surfaceOverrides: [2],
                Matrix4x4.Identity,
                CancellationToken.None));

        TextureBatchData batch = Assert.Single(Assert.Single(mesh.TextureBatches).Value);
        Assert.Equal(
            BitConverter.SingleToInt32Bits(expectedOpacity),
            BitConverter.SingleToInt32Bits(batch.SurfaceOpacity));
    }

    public static TheoryData<SurfaceType, bool, RetailSetSurfaceBlend,
        RetailSetSurfaceAlphaTest, bool> SetSurfaceExtractionRows() => new()
    {
        { SurfaceType.Base1Image, false, RetailSetSurfaceBlend.Opaque, RetailSetSurfaceAlphaTest.Disabled, true },
        { SurfaceType.Base1Image | SurfaceType.Alpha, false, RetailSetSurfaceBlend.StraightAlpha, RetailSetSurfaceAlphaTest.Disabled, true },
        { SurfaceType.Base1Image | SurfaceType.Alpha | SurfaceType.Additive, false, RetailSetSurfaceBlend.AlphaAdditive, RetailSetSurfaceAlphaTest.Disabled, false },
        { SurfaceType.Base1Image | SurfaceType.Additive, false, RetailSetSurfaceBlend.Additive, RetailSetSurfaceAlphaTest.Disabled, false },
        { SurfaceType.Base1Image | SurfaceType.InvAlpha, false, RetailSetSurfaceBlend.InverseAlpha, RetailSetSurfaceAlphaTest.Disabled, true },
        { SurfaceType.Base1Image | SurfaceType.InvAlpha | SurfaceType.Additive, false, RetailSetSurfaceBlend.InverseAdditive, RetailSetSurfaceAlphaTest.Disabled, false },
        { SurfaceType.Base1Image | SurfaceType.Base1ClipMap, false, RetailSetSurfaceBlend.Clip, RetailSetSurfaceAlphaTest.Dds, true },
        { SurfaceType.Base1Image | SurfaceType.Base1ClipMap, true, RetailSetSurfaceBlend.Clip, RetailSetSurfaceAlphaTest.Paletted, true },
        { SurfaceType.Base1Image | SurfaceType.Alpha | SurfaceType.Base1ClipMap, true, RetailSetSurfaceBlend.StraightAlpha, RetailSetSurfaceAlphaTest.Paletted, true },
        { SurfaceType.Base1Image | SurfaceType.Translucent | SurfaceType.Base1ClipMap | SurfaceType.Additive, true, RetailSetSurfaceBlend.StraightAlpha, RetailSetSurfaceAlphaTest.Disabled, false },
    };

    [Theory]
    [MemberData(nameof(SetSurfaceExtractionRows))]
    public void OrdinaryAndCellExtraction_CarryIdenticalResolvedSetSurfaceState(
        SurfaceType type,
        bool paletted,
        RetailSetSurfaceBlend expectedBlend,
        RetailSetSurfaceAlphaTest expectedAlphaTest,
        bool expectedFog)
    {
        var dats = new FakeMeshExtractorDats();
        uint paletteId = paletted ? 0x04000001u : 0u;
        RegisterTexturedQuad(
            dats,
            type,
            PixelFormat.PFID_A8R8G8B8,
            new byte[4 * 4 * 4],
            paletteId: paletteId);
        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        ObjectMeshData ordinary = Assert.IsType<ObjectMeshData>(
            extractor.PrepareMeshData(GfxObjId, isSetup: false));
        ObjectMeshData cell = Assert.IsType<ObjectMeshData>(
            extractor.PrepareCellStructMeshData(
                id: 1,
                BuildQuadCellStruct(RetailCullMode.Landblock),
                surfaceOverrides: [2],
                Matrix4x4.Identity,
                CancellationToken.None));
        RetailSetSurfaceMaterialState ordinaryState =
            Assert.Single(Assert.Single(ordinary.TextureBatches).Value).MaterialState;
        RetailSetSurfaceMaterialState cellState =
            Assert.Single(Assert.Single(cell.TextureBatches).Value).MaterialState;

        Assert.Equal(ordinaryState, cellState);
        Assert.Equal(expectedBlend, ordinaryState.Blend);
        Assert.Equal(expectedAlphaTest, ordinaryState.AlphaTest);
        Assert.Equal(expectedFog, ordinaryState.FogEnabled);
    }

    private static void RegisterCellTexturedSurface(
        FakeMeshExtractorDats dats,
        SurfaceType type = SurfaceType.Base1Image,
        float translucency = 0f)
    {
        dats.Register(TexturedSurfaceId, new Surface
        {
            Type = type,
            OrigTextureId = SurfaceTextureId,
            Translucency = translucency,
        });
        dats.Register(SurfaceTextureId, new SurfaceTexture
        {
            Textures = new List<QualifiedDataId<RenderSurface>> { RenderSurfaceId },
        });
        dats.Register(RenderSurfaceId, new RenderSurface
        {
            Width = 1,
            Height = 1,
            Format = PixelFormat.PFID_A8R8G8B8,
            SourceData = new byte[] { 10, 20, 30, 255 },
        });
    }

    [Fact]
    public void PrepareCellStructMeshData_LandblockSide_PreservesRetailTriangleFanWinding()
    {
        var dats = new FakeMeshExtractorDats();
        RegisterCellTexturedSurface(dats);
        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareCellStructMeshData(
                id: 1,
                BuildQuadCellStruct(RetailCullMode.Landblock),
                surfaceOverrides: [2],
                Matrix4x4.Identity,
                CancellationToken.None));

        TextureBatchData batch = Assert.Single(Assert.Single(mesh.TextureBatches).Value);
        Assert.Equal(RetailCullMode.Clockwise, batch.CullMode);
        Assert.True(batch.IsCellShell);
        Assert.Equal(0, batch.SourceSurfaceIndex);
        Assert.Equal([0, 1, 2, 0, 2, 3], batch.Indices);
        Assert.Equal(4, mesh.Vertices.Length);
        Assert.All(mesh.Vertices, vertex => Assert.Equal(Vector3.UnitZ, vertex.Normal));
    }

    [Fact]
    public void PrepareCellStructMeshData_NoneSide_ExpandsReversedFaceExactlyLikeRetailConstructMesh()
    {
        var dats = new FakeMeshExtractorDats();
        RegisterCellTexturedSurface(dats);
        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareCellStructMeshData(
                id: 1,
                BuildQuadCellStruct(RetailCullMode.None),
                surfaceOverrides: [2],
                Matrix4x4.Identity,
                CancellationToken.None));

        TextureBatchData batch = Assert.Single(Assert.Single(mesh.TextureBatches).Value);
        Assert.Equal(RetailCullMode.Clockwise, batch.CullMode);
        Assert.True(batch.IsCellShell);
        Assert.Equal(0, batch.SourceSurfaceIndex);
        Assert.Equal(
            [0, 1, 2, 0, 2, 3, 6, 5, 4, 7, 6, 4],
            batch.Indices);
        Assert.Equal(8, mesh.Vertices.Length);
        Assert.All(mesh.Vertices.Take(4), vertex => Assert.Equal(Vector3.UnitZ, vertex.Normal));
        Assert.All(mesh.Vertices.Skip(4), vertex => Assert.Equal(-Vector3.UnitZ, vertex.Normal));
    }

    [Fact]
    public void PrepareCellStructMeshData_BothSide_NegativeCandidateIsNotWindingReversed()
    {
        var dats = new FakeMeshExtractorDats();
        dats.Register(0x0800000Au, new Surface { Type = SurfaceType.Base1Image, OrigTextureId = SurfaceTextureId });
        dats.Register(0x08000014u, new Surface { Type = SurfaceType.Base1Image, OrigTextureId = SurfaceTextureId });
        dats.Register(SurfaceTextureId, new SurfaceTexture
        {
            Textures = new List<QualifiedDataId<RenderSurface>> { RenderSurfaceId },
        });
        dats.Register(RenderSurfaceId, new RenderSurface
        {
            Width = 1,
            Height = 1,
            Format = PixelFormat.PFID_A8R8G8B8,
            SourceData = new byte[] { 1, 2, 3, 255 },
        });
        var cellStruct = new CellStruct
        {
            VertexArray = new VertexArray
            {
                Vertices = new Dictionary<ushort, SWVertex>
                {
                    [0] = new() { Origin = new Vector3(0, 0, 0), Normal = Vector3.UnitZ },
                    [1] = new() { Origin = new Vector3(1, 0, 0), Normal = Vector3.UnitZ },
                    [2] = new() { Origin = new Vector3(1, 1, 0), Normal = Vector3.UnitZ },
                    [3] = new() { Origin = new Vector3(0, 1, 0), Normal = Vector3.UnitZ },
                },
            },
            Polygons = new Dictionary<ushort, Polygon>
            {
                [0] = new() { SidesType = RetailCullMode.Clockwise, PosSurface = 0, NegSurface = 1, VertexIds = [0, 1, 2, 3] },
            },
        };
        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareCellStructMeshData(
                id: 1, cellStruct, surfaceOverrides: [10, 20], Matrix4x4.Identity, CancellationToken.None));

        List<TextureBatchData> batches = mesh.TextureBatches.Values.SelectMany(b => b).OrderBy(b => b.SourceSurfaceIndex).ToList();
        Assert.Equal(2, batches.Count);

        TextureBatchData positive = batches[0];
        Assert.Equal(0, positive.SourceSurfaceIndex);
        Assert.Equal([0, 1, 2, 0, 2, 3], positive.Indices);

        TextureBatchData negative = batches[1];
        Assert.Equal(1, negative.SourceSurfaceIndex);
        // NOT reversed: forward fan order over the negative-lane vertices.
        Assert.Equal([4, 5, 6, 4, 6, 7], negative.Indices);

        Assert.All(mesh.Vertices.Take(4), vertex => Assert.Equal(Vector3.UnitZ, vertex.Normal));
        Assert.All(mesh.Vertices.Skip(4), vertex => Assert.Equal(-Vector3.UnitZ, vertex.Normal));
    }

    [Fact]
    public void PrepareCellStructMeshData_UntexturedSlot_ConstructedButNotEmitted()
    {
        var dats = new FakeMeshExtractorDats();
        dats.Register(SolidSurfaceId, new Surface
        {
            Type = SurfaceType.Base1Solid,
            ColorValue = new ColorARGB { Alpha = 255, Red = 64, Green = 96, Blue = 128 },
        });
        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareCellStructMeshData(
                id: 1,
                BuildQuadCellStruct(RetailCullMode.Landblock),
                surfaceOverrides: [1],
                Matrix4x4.Identity,
                CancellationToken.None));

        Assert.Empty(mesh.TextureBatches);
    }

    [Fact]
    public void PrepareCellStructMeshData_TexturedNoPos_ReadsVertexUvSlotZero()
    {
        var dats = new FakeMeshExtractorDats();
        RegisterCellTexturedSurface(dats);
        var cellStruct = new CellStruct
        {
            VertexArray = new VertexArray
            {
                Vertices = new Dictionary<ushort, SWVertex>
                {
                    [0] = new() { Origin = new Vector3(0, 0, 0), Normal = Vector3.UnitZ, UVs = { new Vec2Duv { U = 0.5f, V = 0.5f } } },
                    [1] = new() { Origin = new Vector3(1, 0, 0), Normal = Vector3.UnitZ, UVs = { new Vec2Duv { U = 0.5f, V = 0.5f } } },
                    [2] = new() { Origin = new Vector3(1, 1, 0), Normal = Vector3.UnitZ, UVs = { new Vec2Duv { U = 0.5f, V = 0.5f } } },
                    [3] = new() { Origin = new Vector3(0, 1, 0), Normal = Vector3.UnitZ, UVs = { new Vec2Duv { U = 0.5f, V = 0.5f } } },
                },
            },
            Polygons = new Dictionary<ushort, Polygon>
            {
                [0] = new() { SidesType = RetailCullMode.Landblock, Stippling = StipplingType.NoPos, PosSurface = 0, NegSurface = -1, VertexIds = [0, 1, 2, 3] },
            },
        };
        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareCellStructMeshData(
                id: 1, cellStruct, surfaceOverrides: [2], Matrix4x4.Identity, CancellationToken.None));

        TextureBatchData batch = Assert.Single(Assert.Single(mesh.TextureBatches).Value);
        Assert.False(batch.Key.IsSolid);
        Assert.Equal(4, mesh.Vertices.Length);
        Assert.All(mesh.Vertices, vertex => Assert.Equal(new Vector2(0.5f, 0.5f), vertex.UV));
    }

    [Fact]
    public void PrepareCellStructMeshData_TexturedNoPos_VertexWithoutUvArray_EmitsZeroUVs()
    {
        var dats = new FakeMeshExtractorDats();
        RegisterCellTexturedSurface(dats);
        var cellStruct = BuildQuadCellStruct(RetailCullMode.Landblock);
        cellStruct.Polygons[0].Stippling = StipplingType.NoPos;
        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareCellStructMeshData(
                id: 1, cellStruct, surfaceOverrides: [2], Matrix4x4.Identity, CancellationToken.None));

        Assert.Equal(4, mesh.Vertices.Length);
        Assert.All(mesh.Vertices, vertex => Assert.Equal(Vector2.Zero, vertex.UV));
    }

    [Fact]
    public void PrepareCellStructMeshData_OutOfRangeUvIndex_EmitsZeroUVs_NotSlotZero()
    {
        var dats = new FakeMeshExtractorDats();
        RegisterCellTexturedSurface(dats);
        var cellStruct = new CellStruct
        {
            VertexArray = new VertexArray
            {
                Vertices = new Dictionary<ushort, SWVertex>
                {
                    [0] = new() { Origin = new Vector3(0, 0, 0), Normal = Vector3.UnitZ, UVs = { new Vec2Duv { U = 0.5f, V = 0.5f } } },
                    [1] = new() { Origin = new Vector3(1, 0, 0), Normal = Vector3.UnitZ, UVs = { new Vec2Duv { U = 0.5f, V = 0.5f } } },
                    [2] = new() { Origin = new Vector3(1, 1, 0), Normal = Vector3.UnitZ, UVs = { new Vec2Duv { U = 0.5f, V = 0.5f } } },
                    [3] = new() { Origin = new Vector3(0, 1, 0), Normal = Vector3.UnitZ, UVs = { new Vec2Duv { U = 0.5f, V = 0.5f } } },
                },
            },
            Polygons = new Dictionary<ushort, Polygon>
            {
                [0] = new() { SidesType = RetailCullMode.Landblock, Stippling = default, PosSurface = 0, NegSurface = -1, VertexIds = [0, 1, 2, 3], PosUVIndices = [5, 5, 5, 5] },
            },
        };
        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareCellStructMeshData(
                id: 1, cellStruct, surfaceOverrides: [2], Matrix4x4.Identity, CancellationToken.None));

        Assert.Equal(4, mesh.Vertices.Length);
        Assert.All(mesh.Vertices, vertex => Assert.Equal(Vector2.Zero, vertex.UV));
    }

    [Fact]
    public void PrepareCellStructMeshData_DegeneratePolygon_StillOrsPositiveSurfaceMask()
    {
        var dats = new FakeMeshExtractorDats();
        RegisterCellTexturedSurface(dats);
        var cellStruct = BuildQuadCellStruct(RetailCullMode.Landblock);
        cellStruct.Polygons[1] = new Polygon
        {
            SidesType = RetailCullMode.Landblock,
            Stippling = StipplingType.NoPos,
            PosSurface = 0,
            NegSurface = -1,
            VertexIds = [0, 1],
        };
        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareCellStructMeshData(
                id: 1, cellStruct, surfaceOverrides: [2], Matrix4x4.Identity, CancellationToken.None));

        TextureBatchData batch = Assert.Single(Assert.Single(mesh.TextureBatches).Value);
        Assert.Equal(6, batch.Indices.Count);
        Assert.Equal(1, batch.RetailSurfaceMask);
    }

    [Fact]
    public void PrepareCellStructMeshData_UntexturedSlot_EmitsNoVertices()
    {
        var dats = new FakeMeshExtractorDats();
        RegisterCellTexturedSurface(dats);
        dats.Register(SolidSurfaceId, new Surface
        {
            Type = SurfaceType.Base1Solid,
            ColorValue = new ColorARGB { Alpha = 255, Red = 64, Green = 96, Blue = 128 },
        });
        var cellStruct = BuildQuadCellStruct(RetailCullMode.Landblock);
        cellStruct.Polygons[1] = new Polygon
        {
            SidesType = RetailCullMode.Landblock,
            PosSurface = 1,
            NegSurface = -1,
            VertexIds = [0, 1, 2, 3],
        };
        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareCellStructMeshData(
                id: 1, cellStruct, surfaceOverrides: [2, 1], Matrix4x4.Identity, CancellationToken.None));

        TextureBatchData batch = Assert.Single(Assert.Single(mesh.TextureBatches).Value);
        Assert.Equal(0, batch.SourceSurfaceIndex);
        Assert.Equal(4, mesh.Vertices.Length);
    }

    [Fact]
    public void PrepareCellStructMeshData_SidesValueOutsideRetailSet_ConstructsSingleSideFan()
    {
        var dats = new FakeMeshExtractorDats();
        RegisterCellTexturedSurface(dats);
        var cellStruct = BuildQuadCellStruct((RetailCullMode)7);
        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareCellStructMeshData(
                id: 1, cellStruct, surfaceOverrides: [2], Matrix4x4.Identity, CancellationToken.None));

        TextureBatchData batch = Assert.Single(Assert.Single(mesh.TextureBatches).Value);
        Assert.Equal(6, batch.Indices.Count);
        Assert.Equal(4, mesh.Vertices.Length);
    }

    [Fact]
    public void PrepareCellStructMeshData_TwoSlotsSameSurfaceDid_RemainTwoDistinctSubsets()
    {
        var dats = new FakeMeshExtractorDats();
        RegisterCellTexturedSurface(dats);
        var cellStruct = new CellStruct
        {
            VertexArray = new VertexArray
            {
                Vertices = new Dictionary<ushort, SWVertex>
                {
                    [0] = new() { Origin = new Vector3(0, 0, 0), Normal = Vector3.UnitZ },
                    [1] = new() { Origin = new Vector3(1, 0, 0), Normal = Vector3.UnitZ },
                    [2] = new() { Origin = new Vector3(1, 1, 0), Normal = Vector3.UnitZ },
                    [3] = new() { Origin = new Vector3(0, 1, 0), Normal = Vector3.UnitZ },
                    [4] = new() { Origin = new Vector3(2, 0, 0), Normal = Vector3.UnitZ },
                    [5] = new() { Origin = new Vector3(3, 0, 0), Normal = Vector3.UnitZ },
                    [6] = new() { Origin = new Vector3(3, 1, 0), Normal = Vector3.UnitZ },
                },
            },
            Polygons = new Dictionary<ushort, Polygon>
            {
                [0] = new() { SidesType = RetailCullMode.Landblock, PosSurface = 0, NegSurface = -1, VertexIds = [0, 1, 2, 3] },
                [1] = new() { SidesType = RetailCullMode.Landblock, PosSurface = 1, NegSurface = -1, VertexIds = [4, 5, 6] },
            },
        };
        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        // Both slots resolve to override value 2 -> the SAME Surface DID.
        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareCellStructMeshData(
                id: 1, cellStruct, surfaceOverrides: [2, 2], Matrix4x4.Identity, CancellationToken.None));

        List<TextureBatchData> batches = mesh.TextureBatches.Values.SelectMany(b => b).OrderBy(b => b.SourceSurfaceIndex).ToList();
        Assert.Equal(2, batches.Count);
        Assert.Equal(0, batches[0].SourceSurfaceIndex);
        Assert.Equal(1, batches[1].SourceSurfaceIndex);
        Assert.Equal(batches[0].Key.SurfaceId, batches[1].Key.SurfaceId);
        Assert.NotSame(batches[0], batches[1]);
    }

    [Fact]
    public void PrepareCellStructMeshData_DifferentStipplingOnOneSlot_StaysOneSubsetWithAggregatedMask()
    {
        var dats = new FakeMeshExtractorDats();
        RegisterCellTexturedSurface(dats);
        var cellStruct = new CellStruct
        {
            VertexArray = new VertexArray
            {
                Vertices = new Dictionary<ushort, SWVertex>
                {
                    [0] = new() { Origin = new Vector3(0, 0, 0), Normal = Vector3.UnitZ },
                    [1] = new() { Origin = new Vector3(1, 0, 0), Normal = Vector3.UnitZ },
                    [2] = new() { Origin = new Vector3(1, 1, 0), Normal = Vector3.UnitZ },
                    [3] = new() { Origin = new Vector3(0, 1, 0), Normal = Vector3.UnitZ },
                },
            },
            Polygons = new Dictionary<ushort, Polygon>
            {
                [0] = new() { SidesType = RetailCullMode.Landblock, Stippling = StipplingType.None, PosSurface = 0, NegSurface = -1, VertexIds = [0, 1, 2] },
                [1] = new() { SidesType = RetailCullMode.Landblock, Stippling = StipplingType.Positive, PosSurface = 0, NegSurface = -1, VertexIds = [0, 2, 3] },
            },
        };
        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareCellStructMeshData(
                id: 1, cellStruct, surfaceOverrides: [2], Matrix4x4.Identity, CancellationToken.None));

        TextureBatchData batch = Assert.Single(Assert.Single(mesh.TextureBatches).Value);
        Assert.Equal((byte)1, batch.RetailSurfaceMask);
        Assert.Equal(6, batch.Indices.Count); // both triangles landed in the one subset
    }

    [Fact]
    public void PrepareCellStructMeshData_DifferentSidesValuesOnOneSlot_StaysOneSubset()
    {
        var dats = new FakeMeshExtractorDats();
        RegisterCellTexturedSurface(dats);
        var cellStruct = new CellStruct
        {
            VertexArray = new VertexArray
            {
                Vertices = new Dictionary<ushort, SWVertex>
                {
                    [0] = new() { Origin = new Vector3(0, 0, 0), Normal = Vector3.UnitZ },
                    [1] = new() { Origin = new Vector3(1, 0, 0), Normal = Vector3.UnitZ },
                    [2] = new() { Origin = new Vector3(1, 1, 0), Normal = Vector3.UnitZ },
                },
            },
            Polygons = new Dictionary<ushort, Polygon>
            {
                [0] = new() { SidesType = RetailCullMode.Landblock, PosSurface = 0, NegSurface = -1, VertexIds = [0, 1, 2] },
                [1] = new() { SidesType = RetailCullMode.None, PosSurface = 0, NegSurface = -1, VertexIds = [0, 1, 2] },
            },
        };
        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareCellStructMeshData(
                id: 1, cellStruct, surfaceOverrides: [2], Matrix4x4.Identity, CancellationToken.None));

        TextureBatchData batch = Assert.Single(Assert.Single(mesh.TextureBatches).Value);
        Assert.Equal(0, batch.SourceSurfaceIndex);
        Assert.Equal(9, batch.Indices.Count);
    }

    [Fact]
    public void PrepareCellStructMeshData_SubsetOrder_IsAscendingSurfaceIndexAcrossTextureFormats()
    {
        var dats = new FakeMeshExtractorDats();
        dats.Register(0x08000005u, new Surface { Type = SurfaceType.Base1Image, OrigTextureId = 0x05000005u });
        dats.Register(0x05000005u, new SurfaceTexture { Textures = new List<QualifiedDataId<RenderSurface>> { 0x06000005u } });
        dats.Register(0x06000005u, new RenderSurface { Width = 8, Height = 8, Format = PixelFormat.PFID_A8R8G8B8, SourceData = new byte[8 * 8 * 4] });

        dats.Register(0x08000002u, new Surface { Type = SurfaceType.Base1Image, OrigTextureId = SurfaceTextureId });
        dats.Register(SurfaceTextureId, new SurfaceTexture { Textures = new List<QualifiedDataId<RenderSurface>> { RenderSurfaceId } });
        dats.Register(RenderSurfaceId, new RenderSurface { Width = 1, Height = 1, Format = PixelFormat.PFID_A8R8G8B8, SourceData = new byte[] { 1, 2, 3, 255 } });

        var cellStruct = new CellStruct
        {
            VertexArray = new VertexArray
            {
                Vertices = new Dictionary<ushort, SWVertex>
                {
                    [0] = new() { Origin = new Vector3(0, 0, 0), Normal = Vector3.UnitZ },
                    [1] = new() { Origin = new Vector3(1, 0, 0), Normal = Vector3.UnitZ },
                    [2] = new() { Origin = new Vector3(1, 1, 0), Normal = Vector3.UnitZ },
                },
            },
            Polygons = new Dictionary<ushort, Polygon>
            {
                // Higher slot (5, the wide texture) authored/iterated FIRST.
                [0] = new() { SidesType = RetailCullMode.Landblock, PosSurface = 5, NegSurface = -1, VertexIds = [0, 1, 2] },
                [1] = new() { SidesType = RetailCullMode.Landblock, PosSurface = 2, NegSurface = -1, VertexIds = [0, 1, 2] },
            },
        };
        var extractor = new MeshExtractor(dats, NullLogger.Instance, sideStagedSink: null);

        ObjectMeshData mesh = Assert.IsType<ObjectMeshData>(
            extractor.PrepareCellStructMeshData(
                id: 1, cellStruct, surfaceOverrides: [0, 0, 2, 0, 0, 5], Matrix4x4.Identity, CancellationToken.None));

        // At least two distinct (Width,Height,Format) buckets prove this
        // isn't accidentally passing because everything landed in one list.
        Assert.True(mesh.TextureBatches.Count >= 2);

        List<int> order = CellSurfaceSubsets.InAscendingSurfaceOrder(mesh)
            .Select(b => b.SourceSurfaceIndex)
            .ToList();
        Assert.Equal([2, 5], order);
    }

    private static void RegisterTexturedQuad(
        FakeMeshExtractorDats dats,
        SurfaceType surfaceType,
        PixelFormat pixelFormat,
        byte[] sourceData,
        float translucency = 0.0f,
        uint paletteId = 0)
    {
        dats.RegisterRootGfxObj(GfxObjId, BuildQuadGfxObj(TexturedSurfaceId, noPos: false));
        dats.Register(TexturedSurfaceId, new Surface
        {
            Type = surfaceType,
            OrigTextureId = SurfaceTextureId,
            Translucency = translucency,
        });
        dats.Register(SurfaceTextureId, new SurfaceTexture
        {
            Textures = new List<QualifiedDataId<RenderSurface>> { RenderSurfaceId },
        });
        dats.Register(RenderSurfaceId, new RenderSurface
        {
            Width = 4,
            Height = 4,
            Format = pixelFormat,
            DefaultPaletteId = paletteId,
            SourceData = sourceData,
        });
    }

    private static CellStruct BuildQuadCellStruct(RetailCullMode sidesType) => new()
    {
        VertexArray = new VertexArray
        {
            Vertices = new Dictionary<ushort, SWVertex>
            {
                [0] = new() { Origin = new Vector3(0, 0, 0), Normal = Vector3.UnitZ },
                [1] = new() { Origin = new Vector3(1, 0, 0), Normal = Vector3.UnitZ },
                [2] = new() { Origin = new Vector3(1, 1, 0), Normal = Vector3.UnitZ },
                [3] = new() { Origin = new Vector3(0, 1, 0), Normal = Vector3.UnitZ },
            },
        },
        Polygons = new Dictionary<ushort, Polygon>
        {
            [0] = new()
            {
                SidesType = sidesType,
                PosSurface = 0,
                NegSurface = -1,
                VertexIds = [0, 1, 2, 3],
            },
        },
    };

    /// <summary>One quad (4 verts), single polygon, PosSurface referencing <paramref name="surfaceId"/>.</summary>
    private static GfxObj BuildQuadGfxObj(uint surfaceId, bool noPos)
    {
        return new GfxObj
        {
            Surfaces = { surfaceId },
            VertexArray = new VertexArray
            {
                Vertices =
                {
                    [0] = new SWVertex { Origin = new Vector3(0, 0, 0), Normal = Vector3.UnitZ },
                    [1] = new SWVertex { Origin = new Vector3(1, 0, 0), Normal = Vector3.UnitZ },
                    [2] = new SWVertex { Origin = new Vector3(1, 1, 0), Normal = Vector3.UnitZ },
                    [3] = new SWVertex { Origin = new Vector3(0, 1, 0), Normal = Vector3.UnitZ },
                },
            },
            Polygons =
            {
                [0] = new Polygon
                {
                    Stippling = noPos ? StipplingType.NoPos : default,
                    PosSurface = 0,
                    NegSurface = -1,
                    VertexIds = { 0, 1, 2, 3 },
                },
            },
        };
    }

    private sealed class FakeMeshExtractorDats : IDatReaderWriter
    {
        private readonly Dictionary<uint, IDBObj> _portalObjects = new();
        private uint _rootId;

        public FakeMeshExtractorDats() => Portal = new FakeDatDatabase(_portalObjects);

        public void RegisterRootGfxObj(uint id, GfxObj gfxObj)
        {
            _rootId = id;
            _portalObjects[id] = gfxObj;
        }

        public void Register<T>(uint id, T obj) where T : IDBObj => _portalObjects[id] = obj;

        public string SourceDirectory => string.Empty;
        public IDatDatabase Portal { get; }
        public IDatDatabase Cell => EmptyDatDatabase.Instance;
        public ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; } =
            new(new Dictionary<uint, IDatDatabase>());
        public IDatDatabase HighRes => EmptyDatDatabase.Instance;
        public IDatDatabase Language => EmptyDatDatabase.Instance;
        public IDatDatabase Local => EmptyDatDatabase.Instance;
        public ReadOnlyDictionary<uint, uint> RegionFileMap { get; } =
            new(new Dictionary<uint, uint>());
        public int PortalIteration => 0;
        public int CellIteration => 0;
        public int HighResIteration => 0;
        public int LanguageIteration => 0;

        public bool TryGetFileBytes(uint regionId, uint fileId, ref byte[] bytes, out int bytesRead)
        {
            bytesRead = 0;
            return false;
        }

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj => Array.Empty<uint>();

        public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) =>
            id == _rootId
                ? new[] { new IDatReaderWriter.IdResolution(Portal, DBObjType.GfxObj) }
                : Array.Empty<IDatReaderWriter.IdResolution>();

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        public bool TrySave<T>(uint regionId, T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : IDBObj =>
            Portal.TryGet<T>(fileId, out var value) ? value : default;

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value) where T : IDBObj =>
            Portal.TryGet(fileId, out value);

        public void Dispose()
        {
        }
    }

    private sealed class FakeDatDatabase : IDatDatabase
    {
        private readonly Dictionary<uint, IDBObj> _objects;

        public FakeDatDatabase(Dictionary<uint, IDBObj> objects) => _objects = objects;

        public DatDatabase Db => null!;
        public int Iteration => 0;

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj => Array.Empty<uint>();

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value) where T : IDBObj
        {
            if (_objects.TryGetValue(fileId, out IDBObj? obj) && obj is T typed)
            {
                value = typed;
                return true;
            }
            value = default;
            return false;
        }

        public bool TryGetFileBytes(uint fileId, [MaybeNullWhen(false)] out byte[] value)
        {
            value = null;
            return false;
        }

        public bool TryGetFileBytes(uint fileId, ref byte[] bytes, out int bytesRead)
        {
            bytesRead = 0;
            return false;
        }

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class EmptyDatDatabase : IDatDatabase
    {
        public static readonly EmptyDatDatabase Instance = new();

        public DatDatabase Db => null!;
        public int Iteration => 0;

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj => Array.Empty<uint>();

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value) where T : IDBObj
        {
            value = default;
            return false;
        }

        public bool TryGetFileBytes(uint fileId, [MaybeNullWhen(false)] out byte[] value)
        {
            value = null;
            return false;
        }

        public bool TryGetFileBytes(uint fileId, ref byte[] bytes, out int bytesRead)
        {
            bytesRead = 0;
            return false;
        }

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
