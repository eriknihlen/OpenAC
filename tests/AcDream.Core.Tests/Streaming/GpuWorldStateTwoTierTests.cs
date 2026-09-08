using System.Linq;
using AcDream.App.Streaming;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using Xunit;

namespace AcDream.Core.Tests.Streaming;

public class GpuWorldStateTwoTierTests
{
    private static LoadedLandblock MakeStubLandblock(uint canonicalId, params WorldEntity[] entities)
        => new(canonicalId, new LandBlock(), entities);

    private static WorldEntity MakeStubEntity(uint id)
        => new()
        {
            Id = id,
            SourceGfxObjOrSetupId = 0x01000001u,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = System.Array.Empty<MeshRef>(),
        };

    [Fact]
    public void RemoveEntitiesFromLandblock_KeepsLandblockButDropsEntities()
    {
        var state = new GpuWorldState();
        var lb = MakeStubLandblock(0xAAAAFFFFu,
            MakeStubEntity(1),
            MakeStubEntity(2));
        state.AddLandblock(lb);
        Assert.Equal(2, state.Entities.Count);

        state.RemoveEntitiesFromLandblock(0xAAAAFFFFu);

        Assert.Empty(state.Entities);
        Assert.True(state.IsLoaded(0xAAAAFFFFu));   // landblock still resident
    }

    [Fact]
    public void RemoveEntitiesFromLandblock_DropsStaticLayerButRetainsLiveProjection()
    {
        var state = new GpuWorldState();
        WorldEntity staticEntity = MakeStubEntity(1);
        var liveEntity = new WorldEntity
        {
            Id = 2,
            ServerGuid = 0x70000002u,
            SourceGfxObjOrSetupId = 0x01000001u,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = System.Array.Empty<MeshRef>(),
        };
        state.AddLandblock(MakeStubLandblock(
            0xAAAAFFFFu,
            staticEntity,
            liveEntity));

        state.RemoveEntitiesFromLandblock(0xAAAAFFFFu);

        Assert.Same(liveEntity, Assert.Single(state.Entities));
        Assert.DoesNotContain(staticEntity, state.Entities);
        Assert.True(state.IsLoaded(0xAAAAFFFFu));
    }

    [Fact]
    public void AddEntitiesToExistingLandblock_MergesIntoExistingRecord()
    {
        var state = new GpuWorldState();
        var lb = MakeStubLandblock(0xAAAAFFFFu, MakeStubEntity(1));
        state.AddLandblock(lb);

        state.AddEntitiesToExistingLandblock(0xAAAAFFFFu, new[]
        {
            MakeStubEntity(2),
            MakeStubEntity(3),
        });

        Assert.Equal(3, state.Entities.Count);
    }

    [Fact]
    public void AddEntitiesToExistingLandblock_LandblockNotYetLoaded_ParksInPending()
    {
        var state = new GpuWorldState();

        // Landblock not loaded yet.
        state.AddEntitiesToExistingLandblock(0xAAAAFFFFu, new[]
        {
            MakeStubEntity(1),
            MakeStubEntity(2),
        });

        // Nothing in the flat view yet.
        Assert.Empty(state.Entities);
        Assert.Equal(2, state.PendingLiveEntityCount);

        // Now load the landblock — pending entities should merge in.
        state.AddLandblock(MakeStubLandblock(0xAAAAFFFFu));
        Assert.Equal(2, state.Entities.Count);
    }

    [Fact]
    public void LandblockEntriesWithoutAnimatedIndex_LeavesAnimatedLookupNull()
    {
        var state = new GpuWorldState();
        state.AddLandblock(MakeStubLandblock(0xAAAAFFFFu,
            MakeStubEntity(1),
            MakeStubEntity(2)));

        var staticEntry = Assert.Single(state.LandblockEntriesWithoutAnimatedIndex);
        Assert.Null(staticEntry.AnimatedById);

        var liveEntry = Assert.Single(state.LandblockEntries);
        Assert.NotNull(liveEntry.AnimatedById);
        Assert.True(liveEntry.AnimatedById!.ContainsKey(1));
        Assert.True(liveEntry.AnimatedById.ContainsKey(2));
    }

    [Fact]
    public void LandblockBounds_EnumeratesWithoutEntityPayload()
    {
        var state = new GpuWorldState();
        state.AddLandblock(MakeStubLandblock(0xAAAAFFFFu,
            MakeStubEntity(1),
            MakeStubEntity(2)));

        var bounds = Assert.Single(state.LandblockBounds);

        Assert.Equal(0xAAAAFFFFu, bounds.LandblockId);
    }

    [Fact]
    public void RemoveEntitiesFromLandblock_FiresUnloadCallbackWithCanonicalId()
    {
        uint? observed = null;
        int callCount = 0;
        var state = new GpuWorldState(
            wbSpawnAdapter: null,
            onLandblockUnloaded: id => { observed = id; callCount++; });

        state.AddLandblock(MakeStubLandblock(0xA9B4FFFFu, MakeStubEntity(1)));

        state.RemoveEntitiesFromLandblock(0xA9B40042u);

        Assert.Equal(1, callCount);
        Assert.Equal(0xA9B4FFFFu, observed);
        Assert.Empty(state.Entities);
    }

    [Fact]
    public void RemoveEntitiesFromLandblock_NotLoaded_DoesNotFireCallback()
    {
        int callCount = 0;
        var state = new GpuWorldState(
            wbSpawnAdapter: null,
            onLandblockUnloaded: _ => callCount++);

        // Landblock never loaded.
        state.RemoveEntitiesFromLandblock(0xA9B4FFFFu);

        Assert.Equal(0, callCount);
    }
}
