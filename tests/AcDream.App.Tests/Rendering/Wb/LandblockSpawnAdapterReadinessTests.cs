using System.Numerics;
using AcDream.App.Rendering.Wb;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class LandblockSpawnAdapterReadinessTests
{
    [Fact]
    public void RenderReady_WaitsForStaticAndEnvCellMeshes()
    {
        var meshes = new ReadinessMeshAdapter();
        var adapter = new LandblockSpawnAdapter(meshes);
        var entity = new WorldEntity
        {
            Id = 1,
            ServerGuid = 0,
            SourceGfxObjOrSetupId = 0x01000010u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = [new MeshRef(0x01000010u, Matrix4x4.Identity)],
        };
        var landblock = new LoadedLandblock(
            0x1234FFFFu,
            new LandBlock(),
            new[] { entity });
        const ulong envCellGeometryId = 0x2_0000_1234ul;

        adapter.OnLandblockLoaded(landblock, new[] { envCellGeometryId });

        Assert.False(adapter.IsLandblockRenderReady(landblock.LandblockId));
        meshes.ReadyIds.Add(0x01000010ul);
        Assert.False(adapter.IsLandblockRenderReady(landblock.LandblockId));
        meshes.ReadyIds.Add(envCellGeometryId);
        Assert.True(adapter.IsLandblockRenderReady(landblock.LandblockId));
    }

    [Fact]
    public void RenderReady_EmptyPublishedLandblockIsReadyUntilUnload()
    {
        var meshes = new ReadinessMeshAdapter();
        var adapter = new LandblockSpawnAdapter(meshes);
        var landblock = new LoadedLandblock(
            0x1234FFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>());

        adapter.OnLandblockLoaded(landblock);

        Assert.True(adapter.IsLandblockRenderReady(landblock.LandblockId));
        adapter.OnLandblockUnloaded(landblock.LandblockId);
        Assert.False(adapter.IsLandblockRenderReady(landblock.LandblockId));
    }

    [Fact]
    public void SharedEnvCellGeometry_IsPinnedPerLandblockAndReleasedSymmetrically()
    {
        const ulong sharedGeometryId = 0x2_0000_1234ul;
        var meshes = new ReadinessMeshAdapter();
        var adapter = new LandblockSpawnAdapter(meshes);
        var first = new LoadedLandblock(
            0x1234FFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>());
        var second = new LoadedLandblock(
            0x1235FFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>());

        adapter.OnLandblockLoaded(first, new[] { sharedGeometryId, sharedGeometryId });
        adapter.OnLandblockLoaded(second, new[] { sharedGeometryId });

        Assert.Equal(2, meshes.ReferenceCounts[sharedGeometryId]);
        Assert.Equal(2, meshes.PreparedPinCalls.Count);

        adapter.OnLandblockUnloaded(first.LandblockId);
        Assert.Equal(1, meshes.ReferenceCounts[sharedGeometryId]);

        adapter.OnLandblockUnloaded(second.LandblockId);
        Assert.DoesNotContain(sharedGeometryId, meshes.ReferenceCounts);
    }

    [Fact]
    public void WalkLadderDependencies_AreOrdinaryOwnedDeduplicatedAndBalancedAcrossRevisit()
    {
        const ulong baseAndLadder = 0x01000010ul;
        const ulong ladderOnly = 0x01000020ul;
        const ulong prepared = 0x2_0000_1234ul;
        var meshes = new ReadinessMeshAdapter();
        var adapter = new LandblockSpawnAdapter(meshes);
        LoadedLandblock landblock = MakeLandblock(0x1234FFFFu, (uint)baseAndLadder);

        adapter.OnLandblockLoaded(
            landblock,
            [prepared],
            [0ul, baseAndLadder, ladderOnly, ladderOnly]);

        Assert.Equal(1, meshes.ReferenceCounts[baseAndLadder]);
        Assert.Equal(1, meshes.ReferenceCounts[ladderOnly]);
        Assert.Equal(1, meshes.ReferenceCounts[prepared]);
        Assert.Equal([prepared], meshes.PreparedPinCalls);
        adapter.OnLandblockUnloaded(landblock.LandblockId);
        Assert.Empty(meshes.ReferenceCounts);

        adapter.OnLandblockLoaded(landblock, [prepared], [baseAndLadder, ladderOnly]);
        Assert.Equal(1, meshes.ReferenceCounts[baseAndLadder]);
        Assert.Equal(1, meshes.ReferenceCounts[ladderOnly]);
        adapter.OnLandblockUnloaded(landblock.LandblockId);
        Assert.Empty(meshes.ReferenceCounts);
    }

    [Fact]
    public void WalkLadderDependency_PartialAcquireFailureCanCancelThenReplaceWithoutLeak()
    {
        const ulong first = 0x01000010ul;
        const ulong failed = 0x01000020ul;
        const ulong replacement = 0x01000030ul;
        var meshes = new FaultInjectingMeshAdapter();
        meshes.FailNext(ReferenceOperation.Increment, failed, committed: false);
        var adapter = new LandblockSpawnAdapter(meshes);
        LoadedLandblock landblock = MakeLandblock(0x1234FFFFu);

        Assert.Throws<InvalidOperationException>(() =>
            adapter.OnLandblockLoaded(landblock, null, [first, failed]));
        Assert.Equal(1, meshes.ReferenceCounts[first]);

        adapter.OnLandblockUnloaded(landblock.LandblockId);
        Assert.Empty(meshes.ReferenceCounts);
        adapter.OnLandblockLoaded(landblock, null, [replacement]);
        Assert.Equal(1, meshes.ReferenceCounts[replacement]);
        adapter.OnLandblockLoaded(landblock, null, [replacement]);
        Assert.Equal(1, meshes.CallCount(ReferenceOperation.Increment, replacement));
        adapter.OnLandblockUnloaded(landblock.LandblockId);
        Assert.Empty(meshes.ReferenceCounts);
    }

    [Fact]
    public void WalkLadderDependency_ExactReplacementReleasesDisplacedDependencyOnce()
    {
        const ulong displaced = 0x01000010ul;
        const ulong retained = 0x01000020ul;
        const ulong added = 0x01000030ul;
        var meshes = new FaultInjectingMeshAdapter();
        var adapter = new LandblockSpawnAdapter(meshes);
        LoadedLandblock landblock = MakeLandblock(0x1234FFFFu);
        adapter.OnLandblockLoaded(landblock, null, [displaced, retained]);

        adapter.OnLandblockLoaded(
            landblock,
            additionalOrdinaryIds: [retained, added],
            replaceExisting: true);

        Assert.DoesNotContain(displaced, meshes.ReferenceCounts);
        Assert.Equal(1, meshes.ReferenceCounts[retained]);
        Assert.Equal(1, meshes.ReferenceCounts[added]);
        Assert.Equal(1, meshes.CallCount(ReferenceOperation.Decrement, displaced));
        Assert.Equal(1, meshes.CallCount(ReferenceOperation.Increment, retained));
        adapter.OnLandblockUnloaded(landblock.LandblockId);
        Assert.Empty(meshes.ReferenceCounts);
    }

    [Fact]
    public void Load_BeforeCommitFailure_RetryAcquiresOnlyUnfinishedReferences()
    {
        const ulong first = 0x01000010ul;
        const ulong failed = 0x01000020ul;
        const ulong last = 0x01000030ul;
        var meshes = new FaultInjectingMeshAdapter();
        meshes.FailNext(ReferenceOperation.Increment, failed, committed: false);
        var adapter = new LandblockSpawnAdapter(meshes);
        LoadedLandblock landblock = MakeLandblock(0x1234FFFFu, (uint)first, (uint)failed, (uint)last);

        Assert.Throws<InvalidOperationException>(() => adapter.OnLandblockLoaded(landblock));

        Assert.Equal(1, meshes.ReferenceCounts[first]);
        Assert.DoesNotContain(failed, meshes.ReferenceCounts);
        Assert.Equal(1, meshes.ReferenceCounts[last]);
        Assert.False(adapter.IsLandblockRenderReady(landblock.LandblockId));

        adapter.OnLandblockLoaded(landblock);

        Assert.Equal(1, meshes.ReferenceCounts[first]);
        Assert.Equal(1, meshes.ReferenceCounts[failed]);
        Assert.Equal(1, meshes.ReferenceCounts[last]);
        Assert.Equal(1, meshes.CallCount(ReferenceOperation.Increment, first));
        Assert.Equal(2, meshes.CallCount(ReferenceOperation.Increment, failed));
        Assert.Equal(1, meshes.CallCount(ReferenceOperation.Increment, last));
        Assert.True(adapter.IsLandblockRenderReady(landblock.LandblockId));
    }

    [Fact]
    public void Load_CommittedPreparedPinFailure_RetryDoesNotDoubleAcquire()
    {
        const ulong prepared = 0x2_0000_1234ul;
        var meshes = new FaultInjectingMeshAdapter();
        meshes.FailNext(ReferenceOperation.PinPrepared, prepared, committed: true);
        var adapter = new LandblockSpawnAdapter(meshes);
        LoadedLandblock landblock = MakeLandblock(0x1234FFFFu);

        MeshReferenceMutationException error = Assert.Throws<MeshReferenceMutationException>(
            () => adapter.OnLandblockLoaded(landblock, new[] { prepared }));

        Assert.True(error.MutationCommitted);
        Assert.Equal(1, meshes.ReferenceCounts[prepared]);

        adapter.OnLandblockLoaded(landblock, new[] { prepared });

        Assert.Equal(1, meshes.ReferenceCounts[prepared]);
        Assert.Equal(1, meshes.CallCount(ReferenceOperation.PinPrepared, prepared));
        Assert.True(adapter.IsLandblockRenderReady(landblock.LandblockId));

        adapter.OnLandblockUnloaded(landblock.LandblockId);
        Assert.Empty(meshes.ReferenceCounts);
    }

    [Fact]
    public void Load_CommittedIncrementFailure_RetryDoesNotDoubleAcquire()
    {
        const ulong id = 0x01000010ul;
        var meshes = new FaultInjectingMeshAdapter();
        meshes.FailNext(ReferenceOperation.Increment, id, committed: true);
        var adapter = new LandblockSpawnAdapter(meshes);
        LoadedLandblock landblock = MakeLandblock(0x1234FFFFu, (uint)id);

        MeshReferenceMutationException error = Assert.Throws<MeshReferenceMutationException>(
            () => adapter.OnLandblockLoaded(landblock));

        Assert.True(error.MutationCommitted);
        Assert.Equal(1, meshes.ReferenceCounts[id]);

        adapter.OnLandblockLoaded(landblock);

        Assert.Equal(1, meshes.ReferenceCounts[id]);
        Assert.Equal(1, meshes.CallCount(ReferenceOperation.Increment, id));
        Assert.True(adapter.IsLandblockRenderReady(landblock.LandblockId));
    }

    [Fact]
    public void Unload_BeforeCommitFailure_RetryReleasesOnlyStillHeldReferences()
    {
        const ulong first = 0x01000010ul;
        const ulong failed = 0x01000020ul;
        const ulong last = 0x01000030ul;
        var meshes = new FaultInjectingMeshAdapter();
        var adapter = new LandblockSpawnAdapter(meshes);
        LoadedLandblock landblock = MakeLandblock(0x1234FFFFu, (uint)first, (uint)failed, (uint)last);
        adapter.OnLandblockLoaded(landblock);
        meshes.FailNext(ReferenceOperation.Decrement, failed, committed: false);

        Assert.Throws<InvalidOperationException>(
            () => adapter.OnLandblockUnloaded(landblock.LandblockId));

        Assert.Single(meshes.ReferenceCounts);
        Assert.Equal(1, meshes.ReferenceCounts[failed]);
        Assert.False(adapter.IsLandblockRenderReady(landblock.LandblockId));

        adapter.OnLandblockUnloaded(landblock.LandblockId);

        Assert.Empty(meshes.ReferenceCounts);
        Assert.Equal(1, meshes.CallCount(ReferenceOperation.Decrement, first));
        Assert.Equal(2, meshes.CallCount(ReferenceOperation.Decrement, failed));
        Assert.Equal(1, meshes.CallCount(ReferenceOperation.Decrement, last));

        adapter.OnLandblockUnloaded(landblock.LandblockId);
        Assert.Equal(2, meshes.CallCount(ReferenceOperation.Decrement, failed));
    }

    [Fact]
    public void Unload_CommittedFailure_DropsCompletedRegistrationBeforePropagating()
    {
        const ulong id = 0x01000010ul;
        var meshes = new FaultInjectingMeshAdapter();
        var adapter = new LandblockSpawnAdapter(meshes);
        LoadedLandblock landblock = MakeLandblock(0x1234FFFFu, (uint)id);
        adapter.OnLandblockLoaded(landblock);
        meshes.FailNext(ReferenceOperation.Decrement, id, committed: true);

        MeshReferenceMutationException error = Assert.Throws<MeshReferenceMutationException>(
            () => adapter.OnLandblockUnloaded(landblock.LandblockId));

        Assert.True(error.MutationCommitted);
        Assert.Empty(meshes.ReferenceCounts);
        Assert.False(adapter.IsLandblockRenderReady(landblock.LandblockId));

        adapter.OnLandblockUnloaded(landblock.LandblockId);
        Assert.Equal(1, meshes.CallCount(ReferenceOperation.Decrement, id));
    }

    [Fact]
    public void OppositeUnloadAfterPartialLoad_ReleasesOnlyReferencesActuallyAcquired()
    {
        const ulong held = 0x01000010ul;
        const ulong neverAcquired = 0x01000020ul;
        var meshes = new FaultInjectingMeshAdapter();
        meshes.FailNext(ReferenceOperation.Increment, neverAcquired, committed: false);
        var adapter = new LandblockSpawnAdapter(meshes);
        LoadedLandblock landblock = MakeLandblock(
            0x1234FFFFu,
            (uint)held,
            (uint)neverAcquired);
        Assert.Throws<InvalidOperationException>(() => adapter.OnLandblockLoaded(landblock));

        adapter.OnLandblockUnloaded(landblock.LandblockId);

        Assert.Empty(meshes.ReferenceCounts);
        Assert.Equal(1, meshes.CallCount(ReferenceOperation.Decrement, held));
        Assert.Equal(0, meshes.CallCount(ReferenceOperation.Decrement, neverAcquired));
        Assert.False(adapter.IsLandblockRenderReady(landblock.LandblockId));
    }

    [Fact]
    public void OppositeLoadAfterPartialUnload_ReusesOverlapAndReconcilesObsoleteResidual()
    {
        const ulong released = 0x01000010ul;
        const ulong residual = 0x01000020ul;
        const ulong newlyDesired = 0x01000030ul;
        var meshes = new FaultInjectingMeshAdapter();
        var adapter = new LandblockSpawnAdapter(meshes);
        LoadedLandblock original = MakeLandblock(
            0x1234FFFFu,
            (uint)released,
            (uint)residual);
        adapter.OnLandblockLoaded(original);
        meshes.FailNext(ReferenceOperation.Decrement, residual, committed: false);
        Assert.Throws<InvalidOperationException>(
            () => adapter.OnLandblockUnloaded(original.LandblockId));

        // The residual id is intentionally absent from the replacement. The
        // reverse edge must finish its old release while acquiring the new
        // snapshot, without reacquiring the already-released first id.
        LoadedLandblock replacement = MakeLandblock(
            original.LandblockId,
            (uint)newlyDesired);
        adapter.OnLandblockLoaded(replacement);

        Assert.Single(meshes.ReferenceCounts);
        Assert.Equal(1, meshes.ReferenceCounts[newlyDesired]);
        Assert.Equal(1, meshes.CallCount(ReferenceOperation.Increment, released));
        Assert.Equal(1, meshes.CallCount(ReferenceOperation.Increment, residual));
        Assert.Equal(1, meshes.CallCount(ReferenceOperation.Increment, newlyDesired));
        Assert.Equal(2, meshes.CallCount(ReferenceOperation.Decrement, residual));
        Assert.True(adapter.IsLandblockRenderReady(replacement.LandblockId));
    }

    [Fact]
    public void OppositeLoadAfterPartialUnload_RetainsStillHeldOverlapWithoutMutation()
    {
        const ulong released = 0x01000010ul;
        const ulong residualOverlap = 0x01000020ul;
        var meshes = new FaultInjectingMeshAdapter();
        var adapter = new LandblockSpawnAdapter(meshes);
        LoadedLandblock landblock = MakeLandblock(
            0x1234FFFFu,
            (uint)released,
            (uint)residualOverlap);
        adapter.OnLandblockLoaded(landblock);
        meshes.FailNext(ReferenceOperation.Decrement, residualOverlap, committed: false);
        Assert.Throws<InvalidOperationException>(
            () => adapter.OnLandblockUnloaded(landblock.LandblockId));

        LoadedLandblock replacement = MakeLandblock(
            landblock.LandblockId,
            (uint)residualOverlap);
        adapter.OnLandblockLoaded(replacement);

        Assert.Single(meshes.ReferenceCounts);
        Assert.Equal(1, meshes.ReferenceCounts[residualOverlap]);
        Assert.Equal(1, meshes.CallCount(ReferenceOperation.Increment, residualOverlap));
        Assert.Equal(1, meshes.CallCount(ReferenceOperation.Decrement, residualOverlap));
        Assert.True(adapter.IsLandblockRenderReady(replacement.LandblockId));
    }

    private static LoadedLandblock MakeLandblock(uint landblockId, params uint[] gfxObjIds)
    {
        WorldEntity[] entities = gfxObjIds.Length == 0
            ? Array.Empty<WorldEntity>()
            :
            [
                new WorldEntity
                {
                    Id = 1,
                    ServerGuid = 0,
                    SourceGfxObjOrSetupId = gfxObjIds[0],
                    Position = Vector3.Zero,
                    Rotation = Quaternion.Identity,
                    MeshRefs = gfxObjIds
                        .Select(id => new MeshRef(id, Matrix4x4.Identity))
                        .ToArray(),
                },
            ];
        return new LoadedLandblock(landblockId, new LandBlock(), entities);
    }

    private enum ReferenceOperation
    {
        Increment,
        PinPrepared,
        Decrement,
    }

    private sealed class FaultInjectingMeshAdapter : IWbMeshAdapter
    {
        private readonly Dictionary<(ReferenceOperation Operation, ulong Id), Queue<bool>> _failures = new();
        private readonly List<(ReferenceOperation Operation, ulong Id)> _calls = new();

        public Dictionary<ulong, int> ReferenceCounts { get; } = new();

        public void FailNext(ReferenceOperation operation, ulong id, bool committed)
        {
            if (!_failures.TryGetValue((operation, id), out Queue<bool>? failures))
            {
                failures = new Queue<bool>();
                _failures.Add((operation, id), failures);
            }
            failures.Enqueue(committed);
        }

        public int CallCount(ReferenceOperation operation, ulong id)
            => _calls.Count(call => call == (operation, id));

        public void IncrementRefCount(ulong id)
            => Mutate(ReferenceOperation.Increment, id, delta: 1);

        public void PinPreparedRenderData(ulong id)
            => Mutate(ReferenceOperation.PinPrepared, id, delta: 1);

        public void DecrementRefCount(ulong id)
            => Mutate(ReferenceOperation.Decrement, id, delta: -1);

        public bool IsRenderDataReady(ulong id) => ReferenceCounts.ContainsKey(id);

        private void Mutate(ReferenceOperation operation, ulong id, int delta)
        {
            _calls.Add((operation, id));
            if (_failures.TryGetValue((operation, id), out Queue<bool>? failures)
                && failures.TryDequeue(out bool committed))
            {
                if (committed)
                    ApplyDelta(id, delta);
                if (failures.Count == 0)
                    _failures.Remove((operation, id));

                var inner = new InvalidOperationException("Injected mesh-reference failure.");
                if (committed)
                {
                    throw new MeshReferenceMutationException(
                        "Injected committed mesh-reference failure.",
                        mutationCommitted: true,
                        inner);
                }
                throw inner;
            }

            ApplyDelta(id, delta);
        }

        private void ApplyDelta(ulong id, int delta)
        {
            int next = ReferenceCounts.GetValueOrDefault(id) + delta;
            if (next < 0)
                throw new InvalidOperationException($"Reference underflow for 0x{id:X}.");
            if (next == 0)
                ReferenceCounts.Remove(id);
            else
                ReferenceCounts[id] = next;
        }
    }

    private sealed class ReadinessMeshAdapter : IWbMeshAdapter
    {
        public HashSet<ulong> ReadyIds { get; } = new();
        public Dictionary<ulong, int> ReferenceCounts { get; } = new();
        public List<ulong> PreparedPinCalls { get; } = new();
        public void IncrementRefCount(ulong id) =>
            ReferenceCounts[id] = ReferenceCounts.GetValueOrDefault(id) + 1;
        public void PinPreparedRenderData(ulong id)
        {
            PreparedPinCalls.Add(id);
            IncrementRefCount(id);
        }
        public void DecrementRefCount(ulong id)
        {
            int count = ReferenceCounts[id] - 1;
            if (count == 0) ReferenceCounts.Remove(id);
            else ReferenceCounts[id] = count;
        }
        public bool IsRenderDataReady(ulong id) => ReadyIds.Contains(id);
    }
}
