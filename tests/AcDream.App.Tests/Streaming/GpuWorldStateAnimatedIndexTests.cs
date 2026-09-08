using System.Numerics;
using AcDream.App.Streaming;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Streaming;

public sealed class GpuWorldStateAnimatedIndexTests
{
    private const uint Landblock = 0x0101FFFFu;

    [Fact]
    public void StableLandblock_ReusesAnimatedIndexAcrossFrames()
    {
        var state = new GpuWorldState();
        WorldEntity entity = Entity(1u, 0u);
        state.AddLandblock(Loaded(entity));

        IReadOnlyDictionary<uint, WorldEntity>? first = SingleIndex(state);
        IReadOnlyDictionary<uint, WorldEntity>? second = SingleIndex(state);

        Assert.Same(first, second);
        Assert.Same(entity, first![entity.Id]);
    }

    [Fact]
    public void ReplacedLandblockRecord_RebuildsOnlyThatGeneration()
    {
        var state = new GpuWorldState();
        WorldEntity original = Entity(1u, 0u);
        WorldEntity live = Entity(2u, 0x70000002u);
        state.AddLandblock(Loaded(original));
        IReadOnlyDictionary<uint, WorldEntity>? first = SingleIndex(state);

        state.PlaceLiveEntityProjection(Landblock, live);
        IReadOnlyDictionary<uint, WorldEntity>? second = SingleIndex(state);

        Assert.NotSame(first, second);
        Assert.Same(original, second![original.Id]);
        Assert.Same(live, second[live.Id]);
        Assert.Same(second, SingleIndex(state));
    }

    [Fact]
    public void RemoveThenReload_DoesNotRetainRemovedGeneration()
    {
        var state = new GpuWorldState();
        WorldEntity removed = Entity(1u, 0u);
        state.AddLandblock(Loaded(removed));
        IReadOnlyDictionary<uint, WorldEntity>? first = SingleIndex(state);

        state.RemoveLandblock(Landblock);
        WorldEntity replacement = Entity(2u, 0u);
        state.AddLandblock(Loaded(replacement));
        IReadOnlyDictionary<uint, WorldEntity>? second = SingleIndex(state);

        Assert.NotSame(first, second);
        Assert.False(second!.ContainsKey(removed.Id));
        Assert.Same(replacement, second[replacement.Id]);
    }

    private static IReadOnlyDictionary<uint, WorldEntity>? SingleIndex(GpuWorldState state) =>
        Assert.Single(state.LandblockEntries).AnimatedById;

    private static LoadedLandblock Loaded(params WorldEntity[] entities) =>
        new(Landblock, new LandBlock(), entities);

    private static WorldEntity Entity(uint id, uint serverGuid) => new()
    {
        Id = id,
        ServerGuid = serverGuid,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };
}
