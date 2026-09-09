using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class ResolveCellIdTests
{
    [Fact]
    public void ResolveCellId_FallbackZero_ReturnsZero()
    {
        var engine = new PhysicsEngine();
        uint result = engine.ResolveCellId(Vector3.Zero, sphereRadius: 0.5f, fallbackCellId: 0u);
        Assert.Equal(0u, result);
    }

    [Fact]
    public void ResolveCellId_NoLandblock_OutdoorSeed_ReturnsFallback()
    {
        var engine = new PhysicsEngine();
        engine.DataCache = new PhysicsDataCache();
        uint result = engine.ResolveCellId(
            new Vector3(100, 100, 0),
            sphereRadius: 0.5f,
            fallbackCellId: 0xA9B40001u);

        Assert.Equal(0xA9B40001u, result);
    }

    [Fact]
    public void ResolveCellId_NoDataCache_ReturnsFallback()
    {
        // Build a PhysicsEngine without setting DataCache.
        var engine = new PhysicsEngine { DataCache = null };
        uint result = engine.ResolveCellId(
            new Vector3(100, 100, 0),
            sphereRadius: 0.5f,
            fallbackCellId: 0xA9B40100u);  // indoor seed
        // Indoor branch falls back when DataCache is null.
        Assert.Equal(0xA9B40100u, result);
    }
}
