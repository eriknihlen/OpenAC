using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.App.Streaming;
using AcDream.Core.Terrain;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using Xunit;

namespace AcDream.Core.Tests.Streaming;

public class StreamingControllerPriorityApplyTests
{
    private const double UntimedFrameMilliseconds = 3_600_000d;

    private static StreamingWorkBudgetOptions UntimedBudget() =>
        StreamingWorkBudgetOptions.Default with
        {
            MaxUpdateMilliseconds = UntimedFrameMilliseconds,
        };

    private static LandblockStreamResult.Loaded LoadedOf(uint canonicalId, ulong generation = 0)
        => new(canonicalId, LandblockStreamTier.Near,
               new LoadedLandblock(canonicalId, new LandBlock(), Array.Empty<WorldEntity>()),
               new LandblockMeshData(Array.Empty<TerrainVertex>(), Array.Empty<uint>()),
               generation);

    [Fact]
    public void DestinationReservation_WinsExecutionOrderAfterBoundedAdmission()
    {
        uint priority = StreamingRegion.EncodeLandblockIdForTest(169, 180);
        var outbox = new Queue<LandblockStreamResult>(new LandblockStreamResult[]
        {
            LoadedOf(StreamingRegion.EncodeLandblockIdForTest(169, 181)),
            LoadedOf(StreamingRegion.EncodeLandblockIdForTest(169, 182)),
            LoadedOf(StreamingRegion.EncodeLandblockIdForTest(169, 183)),
            LoadedOf(StreamingRegion.EncodeLandblockIdForTest(169, 184)),
            LoadedOf(priority),
        });

        var applied = new List<uint>();
        var state = new GpuWorldState();
        var ctrl = new StreamingController(
            enqueueLoad: (_, _) => { },
            enqueueUnload: _ => { },
            drainCompletions: max =>
            {
                var batch = new List<LandblockStreamResult>();
                while (batch.Count < max && outbox.Count > 0) batch.Add(outbox.Dequeue());
                return batch;
            },
            applyTerrain: (lb, _) => applied.Add(lb.LandblockId),
            state: state,
            nearRadius: 4,
            farRadius: 12,
            workBudgetOptions: GenerousBudget())
        { MaxCompletionsPerFrame = 4 };

        ctrl.BeginDestinationReservation(1, priority, 0);
        ctrl.Tick(169, 180);

        Assert.NotEmpty(applied);
        Assert.Equal(priority, applied[0]);
        Assert.True(applied.Count <= 5);
    }

    [Fact]
    public void Deferred_nonPriority_completions_applyOnLaterFrames_withoutLoss()
    {
        // After the priority is found, the non-priority items drained past it must
        // still be applied (over subsequent ticks), never dropped.
        uint priority = StreamingRegion.EncodeLandblockIdForTest(169, 180);
        var outbox = new Queue<LandblockStreamResult>(new LandblockStreamResult[]
        {
            LoadedOf(StreamingRegion.EncodeLandblockIdForTest(169, 181)),
            LoadedOf(StreamingRegion.EncodeLandblockIdForTest(169, 182)),
            LoadedOf(StreamingRegion.EncodeLandblockIdForTest(169, 183)),
            LoadedOf(StreamingRegion.EncodeLandblockIdForTest(169, 184)),
            LoadedOf(StreamingRegion.EncodeLandblockIdForTest(169, 185)),
            LoadedOf(priority),
        });
        var applied = new List<uint>();
        var state = new GpuWorldState();
        var ctrl = new StreamingController(
            enqueueLoad:       (_, _) => { },
            enqueueUnload:     _ => { },
            drainCompletions:  max =>
            {
                var batch = new List<LandblockStreamResult>();
                while (batch.Count < max && outbox.Count > 0) batch.Add(outbox.Dequeue());
                return batch;
            },
            applyTerrain:      (lb, _) => applied.Add(lb.LandblockId),
            state:             state,
            nearRadius:        4,
            farRadius:         12,
            workBudgetOptions: UntimedBudget())
        { MaxCompletionsPerFrame = 4 };
        ctrl.BeginDestinationReservation(1, priority, 0);

        ctrl.Tick(169, 180);  // priority + some others
        ctrl.EndDestinationReservation(1);
        ctrl.Tick(169, 180);  // drains the deferred remainder
        ctrl.Tick(169, 180);

        Assert.Contains(priority, applied);
        Assert.Equal(6, applied.Count);  // all six applied, none lost
    }

