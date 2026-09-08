using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Meshing;
using Xunit;

namespace AcDream.Core.Tests.Rendering.Wb;

public class EntityClassificationCacheTests
{
    [Fact]
    public void TryGet_EmptyCache_ReturnsFalse()
    {
        var cache = new EntityClassificationCache();
        bool found = cache.TryGet(entityId: 42, landblockHint: 0u, out var entry);
        Assert.False(found);
        Assert.Null(entry);
    }

    [Fact]
    public void Populate_ThenTryGet_ReturnsBatchesInOrder()
    {
        var cache = new EntityClassificationCache();
        var batches = new[]
        {
            MakeCachedBatch(ibo: 1, firstIndex: 0, indexCount: 6, texSlot: 0xAA),
            MakeCachedBatch(ibo: 1, firstIndex: 6, indexCount: 6, texSlot: 0xBB),
        };

        cache.Populate(entityId: 100, landblockHint: 0xA9B40000u, batches);

        Assert.True(cache.TryGet(100, 0xA9B40000u, out var entry));
        Assert.NotNull(entry);
        Assert.Equal(100u, entry!.EntityId);
        Assert.Equal(0xA9B40000u, entry.LandblockHint);
        Assert.Equal(batches, entry.Batches);
    }

    [Fact]
    public void Populate_OverridesExistingEntry()
    {
        var cache = new EntityClassificationCache();
        cache.Populate(100, 0u, new[] { MakeCachedBatch(1, 0, 6, 0xAA) });
        cache.Populate(100, 0u, new[] { MakeCachedBatch(2, 0, 12, 0xCC) });

        Assert.True(cache.TryGet(100, 0u, out var entry));
        Assert.NotNull(entry);
        Assert.Single(entry!.Batches);
        Assert.Equal(0xCCu, entry.Batches[0].TextureSlot.Index);
    }


    [Fact]
    public void ResolveCacheLandblockHint_InteriorEntity_DerivesOwnerLandblock()
    {
        var towerStairs = MakeEntity(id: 0x40AAB309u, parentCellId: 0xAAB30107u);
        // Tuple landblock = the PLAYER's landblock (the bucket path) — must be ignored.
        uint hint = WbDrawDispatcher.ResolveCacheLandblockHint(towerStairs, tupleLandblockId: 0xA9B3FFFFu);
        Assert.Equal(0xAAB3FFFFu, hint);
    }

    [Fact]
    public void ResolveCacheLandblockHint_NoParentCell_KeepsTupleLandblock()
    {
        var outdoorStab = MakeEntity(id: 0x00001234u, parentCellId: null);
        uint hint = WbDrawDispatcher.ResolveCacheLandblockHint(outdoorStab, tupleLandblockId: 0xA9B4FFFFu);
        Assert.Equal(0xA9B4FFFFu, hint);
    }

    [Fact]
    public void CollidingEntityIds_UnderOwnerHints_KeepDistinctBatchSets()
    {
        var cache = new EntityClassificationCache();
        var townBatches = new[] { MakeCachedBatch(1, 0, 6, 0xAA) };
        var towerBatches = new[] { MakeCachedBatch(2, 0, 12, 0xBB), MakeCachedBatch(2, 12, 6, 0xCC) };

        cache.Populate(0x40B3FF09u, 0xA9B3FFFFu, townBatches);
        cache.Populate(0x40B3FF09u, 0xAAB3FFFFu, towerBatches);

        Assert.True(cache.TryGet(0x40B3FF09u, 0xA9B3FFFFu, out var town));
        Assert.True(cache.TryGet(0x40B3FF09u, 0xAAB3FFFFu, out var tower));
        Assert.Equal(townBatches, town!.Batches);
        Assert.Equal(towerBatches, tower!.Batches);

        // Owner-landblock invalidation sweeps ONLY the owner's entry.
        cache.InvalidateLandblock(0xA9B3FFFFu);
        Assert.False(cache.TryGet(0x40B3FF09u, 0xA9B3FFFFu, out _));
        Assert.True(cache.TryGet(0x40B3FF09u, 0xAAB3FFFFu, out _));
    }

    private static AcDream.Core.World.WorldEntity MakeEntity(uint id, uint? parentCellId) => new()
    {
        Id = id,
        SourceGfxObjOrSetupId = 0x020003F2u,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = new List<AcDream.Core.World.MeshRef>(),
        ParentCellId = parentCellId,
    };

