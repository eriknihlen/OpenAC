using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using System.Numerics;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class CompositeWarmupEntitySourceTests
{
    [Fact]
    public void RefreshTracksOnlyDestinationNeighborhoodMembership()
    {
        var center = Entity(1);
        var neighbor = Entity(2);
        var far = Entity(3);
        var world = new GpuWorldState();
        world.AddLandblock(Landblock(0x1010FFFFu, center));
        world.AddLandblock(Landblock(0x1110FFFFu, neighbor));
        world.AddLandblock(Landblock(0x1310FFFFu, far));
        var source = new CompositeWarmupEntitySource(world);

        source.Refresh(0x10100001u, radius: 1);

        Assert.Equal(world.FlatViewGeneration, source.Generation);
        Assert.Equal([center, neighbor], source.Entities);
    }

    [Fact]
    public void RefreshObservesLaterMembershipWithoutReplacingItsStableView()
    {
        var center = Entity(1);
        var later = Entity(2);
        var world = new GpuWorldState();
        world.AddLandblock(Landblock(0x1010FFFFu, center));
        var source = new CompositeWarmupEntitySource(world);
        source.Refresh(0x10100001u, radius: 0);
        IReadOnlyList<WorldEntity> stableView = source.Entities;

        world.AddLandblock(Landblock(0x1010FFFFu, center, later));
        source.Refresh(0x10100001u, radius: 0);

        Assert.Same(stableView, source.Entities);
        Assert.Equal([center, later], source.Entities);
        Assert.Equal(world.FlatViewGeneration, source.Generation);
    }

    private static LoadedLandblock Landblock(
        uint id,
        params WorldEntity[] entities) =>
        new(id, new LandBlock(), entities);

    private static WorldEntity Entity(uint id) => new()
    {
        Id = id,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };
}