    [Fact]
    public void PriorityNeverArrives_noThrow_noLoss_noDoubleApply()
    {
        uint priority  = StreamingRegion.EncodeLandblockIdForTest(169, 180);
        uint otherId0  = StreamingRegion.EncodeLandblockIdForTest(169, 181);
        uint otherId1  = StreamingRegion.EncodeLandblockIdForTest(169, 182);
        uint otherId2  = StreamingRegion.EncodeLandblockIdForTest(169, 183);

        var outbox = new Queue<LandblockStreamResult>(new LandblockStreamResult[]
        {
            LoadedOf(otherId0),
            LoadedOf(otherId1),
            LoadedOf(otherId2),
        });

        var applied = new List<uint>();
        var state = new GpuWorldState();
        var ctrl = new StreamingController(
            enqueueLoad:      (_, _) => { },
            enqueueUnload:    _ => { },
            drainCompletions: max =>
            {
                var batch = new List<LandblockStreamResult>();
                while (batch.Count < max && outbox.Count > 0) batch.Add(outbox.Dequeue());
                return batch;
            },
            applyTerrain:     (lb, _) => applied.Add(lb.LandblockId),
            state:            state,
            nearRadius:       4,
            farRadius:        12,
            workBudgetOptions: UntimedBudget())
        { MaxCompletionsPerFrame = 4 };

        // Set a priority id that will NEVER appear in the outbox.
        ctrl.BeginDestinationReservation(1, priority, 0);

        ctrl.Tick(169, 180);
        ctrl.Tick(169, 180);
        ctrl.Tick(169, 180);

        Assert.Contains(otherId0, applied);
        Assert.Contains(otherId1, applied);
        Assert.Contains(otherId2, applied);
        Assert.Equal(3, applied.Count);
    }

    private static StreamingWorkBudgetOptions GenerousBudget() => new(
        MaxUpdateMilliseconds: UntimedFrameMilliseconds,
        MaxCompletionAdmissions: 64,
        MaxAdoptedCpuBytes: 16 * StreamingWorkBudgetOptions.MiB,
        MaxEntityOperations: 10_000,
        MaxGpuUploadBytes: 16 * StreamingWorkBudgetOptions.MiB,
        MaxGlRetireOperations: 10_000,
        DestinationReserveFraction: 0.75f);

    [Fact]
    public void DestinationReservation_StaleGenerationCannotClearReplacement()
    {
        var controller = new StreamingController(
            enqueueLoad: (_, _) => { },
            enqueueUnload: _ => { },
            drainCompletions: _ => Array.Empty<LandblockStreamResult>(),
            applyTerrain: (_, _) => { },
            state: new GpuWorldState(),
            nearRadius: 1,
            farRadius: 2,
            workBudgetOptions: UntimedBudget());

        controller.BeginDestinationReservation(1, 0x11340021u, 1);
        controller.BeginDestinationReservation(2, 0x20210123u, 0);
        controller.EndDestinationReservation(1);

        Assert.Equal(2, controller.ActiveRevealGeneration);
        Assert.Equal(0x2021FFFFu, controller.DestinationLandblockId);
        Assert.Equal(0, controller.DestinationRadius);

        controller.EndDestinationReservation(2);
        Assert.Equal(0, controller.ActiveRevealGeneration);
    }

    [Fact]
    public void DeferredCompaction_ApplyFailureRetainsExactResultWithoutReplayingCommittedPrefix()
    {
        uint priority = StreamingRegion.EncodeLandblockIdForTest(169, 180);
        uint first = StreamingRegion.EncodeLandblockIdForTest(169, 182);
        uint retry = StreamingRegion.EncodeLandblockIdForTest(169, 183);
        uint last = StreamingRegion.EncodeLandblockIdForTest(169, 184);
        var outbox = new Queue<LandblockStreamResult>(
        [
            LoadedOf(first),
            LoadedOf(retry),
            LoadedOf(last),
        ]);
        var applied = new List<uint>();
        bool failRetryOnce = true;
        var ctrl = new StreamingController(
            enqueueLoad: (_, _) => { },
            enqueueUnload: _ => { },
            drainCompletions: max =>
            {
                var batch = new List<LandblockStreamResult>();
                while (batch.Count < max && outbox.Count > 0)
                    batch.Add(outbox.Dequeue());
                return batch;
            },
            applyTerrain: (build, _) =>
            {
                if (build.LandblockId == retry && failRetryOnce)
                {
                    failRetryOnce = false;
                    throw new InvalidOperationException("synthetic upload failure");
                }
                applied.Add(build.LandblockId);
            },
            state: new GpuWorldState(),
            nearRadius: 4,
            farRadius: 12,
            workBudgetOptions: UntimedBudget())
        {
            MaxCompletionsPerFrame = 3,
        };
        ctrl.BeginDestinationReservation(1, priority, 0);

        Assert.Throws<InvalidOperationException>(() => ctrl.Tick(169, 180));
        Assert.Equal([first], applied);
        Assert.Equal(2, ctrl.DeferredApplyBacklog);

        ctrl.Tick(169, 180);

        Assert.Equal([first, retry, last], applied);
        Assert.Equal(0, ctrl.DeferredApplyBacklog);
    }

