using System.Numerics;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Runtime;
using AcDream.Runtime.World;

namespace AcDream.App.Tests.Streaming;

public sealed class WorldRevealCoordinatorTests
{
    private sealed class State
    {
        public RuntimeWorldTransitState Transit { get; private set; } = null!;
        public bool RenderReady { get; set; }
        public bool CollisionReady { get; set; }
        public bool CompositesReady { get; set; }
        public int InvalidateCount { get; private set; }
        public int PrepareCount { get; private set; }

        public StreamingRevealWindow Window { get; set; } =
            new(NearRadius: 1, FarRadius: 1);

        public WorldRevealCoordinator Build(
            List<string>? logs = null,
            IWorldRevealStreamingScheduler? streaming = null,
            IWorldRevealRenderResourceScheduler? renderResources = null,
            RuntimeWorldTransitState? transit = null)
        {
            transit ??= new RuntimeWorldTransitState(
                logs is null ? null : new Action<string>(logs.Add));
            Transit = transit;
            return new WorldRevealCoordinator(
                transit,
                revealWindow: () => Window,
                isRenderNeighborhoodReady: (_, _, _) => RenderReady,
                isSpawnCellReady: _ => CollisionReady,
                isTerrainNeighborhoodReady: (_, _) => CollisionReady,
                areCompositeTexturesReady: () => CompositesReady,
                prepareCompositeTextures: (_, _) => PrepareCount++,
                invalidateCompositeTextures: () =>
                {
                    InvalidateCount++;
                    CompositesReady = false;
                },
                isSpawnClaimUnhydratable: _ => false,
                streaming: streaming,
                renderResources: renderResources);
        }
    }

    [Fact]
    public void Begin_InvalidatesPriorDestinationAndStartsOneGeneration()
    {
        var state = new State { CompositesReady = true };
        WorldRevealCoordinator coordinator = state.Build();

        long generation = coordinator.BeginLogin(0x11340021u);

        Assert.Equal(1, generation);
        Assert.Equal(1, state.InvalidateCount);
        Assert.False(state.CompositesReady);
        Assert.Equal(RuntimePortalKind.Login, coordinator.Snapshot.Kind);
    }

    [Fact]
    public void PrepareAndEvaluate_UsesOneCanonicalSnapshotForLifecycle()
    {
        const uint outdoorCell = 0x11340021u;
        var state = new State
        {
            RenderReady = true,
            CollisionReady = true,
        };
        WorldRevealCoordinator coordinator = state.Build();
        coordinator.BeginLogin(outdoorCell);

        WorldRevealReadinessSnapshot first = coordinator.PrepareAndEvaluate(outdoorCell);
        state.CompositesReady = true;
        WorldRevealReadinessSnapshot ready = coordinator.PrepareAndEvaluate(outdoorCell);

        Assert.False(first.IsReady);
        Assert.True(ready.IsReady);
        Assert.Equal(2, state.PrepareCount);
        Assert.Equal(ready.DestinationCell, coordinator.Snapshot.DestinationCell);
        Assert.Equal(ready.IsReady, coordinator.Snapshot.IsReady);
    }

    [Fact]
    public void PortalLifecycle_CountsMaterializationAndCompletesVisibleGeneration()
    {
        const uint outdoorCell = 0x3032001Cu;
        var logs = new List<string>();
        var state = new State
        {
            RenderReady = true,
            CollisionReady = true,
            CompositesReady = true,
        };
        WorldRevealCoordinator coordinator = state.Build(logs);
        long generation = BeginPortal(
            coordinator,
            state.Transit,
            outdoorCell,
            sequence: 1);
        state.CompositesReady = true;

        Assert.True(coordinator.Evaluate(outdoorCell).IsReady);
        coordinator.ObserveMaterialized(generation, 1, outdoorCell);
        coordinator.ObserveMaterialized(generation, 1, outdoorCell);
        coordinator.ObserveWorldViewportVisible();
        coordinator.Complete();

        Assert.Equal(1, coordinator.PortalMaterializationCount);
        Assert.True(coordinator.Snapshot.Materialized);
        Assert.True(coordinator.Snapshot.WorldViewportObserved);
        Assert.True(coordinator.Snapshot.Completed);
        Assert.Equal(0, coordinator.Snapshot.InvariantFailureCount);
        Assert.Single(logs, line => line.Contains("event=materialized", StringComparison.Ordinal));
    }

