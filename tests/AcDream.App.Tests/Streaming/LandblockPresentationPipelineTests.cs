using System.Collections.Immutable;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Terrain;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using Xunit;

namespace AcDream.App.Tests.Streaming;

public sealed class LandblockPresentationPipelineTests
{
    [Fact]
    public void ControllerRejectsPresentationPipelineFromForeignWorldState()
    {
        var controllerState = new GpuWorldState();
        var foreignState = new GpuWorldState();
        var foreignPipeline = new LandblockPresentationPipeline(
            static (_, _) => { },
            foreignState);

        Assert.Throws<ArgumentException>(() => new StreamingController(
            enqueueLoad: static (_, _, _) => { },
            enqueueUnload: static (_, _) => { },
            drainCompletions: static _ => Array.Empty<LandblockStreamResult>(),
            controllerState,
            nearRadius: 0,
            farRadius: 0,
            presentationPipeline: foreignPipeline));
    }

    [Fact]
    public void LegacyPipelineRejectsRetirementCoordinatorFromForeignWorldState()
    {
        var pipelineState = new GpuWorldState();
        var foreignCoordinator = new LandblockRetirementCoordinator(
            new GpuWorldState(),
            static _ => { });

        Assert.Throws<ArgumentException>(() =>
            new LandblockPresentationPipeline(
                static (_, _) => { },
                pipelineState,
                retirementCoordinator: foreignCoordinator));
    }

    [Fact]
    public void PublicPresentationConstructorsExposeOnlyConcreteOwnerGraph()
    {
        string[] legacyNames =
        [
            "applyTerrain",
            "removeTerrain",
            "demoteNearLayer",
            "onLandblockLoaded",
            "ensureEnvCellMeshes",
            "retirementCoordinator",
        ];

        System.Reflection.ConstructorInfo controller = Assert.Single(
            typeof(StreamingController).GetConstructors());
        Assert.Contains(
            controller.GetParameters(),
            parameter => parameter.Name == "presentationPipeline");
        string[] names = controller.GetParameters()
            .Select(parameter => parameter.Name!)
            .ToArray();
        Assert.DoesNotContain(names, legacyNames.Contains);

        System.Reflection.ConstructorInfo pipeline = Assert.Single(
            typeof(LandblockPresentationPipeline).GetConstructors());
        Type[] pipelineParameterTypes = pipeline.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Assert.Contains(typeof(LandblockRenderPublisher), pipelineParameterTypes);
        Assert.Contains(typeof(LandblockPhysicsPublisher), pipelineParameterTypes);
        Assert.Contains(typeof(LandblockStaticPresentationPublisher), pipelineParameterTypes);
        Assert.DoesNotContain(
            pipeline.GetParameters(),
            parameter => parameter.Name == "publishBeforeSpatialCommit");

        string[] coordinatorOnlyMethods =
        [
            "BeginOriginRecenter",
            "IsOriginRecenterRetirementComplete",
            "TryCommitOriginRecenter",
            "TryCancelOriginRecenter",
        ];
        foreach (string methodName in coordinatorOnlyMethods)
        {
            Assert.Null(typeof(StreamingController).GetMethod(methodName));
            Assert.NotNull(typeof(StreamingController).GetMethod(
                methodName,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic));
        }
    }