    [Fact]
    public void ForceReloadWindow_DiscardsBufferedCompletionsFromOldWindow()
    {
        var outbox = new Queue<LandblockStreamResult>(
            Enumerable.Range(0, 6)
                .Select(i => (LandblockStreamResult)LoadedOf(
                    StreamingRegion.EncodeLandblockIdForTest(169, 181 + i))));
        var applied = new List<uint>();
        var state = new GpuWorldState();
        var ctrl = new StreamingController(
            enqueueLoad: (_, _) => { },
            enqueueUnload: _ => { },
            drainCompletions: max =>
            {
                var batch = new List<LandblockStreamResult>();
                while (batch.Count < max && outbox.Count > 0) batch.Add(outbox.Dequeue());
                return batch;
            },
            applyTerrain: (build, _) => applied.Add(build.LandblockId),
            state: state,
            nearRadius: 4,
            farRadius: 12,
            workBudgetOptions: new StreamingWorkBudgetOptions(
                MaxUpdateMilliseconds: UntimedFrameMilliseconds,
                MaxCompletionAdmissions: 4,
                MaxAdoptedCpuBytes: 1_000_000,
                MaxEntityOperations: 1_000,
                MaxGpuUploadBytes: 1_000_000,
                MaxGlRetireOperations: 1_000,
                DestinationReserveFraction: 0.75f))
        { MaxCompletionsPerFrame = 4 };
        ctrl.Tick(169, 180);
        Assert.Equal(4, applied.Count);
        applied.Clear();

        ctrl.ForceReloadWindow();
        ctrl.Tick(169, 180);

        Assert.Empty(applied);
    }

    [Fact]
    public void ForceReloadWindow_RejectsLateInFlightCompletionFromOldRegion()
    {
        uint oldId = StreamingRegion.EncodeLandblockIdForTest(10, 10);
        var outbox = new Queue<LandblockStreamResult>();
        var applied = new List<uint>();
        var state = new GpuWorldState();
        var ctrl = new StreamingController(
            enqueueLoad: (_, _) => { },
            enqueueUnload: _ => { },
            drainCompletions: max =>
            {
                var batch = new List<LandblockStreamResult>();
                while (batch.Count < max && outbox.Count > 0) batch.Add(outbox.Dequeue());
                return batch;
            },
            applyTerrain: (build, _) => applied.Add(build.LandblockId),
            state: state,
            nearRadius: 1,
            farRadius: 2,
            workBudgetOptions: UntimedBudget());

        ctrl.Tick(10, 10);
        ctrl.ForceReloadWindow();
        outbox.Enqueue(LoadedOf(oldId));

        ctrl.Tick(100, 100);

        Assert.Empty(applied);
        Assert.False(state.IsLoaded(oldId));
    }

