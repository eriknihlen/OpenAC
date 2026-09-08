
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.World.Cells;
using DatReaderWriter.Enums;
using Xunit;
using CellBSPNode = DatReaderWriter.Types.CellBSPNode;
using CellBSPTree = DatReaderWriter.Types.CellBSPTree;

namespace AcDream.Core.Tests.Rendering;

public class CellGraphRootTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static EnvCell MakeEnvCell(uint id, Vector3 min, Vector3 max, bool seenOutside = false)
        => new EnvCell(
            id,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            min, max,
            portals: new List<CellPortal> { new(0xFFFFu, 0, 0, 0) },
            stabList: new List<uint>(),
            seenOutside: seenOutside,
            containmentBsp: new CellBSPTree { Root = BoundsBsp(min, max) });

    /// <summary>
    /// EnvCell with an explicit stab list (used by FindVisibleChildCell tests).
    /// </summary>
    private static EnvCell MakeEnvCellWithStab(uint id, Vector3 min, Vector3 max,
        IReadOnlyList<uint> stabList, bool seenOutside = false)
        => new EnvCell(
            id,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            min, max,
            portals: new List<CellPortal> { new(0xFFFFu, 0, 0, 0) },
            stabList: stabList,
            seenOutside: seenOutside,
            containmentBsp: new CellBSPTree { Root = BoundsBsp(min, max) });

    private static CellBSPNode BoundsBsp(Vector3 min, Vector3 max)
    {
        var leaf = new CellBSPNode { Type = BSPNodeType.Leaf };
        CellBSPNode Add(Plane plane, CellBSPNode positive) => new()
        {
            Type = BSPNodeType.BPIn,
            SplittingPlane = plane,
            PosNode = positive,
        };

        CellBSPNode root = leaf;
        root = Add(new Plane(-Vector3.UnitZ, max.Z), root);
        root = Add(new Plane(Vector3.UnitZ, -min.Z), root);
        root = Add(new Plane(-Vector3.UnitY, max.Y), root);
        root = Add(new Plane(Vector3.UnitY, -min.Y), root);
        root = Add(new Plane(-Vector3.UnitX, max.X), root);
        root = Add(new Plane(Vector3.UnitX, -min.X), root);
        return root;
    }

    // ------------------------------------------------------------------
    // Predicate helpers — mirror the formulas in GameWindow.OnRender (Stage 3)
    // ------------------------------------------------------------------

    private static bool RootSeenOutside(EnvCell? physicsRoot) => physicsRoot?.SeenOutside ?? true;

    private static bool PlayerInsideCell(bool cameraInsideCell, bool rootSeenOutside)
        => cameraInsideCell && !rootSeenOutside;

    // Stage 3 sky gate: visible unless inside a sealed dungeon.
    private static bool RenderSky(bool cameraInsideCell, bool rootSeenOutside)
        => !cameraInsideCell || rootSeenOutside;


    [Fact]
    public void RootSelection_OutdoorRoot_NullCurrCell_SeenOutsideDefaultsToTrue()
    {
        bool rootSeenOutside = RootSeenOutside(null);
        bool cameraInsideCell = false;

        bool playerInsideCell = PlayerInsideCell(cameraInsideCell, rootSeenOutside);
        bool renderSky        = RenderSky(cameraInsideCell, rootSeenOutside);

        Assert.True(rootSeenOutside,    "outdoor null root → rootSeenOutside=true");
        Assert.False(playerInsideCell,  "outdoor → playerInsideCell=false (sun kept live)");
        Assert.True(renderSky,          "outdoor → sky rendered");
    }


    [Fact]
    public void RootSelection_BuildingInterior_SeenOutside_SkyRenderedAndSunKept()
    {
        var physicsRoot = MakeEnvCell(0xA9B40170u, Vector3.Zero, new Vector3(10, 10, 10),
            seenOutside: true);

        bool rootSeenOutside  = RootSeenOutside(physicsRoot);
        bool cameraInsideCell = true;

        bool playerInsideCell = PlayerInsideCell(cameraInsideCell, rootSeenOutside);
        bool renderSky        = RenderSky(cameraInsideCell, rootSeenOutside);

        Assert.True(rootSeenOutside,    "building interior seen_outside=true");
        Assert.False(playerInsideCell,  "seen_outside=true → sun kept live");
        Assert.True(renderSky,          "seen_outside=true → sky rendered (Stage 4 will clip to doorway)");
    }

    // ------------------------------------------------------------------
    // Test 3: sealed dungeon (seen_outside=false) — sky suppressed, sun zeroed.
    // ------------------------------------------------------------------

    [Fact]
    public void RootSelection_Dungeon_NoSeenOutside_SkyNotRenderedAndSunZeroed()
    {
        var physicsRoot = MakeEnvCell(0x01D90100u, Vector3.Zero, new Vector3(10, 10, 10),
            seenOutside: false);

        bool rootSeenOutside  = RootSeenOutside(physicsRoot);
        bool cameraInsideCell = true;

        bool playerInsideCell = PlayerInsideCell(cameraInsideCell, rootSeenOutside);
        bool renderSky        = RenderSky(cameraInsideCell, rootSeenOutside);

        Assert.False(rootSeenOutside,   "dungeon seen_outside=false");
        Assert.True(playerInsideCell,   "sealed dungeon → playerInsideCell=true (sun zeroed)");
        Assert.False(renderSky,         "sealed dungeon → sky suppressed");
    }


    [Fact]
    public void FindVisibleChildCell_PlayerCellContains_ReturnsPlayerCell()
    {
        var graph = new CellGraph();
        var root = MakeEnvCell(0xA9B40170u, Vector3.Zero, new Vector3(10, 10, 10));
        graph.Add(root);

        var point = new Vector3(5, 5, 5); // inside root's bounds
        var result = graph.FindVisibleChildCell(root.Id, point);

        Assert.NotNull(result);
        Assert.Equal(root.Id, result!.Id);
    }


    [Fact]
    public void FindVisibleChildCell_StabListContains_ReturnsNeighbour()
    {
        var graph = new CellGraph();

        // Root at [0,10]. Neighbour B at [20,30]. Root's stab list includes B.
        uint rootId = 0xA9B40170u;
        uint neighId = 0xA9B40171u;

        var neigh = MakeEnvCell(neighId, new Vector3(20, 0, 0), new Vector3(30, 10, 10));
        var root  = MakeEnvCellWithStab(rootId, Vector3.Zero, new Vector3(10, 10, 10),
            stabList: new List<uint> { neighId });

        graph.Add(root);
        graph.Add(neigh);

        var point = new Vector3(25, 5, 5); // inside neighbour, outside root
        var result = graph.FindVisibleChildCell(rootId, point);

        Assert.NotNull(result);
        Assert.Equal(neighId, result!.Id);
    }


    [Fact]
    public void FindVisibleChildCell_NeitherContains_ReturnsNull()
    {
        var graph = new CellGraph();
        uint rootId = 0xA9B40170u;

        var root = MakeEnvCellWithStab(rootId, Vector3.Zero, new Vector3(10, 10, 10),
            stabList: new List<uint>());
        graph.Add(root);

        var pointFarAway = new Vector3(999, 999, 999);
        var result = graph.FindVisibleChildCell(rootId, pointFarAway);

        Assert.Null(result);
    }
}
