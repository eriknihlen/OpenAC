using AcDream.Core.Physics;

namespace AcDream.Core.Tests.Physics;

public sealed class NeighborhoodTerrainResidencyTests
{
    private const int FarRadius = 12;

    [Fact]
    public void WarmedNeighborhoodQuery_AtTheFarRadius_AllocatesNothing()
    {
        PhysicsEngine engine = EngineWithWindow(0x80, 0x80, FarRadius);
        uint center = Canonical(0x80, 0x80);

        // Warm the JIT, the scratch set's buckets/entries arrays, and the
        // dictionary enumerator before measuring.
        for (int i = 0; i < 64; i++)
            Assert.True(engine.IsNeighborhoodTerrainResident(center, FarRadius));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
            Assert.True(engine.IsNeighborhoodTerrainResident(center, FarRadius));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated == 0,
            $"1,000 radius-{FarRadius} residency queries allocated "
            + $"{allocated:N0} bytes");
    }

    [Fact]
    public void NeighborhoodQuery_StillRejectsAMissingOuterRingMember()
    {
        PhysicsEngine engine = EngineWithWindow(0x80, 0x80, FarRadius);
        uint center = Canonical(0x80, 0x80);

        Assert.True(engine.IsNeighborhoodTerrainResident(center, FarRadius));

        engine.RemoveLandblock(Canonical(0x80 + FarRadius, 0x80));

        Assert.False(engine.IsNeighborhoodTerrainResident(center, FarRadius));
        Assert.True(engine.IsNeighborhoodTerrainResident(center, FarRadius - 1));
    }

    [Fact]
    public void NeighborhoodQuery_KeepsPrefixMaskedMembership()
    {
        var engine = new PhysicsEngine();
        AddTerrain(engine, (0x40u << 24) | (0x40u << 16) | 0x0021u);

        Assert.True(
            engine.IsNeighborhoodTerrainResident(Canonical(0x40, 0x40), 0));
        Assert.False(
            engine.IsNeighborhoodTerrainResident(Canonical(0x41, 0x40), 0));
    }

    private static PhysicsEngine EngineWithWindow(int cx, int cy, int radius)
    {
        var engine = new PhysicsEngine();
        for (int dx = -radius; dx <= radius; dx++)
        for (int dy = -radius; dy <= radius; dy++)
            AddTerrain(engine, Canonical(cx + dx, cy + dy));
        return engine;
    }

    private static uint Canonical(int x, int y) =>
        ((uint)x << 24) | ((uint)y << 16) | 0xFFFFu;

    private static void AddTerrain(PhysicsEngine engine, uint landblockId)
    {
        var heights = new byte[81];
        var heightTable = new float[256];
        engine.AddLandblock(
            landblockId,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
    }
}