    [Fact]
    public void DungeonCollapseBeforePromotionBase_RetiresProvisionalTerrainAndPendingStatics()
    {
        uint outdoorId = StreamingRegion.EncodeLandblockIdForTest(10, 10);
        uint dungeonId = StreamingRegion.EncodeLandblockIdForTest(20, 20);
        var staticEntity = new WorldEntity
        {
            Id = 77,
            ServerGuid = 0,
            SourceGfxObjOrSetupId = 0x01000077u,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = [new MeshRef(0x01000077u, System.Numerics.Matrix4x4.Identity)],
        };
        var mesh = new LandblockMeshData(Array.Empty<TerrainVertex>(), Array.Empty<uint>());
        var promotedBuild = new LandblockBuild(
            new LoadedLandblock(outdoorId, new LandBlock(), new[] { staticEntity }));
        var outbox = new Queue<LandblockStreamResult>();
        outbox.Enqueue(new LandblockStreamResult.Promoted(
            outdoorId,
            promotedBuild,
            mesh,
            Generation: 0));
        var terrainApplied = new List<uint>();
        var terrainRemoved = new List<uint>();
        var state = new GpuWorldState();
        var unloads = new List<(uint Id, ulong Generation)>();
        var ctrl = new StreamingController(
            enqueueLoad: (_, _, _) => { },
            enqueueUnload: (id, generation) => unloads.Add((id, generation)),
            drainCompletions: max =>
            {
                var batch = new List<LandblockStreamResult>();
                while (batch.Count < max && outbox.Count > 0) batch.Add(outbox.Dequeue());
                return batch;
            },
            applyTerrain: (build, _) => terrainApplied.Add(build.LandblockId),
            state: state,
            nearRadius: 0,
            farRadius: 2,
            removeTerrain: id => terrainRemoved.Add(id),
            workBudgetOptions: UntimedBudget());

        ctrl.Tick(10, 10); // generation 0 promotion supersedes its queued Far base.
        Assert.Equal(new[] { outdoorId }, terrainApplied);
        Assert.True(state.IsLoaded(outdoorId));
        Assert.True(state.IsNearTier(outdoorId));

        ctrl.Tick(20, 20, insideDungeon: true); // hard generation 1 boundary.
        var outdoorUnload = Assert.Single(unloads, unload => unload.Id == outdoorId);
        outbox.Enqueue(new LandblockStreamResult.Unloaded(
            outdoorId,
            outdoorUnload.Generation));
        ctrl.Tick(20, 20, insideDungeon: true);

        Assert.Equal(new[] { outdoorId }, terrainRemoved);

        ctrl.Tick(10, 10, insideDungeon: false);
        outbox.Enqueue(new LandblockStreamResult.Loaded(
            outdoorId,
            LandblockStreamTier.Near,
            new LoadedLandblock(outdoorId, new LandBlock(), Array.Empty<WorldEntity>()),
            mesh,
            generation: 2));
        ctrl.Tick(10, 10);

        Assert.True(state.IsLoaded(outdoorId));
        Assert.DoesNotContain(staticEntity, state.Entities);
        Assert.Equal(2, terrainApplied.Count(id => id == outdoorId));
    }

    [Fact]
    public void LatePromotion_IsRejectedAfterRegionDemotesTargetToFar()
    {
        uint target = StreamingRegion.EncodeLandblockIdForTest(10, 13);
        var outbox = new Queue<LandblockStreamResult>();
        var loads = new List<(uint Id, LandblockStreamJobKind Kind)>();
        var applied = new List<uint>();
        var state = new GpuWorldState();
        state.AddLandblock(
            new LoadedLandblock(target, new LandBlock(), Array.Empty<WorldEntity>()),
            tier: LandblockStreamTier.Far);
        var ctrl = new StreamingController(
            enqueueLoad: (id, kind) => loads.Add((id, kind)),
            enqueueUnload: _ => { },
            drainCompletions: max =>
            {
                var batch = new List<LandblockStreamResult>();
                while (batch.Count < max && outbox.Count > 0) batch.Add(outbox.Dequeue());
                return batch;
            },
            applyTerrain: (build, _) => applied.Add(build.LandblockId),
            state: state,
            nearRadius: 0,
            farRadius: 3,
            workBudgetOptions: UntimedBudget());

        ctrl.Tick(10, 10); // target starts Far.
        ctrl.Tick(10, 13); // target becomes Near; promotion is now in flight.
        Assert.Contains(loads, load =>
            load.Id == target && load.Kind == LandblockStreamJobKind.PromoteToNear);
        ctrl.Tick(10, 10);

        var promotedEntity = new WorldEntity
        {
            Id = 1,
            ServerGuid = 0,
            SourceGfxObjOrSetupId = 0x01000010u,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = [new MeshRef(0x01000010u, System.Numerics.Matrix4x4.Identity)],
        };
        var promotedLandblock = new LoadedLandblock(
            target,
            new LandBlock(),
            new[] { promotedEntity });
        outbox.Enqueue(new LandblockStreamResult.Promoted(
            target,
            promotedLandblock,
            new LandblockMeshData(Array.Empty<TerrainVertex>(), Array.Empty<uint>())));

        ctrl.Tick(10, 10);

        Assert.True(state.IsLoaded(target));
        Assert.False(state.IsNearTier(target));
        Assert.DoesNotContain(promotedEntity, state.Entities);
        Assert.Empty(applied);
    }

