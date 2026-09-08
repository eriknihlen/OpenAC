using System.Numerics;
using AcDream.App.Streaming;
using AcDream.Core.Terrain;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using Xunit;

namespace AcDream.App.Tests.Streaming;

public sealed class LandblockRetirementCoordinatorTests
{
    [Fact]
    public void PendingPhysicsStageYieldsBeforeTerrainAndRemainsRetryable()
    {
        const uint landblockId = 0x2020FFFFu;
        var ticket = new LandblockRetirementTicket(
            new GpuLandblockRetirement(
                landblockId,
                LandblockRetirementKind.Full,
                Array.Empty<WorldEntity>()),
            LandblockRetirementStage.Physics
                | LandblockRetirementStage.Terrain);
        int physicsPolls = 0;
        int terrainPolls = 0;

        Assert.Equal(
            LandblockRetirementOperationResult.Pending,
            ticket.RunOnceStep(
                LandblockRetirementStage.Physics,
                () =>
                {
                    physicsPolls++;
                    return false;
                }));
        Assert.Equal(LandblockRetirementStage.Physics, ticket.NextIncompleteStage);
        Assert.Equal(1, physicsPolls);
        Assert.Equal(0, terrainPolls);

        Assert.Equal(
            LandblockRetirementOperationResult.Progressed,
            ticket.RunOnceStep(
                LandblockRetirementStage.Physics,
                () =>
                {
                    physicsPolls++;
                    return true;
                }));
        Assert.Equal(LandblockRetirementStage.Terrain, ticket.NextIncompleteStage);
        Assert.True(ticket.RunOnce(
            LandblockRetirementStage.Terrain,
            () => terrainPolls++));
        Assert.True(ticket.IsComplete);
        Assert.Equal(2, physicsPolls);
        Assert.Equal(1, terrainPolls);
    }

    [Fact]
    public void BudgetedPendingPhysicsPollsExactlyOncePerAdvance()
    {
        const uint landblockId = 0x2023FFFFu;
        var state = StateWith(landblockId);
        int physicsPolls = 0;
        int terrainPolls = 0;
        bool acknowledgePhysics = false;
        LandblockRetirementOperationResult AdvancePresentation(
            LandblockRetirementTicket ticket) =>
            ticket.NextIncompleteStage switch
            {
                LandblockRetirementStage.Physics => ticket.RunOnceStep(
                    LandblockRetirementStage.Physics,
                    () =>
                    {
                        physicsPolls++;
                        return acknowledgePhysics;
                    }),
                LandblockRetirementStage.Terrain => ticket.RunOnceStep(
                    LandblockRetirementStage.Terrain,
                    () => terrainPolls++),
                { } stage => ticket.RunOnceStep(stage, () => { }),
            };
        LandblockRetirementCoordinator coordinator =
            LandblockRetirementCoordinator.CreateBudgeted(
                state,
                AdvancePresentation,
                ticket =>
                {
                    while (!ticket.IsComplete)
                        _ = AdvancePresentation(ticket);
                });
        coordinator.BeginFull(landblockId);

        var first = new StreamingWorkMeter(Budget(maxEntityOperations: 64));
        coordinator.Advance(first);
        first.FinishFrame();

        Assert.Equal(1, physicsPolls);
        Assert.Equal(0, terrainPolls);
        Assert.Equal(0, first.Snapshot.FailureCount);
        Assert.Equal(1, coordinator.PendingCount);

        acknowledgePhysics = true;
        var second = new StreamingWorkMeter(Budget(maxEntityOperations: 64));
        coordinator.Advance(second);
        second.FinishFrame();

        Assert.Equal(2, physicsPolls);
        Assert.Equal(1, terrainPolls);
        Assert.Equal(0, second.Snapshot.FailureCount);
        Assert.Equal(0, coordinator.PendingCount);
    }

