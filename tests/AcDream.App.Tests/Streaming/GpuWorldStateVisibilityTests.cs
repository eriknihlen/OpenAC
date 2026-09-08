using System.Numerics;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Streaming;

public sealed class GpuWorldStateVisibilityTests
{
    [Fact]
    public void PendingSpawnFlood_DoesNotPublishOrRebuildLoadedView()
    {
        const uint landblock = 0x0101FFFFu;
        const int count = 10_000;
        var state = new GpuWorldState();
        int edges = 0;
        state.LiveProjectionVisibilityChanged += (_, _) => edges++;
        long commitsBefore = state.VisibilityCommitCount;

        using (state.BeginMutationBatch())
        {
            for (int i = 0; i < count; i++)
            {
                state.PlaceLiveEntityProjection(
                    landblock,
                    Entity((uint)(i + 1), 0x70000000u + (uint)i));
            }

            Assert.Empty(state.Entities);
            Assert.Equal(count, state.PendingLiveEntityCount);
            Assert.Equal(commitsBefore, state.VisibilityCommitCount);
            Assert.Equal(0, edges);
        }

        Assert.Empty(state.Entities);
        Assert.Equal(count, state.PendingLiveEntityCount);
        Assert.Equal(commitsBefore + 1, state.VisibilityCommitCount);
        Assert.Equal(0, edges);
    }

    [Fact]
    public void LoadedSpawnFlood_AppendsInPlaceAndPublishesOneBatch()
    {
        const uint landblock = 0x0101FFFFu;
        const int count = 10_000;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            landblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        Assert.True(state.TryGetLandblock(landblock, out LoadedLandblock? loaded));
        IReadOnlyList<WorldEntity> residentBucket = loaded!.Entities;
        int edges = 0;
        state.LiveProjectionVisibilityChanged += (_, visible) =>
        {
            Assert.True(visible);
            edges++;
        };
        long commitsBefore = state.VisibilityCommitCount;

        using (state.BeginMutationBatch())
        {
            for (int i = 0; i < count; i++)
            {
                state.PlaceLiveEntityProjection(
                    landblock,
                    Entity((uint)(i + 1), 0x71000000u + (uint)i));
            }

            Assert.Same(residentBucket, loaded.Entities);
            Assert.Equal(count, residentBucket.Count);
            Assert.Equal(count, state.Entities.Count);
            Assert.Equal(0, edges);
            Assert.Equal(commitsBefore, state.VisibilityCommitCount);
        }

        Assert.Equal(count, edges);
        Assert.Equal(commitsBefore + 1, state.VisibilityCommitCount);
        Assert.Equal(count, state.Entities.Count);
    }