    [Fact]
    public void PortalBegin_WrongSequenceOrCellCannotAcquireHostResources()
    {
        const uint destinationCell = 0x3032001Cu;
        var state = new State { CompositesReady = true };
        var scheduler = new RecordingDestinationScheduler();
        WorldRevealCoordinator coordinator = state.Build(streaming: scheduler);
        RuntimeWorldTransitTestDriver.AcceptPortalDestination(
            state.Transit,
            destinationCell,
            sequence: 7);

        Assert.False(coordinator.TryBeginPortal(
            8,
            destinationCell,
            out _));
        Assert.False(coordinator.TryBeginPortal(
            7,
            0x11340021u,
            out _));
        Assert.Empty(scheduler.Begins);
        Assert.Equal(0, state.InvalidateCount);
        Assert.Equal(0, coordinator.Snapshot.Generation);

        Assert.True(coordinator.TryBeginPortal(
            7,
            destinationCell,
            out long generation));
        Assert.Equal(
            (generation, destinationCell, 1),
            Assert.Single(scheduler.Begins));
        Assert.Equal(1, state.InvalidateCount);
    }

    [Fact]
    public void Cancel_PreventsLateDestinationWorkFromMutatingLifecycle()
    {
        var state = new State
        {
            RenderReady = true,
            CollisionReady = true,
            CompositesReady = true,
        };
        WorldRevealCoordinator coordinator = state.Build();
        long generation = BeginPortal(
            coordinator,
            state.Transit,
            0x11340021u,
            sequence: 1);
        coordinator.Cancel();

        coordinator.PrepareAndEvaluate(0x11340021u);
        coordinator.ObserveMaterialized(
            generation,
            1,
            0x11340021u);
        coordinator.ObserveWorldViewportVisible();
        coordinator.Complete();

        Assert.True(coordinator.Snapshot.Cancelled);
        Assert.Equal(0x11340021u, coordinator.Snapshot.DestinationCell);
        Assert.False(coordinator.Snapshot.IsReady);
        Assert.Equal(0, coordinator.PortalMaterializationCount);
    }

    [Fact]
    public void RevealLifetime_QuiescesContinuouslyAndOnlyActiveGenerationReopens()
    {
        var transit = new RuntimeWorldTransitState();
        var availability = new WorldGenerationAvailabilityState(transit);
        var world = new GpuWorldState(availability: availability);
        var audio = new RecordingAudioQuiescence();
        var quiescence = new WorldGenerationQuiescence(
            new SelectionState(),
            world,
            _ => null,
            audio);
        var coordinator = new WorldRevealCoordinator(
            transit,
            revealWindow: () => new StreamingRevealWindow(1, 1),
            isRenderNeighborhoodReady: (_, _, _) => true,
            isSpawnCellReady: _ => true,
            isTerrainNeighborhoodReady: (_, _) => true,
            areCompositeTexturesReady: () => true,
            prepareCompositeTextures: (_, _) => { },
            invalidateCompositeTextures: () => { },
            isSpawnClaimUnhydratable: _ => false,
            quiescence: quiescence);

        Assert.Equal(1, coordinator.BeginLogin(0x11340021u));
        long generation = BeginPortal(
            coordinator,
            transit,
            0x11340021u,
            sequence: 1);
        Assert.Equal(2, generation);
        Assert.False(availability.IsWorldAvailable);
        Assert.Equal(2, availability.QuiescedGeneration);
        Assert.Equal(1, audio.SuspendCalls);

        Assert.True(coordinator.Evaluate(0x11340021u).IsReady);
        coordinator.ObserveMaterialized(
            generation,
            1,
            0x11340021u);
        coordinator.Complete();

        Assert.True(availability.IsWorldAvailable);
        Assert.Equal(1, audio.ResumeCalls);
    }

