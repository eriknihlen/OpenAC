using System.Collections.Generic;
using AcDream.App.Streaming;
using AcDream.Core.Terrain;
using AcDream.Core.World;
using Xunit;

namespace AcDream.Core.Tests.Streaming;

public class StreamingControllerTwoTierTests
{
    [Fact]
    public void Tick_FirstCall_EnqueuesNearAndFarLoadsByTier()
    {
        var loads = new List<(uint Id, LandblockStreamJobKind Kind)>();
        var unloads = new List<uint>();
        var state = new GpuWorldState();

        var ctrl = new StreamingController(
            enqueueLoad: (id, kind) => loads.Add((id, kind)),
            enqueueUnload: unloads.Add,
            drainCompletions: _ => System.Array.Empty<LandblockStreamResult>(),
            applyTerrain: (_, _) => { },
            state: state,
            nearRadius: 1,
            farRadius: 3);

        ctrl.Tick(observerCx: 100, observerCy: 100);

        int nearCount = 0, farCount = 0;
        foreach (var (_, kind) in loads)
        {
            if (kind == LandblockStreamJobKind.LoadNear) nearCount++;
            else if (kind == LandblockStreamJobKind.LoadFar) farCount++;
        }
        Assert.Equal(9, nearCount);   // 3x3 inner ring (radius=1)
        Assert.Equal(40, farCount);   // 7x7 - 3x3 outer ring (radius=3)
    }

    [Fact]
    public void Tick_PlayerWalksOutOfNear_ToDemoteRoutesToRemoveEntities()
    {
        // Setup: bootstrap region at (100,100) with near=1, far=3.
        // The bootstrap puts LB (100,100) in the near tier.
        // Walking 4+ east drops LB (100,100) past the near-hysteresis
        // threshold (NearRadius+2 = 3); ToDemote should fire.

        var loads = new List<(uint, LandblockStreamJobKind)>();
        var unloads = new List<uint>();
        var state = new GpuWorldState();

        var lb100 = new LoadedLandblock(
            (100u << 24) | (100u << 16) | 0xFFFFu,
            Heightmap: null!,
            Entities: new[] { new WorldEntity {
                Id = 1, SourceGfxObjOrSetupId = 0,
                Position = System.Numerics.Vector3.Zero,
                Rotation = System.Numerics.Quaternion.Identity,
                MeshRefs = System.Array.Empty<MeshRef>() } });
        state.AddLandblock(lb100);
        Assert.Single(state.Entities);

        var ctrl = new StreamingController(
            enqueueLoad: (id, kind) => loads.Add((id, kind)),
            enqueueUnload: unloads.Add,
            drainCompletions: _ => System.Array.Empty<LandblockStreamResult>(),
            applyTerrain: (_, _) => { },
            state: state,
            nearRadius: 1,
            farRadius: 3);

        ctrl.Tick(observerCx: 100, observerCy: 100);   // bootstrap
        loads.Clear();

        ctrl.Tick(observerCx: 104, observerCy: 100);

        // ToDemote runs synchronously on the render thread (no enqueue).
        // The visible effect is RemoveEntitiesFromLandblock dropping the entity.
        Assert.Empty(state.Entities);
        // Terrain stays loaded (demote != unload).
        Assert.True(state.IsLoaded((100u << 24) | (100u << 16) | 0xFFFFu));
    }

    [Fact]
    public void Tick_DrainingPromoted_RoutesToAddEntitiesToExisting()
    {
        var loads = new List<(uint, LandblockStreamJobKind)>();
        var unloads = new List<uint>();
        var state = new GpuWorldState();

        uint lbId = 0x3232FFFFu;
        var lb = new LoadedLandblock(lbId, Heightmap: null!, Entities: System.Array.Empty<WorldEntity>());
        state.AddLandblock(lb, tier: LandblockStreamTier.Far);
        Assert.Empty(state.Entities);

        var promotedLb = new LoadedLandblock(
            lbId,
            Heightmap: null!,
            Entities: new[] { new WorldEntity {
                Id = 7, SourceGfxObjOrSetupId = 0,
                Position = System.Numerics.Vector3.Zero,
                Rotation = System.Numerics.Quaternion.Identity,
                MeshRefs = System.Array.Empty<MeshRef>() } });
        var promotedMesh = new LandblockMeshData(
            System.Array.Empty<TerrainVertex>(),
            System.Array.Empty<uint>());
        var promoted = new LandblockStreamResult.Promoted(
            lbId,
            promotedLb,
            promotedMesh);
        var queue = new Queue<LandblockStreamResult>();
        queue.Enqueue(promoted);
        int applyPromotedCount = 0;

        var ctrl = new StreamingController(
            enqueueLoad: (id, kind) => loads.Add((id, kind)),
            enqueueUnload: unloads.Add,
            drainCompletions: max =>
            {
                var batch = new List<LandblockStreamResult>();
                while (batch.Count < max && queue.Count > 0) batch.Add(queue.Dequeue());
                return batch;
            },
            applyTerrain: (appliedLb, appliedMesh) =>
            {
                applyPromotedCount++;
                Assert.Same(promotedLb, appliedLb.Landblock);
                Assert.Same(promotedMesh, appliedMesh);
            },
            state: state,
            nearRadius: 2,
            farRadius: 2,
            workBudgetOptions: new StreamingWorkBudgetOptions(
                MaxUpdateMilliseconds: 100,
                MaxCompletionAdmissions: 64,
                MaxAdoptedCpuBytes: 16 * StreamingWorkBudgetOptions.MiB,
                MaxEntityOperations: 512,
                MaxGpuUploadBytes: 16 * StreamingWorkBudgetOptions.MiB,
                MaxGlRetireOperations: 128,
                DestinationReserveFraction: 0.75f));

        ctrl.Tick(50, 50);   // drains the Promoted result

        // Promoted routes to AddEntitiesToExistingLandblock — the entity is now
        // merged into the existing LB record.
        Assert.Equal(1, applyPromotedCount);
        Assert.Single(state.Entities);
        Assert.Equal(7u, state.Entities[0].Id);
    }
}
