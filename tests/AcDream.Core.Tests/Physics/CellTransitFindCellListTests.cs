using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class CellTransitFindCellListTests
{
    [Fact]
    public void IndoorSeed_NoCacheEntry_ReturnsFallback()
    {
        var cache = new PhysicsDataCache();
        uint result = CellTransit.FindCellList(
            cache,
            worldSphereCenter: Vector3.Zero,
            sphereRadius: 0.5f,
            currentCellId: 0xA9B40100u);

        Assert.Equal(0xA9B40100u, result);
    }

    [Fact]
    public void OutdoorSeed_Returns_FallbackWhenNoCellBSPs()
    {
        var cache = new PhysicsDataCache();
        uint result = CellTransit.FindCellList(
            cache,
            worldSphereCenter: new Vector3(12f, 12f, 0f),
            sphereRadius: 0.5f,
            currentCellId: 0xA9B40001u);

        Assert.Equal(0xA9B40001u, result);
    }
}