    [Fact]
    public void Materialization_ReopensWorldBeforePortalViewportRelease()
    {
        var transit = new RuntimeWorldTransitState();
        var availability = new WorldGenerationAvailabilityState(transit);
        var world = new GpuWorldState(availability: availability);
        var audio = new RecordingAudioQuiescence();
        var quiescence = new WorldGenerationQuiescence(
            new SelectionState(),
            world,
            _ => null,
            audio);
        var scheduler = new RecordingDestinationScheduler();
        var coordinator = new WorldRevealCoordinator(
            transit,
            revealWindow: () => new StreamingRevealWindow(1, 1),
            isRenderNeighborhoodReady: (_, _, _) => true,
            isSpawnCellReady: _ => true,
            isTerrainNeighborhoodReady: (_, _) => true,
            areCompositeTexturesReady: () => true,
            prepareCompositeTextures: (_, _) => { },
            invalidateCompositeTextures: () => { },
            isSpawnClaimUnhydratable: _ => false,
            quiescence: quiescence,
            streaming: scheduler);
        const uint destinationCell = 0x3032001Cu;
        const ushort teleportSequence = 1;
        long generation = BeginPortal(
            coordinator,
            transit,
            destinationCell,
            teleportSequence);

        Assert.False(availability.IsWorldAvailable);
        Assert.True(coordinator.Evaluate(destinationCell).IsReady);

        coordinator.ObserveMaterialized(
            generation,
            teleportSequence,
            destinationCell);

        Assert.True(availability.IsWorldAvailable);
        Assert.False(coordinator.Snapshot.WorldViewportObserved);
        Assert.False(coordinator.Snapshot.Completed);
        Assert.Equal(1, audio.ResumeCalls);
        Assert.Empty(scheduler.Ends);

        coordinator.RevealWorldViewport();
        coordinator.RevealWorldViewport();

        Assert.True(availability.IsWorldAvailable);
        Assert.False(coordinator.Snapshot.Completed);
        Assert.Equal(1, audio.ResumeCalls);
        Assert.Equal([generation], scheduler.Ends);

        coordinator.Complete();

        Assert.True(coordinator.Snapshot.Completed);
        Assert.Equal(1, audio.ResumeCalls);
        Assert.Equal([generation], scheduler.Ends);
    }

    [Fact]
    public void TunnelContinue_TicksDestinationObjectsBehindPortalViewport()
    {
        var transit = new RuntimeWorldTransitState();
        var availability = new WorldGenerationAvailabilityState(transit);
        var world = new GpuWorldState(availability: availability);
        var quiescence = new WorldGenerationQuiescence(
            new SelectionState(),
            world,
            _ => null,
            audio: null);
        var scheduler = new RecordingDestinationScheduler();
        var coordinator = new WorldRevealCoordinator(
            transit,
            revealWindow: () => new StreamingRevealWindow(1, 1),
            isRenderNeighborhoodReady: (_, _, _) => true,
            isSpawnCellReady: _ => true,
            isTerrainNeighborhoodReady: (_, _) => true,
            areCompositeTexturesReady: () => true,
            prepareCompositeTextures: (_, _) => { },
            invalidateCompositeTextures: () => { },
            isSpawnClaimUnhydratable: _ => false,
            quiescence: quiescence,
            streaming: scheduler);
        var calls = new List<string>();
        var frame = new RetailLiveFrameCoordinator(
            new global::AcDream.App.Tests.TestLiveObjectFramePhase(
                _ => calls.Add("objects")),
            world,
            new global::AcDream.App.Tests.TestLiveSessionFramePhase(
                () => calls.Add("network")),
            new global::AcDream.App.Tests.TestPostNetworkCommandFramePhase(
                () => calls.Add("commands")),
            new global::AcDream.App.Tests.TestLiveSpatialReconcilePhase(
                () => calls.Add("spatial")),
            availability);

        const uint destinationCell = 0x3032001Cu;
        long generation = BeginPortal(
            coordinator,
            transit,
            destinationCell,
            sequence: 1);
        frame.Tick(1f / 60f);
        Assert.Equal(["network", "commands"], calls);

        calls.Clear();
        Assert.True(coordinator.Evaluate(destinationCell).IsReady);
        coordinator.ObserveMaterialized(
            generation,
            1,
            destinationCell);
        frame.Tick(1f / 60f);

        Assert.Equal(["objects", "network", "commands", "spatial"], calls);
        Assert.Empty(scheduler.Ends);
        Assert.False(coordinator.Snapshot.WorldViewportObserved);
    }