    [Fact]
    public void DuplicateNearCompletions_PublishExactlyOnce()
    {
        uint id = StreamingRegion.EncodeLandblockIdForTest(10, 10);
        var entity = new WorldEntity
        {
            Id = 91,
            ServerGuid = 0,
            SourceGfxObjOrSetupId = 0x01000091u,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = [new MeshRef(0x01000091u, System.Numerics.Matrix4x4.Identity)],
        };
        var build = new LandblockBuild(
            new LoadedLandblock(id, new LandBlock(), new[] { entity }));
        var mesh = new LandblockMeshData(Array.Empty<TerrainVertex>(), Array.Empty<uint>());
        var outbox = new Queue<LandblockStreamResult>(new LandblockStreamResult[]
        {
            new LandblockStreamResult.Promoted(id, build, mesh),
            new LandblockStreamResult.Promoted(id, build, mesh),
        });
        var applied = new List<uint>();
        var loadedCallbacks = new List<uint>();
        var state = new GpuWorldState();
        var ctrl = new StreamingController(
            enqueueLoad: (_, _) => { },
            enqueueUnload: _ => { },
            drainCompletions: max =>
            {
                var batch = new List<LandblockStreamResult>();
                while (batch.Count < max && outbox.Count > 0) batch.Add(outbox.Dequeue());
                return batch;
            },
            applyTerrain: (completed, _) => applied.Add(completed.LandblockId),
            state: state,
            nearRadius: 0,
            farRadius: 0,
            onLandblockLoaded: loadedCallbacks.Add,
            workBudgetOptions: GenerousBudget());

        ctrl.Tick(10, 10);

        Assert.Equal(new[] { id }, applied);
        Assert.Equal(new[] { id }, loadedCallbacks);
        Assert.Single(state.Entities, existing => ReferenceEquals(existing, entity));
    }

    [Fact]
    public void InFlightNearLoad_DemotedBeforeFirstCompletion_PublishesTerrainAsFar()
    {
        uint target = StreamingRegion.EncodeLandblockIdForTest(10, 10);
        var outbox = new Queue<LandblockStreamResult>();
        var applied = new List<LandblockBuild>();
        var state = new GpuWorldState();
        var ctrl = new StreamingController(
            enqueueLoad: (_, _) => { },
            enqueueUnload: _ => { },
            drainCompletions: max =>
            {
                var batch = new List<LandblockStreamResult>();
                while (batch.Count < max && outbox.Count > 0) batch.Add(outbox.Dequeue());
                return batch;
            },
            applyTerrain: (build, _) => applied.Add(build),
            state: state,
            nearRadius: 0,
            farRadius: 3,
            workBudgetOptions: UntimedBudget());

        ctrl.Tick(10, 10); // target's initial LoadNear is now in flight.
        ctrl.Tick(10, 13);

        var nearEntity = new WorldEntity
        {
            Id = 1,
            ServerGuid = 0,
            SourceGfxObjOrSetupId = 0x01000010u,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = [new MeshRef(0x01000010u, System.Numerics.Matrix4x4.Identity)],
        };
        var nearLandblock = new LoadedLandblock(
            target,
            new LandBlock(),
            new[] { nearEntity });
        outbox.Enqueue(new LandblockStreamResult.Loaded(
            target,
            LandblockStreamTier.Near,
            nearLandblock,
            new LandblockMeshData(Array.Empty<TerrainVertex>(), Array.Empty<uint>())));

        ctrl.Tick(10, 13);

        Assert.Single(applied);
        Assert.Equal(target, applied[0].LandblockId);
        Assert.Empty(applied[0].Landblock.Entities);
        Assert.Null(applied[0].EnvCells);
        Assert.True(state.IsLoaded(target));
        Assert.False(state.IsNearTier(target));
        Assert.True(state.TryGetLandblock(target, out var resident));
        Assert.Empty(resident!.Entities);
    }

