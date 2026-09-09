using System.Collections.Immutable;
using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Items;
using AcDream.Core.Lighting;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Plugins;
using AcDream.Core.Rendering;
using AcDream.Core.Rendering.Wb;
using AcDream.Core.Terrain;
using AcDream.Core.Vfx;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using AcDream.Runtime.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Streaming;

public sealed class LandblockPlacementPublicationCompositionTests
{
    private const uint LandblockId = 0x3031FFFFu;
    private const uint CellId = 0x30310014u;
    private const uint PlayerGuid = 0x5000000Au;

    [Fact]
    public void CollisionRefresh_WithMissingGraphicalBackend_PublishesBackendBeforeExactRestoreAck()
    {
        using var fixture = new Fixture();
        RuntimePlacementProjectionToken seeded = fixture.SeedResident(PlayerGuid);
        Assert.True(fixture.Lifetime.Physics.SetPosition.AcknowledgeProjection(seeded));
        using var subscription = fixture.Subscribe(PlayerGuid);
        LandblockStreamResult.Loaded result = fixture.Result();

        fixture.Pipeline.PublishLoaded(result);

        Assert.True(fixture.Pipeline.HasPendingPublication(result));
        Assert.Equal(0, fixture.Pipeline.Diagnostics.Physics.CompleteCount);
        Assert.Equal(0, fixture.Pipeline.Diagnostics.Statics.CompleteCount);
        Assert.True(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot restore));
        Assert.Equal(RuntimePlacementProjectionKind.Place, restore.Kind);
        Assert.Equal(CellId, restore.Token.ExactCellId);
        Assert.True(fixture.State.IsLoaded(LandblockId));
        Assert.Equal(
            1,
            fixture.Lifetime.Physics.CaptureOwnership()
                .CommittedCollisionPrefixMutationCount);
        Assert.False(fixture.Controller.IsRenderNeighborhoodResident(
            LandblockId,
            nearRadius: 0,
            farRadius: 0));
        var serviceWindow = new GraphicalRemotePlacementServiceWindow(
            fixture.State,
            fixture.Controller.IsLandblockPresentationReady);
        Assert.False(serviceWindow.IsWithinServiceWindow(CellId));

        Assert.True(subscription.RetryPending());
        Assert.False(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(out _));
        fixture.Pipeline.ResumePublication(result);