    [Fact]
    public void Count_TracksLiveEntries()
    {
        var cache = new EntityClassificationCache();
        Assert.Equal(0, cache.Count);

        cache.Populate(1, 0u, new[] { MakeCachedBatch(1, 0, 6, 0xAA) });
        Assert.Equal(1, cache.Count);

        cache.Populate(2, 0u, new[] { MakeCachedBatch(2, 0, 6, 0xAA) });
        Assert.Equal(2, cache.Count);

        cache.Populate(1, 0u, new[] { MakeCachedBatch(3, 0, 6, 0xBB) });
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Populate_WithEmptyBatches_StoresEmptyEntry()
    {
        var cache = new EntityClassificationCache();
        cache.Populate(entityId: 7, landblockHint: 0u, System.Array.Empty<CachedBatch>());

        Assert.True(cache.TryGet(7, 0u, out var entry));
        Assert.NotNull(entry);
        Assert.Empty(entry!.Batches);
    }

    [Fact]
    public void Populate_SetupMultiPart_StoresFlatBatchPerSubPart()
    {
        // Synthetic Setup with 3 subParts × 2 batches each = 6 flat entries.
        // This pins the spec §3 Q4 decision: pre-flatten Setup multi-parts at
        // populate time so the per-frame hot path is branchless.
        var cache = new EntityClassificationCache();
        var batches = new CachedBatch[6];
        for (int subPart = 0; subPart < 3; subPart++)
        for (int b = 0; b < 2; b++)
        {
            batches[subPart * 2 + b] = MakeCachedBatch(
                ibo: (uint)(subPart + 1),
                firstIndex: (uint)(b * 6),
                indexCount: 6,
                texSlot: (uint)(0x100 + subPart * 2 + b));
        }
        cache.Populate(99, 0u, batches);

        Assert.True(cache.TryGet(99, 0u, out var entry));
        Assert.NotNull(entry);
        Assert.Equal(6, entry!.Batches.Length);
        Assert.Equal(0x100u, entry.Batches[0].TextureSlot.Index);
        Assert.Equal(0x105u, entry.Batches[5].TextureSlot.Index);
    }

    [Fact]
    public void InvalidateEntity_RemovesEntry()
    {
        var cache = new EntityClassificationCache();
        cache.Populate(100, 0u, new[] { MakeCachedBatch(1, 0, 6, 0xAA) });
        Assert.True(cache.TryGet(100, 0u, out _));

        cache.InvalidateEntity(100);

        Assert.False(cache.TryGet(100, 0u, out var entry));
        Assert.Null(entry);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void InvalidateEntity_OnMissingId_NoThrow()
    {
        var cache = new EntityClassificationCache();
        var ex = Record.Exception(() => cache.InvalidateEntity(99999));
        Assert.Null(ex);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void InvalidateLandblock_RemovesAllMatchingEntries()
    {
        var cache = new EntityClassificationCache();
        cache.Populate(1, 0xA9B40000u, new[] { MakeCachedBatch(1, 0, 6, 0xAA) });
        cache.Populate(2, 0xA9B40000u, new[] { MakeCachedBatch(2, 0, 6, 0xBB) });
        cache.Populate(3, 0xA9B40000u, new[] { MakeCachedBatch(3, 0, 6, 0xCC) });
        Assert.Equal(3, cache.Count);

        cache.InvalidateLandblock(0xA9B40000u);

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(1, 0xA9B40000u, out _));
        Assert.False(cache.TryGet(2, 0xA9B40000u, out _));
        Assert.False(cache.TryGet(3, 0xA9B40000u, out _));
    }

    [Fact]
    public void InvalidateLandblock_LeavesNonMatchingEntries()
    {
        var cache = new EntityClassificationCache();
        cache.Populate(1, 0xA9B40000u, new[] { MakeCachedBatch(1, 0, 6, 0xAA) });
        cache.Populate(2, 0xA9B50000u, new[] { MakeCachedBatch(2, 0, 6, 0xBB) });
        cache.Populate(3, 0xA9B40000u, new[] { MakeCachedBatch(3, 0, 6, 0xCC) });

        cache.InvalidateLandblock(0xA9B40000u);

        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet(1, 0xA9B40000u, out _));
        Assert.True(cache.TryGet(2, 0xA9B50000u, out var keep));
        Assert.NotNull(keep);
        Assert.Equal(0xA9B50000u, keep!.LandblockHint);
        Assert.False(cache.TryGet(3, 0xA9B40000u, out _));
    }

    [Fact]
    public void InvalidateLandblock_OnMissingLb_NoThrow()
    {
        var cache = new EntityClassificationCache();
        cache.Populate(1, 0xA9B40000u, new[] { MakeCachedBatch(1, 0, 6, 0xAA) });
        var ex = Record.Exception(() => cache.InvalidateLandblock(0xDEADBEEFu));
        Assert.Null(ex);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void InvalidateRepopulate_UnderReusedId_RepopulatesFresh()
    {
        var cache = new EntityClassificationCache();
        var batchesV1 = new[] { MakeCachedBatch(1, 0, 6, 0xAA) };
        var batchesV2 = new[] { MakeCachedBatch(2, 6, 12, 0xCC) };

        cache.Populate(100, 0xA9B40000u, batchesV1);
        cache.InvalidateEntity(100);
        cache.Populate(100, 0xA9B40000u, batchesV2);

        Assert.True(cache.TryGet(100, 0xA9B40000u, out var entry));
        Assert.NotNull(entry);
        Assert.Equal(batchesV2, entry!.Batches);
        Assert.Equal(0xCCu, entry.Batches[0].TextureSlot.Index);
    }

#if DEBUG
    [Fact]
    public void DebugCrossCheck_BatchCountMismatch_FiresAssert()
    {
        var cache = new EntityClassificationCache();
        cache.Populate(100, 0u, new[]
        {
            MakeCachedBatch(1, 0, 6, 0xAA),
            MakeCachedBatch(1, 6, 6, 0xBB),
        });

        // Synthetic "live" with fewer batches → should fire Debug.Assert.
        var liveBatches = new[] { MakeCachedBatch(1, 0, 6, 0xAA) };

        // Capture Debug.Assert via a custom TraceListener.
        var originalListeners = new System.Diagnostics.TraceListener[System.Diagnostics.Trace.Listeners.Count];
        System.Diagnostics.Trace.Listeners.CopyTo(originalListeners, 0);
        System.Diagnostics.Trace.Listeners.Clear();
        var asserts = new List<string>();
        System.Diagnostics.Trace.Listeners.Add(new CaptureListener(asserts));

        try
        {
            cache.DebugCrossCheck(100, 0u, liveBatches);
        }
        finally
        {
            System.Diagnostics.Trace.Listeners.Clear();
            foreach (var l in originalListeners) System.Diagnostics.Trace.Listeners.Add(l);
        }

        Assert.NotEmpty(asserts);
        string joined = string.Join(" ", asserts);
        Assert.Contains("batch count mismatch", joined);
    }

    [Fact]
    public void DebugCrossCheck_RestPoseMatch_NoAssert()
    {
        var cache = new EntityClassificationCache();
        var batches = new[] { MakeCachedBatch(1, 0, 6, 0xAA) };
        cache.Populate(100, 0u, batches);

        var originalListeners = new System.Diagnostics.TraceListener[System.Diagnostics.Trace.Listeners.Count];
        System.Diagnostics.Trace.Listeners.CopyTo(originalListeners, 0);
        System.Diagnostics.Trace.Listeners.Clear();
        var asserts = new List<string>();
        System.Diagnostics.Trace.Listeners.Add(new CaptureListener(asserts));

        try
        {
            cache.DebugCrossCheck(100, 0u, batches);
        }
        finally
        {
            System.Diagnostics.Trace.Listeners.Clear();
            foreach (var l in originalListeners) System.Diagnostics.Trace.Listeners.Add(l);
        }

        Assert.Empty(asserts);
    }

    private sealed class CaptureListener : System.Diagnostics.TraceListener
    {
        private readonly List<string> _captured;
        public CaptureListener(List<string> captured) { _captured = captured; }
        public override void Write(string? message) { if (message != null) _captured.Add(message); }
        public override void WriteLine(string? message) { if (message != null) _captured.Add(message); }
        public override void Fail(string? message, string? detailMessage)
        {
            _captured.Add($"{message}: {detailMessage}");
        }
        public override void Fail(string? message) { if (message != null) _captured.Add(message); }
    }
#endif

    private static CachedBatch MakeCachedBatch(
        uint ibo, uint firstIndex, int indexCount, uint texSlot)
    {
        var key = new GroupKey(
            FirstIndex: firstIndex,
            BaseVertex: 0,
            IndexCount: indexCount,
            TextureSlot: new AcDream.App.Rendering.Gpu.GpuTextureSlot(texSlot),
            TextureLayer: 0,
            Translucency: TranslucencyKind.Opaque,
            MaterialState: RetailSetSurfaceMaterialState.Opaque,
            FoliageFlags: 0u);
        return new CachedBatch(key, new AcDream.App.Rendering.Gpu.GpuTextureSlot(texSlot), Matrix4x4.Identity);
    }
}
