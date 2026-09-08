using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.World.Cells;
using Xunit;

namespace AcDream.Core.Tests.World.Cells;

public class LandCellTests
{
    private static TerrainSurface FlatTerrain()
        => new TerrainSurface(new byte[81], new float[256], landblockX: 0, landblockY: 0);

    [Fact]
    public void Synthesize_SetsCellIndicesAndQuadBounds()
    {
        var origin = new Vector3(1000f, 2000f, 0f);
        var cell = LandCell.Synthesize(0xA9B40014u, FlatTerrain(), origin, cx: 2, cy: 3);
        Assert.Equal(2, cell.Cx);
        Assert.Equal(3, cell.Cy);
        Assert.Equal(2 * 24f, cell.LocalBoundsMin.X);
        Assert.Equal(3 * 24f, cell.LocalBoundsMin.Y);
        Assert.Equal(3 * 24f, cell.LocalBoundsMax.X);
        Assert.Equal(4 * 24f, cell.LocalBoundsMax.Y);
        Assert.False(cell.IsEnv);
    }

    [Fact]
    public void PointInCell_TestsWorldXyAgainstThe24mQuad()
    {
        var origin = new Vector3(1000f, 2000f, 0f);
        var cell = LandCell.Synthesize(0xA9B40014u, FlatTerrain(), origin, cx: 2, cy: 3);
        Assert.True(cell.PointInCell(new Vector3(1060f, 2080f, 12.3f)));
        Assert.False(cell.PointInCell(new Vector3(1000f, 2080f, 0f)));
        Assert.False(cell.PointInCell(new Vector3(1060f, 2100f, 0f)));
    }
}