    [Fact]
    public void SessionReset_ReleasesHostEdgesAndClearsRuntimeSnapshot()
    {
        var transit = new RuntimeWorldTransitState();
        var availability = new WorldGenerationAvailabilityState(transit);
        var world = new GpuWorldState(availability: availability);
        var audio = new RecordingAudioQuiescence();
        var quiescence = new WorldGenerationQuiescence(
            new SelectionState(),
            world,
            _ => null,
            audio);
        var scheduler = new RecordingDestinationScheduler();
        var coordinator = new WorldRevealCoordinator(
            transit,
            revealWindow: () => new StreamingRevealWindow(1, 1),
            isRenderNeighborhoodReady: (_, _, _) => false,
            isSpawnCellReady: _ => false,
            isTerrainNeighborhoodReady: (_, _) => false,
            areCompositeTexturesReady: () => false,
            prepareCompositeTextures: (_, _) => { },
            invalidateCompositeTextures: () => { },
            isSpawnClaimUnhydratable: _ => false,
            quiescence: quiescence,
            streaming: scheduler);
        long generation = BeginPortal(
            coordinator,
            transit,
            0x3032001Cu,
            sequence: 1);

        coordinator.ResetSession();

        Assert.Equal(RuntimePortalSnapshot.Idle, coordinator.Snapshot);
        Assert.True(availability.IsWorldAvailable);
        Assert.Equal(1, audio.SuspendCalls);
        Assert.Equal(1, audio.ResumeCalls);
        Assert.Equal([generation], scheduler.Ends);
    }

    [Fact]
    public void RevealGeneration_OwnsExactDestinationReservationUntilCompletion()
    {
        var state = new State();
        var scheduler = new RecordingDestinationScheduler();
        WorldRevealCoordinator coordinator = state.Build(streaming: scheduler);

        long first = BeginPortal(
            coordinator,
            state.Transit,
            0x11340021u,
            sequence: 1);
        state.Transit.EndTeleport();
        long second = BeginPortal(
            coordinator,
            state.Transit,
            0x20210123u,
            sequence: 2);

        Assert.Equal(
            [
                (first, 0x11340021u, 1),
                (second, 0x20210123u, 0),
            ],
            scheduler.Begins);
        Assert.Equal([first], scheduler.Ends);
        coordinator.Cancel();
        Assert.Equal([first, second], scheduler.Ends);
    }

    [Fact]
    public void RevealGeneration_OwnsRenderUploadProfileUntilViewportRelease()
    {
        var state = new State();
        var resources = new RecordingRenderResourceScheduler();
        WorldRevealCoordinator coordinator =
            state.Build(renderResources: resources);

        long first = BeginPortal(
            coordinator,
            state.Transit,
            0x11340021u,
            sequence: 1);
        state.Transit.EndTeleport();
        long second = BeginPortal(
            coordinator,
            state.Transit,
            0x20210123u,
            sequence: 2);

        Assert.Equal([first, second], resources.Begins);
        Assert.Equal([first], resources.Ends);
        coordinator.RevealWorldViewport();
        coordinator.Complete();

        Assert.Equal([first, second], resources.Ends);
    }

    [Fact]
    public void RegistrationCallback_ReentrantReplacementRetiresExactOldSuffix()
    {
        var state = new State();
        var scheduler = new RecordingDestinationScheduler();
        WorldRevealCoordinator coordinator = null!;
        long replacement = 0;
        scheduler.OnBegin = () =>
        {
            scheduler.OnBegin = null;
            replacement = coordinator.BeginLogin(0x20210123u);
        };
        coordinator = state.Build(streaming: scheduler);

        long first = coordinator.BeginLogin(0x11340021u);

        Assert.NotEqual(first, replacement);
        Assert.Equal(replacement, coordinator.Snapshot.Generation);
        Assert.Equal(
            [
                (first, 0x11340021u, 1),
                (replacement, 0x20210123u, 0),
            ],
            scheduler.Begins);
        Assert.Equal([first], scheduler.Ends);
        Assert.Equal(1, coordinator.Ownership.HostProjectionCount);
        Assert.Equal(
            0,
            coordinator.Ownership.PendingHostAcknowledgementCount);

        coordinator.Cancel();

        Assert.Equal([first, replacement], scheduler.Ends);
        Assert.Equal(0, coordinator.Ownership.HostProjectionCount);
    }

