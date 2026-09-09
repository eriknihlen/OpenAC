using System.Numerics;
using AcDream.Core.Meshing;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Meshing;

public class GfxObjMeshTests
{
    /// <summary>
    /// Build a minimal GfxObj fixture with a single triangle using surface index 0.
    /// Three unique positions, one UV slot each.
    /// </summary>
    private static GfxObj BuildSingleTriangle()
    {
        var gfx = new GfxObj
        {
            Surfaces = { 0x08000000u },  // synthetic surface id
            VertexArray = new VertexArray
            {
                VertexType = VertexType.CSWVertexType,
                Vertices =
                {
                    [0] = new SWVertex
                    {
                        Origin = new Vector3(0, 0, 0),
                        Normal = new Vector3(0, 0, 1),
                        UVs = { new Vec2Duv { U = 0, V = 0 } },
                    },
                    [1] = new SWVertex
                    {
                        Origin = new Vector3(1, 0, 0),
                        Normal = new Vector3(0, 0, 1),
                        UVs = { new Vec2Duv { U = 1, V = 0 } },
                    },
                    [2] = new SWVertex
                    {
                        Origin = new Vector3(0, 1, 0),
                        Normal = new Vector3(0, 0, 1),
                        UVs = { new Vec2Duv { U = 0, V = 1 } },
                    },
                },
            },
            Polygons =
            {
                [0] = new Polygon
                {
                    PosSurface = 0,
                    NegSurface = -1,
                    VertexIds = { 0, 1, 2 },
                    PosUVIndices = { 0, 0, 0 },
                },
            },
        };
        return gfx;
    }

    [Fact]
    public void Build_SingleTriangle_ProducesOneSubMeshOneTriangle()
    {
        var gfx = BuildSingleTriangle();

        var subs = GfxObjMesh.Build(gfx);

        var sub = Assert.Single(subs);
        Assert.Equal(0x08000000u, sub.SurfaceId);
        Assert.Equal(3, sub.Vertices.Length);
        Assert.Equal(3, sub.Indices.Length);  // one triangle, 3 indices
    }

    [Fact]
    public void Build_SingleTriangle_CopiesPositionsNormalsAndUVs()
    {
        var gfx = BuildSingleTriangle();

        var sub = GfxObjMesh.Build(gfx).Single();

        var vAtIdx0 = sub.Vertices[sub.Indices[0]];
        var vAtIdx1 = sub.Vertices[sub.Indices[1]];
        var vAtIdx2 = sub.Vertices[sub.Indices[2]];

        Assert.Equal(new Vector3(0, 0, 0), vAtIdx0.Position);
        Assert.Equal(new Vector3(1, 0, 0), vAtIdx1.Position);
        Assert.Equal(new Vector3(0, 1, 0), vAtIdx2.Position);

        Assert.Equal(new Vector3(0, 0, 1), vAtIdx0.Normal);
        Assert.Equal(new Vector2(0, 0), vAtIdx0.TexCoord);
        Assert.Equal(new Vector2(1, 0), vAtIdx1.TexCoord);
        Assert.Equal(new Vector2(0, 1), vAtIdx2.TexCoord);
    }

    [Fact]
    public void Build_Quad_IsTriangulatedAsFan()
    {
        // Single quad polygon with 4 vertices -> 2 triangles, 6 indices.
        var gfx = new GfxObj
        {
            Surfaces = { 0x08000000u },
            VertexArray = new VertexArray
            {
                Vertices =
                {
                    [0] = new SWVertex { Origin = new(0, 0, 0), UVs = { new Vec2Duv() } },
                    [1] = new SWVertex { Origin = new(1, 0, 0), UVs = { new Vec2Duv() } },
                    [2] = new SWVertex { Origin = new(1, 1, 0), UVs = { new Vec2Duv() } },
                    [3] = new SWVertex { Origin = new(0, 1, 0), UVs = { new Vec2Duv() } },
                },
            },
            Polygons =
            {
                [0] = new Polygon
                {
                    PosSurface = 0,
                    VertexIds = { 0, 1, 2, 3 },
                    PosUVIndices = { 0, 0, 0, 0 },
                },
            },
        };

        var sub = GfxObjMesh.Build(gfx).Single();

        Assert.Equal(4, sub.Vertices.Length);
        Assert.Equal(6, sub.Indices.Length);  // 2 triangles
    }

