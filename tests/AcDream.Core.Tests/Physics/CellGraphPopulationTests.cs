using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.World.Cells;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;
using DatEnvCell = DatReaderWriter.DBObjs.EnvCell;

namespace AcDream.Core.Tests.Physics;

public class CellGraphPopulationTests
{
    [Fact]
    public void CacheCellStruct_RejectsRootlessContainment_ThenAllowsValidRetry()
    {
        var cache = new PhysicsDataCache();
        var cellStruct = new CellStruct
        {
            VertexArray = new VertexArray { Vertices = new Dictionary<ushort, SWVertex>() },
            Polygons = new Dictionary<ushort, Polygon>(),
            CellBSP = new CellBSPTree { Root = null },
        };
        var dat = new DatEnvCell
        {
            Flags = (DatReaderWriter.Enums.EnvCellFlags)0,
            CellPortals = new List<DatReaderWriter.Types.CellPortal>(),
            VisibleCells = new List<ushort>(),
        };

        cache.CacheCellStruct(0xA9B40174u, dat, cellStruct, Matrix4x4.Identity);

        Assert.Null(cache.GetCellStruct(0xA9B40174u));
        Assert.Null(cache.CellGraph.GetVisible(0xA9B40174u));

        cellStruct.CellBSP.Root = new CellBSPNode { Type = BSPNodeType.Leaf };
        cache.CacheCellStruct(0xA9B40174u, dat, cellStruct, Matrix4x4.Identity);

        CellPhysics loaded = Assert.IsType<CellPhysics>(
            cache.GetCellStruct(0xA9B40174u));
        Assert.False(CollisionTraversal.HasPhysics(cache, loaded));
        Assert.True(CollisionTraversal.HasCellContainment(cache, loaded));
        Assert.NotNull(cache.CellGraph.GetVisible(0xA9B40174u));
        Assert.IsType<EnvCell>(cache.CellGraph.GetVisible(0xA9B40174u));
    }
}
