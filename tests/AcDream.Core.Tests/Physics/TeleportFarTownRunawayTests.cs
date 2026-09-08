using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class TeleportFarTownRunawayTests
{
    private static int LbX(uint cellId) => (int)((cellId >> 24) & 0xFFu);
    private static int LbY(uint cellId) => (int)((cellId >> 16) & 0xFFu);

    [Fact]
    public void SouthEdge_UnstreamedNeighbour_CarriedAnchor_DoesNotMarch()
    {
        var cache = new PhysicsDataCache();
        var spheres = new[] { new DatReaderWriter.Types.Sphere { Origin = new Vector3(14.8f, -0.088f, 12f), Radius = 0.48f } };
        const uint currentCell = 0xC95A0001u;
        var anchor = new Vector3(0f, -192f, 0f);

        uint withAnchor = CellTransit.FindCellSet(cache, spheres, 1, currentCell, out _, anchor);
        Assert.Equal(0x5A, LbY(withAnchor));       // stays in 0xC95A — no march

        uint noAnchor = CellTransit.FindCellSet(cache, spheres, 1, currentCell, out _, null);
        Assert.Equal(currentCell, noAnchor);
    }

    [Fact]
    public void EastEdge_UnstreamedNeighbour_CarriedAnchor_DoesNotMarch()
    {
        var cache = new PhysicsDataCache();
        var spheres = new[] { new DatReaderWriter.Types.Sphere { Origin = new Vector3(192.088f, 14.8f, 12f), Radius = 0.48f } };
        const uint currentCell = 0xCA5B0001u;
        var anchor = new Vector3(192f, 0f, 0f);

        uint withAnchor = CellTransit.FindCellSet(cache, spheres, 1, currentCell, out _, anchor);
        Assert.Equal(0xCA, LbX(withAnchor));       // stays in 0xCA5B — no march

        uint noAnchor = CellTransit.FindCellSet(cache, spheres, 1, currentCell, out _, null);
        Assert.Equal(currentCell, noAnchor);
    }
}