    [Fact]
    public void Build_SamePositionDifferentUVs_DuplicatesOutputVertices()
    {
        var gfx = new GfxObj
        {
            Surfaces = { 0x08000000u },
            VertexArray = new VertexArray
            {
                Vertices =
                {
                    [0] = new SWVertex
                    {
                        Origin = new(0, 0, 0),
                        UVs =
                        {
                            new Vec2Duv { U = 0, V = 0 },
                            new Vec2Duv { U = 1, V = 1 },
                        },
                    },
                    [1] = new SWVertex { Origin = new(1, 0, 0), UVs = { new Vec2Duv() } },
                    [2] = new SWVertex { Origin = new(0, 1, 0), UVs = { new Vec2Duv() } },
                },
            },
            Polygons =
            {
                [0] = new Polygon
                {
                    PosSurface = 0,
                    VertexIds = { 0, 1, 2 },
                    PosUVIndices = { 0, 0, 0 },
                },
                [1] = new Polygon
                {
                    PosSurface = 0,
                    VertexIds = { 0, 1, 2 },
                    PosUVIndices = { 1, 0, 0 },  // same positions, different UV on vert 0
                },
            },
        };

        var sub = GfxObjMesh.Build(gfx).Single();

        // vert 0 has two different UV slots → 2 output vertices for pos 0
        // vert 1 + 2 unique → 2 more output vertices
        // total: 4 output vertices
        Assert.Equal(4, sub.Vertices.Length);
        Assert.Equal(6, sub.Indices.Length);  // 2 triangles
    }

    [Fact]
    public void Build_MultipleSurfaces_ProducesMultipleSubMeshes()
    {
        // 2 polygons, 2 surfaces → 2 sub-meshes.
        var gfx = new GfxObj
        {
            Surfaces = { 0x08000001u, 0x08000002u },
            VertexArray = new VertexArray
            {
                Vertices =
                {
                    [0] = new SWVertex { Origin = new(0, 0, 0), UVs = { new Vec2Duv() } },
                    [1] = new SWVertex { Origin = new(1, 0, 0), UVs = { new Vec2Duv() } },
                    [2] = new SWVertex { Origin = new(0, 1, 0), UVs = { new Vec2Duv() } },
                    [3] = new SWVertex { Origin = new(1, 1, 0), UVs = { new Vec2Duv() } },
                },
            },
            Polygons =
            {
                [0] = new Polygon
                {
                    PosSurface = 0,
                    VertexIds = { 0, 1, 2 },
                    PosUVIndices = { 0, 0, 0 },
                },
                [1] = new Polygon
                {
                    PosSurface = 1,
                    VertexIds = { 1, 3, 2 },
                    PosUVIndices = { 0, 0, 0 },
                },
            },
        };

        var subs = GfxObjMesh.Build(gfx);

        Assert.Equal(2, subs.Count);
        Assert.Contains(subs, s => s.SurfaceId == 0x08000001u);
        Assert.Contains(subs, s => s.SurfaceId == 0x08000002u);
    }

    [Fact]
    public void Build_DegeneratePolygonWithTwoVertices_Skipped()
    {
        var gfx = new GfxObj
        {
            Surfaces = { 0x08000000u },
            VertexArray = new VertexArray
            {
                Vertices =
                {
                    [0] = new SWVertex { Origin = new(0, 0, 0), UVs = { new Vec2Duv() } },
                    [1] = new SWVertex { Origin = new(1, 0, 0), UVs = { new Vec2Duv() } },
                },
            },
            Polygons =
            {
                [0] = new Polygon
                {
                    PosSurface = 0,
                    VertexIds = { 0, 1 },
                    PosUVIndices = { 0, 0 },
                },
            },
        };

        var subs = GfxObjMesh.Build(gfx);

        Assert.Empty(subs);  // no valid polygons → no sub-meshes
    }

    [Fact]
    public void Build_NoPosQuad_StillEmitsPositiveSideVerticesAndIndices()
    {
        var gfx = new GfxObj
        {
            Surfaces = { 0x08000000u },
            VertexArray = new VertexArray
            {
                Vertices =
                {
                    [0] = new SWVertex { Origin = new(0, 0, 0) },
                    [1] = new SWVertex { Origin = new(1, 0, 0) },
                    [2] = new SWVertex { Origin = new(1, 1, 0) },
                    [3] = new SWVertex { Origin = new(0, 1, 0) },
                },
            },
            Polygons =
            {
                [0] = new Polygon
                {
                    Stippling = StipplingType.NoPos,
                    PosSurface = 0,
                    NegSurface = -1,
                    VertexIds = { 0, 1, 2, 3 },
                    // No PosUVIndices — NoPos means there ARE none on the wire.
                },
            },
        };

        var sub = GfxObjMesh.Build(gfx).Single();

        Assert.Equal(4, sub.Vertices.Length);
        Assert.Equal(6, sub.Indices.Length); // fan-triangulated quad, 2 triangles
        Assert.All(sub.Vertices, v => Assert.Equal(Vector2.Zero, v.TexCoord));
    }
}
