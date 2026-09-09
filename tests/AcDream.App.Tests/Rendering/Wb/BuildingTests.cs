using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering.Wb;
using Xunit;

namespace AcDream.App.Tests.Rendering.Wb;

public class BuildingTests
{
    [Fact]
    public void Building_RequiredFields_PopulateCorrectly()
    {
        var b = new Building
        {
            BuildingId = 42,
            EnvCellIds = new HashSet<uint> { 0xA9B40150u, 0xA9B40151u },
            ExitPortalPolygons = new List<Vector3[]>
            {
                new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0) },
            },
        };

        Assert.Equal(42u, b.BuildingId);
        Assert.Equal(2, b.EnvCellIds.Count);
        Assert.Contains(0xA9B40150u, b.EnvCellIds);
        Assert.Single(b.ExitPortalPolygons);
        Assert.Equal(3, b.ExitPortalPolygons[0].Length);
    }

    [Fact]
    public void Building_OcclusionQueryState_DefaultsZero()
    {
        var b = new Building
        {
            BuildingId = 0,
            EnvCellIds = new HashSet<uint>(),
            ExitPortalPolygons = new List<Vector3[]>(),
        };

        Assert.Equal(0u, b.QueryId);
        Assert.False(b.QueryStarted);
        Assert.False(b.WasVisible);
    }
}