    [Fact]
    public void HardRecenter_RejectsOldOverlappingLoadAndUnloadGenerations()
    {
        uint overlap = StreamingRegion.EncodeLandblockIdForTest(10, 10);
        var outbox = new Queue<LandblockStreamResult>();
        var loads = new List<(uint Id, LandblockStreamJobKind Kind, ulong Generation)>();
        var applied = new List<uint>();
        var state = new GpuWorldState();
        var ctrl = new StreamingController(
            enqueueLoad: (id, kind, generation) => loads.Add((id, kind, generation)),
            enqueueUnload: (_, _) => { },
            drainCompletions: max =>
            {
                var batch = new List<LandblockStreamResult>();
                while (batch.Count < max && outbox.Count > 0) batch.Add(outbox.Dequeue());
                return batch;
            },
            applyTerrain: (build, _) => applied.Add(build.LandblockId),
            state: state,
            nearRadius: 1,
            farRadius: 2,
            workBudgetOptions: UntimedBudget());

        ctrl.Tick(10, 10);
        ulong oldGeneration = loads.Single(load => load.Id == overlap).Generation;

        ctrl.ForceReloadWindow();
        loads.Clear();
        for (int i = 0; i < 32 && !loads.Any(load => load.Id == overlap); i++)
            ctrl.Tick(11, 10);
        ulong newGeneration = loads.Single(load => load.Id == overlap).Generation;
        Assert.NotEqual(oldGeneration, newGeneration);

        outbox.Enqueue(LoadedOf(overlap, oldGeneration));
        outbox.Enqueue(LoadedOf(overlap, newGeneration));
        outbox.Enqueue(new LandblockStreamResult.Unloaded(overlap, oldGeneration));

        for (int i = 0; i < 8 && applied.Count == 0; i++)
            ctrl.Tick(11, 10);

        Assert.Equal(new[] { overlap }, applied);
        Assert.True(state.IsLoaded(overlap));
        Assert.True(state.IsNearTier(overlap));
    }

    [Fact]
    public void HardRecenter_DropsStaleOutboxThroughBoundedAdmissionBeforeCurrentResult()
    {
        uint current = StreamingRegion.EncodeLandblockIdForTest(11, 10);
        var outbox = new Queue<LandblockStreamResult>();
        var loads = new List<(uint Id, LandblockStreamJobKind Kind, ulong Generation)>();
        var applied = new List<uint>();
        var ctrl = new StreamingController(
            enqueueLoad: (id, kind, generation) => loads.Add((id, kind, generation)),
            enqueueUnload: (_, _) => { },
            drainCompletions: max =>
            {
                var batch = new List<LandblockStreamResult>();
                while (batch.Count < max && outbox.Count > 0) batch.Add(outbox.Dequeue());
                return batch;
            },
            applyTerrain: (build, _) => applied.Add(build.LandblockId),
            state: new GpuWorldState(),
            nearRadius: 1,
            farRadius: 2,
            workBudgetOptions: UntimedBudget())
        { MaxCompletionsPerFrame = 1 };

        ctrl.Tick(10, 10);
        ulong oldGeneration = loads[0].Generation;
        ctrl.ForceReloadWindow();
        loads.Clear();
        for (int i = 0; i < 32 && !loads.Any(load => load.Id == current); i++)
            ctrl.Tick(11, 10);
        ulong newGeneration = loads.Single(load => load.Id == current).Generation;

        for (int i = 0; i < 12; i++)
            outbox.Enqueue(LoadedOf(
                StreamingRegion.EncodeLandblockIdForTest(10, 10 + i),
                oldGeneration));
        outbox.Enqueue(LoadedOf(current, newGeneration));

        for (int i = 0; i < 32 && !applied.Contains(current); i++)
            ctrl.Tick(11, 10);

        Assert.Equal(new[] { current }, applied);
        Assert.Equal(0, ctrl.DeferredApplyBacklog);
    }

    [Fact]
    public void NormalRecenter_AwayThenBack_DiscardsCompletedUnloadForReownedId()
    {
        uint target = StreamingRegion.EncodeLandblockIdForTest(10, 10);
        var outbox = new Queue<LandblockStreamResult>();
        var state = new GpuWorldState();
        var ctrl = new StreamingController(
            enqueueLoad: (_, _) => { },
            enqueueUnload: _ => { },
            drainCompletions: max =>
            {
                var batch = new List<LandblockStreamResult>();
                while (batch.Count < max && outbox.Count > 0) batch.Add(outbox.Dequeue());
                return batch;
            },
            applyTerrain: (_, _) => { },
            state: state,
            nearRadius: 0,
            farRadius: 0,
            workBudgetOptions: UntimedBudget());

        ctrl.Tick(10, 10);
        state.AddLandblock(
            new LoadedLandblock(target, new LandBlock(), Array.Empty<WorldEntity>()),
            tier: LandblockStreamTier.Near);

        ctrl.Tick(10, 13); // outside hysteresis: unload is now in flight.
        ctrl.Tick(10, 10);
        outbox.Enqueue(new LandblockStreamResult.Unloaded(target));

        ctrl.Tick(10, 10);

        Assert.True(state.IsLoaded(target));
    }
}
