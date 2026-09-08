using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.App.Rendering.Wb;
using AcDream.Core.World;

namespace AcDream.Core.Tests.Rendering.Wb;

public sealed class LandblockSpawnAdapterTests
{
    [Fact]
    public void OnLandblockLoaded_RegistersIncrementForEachUniqueAtlasGfxObj()
    {
        var captured = new CapturingAdapterMock();
        var adapter = new LandblockSpawnAdapter(captured);

        // Two procedural (ServerGuid=0) entities with different GfxObj ids.
        var lb = MakeLandblock(landblockId: 0x12340000u, entities: new[]
        {
            MakeAtlasEntity(id: 1, gfxObjIds: new[] { 0x01000010u, 0x01000020u }),
            MakeAtlasEntity(id: 2, gfxObjIds: new[] { 0x01000030u }),
        });

        adapter.OnLandblockLoaded(lb);

        // Three unique ids registered.
        Assert.Equal(3, captured.IncrementCalls.Count);
        Assert.Contains(0x01000010ul, captured.IncrementCalls);
        Assert.Contains(0x01000020ul, captured.IncrementCalls);
        Assert.Contains(0x01000030ul, captured.IncrementCalls);
    }

    [Fact]
    public void OnLandblockLoaded_DedupsSharedIdsAcrossEntities()
    {
        var captured = new CapturingAdapterMock();
        var adapter = new LandblockSpawnAdapter(captured);

        var lb = MakeLandblock(landblockId: 0x12340000u, entities: new[]
        {
            MakeAtlasEntity(id: 1, gfxObjIds: new[] { 0x01000010u, 0x01000020u }),
            MakeAtlasEntity(id: 2, gfxObjIds: new[] { 0x01000010u, 0x01000020u }),
        });

        adapter.OnLandblockLoaded(lb);

        // Two unique ids despite two entities sharing both.
        Assert.Equal(2, captured.IncrementCalls.Count);
    }

    [Fact]
    public void OnLandblockLoaded_SkipsServerSpawnedEntities()
    {
        var captured = new CapturingAdapterMock();
        var adapter = new LandblockSpawnAdapter(captured);

        var lb = MakeLandblock(landblockId: 0x12340000u, entities: new[]
        {
            MakeAtlasEntity(id: 1, gfxObjIds: new[] { 0x01000010u }),
            // ServerGuid != 0 → per-instance tier → must NOT register.
            MakePerInstanceEntity(id: 2, serverGuid: 0xCAFE0001u, gfxObjIds: new[] { 0x01000020u }),
        });

        adapter.OnLandblockLoaded(lb);

        // Only the atlas-tier entity's GfxObj is registered.
        Assert.Single(captured.IncrementCalls);
        Assert.Contains(0x01000010ul, captured.IncrementCalls);
        Assert.DoesNotContain(0x01000020ul, captured.IncrementCalls);
    }

    [Fact]
    public void OnLandblockUnloaded_RegistersMatchingDecrements()
    {
        var captured = new CapturingAdapterMock();
        var adapter = new LandblockSpawnAdapter(captured);

        var lb = MakeLandblock(landblockId: 0x12340000u, entities: new[]
        {
            MakeAtlasEntity(id: 1, gfxObjIds: new[] { 0x01000010u, 0x01000020u }),
        });

        adapter.OnLandblockLoaded(lb);
        adapter.OnLandblockUnloaded(0x12340000u);

        Assert.Equal(captured.IncrementCalls.OrderBy(x => x), captured.DecrementCalls.OrderBy(x => x));
    }

    [Fact]
    public void OnLandblockUnloaded_UnknownLandblock_NoOps()
    {
        var captured = new CapturingAdapterMock();
        var adapter = new LandblockSpawnAdapter(captured);

        adapter.OnLandblockUnloaded(0xDEADBEEFu);

        Assert.Empty(captured.DecrementCalls);
    }