    [Fact]
    public void OriginRecenter_CommitsAfterDetachWhileOldOwnerCleanupContinues()
    {
        const uint centerId = 0x1919FFFFu;
        const uint neighborId = 0x191AFFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            centerId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        state.AddLandblock(new LoadedLandblock(
            neighborId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var origin = new LiveWorldOriginState();
        Assert.True(origin.TryInitialize(0x19, 0x19));
        var enqueued = new List<uint>();
        bool failNeighbor = true;
        int neighborAttempts = 0;
        var controller = new StreamingController(
            enqueueLoad: (id, _) => enqueued.Add(id),
            enqueueUnload: static _ => { },
            drainCompletions: static _ => Array.Empty<LandblockStreamResult>(),
            applyTerrain: static (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0,
            removeTerrain: id =>
            {
                if (id != neighborId)
                    return;
                neighborAttempts++;
                if (failNeighbor)
                    throw new InvalidOperationException("injected non-center failure");
            },
            workBudgetOptions: GenerousWorkBudget());
        var recenter = new StreamingOriginRecenterCoordinator(controller, origin);

        Assert.False(recenter.Begin(0x22, 0x23, isSealedDungeon: false));
        controller.Tick(0x19, 0x19);

        Assert.Equal((0x19, 0x19), (origin.CenterX, origin.CenterY));
        Assert.False(state.IsLoaded(centerId));
        Assert.False(state.IsLoaded(neighborId));
        Assert.Equal(1, controller.PendingRetirementCount);
        Assert.Equal(2, controller.LastFullWindowRetirementLandblockCount);

        Assert.Empty(enqueued);
        Assert.Equal(1, neighborAttempts);

        failNeighbor = false;
        Assert.True(Converge(recenter, controller, 0x19, 0x19));

        Assert.Equal((0x22, 0x23), (origin.CenterX, origin.CenterY));
        Assert.Equal(1, controller.PendingRetirementCount);
        controller.Tick(0x22, 0x23);
        Assert.Equal(0, controller.PendingRetirementCount);
        Assert.Equal([0x2223FFFFu], enqueued);
    }

    [Fact]
    public void OriginRecenter_PendingOnlyLiveProjectionKeepsItsExistingRetirementOwner()
    {
        const uint oldLandblockId = 0x2424FFFFu;
        var live = Entity(1u, serverGuid: 0x70000004u);
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            oldLandblockId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        state.PlaceLiveEntityProjection(oldLandblockId, live);

        bool holdCleanup = true;
        var retirements = new LandblockRetirementCoordinator(
            state,
            ticket => ticket.RunOnce(
                LandblockRetirementStage.EntityLighting,
                () =>
                {
                    if (holdCleanup)
                    {
                        throw new InvalidOperationException(
                            "injected retained cleanup");
                    }
                }),
            _ => LandblockRetirementStage.EntityLighting);
        retirements.BeginFull(oldLandblockId);
        Assert.Equal(1, retirements.PendingCount);
        Assert.Equal(1, state.PendingLiveEntityCount);

        var origin = new LiveWorldOriginState();
        Assert.True(origin.TryInitialize(0x24, 0x24));
        var controller = new StreamingController(
            enqueueLoad: static (_, _) => { },
            enqueueUnload: static _ => { },
            drainCompletions: static _ => Array.Empty<LandblockStreamResult>(),
            applyTerrain: static (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0,
            retirementCoordinator: retirements,
            workBudgetOptions: GenerousWorkBudget());
        var recenter = new StreamingOriginRecenterCoordinator(controller, origin);

        Assert.False(recenter.Begin(0x25, 0x25, isSealedDungeon: false));
        Assert.True(Converge(recenter, controller, 0x24, 0x24));

        Assert.Equal((0x25, 0x25), (origin.CenterX, origin.CenterY));
        Assert.Equal(1, retirements.PendingCount);
        Assert.Equal(1, state.PendingLiveEntityCount);

        holdCleanup = false;
        controller.Tick(0x25, 0x25);
        Assert.Equal(0, retirements.PendingCount);
    }

    [Fact]
    public void OriginRecenter_CommittedReceiptInvariantFailsFastInsteadOfReplayingDetach()
    {
        const uint landblockId = 0x2626FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            landblockId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var retirements = new LandblockRetirementCoordinator(
            state,
            ticket => ticket.RunOnce(
                LandblockRetirementStage.EntityLighting,
                static () => throw new InvalidOperationException(
                    "injected retained cleanup")),
            _ => LandblockRetirementStage.EntityLighting);
        retirements.BeginFull(landblockId);
        Assert.Equal(1, retirements.PendingCount);

        state.AddLandblock(new LoadedLandblock(
            landblockId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));

        var origin = new LiveWorldOriginState();
        Assert.True(origin.TryInitialize(0x26, 0x26));
        var controller = new StreamingController(
            enqueueLoad: static (_, _) => { },
            enqueueUnload: static _ => { },
            drainCompletions: static _ => Array.Empty<LandblockStreamResult>(),
            applyTerrain: static (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0,
            retirementCoordinator: retirements,
            workBudgetOptions: GenerousWorkBudget());
        var recenter = new StreamingOriginRecenterCoordinator(controller, origin);
        Assert.False(recenter.Begin(0x27, 0x27, isSealedDungeon: false));

        StreamingMutationException error = Assert.Throws<StreamingMutationException>(
            () => controller.Tick(0x26, 0x26));

        Assert.True(error.MutationCommitted);
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.False(state.IsLoaded(landblockId));
    }

    [Fact]
    public void OriginRecenter_DetachesFullTwentyFiveByTwentyFiveWindowAtomically()
    {
        const int radius = 12;
        const int side = radius * 2 + 1;
        const int residentCount = side * side;
        var state = new GpuWorldState();
        for (int x = 0x40 - radius; x <= 0x40 + radius; x++)
        for (int y = 0x40 - radius; y <= 0x40 + radius; y++)
        {
            uint id = ((uint)x << 24) | ((uint)y << 16) | 0xFFFFu;
            state.AddLandblock(new LoadedLandblock(
                id,
                new LandBlock(),
                Array.Empty<WorldEntity>()));
        }

        var origin = new LiveWorldOriginState();
        Assert.True(origin.TryInitialize(0x40, 0x40));
        var controller = new StreamingController(
            enqueueLoad: static (_, _) => { },
            enqueueUnload: static _ => { },
            drainCompletions: static _ => Array.Empty<LandblockStreamResult>(),
            applyTerrain: static (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0,
            workBudgetOptions: new StreamingWorkBudgetOptions(
                MaxUpdateMilliseconds: 100,
                MaxCompletionAdmissions: 1,
                MaxAdoptedCpuBytes: 1,
                MaxEntityOperations: 1,
                MaxGpuUploadBytes: 1,
                MaxGlRetireOperations: 1,
                DestinationReserveFraction: 0.5f));
        var recenter = new StreamingOriginRecenterCoordinator(controller, origin);

        Assert.False(recenter.Begin(0x50, 0x50, isSealedDungeon: false));
        int frames = 0;
        while (!recenter.Advance() && frames < 8)
        {
            controller.Tick(0x40, 0x40);
            frames++;
        }

        Assert.InRange(frames, 1, 4);
        Assert.Empty(state.LoadedLandblockIds);
        Assert.Equal(residentCount, controller.LastFullWindowRetirementLandblockCount);
        Assert.Equal((0x50, 0x50), (origin.CenterX, origin.CenterY));
    }

    [Fact]
    public void OriginRecenter_PreparationFailureBeforeDetachRetainsOldOriginAndResidents()
    {
        const uint landblockId = 0x3030FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            landblockId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var origin = new LiveWorldOriginState();
        Assert.True(origin.TryInitialize(0x30, 0x30));
        bool failClear = true;
        int clearAttempts = 0;
        var controller = new StreamingController(
            enqueueLoad: static (_, _) => { },
            enqueueUnload: static _ => { },
            drainCompletions: static _ => Array.Empty<LandblockStreamResult>(),
            applyTerrain: static (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0,
            clearPendingLoads: () =>
            {
                clearAttempts++;
                if (failClear)
                    throw new InvalidOperationException("injected clear failure");
            });
        var recenter = new StreamingOriginRecenterCoordinator(controller, origin);

        Assert.False(recenter.Begin(0x40, 0x41, isSealedDungeon: false));
        controller.Tick(0x30, 0x30);
        Assert.Equal((0x30, 0x30), (origin.CenterX, origin.CenterY));
        Assert.True(state.IsLoaded(landblockId));
        Assert.Equal(0, controller.PendingRetirementCount);

        failClear = false;
        Assert.True(Converge(recenter, controller, 0x30, 0x30));

        Assert.Equal(2, clearAttempts);
        Assert.False(state.IsLoaded(landblockId));
        Assert.Equal((0x40, 0x41), (origin.CenterX, origin.CenterY));
    }

    [Fact]
    public void OriginRecenter_ObserverFailureHalfwayRetainsBarrierUntilSnapshotFinishes()
    {
        const uint firstId = 0x4242FFFFu;
        const uint secondId = 0x4243FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            firstId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        state.AddLandblock(new LoadedLandblock(
            secondId,
            new LandBlock(),
            [Entity(2, serverGuid: 0x70000042u)]));
        int observerCalls = 0;
        state.LiveProjectionVisibilityChanged += (_, _) =>
        {
            observerCalls++;
            throw new InvalidOperationException("injected observer failure");
        };
        var origin = new LiveWorldOriginState();
        Assert.True(origin.TryInitialize(0x42, 0x42));
        var removed = new List<uint>();
        var controller = new StreamingController(
            enqueueLoad: static (_, _) => { },
            enqueueUnload: static _ => { },
            drainCompletions: static _ => Array.Empty<LandblockStreamResult>(),
            applyTerrain: static (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0,
            removeTerrain: id => removed.Add(id));
        var recenter = new StreamingOriginRecenterCoordinator(controller, origin);

        Assert.False(recenter.Begin(0x43, 0x44, isSealedDungeon: false));
        for (int frame = 0; frame < 16 && observerCalls == 0; frame++)
            controller.Tick(0x42, 0x42);

        Assert.Equal((0x42, 0x42), (origin.CenterX, origin.CenterY));
        Assert.False(state.IsLoaded(firstId));
        Assert.False(state.IsLoaded(secondId));
        Assert.Equal(1, observerCalls);

        Assert.True(Converge(recenter, controller, 0x42, 0x42));
        Assert.Equal((0x43, 0x44), (origin.CenterX, origin.CenterY));
        Assert.Equal([firstId, secondId], removed);
    }

    [Fact]
    public void OriginRecenter_RetryPreservesLiveIdentityAndDoesNotRescueReusedGuid()
    {
        const uint oldId = 0x4545FFFFu;
        const uint destinationId = 0x4646FFFFu;
        const uint playerGuid = 0x50000045u;
        const uint remoteGuid = 0x70000045u;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            oldId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        WorldEntity player = Entity(10, playerGuid);
        WorldEntity remote = Entity(11, remoteGuid);
        state.MarkPersistent(playerGuid);
        state.PlaceLiveEntityProjection(oldId, player);
        state.PlaceLiveEntityProjection(oldId, remote);
        var origin = new LiveWorldOriginState();
        Assert.True(origin.TryInitialize(0x45, 0x45));
        bool failRetirement = true;
        var controller = new StreamingController(
            enqueueLoad: static (_, _) => { },
            enqueueUnload: static _ => { },
            drainCompletions: static _ => Array.Empty<LandblockStreamResult>(),
            applyTerrain: static (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0,
            removeTerrain: _ =>
            {
                if (failRetirement)
                    throw new InvalidOperationException("injected live-owner retry");
            });
        var recenter = new StreamingOriginRecenterCoordinator(controller, origin);

        Assert.False(recenter.Begin(0x46, 0x46, isSealedDungeon: false));
        controller.Tick(0x45, 0x45);

        Assert.Same(player, Assert.Single(state.DrainRescued()));
        Assert.Equal(1, state.PendingLiveEntityCount);
        Assert.Empty(state.Entities);

        failRetirement = false;
        Assert.True(Converge(recenter, controller, 0x45, 0x45));
        Assert.Empty(state.DrainRescued());
        state.AddLandblock(new LoadedLandblock(
            destinationId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        state.RebucketLiveEntity(player, destinationId);
        Assert.Same(player, Assert.Single(state.Entities));

        state.AddLandblock(new LoadedLandblock(
            oldId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        Assert.Contains(state.Entities, entity => ReferenceEquals(entity, remote));

        state.ForgetLiveEntity(playerGuid);
        WorldEntity replacement = Entity(12, playerGuid);
        state.MarkPersistent(playerGuid);
        state.PlaceLiveEntityProjection(destinationId, replacement);
        state.RemoveLandblock(destinationId);

        Assert.Same(replacement, Assert.Single(state.DrainRescued()));
        Assert.DoesNotContain(state.Entities, entity => ReferenceEquals(entity, player));
    }

    [Fact]
    public void OriginRecenter_ResetWithoutReplacementConvergesAtCurrentOrigin()
    {
        const uint landblockId = 0x5050FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            landblockId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var origin = new LiveWorldOriginState();
        Assert.True(origin.TryInitialize(0x50, 0x50));
        bool failRetirement = true;
        var controller = new StreamingController(
            enqueueLoad: static (_, _) => { },
            enqueueUnload: static _ => { },
            drainCompletions: static _ => Array.Empty<LandblockStreamResult>(),
            applyTerrain: static (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0,
            removeTerrain: _ =>
            {
                if (failRetirement)
                    throw new InvalidOperationException("injected retirement failure");
            });
        var recenter = new StreamingOriginRecenterCoordinator(controller, origin);

        Assert.False(recenter.Begin(0x60, 0x60, isSealedDungeon: false));
        recenter.Reset();
        Assert.True(recenter.IsPending);

        failRetirement = false;
        Assert.True(Converge(recenter, controller, 0x50, 0x50));

        Assert.False(recenter.IsPending);
        Assert.Equal((0x50, 0x50), (origin.CenterX, origin.CenterY));
        controller.Tick(0x50, 0x50);
    }

    [Fact]
    public void OriginRecenter_ResetBackToSealedSourceKeepsRadiusZeroMode()
    {
        const uint sourceId = 0x5252FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            sourceId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var origin = new LiveWorldOriginState();
        Assert.True(origin.TryInitialize(0x52, 0x52));
        var enqueued = new List<(uint Id, LandblockStreamJobKind Kind)>();
        bool failRetirement = true;
        var controller = new StreamingController(
            enqueueLoad: (id, kind) => enqueued.Add((id, kind)),
            enqueueUnload: static _ => { },
            drainCompletions: static _ => Array.Empty<LandblockStreamResult>(),
            applyTerrain: static (_, _) => { },
            state,
            nearRadius: 2,
            farRadius: 4,
            removeTerrain: _ =>
            {
                if (failRetirement)
                    throw new InvalidOperationException("injected retirement failure");
            });
        controller.PreCollapseToDungeon(0x52, 0x52);
        enqueued.Clear();
        var recenter = new StreamingOriginRecenterCoordinator(controller, origin);

        Assert.False(recenter.Begin(0x60, 0x60, isSealedDungeon: false));
        recenter.Reset();
        failRetirement = false;
        Assert.True(Converge(
            recenter,
            controller,
            0x52,
            0x52,
            insideDungeon: true));

        Assert.Equal((0x52, 0x52), (origin.CenterX, origin.CenterY));
        Assert.Equal([(sourceId, LandblockStreamJobKind.LoadNear)], enqueued);
        controller.Tick(0x52, 0x52, insideDungeon: true);
        Assert.Single(enqueued);
    }

    [Fact]
    public void OriginRecenter_SessionResetConvergesWithoutBootstrappingEndingWorld()
    {
        const uint sourceId = 0x5353FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            sourceId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var origin = new LiveWorldOriginState();
        Assert.True(origin.TryInitialize(0x53, 0x53));
        var enqueued = new List<uint>();
        bool failRetirement = true;
        var controller = new StreamingController(
            enqueueLoad: (id, _) => enqueued.Add(id),
            enqueueUnload: static _ => { },
            drainCompletions: static _ => Array.Empty<LandblockStreamResult>(),
            applyTerrain: static (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0,
            removeTerrain: _ =>
            {
                if (failRetirement)
                    throw new InvalidOperationException("injected session reset failure");
            });
        var recenter = new StreamingOriginRecenterCoordinator(controller, origin);

        Assert.False(recenter.Begin(0x54, 0x54, isSealedDungeon: true));
        recenter.Reset(sessionEnding: true);
        failRetirement = false;
        Assert.True(Converge(recenter, controller, 0x53, 0x53));

        Assert.Empty(enqueued);
        Assert.Equal((0x53, 0x53), (origin.CenterX, origin.CenterY));
        controller.Tick(0x53, 0x53);
        Assert.Equal([sourceId], enqueued);
    }

    [Fact]
    public void OriginRecenter_SessionResetCannotOverwriteNextSessionOrigin()
    {
        const uint sourceId = 0x5353FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            sourceId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var origin = new LiveWorldOriginState();
        Assert.True(origin.TryInitialize(0x53, 0x53));
        bool failRetirement = true;
        var controller = new StreamingController(
            enqueueLoad: static (_, _) => { },
            enqueueUnload: static _ => { },
            drainCompletions: static _ => Array.Empty<LandblockStreamResult>(),
            applyTerrain: static (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0,
            removeTerrain: _ =>
            {
                if (failRetirement)
                    throw new InvalidOperationException("injected session reset failure");
            });
        var recenter = new StreamingOriginRecenterCoordinator(controller, origin);

        Assert.False(recenter.Begin(0x54, 0x54, isSealedDungeon: true));
        recenter.Reset(sessionEnding: true);
        origin.Reset();
        Assert.True(origin.TryInitialize(0x61, 0x62));

        failRetirement = false;
        Assert.True(Converge(recenter, controller, 0x61, 0x62));

        Assert.Equal((0x61, 0x62), (origin.CenterX, origin.CenterY));
    }

    [Fact]
    public void OriginRecenter_CommittedPendingClearFailureDoesNotReplayClear()
    {
        var state = new GpuWorldState();
        var origin = new LiveWorldOriginState();
        Assert.True(origin.TryInitialize(0x10, 0x10));
        int clearAttempts = 0;
        var controller = new StreamingController(
            enqueueLoad: static (_, _) => { },
            enqueueUnload: static _ => { },
            drainCompletions: static _ => Array.Empty<LandblockStreamResult>(),
            applyTerrain: static (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0,
            clearPendingLoads: () =>
            {
                clearAttempts++;
                throw new StreamingMutationException(
                    "injected committed clear failure",
                    mutationCommitted: true);
            });
        var recenter = new StreamingOriginRecenterCoordinator(controller, origin);

        Assert.False(recenter.Begin(0x11, 0x11, isSealedDungeon: false));
        Assert.True(Converge(recenter, controller, 0x10, 0x10));

        Assert.Equal(1, clearAttempts);
        Assert.Equal((0x11, 0x11), (origin.CenterX, origin.CenterY));
    }

    [Fact]
    public void OriginRecenter_ReentrantClearAndRetirementDoNotRecurseOrSkipResidents()
    {
        const uint firstId = 0x7070FFFFu;
        const uint secondId = 0x7071FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            firstId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        state.AddLandblock(new LoadedLandblock(
            secondId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var origin = new LiveWorldOriginState();
        Assert.True(origin.TryInitialize(0x70, 0x70));
        StreamingController? controller = null;
        int clearCalls = 0;
        var removed = new List<uint>();
        controller = new StreamingController(
            enqueueLoad: static (_, _) => { },
            enqueueUnload: static _ => { },
            drainCompletions: static _ => Array.Empty<LandblockStreamResult>(),
            applyTerrain: static (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0,
            clearPendingLoads: () =>
            {
                clearCalls++;
                Assert.False(controller!.IsOriginRecenterRetirementComplete());
            },
            removeTerrain: id =>
            {
                removed.Add(id);
                if (id == firstId)
                    Assert.False(controller!.IsOriginRecenterRetirementComplete());
        });
        var recenter = new StreamingOriginRecenterCoordinator(controller, origin);

        Assert.False(recenter.Begin(0x71, 0x71, isSealedDungeon: false));
        Assert.True(Converge(recenter, controller, 0x70, 0x70));

        Assert.Equal(1, clearCalls);
        Assert.Equal([firstId, secondId], removed);
        Assert.False(state.IsLoaded(firstId));
        Assert.False(state.IsLoaded(secondId));
    }

    [Fact]
    public void OriginRecenter_DefersRadiusChangeUntilDestinationCommit()
    {
        const uint oldId = 0x7272FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            oldId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var origin = new LiveWorldOriginState();
        Assert.True(origin.TryInitialize(0x72, 0x72));
        var enqueued = new List<uint>();
        bool failRetirement = true;
        var controller = new StreamingController(
            enqueueLoad: (id, _) => enqueued.Add(id),
            enqueueUnload: static _ => { },
            drainCompletions: static _ => Array.Empty<LandblockStreamResult>(),
            applyTerrain: static (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0,
            removeTerrain: _ =>
            {
                if (failRetirement)
                    throw new InvalidOperationException("injected radius barrier failure");
            });
        var recenter = new StreamingOriginRecenterCoordinator(controller, origin);

        Assert.False(recenter.Begin(0x73, 0x73, isSealedDungeon: false));
        controller.ReconfigureRadii(nearRadius: 1, farRadius: 2);
        controller.Tick(0x72, 0x72);

        Assert.Equal(0, controller.NearRadius);
        Assert.Equal(0, controller.FarRadius);
        Assert.Empty(enqueued);

        failRetirement = false;
        Assert.True(Converge(recenter, controller, 0x72, 0x72));
        controller.Tick(0x73, 0x73);

        Assert.Equal(1, controller.NearRadius);
        Assert.Equal(2, controller.FarRadius);
        Assert.Equal(25, enqueued.Count);
    }

    [Fact]
    public void OriginRecenter_SealedDungeonPinsRadiusZeroBeforeCenterCompletion()
    {
        const uint oldId = 0x2020FFFFu;
        const uint dungeonId = 0x3030FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            oldId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var origin = new LiveWorldOriginState();
        Assert.True(origin.TryInitialize(0x20, 0x20));
        var outbox = new Queue<LandblockStreamResult>();
        var enqueued = new List<(uint Id, LandblockStreamJobKind Kind)>();
        bool failDungeonEnqueue = true;
        int enqueueAttempts = 0;
        int publications = 0;
        var controller = new StreamingController(
            enqueueLoad: (id, kind) =>
            {
                enqueueAttempts++;
                if (failDungeonEnqueue)
                    throw new InvalidOperationException("injected dungeon enqueue failure");
                enqueued.Add((id, kind));
            },
            enqueueUnload: static _ => { },
            drainCompletions: max => Drain(outbox, max),
            applyTerrain: (_, _) => publications++,
            state,
            nearRadius: 2,
            farRadius: 4,
            workBudgetOptions: new StreamingWorkBudgetOptions(
                MaxUpdateMilliseconds: 100,
                MaxCompletionAdmissions: 64,
                MaxAdoptedCpuBytes: 1_000_000,
                MaxEntityOperations: 1_000,
                MaxGpuUploadBytes: 1_000_000,
                MaxGlRetireOperations: 1_000,
                DestinationReserveFraction: 0.75f));
        var recenter = new StreamingOriginRecenterCoordinator(controller, origin);

        Assert.False(recenter.Begin(0x30, 0x30, isSealedDungeon: true));
        Assert.True(recenter.IsPending);
        controller.Tick(0x30, 0x30, insideDungeon: true);
        Assert.Empty(enqueued);
        Assert.False(recenter.Advance());

        failDungeonEnqueue = false;
        Assert.True(recenter.Advance());
        Assert.Equal(2, enqueueAttempts);
        Assert.Equal([(dungeonId, LandblockStreamJobKind.LoadNear)], enqueued);

        outbox.Enqueue(new LandblockStreamResult.Loaded(
            dungeonId,
            LandblockStreamTier.Near,
            new LoadedLandblock(
                dungeonId,
                new LandBlock(),
                Array.Empty<WorldEntity>()),
            EmptyMesh(),
            generation: 1));
        controller.Tick(0x30, 0x30, insideDungeon: true);

        Assert.Equal(1, publications);
        Assert.True(state.IsNearTier(dungeonId));
    }

    [Fact]
    public void Loaded_PreservesPresentationSpatialPinReplayAndLiveRecoveryOrder()
    {
        const uint landblockId = 0x2020FFFFu;
        var calls = new List<string>();
        var meshes = new RecordingMeshAdapter(calls);
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        LandblockBuild build = BuildWithShell(landblockId);
        var pipeline = new LandblockPresentationPipeline(
            publishBeforeSpatialCommit: (accepted, _) =>
            {
                Assert.Same(build, accepted);
                Assert.False(state.IsLoaded(landblockId));
                calls.Add("presentation");
            },
            state,
            onLandblockLoaded: id =>
            {
                Assert.Equal(landblockId, id);
                Assert.True(state.IsLoaded(id));
                calls.Add("live-recovery");
            },
            ensureEnvCellMeshes: envCells =>
            {
                Assert.Same(build.EnvCells, envCells);
                Assert.True(state.IsLoaded(landblockId));
                calls.Add("mesh-replay-after-pin");
            });

        pipeline.PublishLoaded(new LandblockStreamResult.Loaded(
            landblockId,
            LandblockStreamTier.Near,
            build,
            EmptyMesh()));

        Assert.Equal(
            ["presentation", "pin", "mesh-replay-after-pin", "live-recovery"],
            calls);
        Assert.True(state.IsNearTier(landblockId));
    }

    [Fact]
    public void Promoted_ExistingFar_MergesNearLayerBeforeReplayAndRecovery()
    {
        const uint landblockId = 0x3030FFFFu;
        var calls = new List<string>();
        var meshes = new RecordingMeshAdapter(calls);
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        state.AddLandblock(
            new LoadedLandblock(
                landblockId,
                new LandBlock(),
                Array.Empty<WorldEntity>(),
                PhysicsDatBundle.Empty),
            tier: LandblockStreamTier.Far);
        LandblockBuild build = BuildWithShell(landblockId, Entity(7));
        var pipeline = new LandblockPresentationPipeline(
            publishBeforeSpatialCommit: (_, _) => calls.Add("presentation"),
            state,
            onLandblockLoaded: _ =>
            {
                Assert.True(state.IsNearTier(landblockId));
                Assert.Contains(state.Entities, entity => entity.Id == 7);
                calls.Add("live-recovery");
            },
            ensureEnvCellMeshes: _ =>
            {
                Assert.True(state.IsNearTier(landblockId));
                Assert.Contains(state.Entities, entity => entity.Id == 7);
                calls.Add("mesh-replay-after-pin");
            });

        pipeline.PublishPromoted(new LandblockStreamResult.Promoted(
            landblockId,
            build,
            EmptyMesh()),
            mergeIntoExistingLandblock: true);

        Assert.Equal(
            ["presentation", "pin", "mesh-replay-after-pin", "live-recovery"],
            calls);
    }

    [Fact]
    public void PublishAsFar_StripsNearPayloadAndSkipsEnvCellReplay()
    {
        const uint landblockId = 0x4040FFFFu;
        var state = new GpuWorldState();
        var origin = new LandblockBuildOrigin(0x40, 0x41);
        LandblockBuild source = BuildWithShell(landblockId, Entity(9)) with
        {
            Origin = origin,
        };
        LandblockBuild? published = null;
        int replayCount = 0;
        int recoveryCount = 0;
        var pipeline = new LandblockPresentationPipeline(
            publishBeforeSpatialCommit: (build, _) => published = build,
            state,
            onLandblockLoaded: _ => recoveryCount++,
            ensureEnvCellMeshes: _ => replayCount++);

        var accepted = new LandblockStreamResult.Loaded(
            landblockId,
            LandblockStreamTier.Near,
            source,
            EmptyMesh());
        pipeline.PublishAsFar(accepted, source, accepted.MeshData);

        Assert.NotNull(published);
        Assert.Empty(published.Landblock.Entities);
        Assert.Same(PhysicsDatBundle.Empty, published.Landblock.PhysicsDats);
        Assert.Null(published.EnvCells);
        Assert.Equal(origin, published.Origin);
        Assert.True(state.IsLoaded(landblockId));
        Assert.False(state.IsNearTier(landblockId));
        Assert.Equal(0, replayCount);
        Assert.Equal(1, recoveryCount);
    }

    [Fact]
    public void EnvCellReplayFailure_ResumesSuffixWithoutReplayingCommittedPrefix()
    {
        const uint landblockId = 0x5151FFFFu;
        var calls = new List<string>();
        var state = new GpuWorldState(new LandblockSpawnAdapter(
            new RecordingMeshAdapter(calls)));
        bool failReplayOnce = true;
        var pipeline = new LandblockPresentationPipeline(
            publishBeforeSpatialCommit: (_, _) => calls.Add("presentation"),
            state,
            onLandblockLoaded: _ => calls.Add("live-recovery"),
            ensureEnvCellMeshes: _ =>
            {
                calls.Add("mesh-replay");
                if (failReplayOnce)
                {
                    failReplayOnce = false;
                    throw new InvalidOperationException("injected replay failure");
                }
            });
        LandblockBuild build = BuildWithShell(landblockId);
        var result = new LandblockStreamResult.Loaded(
            landblockId,
            LandblockStreamTier.Near,
            build,
            EmptyMesh());

        Assert.Throws<InvalidOperationException>(() => pipeline.PublishLoaded(result));
        Assert.True(state.IsNearTier(landblockId));
        Assert.True(pipeline.HasPendingPublication(result));

        pipeline.PublishLoaded(result);

        Assert.Equal(
            ["presentation", "pin", "mesh-replay", "mesh-replay", "live-recovery"],
            calls);
        Assert.False(pipeline.HasPendingPublication(result));
    }

    [Fact]
    public void PromotionReplayFailure_RetainsOriginalSelfContainedModeAcrossRetry()
    {
        const uint landblockId = 0x5252FFFFu;
        var calls = new List<string>();
        var state = new GpuWorldState(new LandblockSpawnAdapter(
            new RecordingMeshAdapter(calls)));
        bool failReplayOnce = true;
        var pipeline = new LandblockPresentationPipeline(
            publishBeforeSpatialCommit: (_, _) => calls.Add("presentation"),
            state,
            onLandblockLoaded: _ => calls.Add("live-recovery"),
            ensureEnvCellMeshes: _ =>
            {
                calls.Add("mesh-replay");
                if (failReplayOnce)
                {
                    failReplayOnce = false;
                    throw new InvalidOperationException("injected replay failure");
                }
            });
        LandblockBuild build = BuildWithShell(landblockId, Entity(17));
        var result = new LandblockStreamResult.Promoted(
            landblockId,
            build,
            EmptyMesh());

        Assert.Throws<InvalidOperationException>(() => pipeline.PublishPromoted(
            result,
            mergeIntoExistingLandblock: false));
        Assert.True(state.IsNearTier(landblockId));

        pipeline.PublishPromoted(result, mergeIntoExistingLandblock: true);

        Assert.Equal(
            ["presentation", "pin", "mesh-replay", "mesh-replay", "live-recovery"],
            calls);
        Assert.Single(state.Entities, entity => entity.Id == 17);
    }

    [Fact]
    public void SpatialPinFailure_ControllerRetriesExactCompletionWithoutReplayingPresentation()
    {
        const uint landblockId = 0x6060FFFFu;
        var meshes = new FailFirstPreparedPinAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        var outbox = new Queue<LandblockStreamResult>();
        LandblockBuild build = BuildWithShell(landblockId);
        outbox.Enqueue(new LandblockStreamResult.Loaded(
            landblockId,
            LandblockStreamTier.Near,
            build,
            EmptyMesh()));
        int presentationCalls = 0;
        int replayCalls = 0;
        int liveRecoveryCalls = 0;
        var controller = new StreamingController(
            enqueueLoad: (_, _) => { },
            enqueueUnload: _ => { },
            drainCompletions: max => Drain(outbox, max),
            applyTerrain: (_, _) => presentationCalls++,
            state,
            nearRadius: 0,
            farRadius: 0,
            onLandblockLoaded: _ => liveRecoveryCalls++,
            ensureEnvCellMeshes: _ => replayCalls++,
            workBudgetOptions: GenerousWorkBudget());

        Assert.Throws<InvalidOperationException>(() => controller.Tick(0x60, 0x60));
        Assert.True(state.IsNearTier(landblockId));
        Assert.Equal(1, presentationCalls);
        Assert.Equal(0, replayCalls);
        Assert.Equal(0, liveRecoveryCalls);

        controller.Tick(0x60, 0x60);

        Assert.Equal(1, presentationCalls);
        Assert.Equal(2, meshes.PinAttempts);
        Assert.Equal(1, replayCalls);
        Assert.Equal(1, liveRecoveryCalls);
    }

    [Fact]
    public void PromotionPinFailure_RetriesActivationWithoutAppendingEntitiesAgain()
    {
        const uint landblockId = 0x6161FFFFu;
        var meshes = new FailFirstPreparedPinAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        state.AddLandblock(
            new LoadedLandblock(
                landblockId,
                new LandBlock(),
                Array.Empty<WorldEntity>()),
            tier: LandblockStreamTier.Far);
        int presentationCalls = 0;
        var pipeline = new LandblockPresentationPipeline(
            publishBeforeSpatialCommit: (_, _) => presentationCalls++,
            state);
        WorldEntity promotedEntity = Entity(31);
        LandblockBuild build = BuildWithShell(landblockId, promotedEntity);
        var result = new LandblockStreamResult.Promoted(
            landblockId,
            build,
            EmptyMesh());

        Assert.Throws<InvalidOperationException>(() => pipeline.PublishPromoted(
            result,
            mergeIntoExistingLandblock: true));
        Assert.Single(state.Entities, entity => ReferenceEquals(entity, promotedEntity));

        pipeline.PublishPromoted(result, mergeIntoExistingLandblock: true);

        Assert.Equal(1, presentationCalls);
        Assert.Equal(2, meshes.PinAttempts);
        Assert.Single(state.Entities, entity => ReferenceEquals(entity, promotedEntity));
        Assert.False(pipeline.HasPendingPublication(result));
    }

    [Fact]
    public void LoadedPinFailure_RetryPreservesPendingLiveProjectionMergedAtSpatialCommit()
    {
        const uint landblockId = 0x6262FFFFu;
        var meshes = new FailFirstPreparedPinAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        var pendingLive = new WorldEntity
        {
            Id = 77,
            ServerGuid = 0x80000077u,
            SourceGfxObjOrSetupId = 0x01000077u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = [new MeshRef(0x01000077u, Matrix4x4.Identity)],
        };
        state.PlaceLiveEntityProjection(0x62620100u, pendingLive);
        LandblockBuild build = BuildWithShell(landblockId, Entity(32));
        var result = new LandblockStreamResult.Loaded(
            landblockId,
            LandblockStreamTier.Near,
            build,
            EmptyMesh());
        var pipeline = new LandblockPresentationPipeline(
            static (_, _) => { },
            state);

        Assert.Throws<InvalidOperationException>(() => pipeline.PublishLoaded(result));
        Assert.Contains(state.Entities, entity => ReferenceEquals(entity, pendingLive));
        Assert.Equal(0, state.PendingLiveEntityCount);

        pipeline.PublishLoaded(result);

        Assert.Contains(state.Entities, entity => ReferenceEquals(entity, pendingLive));
        Assert.Single(state.Entities, entity => ReferenceEquals(entity, pendingLive));
        Assert.Equal(0, state.PendingLiveEntityCount);
        Assert.Equal(2, meshes.PinAttempts);
    }

    [Fact]
    public void StaticRenderProjection_ReconcilesOnlyAfterSuccessfulSpatialActivation()
    {
        const uint landblockId = 0x6363FFFFu;
        var meshes = new FailFirstPreparedPinAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        var journal = new RenderProjectionJournal(
            RenderSceneGeneration.FromRaw(1));
        var statics = new StaticRenderProjectionJournal(journal);
        LandblockBuild build = BuildWithShell(landblockId, Entity(32));
        var result = new LandblockStreamResult.Loaded(
            landblockId,
            LandblockStreamTier.Near,
            build,
            EmptyMesh());
        var pipeline = new LandblockPresentationPipeline(
            static (_, _) => { },
            state,
            staticProjectionSink: statics);

        Assert.Throws<InvalidOperationException>(
            () => pipeline.PublishLoaded(result));
        Assert.Equal(0, journal.Count);
        Assert.Equal(0, statics.ProjectionCount);

        pipeline.PublishLoaded(result);

        Assert.Equal(2, journal.Count);
        Assert.Equal(2, statics.ProjectionCount);
        Assert.Equal(2, meshes.PinAttempts);
        pipeline.PublishLoaded(result);
        Assert.Equal(2, journal.Count);
    }

    [Fact]
    public void PriorityFailure_PreservesFailingResultAndUntouchedDrainedSuffix()
    {
        const uint priorityId = 0x7070FFFFu;
        const uint laterId = 0x7071FFFFu;
        const uint unloadId = 0x1010FFFFu;
        var meshes = new FailFirstPreparedPinAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        state.AddLandblock(new LoadedLandblock(
            unloadId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var outbox = new Queue<LandblockStreamResult>(
        [
            new LandblockStreamResult.Loaded(
                priorityId,
                LandblockStreamTier.Near,
                BuildWithShell(priorityId),
                EmptyMesh()),
            new LandblockStreamResult.Loaded(
                laterId,
                LandblockStreamTier.Near,
                BuildWithShell(laterId),
                EmptyMesh()),
            new LandblockStreamResult.Unloaded(unloadId),
        ]);
        var calls = new List<string>();
        var controller = new StreamingController(
            enqueueLoad: (_, _) => { },
            enqueueUnload: _ => { },
            drainCompletions: max => Drain(outbox, max),
            applyTerrain: (build, _) => calls.Add($"publish:{build.LandblockId:X8}"),
            state,
            nearRadius: 0,
            farRadius: 2,
            removeTerrain: id => calls.Add($"unload:{id:X8}"),
            workBudgetOptions: new StreamingWorkBudgetOptions(
                MaxUpdateMilliseconds: 100,
                MaxCompletionAdmissions: 3,
                MaxAdoptedCpuBytes: 1_000_000,
                MaxEntityOperations: 1_000,
                MaxGpuUploadBytes: 1_000_000,
                MaxGlRetireOperations: 1_000,
                DestinationReserveFraction: 0.75f));
        controller.BeginDestinationReservation(1, priorityId, 0);

        Assert.Throws<InvalidOperationException>(() => controller.Tick(0x70, 0x70));
        Assert.Empty(outbox);
        Assert.False(state.IsLoaded(laterId));
        Assert.True(state.IsLoaded(unloadId));

        controller.EndDestinationReservation(1);
        controller.Tick(0x70, 0x70);

        Assert.True(state.IsLoaded(priorityId));
        Assert.True(state.IsLoaded(laterId));
        Assert.False(state.IsLoaded(unloadId));
        Assert.Equal(
            [
                $"publish:{priorityId:X8}",
                $"unload:{unloadId:X8}",
                $"publish:{laterId:X8}",
            ],
            calls);
    }

    [Fact]
    public void HardRecenter_ConvergesPendingSpatialPublicationBeforeRetiringIt()
    {
        const uint landblockId = 0x8080FFFFu;
        var meshes = new FailFirstPreparedPinAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        var outbox = new Queue<LandblockStreamResult>();
        var result = new LandblockStreamResult.Loaded(
            landblockId,
            LandblockStreamTier.Near,
            BuildWithShell(landblockId),
            EmptyMesh());
        outbox.Enqueue(result);
        int presentationCalls = 0;
        int replayCalls = 0;
        int recoveryCalls = 0;
        var controller = new StreamingController(
            enqueueLoad: (_, _) => { },
            enqueueUnload: _ => { },
            drainCompletions: max => Drain(outbox, max),
            applyTerrain: (_, _) => presentationCalls++,
            state,
            nearRadius: 0,
            farRadius: 0,
            onLandblockLoaded: _ => recoveryCalls++,
            ensureEnvCellMeshes: _ => replayCalls++,
            workBudgetOptions: GenerousWorkBudget());

        Assert.Throws<InvalidOperationException>(() => controller.Tick(0x80, 0x80));
        Assert.True(state.IsNearTier(landblockId));

        controller.ForceReloadWindow();
        for (int frame = 0; frame < 16 && state.IsLoaded(landblockId); frame++)
            controller.Tick(0x80, 0x80);

        Assert.False(state.IsLoaded(landblockId));
        Assert.Equal(1, presentationCalls);
        Assert.Equal(1, replayCalls);
        Assert.Equal(1, recoveryCalls);
        Assert.Equal(2, meshes.PinAttempts);
    }

    [Fact]
    public void DesiredNearToFarChurn_ResumesOriginalPublicationThenDemotes()
    {
        const uint landblockId = 0x9090FFFFu;
        var calls = new List<string>();
        var state = new GpuWorldState(new LandblockSpawnAdapter(
            new RecordingMeshAdapter(calls)));
        var outbox = new Queue<LandblockStreamResult>();
        var result = new LandblockStreamResult.Loaded(
            landblockId,
            LandblockStreamTier.Near,
            BuildWithShell(landblockId, Entity(23)),
            EmptyMesh());
        outbox.Enqueue(result);
        bool failReplayOnce = true;
        var controller = new StreamingController(
            enqueueLoad: (_, _) => { },
            enqueueUnload: _ => { },
            drainCompletions: max => Drain(outbox, max),
            applyTerrain: (_, _) => calls.Add("presentation"),
            state,
            nearRadius: 0,
            farRadius: 2,
            demoteNearLayer: _ => calls.Add("demote"),
            ensureEnvCellMeshes: _ =>
            {
                calls.Add("mesh-replay");
                if (failReplayOnce)
                {
                    failReplayOnce = false;
                    throw new InvalidOperationException("injected replay failure");
                }
            },
            workBudgetOptions: new StreamingWorkBudgetOptions(
                MaxUpdateMilliseconds: 100,
                MaxCompletionAdmissions: 64,
                MaxAdoptedCpuBytes: 1_000_000,
                MaxEntityOperations: 1_000,
                MaxGpuUploadBytes: 1_000_000,
                MaxGlRetireOperations: 1_000,
                DestinationReserveFraction: 0.75f));

        Assert.Throws<InvalidOperationException>(() => controller.Tick(0x90, 0x90));
        Assert.True(state.IsNearTier(landblockId));

        controller.Tick(0x90, 0x93);

        Assert.True(state.IsLoaded(landblockId));
        Assert.False(state.IsNearTier(landblockId));
        Assert.DoesNotContain(state.Entities, entity => entity.Id == 23);
        Assert.Equal(1, calls.Count(call => call == "presentation"));
        Assert.Equal(2, calls.Count(call => call == "mesh-replay"));
        Assert.Contains("demote", calls);
    }

    [Fact]
    public void CoarsePresentationFailure_IsFailFastAndNotRetainedForAutomaticReplay()
    {
        const uint landblockId = 0xA0A0FFFFu;
        var state = new GpuWorldState();
        int presentationCalls = 0;
        var pipeline = new LandblockPresentationPipeline(
            publishBeforeSpatialCommit: (_, _) =>
            {
                presentationCalls++;
                throw new StreamingMutationException(
                    "injected committed coarse failure",
                    mutationCommitted: true);
            },
            state);
        var result = new LandblockStreamResult.Loaded(
            landblockId,
            LandblockStreamTier.Near,
            BuildWithShell(landblockId),
            EmptyMesh());

        Assert.Throws<StreamingMutationException>(() => pipeline.PublishLoaded(result));

        Assert.Equal(1, presentationCalls);
        Assert.False(pipeline.HasPendingPublication(result));
        Assert.Equal(0, pipeline.PendingPublicationCount);
        Assert.False(state.IsLoaded(landblockId));
    }

    [Fact]
    public void DeferredCommittedPresentationFailure_DropsOnlyFailedResultAndPreservesTail()
    {
        const uint failedId = 0xB0B0FFFFu;
        const uint tailId = 0xB0B1FFFFu;
        var outbox = new Queue<LandblockStreamResult>(
        [
            new LandblockStreamResult.Loaded(
                failedId,
                LandblockStreamTier.Near,
                new LandblockBuild(new LoadedLandblock(
                    failedId,
                    new LandBlock(),
                    Array.Empty<WorldEntity>())),
                EmptyMesh()),
            new LandblockStreamResult.Loaded(
                tailId,
                LandblockStreamTier.Near,
                new LandblockBuild(new LoadedLandblock(
                    tailId,
                    new LandBlock(),
                    Array.Empty<WorldEntity>())),
                EmptyMesh()),
        ]);
        int failedCalls = 0;
        int tailCalls = 0;
        var state = new GpuWorldState();
        var controller = new StreamingController(
            enqueueLoad: (_, _) => { },
            enqueueUnload: _ => { },
            drainCompletions: max => Drain(outbox, max),
            applyTerrain: (build, _) =>
            {
                if (build.LandblockId == failedId)
                {
                    failedCalls++;
                    throw new StreamingMutationException(
                        "injected committed presentation failure",
                        mutationCommitted: true);
                }
                tailCalls++;
            },
            state,
            nearRadius: 0,
            farRadius: 2,
            workBudgetOptions: GenerousWorkBudget());

        Assert.Throws<StreamingMutationException>(() => controller.Tick(0xB0, 0xB0));
        Assert.Equal(1, failedCalls);
        Assert.Equal(0, tailCalls);
        Assert.Equal(1, controller.DeferredApplyBacklog);

        controller.Tick(0xB0, 0xB0);

        Assert.Equal(1, failedCalls);
        Assert.Equal(1, tailCalls);
        Assert.False(state.IsLoaded(failedId));
        Assert.True(state.IsLoaded(tailId));
        Assert.Equal(0, controller.DeferredApplyBacklog);
    }

    [Fact]
    public void RetirementFacade_DetachesFirstAndReportsPendingLedger()
    {
        const uint landblockId = 0x5050FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            landblockId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        bool failOnce = true;
        var retirements = new LandblockRetirementCoordinator(
            state,
            ticket =>
            {
                ticket.RunOnce(
                    LandblockRetirementStage.EntityLighting,
                    () =>
                    {
                        if (failOnce)
                        {
                            failOnce = false;
                            throw new InvalidOperationException("injected failure");
                        }
                    });
            },
            _ => LandblockRetirementStage.EntityLighting);
        var pipeline = new LandblockPresentationPipeline(
            static (_, _) => { },
            state,
            retirementCoordinator: retirements);

        pipeline.BeginFullRetirement(landblockId);

        Assert.False(state.IsLoaded(landblockId));
        Assert.True(pipeline.IsRetirementPending(landblockId));
        Assert.Equal(1, pipeline.PendingRetirementCount);

        pipeline.AdvanceRetirements();

        Assert.False(pipeline.IsRetirementPending(landblockId));
        Assert.Equal(0, pipeline.PendingRetirementCount);
    }

    [Fact]
    public void Pipeline_DoesNotOwnStreamingPolicyOrLiveIdentityMaps()
    {
        string[] forbiddenFragments =
        [
            "generation",
            "desiredtier",
            "streamingregion",
            "serverguid",
            "liveentity",
        ];

        string[] fields = typeof(LandblockPresentationPipeline)
            .GetFields(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)
            .Select(field => $"{field.Name}:{field.FieldType.FullName}")
            .ToArray();

        foreach (string field in fields)
        foreach (string forbidden in forbiddenFragments)
            Assert.DoesNotContain(forbidden, field, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Converge(
        StreamingOriginRecenterCoordinator recenter,
        StreamingController controller,
        int observerX,
        int observerY,
        bool insideDungeon = false,
        int maximumFrames = 64)
    {
        for (int frame = 0; frame < maximumFrames; frame++)
        {
            if (recenter.Advance())
                return true;

            controller.Tick(observerX, observerY, insideDungeon);
        }

        return recenter.Advance();
    }

    private static LandblockBuild BuildWithShell(
        uint landblockId,
        params WorldEntity[] entities)
    {
        uint cellId = (landblockId & 0xFFFF0000u) | 0x0100u;
        var shell = new EnvCellShellPlacement(
            cellId,
            0x2_0000_0001UL,
            0x0D000001u,
            1,
            ImmutableArray<ushort>.Empty,
            Vector3.Zero,
            Quaternion.Identity,
            Matrix4x4.Identity,
            new WbBoundingBox(Vector3.Zero, Vector3.One),
            new WbBoundingBox(Vector3.Zero, Vector3.One));
        var envCells = new EnvCellLandblockBuild(
            landblockId,
            Array.Empty<LoadedCell>(),
            [shell]);
        return new LandblockBuild(
            new LoadedLandblock(
                landblockId,
                new LandBlock(),
                entities,
                PhysicsDatBundle.Empty),
            envCells);
    }

    private static WorldEntity Entity(uint id, uint serverGuid = 0) => new()
    {
        Id = id,
        ServerGuid = serverGuid,
        SourceGfxObjOrSetupId = 0x01000000u + id,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };

    private static LandblockMeshData EmptyMesh() => new(
        Array.Empty<TerrainVertex>(),
        Array.Empty<uint>());

    private static StreamingWorkBudgetOptions GenerousWorkBudget() => new(
        MaxUpdateMilliseconds: 1_000,
        MaxCompletionAdmissions: 1_000,
        MaxAdoptedCpuBytes: 1L << 40,
        MaxEntityOperations: 1_000_000,
        MaxGpuUploadBytes: 1L << 40,
        MaxGlRetireOperations: 1_000_000,
        DestinationReserveFraction: 0.75f);

    private static IReadOnlyList<LandblockStreamResult> Drain(
        Queue<LandblockStreamResult> queue,
        int max)
    {
        var drained = new List<LandblockStreamResult>(max);
        while (drained.Count < max && queue.TryDequeue(out LandblockStreamResult? result))
            drained.Add(result);
        return drained;
    }

    private sealed class RecordingMeshAdapter(List<string> calls) : IWbMeshAdapter
    {
        public void IncrementRefCount(ulong id)
        {
        }

        public void PinPreparedRenderData(ulong id) => calls.Add("pin");

        public void DecrementRefCount(ulong id)
        {
        }
    }

    private sealed class FailFirstPreparedPinAdapter : IWbMeshAdapter
    {
        public int PinAttempts { get; private set; }

        public void IncrementRefCount(ulong id)
        {
        }

        public void PinPreparedRenderData(ulong id)
        {
            PinAttempts++;
            if (PinAttempts == 1)
                throw new InvalidOperationException("injected pin failure");
        }

        public void DecrementRefCount(ulong id)
        {
        }
    }
}