    [Fact]
    public void DenseSameBucketRebucketAndDelete_PreserveEveryProjectionIndex()
    {
        const uint sourceLandblock = 0x0101FFFFu;
        const uint targetLandblock = 0x0202FFFFu;
        const int count = 10_000;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            sourceLandblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        state.AddLandblock(new LoadedLandblock(
            targetLandblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var entities = new WorldEntity[count];
        using (state.BeginMutationBatch())
        {
            for (int i = 0; i < count; i++)
            {
                entities[i] = Entity((uint)(i + 1), 0x72000000u + (uint)i);
                state.PlaceLiveEntityProjection(sourceLandblock, entities[i]);
            }
        }

        using (state.BeginMutationBatch())
        {
            for (int i = 0; i < count; i++)
                state.RebucketLiveEntity(entities[i], targetLandblock);
        }

        Assert.True(state.TryGetLandblock(sourceLandblock, out LoadedLandblock? source));
        Assert.True(state.TryGetLandblock(targetLandblock, out LoadedLandblock? target));
        Assert.Empty(source!.Entities);
        Assert.Equal(count, target!.Entities.Count);
        Assert.Equal(count, state.Entities.Count);

        using (state.BeginMutationBatch())
        {
            for (int i = 0; i < count; i++)
                state.RemoveLiveEntityProjection(entities[i]);
        }

        Assert.Empty(target.Entities);
        Assert.Empty(state.Entities);
    }

    [Fact]
    public void DenseDemotion_CompactsLiveEntriesAndKeepsTheirIndexesValid()
    {
        const uint sourceLandblock = 0x0101FFFFu;
        const uint targetLandblock = 0x0202FFFFu;
        const int liveCount = 2_000;
        var state = new GpuWorldState();
        var mixed = new List<WorldEntity>(liveCount * 2);
        var live = new List<WorldEntity>(liveCount);
        for (int i = 0; i < liveCount; i++)
        {
            mixed.Add(Entity(0xC1000000u + (uint)i, 0u));
            WorldEntity projection = Entity((uint)(i + 1), 0x73000000u + (uint)i);
            mixed.Add(projection);
            live.Add(projection);
        }
        state.AddLandblock(new LoadedLandblock(
            sourceLandblock,
            new LandBlock(),
            mixed));
        state.AddLandblock(new LoadedLandblock(
            targetLandblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));

        state.RemoveEntitiesFromLandblock(sourceLandblock);

        Assert.True(state.TryGetLandblock(sourceLandblock, out LoadedLandblock? source));
        Assert.Equal(liveCount, source!.Entities.Count);
        Assert.All(source.Entities, entity => Assert.NotEqual(0u, entity.ServerGuid));

        using (state.BeginMutationBatch())
        {
            foreach (WorldEntity entity in live)
                state.RebucketLiveEntity(entity, targetLandblock);
        }

        Assert.Empty(source.Entities);
        Assert.True(state.TryGetLandblock(targetLandblock, out LoadedLandblock? target));
        Assert.Equal(liveCount, target!.Entities.Count);
    }

    [Fact]
    public void PendingDemotion_CompactsMixedStaticAndLiveEntriesAndKeepsLiveIndexesValid()
    {
        const uint pendingLandblock = 0x0101FFFFu;
        const uint targetLandblock = 0x0202FFFFu;
        var state = new GpuWorldState();
        WorldEntity first = Entity(1u, 0x73500001u);
        WorldEntity second = Entity(2u, 0x73500002u);
        WorldEntity[] mixed =
        [
            Entity(0xC1000001u, 0u),
            first,
            Entity(0xC1000002u, 0u),
            second,
        ];

        Assert.False(state.AddEntitiesToExistingLandblock(pendingLandblock, mixed));
        Assert.Equal(4, state.PendingLiveEntityCount);

        state.RemoveEntitiesFromLandblock(pendingLandblock);

        Assert.Equal(2, state.PendingLiveEntityCount);
        state.AddLandblock(new LoadedLandblock(
            pendingLandblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        state.AddLandblock(new LoadedLandblock(
            targetLandblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));

        Assert.True(state.TryGetLandblock(pendingLandblock, out LoadedLandblock? pending));
        Assert.Equal([first, second], pending!.Entities);

        state.RebucketLiveEntity(first, targetLandblock);
        state.RebucketLiveEntity(second, targetLandblock);

        Assert.Empty(pending.Entities);
        Assert.True(state.TryGetLandblock(targetLandblock, out LoadedLandblock? target));
        Assert.Equal(2, target!.Entities.Count);
        Assert.Contains(first, target.Entities);
        Assert.Contains(second, target.Entities);
    }

    [Fact]
    public void UnloadToPendingThenReload_PreservesProjectionIdentityAndVisibilityEdges()
    {
        const uint landblock = 0x0101FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            landblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        WorldEntity entity = Entity(1u, 0x73600001u);
        state.PlaceLiveEntityProjection(landblock, entity);
        RuntimeEntityKey key = Key(entity);
        var edges = new List<(RuntimeEntityKey Key, bool Visible)>();
        state.LiveProjectionVisibilityChanged += (observedKey, visible) =>
            edges.Add((observedKey, visible));

        state.RemoveLandblock(landblock);

        Assert.Empty(state.Entities);
        Assert.Equal(1, state.PendingLiveEntityCount);
        Assert.False(state.IsLiveEntityVisible(key));

        state.AddLandblock(new LoadedLandblock(
            landblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));

        Assert.Same(entity, Assert.Single(state.Entities));
        Assert.True(state.TryGetLandblock(landblock, out LoadedLandblock? reloaded));
        Assert.Same(entity, Assert.Single(reloaded!.Entities));
        Assert.True(state.IsLiveEntityVisible(key));
        Assert.Equal(
            [(key, false), (key, true)],
            edges);

        state.RemoveLiveEntityProjection(entity);
        Assert.Empty(state.Entities);
        Assert.Empty(reloaded.Entities);
    }

    [Fact]
    public void OriginRecenterSpatialSwap_RetainsLiveIdentityAndReturnsExactReceipts()
    {
        const uint firstLandblock = 0x1010FFFFu;
        const uint secondLandblock = 0x1011FFFFu;
        const uint pendingLandblock = 0x2020FFFFu;
        const uint playerGuid = 0x50000001u;
        var firstStatic = Entity(100u, 0u);
        var secondStatic = Entity(101u, 0u);
        var remote = Entity(1u, 0x70000001u);
        var pending = Entity(2u, 0x70000002u);
        var player = Entity(3u, playerGuid);
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            firstLandblock,
            new LandBlock(),
            [firstStatic]));
        state.AddLandblock(new LoadedLandblock(
            secondLandblock,
            new LandBlock(),
            [secondStatic]));
        state.PlaceLiveEntityProjection(firstLandblock, remote);
        state.MarkPersistent(playerGuid);
        state.PlaceLiveEntityProjection(secondLandblock, player);
        state.PlaceLiveEntityProjection(pendingLandblock, pending);
        var edges = new List<(RuntimeEntityKey Key, bool Visible)>();
        state.LiveProjectionVisibilityChanged += (key, visible) =>
            edges.Add((key, visible));

        GpuWorldRecenterRetirement result =
            state.DetachAllForOriginRecenter();

        Assert.Null(result.ObserverFailure);
        Assert.Equal(
            [firstLandblock, secondLandblock],
            result.Landblocks.Select(retirement => retirement.LandblockId));
        Assert.Contains(
            result.Landblocks.Single(
                retirement => retirement.LandblockId == firstLandblock).Entities,
            entity => ReferenceEquals(entity, firstStatic));
        Assert.Contains(
            result.Landblocks.Single(
                retirement => retirement.LandblockId == firstLandblock).Entities,
            entity => ReferenceEquals(entity, remote));
        Assert.Contains(
            result.Landblocks.Single(
                retirement => retirement.LandblockId == secondLandblock).Entities,
            entity => ReferenceEquals(entity, secondStatic));
        Assert.Contains(
            result.Landblocks.Single(
                retirement => retirement.LandblockId == secondLandblock).Entities,
            entity => ReferenceEquals(entity, player));
        Assert.DoesNotContain(
            result.Landblocks,
            retirement => retirement.LandblockId == pendingLandblock);

        Assert.Empty(state.LoadedLandblockIds);
        Assert.Empty(state.Entities);
        Assert.Equal(2, state.PendingLiveEntityCount);
        Assert.Same(player, Assert.Single(state.DrainRescued()));
        Assert.Equal(
            [(Key(remote), false), (Key(player), false)],
            edges);

        state.AddLandblock(new LoadedLandblock(
            firstLandblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        Assert.Same(remote, Assert.Single(state.Entities));
        state.AddLandblock(new LoadedLandblock(
            pendingLandblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        Assert.Contains(state.Entities, entity => ReferenceEquals(entity, pending));
    }

    [Fact]
    public void OriginRecenterSpatialSwap_PreservesCurrentLiveBucketOrder()
    {
        const uint landblock = 0x3030FFFFu;
        var staticEntity = Entity(100u, 0u);
        var removed = Entity(1u, 0x70000001u);
        var middle = Entity(2u, 0x70000002u);
        var movedTail = Entity(3u, 0x70000003u);
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            landblock,
            new LandBlock(),
            [staticEntity]));
        state.PlaceLiveEntityProjection(landblock, removed);
        state.PlaceLiveEntityProjection(landblock, middle);
        state.PlaceLiveEntityProjection(landblock, movedTail);

        state.RemoveLiveEntityProjection(removed);
        Assert.Equal(
            [staticEntity, movedTail, middle],
            state.Entities);

        state.DetachAllForOriginRecenter();
        state.AddLandblock(new LoadedLandblock(
            landblock,
            new LandBlock(),
            [staticEntity]));

        Assert.Equal(
            [staticEntity, movedTail, middle],
            state.Entities);
    }

    [Fact]
    public void BatchedVisibilityObserver_SeesCommittedFlatAndResidentViews()
    {
        const uint landblock = 0x0101FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            landblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        WorldEntity entity = Entity(1u, 0x73700001u);
        int callbacks = 0;
        state.LiveProjectionVisibilityChanged += (key, visible) =>
        {
            callbacks++;
            Assert.Equal(entity.Id, key.LocalEntityId);
            Assert.True(visible);
            Assert.True(state.IsLiveEntityVisible(key));
            Assert.Same(entity, Assert.Single(state.Entities));
            Assert.True(state.TryGetLandblock(landblock, out LoadedLandblock? loaded));
            Assert.Same(entity, Assert.Single(loaded!.Entities));
        };

        using (state.BeginMutationBatch())
        {
            state.PlaceLiveEntityProjection(landblock, entity);
            Assert.Equal(0, callbacks);
        }

        Assert.Equal(1, callbacks);
    }

    [Fact]
    public void LoadedToLoadedRebucket_EmitsNoVisibilityPulse()
    {
        const uint sourceLandblock = 0x0101FFFFu;
        const uint targetLandblock = 0x0202FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            sourceLandblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        state.AddLandblock(new LoadedLandblock(
            targetLandblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        WorldEntity entity = Entity(1u, 0x73800001u);
        state.PlaceLiveEntityProjection(sourceLandblock, entity);
        var edges = new List<(RuntimeEntityKey Key, bool Visible)>();
        state.LiveProjectionVisibilityChanged += (key, visible) =>
            edges.Add((key, visible));

        state.RebucketLiveEntity(entity, targetLandblock);

        Assert.Empty(edges);
        Assert.True(state.IsLiveEntityVisible(Key(entity)));
        Assert.True(state.TryGetLandblock(sourceLandblock, out LoadedLandblock? source));
        Assert.Empty(source!.Entities);
        Assert.True(state.TryGetLandblock(targetLandblock, out LoadedLandblock? target));
        Assert.Same(entity, Assert.Single(target!.Entities));
        Assert.Same(entity, Assert.Single(state.Entities));
    }

    [Fact]
    public void LoadedDetachAndReload_PreserveExactIncarnationKey()
    {
        const uint landblock = 0x0101FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            landblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        WorldEntity entity = Entity(1u, 0x73800011u);
        var exactKey = new RuntimeEntityKey(entity.Id, 37);
        state.PlaceLiveEntityProjection(exactKey, landblock, entity);
        var edges = new List<(RuntimeEntityKey Key, bool Visible)>();
        state.LiveProjectionVisibilityChanged += (key, visible) =>
            edges.Add((key, visible));

        state.DetachLandblock(landblock);

        Assert.Equal([(exactKey, false)], edges);
        Assert.False(state.IsLiveEntityVisible(exactKey));
        Assert.Empty(state.Entities);

        state.AddLandblock(new LoadedLandblock(
            landblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));

        Assert.Equal([(exactKey, false), (exactKey, true)], edges);
        Assert.True(state.IsLiveEntityVisible(exactKey));
        Assert.Same(entity, Assert.Single(state.Entities));
    }

    [Fact]
    public void PendingRebucket_WithWrongIncarnation_IsRejectedWithoutMutation()
    {
        const uint sourceLandblock = 0x0101FFFFu;
        const uint targetLandblock = 0x0202FFFFu;
        var state = new GpuWorldState();
        WorldEntity entity = Entity(1u, 0x73800012u);
        var exactKey = new RuntimeEntityKey(entity.Id, 37);
        state.PlaceLiveEntityProjection(exactKey, sourceLandblock, entity);

        Assert.Throws<InvalidOperationException>(() =>
            state.RebucketLiveEntity(
                new RuntimeEntityKey(entity.Id, 38),
                entity,
                targetLandblock));

        Assert.Equal(1, state.PendingLiveEntityCount);
        Assert.False(state.IsLiveEntityProjectionResident(exactKey));
        state.AddLandblock(new LoadedLandblock(
            sourceLandblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        Assert.True(state.IsLiveEntityVisible(exactKey));
        Assert.Same(entity, Assert.Single(state.Entities));
    }

    [Fact]
    public void SameGuidOverlap_TracksExactProjectionVisibilityIndependently()
    {
        const uint landblock = 0x0101FFFFu;
        const uint guid = 0x73900001u;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            landblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        WorldEntity first = Entity(1u, guid);
        WorldEntity second = Entity(2u, guid);
        state.PlaceLiveEntityProjection(landblock, first);
        state.PlaceLiveEntityProjection(landblock, second);
        var edges = new List<(RuntimeEntityKey Key, bool Visible)>();
        state.LiveProjectionVisibilityChanged += (key, visible) =>
            edges.Add((key, visible));

        state.RemoveLiveEntityProjection(first);

        Assert.Equal([(Key(first), false)], edges);
        Assert.False(state.IsLiveEntityVisible(Key(first)));
        Assert.True(state.IsLiveEntityVisible(Key(second)));
        Assert.Same(second, Assert.Single(state.Entities));

        state.RemoveLiveEntityProjection(guid);

        Assert.Equal([(Key(first), false), (Key(second), false)], edges);
        Assert.False(state.IsLiveEntityVisible(Key(second)));
        Assert.Empty(state.Entities);
    }

    [Fact]
    public void DuplicateDirectPlacement_IsRejectedBeforeAnyBucketMutation()
    {
        const uint landblock = 0x0101FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            landblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        WorldEntity entity = Entity(1u, 0x74000001u);
        state.PlaceLiveEntityProjection(landblock, entity);

        Assert.Throws<InvalidOperationException>(() =>
            state.PlaceLiveEntityProjection(landblock, entity));

        Assert.Same(entity, Assert.Single(state.Entities));
        Assert.True(state.TryGetLandblock(landblock, out LoadedLandblock? loaded));
        Assert.Same(entity, Assert.Single(loaded!.Entities));
    }

    [Fact]
    public void ThrowingObserver_DoesNotStrandLaterSubscribersOrQueuedOwnerEdges()
    {
        const uint landblock = 0x0101FFFFu;
        WorldEntity first = Entity(1u, 0x70000001u);
        WorldEntity second = Entity(2u, 0x70000002u);
        var state = new GpuWorldState();
        state.PlaceLiveEntityProjection(landblock, first);
        state.PlaceLiveEntityProjection(landblock, second);
        var observed = new List<(RuntimeEntityKey Key, bool Visible)>();
        state.LiveProjectionVisibilityChanged += (_, _) =>
            throw new InvalidOperationException("fixture observer failure");
        state.LiveProjectionVisibilityChanged += (key, visible) =>
            observed.Add((key, visible));

        AggregateException error = Assert.Throws<AggregateException>(() =>
            state.AddLandblock(new LoadedLandblock(
                landblock,
                new LandBlock(),
                Array.Empty<WorldEntity>())));

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal(
            [(Key(first), true), (Key(second), true)],
            observed.OrderBy(edge => edge.Key.LocalEntityId).ToArray());
        Assert.True(state.IsLiveEntityVisible(Key(first)));
        Assert.True(state.IsLiveEntityVisible(Key(second)));
        Assert.Equal(0, state.PendingVisibilityTransitionCount);
        Assert.Equal(2, state.Entities.Count);
    }

    [Fact]
    public void ThrowingRuntimeObserver_DoesNotSkipOtherOwnersOrPresentationSubscribers()
    {
        const uint landblock = 0x0101FFFFu;
        const uint cell = 0x01010001u;
        var state = new GpuWorldState();
        var runtime = LiveEntityRuntimeFixture.Create(
            state,
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));
        WorldSession.EntitySpawn first = Spawn(0x70000011u, cell);
        WorldSession.EntitySpawn second = Spawn(0x70000012u, cell);
        runtime.RegisterLiveEntity(first);
        runtime.RegisterLiveEntity(second);
        runtime.MaterializeLiveEntity(first.Guid, cell, id => Entity(id, first.Guid));
        runtime.MaterializeLiveEntity(second.Guid, cell, id => Entity(id, second.Guid));
        var observed = new List<(uint Guid, bool Visible)>();
        runtime.ProjectionVisibilityChanged += (_, _) =>
            throw new InvalidOperationException("fixture presentation failure");
        runtime.ProjectionVisibilityChanged += (record, visible) =>
            observed.Add((record.ServerGuid, visible));

        AggregateException error = Assert.Throws<AggregateException>(() =>
            state.AddLandblock(new LoadedLandblock(
                landblock,
                new LandBlock(),
                Array.Empty<WorldEntity>())));

        Assert.Equal(2, error.Flatten().InnerExceptions.Count);
        Assert.Equal(
            [(first.Guid, true), (second.Guid, true)],
            observed.OrderBy(edge => edge.Guid).ToArray());
        Assert.True(runtime.TryGetRecord(first.Guid, out LiveEntityRecord firstRecord));
        Assert.True(runtime.TryGetRecord(second.Guid, out LiveEntityRecord secondRecord));
        Assert.True(firstRecord.IsSpatiallyVisible);
        Assert.True(secondRecord.IsSpatiallyVisible);
        Assert.Equal(0, state.PendingVisibilityTransitionCount);
    }

    [Fact]
    public void QuiescedDestinationProjection_AcquiresSpatialPresentationBeforeWorldReveal()
    {
        const uint landblock = 0x0101FFFFu;
        const uint cell = 0x01010001u;
        const uint guid = 0x70000021u;
        var transit = new RuntimeWorldTransitState();
        var availability = new WorldGenerationAvailabilityState(transit);
        long generation =
            RuntimeWorldTransitTestDriver.BeginPortal(transit, cell);
        var state = new GpuWorldState(availability: availability);
        state.AddLandblock(new LoadedLandblock(
            landblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var runtime = LiveEntityRuntimeFixture.Create(
            state,
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));
        WorldSession.EntitySpawn spawn = Spawn(guid, cell);
        runtime.RegisterLiveEntity(spawn);
        var edges = new List<bool>();
        runtime.ProjectionVisibilityChanged += (_, visible) => edges.Add(visible);

        runtime.MaterializeLiveEntity(
            guid,
            cell,
            id => Entity(id, guid));

        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord record));
        RuntimeEntityKey projectionKey = record.ProjectionKey!.Value;
        Assert.False(state.IsLiveEntityVisible(projectionKey));
        Assert.True(state.IsLiveEntityProjectionResident(projectionKey));
        Assert.True(record.IsSpatiallyVisible);
        Assert.Equal([true], edges);

        Assert.True(transit.AcknowledgeDestinationReadiness(
            new RuntimeDestinationReadiness(
                generation,
                cell,
                IsIndoor: false,
                IsUnhydratable: false,
                RequiredRenderRadius: 1,
                IsRenderNeighborhoodReady: true,
                AreCompositeTexturesReady: true,
                IsCollisionReady: true)));
        Assert.True(RuntimeWorldTransitTestDriver.MaterializePortal(
            transit,
            generation,
            cell));
        Assert.True(transit.Complete(generation));
        Assert.True(state.IsLiveEntityVisible(projectionKey));
        Assert.True(record.IsSpatiallyVisible);
    }

    [Fact]
    public void CopyLiveEntitiesNearLandblock_UsesBoundedNeighborhoodAndSkipsStatics()
    {
        var centerLive = Entity(1, 0x70000001u);
        var neighborLive = Entity(2, 0x70000002u);
        var farLive = Entity(3, 0x70000003u);
        WorldEntity[] staticEntities = Enumerable.Range(0, 20_000)
            .Select(index => Entity((uint)(index + 10), 0u))
            .ToArray();
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            0x1010FFFFu,
            new LandBlock(),
            staticEntities.Append(centerLive).ToArray()));
        state.AddLandblock(new LoadedLandblock(
            0x1110FFFFu,
            new LandBlock(),
            new[] { neighborLive }));
        state.AddLandblock(new LoadedLandblock(
            0x1310FFFFu,
            new LandBlock(),
            new[] { farLive }));
        var destination = new List<KeyValuePair<uint, WorldEntity>>
        {
            new(0xDEADBEEFu, farLive),
        };

        state.CopyLiveEntitiesNearLandblock(0x10100001u, 1, destination);

        Assert.Equal(2, destination.Count);
        Assert.Contains(destination, pair => pair.Key == centerLive.ServerGuid);
        Assert.Contains(destination, pair => pair.Key == neighborLive.ServerGuid);
        Assert.DoesNotContain(destination, pair => pair.Key == farLive.ServerGuid);
        Assert.DoesNotContain(destination, pair => pair.Key == 0u);
    }

    [Fact]
    public void ReadinessNeighborhood_CopiesStaticAndLiveWhileWorldIsQuiesced()
    {
        var transit = new RuntimeWorldTransitState();
        var availability = new WorldGenerationAvailabilityState(transit);
        RuntimeWorldTransitTestDriver.BeginPortal(
            transit,
            0x10100001u);
        var centerStatic = Entity(1, 0u);
        var centerLive = Entity(2, 0x70000001u);
        var neighborStatic = Entity(3, 0u);
        var farStatic = Entity(4, 0u);
        var state = new GpuWorldState(availability: availability);
        state.AddLandblock(new LoadedLandblock(
            0x1010FFFFu,
            new LandBlock(),
            new[] { centerStatic, centerLive }));
        state.AddLandblock(new LoadedLandblock(
            0x1110FFFFu,
            new LandBlock(),
            new[] { neighborStatic }));
        state.AddLandblock(new LoadedLandblock(
            0x1310FFFFu,
            new LandBlock(),
            new[] { farStatic }));
        var destination = new List<WorldEntity> { farStatic };

        state.CopyPublishedEntitiesNearLandblockForReadiness(
            0x10100001u,
            1,
            destination);

        Assert.Equal(3, destination.Count);
        Assert.Contains(centerStatic, destination);
        Assert.Contains(centerLive, destination);
        Assert.Contains(neighborStatic, destination);
        Assert.DoesNotContain(farStatic, destination);
    }

    private static WorldEntity Entity(uint id, uint guid) => new()
    {
        Id = id,
        ServerGuid = guid,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };

    private static RuntimeEntityKey Key(WorldEntity entity) =>
        new(entity.Id, 0);

    private static WorldSession.EntitySpawn Spawn(uint guid, uint cell)
    {
        var position = new CreateObject.ServerPosition(
            cell, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(1, 1, 1, 1, 0, 1, 0, 1, 1);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: null,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: null,
            Name: "visibility fixture",
            ItemType: null,
            MotionState: null,
            MotionTableId: null,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
