using System.Numerics;
using AcDream.App.Rendering.Walk;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed class WalkBuildingRegistryTests
{
    private static WalkBuildingFactory.Entry Entry(uint positionCellId) =>
        new(new WalkBuilding { PositionCellId = positionCellId }, Matrix4x4.Identity, Matrix4x4.Identity);

    [Fact]
    public void Publish_MakesBuildingsFindableByLandblockAndByReference()
    {
        var registry = new WalkBuildingRegistry();
        WalkBuildingFactory.Entry entry = Entry(0xA9B40001u);

        registry.Publish(0xA9B4FFFFu, new[] { entry });

        Assert.Same(entry, Assert.Single(registry.GetBuildings(0xA9B40100u)));
        Assert.True(registry.TryGetEntry(entry.Building, out var found));
        Assert.Same(entry, found);
        Assert.Equal(1, registry.LandblockCount);
    }

    [Fact]
    public void Publish_ReplacesThePreviousLandblockAndDropsItsReverseIndexEntries()
    {
        var registry = new WalkBuildingRegistry();
        WalkBuildingFactory.Entry first = Entry(0xA9B40001u);
        WalkBuildingFactory.Entry second = Entry(0xA9B40002u);
        registry.Publish(0xA9B4FFFFu, new[] { first });

        registry.Publish(0xA9B4FFFFu, new[] { second });

        Assert.Same(second, Assert.Single(registry.GetBuildings(0xA9B4FFFFu)));
        Assert.False(registry.TryGetEntry(first.Building, out _));
        Assert.True(registry.TryGetEntry(second.Building, out _));
        Assert.Equal(1, registry.LandblockCount);
    }

    [Fact]
    public void Retire_RemovesTheLandblockAndItsReverseIndexEntries()
    {
        var registry = new WalkBuildingRegistry();
        WalkBuildingFactory.Entry entry = Entry(0xA9B40001u);
        registry.Publish(0xA9B4FFFFu, new[] { entry });

        registry.Retire(0xA9B40100u);   // any id sharing the landblock prefix

        Assert.Empty(registry.GetBuildings(0xA9B4FFFFu));
        Assert.False(registry.TryGetEntry(entry.Building, out _));
        Assert.Equal(0, registry.LandblockCount);
    }

    [Fact]
    public void Retire_UnknownLandblockIsANoOp()
    {
        var registry = new WalkBuildingRegistry();

        registry.Retire(0xA9B4FFFFu);

        Assert.Equal(0, registry.LandblockCount);
    }

    [Fact]
    public void GetBuildings_UnpublishedLandblockReturnsEmpty()
    {
        var registry = new WalkBuildingRegistry();

        Assert.Empty(registry.GetBuildings(0xA9B4FFFFu));
    }
}
