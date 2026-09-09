using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.World.Cells;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;
using DatEnvCell = DatReaderWriter.DBObjs.EnvCell;
using DatCellPortal = DatReaderWriter.Types.CellPortal;

namespace AcDream.Core.Tests.World.Cells;

public class EnvCellFromDatTests
{
    [Fact]
    public void FromDat_DerivesSeenOutside_OtherPortalId_PrefixedStab_AndBoundsFallback()
    {
        var cellStruct = new CellStruct
        {
            VertexArray = new VertexArray { Vertices = new Dictionary<ushort, SWVertex>() }, // empty -> bounds fallback
            Polygons = new Dictionary<ushort, Polygon>(),
        };
        var dat = new DatEnvCell
        {
            Flags = EnvCellFlags.SeenOutside,
            CellPortals = new List<DatCellPortal>
            {
                new() { OtherCellId = 0x0105, PolygonId = 0, OtherPortalId = 7, Flags = (PortalFlags)0 },
            },
            VisibleCells = new List<ushort> { 0x0105, 0x0106 },
        };

        var env = EnvCell.FromDat(0xA9B40104u, dat, cellStruct, Matrix4x4.Identity);

        Assert.True(env.SeenOutside);
        Assert.Single(env.Portals);
        Assert.Equal(0x0105u, env.Portals[0].OtherCellId);
        Assert.Equal((ushort)7, env.Portals[0].OtherPortalId);
        Assert.Equal(new[] { 0xA9B40105u, 0xA9B40106u }, env.StabList);
        Assert.Equal(Vector3.Zero, env.LocalBoundsMin);
        Assert.Equal(Vector3.Zero, env.LocalBoundsMax);
        Assert.Null(env.ContainmentBsp);
    }

    [Fact]
    public void FromDat_PortalPolygonMissingAVertex_YieldsEmptyPolygonLocal()
    {
        var cellStruct = new CellStruct
        {
            VertexArray = new VertexArray { Vertices = new Dictionary<ushort, SWVertex>
            {
                [0] = new SWVertex { Origin = new Vector3(0, 0, 0) },
                [1] = new SWVertex { Origin = new Vector3(1, 0, 0) },
                // vertex 2 intentionally MISSING
            }},
            Polygons = new Dictionary<ushort, Polygon>
            {
                [0] = new Polygon { VertexIds = new List<short> { 0, 1, 2 } },
            },
        };
        var dat = new DatEnvCell
        {
            Flags = (EnvCellFlags)0,
            CellPortals = new List<DatCellPortal>
            {
                new() { OtherCellId = 0x0105, PolygonId = 0, OtherPortalId = 0, Flags = (PortalFlags)0 },
            },
            VisibleCells = new List<ushort>(),
        };

        var env = EnvCell.FromDat(0xA9B40104u, dat, cellStruct, Matrix4x4.Identity);

        Assert.Single(env.Portals);
        Assert.Empty(env.Portals[0].PolygonLocal);
    }
}