    [Fact]
    public void RegistrationFailure_RetriesExactSuffixWithoutReplayingBegin()
    {
        var state = new State();
        var scheduler = new RecordingDestinationScheduler();
        var resources = new RecordingRenderResourceScheduler
        {
            ThrowBeginCount = 1,
        };
        WorldRevealCoordinator coordinator = state.Build(
            streaming: scheduler,
            renderResources: resources);

        Assert.Throws<InvalidOperationException>(
            () => coordinator.BeginLogin(0x11340021u));
        Assert.Single(scheduler.Begins);
        Assert.Equal(1, resources.BeginAttempts);
        Assert.Empty(resources.Begins);
        Assert.Equal(1, coordinator.Ownership.HostProjectionCount);
        Assert.Equal(
            1,
            coordinator.Ownership.PendingHostAcknowledgementCount);

        coordinator.RetryPendingHostWork();

        Assert.Single(scheduler.Begins);
        Assert.Equal(2, resources.BeginAttempts);
        Assert.Single(resources.Begins);
        Assert.Equal(
            0,
            coordinator.Ownership.PendingHostAcknowledgementCount);

        coordinator.Cancel();
        Assert.Equal(0, coordinator.Ownership.HostProjectionCount);
    }

    [Fact]
    public void ReservationReleaseFailure_RetriesOnlyUnfinishedCallback()
    {
        var state = new State();
        var scheduler = new RecordingDestinationScheduler();
        var resources = new RecordingRenderResourceScheduler
        {
            ThrowEndCount = 1,
        };
        WorldRevealCoordinator coordinator = state.Build(
            streaming: scheduler,
            renderResources: resources);
        long generation = coordinator.BeginLogin(0x11340021u);

        Assert.Throws<InvalidOperationException>(
            coordinator.RevealWorldViewport);
        Assert.Equal([generation], scheduler.Ends);
        Assert.Equal(1, resources.EndAttempts);
        Assert.Empty(resources.Ends);
        Assert.Equal(
            1,
            coordinator.Ownership.PendingHostAcknowledgementCount);

        coordinator.RetryPendingHostWork();

        Assert.Equal([generation], scheduler.Ends);
        Assert.Equal(2, resources.EndAttempts);
        Assert.Equal([generation], resources.Ends);
        Assert.Equal(
            0,
            coordinator.Ownership.PendingHostAcknowledgementCount);
        coordinator.Cancel();
    }

    [Fact]
    public void ResetFailure_RetainsWithdrawalAndConvergesOnRetry()
    {
        var state = new State();
        var scheduler = new RecordingDestinationScheduler
        {
            ThrowEndCount = 1,
        };
        WorldRevealCoordinator coordinator =
            state.Build(streaming: scheduler);
        coordinator.BeginLogin(0x11340021u);

        Assert.Throws<InvalidOperationException>(coordinator.ResetSession);
        Assert.True(coordinator.Snapshot.Cancelled);
        Assert.Equal(1, coordinator.Ownership.HostProjectionCount);
        Assert.True(
            coordinator.Ownership.PendingHostAcknowledgementCount > 0);

        coordinator.ResetSession();

        Assert.Equal(RuntimePortalSnapshot.Idle, coordinator.Snapshot);
        Assert.True(coordinator.Ownership.IsSessionIdle);
        Assert.Equal(2, scheduler.EndAttempts);
        Assert.Single(scheduler.Ends);
    }

