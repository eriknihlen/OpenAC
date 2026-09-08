
using System.Numerics;
using AcDream.App.Rendering;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class CellVisibilityPortalPolygonsTests
{
    [Fact]
    public void LoadedCell_DefaultPortalPolygons_IsEmpty()
    {
        var cell = new LoadedCell();
        Assert.NotNull(cell.PortalPolygons);
        Assert.Empty(cell.PortalPolygons);
    }

    [Fact]
    public void LoadedCell_PortalPolygons_ParallelIndexedToPortals()
    {
        var cell = new LoadedCell
        {
            Portals = new()
            {
                new CellPortalInfo(0xFFFF, 100, 0, 0),  // exit portal, has geometry
                new CellPortalInfo(0x0102, 101, 0, 0),  // inner portal, no geometry resolved
            },
            ClipPlanes = new() { default, default },
            PortalPolygons = new()
            {
                new[]
                {
                    new Vector3(0, 0, 0),
                    new Vector3(1, 0, 0),
                    new Vector3(1, 1, 0),
                    new Vector3(0, 1, 0),
                },
                System.Array.Empty<Vector3>(),
            },
        };

        Assert.Equal(2, cell.Portals.Count);
        Assert.Equal(2, cell.PortalPolygons.Count);
        Assert.Equal(4, cell.PortalPolygons[0].Length);
        Assert.Empty(cell.PortalPolygons[1]);
    }
}
