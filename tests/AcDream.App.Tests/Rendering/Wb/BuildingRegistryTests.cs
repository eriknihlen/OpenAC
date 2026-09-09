using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering.Wb;
using Xunit;

namespace AcDream.App.Tests.Rendering.Wb;

public class BuildingRegistryTests
{
    private static Building B(uint id, params uint[] cellIds) => new()
    {
        BuildingId = id,
        EnvCellIds = new HashSet<uint>(cellIds),
        ExitPortalPolygons = new List<Vector3[]>(),
    };

    [Fact]
    public void Empty_NoBuildingsRegistered()
    {
        var reg = new BuildingRegistry();
        Assert.Equal(0, reg.Count);
        Assert.Empty(reg.All());
        Assert.Empty(reg.GetBuildingsContainingCell(0xA9B40150u));
        Assert.Null(reg.GetById(0));
    }

    [Fact]
    public void Add_IndexesBothDirections()
    {
        var reg = new BuildingRegistry();
        var b = B(1, 0xA9B40150u, 0xA9B40151u);
        reg.Add(b);

        Assert.Equal(1, reg.Count);
        Assert.Same(b, reg.GetById(1));
        Assert.Single(reg.GetBuildingsContainingCell(0xA9B40150u));
        Assert.Single(reg.GetBuildingsContainingCell(0xA9B40151u));
        Assert.Same(b, reg.GetBuildingsContainingCell(0xA9B40150u)[0]);
        Assert.Empty(reg.GetBuildingsContainingCell(0xDEADBEEFu));
    }

    [Fact]
    public void CellSharedBetweenTwoBuildings_GetBuildingsContainingCellReturnsBoth()
    {
        var reg = new BuildingRegistry();
        var b1 = B(1, 0xA9B40150u, 0xA9B40151u);
        var b2 = B(2, 0xA9B40151u, 0xA9B40152u);  // shares 0151 with b1
        reg.Add(b1);
        reg.Add(b2);

        var bothAt0151 = reg.GetBuildingsContainingCell(0xA9B40151u);
        Assert.Equal(2, bothAt0151.Count);
        Assert.Contains(b1, bothAt0151);
        Assert.Contains(b2, bothAt0151);
    }

    [Fact]
    public void All_EnumeratesEveryBuilding()
    {
        var reg = new BuildingRegistry();
        reg.Add(B(1, 0xA9B40150u));
        reg.Add(B(2, 0xA9B40160u));
        reg.Add(B(3, 0xA9B40170u));

        var ids = new HashSet<uint>();
        foreach (var b in reg.All()) ids.Add(b.BuildingId);

        Assert.Equal(new HashSet<uint> { 1, 2, 3 }, ids);
    }
}
