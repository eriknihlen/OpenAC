using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class TerrainSurfaceBlockWaterTests
{
    private const byte WaterVertex = 0x10 << 2;
    private const byte LandVertex = 0x01 << 2;

    [Fact]
    public void BlockWaterType_ReflectsEveryCell()
    {
        Assert.Equal(2, Surface(WaterVertex).BlockWaterType);
        Assert.True(Surface(WaterVertex).IsEntirelyWater);

        byte[] shore = FilledTerrain(WaterVertex);
        shore[0] = LandVertex;                    // one dry corner in one cell
        Assert.Equal(1, Surface(shore).BlockWaterType);
        Assert.False(Surface(shore).IsEntirelyWater);

        Assert.Equal(0, Surface(LandVertex).BlockWaterType);
    }

    private static byte[] FilledTerrain(byte vertex)
    {
        var terrain = new byte[81];
        Array.Fill(terrain, vertex);
        return terrain;
    }

    private static TerrainSurface Surface(byte vertex) => Surface(FilledTerrain(vertex));

    private static TerrainSurface Surface(byte[] terrain) =>
        new(new byte[81], new float[256], 0xA9u, 0xB4u, terrain);
}