    [Fact]
    public void SimulationReleaseFailure_RetriesAudioWithoutReplayingMaterialization()
    {
        var transit = new RuntimeWorldTransitState();
        var availability = new WorldGenerationAvailabilityState(transit);
        var world = new GpuWorldState(availability: availability);
        var audio = new RecordingAudioQuiescence
        {
            ThrowResumeCount = 1,
        };
        var quiescence = new WorldGenerationQuiescence(
            new SelectionState(),
            world,
            _ => null,
            audio);
        var coordinator = new WorldRevealCoordinator(
            transit,
            revealWindow: () => new StreamingRevealWindow(1, 1),
            isRenderNeighborhoodReady: (_, _, _) => true,
            isSpawnCellReady: _ => true,
            isTerrainNeighborhoodReady: (_, _) => true,
            areCompositeTexturesReady: () => true,
            prepareCompositeTextures: (_, _) => { },
            invalidateCompositeTextures: () => { },
            isSpawnClaimUnhydratable: _ => false,
            quiescence: quiescence);
        const uint cell = 0x3032001Cu;
        long generation = BeginPortal(
            coordinator,
            transit,
            cell,
            sequence: 1);
        Assert.True(coordinator.Evaluate(cell).IsReady);

        Assert.Throws<InvalidOperationException>(() =>
            coordinator.ObserveMaterialized(
                generation,
                1,
                cell));
        Assert.True(coordinator.Snapshot.Materialized);
        Assert.Equal(1, coordinator.PortalMaterializationCount);
        Assert.Equal(1, audio.ResumeAttempts);
        Assert.Equal(0, audio.ResumeCalls);
        Assert.Equal(
            1,
            coordinator.Ownership.PendingHostAcknowledgementCount);

        coordinator.RetryPendingHostWork();

        Assert.Equal(2, audio.ResumeAttempts);
        Assert.Equal(1, audio.ResumeCalls);
        Assert.Equal(1, coordinator.PortalMaterializationCount);
        Assert.Equal(
            0,
            coordinator.Ownership.PendingHostAcknowledgementCount);
        coordinator.Cancel();
    }

    private static long BeginPortal(
        WorldRevealCoordinator coordinator,
        RuntimeWorldTransitState transit,
        uint destinationCell,
        ushort sequence)
    {
        Assert.True(transit.TryQueueTeleportStart(sequence));
        Assert.True(transit.ActivateQueuedTeleport());
        Assert.True(transit.OfferTeleportDestination(
            new RuntimeTeleportDestination(
                EntityGuid: 0x50000001u,
                InstanceSequence: 1,
                PositionSequence: 1,
                TeleportSequence: sequence,
                ForcePositionSequence: 1,
                Position: new Position(
                    destinationCell,
                    Vector3.Zero,
                    Quaternion.Identity)),
            teleportTimestampAdvanced: true));
        Assert.True(coordinator.TryBeginPortal(
            sequence,
            destinationCell,
            out long generation));
        return generation;
    }

    private sealed class RecordingAudioQuiescence : AcDream.App.Audio.IWorldAudioQuiescence
    {
        public int SuspendCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public int SuspendAttempts { get; private set; }
        public int ResumeAttempts { get; private set; }
        public int ThrowSuspendCount { get; set; }
        public int ThrowResumeCount { get; set; }

        public void SuspendWorldAudio()
        {
            SuspendAttempts++;
            if (ThrowSuspendCount > 0)
            {
                ThrowSuspendCount--;
                throw new InvalidOperationException("suspend failed");
            }
            SuspendCalls++;
        }

        public void ResumeWorldAudio()
        {
            ResumeAttempts++;
            if (ThrowResumeCount > 0)
            {
                ThrowResumeCount--;
                throw new InvalidOperationException("resume failed");
            }
            ResumeCalls++;
        }
    }