        Assert.False(fixture.Pipeline.HasPendingPublication(result));
        Assert.Equal(1, fixture.Pipeline.Diagnostics.Physics.CompleteCount);
        Assert.Equal(1, fixture.Pipeline.Diagnostics.Statics.CompleteCount);
        Assert.True(fixture.Controller.IsRenderNeighborhoodResident(
            LandblockId,
            nearRadius: 0,
            farRadius: 0));
        Assert.True(serviceWindow.IsWithinServiceWindow(CellId));
        LiveEntityRecord record = Assert.Single(fixture.Runtime.MaterializedRecords);
        Assert.True(record.IsSpatiallyProjected);
        Assert.Equal(CellId, record.FullCellId);
        WorldEntity projected = Assert.Single(
            fixture.State.Entities,
            entity => entity.ServerGuid == PlayerGuid);
        Assert.Same(
            Assert.Single(fixture.Runtime.MaterializedRecords).WorldEntity,
            projected);
    }

    [Fact]
    public void CollisionRefresh_TwoResidentsRetainOrderedExactReceiptsAndCompleteOnce()
    {
        const uint remoteGuid = 0x5000000Bu;
        using var fixture = new Fixture();
        RuntimePlacementProjectionToken firstSeed =
            fixture.SeedResident(PlayerGuid, isLocalPlayer: true);
        Assert.True(fixture.Lifetime.Physics.SetPosition.AcknowledgeProjection(
            firstSeed));
        RuntimePlacementProjectionToken secondSeed =
            fixture.SeedResident(remoteGuid, isLocalPlayer: false);
        Assert.True(fixture.Lifetime.Physics.SetPosition.AcknowledgeProjection(
            secondSeed));
        var observer = new PlacementRecorder();
        using IDisposable observerSubscription =
            fixture.Lifetime.Events.SubscribePlacement(observer);
        using var subscription = fixture.Subscribe(PlayerGuid);
        LandblockStreamResult.Loaded result = fixture.Result();

        fixture.Pipeline.PublishLoaded(result);

        RuntimePlacementProjectionSnapshot head = AssertHead(fixture);
        RuntimeEntityKey[] orderedPlaces = observer.Placements
            .Where(static placement =>
                placement.Kind == RuntimePlacementProjectionKind.Place)
            .Select(static placement => placement.Token.Entity)
            .ToArray();
        Assert.Equal(2, orderedPlaces.Length);
        Assert.Equal(orderedPlaces[0], head.Token.Entity);
        Assert.NotEqual(orderedPlaces[0], orderedPlaces[1]);
        Assert.Equal(2, fixture.Lifetime.Physics.SetPosition.PendingProjectionCount);
        Assert.True(fixture.State.IsLoaded(LandblockId));

        Assert.True(subscription.RetryPending());
        Assert.False(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(out _));
        fixture.Pipeline.ResumePublication(result);

        Assert.False(fixture.Pipeline.HasPendingPublication(result));
        Assert.Equal(1, fixture.Pipeline.Diagnostics.Physics.CompleteCount);
        Assert.Equal(1, fixture.Pipeline.Diagnostics.Statics.CompleteCount);
        Assert.Equal(
            [PlayerGuid, remoteGuid],
            fixture.State.Entities
                .Where(static entity => entity.ServerGuid != 0u)
                .Select(static entity => entity.ServerGuid)
                .Order()
                .ToArray());
        RuntimePhysicsOwnershipSnapshot ownership =
            fixture.Lifetime.Physics.CaptureOwnership();
        Assert.Equal(0, ownership.PendingSetPositionHostAcknowledgementCount);
        Assert.Equal(0, ownership.CollisionPrefixMutationCount);
        Assert.Equal(0, ownership.CollisionAdmissionCount);
    }

    [Fact]
    public void MeterExpiryAfterEngineTransfer_YieldsBeforeBackendAndNextFrameProgresses()
    {
        using var fixture = new Fixture();
        RuntimePlacementProjectionToken seeded = fixture.SeedResident(PlayerGuid);
        Assert.True(fixture.Lifetime.Physics.SetPosition.AcknowledgeProjection(seeded));
        using var subscription = fixture.Subscribe(PlayerGuid);
        LandblockStreamResult.Loaded result = fixture.Result();
        long timestamp = 0;
        var observer = new PlacementRecorder(
            onPlacement: placement =>
            {
                if (placement.Kind == RuntimePlacementProjectionKind.Place)
                    timestamp = 2;
            });
        using IDisposable observerSubscription =
            fixture.Lifetime.Events.SubscribePlacement(observer);
        StreamingWorkBudget budget = MeterBudget();
        var firstMeter = new StreamingWorkMeter(
            budget,
            () => timestamp,
            timestampFrequency: 1_000);

        LandblockPublicationAdvance first = fixture.Pipeline.PublishLoaded(
            result,
            LandblockStreamResultCost.Estimate(result),
            firstMeter,
            ensureProgress: false);
        firstMeter.FinishFrame();

        Assert.False(first.Completed);
        Assert.True(first.Progressed);
        Assert.Equal(StreamingWorkLimit.Time, firstMeter.Snapshot.LastLimit);
        Assert.False(fixture.State.IsLoaded(LandblockId));
        RuntimePlacementProjectionSnapshot head = AssertHead(fixture);
        Assert.Equal(RuntimePlacementProjectionKind.Place, head.Kind);
        Assert.Equal(1, observer.Placements.Count(static placement =>
            placement.Kind == RuntimePlacementProjectionKind.Place));

        timestamp = 0;
        var secondMeter = new StreamingWorkMeter(
            budget,
            () => timestamp,
            timestampFrequency: 1_000);
        LandblockPublicationAdvance second = fixture.Pipeline.ResumePublication(
            result,
            secondMeter,
            ensureProgress: false);
        secondMeter.FinishFrame();

        Assert.False(second.Completed);
        Assert.True(second.Progressed);
        Assert.True(fixture.State.IsLoaded(LandblockId));
        Assert.Equal(1, fixture.Pipeline.Diagnostics.Physics.BeginCount);
        Assert.Equal(0, fixture.Pipeline.Diagnostics.Physics.CompleteCount);
        Assert.Equal(1, observer.Placements.Count(static placement =>
            placement.Kind == RuntimePlacementProjectionKind.Place));

        Assert.True(subscription.RetryPending());
        var thirdMeter = new StreamingWorkMeter(budget);
        LandblockPublicationAdvance third = fixture.Pipeline.ResumePublication(
            result,
            thirdMeter,
            ensureProgress: true);
        thirdMeter.FinishFrame();
        Assert.True(third.Completed);
        Assert.Equal(1, fixture.Pipeline.Diagnostics.Physics.CompleteCount);
        Assert.Equal(1, fixture.Pipeline.Diagnostics.Statics.CompleteCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ControllerResetOrRecenter_AfterEngineTransferRetriesFailedBackendThenRetires(
        bool recenter,
        bool activateBeforeRequest = false)
    {
        var projection = new FailFirstStaticProjectionSink();
        using var fixture = new Fixture(projection);
        RuntimePlacementProjectionToken seeded = fixture.SeedResident(PlayerGuid);
        Assert.True(fixture.Lifetime.Physics.SetPosition.AcknowledgeProjection(seeded));
        using var subscription = fixture.Subscribe(PlayerGuid);
        LandblockStreamResult.Loaded result = fixture.Result();
        long timestamp = 0;
        var observer = new PlacementRecorder(
            onPlacement: placement =>
            {
                if (placement.Kind == RuntimePlacementProjectionKind.Place)
                    timestamp = 2;
            });
        using IDisposable observerSubscription =
            fixture.Lifetime.Events.SubscribePlacement(observer);
        var firstMeter = new StreamingWorkMeter(
            MeterBudget(),
            () => timestamp,
            timestampFrequency: 1_000);
        LandblockPublicationAdvance first = fixture.Pipeline.PublishLoaded(
            result,
            LandblockStreamResultCost.Estimate(result),
            firstMeter,
            ensureProgress: false);
        firstMeter.FinishFrame();
        Assert.False(first.Completed);
        Assert.False(fixture.State.IsLoaded(LandblockId));
        Assert.Equal(0, projection.Attempts);

        if (activateBeforeRequest)
        {
            Assert.Throws<InvalidOperationException>(() =>
                fixture.Pipeline.ResumePublication(result));
            Assert.True(fixture.State.IsLoaded(LandblockId));
            Assert.Equal(1, projection.Attempts);
            Assert.False(fixture.Controller.IsLandblockPresentationReady(
                LandblockId));
        }

        if (recenter)
            fixture.Controller.BeginOriginRecenter();
        else
            fixture.Controller.ForceReloadWindow();
        fixture.Controller.Tick(0x30, 0x31);

        Assert.True(fixture.Pipeline.HasPendingPublication(result));
        Assert.True(fixture.State.IsLoaded(LandblockId));
        Assert.Equal(activateBeforeRequest ? 2 : 1, projection.Attempts);
        Assert.False(fixture.Controller.IsLandblockPresentationReady(LandblockId));
        RuntimePhysicsOwnershipSnapshot afterFailure =
            fixture.Lifetime.Physics.CaptureOwnership();
        Assert.Equal(1, afterFailure.CommittedCollisionPrefixMutationCount);
        Assert.Equal(1, afterFailure.PendingSetPositionHostAcknowledgementCount);

        fixture.Controller.Tick(0x30, 0x31);
        Assert.Equal(2, projection.Attempts);
        Assert.True(fixture.State.IsLoaded(LandblockId));
        Assert.False(fixture.Controller.IsLandblockPresentationReady(LandblockId));

        Assert.True(subscription.RetryPending());
        bool recenterCommitted = false;
        for (int frame = 0;
             frame < 16
                && (fixture.Pipeline.HasPendingPublication(result)
                    || fixture.State.IsLoaded(LandblockId)
                    || fixture.Pipeline.PendingRetirementCount != 0);
             frame++)
        {
            fixture.Controller.Tick(0x30, 0x31);
            if (recenter
                && !recenterCommitted
                && fixture.Controller.IsOriginRecenterRetirementComplete())
            {
                Assert.True(fixture.Controller.TryCommitOriginRecenter(
                    destinationX: 0x32,
                    destinationY: 0x31,
                    isSealedDungeon: false));
                recenterCommitted = true;
            }
        }

        Assert.False(fixture.Pipeline.HasPendingPublication(result));
        Assert.False(fixture.State.IsLoaded(LandblockId));
        Assert.Equal(recenter, recenterCommitted);
        Assert.Equal(2, projection.Attempts);
        Assert.Equal(0, fixture.Meshes.ReferenceCount);
        RuntimePhysicsOwnershipSnapshot completed =
            fixture.Lifetime.Physics.CaptureOwnership();
        Assert.Equal(0, completed.CollisionPrefixMutationCount);
        Assert.Equal(0, completed.CollisionAdmissionCount);
        Assert.Equal(0, completed.PendingSetPositionHostAcknowledgementCount);
    }

    [Fact]
    public void PresentationFence_IsExactLandblockAndStopsBeforeLiveRecovery()
    {
        const uint unrelated = 0x4040FFFFu;
        using (var fixture = new Fixture())
        {
            RuntimePlacementProjectionToken seeded =
                fixture.SeedResident(PlayerGuid);
            Assert.True(fixture.Lifetime.Physics.SetPosition.AcknowledgeProjection(
                seeded));
            using var subscription = fixture.Subscribe(PlayerGuid);
            LandblockStreamResult.Loaded result = fixture.Result();
            fixture.Pipeline.PublishLoaded(result);
            fixture.State.AddLandblock(new LoadedLandblock(
                unrelated,
                new LandBlock(),
                Array.Empty<WorldEntity>()));
            var window = new GraphicalRemotePlacementServiceWindow(
                fixture.State,
                fixture.Controller.IsLandblockPresentationReady);

            Assert.False(window.IsWithinServiceWindow(CellId));
            Assert.True(window.IsWithinServiceWindow(unrelated));
        }

        bool failRecovery = true;
        using (var fixture = new Fixture(onLandblockLoaded: _ =>
        {
            if (failRecovery)
            {
                failRecovery = false;
                throw new InvalidOperationException("injected recovery failure");
            }
        }))
        {
            LandblockStreamResult.Loaded result = fixture.Result();
            Assert.Throws<InvalidOperationException>(() =>
                fixture.Pipeline.PublishLoaded(result));
            Assert.True(fixture.Pipeline.HasPendingPublication(result));
            Assert.True(fixture.Controller.IsLandblockPresentationReady(
                LandblockId));
            Assert.True(fixture.Controller.IsRenderNeighborhoodResident(
                LandblockId,
                nearRadius: 0,
                farRadius: 0));

            fixture.Pipeline.ResumePublication(result);
            Assert.False(fixture.Pipeline.HasPendingPublication(result));
        }
    }

    [Fact]
    public void CollisionRefresh_OutdoorSeamRecordsActualPrefixAndBackend()
    {
        const uint adjacentLandblock = 0x3131FFFFu;
        const uint seamCell = 0x3031003Cu;
        var seamPosition = new Vector3(191.5f, 84f, 0f);
        using var fixture = new Fixture();
        fixture.AddCollisionLandblock(adjacentLandblock);
        RuntimePlacementProjectionToken seeded = fixture.SeedResident(
            PlayerGuid,
            isLocalPlayer: true,
            seamCell,
            seamPosition);
        Assert.True(fixture.Lifetime.Physics.SetPosition.AcknowledgeProjection(
            seeded));
        using var subscription = fixture.Subscribe(PlayerGuid);
        LandblockStreamResult.Loaded result = fixture.Result();

        fixture.Pipeline.PublishLoaded(result);

        RuntimePlacementProjectionSnapshot restore = AssertHead(fixture);
        uint actualPrefix = restore.Token.ExactCellId & 0xFFFF0000u;
        Console.WriteLine(
            $"seam restoreCell=0x{restore.Token.ExactCellId:X8} "
            + $"actualPrefix=0x{actualPrefix:X8} "
            + $"targetBackend={fixture.State.IsLoaded(LandblockId)} "
            + $"adjacentBackend={fixture.State.IsLoaded(adjacentLandblock)}");
        Assert.Contains(actualPrefix, new[]
        {
            LandblockId & 0xFFFF0000u,
            adjacentLandblock & 0xFFFF0000u,
        });
        Assert.True(fixture.State.IsLoaded(LandblockId));
        Assert.False(fixture.State.IsLoaded(adjacentLandblock));
        Assert.True(
            fixture.State.IsLoaded(actualPrefix | 0xFFFFu),
            $"actual restore prefix 0x{actualPrefix:X8} lacked a backend");

        Assert.True(subscription.RetryPending());
        fixture.Pipeline.ResumePublication(result);
        Assert.False(fixture.Pipeline.HasPendingPublication(result));
        Assert.Equal(restore.Token.ExactCellId,
            Assert.Single(fixture.Runtime.MaterializedRecords).FullCellId);
    }

    [Fact]
    public void DebtFreeFullRetirementsDrainMultipleRealOwnersInOneBudgetedFrame()
    {
        uint[] landblockIds =
        [
            0x3031FFFFu,
            0x3032FFFFu,
            0x3033FFFFu,
        ];
        var removalOrder = new List<uint>();
        using var fixture = new Fixture(removeTerrain: removalOrder.Add);
        foreach (uint landblockId in landblockIds)
        {
            fixture.AddDebtFreeLandblock(landblockId);
            fixture.Pipeline.EnqueueFullRetirement(landblockId);
        }
        Assert.Equal(landblockIds.Length, fixture.Pipeline.PendingRetirementCount);

        var meter = new StreamingWorkMeter(RetirementBudget(
            maxEntityOperations: 64,
            maxGlRetireOperations: 16));
        fixture.Pipeline.AdvanceRetirements(meter);
        meter.FinishFrame();

        Assert.Equal(0, fixture.Pipeline.PendingRetirementCount);
        Assert.Equal(landblockIds.Length,
            fixture.PhysicsPublisher.Diagnostics.FullRemovalCount);
        Assert.Equal(landblockIds, removalOrder);
        Assert.Equal(removalOrder.Count, removalOrder.Distinct().Count());
        Assert.Equal(33, meter.Snapshot.Operations);
        Assert.Equal(33, meter.Snapshot.CompletedOperations);
        Assert.Equal(0, meter.Snapshot.YieldCount);
        Assert.All(landblockIds, landblockId =>
        {
            Assert.False(fixture.State.IsLoaded(landblockId));
            Assert.False(fixture.Lifetime.Physics.Engine
                .IsLandblockTerrainResident(landblockId));
            Assert.False(fixture.Pipeline.IsRetirementPending(landblockId));
        });
        RuntimePhysicsOwnershipSnapshot ownership =
            fixture.Lifetime.Physics.CaptureOwnership();
        Assert.Equal(0, ownership.CollisionPrefixMutationCount);
        Assert.Equal(0, ownership.CollisionPrefixQuiescenceCount);
        Assert.Equal(0, ownership.PendingCollisionPrefixProjectionCount);
    }

    [Fact]
    public void DebtFreeFullRetirementsRespectSmallMeterAndResumeExactlyOnce()
    {
        uint[] landblockIds =
        [
            0x3031FFFFu,
            0x3032FFFFu,
            0x3033FFFFu,
        ];
        using var fixture = new Fixture();
        foreach (uint landblockId in landblockIds)
        {
            fixture.AddDebtFreeLandblock(landblockId);
            fixture.Pipeline.EnqueueFullRetirement(landblockId);
        }

        int frames = 0;
        while (fixture.Pipeline.PendingRetirementCount != 0 && frames++ < 8)
        {
            var meter = new StreamingWorkMeter(RetirementBudget(
                maxEntityOperations: 7,
                maxGlRetireOperations: 4));
            fixture.Pipeline.AdvanceRetirements(meter);
            meter.FinishFrame();
            Assert.InRange(meter.Snapshot.Used.EntityOperations, 1, 7);
            Assert.InRange(meter.Snapshot.Used.GlRetireOperations, 0, 4);
            Assert.Equal(meter.Snapshot.Operations,
                meter.Snapshot.CompletedOperations);
        }

        Assert.Equal(5, frames);
        Assert.Equal(0, fixture.Pipeline.PendingRetirementCount);
        Assert.Equal(landblockIds.Length,
            fixture.PhysicsPublisher.Diagnostics.FullRemovalCount);
        Assert.All(landblockIds, landblockId =>
            Assert.False(fixture.Lifetime.Physics.Engine
                .IsLandblockTerrainResident(landblockId)));
    }

    [Fact]
    public void HeldAndThrowingPlaceKeepsExactRetirementFifoHeadUntilProductionSinkAck()
    {
        uint[] retiringLandblockIds =
        [
            0x3032FFFFu,
            0x3033FFFFu,
        ];
        var removalOrder = new List<uint>();
        using var fixture = new Fixture(removeTerrain: removalOrder.Add);
        fixture.AddDebtFreeLandblock(LandblockId);
        RuntimePlacementProjectionToken seeded = fixture.SeedResident(PlayerGuid);
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(seeded));
        using RuntimePlacementProjectionSubscription subscription =
            fixture.SubscribeWithThrowThenHold(PlayerGuid, out var sink);
        RuntimePlacementProjectionToken exact = fixture.MoveResident(PlayerGuid);
        Assert.Equal(exact, Assert.Single(sink.Attempts));
        Assert.Equal(RuntimePlacementProjectionKind.Place,
            AssertHead(fixture).Kind);
        foreach (uint landblockId in retiringLandblockIds)
        {
            fixture.AddDebtFreeLandblock(landblockId);
            fixture.Pipeline.EnqueueFullRetirement(landblockId);
        }

        var blockedMeter = new StreamingWorkMeter(RetirementBudget(
            maxEntityOperations: 64,
            maxGlRetireOperations: 16));
        fixture.Pipeline.AdvanceRetirements(blockedMeter);
        blockedMeter.FinishFrame();

        Assert.Equal(2, fixture.Pipeline.PendingRetirementCount);
        Assert.Equal(0,
            fixture.PhysicsPublisher.Diagnostics.FullRemovalCount);
        Assert.Empty(removalOrder);
        Assert.True(fixture.Lifetime.Physics.Engine
            .IsLandblockTerrainResident(retiringLandblockIds[0]));

        Assert.True(subscription.RetryPending());
        Assert.Equal([exact, exact], sink.Attempts);
        Assert.Equal(2, fixture.Pipeline.PendingRetirementCount);
        Assert.True(subscription.RetryPending());
        Assert.Equal([exact, exact, exact], sink.Attempts);
        Assert.False(fixture.Lifetime.Physics.SetPosition
            .TryPeekProjection(out _));

        var completedMeter = new StreamingWorkMeter(RetirementBudget(
            maxEntityOperations: 64,
            maxGlRetireOperations: 16));
        fixture.Pipeline.AdvanceRetirements(completedMeter);
        completedMeter.FinishFrame();

        Assert.Equal(0, fixture.Pipeline.PendingRetirementCount);
        Assert.Equal(2,
            fixture.PhysicsPublisher.Diagnostics.FullRemovalCount);
        Assert.Equal(
            retiringLandblockIds,
            removalOrder);
        Assert.Equal(removalOrder.Count, removalOrder.Distinct().Count());
    }

    private static RuntimePlacementProjectionSnapshot AssertHead(Fixture fixture)
    {
        Assert.True(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot head));
        return head;
    }

    private static StreamingWorkBudget MeterBudget() => new(
        TimeSpan.FromMilliseconds(1),
        maxCompletionAdmissions: 64,
        maxAdoptedCpuBytes: 1_000_000,
        maxEntityOperations: 32,
        maxGpuUploadBytes: 1_000_000,
        maxGlRetireOperations: 64,
        destinationReserveFraction: 0.75f);

    private static StreamingWorkBudget RetirementBudget(
        int maxEntityOperations,
        int maxGlRetireOperations) => new(
        TimeSpan.FromSeconds(10),
        maxCompletionAdmissions: 64,
        maxAdoptedCpuBytes: 1_000_000,
        maxEntityOperations,
        maxGpuUploadBytes: 1_000_000,
        maxGlRetireOperations,
        destinationReserveFraction: 0.75f);

    private sealed class Fixture : IDisposable
    {
        internal Fixture(
            IRenderStaticProjectionJournalSink? staticProjectionSink = null,
            Action<uint>? onLandblockLoaded = null,
            Action<uint>? removeTerrain = null)
        {
            Lifetime = new RuntimeEntityObjectLifetime(new PhysicsDataCache());
            Lifetime.BindEventContext(
                static () => new RuntimeGenerationToken(1UL),
                static () => 1UL);
            RuntimePhysicsState physics = Lifetime.Physics;
            physics.SetPosition.BeginCollisionGeneration(LandblockId, 1UL);
            physics.Engine.AddLandblock(
                LandblockId,
                new TerrainSurface(new byte[81], new float[256]),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                worldOffsetX: 0f,
                worldOffsetY: 0f);
            physics.SetPosition.CommitCollisionGeneration(
                LandblockId,
                1UL,
                ready: true);
            physics.ObserveLocalWorldFrame(CellId, teleportAdvanced: false);

            Meshes = new CountingMeshAdapter();
            State = new GpuWorldState(new LandblockSpawnAdapter(Meshes));
            Runtime = new LiveEntityRuntime(State, new NoopResources(), Lifetime);

            var render = new LandblockRenderPublisher(
                static (_, _, _) => { },
                removeTerrain ?? (static _ => { }),
                new CellVisibility(),
                State);
            PhysicsPublisher = new LandblockPhysicsPublisher(
                physics,
                new float[256]);
            var lighting = new LightingHookSink(
                new LightManager(),
                new NullPoseSource());
            var translucency = new TranslucencyFadeManager();
            var staticPublisher = new LandblockStaticPresentationPublisher(
                lighting,
                translucency,
                new WorldGameState(),
                new WorldEvents());
            var retirement = new LandblockPresentationRetirementOwner(
                render,
                PhysicsPublisher,
                staticPublisher,
                lighting,
                translucency);
            Pipeline = new LandblockPresentationPipeline(
                render,
                PhysicsPublisher,
                staticPublisher,
                State,
                retirement,
                onLandblockLoaded,
                ensureEnvCellMeshes: null,
                staticProjectionSink: staticProjectionSink);
            Controller = new StreamingController(
                enqueueLoad: static (_, _, _) => { },
                enqueueUnload: static (_, _) => { },
                drainCompletions: static _ =>
                    Array.Empty<LandblockStreamResult>(),
                State,
                nearRadius: 0,
                farRadius: 0,
                Pipeline);
        }

        internal RuntimeEntityObjectLifetime Lifetime { get; }
        internal CountingMeshAdapter Meshes { get; }
        internal GpuWorldState State { get; }
        internal LiveEntityRuntime Runtime { get; }
        internal LandblockPhysicsPublisher PhysicsPublisher { get; }
        internal LandblockPresentationPipeline Pipeline { get; }
        internal StreamingController Controller { get; }

        internal void AddCollisionLandblock(uint landblockId)
        {
            RuntimePhysicsState physics = Lifetime.Physics;
            physics.SetPosition.BeginCollisionGeneration(landblockId, 1UL);
            physics.Engine.AddLandblock(
                landblockId,
                new TerrainSurface(new byte[81], new float[256]),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                worldOffsetX: 192f,
                worldOffsetY: 0f);
            physics.SetPosition.CommitCollisionGeneration(
                landblockId,
                1UL,
                ready: true);
        }

        internal void AddDebtFreeLandblock(uint landblockId)
        {
            if (!Lifetime.Physics.Engine
                .IsLandblockTerrainResident(landblockId))
            {
                AddCollisionLandblock(landblockId);
            }
            if (!State.IsLoaded(landblockId))
            {
                State.AddLandblock(new LoadedLandblock(
                    landblockId,
                    new LandBlock
                    {
                        Terrain = new TerrainInfo[81],
                        Height = new byte[81],
                    },
                    Array.Empty<WorldEntity>()));
            }
        }

        internal RuntimePlacementProjectionToken SeedResident(
            uint guid,
            bool isLocalPlayer = true,
            uint cellId = CellId,
            Vector3? position = null)
        {
            Vector3 resolvedPosition = position ?? new Vector3(60f, 84f, 0f);
            LiveEntityRecord record = Runtime.RegisterAndMaterializeProjection(
                Spawn(guid, cellId, resolvedPosition),
                isLocalPlayer: isLocalPlayer);
            Assert.False(Runtime.HasActiveInitialCreateResidence(record.Canonical));
            var body = new PhysicsBody
            {
                Position = resolvedPosition,
                Orientation = Quaternion.Identity,
                LastUpdateTime = 1d,
                State = PhysicsStateFlags.Gravity,
                TransientState = TransientStateFlags.Active,
            };
            body.SnapToCell(cellId, body.Position, body.Position);
            Lifetime.Entities.SetFinalPhysicsState(
                record.Canonical,
                PhysicsStateFlags.Gravity);
            Lifetime.Entities.SetFullCell(
                record.Canonical,
                cellId,
                (cellId & 0xFFFF0000u) | 0xFFFFu);
            Lifetime.Entities.SetPhysicsBody(record.Canonical, body);
            record.Canonical.ObjectClock.Activate();
            Lifetime.Physics.AcknowledgeSpatialProjection(
                record.Canonical,
                spatial: true);

            var request = new PhysicsSetPositionRequest(
                body.Position,
                body.Orientation,
                cellId,
                body.CellPosition.Frame.Origin,
                [new FlatCollisionSphere(Vector3.Zero, 0.48f)],
                1f,
                0.4f,
                0.4f,
                Flags: PhysicsSetPositionFlags.Placement
                    | PhysicsSetPositionFlags.Slide,
                CurrentCellId: cellId);
            RuntimeSetPositionOutcome placed = Lifetime.Physics.SetPosition.Apply(
                record.Canonical,
                record.Canonical.PositionAuthorityVersion,
                new RuntimeSetPositionCommand(
                    request,
                    RuntimeSetPositionOperationKind.LocalAuthoritative,
                    1d,
                    record.Canonical.VelocityAuthorityVersion));
            Assert.Equal(
                RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
                placed.Status);
            return placed.Projection;
        }

        internal RuntimePlacementProjectionToken MoveResident(uint guid)
        {
            Assert.True(Lifetime.Entities.TryGetActive(
                guid,
                out RuntimeEntityRecord record));
            PhysicsBody body = Assert.IsType<PhysicsBody>(record.PhysicsBody);
            var next = body.Position + new Vector3(0.25f, 0f, 0f);
            var request = new PhysicsSetPositionRequest(
                next,
                body.Orientation,
                record.FullCellId,
                next,
                [new FlatCollisionSphere(Vector3.Zero, 0.48f)],
                1f,
                0.4f,
                0.4f,
                Flags: PhysicsSetPositionFlags.Placement
                    | PhysicsSetPositionFlags.Slide,
                CurrentCellId: record.FullCellId);
            RuntimeSetPositionOutcome placed = Lifetime.Physics.SetPosition.Apply(
                record,
                record.PositionAuthorityVersion,
                new RuntimeSetPositionCommand(
                    request,
                    RuntimeSetPositionOperationKind.LocalAuthoritative,
                    2d,
                    record.VelocityAuthorityVersion));
            Assert.Equal(
                RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
                placed.Status);
            return placed.Projection;
        }

        internal RuntimePlacementProjectionSubscription Subscribe(uint guid)
        {
            return new RuntimePlacementProjectionSubscription(
                Lifetime.Placements,
                static () => new RuntimeGenerationToken(1UL),
                CreatePlacementSink(guid));
        }

        internal RuntimePlacementProjectionSubscription SubscribeWithThrowThenHold(
            uint guid,
            out ThrowThenHoldPlacementSink sink)
        {
            sink = new ThrowThenHoldPlacementSink(CreatePlacementSink(guid));
            return new RuntimePlacementProjectionSubscription(
                Lifetime.Placements,
                static () => new RuntimeGenerationToken(1UL),
                sink);
        }

        private IRuntimePlacementProjectionSink CreatePlacementSink(uint guid)
        {
            var identity = new LocalPlayerIdentityState { ServerGuid = guid };
            var origin = new LiveWorldOriginState();
            origin.SetPlaceholder(0, 0);
            return new RuntimePlacementPresentationSink(
                Runtime,
                new RuntimeWorldTransitState(),
                new WorldGameState(),
                new WorldEvents(),
                new EntityEffectPoseRegistry(),
                new LocalPlayerShadowSynchronizer(
                    Lifetime.Physics.Engine,
                    Runtime,
                    identity,
                    origin,
                    new LocalPlayerShadowState()),
                () => guid,
                static _ => { });
        }

        internal LandblockStreamResult.Loaded Result()
        {
            var landblock = new LoadedLandblock(
                LandblockId,
                new LandBlock
                {
                    Terrain = new TerrainInfo[81],
                    Height = new byte[81],
                },
                Array.Empty<WorldEntity>(),
                PhysicsDatBundle.Empty);
            var shell = new EnvCellShellPlacement(
                CellId: 0x30310100u,
                GeometryId: 0x2_0000_0474UL,
                EnvironmentId: 0x0D000001u,
                CellStructure: 1,
                Surfaces: ImmutableArray<ushort>.Empty,
                WorldPosition: Vector3.Zero,
                Rotation: Quaternion.Identity,
                Transform: Matrix4x4.Identity,
                LocalBounds: new WbBoundingBox(Vector3.Zero, Vector3.One),
                WorldBounds: new WbBoundingBox(Vector3.Zero, Vector3.One));
            var build = new LandblockBuild(
                landblock,
                new EnvCellLandblockBuild(
                    LandblockId,
                    Array.Empty<LoadedCell>(),
                    [shell]),
                Origin: new LandblockBuildOrigin(0x30, 0x31));
            return new LandblockStreamResult.Loaded(
                LandblockId,
                LandblockStreamTier.Near,
                build,
                new LandblockMeshData(
                    Array.Empty<TerrainVertex>(),
                    Array.Empty<uint>()));
        }

        public void Dispose()
        {
            Runtime.Clear();
            Lifetime.Dispose();
        }
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        uint cellId = CellId,
        Vector3? worldPosition = null)
    {
        Vector3 positionValue = worldPosition ?? new Vector3(60f, 84f, 0f);
        var position = new CreateObject.ServerPosition(
            cellId,
            positionValue.X,
            positionValue.Y,
            positionValue.Z,
            1f,
            0f,
            0f,
            0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: 1);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.Gravity,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: 0x09000001u,
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
            [],
            [],
            [],
            null,
            null,
            "resident",
            (uint)ItemType.Creature,
            null,
            0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.Gravity,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private sealed class NoopResources : ILiveEntityResourceLifecycle
    {
        public void Register(WorldEntity entity) { }
        public void Unregister(WorldEntity entity) { }
    }

    private sealed class PlacementRecorder(
        Action<RuntimePlacementProjectionSnapshot>? onPlacement = null)
        : IRuntimePlacementObserver
    {
        internal List<RuntimePlacementProjectionSnapshot> Placements { get; } = [];

        public void OnPlacement(in RuntimePlacementDelta delta)
        {
            Placements.Add(delta.Placement);
            onPlacement?.Invoke(delta.Placement);
        }
    }

    internal sealed class ThrowThenHoldPlacementSink(
        IRuntimePlacementProjectionSink inner)
        : IRuntimePlacementProjectionSink
    {
        internal List<RuntimePlacementProjectionToken> Attempts { get; } = [];

        public bool TryApply(in RuntimePlacementProjectionSnapshot projection)
        {
            Attempts.Add(projection.Token);
            return Attempts.Count switch
            {
                1 => throw new InvalidOperationException(
                    "injected retirement sink failure"),
                2 => false,
                _ => inner.TryApply(projection),
            };
        }
    }

    private sealed class FailFirstStaticProjectionSink
        : IRenderStaticProjectionJournalSink
    {
        internal int Attempts { get; private set; }

        public void Reconcile(
            LandblockBuild build,
            GpuLandblockSpatialPublication publication)
        {
            Attempts++;
            if (Attempts == 1)
                throw new InvalidOperationException("injected projection failure");
        }

        public void Retire(GpuLandblockRetirement retirement) { }
    }

    internal sealed class CountingMeshAdapter : IWbMeshAdapter
    {
        internal int ReferenceCount { get; private set; }

        public void IncrementRefCount(ulong id) => ReferenceCount++;
        public void PinPreparedRenderData(ulong id) => ReferenceCount++;
        public void DecrementRefCount(ulong id) => ReferenceCount--;
    }

    private sealed class NullPoseSource : IEntityEffectPoseSource
    {
        public bool TryGetRootPose(uint localEntityId, out Matrix4x4 rootWorld)
        {
            rootWorld = default;
            return false;
        }

        public bool TryGetPartPose(
            uint localEntityId,
            int partIndex,
            out Matrix4x4 partLocal)
        {
            partLocal = default;
            return false;
        }
    }
}
