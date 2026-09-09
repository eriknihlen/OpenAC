using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.World.Cells;
using Xunit;

namespace AcDream.Core.Tests.World.Cells;

public class CellGraphTests
{
    private static TerrainSurface FlatTerrain() => new TerrainSurface(new byte[81], new float[256]);

    private static EnvCell Env(uint id) => new EnvCell(id, Matrix4x4.Identity, Matrix4x4.Identity,
        Vector3.Zero, new Vector3(10,10,10), System.Array.Empty<CellPortal>(),
        System.Array.Empty<uint>(), false, null);

    [Fact]
    public void GetVisible_ZeroId_ReturnsNull()
        => Assert.Null(new CellGraph().GetVisible(0u));

    [Fact]
    public void GetVisible_EnvId_ReturnsAddedEnvCell()
    {
        var g = new CellGraph();
        var env = Env(0xA9B40174u);
        g.Add(env);
        Assert.Same(env, g.GetVisible(0xA9B40174u));
    }

    [Fact]
    public void GetVisible_UnknownEnvId_ReturnsNull()
        => Assert.Null(new CellGraph().GetVisible(0xA9B40174u));

    [Fact]
    public void GetVisible_LandId_SynthesizesFromRegisteredTerrain()
    {
        var g = new CellGraph();
        g.RegisterTerrain(0xA9B40000u, FlatTerrain(), new Vector3(1000,2000,0));
        var cell = g.GetVisible(0xA9B40014u);
        var land = Assert.IsType<LandCell>(cell);
        Assert.Equal(2, land.Cx);
        Assert.Equal(3, land.Cy);
    }

    [Fact]
    public void GetVisible_LandId_NoTerrain_ReturnsNull()
        => Assert.Null(new CellGraph().GetVisible(0xA9B40014u));

    [Fact]
    public void RemoveLandblock_EvictsEnvAndTerrain()
    {
        var g = new CellGraph();
        g.Add(Env(0xA9B40174u));
        g.RegisterTerrain(0xA9B40000u, FlatTerrain(), Vector3.Zero);
        g.RemoveLandblock(0xA9B40000u);
        Assert.Null(g.GetVisible(0xA9B40174u));
        Assert.Null(g.GetVisible(0xA9B40014u));
    }

    [Fact]
    public void RemoveLandblock_ClearsCurrentCellForRetiredPrefixOnly()
    {
        var g = new CellGraph();
        var current = Env(0xA9B40174u);
        g.Add(current);
        g.CurrCell = current;

        g.RemoveLandblock(0xAAB40000u);
        Assert.Same(current, g.CurrCell);

        g.RemoveLandblock(0xA9B40000u);
        Assert.Null(g.CurrCell);
    }

    [Fact]
    public void RemoveEnvCellsForLandblock_PreservesTerrain()
    {
        var g = new CellGraph();
        g.Add(Env(0xA9B40174u));
        g.RegisterTerrain(0xA9B40000u, FlatTerrain(), Vector3.Zero);

        g.RemoveEnvCellsForLandblock(0xA9B4FFFFu);

        Assert.Null(g.GetVisible(0xA9B40174u));
        Assert.IsType<LandCell>(g.GetVisible(0xA9B40014u));
    }

    [Fact]
    public void RemoveEnvCellsForLandblock_ClearsOnlyMatchingIndoorCurrentCell()
    {
        var g = new CellGraph();
        var current = Env(0xA9B40174u);
        g.Add(current);
        g.CurrCell = current;

        g.RemoveEnvCellsForLandblock(0xAAB4FFFFu);
        Assert.Same(current, g.CurrCell);

        g.RemoveEnvCellsForLandblock(0xA9B4FFFFu);
        Assert.Null(g.CurrCell);
    }

    [Fact]
    public void Neighbor_ResolvesPortalOtherCellId()
    {
        var g = new CellGraph();
        var target = Env(0xA9B40175u);
        g.Add(target);
        var portal = new CellPortal(0xA9B40175u, 0, 0, 0);
        Assert.Same(target, g.Neighbor(Env(0xA9B40174u), portal));
    }

    [Fact]
    public void CurrCell_IsNull_InStage1()
        => Assert.Null(new CellGraph().CurrCell);
}
