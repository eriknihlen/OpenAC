using System.Numerics;
using AcDream.App.Audio;
using AcDream.App.Streaming;
using AcDream.Core.Selection;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Streaming;

public sealed class WorldGenerationQuiescenceTests
{
    private const uint LandblockId = 0x1010FFFFu;
    private const uint DestinationCell = 0x10100001u;
    private const uint ServerGuid = 0x70000001u;

    [Fact]
    public void RuntimeGeneration_HidesWorldQueriesButRetainsCanonicalOwners()
    {
        var transit = new RuntimeWorldTransitState();
        var availability = new WorldGenerationAvailabilityState(transit);
        var world = new GpuWorldState(availability: availability);
        WorldEntity entity = Entity();
        world.AddLandblock(new LoadedLandblock(
            LandblockId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        world.PlaceLiveEntityProjection(LandblockId, entity);
        RuntimeEntityKey key = Key(entity);
        world.SetLandblockAabb(LandblockId, Vector3.Zero, Vector3.One);
        var nearby = new List<KeyValuePair<uint, WorldEntity>>();

        Assert.True(world.IsLiveEntityVisible(key));
        Assert.Single(world.LandblockEntries);
        Assert.Single(world.LandblockBounds);

        long generation =
            RuntimeWorldTransitTestDriver.BeginPortal(
                transit,
                DestinationCell);
        world.CopyLiveEntitiesNearLandblock(LandblockId, 0, nearby);

        Assert.False(availability.IsWorldAvailable);
        Assert.Equal(generation, availability.QuiescedGeneration);
        Assert.False(world.IsLiveEntityVisible(key));
        Assert.Empty(world.LandblockEntries);
        Assert.Empty(world.LandblockBounds);
        Assert.Empty(nearby);
        Assert.Same(entity, Assert.Single(world.Entities));
        Assert.True(world.IsLoaded(LandblockId));

        Assert.False(transit.Complete(generation + 1));
        Assert.False(availability.IsWorldAvailable);
        Assert.True(transit.AcknowledgeDestinationReadiness(
            Ready(generation, DestinationCell)));
        Assert.True(RuntimeWorldTransitTestDriver.MaterializePortal(
            transit,
            generation,
            DestinationCell));
        Assert.True(transit.Complete(generation));
        Assert.True(world.IsLiveEntityVisible(key));
        Assert.Same(
            entity,
            Assert.Single(world.LandblockEntries).Entities.Single());
    }

    [Fact]
    public void SupersededReveal_StaysSilentAndOnlyLatestRuntimeEdgeResumesAudio()
    {
        var transit = new RuntimeWorldTransitState();
        var availability = new WorldGenerationAvailabilityState(transit);
        var world = new GpuWorldState(availability: availability);
        world.AddLandblock(new LoadedLandblock(
            LandblockId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        WorldEntity entity = Entity();
        world.PlaceLiveEntityProjection(LandblockId, entity);
        var selection = new SelectionState();
        selection.Select(ServerGuid, SelectionChangeSource.World);
        var audio = new RecordingAudioQuiescence();
        var quiescence = new WorldGenerationQuiescence(
            selection,
            world,
            guid => guid == ServerGuid ? Key(entity) : null,
            audio);

        WorldGenerationQuiescenceEdge firstEdge =
            quiescence.CaptureBegin();
        long first =
            transit.BeginLoginReveal(DestinationCell);
        quiescence.CommitBegin(firstEdge);
        WorldGenerationQuiescenceEdge secondEdge =
            quiescence.CaptureBegin();
        long second =
            RuntimeWorldTransitTestDriver.BeginPortal(
                transit,
                DestinationCell);
        quiescence.CommitBegin(secondEdge);

        Assert.False(transit.Complete(first));
        Assert.False(availability.IsWorldAvailable);
        Assert.Equal(second, availability.QuiescedGeneration);
        Assert.Null(selection.SelectedObjectId);
        Assert.Equal(1, audio.SuspendCalls);
        Assert.Equal(0, audio.ResumeCalls);

        Assert.True(transit.AcknowledgeDestinationReadiness(
            Ready(second, DestinationCell)));
        Assert.True(RuntimeWorldTransitTestDriver.MaterializePortal(
            transit,
            second,
            DestinationCell));
        Assert.True(transit.Complete(second));
        quiescence.ObserveReleased();

        Assert.True(availability.IsWorldAvailable);
        Assert.Equal(1, audio.ResumeCalls);
    }

    [Fact]
    public void Quiesce_PreservesNonWorldInventorySelection()
    {
        const uint inventoryGuid = 0x80000001u;
        var transit = new RuntimeWorldTransitState();
        var availability = new WorldGenerationAvailabilityState(transit);
        var world = new GpuWorldState(availability: availability);
        var selection = new SelectionState();
        selection.Select(inventoryGuid, SelectionChangeSource.Inventory);
        var quiescence = new WorldGenerationQuiescence(
            selection,
            world,
            _ => null,
            audio: null);

        WorldGenerationQuiescenceEdge edge = quiescence.CaptureBegin();
        RuntimeWorldTransitTestDriver.BeginPortal(
            transit,
            DestinationCell);
        quiescence.CommitBegin(edge);

        Assert.Equal(inventoryGuid, selection.SelectedObjectId);
    }

    [Fact]
    public void ReentrantBeginDuringAudioResume_ReconcilesToNewestEdge()
    {
        var transit = new RuntimeWorldTransitState();
        var world = new GpuWorldState(
            availability: new WorldGenerationAvailabilityState(transit));
        var audio = new ReentrantAudioQuiescence();
        var quiescence = new WorldGenerationQuiescence(
            new SelectionState(),
            world,
            _ => null,
            audio);
        var edge = new WorldGenerationQuiescenceEdge(
            ShouldApply: true,
            ClearWorldSelection: false);
        quiescence.CommitBegin(edge);
        audio.OnResume = () =>
        {
            audio.OnResume = null;
            quiescence.CommitBegin(edge);
        };

        quiescence.ObserveReleased();

        Assert.Equal(2, audio.SuspendCalls);
        Assert.Equal(1, audio.ResumeCalls);

        quiescence.ObserveReleased();

        Assert.Equal(2, audio.SuspendCalls);
        Assert.Equal(2, audio.ResumeCalls);
    }

    private static RuntimeDestinationReadiness Ready(
        long generation,
        uint destinationCell) =>
        new(
            generation,
            destinationCell,
            IsIndoor: false,
            IsUnhydratable: false,
            RequiredRenderRadius: 1,
            IsRenderNeighborhoodReady: true,
            AreCompositeTexturesReady: true,
            IsCollisionReady: true);

    private static WorldEntity Entity() => new()
    {
        Id = 1,
        ServerGuid = ServerGuid,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };

    private static RuntimeEntityKey Key(WorldEntity entity) =>
        new(entity.Id, 0);

    private sealed class RecordingAudioQuiescence : IWorldAudioQuiescence
    {
        public int SuspendCalls { get; private set; }
        public int ResumeCalls { get; private set; }

        public void SuspendWorldAudio() => SuspendCalls++;
        public void ResumeWorldAudio() => ResumeCalls++;
    }

    private sealed class ReentrantAudioQuiescence : IWorldAudioQuiescence
    {
        public int SuspendCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public Action? OnResume { get; set; }

        public void SuspendWorldAudio() => SuspendCalls++;

        public void ResumeWorldAudio()
        {
            ResumeCalls++;
            OnResume?.Invoke();
        }
    }
}
