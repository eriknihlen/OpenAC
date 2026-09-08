using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Types;
using Xunit;
using DatEnvCell = DatReaderWriter.DBObjs.EnvCell;

namespace AcDream.Core.Tests.Physics;

public class CellGraphMembershipTests
{
    private static PhysicsEngine MakeEngineWithCell(uint cellId)
    {
        var engine = new PhysicsEngine();
        var cache = new PhysicsDataCache();
        engine.DataCache = cache;

        var cs = new CellStruct
        {
            VertexArray = new VertexArray { Vertices = new Dictionary<ushort, SWVertex>() },
            Polygons = new Dictionary<ushort, Polygon>(),
            PhysicsBSP = null!,
            CellBSP = new CellBSPTree
            {
                Root = new CellBSPNode { Type = DatReaderWriter.Enums.BSPNodeType.Leaf },
            },
        };
        var dat = new DatEnvCell
        {
            Flags = (DatReaderWriter.Enums.EnvCellFlags)0,
            CellPortals = new List<DatReaderWriter.Types.CellPortal>(),
            VisibleCells = new List<ushort>(),
        };
        cache.CacheCellStruct(cellId, dat, cs, Matrix4x4.Identity);  // registers in the graph (W1)
        return engine;
    }

    [Fact]
    public void UpdatePlayerCurrCell_Resolved_WritesCurrCellTrackingTheId()
    {
        var engine = MakeEngineWithCell(0xA9B40174u);

        engine.UpdatePlayerCurrCell(0xA9B40174u);

        Assert.NotNull(engine.DataCache!.CellGraph.CurrCell);
        Assert.Equal(0xA9B40174u, engine.DataCache.CellGraph.CurrCell!.Id);
    }

    [Fact]
    public void UpdatePlayerCurrCell_UnresolvableId_LeavesCurrCellUnchanged()
    {
        var engine = MakeEngineWithCell(0xA9B40174u);
        engine.UpdatePlayerCurrCell(0xA9B40174u);

        engine.UpdatePlayerCurrCell(0xDEADBEEFu);   // not in the graph → stale beats null

        Assert.NotNull(engine.DataCache!.CellGraph.CurrCell);
        Assert.Equal(0xA9B40174u, engine.DataCache.CellGraph.CurrCell!.Id);
    }

    [Fact]
    public void ResolveCellId_DoesNotWriteTheRenderRoot()
    {
        var engine = MakeEngineWithCell(0xA9B40174u);

        uint result = engine.ResolveCellId(new Vector3(0, 0, 0), 0.5f, 0xA9B40174u);

        Assert.Equal(0xA9B40174u, result);
        Assert.Null(engine.DataCache!.CellGraph.CurrCell);    // but does NOT write the render root
    }
}
