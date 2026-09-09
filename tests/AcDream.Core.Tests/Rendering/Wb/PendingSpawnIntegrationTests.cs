using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.Core.World;

namespace AcDream.Core.Tests.Rendering.Wb;

/// <summary>
/// Integration: verifies the pending-spawn list mechanism keeps working
/// after Task 12 wired LandblockSpawnAdapter into GpuWorldState. Server-
/// spawned entities (ServerGuid != 0) park in pending → drain on
/// AddLandblock → end up in the flat view, but they are NEVER registered
/// with the WB adapter (they're per-instance tier).
///
/// The adapter SHOULD see atlas-tier entities (ServerGuid == 0) that
/// arrived in the AddLandblock's payload directly.
/// </summary>
public sealed class PendingSpawnIntegrationTests
{

    [Fact]
    public void LiveEntity_ParkedBeforeLandblock_DrainsButIsNotRegisteredWithAdapter()
    {
        var captured = new CapturingAdapterMock();
        var spawnAdapter = new LandblockSpawnAdapter(captured);
        var state = new GpuWorldState(spawnAdapter);

        var liveEntity = MakeServerSpawned(
            id: 1, serverGuid: 0xCAFE0001u, gfxObjId: 0x01000099u);
        state.PlaceLiveEntityProjection(0x12340011u, liveEntity);

        Assert.Equal(1, state.PendingLiveEntityCount);
        Assert.Empty(captured.IncrementCalls);  // not registered yet — landblock not loaded

        // Now landblock arrives with ONE atlas-tier entity that brings its own
        // GfxObj, plus the pending live entity drains into it.
        var atlasEntity = MakeAtlas(id: 2, gfxObjId: 0x01000010u);
        var lb = new LoadedLandblock(
            LandblockId: 0x1234FFFFu,
            Heightmap: new DatReaderWriter.DBObjs.LandBlock(),
            Entities: new[] { atlasEntity });
        state.AddLandblock(lb);

        // Pending drained.
        Assert.Equal(0, state.PendingLiveEntityCount);

        var allIds = state.Entities.Select(e => e.Id).ToHashSet();
        Assert.Contains(1u, allIds);  // pending entity
        Assert.Contains(2u, allIds);  // landblock entity

        // Adapter only saw the atlas-tier GfxObj. The pending server-spawned
        // entity's GfxObj is NOT registered (filtered by ServerGuid != 0 in
        // LandblockSpawnAdapter).
        Assert.Single(captured.IncrementCalls);
        Assert.Contains(0x01000010ul, captured.IncrementCalls);
        Assert.DoesNotContain(0x01000099ul, captured.IncrementCalls);
    }

    [Fact]
    public void LiveEntity_AfterLandblock_RegistersImmediatelyWithoutAdapterCall()
    {
        var captured = new CapturingAdapterMock();
        var spawnAdapter = new LandblockSpawnAdapter(captured);
        var state = new GpuWorldState(spawnAdapter);

        var atlasEntity = MakeAtlas(id: 1, gfxObjId: 0x01000010u);
        var lb = new LoadedLandblock(
            LandblockId: 0x1234FFFFu,
            Heightmap: new DatReaderWriter.DBObjs.LandBlock(),
            Entities: new[] { atlasEntity });
        state.AddLandblock(lb);

        Assert.Single(captured.IncrementCalls);  // atlas registered

        // Now a live entity arrives — landblock is already loaded.
        var liveEntity = MakeServerSpawned(id: 2, serverGuid: 0xCAFE0001u, gfxObjId: 0x01000099u);
        state.PlaceLiveEntityProjection(0x12340022u, liveEntity);

        Assert.Single(captured.IncrementCalls);
        Assert.Equal(0, state.PendingLiveEntityCount);
    }

    [Fact]
    public void LandblockUnload_ReleasesAtlasIds_PendingDoesNotRegress()
    {
        var captured = new CapturingAdapterMock();
        var spawnAdapter = new LandblockSpawnAdapter(captured);
        var state = new GpuWorldState(spawnAdapter);

        var atlasEntity = MakeAtlas(id: 1, gfxObjId: 0x01000010u);
        var lb = new LoadedLandblock(
            LandblockId: 0x1234FFFFu,
            Heightmap: new DatReaderWriter.DBObjs.LandBlock(),
            Entities: new[] { atlasEntity });
        state.AddLandblock(lb);
        state.RemoveLandblock(0x1234FFFFu);

        Assert.Equal(
            captured.IncrementCalls.OrderBy(x => x),
            captured.DecrementCalls.OrderBy(x => x));
    }

    // ── Test helpers ──────────────────────────────────────────────────────

    private sealed class CapturingAdapterMock : IWbMeshAdapter
    {
        public List<ulong> IncrementCalls { get; } = new();
        public List<ulong> DecrementCalls { get; } = new();
        public void IncrementRefCount(ulong id) => IncrementCalls.Add(id);
        public void DecrementRefCount(ulong id) => DecrementCalls.Add(id);
    }

    private static WorldEntity MakeAtlas(uint id, uint gfxObjId)
        => MakeEntity(id, serverGuid: 0u, gfxObjId);

    private static WorldEntity MakeServerSpawned(uint id, uint serverGuid, uint gfxObjId)
        => MakeEntity(id, serverGuid, gfxObjId);

    private static WorldEntity MakeEntity(uint id, uint serverGuid, uint gfxObjId)
        => new WorldEntity
        {
            Id = id,
            ServerGuid = serverGuid,
            SourceGfxObjOrSetupId = gfxObjId,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = new[] { new MeshRef(gfxObjId, Matrix4x4.Identity) },
        };
}
