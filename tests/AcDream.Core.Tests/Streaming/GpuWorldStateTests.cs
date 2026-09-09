using System;
using AcDream.App.Streaming;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using Xunit;

namespace AcDream.Core.Tests.Streaming;

public class GpuWorldStateTests
{
    private static LoadedLandblock MakeStubLandblock(uint canonicalId)
        => new(canonicalId, new LandBlock(), Array.Empty<WorldEntity>());

    private static WorldEntity MakeStubEntity(uint id)
        => new()
        {
            Id = id,
            SourceGfxObjOrSetupId = 0x01000001u,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
        };

    [Fact]
    public void PlaceLiveEntityProjection_LandblockAlreadyLoaded_AppendsImmediately()
    {
        var state = new GpuWorldState();
        state.AddLandblock(MakeStubLandblock(0xA9B4FFFFu));

        state.PlaceLiveEntityProjection(0xA9B40011u, MakeStubEntity(42));

        Assert.Single(state.Entities);
        Assert.Equal(0u, (uint)state.PendingLiveEntityCount);
    }

    [Fact]
    public void PlaceLiveEntityProjection_LandblockNotLoaded_ParksInPending()
    {
        var state = new GpuWorldState();

        state.PlaceLiveEntityProjection(0xA9B40011u, MakeStubEntity(42));

        Assert.Empty(state.Entities);  // not visible yet
        Assert.Equal(1, state.PendingLiveEntityCount);
    }

    [Fact]
    public void AddLandblock_DrainsPendingEntriesForThatLandblock()
    {
        var state = new GpuWorldState();

        // Three spawns arrive before the landblock loads.
        state.PlaceLiveEntityProjection(0xA9B40011u, MakeStubEntity(1));
        state.PlaceLiveEntityProjection(0xA9B40022u, MakeStubEntity(2));
        state.PlaceLiveEntityProjection(0xA9B40033u, MakeStubEntity(3));
        Assert.Equal(3, state.PendingLiveEntityCount);
        Assert.Empty(state.Entities);

        // Now the landblock streams in.
        state.AddLandblock(MakeStubLandblock(0xA9B4FFFFu));

        // The three pending entities are now visible, and the pending
        // bucket for that landblock is empty.
        Assert.Equal(3, state.Entities.Count);
        Assert.Equal(0, state.PendingLiveEntityCount);
    }

    [Fact]
    public void AddLandblock_DoesNotDrainPendingForADifferentLandblock()
    {
        var state = new GpuWorldState();

        state.PlaceLiveEntityProjection(0xA9B40011u, MakeStubEntity(1));
        state.PlaceLiveEntityProjection(0xAAAA0022u, MakeStubEntity(2));

        state.AddLandblock(MakeStubLandblock(0xA9B4FFFFu));

        Assert.Single(state.Entities);
        Assert.Equal(1, state.PendingLiveEntityCount);
    }

    [Fact]
    public void RebucketLiveEntity_StrandedInPending_MovesToLoadedTarget()
    {
        var state = new GpuWorldState();

        var player = new WorldEntity
        {
            Id = 1,
            ServerGuid = 0x5000000Au,     // server-spawned + persistent
            SourceGfxObjOrSetupId = 0x01000001u,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
        };
        state.MarkPersistent(0x5000000Au);

        // Spawned before its landblock streamed in → parked in pending, hidden.
        state.PlaceLiveEntityProjection(0xADAF0011u, player);
        Assert.Empty(state.Entities);
        Assert.Equal(1, state.PendingLiveEntityCount);

        state.AddLandblock(MakeStubLandblock(0xBBBBFFFFu));
        state.RebucketLiveEntity(player, 0xBBBB0011u);

        Assert.Single(state.Entities);                 // now drawn
        Assert.Equal(0, state.PendingLiveEntityCount); // no longer stranded
    }

    [Fact]
    public void RemoveLandblock_DropsPendingForThatLandblock()
    {
        var state = new GpuWorldState();

        state.PlaceLiveEntityProjection(0xA9B40011u, MakeStubEntity(1));
        state.PlaceLiveEntityProjection(0xA9B40022u, MakeStubEntity(2));
        Assert.Equal(2, state.PendingLiveEntityCount);

        state.RemoveLandblock(0xA9B4FFFFu);

        Assert.Equal(0, state.PendingLiveEntityCount);
        Assert.Empty(state.Entities);
    }

    [Fact]
    public void RemoveLandblock_LoadedThenRemoved_DropsItsEntities()
    {
        var state = new GpuWorldState();

        state.AddLandblock(MakeStubLandblock(0xA9B4FFFFu));
        state.PlaceLiveEntityProjection(0xA9B40011u, MakeStubEntity(1));
        Assert.Single(state.Entities);

        state.RemoveLandblock(0xA9B4FFFFu);

        Assert.Empty(state.Entities);
    }

    [Fact]
    public void IsLoaded_ReturnsTrueForLoaded_FalseForPendingOnly()
    {
        var state = new GpuWorldState();

        state.PlaceLiveEntityProjection(0xA9B40011u, MakeStubEntity(1));
        Assert.False(state.IsLoaded(0xA9B4FFFFu));

        state.AddLandblock(MakeStubLandblock(0xA9B4FFFFu));
        Assert.True(state.IsLoaded(0xA9B4FFFFu));
    }

    private static WorldEntity MakeServerEntity(uint id, uint serverGuid)
        => new()
        {
            Id = id,
            ServerGuid = serverGuid,
            SourceGfxObjOrSetupId = 0x01000001u,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
        };

    [Fact]
    public void RemoveLandblock_RescuesPersistentEntity_FromPendingBucket()
    {
        var state = new GpuWorldState();
        const uint playerGuid = 0x50000001u;
        state.MarkPersistent(playerGuid);

        var player = MakeServerEntity(id: 1, serverGuid: playerGuid);
        state.PlaceLiveEntityProjection(0xA9B40011u, player);   // landblock not loaded → pending
        Assert.Equal(1, state.PendingLiveEntityCount);

        state.RemoveLandblock(0xA9B4FFFFu);            // unloaded while still pending

        Assert.Contains(player, state.DrainRescued()); // rescued, not dropped
    }

    [Fact]
    public void RemoveLandblock_RetainsNonPersistentLiveProjectionForSameIdentityReload()
    {
        var state = new GpuWorldState();
        var door = MakeServerEntity(id: 2, serverGuid: 0x7A9B4001u);  // not marked persistent
        state.PlaceLiveEntityProjection(0xA9B40011u, door);

        state.RemoveLandblock(0xA9B4FFFFu);

        Assert.Empty(state.DrainRescued());
        Assert.Equal(1, state.PendingLiveEntityCount);
        state.AddLandblock(MakeStubLandblock(0xA9B4FFFFu));
        Assert.Same(door, Assert.Single(state.Entities));
    }

    [Fact]
    public void RemoveLandblock_LoadedLiveProjectionMovesToPendingAndReloadsSameIdentity()
    {
        var state = new GpuWorldState();
        var door = MakeServerEntity(id: 3, serverGuid: 0x7A9B4002u);
        state.AddLandblock(MakeStubLandblock(0xA9B4FFFFu));
        state.PlaceLiveEntityProjection(0xA9B40011u, door);

        state.RemoveLandblock(0xA9B4FFFFu);

        Assert.Empty(state.Entities);
        Assert.Equal(1, state.PendingLiveEntityCount);
        state.AddLandblock(MakeStubLandblock(0xA9B4FFFFu));
        Assert.Same(door, Assert.Single(state.Entities));
    }
}
