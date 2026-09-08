using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Meshing;
using AcDream.Core.World;
using Xunit;

namespace AcDream.Core.Tests.Rendering.Wb;

public sealed class WbDrawDispatcherBucketingTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static WorldEntity MakeEntity(uint id, Vector3 position)
        => new WorldEntity
        {
            Id = id,
            SourceGfxObjOrSetupId = 0,
            Position = position,
            Rotation = Quaternion.Identity,
            MeshRefs = System.Array.Empty<MeshRef>(),
        };

    private static WorldEntity MakeEntityWithMesh(uint id, Vector3 position)
        => new WorldEntity
        {
            Id = id,
            SourceGfxObjOrSetupId = 0,
            Position = position,
            Rotation = Quaternion.Identity,
            MeshRefs = new[] { new MeshRef { GfxObjId = 0x01000001u } },
        };

    private static Dictionary<uint, WorldEntity> BuildById(IEnumerable<WorldEntity> entities)
    {
        var d = new Dictionary<uint, WorldEntity>();
        foreach (var e in entities) d[e.Id] = e;
        return d;
    }

    /// <summary>
    /// A frustum positioned at (1e6+1, 1e6+1, 1e6+1) looking toward (1e6, 1e6, 1e6)
    /// with a very narrow near/far. Any AABB near the origin (0..20000) is
    /// far behind the near plane and fails all six planes.
    /// </summary>
    private static FrustumPlanes MakeFarAwayFrustum()
    {
        var view = Matrix4x4.CreateLookAt(
            new Vector3(1e6f + 1f, 1e6f + 1f, 1e6f + 1f),
            new Vector3(1e6f, 1e6f, 1e6f),
            Vector3.UnitZ);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f, 1f, 0.1f, 1f);
        return FrustumPlanes.FromViewProjection(view * proj);
    }


    [Fact]
    public void WalkEntities_InvisibleLb_NoAnimated_SkipsEntireBlock()
    {
        // When LB is invisible AND animatedEntityIds is empty/null,
        // WalkEntities should not walk any entities at all.
        var entities = new List<WorldEntity>();
        for (int i = 0; i < 500; i++)
            entities.Add(MakeEntityWithMesh((uint)i, new Vector3(i, 0, 0)));

        var byId = BuildById(entities);
        var entries = new[]
        {
            new WbDrawDispatcher.LandblockEntry(
                0xAAAA_FFFFu,
                new Vector3(10000, 10000, 10000),
                new Vector3(20000, 20000, 20000),
                entities,
                byId),
        };

        var result = WbDrawDispatcher.WalkEntities(
            entries,
            frustum: MakeFarAwayFrustum(),
            neverCullLandblockId: null,
            visibleCellIds: null,
            animatedEntityIds: null);

        Assert.Equal(0, result.EntitiesWalked);
        Assert.Empty(result.ToDraw);
    }

    [Fact]
    public void WalkEntities_NoDrawHiddenOrSuppressedAncestor_SkipsMeshWithoutRemovingEntity()
    {
        WorldEntity suppressed = MakeEntityWithMesh(7u, Vector3.Zero);
        suppressed.IsDrawVisible = false;
        var entities = new[] { suppressed };
        var entries = new[]
        {
            new WbDrawDispatcher.LandblockEntry(
                0xAAAA_FFFFu,
                new Vector3(-10f),
                new Vector3(10f),
                entities,
                BuildById(entities)),
        };

        var result = WbDrawDispatcher.WalkEntities(
            entries,
            frustum: null,
            neverCullLandblockId: null,
            visibleCellIds: null,
            animatedEntityIds: new HashSet<uint> { suppressed.Id });

        Assert.Equal(0, result.EntitiesWalked);
        Assert.Empty(result.ToDraw);
        Assert.Same(suppressed, Assert.Single(entries[0].Entities));

        suppressed.IsDrawVisible = true;
        suppressed.IsAncestorDrawVisible = false;
        result = WbDrawDispatcher.WalkEntities(
            entries,
            frustum: null,
            neverCullLandblockId: null,
            visibleCellIds: null,
            animatedEntityIds: new HashSet<uint> { suppressed.Id });

        Assert.Equal(0, result.EntitiesWalked);
        Assert.Empty(result.ToDraw);
    }

    [Fact]
    public void WalkEntities_InvisibleLb_AnimatedSet_WalksOnlyAnimatedEntities()
    {
        const int Total = 1000;
        var entities = new List<WorldEntity>(Total);
        for (int i = 0; i < Total; i++)
            entities.Add(MakeEntityWithMesh((uint)i, new Vector3(i, 0, 0)));

        var byId = BuildById(entities);
        var animatedSet = new HashSet<uint> { 42 };

        var entries = new[]
        {
            new WbDrawDispatcher.LandblockEntry(
                0xAAAA_FFFFu,
                new Vector3(10000, 10000, 10000),
                new Vector3(20000, 20000, 20000),
                entities,
                byId),
        };

        var result = WbDrawDispatcher.WalkEntities(
            entries,
            frustum: MakeFarAwayFrustum(),
            neverCullLandblockId: null,
            visibleCellIds: null,
            animatedEntityIds: animatedSet);

        // Only the 1 animated entity should be walked — not 1000.
        Assert.Equal(1, result.EntitiesWalked);
        Assert.Single(result.ToDraw);
        Assert.Equal(42u, result.ToDraw[0].Entity.Id);
    }

    [Fact]
    public void WalkEntities_InvisibleLb_AnimatedIdAbsent_ZeroWalked()
    {
        // Animated entity ids 200 and 300 are NOT in this LB (which only
        // has ids 0..99). Should produce zero walks.
        var entities = new List<WorldEntity>();
        for (int i = 0; i < 100; i++)
            entities.Add(MakeEntityWithMesh((uint)i, Vector3.Zero));

        var byId = BuildById(entities);
        var animatedSet = new HashSet<uint> { 200, 300 }; // not in this LB

        var entries = new[]
        {
            new WbDrawDispatcher.LandblockEntry(
                0xBBBB_FFFFu,
                new Vector3(10000, 10000, 10000),
                new Vector3(20000, 20000, 20000),
                entities,
                byId),
        };

        var result = WbDrawDispatcher.WalkEntities(
            entries,
            frustum: MakeFarAwayFrustum(),
            neverCullLandblockId: null,
            visibleCellIds: null,
            animatedEntityIds: animatedSet);

        Assert.Equal(0, result.EntitiesWalked);
        Assert.Empty(result.ToDraw);
    }

    [Fact]
    public void WalkEntities_NeverCullLb_WalksAllEntitiesRegardlessOfFrustum()
    {
        var entities = new List<WorldEntity>
        {
            MakeEntityWithMesh(1, Vector3.Zero),
            MakeEntityWithMesh(2, Vector3.Zero),
            MakeEntityWithMesh(3, Vector3.Zero),
        };

        var byId = BuildById(entities);
        const uint lbId = 0xCCCC_FFFFu;

        var entries = new[]
        {
            new WbDrawDispatcher.LandblockEntry(
                lbId,
                new Vector3(10000, 10000, 10000), // AABB would fail frustum
                new Vector3(20000, 20000, 20000),
                entities,
                byId),
        };

        var result = WbDrawDispatcher.WalkEntities(
            entries,
            frustum: MakeFarAwayFrustum(),
            neverCullLandblockId: lbId,
            visibleCellIds: null,
            animatedEntityIds: null);

        Assert.Equal(3, result.EntitiesWalked);
    }

    [Fact]
    public void WalkEntities_NullFrustum_WalksEntitiesWithMeshRefs()
    {
        var entities = new List<WorldEntity>
        {
            MakeEntityWithMesh(1, Vector3.Zero),
            MakeEntity(2, Vector3.Zero),          // no MeshRefs — must be skipped
            MakeEntityWithMesh(3, Vector3.Zero),
        };

        var byId = BuildById(entities);
        var entries = new[]
        {
            new WbDrawDispatcher.LandblockEntry(
                0xDDDD_FFFFu, Vector3.Zero, Vector3.Zero,
                entities, byId),
        };

        var result = WbDrawDispatcher.WalkEntities(
            entries,
            frustum: null,
            neverCullLandblockId: null,
            visibleCellIds: null,
            animatedEntityIds: null);

        Assert.Equal(2, result.EntitiesWalked);
        Assert.Equal(2, result.ToDraw.Count);
    }


    [Fact]
    public void WalkEntities_VisibleLb_EntityFarAway_CulledViaCachedAabb()
    {
        var entity = MakeEntityWithMesh(1, new Vector3(50000, 50000, 50000));
        entity.RefreshAabb();

        var byId = BuildById(new[] { entity });
        var entries = new[]
        {
            new WbDrawDispatcher.LandblockEntry(
                0xEEEE_FFFFu,
                new Vector3(-10, -10, -10),
                new Vector3(10, 10, 10),
                new List<WorldEntity> { entity },
                byId),
        };

        var view = Matrix4x4.CreateLookAt(new Vector3(-50, 0, 0), Vector3.Zero, Vector3.UnitZ);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, 0.5f, 100f);
        var tightFrustum = FrustumPlanes.FromViewProjection(view * proj);

        var result = WbDrawDispatcher.WalkEntities(
            entries,
            frustum: tightFrustum,
            neverCullLandblockId: null,
            visibleCellIds: null,
            animatedEntityIds: null);

        Assert.Equal(0, result.EntitiesWalked);
    }

    [Fact]
    public void WalkEntities_AnimatedEntity_BypassesPerEntityAabbCull()
    {
        var entity = MakeEntityWithMesh(7, new Vector3(50000, 50000, 50000));
        entity.RefreshAabb();

        var byId = BuildById(new[] { entity });
        var entries = new[]
        {
            new WbDrawDispatcher.LandblockEntry(
                0xEEEF_FFFFu,
                new Vector3(-10, -10, -10),
                new Vector3(10, 10, 10),
                new List<WorldEntity> { entity },
                byId),
        };

        var view = Matrix4x4.CreateLookAt(new Vector3(-50, 0, 0), Vector3.Zero, Vector3.UnitZ);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, 0.5f, 100f);
        var tightFrustum = FrustumPlanes.FromViewProjection(view * proj);

        var animatedSet = new HashSet<uint> { 7 };

        var result = WbDrawDispatcher.WalkEntities(
            entries,
            frustum: tightFrustum,
            neverCullLandblockId: null,
            visibleCellIds: null,
            animatedEntityIds: animatedSet);

        Assert.Equal(1, result.EntitiesWalked);
        Assert.Single(result.ToDraw);
        Assert.Equal(7u, result.ToDraw[0].Entity.Id);
    }

    [Fact]
    public void WalkEntities_AabbDirty_RefreshedLazilyBeforeCull()
    {
        var entity = MakeEntityWithMesh(5, new Vector3(0, 0, 0));
        Assert.True(entity.AabbDirty);

        var byId = BuildById(new[] { entity });
        var entries = new[]
        {
            new WbDrawDispatcher.LandblockEntry(
                0xF0F0_FFFFu,
                new Vector3(-10, -10, -10),
                new Vector3(10, 10, 10),
                new List<WorldEntity> { entity },
                byId),
        };

        // A frustum that accepts things near origin.
        var view = Matrix4x4.CreateLookAt(new Vector3(-50, 0, 0), Vector3.Zero, Vector3.UnitZ);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, 0.1f, 200f);
        var nearOriginFrustum = FrustumPlanes.FromViewProjection(view * proj);

        var result = WbDrawDispatcher.WalkEntities(
            entries,
            frustum: nearOriginFrustum,
            neverCullLandblockId: null,
            visibleCellIds: null,
            animatedEntityIds: null);

        // Entity at origin is inside the frustum after lazy RefreshAabb.
        Assert.Equal(1, result.EntitiesWalked);
        Assert.False(entity.AabbDirty);
    }


    private static CachedBatch MakeCachedBatch(
        uint ibo,
        uint firstIndex,
        int indexCount,
        uint texSlot,
        Matrix4x4? restPose = null)
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
        return new CachedBatch(
            key,
            new AcDream.App.Rendering.Gpu.GpuTextureSlot(texSlot),
            restPose ?? Matrix4x4.Identity);
    }

    [Fact]
    public void Draw_StaticEntity_PopulatesCacheOnFirstFrameAndHitsOnSecond()
    {
        var cache = new EntityClassificationCache();
        var scratch = new List<CachedBatch>();

        Assert.Equal(0, cache.Count);

        const uint EntityId = 100;
        const uint LandblockId = 0xA9B40000u;

        scratch.Add(MakeCachedBatch(ibo: 1, firstIndex: 0, indexCount: 6, texSlot: 0xAA));
        scratch.Add(MakeCachedBatch(ibo: 1, firstIndex: 6, indexCount: 6, texSlot: 0xBB));

        uint? populateEntityId = null;
        uint populateLandblockId = 0u;
        (populateEntityId, populateLandblockId) = WbDrawDispatcher.MaybeFlushOnEntityChange(
            populateEntityId, populateLandblockId, EntityId, cache, scratch);
        populateEntityId = EntityId;
        populateLandblockId = LandblockId;

        WbDrawDispatcher.FinalFlushPopulate(populateEntityId, populateLandblockId, cache, scratch);

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(EntityId, LandblockId, out var entry));
        Assert.NotNull(entry);
        Assert.Equal(2, entry!.Batches.Length);
        Assert.Equal(0xAAu, entry.Batches[0].TextureSlot.Index);
        Assert.Equal(0xBBu, entry.Batches[1].TextureSlot.Index);

        var groups = new Dictionary<GroupKey, List<Matrix4x4>>();
        void AppendInstance(GroupKey k, Matrix4x4 m)
        {
            if (!groups.TryGetValue(k, out var list))
            {
                list = new List<Matrix4x4>();
                groups[k] = list;
            }
            list.Add(m);
        }

        Assert.True(cache.TryGet(EntityId, LandblockId, out var entryHit));
        Assert.NotNull(entryHit);
        var entityWorld = Matrix4x4.CreateTranslation(new Vector3(10f, 20f, 30f));
        WbDrawDispatcher.ApplyCacheHit(entryHit!, entityWorld, AppendInstance);

        Assert.Equal(1, cache.Count);

        Assert.Equal(2, groups.Count);
        foreach (var (_, list) in groups)
            Assert.Single(list);

        foreach (var (_, list) in groups)
            Assert.Equal(entityWorld, list[0]);
    }

    [Fact]
    public void Draw_AnimatedEntity_DoesNotPopulateCache()
    {
        var cache = new EntityClassificationCache();
        var scratch = new List<CachedBatch>();

        const uint AnimatedId = 7;
        const uint LandblockId = 0xA9B40000u;
        var animatedSet = new HashSet<uint> { AnimatedId };

        bool isAnimated = animatedSet.Contains(AnimatedId);
        Assert.True(isAnimated);

        uint? populateEntityId = null;
        uint populateLandblockId = 0u;
        (populateEntityId, populateLandblockId) = WbDrawDispatcher.MaybeFlushOnEntityChange(
            populateEntityId, populateLandblockId, AnimatedId, cache, scratch);


        // End-of-loop flush — no-op for animated-only iterations.
        WbDrawDispatcher.FinalFlushPopulate(populateEntityId, populateLandblockId, cache, scratch);

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(AnimatedId, LandblockId, out _));
    }

    [Fact]
    public void Draw_MultiMeshRefStaticEntity_PopulatesAllBatchesIntoSingleCacheEntry()
    {
        var cache = new EntityClassificationCache();
        var scratch = new List<CachedBatch>();

        const uint EntityId = 200;
        const uint LandblockId = 0xA9B40000u;
        const int MeshRefCount = 3;
        const int BatchesPerMeshRef = 2;
        const int ExpectedTotalBatches = MeshRefCount * BatchesPerMeshRef;

        uint? populateEntityId = null;
        uint populateLandblockId = 0u;

        for (int meshRefIdx = 0; meshRefIdx < MeshRefCount; meshRefIdx++)
        {
            (populateEntityId, populateLandblockId) = WbDrawDispatcher.MaybeFlushOnEntityChange(
                populateEntityId, populateLandblockId, EntityId, cache, scratch);

            for (int b = 0; b < BatchesPerMeshRef; b++)
            {
                uint texSlot = (uint)(0x100 + meshRefIdx * BatchesPerMeshRef + b);
                scratch.Add(MakeCachedBatch(
                    ibo: (uint)(meshRefIdx + 1),
                    firstIndex: (uint)(b * 6),
                    indexCount: 6,
                    texSlot: texSlot));
            }

            populateEntityId = EntityId;
            populateLandblockId = LandblockId;
        }

        WbDrawDispatcher.FinalFlushPopulate(populateEntityId, populateLandblockId, cache, scratch);

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(EntityId, LandblockId, out var entry));
        Assert.NotNull(entry);
        Assert.Equal(EntityId, entry!.EntityId);
        Assert.Equal(LandblockId, entry.LandblockHint);

        Assert.Equal(ExpectedTotalBatches, entry.Batches.Length);

        for (int i = 0; i < ExpectedTotalBatches; i++)
            Assert.Equal((uint)(0x100 + i), entry.Batches[i].TextureSlot.Index);

        Assert.Empty(scratch);
    }

    [Fact]
    public void Cache_Populate_SkipsEntityWithIncompleteClassification()
    {
        var cache = new EntityClassificationCache();
        const uint EntityId = 100;
        const uint LandblockId = 0xA9B40000u;

        // Simulate Draw's per-entity inner-loop logic.
        var scratch = new List<CachedBatch>();
        bool currentEntityIncomplete = false;
        uint? populateEntityId = null;
        uint populateLandblockId = 0u;

        currentEntityIncomplete = true;

        scratch.Add(MakeCachedBatch(ibo: 1, firstIndex: 0, indexCount: 6, texSlot: 0xAA));
        populateEntityId = EntityId;
        populateLandblockId = LandblockId;

        scratch.Add(MakeCachedBatch(ibo: 2, firstIndex: 0, indexCount: 6, texSlot: 0xBB));
        populateEntityId = EntityId;
        populateLandblockId = LandblockId;

        if (currentEntityIncomplete)
        {
            scratch.Clear();
            populateEntityId = null;
        }
        WbDrawDispatcher.FinalFlushPopulate(populateEntityId, populateLandblockId, cache, scratch);

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(EntityId, LandblockId, out _));
    }

    [Fact]
    public void ApplyCacheHit_PerTupleAmplification_DoesNotOccur()
    {

        const int CachedBatchCount = 6;
        var cache = new EntityClassificationCache();
        var batches = new CachedBatch[CachedBatchCount];
        for (int i = 0; i < CachedBatchCount; i++)
        {
            batches[i] = MakeCachedBatch(
                ibo: 1u, firstIndex: (uint)i, indexCount: 6, texSlot: (uint)(0x100 + i));
        }
        cache.Populate(entityId: 100, landblockHint: 0xA9B40000u, batches);

        var groups = new Dictionary<GroupKey, List<Matrix4x4>>();
        uint? lastHitEntityId = null;
        var entityWorld = Matrix4x4.Identity;
        const uint EntityId = 100;
        const int MeshRefCount = 3;

        void AppendInstance(GroupKey k, Matrix4x4 m)
        {
            if (!groups.TryGetValue(k, out var list))
            {
                list = new List<Matrix4x4>();
                groups[k] = list;
            }
            list.Add(m);
        }

        for (int partIdx = 0; partIdx < MeshRefCount; partIdx++)
        {
            if (lastHitEntityId == EntityId) continue;

            if (cache.TryGet(EntityId, 0xA9B40000u, out var entry))
            {
                Assert.NotNull(entry);
                WbDrawDispatcher.ApplyCacheHit(entry!, entityWorld, AppendInstance);
                lastHitEntityId = EntityId;
            }
        }

        int totalMatrices = 0;
        foreach (var (_, matrices) in groups) totalMatrices += matrices.Count;
        Assert.Equal(CachedBatchCount, totalMatrices);  // 6, NOT 18

        Assert.Equal(CachedBatchCount, groups.Count);
    }
}