    [Fact]
    public void EntityStageFailure_DetachesImmediately_AndResumesAtFailedEntity()
    {
        const uint landblockId = 0x2020FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            landblockId,
            new LandBlock(),
            new[] { Entity(1), Entity(2), Entity(3) }));

        var calls = new Dictionary<uint, int>();
        bool failEntityTwo = true;
        var coordinator = new LandblockRetirementCoordinator(
            state,
            ticket => CompletePresentation(
                ticket,
                lighting: entity =>
                {
                    calls[entity.Id] = calls.GetValueOrDefault(entity.Id) + 1;
                    if (entity.Id == 2 && failEntityTwo)
                    {
                        failEntityTwo = false;
                        throw new InvalidOperationException("injected owner failure");
                    }
                }));

        coordinator.BeginFull(landblockId);

        Assert.False(state.IsLoaded(landblockId));
        Assert.Equal(1, coordinator.PendingCount);
        Assert.Equal(1, calls[1]);
        Assert.Equal(1, calls[2]);
        Assert.False(calls.ContainsKey(3));

        coordinator.Advance();

        Assert.Equal(0, coordinator.PendingCount);
        Assert.Equal(1, calls[1]);
        Assert.Equal(2, calls[2]);
        Assert.Equal(1, calls[3]);
    }

    [Fact]
    public void BudgetedRetirement_AdvancesAtMostTheAdmittedAtomicEntityWork()
    {
        const uint landblockId = 0x2021FFFFu;
        WorldEntity[] entities =
        [
            Entity(1),
            Entity(2),
            Entity(3),
            Entity(4),
        ];
        var state = StateWith(landblockId, entities);
        var lightingCalls = new Dictionary<uint, int>();
        LandblockRetirementCoordinator coordinator =
            LandblockRetirementCoordinator.CreateBudgeted(
                state,
                ticket => AdvancePresentationStep(
                    ticket,
                    entity => lightingCalls[entity.Id] =
                        lightingCalls.GetValueOrDefault(entity.Id) + 1),
                ticket => CompletePresentation(
                    ticket,
                    lighting: entity => lightingCalls[entity.Id] =
                        lightingCalls.GetValueOrDefault(entity.Id) + 1));
        coordinator.BeginFull(landblockId);

        Assert.False(state.IsLoaded(landblockId));
        Assert.Equal(1, coordinator.PendingCount);

        int frames = 0;
        while (coordinator.PendingCount != 0 && frames++ < 64)
        {
            var meter = new StreamingWorkMeter(Budget(maxEntityOperations: 2));
            coordinator.Advance(meter);
            meter.FinishFrame();
            Assert.InRange(meter.Snapshot.Used.EntityOperations, 0, 2);
        }

        Assert.InRange(frames, 2, 64);
        Assert.Equal(0, coordinator.PendingCount);
        Assert.Equal(
            entities.Select(entity => entity.Id).Order(),
            lightingCalls.Keys.Order());
        Assert.All(lightingCalls.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void BudgetedRetirement_FailedEntityRetriesWithoutReplayingCommittedPrefix()
    {
        const uint landblockId = 0x2022FFFFu;
        var state = StateWith(
            landblockId,
            Entity(1),
            Entity(2),
            Entity(3));
        var calls = new Dictionary<uint, int>();
        bool failSecond = true;
        LandblockRetirementCoordinator coordinator =
            LandblockRetirementCoordinator.CreateBudgeted(
                state,
                ticket => AdvancePresentationStep(
                    ticket,
                    entity =>
                    {
                        calls[entity.Id] = calls.GetValueOrDefault(entity.Id) + 1;
                        if (entity.Id == 2 && failSecond)
                        {
                            failSecond = false;
                            throw new InvalidOperationException(
                                "injected metered entity failure");
                        }
                    }),
                ticket => CompletePresentation(ticket));
        coordinator.BeginFull(landblockId);

        var firstMeter = new StreamingWorkMeter(Budget(maxEntityOperations: 64));
        coordinator.Advance(firstMeter);
        firstMeter.FinishFrame();

        Assert.Equal(1, coordinator.PendingCount);
        Assert.Equal(1, calls[1]);
        Assert.Equal(1, calls[2]);
        Assert.False(calls.ContainsKey(3));
        Assert.Equal(1, firstMeter.Snapshot.FailureCount);

        var retryMeter = new StreamingWorkMeter(Budget(maxEntityOperations: 64));
        coordinator.Advance(retryMeter);
        retryMeter.FinishFrame();

        Assert.Equal(0, coordinator.PendingCount);
        Assert.Equal(1, calls[1]);
        Assert.Equal(2, calls[2]);
        Assert.Equal(1, calls[3]);
    }

    [Fact]
    public void ForceReload_RetiresEveryLoadedId_WhenOneOwnerRemainsPending()
    {
        uint[] ids = [0x2020FFFFu, 0x2121FFFFu, 0x2222FFFFu];
        var state = new GpuWorldState();
        foreach (uint id in ids)
            state.AddLandblock(new LoadedLandblock(id, new LandBlock(), Array.Empty<WorldEntity>()));

        bool failRetirement = true;
        var coordinator = new LandblockRetirementCoordinator(
            state,
            ticket => CompletePresentation(
                ticket,
                terrain: () =>
                {
                    if (ticket.LandblockId == ids[1] && failRetirement)
                    {
                        throw new InvalidOperationException("injected terrain failure");
                    }
                }));
        var controller = Controller(state, coordinator);

        controller.ForceReloadWindow();
        for (int frame = 0;
             frame < 16 && ids.Any(state.IsLoaded);
             frame++)
        {
            controller.Tick(0x20, 0x20);
        }

        foreach (uint id in ids)
            Assert.False(state.IsLoaded(id));
        Assert.Equal(1, coordinator.PendingCount);

        failRetirement = false;
        coordinator.Advance();
        Assert.Equal(0, coordinator.PendingCount);
    }

    [Fact]
    public void ForceReload_CapturesAndDetachesThroughTheSharedEntityBudget()
    {
        uint[] ids =
        [
            0x2323FFFFu,
            0x2424FFFFu,
            0x2525FFFFu,
        ];
        var state = new GpuWorldState();
        foreach (uint id in ids)
        {
            state.AddLandblock(new LoadedLandblock(
                id,
                new LandBlock(),
                Array.Empty<WorldEntity>()));
        }

        var coordinator = new LandblockRetirementCoordinator(
            state,
            ticket => CompletePresentation(ticket));
        var controller = Controller(
            state,
            coordinator,
            workBudgetOptions: new StreamingWorkBudgetOptions(
                MaxUpdateMilliseconds: 1000,
                MaxCompletionAdmissions: 64,
                MaxAdoptedCpuBytes: 64 * StreamingWorkBudgetOptions.MiB,
                MaxEntityOperations: 1,
                MaxGpuUploadBytes: 64 * StreamingWorkBudgetOptions.MiB,
                MaxGlRetireOperations: 64,
                DestinationReserveFraction: 0.75f));
        controller.ForceReloadWindow();

        int priorLoaded = ids.Length;
        for (int frame = 0; frame < 32 && priorLoaded != 0; frame++)
        {
            controller.Tick(0x23, 0x23);
            int loaded = ids.Count(state.IsLoaded);
            Assert.InRange(priorLoaded - loaded, 0, 1);
            Assert.InRange(
                controller.WorkDiagnostics.LastFrame.Used.EntityOperations,
                0,
                1);
            priorLoaded = loaded;
        }

        Assert.Equal(0, priorLoaded);
        Assert.Equal(ids.Length, controller.LastFullWindowRetirementLandblockCount);
    }

    [Fact]
    public void ReplacementPublication_WaitsForSameIdRetirementFence()
    {
        const uint landblockId = 0x3232FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            landblockId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));

        int terrainAttempts = 0;
        var coordinator = new LandblockRetirementCoordinator(
            state,
            ticket => CompletePresentation(
                ticket,
                terrain: () =>
                {
                    terrainAttempts++;
                    if (terrainAttempts <= 2)
                        throw new InvalidOperationException("injected terrain failure");
                }));

        var pending = new Queue<LandblockStreamResult>();
        int applyCount = 0;
        var controller = Controller(
            state,
            coordinator,
            drain: max => Drain(pending, max),
            apply: (_, _) => applyCount++);
        controller.ForceReloadWindow(); // generation 1, first retirement attempt

        var replacement = new LoadedLandblock(
            landblockId,
            new LandBlock(),
            Array.Empty<WorldEntity>());
        pending.Enqueue(new LandblockStreamResult.Loaded(
            landblockId,
            LandblockStreamTier.Near,
            replacement,
            EmptyMesh(),
            generation: 1));

        controller.Tick(0x32, 0x32); // first failure; publication stays at worker source
        Assert.Equal(0, applyCount);
        Assert.False(state.IsLoaded(landblockId));
        Assert.True(coordinator.IsPending(landblockId));

        controller.Tick(0x32, 0x32); // second failure
        Assert.Equal(0, applyCount);
        Assert.True(coordinator.IsPending(landblockId));

        controller.Tick(0x32, 0x32);
        Assert.Equal(0, applyCount);
        controller.Tick(0x32, 0x32); // replacement publishes on the next frame
        Assert.Equal(1, applyCount);
        Assert.True(state.IsLoaded(landblockId));
        Assert.False(coordinator.IsPending(landblockId));
    }

    [Fact]
    public void ReentrantSameIdBegin_DoesNotReplaySuccessfulStage()
    {
        const uint landblockId = 0x4040FFFFu;
        var state = StateWith(landblockId, Entity(1));
        int lightingCalls = 0;
        LandblockRetirementCoordinator? coordinator = null;
        coordinator = new LandblockRetirementCoordinator(
            state,
            ticket => CompletePresentation(
                ticket,
                lighting: _ =>
                {
                    lightingCalls++;
                    coordinator!.BeginFull(landblockId);
                }));

        coordinator.BeginFull(landblockId);

        Assert.Equal(1, lightingCalls);
        Assert.Equal(0, coordinator.PendingCount);
        Assert.False(coordinator.IsPending(landblockId));
    }

    [Fact]
    public void ReentrantAdvance_RetriesFailedStageWithoutRecursion()
    {
        const uint landblockId = 0x4141FFFFu;
        var state = StateWith(landblockId, Entity(1));
        int lightingCalls = 0;
        LandblockRetirementCoordinator? coordinator = null;
        coordinator = new LandblockRetirementCoordinator(
            state,
            ticket => CompletePresentation(
                ticket,
                lighting: _ =>
                {
                    lightingCalls++;
                    coordinator!.Advance();
                    if (lightingCalls == 1)
                        throw new InvalidOperationException("injected first failure");
                }));

        coordinator.BeginFull(landblockId);

        Assert.Equal(2, lightingCalls);
        Assert.Equal(0, coordinator.PendingCount);
        Assert.False(coordinator.IsPending(landblockId));
    }

    [Fact]
    public void ReentrantDifferentIdBegin_IsDeferredUntilActiveAdvanceCompletes()
    {
        const uint firstId = 0x4242FFFFu;
        const uint secondId = 0x4343FFFFu;
        var state = StateWith(firstId);
        state.AddLandblock(new LoadedLandblock(
            secondId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        int firstAttempts = 0;
        int secondAttempts = 0;
        LandblockRetirementCoordinator? coordinator = null;
        coordinator = new LandblockRetirementCoordinator(
            state,
            ticket => CompletePresentation(
                ticket,
                terrain: () =>
                {
                    if (ticket.LandblockId == firstId)
                    {
                        firstAttempts++;
                        if (firstAttempts == 1)
                            throw new InvalidOperationException("leave first pending");
                        coordinator!.BeginFull(secondId);
                    }
                    else
                    {
                        secondAttempts++;
                    }
                }));
        coordinator.BeginFull(firstId);
        Assert.Equal(1, coordinator.PendingCount);

        coordinator.Advance();

        Assert.Equal(2, firstAttempts);
        Assert.Equal(1, secondAttempts);
        Assert.False(state.IsLoaded(secondId));
        Assert.Equal(0, coordinator.PendingCount);
    }

    [Fact]
    public void RepeatedBegin_RemovesTicketWhenRetryCompletes()
    {
        const uint landblockId = 0x4444FFFFu;
        var state = StateWith(landblockId);
        int terrainAttempts = 0;
        int requiredStageCalls = 0;
        var coordinator = new LandblockRetirementCoordinator(
            state,
            ticket => CompletePresentation(
                ticket,
                terrain: () =>
                {
                    terrainAttempts++;
                    if (terrainAttempts == 1)
                        throw new InvalidOperationException("injected first failure");
                }),
            _ =>
            {
                requiredStageCalls++;
                return LandblockRetirementCoordinator.ProductionPresentationStages;
            });

        coordinator.BeginFull(landblockId);
        Assert.Equal(1, coordinator.PendingCount);

        coordinator.BeginFull(landblockId);

        Assert.Equal(2, terrainAttempts);
        Assert.Equal(1, requiredStageCalls);
        Assert.Equal(0, coordinator.PendingCount);
        Assert.False(coordinator.IsPending(landblockId));
    }

    [Fact]
    public void VisibilityObserverFailure_CompletesReceiptThenRethrowsCommittedEdge()
    {
        const uint landblockId = 0x4545FFFFu;
        WorldEntity live = Entity(1, serverGuid: 0x70000001u);
        var state = StateWith(landblockId, live);
        state.LiveProjectionVisibilityChanged += (_, _) =>
            throw new InvalidOperationException("injected visibility failure");
        int terrainCalls = 0;
        var coordinator = new LandblockRetirementCoordinator(
            state,
            ticket => CompletePresentation(
                ticket,
                terrain: () => terrainCalls++));

        Assert.Throws<AggregateException>(() =>
            coordinator.BeginFull(landblockId));

        Assert.False(state.IsLoaded(landblockId));
        Assert.Equal(1, terrainCalls);
        Assert.Equal(0, coordinator.PendingCount);
        Assert.False(coordinator.IsPending(landblockId));
    }

    [Fact]
    public void RequiredStageFailure_HappensBeforeSpatialDetachment()
    {
        const uint landblockId = 0x4646FFFFu;
        var state = StateWith(landblockId);
        var coordinator = new LandblockRetirementCoordinator(
            state,
            static _ => { },
            static _ => throw new InvalidOperationException(
                "injected required-stage failure"));

        Assert.Throws<InvalidOperationException>(() =>
            coordinator.BeginFull(landblockId));

        Assert.True(state.IsLoaded(landblockId));
        Assert.Equal(0, coordinator.PendingCount);
    }

    [Fact]
    public void OriginRecenterAdoption_DuplicateReceiptFailsBeforeLedgerMutation()
    {
        const uint landblockId = 0x4647FFFFu;
        var state = new GpuWorldState();
        int detachedCallbacks = 0;
        LandblockRetirementCoordinator coordinator =
            LandblockRetirementCoordinator.CreateBudgeted(
                state,
                ticket => AdvancePresentationStep(ticket),
                ticket => CompletePresentation(ticket),
                _ => detachedCallbacks++);
        GpuLandblockRetirement[] receipts =
        [
            new(
                landblockId,
                LandblockRetirementKind.Full,
                Array.Empty<WorldEntity>()),
            new(
                landblockId & 0xFFFF0000u,
                LandblockRetirementKind.Full,
                Array.Empty<WorldEntity>()),
        ];

        Assert.Throws<ArgumentException>(() =>
            coordinator.AdoptDetachedFull(receipts));

        Assert.Equal(0, coordinator.PendingCount);
        Assert.False(coordinator.IsPending(landblockId));
        Assert.Equal(0, detachedCallbacks);
    }

    [Fact]
    public void OriginRecenterAdoption_PendingOnlyLiveProjectionDoesNotCreateSecondFullReceipt()
    {
        const uint landblockId = 0x4648FFFFu;
        WorldEntity live = Entity(1, serverGuid: 0x70000003u);
        GpuWorldState state = StateWith(landblockId, live);
        LandblockRetirementCoordinator coordinator =
            LandblockRetirementCoordinator.CreateBudgeted(
                state,
                ticket => AdvancePresentationStep(ticket),
                ticket => CompletePresentation(ticket));

        coordinator.BeginFull(landblockId);
        Assert.Equal(1, coordinator.PendingCount);
        Assert.False(state.IsLoaded(landblockId));

        GpuWorldRecenterRetirement recenter =
            state.DetachAllForOriginRecenter();

        Assert.Empty(recenter.Landblocks);
        Assert.Null(coordinator.AdoptDetachedFull(recenter.Landblocks));
        Assert.Equal(1, coordinator.PendingCount);
        Assert.True(coordinator.IsPending(landblockId));
    }

    [Fact]
    public void OriginRecenterAdoption_ObserverFailureRetainsEveryCleanupReceipt()
    {
        uint[] landblockIds = [0x4648FFFFu, 0x4649FFFFu];
        var state = new GpuWorldState();
        int detachedCallbacks = 0;
        LandblockRetirementCoordinator coordinator =
            LandblockRetirementCoordinator.CreateBudgeted(
                state,
                ticket => AdvancePresentationStep(ticket),
                ticket => CompletePresentation(ticket),
                retirement =>
                {
                    detachedCallbacks++;
                    if (retirement.LandblockId == landblockIds[0])
                    {
                        throw new InvalidOperationException(
                            "injected detached observer failure");
                    }
                });
        GpuLandblockRetirement[] receipts = landblockIds
            .Select(id => new GpuLandblockRetirement(
                id,
                LandblockRetirementKind.Full,
                Array.Empty<WorldEntity>()))
            .ToArray();

        Exception? failure = coordinator.AdoptDetachedFull(receipts);

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(2, detachedCallbacks);
        Assert.Equal(2, coordinator.PendingCount);
        Assert.All(landblockIds, id => Assert.True(coordinator.IsPending(id)));

        int frames = 0;
        while (coordinator.PendingCount != 0 && frames++ < 64)
        {
            var meter = new StreamingWorkMeter(Budget(maxEntityOperations: 64));
            coordinator.Advance(meter);
            meter.FinishFrame();
        }

        Assert.Equal(0, coordinator.PendingCount);
        Assert.All(landblockIds, id => Assert.False(coordinator.IsPending(id)));
    }

    [Fact]
    public void DestinationDependency_CompletesOutOfOrder_WithoutReorderingCleanupFifo()
    {
        uint[] landblockIds =
        [
            0x5151FFFFu,
            0x5252FFFFu,
            0x5353FFFFu,
        ];
        uint destinationId = landblockIds[2];
        var terrainOrder = new List<uint>();
        var state = new GpuWorldState();
        LandblockRetirementCoordinator coordinator =
            LandblockRetirementCoordinator.CreateBudgeted(
                state,
                ticket =>
                {
                    if (ticket.NextIncompleteStage
                        == LandblockRetirementStage.Terrain)
                    {
                        return ticket.RunOnceStep(
                            LandblockRetirementStage.Terrain,
                            () => terrainOrder.Add(ticket.LandblockId));
                    }

                    return AdvancePresentationStep(ticket);
                },
                ticket => CompletePresentation(
                    ticket,
                    terrain: () => terrainOrder.Add(ticket.LandblockId)));
        GpuLandblockRetirement[] receipts = landblockIds
            .Select(id => new GpuLandblockRetirement(
                id,
                LandblockRetirementKind.Full,
                Array.Empty<WorldEntity>()))
            .ToArray();
        Assert.Null(coordinator.AdoptDetachedFull(receipts));

        var destinationMeter = new StreamingWorkMeter(
            Budget(maxEntityOperations: 64),
            destinationReservationActive: true);
        using (destinationMeter.EnterLane(StreamingWorkLane.Destination))
            coordinator.AdvancePriority(destinationId, destinationMeter);
        destinationMeter.FinishFrame();

        Assert.False(coordinator.IsPending(destinationId));
        Assert.True(coordinator.IsPending(landblockIds[0]));
        Assert.True(coordinator.IsPending(landblockIds[1]));
        Assert.Equal(2, coordinator.PendingCount);
        Assert.Equal([destinationId], terrainOrder);

        var cleanupMeter = new StreamingWorkMeter(
            Budget(maxEntityOperations: 64));
        coordinator.Advance(cleanupMeter);
        cleanupMeter.FinishFrame();

        Assert.Equal(0, coordinator.PendingCount);
        Assert.Equal(
            [destinationId, landblockIds[0], landblockIds[1]],
            terrainOrder);
    }

    [Fact]
    public void ObserverFailure_DrainsReentrantDifferentIdBeginBeforeRethrow()
    {
        const uint firstId = 0x4747FFFFu;
        const uint secondId = 0x4848FFFFu;
        WorldEntity live = Entity(1, serverGuid: 0x70000002u);
        var state = StateWith(firstId, live);
        state.AddLandblock(new LoadedLandblock(
            secondId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        LandblockRetirementCoordinator? coordinator = null;
        coordinator = new LandblockRetirementCoordinator(
            state,
            ticket => CompletePresentation(ticket));
        state.LiveProjectionVisibilityChanged += (_, _) =>
        {
            coordinator.BeginFull(secondId);
            throw new InvalidOperationException("injected visibility failure");
        };

        Assert.Throws<AggregateException>(() =>
            coordinator.BeginFull(firstId));

        Assert.False(state.IsLoaded(firstId));
        Assert.False(state.IsLoaded(secondId));
        Assert.Equal(0, coordinator.PendingCount);
    }

    private static StreamingController Controller(
        GpuWorldState state,
        LandblockRetirementCoordinator coordinator,
        Func<int, IReadOnlyList<LandblockStreamResult>>? drain = null,
        Action<LandblockBuild, LandblockMeshData>? apply = null,
        StreamingWorkBudgetOptions? workBudgetOptions = null) =>
        new(
            enqueueLoad: (_, _, _) => { },
            enqueueUnload: (_, _) => { },
            drainCompletions: drain ?? (_ => Array.Empty<LandblockStreamResult>()),
            applyTerrain: apply ?? ((_, _) => { }),
            state: state,
            nearRadius: 0,
            farRadius: 0,
            retirementCoordinator: coordinator,
            workBudgetOptions: workBudgetOptions ?? GenerousWorkBudget());

    private static StreamingWorkBudgetOptions GenerousWorkBudget() => new(
        MaxUpdateMilliseconds: 100,
        MaxCompletionAdmissions: 64,
        MaxAdoptedCpuBytes: 64 * StreamingWorkBudgetOptions.MiB,
        MaxEntityOperations: 1_024,
        MaxGpuUploadBytes: 64 * StreamingWorkBudgetOptions.MiB,
        MaxGlRetireOperations: 256,
        DestinationReserveFraction: 0.75f);

    private static IReadOnlyList<LandblockStreamResult> Drain(
        Queue<LandblockStreamResult> pending,
        int max)
    {
        var result = new List<LandblockStreamResult>(max);
        while (result.Count < max && pending.TryDequeue(out LandblockStreamResult? item))
            result.Add(item);
        return result;
    }

    private static LandblockRetirementOperationResult AdvancePresentationStep(
        LandblockRetirementTicket ticket,
        Action<WorldEntity>? lighting = null)
    {
        return ticket.NextIncompleteStage switch
        {
            LandblockRetirementStage.EntityLighting =>
                ticket.RunEntityStep(
                    LandblockRetirementStage.EntityLighting,
                    static _ => true,
                    lighting ?? (_ => { })),
            LandblockRetirementStage.EntityTranslucency =>
                ticket.RunEntityStep(
                    LandblockRetirementStage.EntityTranslucency,
                    static _ => true,
                    static _ => { }),
            LandblockRetirementStage.PluginProjection =>
                ticket.RunEntityStep(
                    LandblockRetirementStage.PluginProjection,
                    static entity => entity.ServerGuid == 0,
                    static _ => { }),
            LandblockRetirementStage.Terrain =>
                ticket.RunOnceStep(
                    LandblockRetirementStage.Terrain,
                    static () => { }),
            LandblockRetirementStage.Physics =>
                ticket.RunOnceStep(
                    LandblockRetirementStage.Physics,
                    static () => { }),
            LandblockRetirementStage.CellVisibility =>
                ticket.RunOnceStep(
                    LandblockRetirementStage.CellVisibility,
                    static () => { }),
            LandblockRetirementStage.BuildingRegistry =>
                ticket.RunOnceStep(
                    LandblockRetirementStage.BuildingRegistry,
                    static () => { }),
            LandblockRetirementStage.EnvironmentCells =>
                ticket.RunOnceStep(
                    LandblockRetirementStage.EnvironmentCells,
                    static () => { }),
            _ => LandblockRetirementOperationResult.NoWork,
        };
    }

    private static StreamingWorkBudget Budget(int maxEntityOperations) => new(
        maxUpdateTime: TimeSpan.FromSeconds(1),
        maxCompletionAdmissions: 64,
        maxAdoptedCpuBytes: 64 * 1024 * 1024,
        maxEntityOperations,
        maxGpuUploadBytes: 64 * 1024 * 1024,
        maxGlRetireOperations: 64,
        destinationReserveFraction: 0.75f);

    private static void CompletePresentation(
        LandblockRetirementTicket ticket,
        Action<WorldEntity>? lighting = null,
        Action? terrain = null)
    {
        ticket.RunForEachEntity(
            LandblockRetirementStage.EntityLighting,
            static _ => true,
            lighting ?? (_ => { }));
        ticket.RunForEachEntity(
            LandblockRetirementStage.EntityTranslucency,
            static _ => true,
            static _ => { });
        ticket.RunForEachEntity(
            LandblockRetirementStage.PluginProjection,
            static entity => entity.ServerGuid == 0,
            static _ => { });
        if (ticket.Kind == LandblockRetirementKind.Full)
            ticket.RunOnce(LandblockRetirementStage.Terrain, terrain ?? (() => { }));
        ticket.RunOnce(LandblockRetirementStage.Physics, static () => { });
        ticket.RunOnce(LandblockRetirementStage.CellVisibility, static () => { });
        ticket.RunOnce(LandblockRetirementStage.BuildingRegistry, static () => { });
        ticket.RunOnce(LandblockRetirementStage.EnvironmentCells, static () => { });
    }

    private static WorldEntity Entity(uint id, uint serverGuid = 0u) => new()
    {
        Id = id,
        ServerGuid = serverGuid,
        SourceGfxObjOrSetupId = 0x01000000u + id,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };

    private static GpuWorldState StateWith(
        uint landblockId,
        params WorldEntity[] entities)
    {
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            landblockId,
            new LandBlock(),
            entities));
        return state;
    }

    private static LandblockMeshData EmptyMesh() => new(
        Array.Empty<TerrainVertex>(),
        Array.Empty<uint>());
}