    private sealed class RecordingDestinationScheduler
        : IWorldRevealStreamingScheduler
    {
        public List<(long Generation, uint Cell, int Radius)> Begins { get; } = [];
        public List<long> Ends { get; } = [];
        public int BeginAttempts { get; private set; }
        public int EndAttempts { get; private set; }
        public int ThrowBeginCount { get; set; }
        public int ThrowEndCount { get; set; }
        public Action? OnBegin { get; set; }
        public Action? OnEnd { get; set; }

        public void BeginDestinationReservation(
            long revealGeneration,
            uint destinationCell,
            int requiredRenderRadius)
        {
            BeginAttempts++;
            if (ThrowBeginCount > 0)
            {
                ThrowBeginCount--;
                throw new InvalidOperationException("stream begin failed");
            }
            Begins.Add(
                (revealGeneration, destinationCell, requiredRenderRadius));
            OnBegin?.Invoke();
        }

        public void EndDestinationReservation(long revealGeneration)
        {
            EndAttempts++;
            if (ThrowEndCount > 0)
            {
                ThrowEndCount--;
                throw new InvalidOperationException("stream end failed");
            }
            Ends.Add(revealGeneration);
            OnEnd?.Invoke();
        }
    }

    [Theory]
    [InlineData(3, 8)]
    [InlineData(4, 12)]
    [InlineData(5, 15)]
    public void DestinationReservation_OpensAtTheDerivedGateRadius(
        int nearRadius,
        int farRadius)
    {
        const uint outdoorCell = 0x11340021u;
        var streaming = new RecordingDestinationScheduler();
        var state = new State
        {
            Window = new StreamingRevealWindow(nearRadius, farRadius),
        };
        WorldRevealCoordinator coordinator = state.Build(streaming: streaming);

        long generation = coordinator.BeginLogin(outdoorCell);

        Assert.Equal(
            (generation, outdoorCell, state.Window.FarRadius),
            Assert.Single(streaming.Begins));
    }

    [Fact]
    public void IndoorDestinationReservation_OpensAtTheEnvCellArmsZeroRadius()
    {
        const uint indoorCell = 0x11340100u;
        var streaming = new RecordingDestinationScheduler();
        var state = new State { Window = new StreamingRevealWindow(4, 12) };
        WorldRevealCoordinator coordinator = state.Build(streaming: streaming);

        long generation = coordinator.BeginLogin(indoorCell);

        Assert.Equal(
            (generation, indoorCell, 0),
            Assert.Single(streaming.Begins));
    }

    [Fact]
    public void MidHoldRadiusChange_ReopensTheReservationOnTheSameGeneration()
    {
        const uint outdoorCell = 0x11340021u;
        var streaming = new RecordingDestinationScheduler();
        var state = new State { Window = new StreamingRevealWindow(3, 8) };
        WorldRevealCoordinator coordinator = state.Build(streaming: streaming);

        long generation = coordinator.BeginLogin(outdoorCell);
        coordinator.Evaluate(outdoorCell);
        Assert.Equal(
            (generation, outdoorCell, 8),
            Assert.Single(streaming.Begins));
        Assert.Empty(streaming.Ends);

        state.Window = new StreamingRevealWindow(5, 15);
        coordinator.Evaluate(outdoorCell);

        Assert.Equal(
            [(generation, outdoorCell, 8), (generation, outdoorCell, 15)],
            streaming.Begins);
        Assert.Equal([generation], streaming.Ends);

        coordinator.Evaluate(outdoorCell);
        Assert.Equal(2, streaming.Begins.Count);
        Assert.Single(streaming.Ends);
    }

    private sealed class RecordingRenderResourceScheduler
        : IWorldRevealRenderResourceScheduler
    {
        public List<long> Begins { get; } = [];
        public List<long> Ends { get; } = [];
        public int BeginAttempts { get; private set; }
        public int EndAttempts { get; private set; }
        public int ThrowBeginCount { get; set; }
        public int ThrowEndCount { get; set; }

        public void BeginDestinationReveal(long revealGeneration)
        {
            BeginAttempts++;
            if (ThrowBeginCount > 0)
            {
                ThrowBeginCount--;
                throw new InvalidOperationException("render begin failed");
            }
            Begins.Add(revealGeneration);
        }

        public void EndDestinationReveal(long revealGeneration)
        {
            EndAttempts++;
            if (ThrowEndCount > 0)
            {
                ThrowEndCount--;
                throw new InvalidOperationException("render end failed");
            }
            Ends.Add(revealGeneration);
        }
    }
}
