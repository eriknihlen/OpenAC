using System.Numerics;
using AcDream.Core.Meshing;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Rendering.Wb;

public sealed class MeshExtractionConformanceTests
{
    [Fact]
    public void Build_QuadGfxObj_ProducesExpectedVerticesAndIndices()
    {
        var gfxObj = MakeUnitQuadGfxObj();

        var ours = GfxObjMesh.Build(gfxObj, dats: null);

        Assert.Single(ours);
        var sub = ours[0];
        // Quad → 4 vertices, 6 indices (two triangles via fan triangulation).
        Assert.Equal(4, sub.Vertices.Length);
        Assert.Equal(6, sub.Indices.Length);
        // Fan from vertex 0: (0,1,2) and (0,2,3).
        Assert.Equal(new uint[] { 0, 1, 2, 0, 2, 3 }, sub.Indices);
    }

    [Fact]
    public void Build_DoubleSidedPoly_ProducesBothPosAndNegSubmeshes()
    {
        var gfxObj = MakeUnitQuadGfxObj();
        var poly = gfxObj.Polygons[0];
        poly.Stippling = StipplingType.Both;
        // NegSurface=0 so the neg side references a valid surface entry.
        poly.NegSurface = 0;

        var ours = GfxObjMesh.Build(gfxObj, dats: null);

        Assert.Equal(2, ours.Count);
    }

    [Fact]
    public void Build_NoNegFlag_WithClockwiseSidesType_StillEmitsNegSide()
    {
        var gfxObj = MakeUnitQuadGfxObj();
        var poly = gfxObj.Polygons[0];
        poly.Stippling = StipplingType.None;
        poly.SidesType = CullMode.Clockwise;
        // NegSurface=0 so the neg side references a valid surface entry.
        poly.NegSurface = 0;

        var ours = GfxObjMesh.Build(gfxObj, dats: null);

        Assert.Equal(2, ours.Count);
    }

    [Fact]
    public void Build_NoPosFlag_EmitsBothPosAndNegSide()
    {
        var gfxObj = MakeUnitQuadGfxObj();
        var poly = gfxObj.Polygons[0];
        poly.Stippling = StipplingType.NoPos | StipplingType.Negative;
        // NegSurface=0 so the neg side references a valid surface entry.
        poly.NegSurface = 0;

        var ours = GfxObjMesh.Build(gfxObj, dats: null);

        Assert.Equal(2, ours.Count);
    }

    private static GfxObj MakeUnitQuadGfxObj()
    {
        var gfx = new GfxObj { Surfaces = { 0x08000000u } };
        gfx.VertexArray = new VertexArray
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
                    Origin = new Vector3(1, 1, 0),
                    Normal = new Vector3(0, 0, 1),
                    UVs = { new Vec2Duv { U = 1, V = 1 } },
                },
                [3] = new SWVertex
                {
                    Origin = new Vector3(0, 1, 0),
                    Normal = new Vector3(0, 0, 1),
                    UVs = { new Vec2Duv { U = 0, V = 1 } },
                },
            },
        };

        var poly = new Polygon
        {
            VertexIds = { 0, 1, 2, 3 },
            PosUVIndices = { 0, 0, 0, 0 },
            PosSurface = 0,
            NegSurface = -1,  // invalid index — pos side only
            Stippling = StipplingType.None,
            SidesType = CullMode.None,
        };
        gfx.Polygons[0] = poly;
        return gfx;
    }
}
