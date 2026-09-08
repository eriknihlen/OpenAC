using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class CellMarchLandblockPreservationTests
{
    private static int LbX(uint cellId) => (int)((cellId >> 24) & 0xFFu);
    private static int LbY(uint cellId) => (int)((cellId >> 16) & 0xFFu);

    [Fact]
    public void WestEdge_UnstreamedLandblock_NoAnchor_PreservesSeedCell()
    {
        var cache = new PhysicsDataCache();

        // Sphere just west of 0xA9B4's origin (world X = −0.088 → block-local X = −0.088
        // relative to (0,0) origin → floor(−0.088/24) = −1 → lbX marches without fix).
        var spheres = new[] { new DatReaderWriter.Types.Sphere { Origin = new Vector3(-0.088f, 14.8f, 12f), Radius = 0.48f } };
        const uint currentCell = 0xA9B40019u;

        uint result = CellTransit.FindCellSet(cache, spheres, 1, currentCell, out _, null);

        Assert.Equal(0xA9, LbX(result));
        Assert.Equal(currentCell, result);
    }

    [Fact]
    public void SouthEdge_UnstreamedLandblock_NoAnchor_PreservesSeedCell()
    {
        var cache = new PhysicsDataCache();
        var spheres = new[] { new DatReaderWriter.Types.Sphere { Origin = new Vector3(14.8f, -0.088f, 12f), Radius = 0.48f } };
        const uint currentCell = 0xA9B40001u;  // south-west landcell of 0xA9B4

        uint result = CellTransit.FindCellSet(cache, spheres, 1, currentCell, out _, null);

        Assert.Equal(0xB4, LbY(result));
        Assert.Equal(currentCell, result);
    }

    [Fact]
    public void WithCarriedAnchor_UnstreamedNeighbour_ReturnsCorrectCell()
    {
        var cache = new PhysicsDataCache();
        var spheres = new[] { new DatReaderWriter.Types.Sphere { Origin = new Vector3(14.8f, -0.088f, 12f), Radius = 0.48f } };
        const uint currentCell = 0xA9B30001u;
        var anchor = new Vector3(0f, -192f, 0f);

        uint result = CellTransit.FindCellSet(cache, spheres, 1, currentCell, out _, anchor);

        Assert.Equal(0xB3, LbY(result));
    }
}