    [Fact]
    public void OnLandblockLoaded_SameLandblockTwice_DedupesAtTheLandblockLevel()
    {
        var captured = new CapturingAdapterMock();
        var adapter = new LandblockSpawnAdapter(captured);

        var lb = MakeLandblock(landblockId: 0x12340000u, entities: new[]
        {
            MakeAtlasEntity(id: 1, gfxObjIds: new[] { 0x01000010u }),
        });

        adapter.OnLandblockLoaded(lb);
        adapter.OnLandblockLoaded(lb);

        // One unique id, one increment — not two.
        Assert.Single(captured.IncrementCalls);
    }

    [Fact]
    public void OnLandblockLoaded_FarEmptyThenNearPromotion_RegistersPromotedIds()
    {
        var captured = new CapturingAdapterMock();
        var adapter = new LandblockSpawnAdapter(captured);

        var far = MakeLandblock(
            landblockId: 0x12340000u,
            entities: System.Array.Empty<WorldEntity>());
        var promoted = MakeLandblock(landblockId: 0x12340000u, entities: new[]
        {
            MakeAtlasEntity(id: 1, gfxObjIds: new[] { 0x01000010u, 0x01000020u }),
        });

        adapter.OnLandblockLoaded(far);
        adapter.OnLandblockLoaded(promoted);

        Assert.Equal(2, captured.IncrementCalls.Count);
        Assert.Contains(0x01000010ul, captured.IncrementCalls);
        Assert.Contains(0x01000020ul, captured.IncrementCalls);
    }

    [Fact]
    public void OnLandblockLoaded_PromotionWithPartiallyRegisteredIds_RegistersOnlyNewIds()
    {
        var captured = new CapturingAdapterMock();
        var adapter = new LandblockSpawnAdapter(captured);

        var initial = MakeLandblock(landblockId: 0x12340000u, entities: new[]
        {
            MakeAtlasEntity(id: 1, gfxObjIds: new[] { 0x01000010u }),
        });
        var promoted = MakeLandblock(landblockId: 0x12340000u, entities: new[]
        {
            MakeAtlasEntity(id: 1, gfxObjIds: new[] { 0x01000010u, 0x01000020u }),
        });

        adapter.OnLandblockLoaded(initial);
        adapter.OnLandblockLoaded(promoted);

        Assert.Equal(new[] { 0x01000010ul, 0x01000020ul }, captured.IncrementCalls);
    }

    // ── Test helpers ──────────────────────────────────────────────────────

    private sealed class CapturingAdapterMock : IWbMeshAdapter
    {
        public List<ulong> IncrementCalls { get; } = new();
        public List<ulong> DecrementCalls { get; } = new();
        public void IncrementRefCount(ulong id) => IncrementCalls.Add(id);
        public void DecrementRefCount(ulong id) => DecrementCalls.Add(id);
    }

    private static LoadedLandblock MakeLandblock(uint landblockId, WorldEntity[] entities)
        => new LoadedLandblock(
            LandblockId: landblockId,
            Heightmap: new DatReaderWriter.DBObjs.LandBlock(),  // empty default
            Entities: entities);

    private static WorldEntity MakeAtlasEntity(uint id, uint[] gfxObjIds)
        => MakeEntity(id, serverGuid: 0u, gfxObjIds);

    private static WorldEntity MakePerInstanceEntity(uint id, uint serverGuid, uint[] gfxObjIds)
        => MakeEntity(id, serverGuid, gfxObjIds);

    private static WorldEntity MakeEntity(uint id, uint serverGuid, uint[] gfxObjIds)
    {
        var meshRefs = gfxObjIds
            .Select(g => new MeshRef(g, Matrix4x4.Identity))
            .ToList();
        return new WorldEntity
        {
            Id = id,
            ServerGuid = serverGuid,
            SourceGfxObjOrSetupId = gfxObjIds.FirstOrDefault(),
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = meshRefs,
        };
    }
}
