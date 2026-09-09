using AcDream.App.Streaming;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Walk;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using System.Collections.Immutable;
using System.Numerics;

namespace AcDream.App.Tests.Streaming;

public sealed class StreamingControllerReadinessTests
{
    [Fact]
    public void NearPublication_OwnsWalkLadderAsOrdinaryReadinessAndReleasesOnRetirement()
    {
        const uint landblockId = 0x1236FFFFu;
        const ulong ladderId = 0x01001234ul;
        var meshes = new ReadinessMeshAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        var outbox = new Queue<LandblockStreamResult>();
        var building = new WalkBuilding
        {
            PositionCellId = 0x12360001u,
            DegradeLevels =
            [
                new WalkBuildingDegradeLevel((uint)ladderId, 1u, 0f, 10f, 20f, null),
            ],
        };
        var envCells = new EnvCellLandblockBuild(
            landblockId,
            Array.Empty<LoadedCell>(),
            Array.Empty<EnvCellShellPlacement>(),
            [new WalkBuildingFactory.Entry(building, Matrix4x4.Identity, Matrix4x4.Identity)]);
        var build = new LandblockBuild(
            new LoadedLandblock(landblockId, new LandBlock(), Array.Empty<WorldEntity>()),
            envCells);
        outbox.Enqueue(new LandblockStreamResult.Loaded(
            landblockId,
            LandblockStreamTier.Near,
            build,
            new AcDream.Core.Terrain.LandblockMeshData([], [])));
        var controller = new StreamingController(
            enqueueLoad: (_, _) => { },
            enqueueUnload: _ => { },
            drainCompletions: maximum =>
            {
                var drained = new List<LandblockStreamResult>();
                while (drained.Count < maximum && outbox.TryDequeue(out var result))
                    drained.Add(result);
                return drained;
            },
            applyTerrain: (_, _) => { },
            state: state,
            nearRadius: 0,
            farRadius: 0);

        for (int frame = 0; frame < 32 && !meshes.ReferenceCounts.ContainsKey(ladderId); frame++)
            controller.Tick(0x12, 0x36);

        Assert.Equal(1, meshes.ReferenceCounts[ladderId]);
        Assert.False(controller.IsRenderNeighborhoodResident(landblockId, 0, 0));
        meshes.ReadyIds.Add(ladderId);
        Assert.True(controller.IsRenderNeighborhoodResident(landblockId, 0, 0));

        state.RemoveEntitiesFromLandblock(landblockId);
        Assert.Empty(meshes.ReferenceCounts);
        GpuLandblockSpatialPublication revisit = state.CommitEntitiesToExistingLandblockSpatial(
            landblockId,
            Array.Empty<WorldEntity>(),
            additionalRenderIds: null,
            additionalOrdinaryRenderIds: envCells.WalkBuildingMeshDependencies);
        state.ActivateLandblockPresentation(revisit);
        Assert.Equal(1, meshes.ReferenceCounts[ladderId]);
        state.RemoveLandblock(landblockId);
        Assert.Empty(meshes.ReferenceCounts);

        GpuLandblockSpatialPublication resetPublication =
            state.CommitLandblockSpatial(
                build.Landblock,
                additionalRenderIds: null,
                tier: LandblockStreamTier.Near,
                additionalOrdinaryRenderIds:
                    envCells.WalkBuildingMeshDependencies);
        state.ActivateLandblockPresentation(resetPublication);
        Assert.Equal(1, meshes.ReferenceCounts[ladderId]);

        GpuWorldRecenterRetirement reset = state.DetachAllForOriginRecenter();
        Assert.Equal(landblockId, Assert.Single(reset.Landblocks).LandblockId);
        state.ReleaseLandblockMeshReferences(landblockId);
        Assert.Empty(meshes.ReferenceCounts);
    }

    [Fact]
    public void RenderNeighborhoodResident_RequiresEveryPublishedLandblockInRing()
    {
        var state = new GpuWorldState();
        StreamingController controller = CreateController(state);

        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
            if (dx == 1 && dy == 1)
                continue;
            AddPublished(state, 0x12 + dx, 0x36 + dy);
        }

        Assert.False(controller.IsRenderNeighborhoodResident(0x12360022u, 1, 1));

        AddPublished(state, 0x13, 0x37);

        Assert.True(controller.IsRenderNeighborhoodResident(0x12360022u, 1, 1));
    }

    [Fact]
    public void RenderNeighborhoodResident_RadiusZeroAcceptsEnvCellId()
    {
        var state = new GpuWorldState();
        StreamingController controller = CreateController(state);
        AddPublished(state, 0x8C, 0x04);

        Assert.True(controller.IsRenderNeighborhoodResident(0x8C0401ADu, 0, 0));
        Assert.False(controller.IsRenderNeighborhoodResident(0x8D0401ADu, 0, 0));
    }

    [Fact]
    public void RenderNeighborhoodResident_SkipsOffMapNeighborsLikePhysicsGate()
    {
        var state = new GpuWorldState();
        StreamingController controller = CreateController(state);
        AddPublished(state, 0, 0);
        AddPublished(state, 0, 1);
        AddPublished(state, 1, 0);
        AddPublished(state, 1, 1);

        Assert.True(controller.IsRenderNeighborhoodResident(0x00000001u, 1, 1));
    }

    [Fact]
    public void RenderNeighborhoodResident_RejectsNegativeRadius()
    {
        StreamingController controller = CreateController(new GpuWorldState());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => controller.IsRenderNeighborhoodResident(0x1236FFFFu, -1, -1));
    }

    [Theory]
    [InlineData(0xFF0401ADu)]
    [InlineData(0x04FF01ADu)]
    [InlineData(0x12360000u)]
    [InlineData(0x12360041u)]
    [InlineData(0x1236FFFEu)]
    public void RenderNeighborhoodResident_RejectsInvalidMapEdgeDestination(uint cellId)
    {
        StreamingController controller = CreateController(new GpuWorldState());

        Assert.False(controller.IsRenderNeighborhoodResident(cellId, 0, 0));
    }

    [Fact]
    public void RenderNeighborhoodResident_WaitsForActualMeshUpload()
    {
        var meshes = new ReadinessMeshAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        StreamingController controller = CreateController(state);
        uint id = 0x1236FFFFu;
        const ulong envCellGeometryId = 0x2_0000_1234ul;
        var entity = new WorldEntity
        {
            Id = 1,
            ServerGuid = 0,
            SourceGfxObjOrSetupId = 0x01000010u,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = new[] { new MeshRef(0x01000010u, System.Numerics.Matrix4x4.Identity) },
        };
        state.AddLandblock(
            new LoadedLandblock(id, new LandBlock(), new[] { entity }),
            new[] { envCellGeometryId });

        Assert.False(controller.IsRenderNeighborhoodResident(id, 0, 0));
        meshes.ReadyIds.Add(0x01000010ul);
        Assert.False(controller.IsRenderNeighborhoodResident(id, 0, 0));
        meshes.ReadyIds.Add(envCellGeometryId);
        Assert.True(controller.IsRenderNeighborhoodResident(id, 0, 0));
    }

    [Fact]
    public void RenderNeighborhoodResident_PreservesShellGateWhenPromotionPrecedesBaseLoad()
    {
        var meshes = new ReadinessMeshAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        StreamingController controller = CreateController(state);
        uint id = 0x1236FFFFu;
        const ulong envCellGeometryId = 0x2_0000_1234ul;

        state.AddEntitiesToExistingLandblock(
            id,
            Array.Empty<WorldEntity>(),
            new[] { envCellGeometryId });
        state.AddLandblock(new LoadedLandblock(id, new LandBlock(), Array.Empty<WorldEntity>()));

        Assert.True(state.IsNearTier(id));
        Assert.False(controller.IsRenderNeighborhoodResident(id, 0, 0));
        meshes.ReadyIds.Add(envCellGeometryId);
        Assert.True(controller.IsRenderNeighborhoodResident(id, 0, 0));
    }

    [Fact]
    public void RenderNeighborhoodResident_FarTerrainDoesNotSatisfyPortalGate()
    {
        var meshes = new ReadinessMeshAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        StreamingController controller = CreateController(state);
        uint id = 0x1236FFFFu;
        const ulong envCellGeometryId = 0x2_0000_1234ul;
        state.AddLandblock(
            new LoadedLandblock(id, new LandBlock(), Array.Empty<WorldEntity>()),
            tier: LandblockStreamTier.Far);

        Assert.False(controller.IsRenderNeighborhoodResident(id, 0, 0));

        state.AddEntitiesToExistingLandblock(
            id,
            Array.Empty<WorldEntity>(),
            new[] { envCellGeometryId });
        Assert.False(controller.IsRenderNeighborhoodResident(id, 0, 0));
        meshes.ReadyIds.Add(envCellGeometryId);
        Assert.True(controller.IsRenderNeighborhoodResident(id, 0, 0));
    }

    [Fact]
    public void StaleFarPublication_DoesNotReplaceNearEntitiesOrReadiness()
    {
        var meshes = new ReadinessMeshAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        StreamingController controller = CreateController(state);
        uint id = 0x1236FFFFu;
        const ulong envCellGeometryId = 0x2_0000_1234ul;
        var entity = new WorldEntity
        {
            Id = 1,
            ServerGuid = 0,
            SourceGfxObjOrSetupId = 0x01000010u,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = [new MeshRef(0x01000010u, System.Numerics.Matrix4x4.Identity)],
        };
        meshes.ReadyIds.UnionWith([0x01000010ul, envCellGeometryId]);
        state.AddLandblock(
            new LoadedLandblock(id, new LandBlock(), new[] { entity }),
            new[] { envCellGeometryId },
            LandblockStreamTier.Near);
        Assert.True(controller.IsRenderNeighborhoodResident(id, 0, 0));

        state.AddLandblock(
            new LoadedLandblock(id, new LandBlock(), Array.Empty<WorldEntity>()),
            tier: LandblockStreamTier.Far);

        Assert.True(state.IsNearTier(id));
        Assert.Contains(entity, state.Entities);
        Assert.True(controller.IsRenderNeighborhoodResident(id, 0, 0));
    }

    [Fact]
    public void EnvCellPublication_RearmsSpecializedPreparationAfterPin()
    {
        uint id = 0x1236FFFFu;
        const ulong geometryId = 0x2_0000_1234ul;
        var meshes = new ReadinessMeshAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        var outbox = new Queue<LandblockStreamResult>();
        int ensureCalls = 0;
        var shell = new EnvCellShellPlacement(
            0x12360001u,
            geometryId,
            0x0D000001u,
            2,
            ImmutableArray.Create<ushort>(3, 4),
            System.Numerics.Vector3.Zero,
            System.Numerics.Quaternion.Identity,
            System.Numerics.Matrix4x4.Identity,
            new WbBoundingBox(System.Numerics.Vector3.Zero, System.Numerics.Vector3.One),
            new WbBoundingBox(System.Numerics.Vector3.Zero, System.Numerics.Vector3.One));
        var envCells = new EnvCellLandblockBuild(
            id,
            Array.Empty<LoadedCell>(),
            new[] { shell });
        var build = new LandblockBuild(
            new LoadedLandblock(id, new LandBlock(), Array.Empty<WorldEntity>()),
            envCells);
        outbox.Enqueue(new LandblockStreamResult.Loaded(
            id,
            LandblockStreamTier.Near,
            build,
            new AcDream.Core.Terrain.LandblockMeshData(
                Array.Empty<AcDream.Core.Terrain.TerrainVertex>(),
                Array.Empty<uint>())));
        var controller = new StreamingController(
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
            ensureEnvCellMeshes: completed =>
            {
                Assert.Equal(1, meshes.ReferenceCounts[geometryId]);
                Assert.Equal(shell, Assert.Single(completed.Shells));
                ensureCalls++;
            });

        for (int frame = 0; frame < 32 && ensureCalls == 0; frame++)
            controller.Tick(0x12, 0x36);

        Assert.Equal(1, ensureCalls);
    }

    [Fact]
    public void PromotionWithoutBase_PublishesSelfContainedNearAndPinsBeforeReplay()
    {
        uint id = 0x1236FFFFu;
        const ulong geometryId = 0x2_0000_5678ul;
        var meshes = new ReadinessMeshAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        var outbox = new Queue<LandblockStreamResult>();
        int ensureCalls = 0;
        var shell = new EnvCellShellPlacement(
            0x12360001u,
            geometryId,
            0x0D000001u,
            2,
            ImmutableArray.Create<ushort>(3, 4),
            System.Numerics.Vector3.Zero,
            System.Numerics.Quaternion.Identity,
            System.Numerics.Matrix4x4.Identity,
            new WbBoundingBox(System.Numerics.Vector3.Zero, System.Numerics.Vector3.One),
            new WbBoundingBox(System.Numerics.Vector3.Zero, System.Numerics.Vector3.One));
        var envCells = new EnvCellLandblockBuild(
            id,
            Array.Empty<LoadedCell>(),
            new[] { shell });
        var nearBuild = new LandblockBuild(
            new LoadedLandblock(id, new LandBlock(), Array.Empty<WorldEntity>()),
            envCells);
        var mesh = new AcDream.Core.Terrain.LandblockMeshData(
            Array.Empty<AcDream.Core.Terrain.TerrainVertex>(),
            Array.Empty<uint>());
        outbox.Enqueue(new LandblockStreamResult.Promoted(id, nearBuild, mesh));
        var controller = new StreamingController(
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
            ensureEnvCellMeshes: completed =>
            {
                Assert.Equal(1, meshes.ReferenceCounts[geometryId]);
                Assert.Equal(shell, Assert.Single(completed.Shells));
                ensureCalls++;
            });

        for (int frame = 0; frame < 32 && ensureCalls == 0; frame++)
            controller.Tick(0x12, 0x36);

        Assert.Equal(1, ensureCalls);
        Assert.Equal(1, meshes.ReferenceCounts[geometryId]);
        Assert.True(state.IsNearTier(id));

        var farBase = new LandblockBuild(
            new LoadedLandblock(id, new LandBlock(), Array.Empty<WorldEntity>()));
        outbox.Enqueue(new LandblockStreamResult.Loaded(
            id,
            LandblockStreamTier.Far,
            farBase,
            mesh));

        controller.Tick(0x12, 0x36);

        Assert.Equal(1, ensureCalls);
        Assert.Equal(1, meshes.ReferenceCounts[geometryId]);
        Assert.True(state.IsNearTier(id));
    }

    [Fact]
    public void PromotionBeforeBase_DemotedBeforeBase_DropsPendingNearPresentation()
    {
        uint id = 0x1236FFFFu;
        const ulong geometryId = 0x2_0000_9ABCul;
        var meshes = new ReadinessMeshAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        var outbox = new Queue<LandblockStreamResult>();
        int ensureCalls = 0;
        var appliedBuilds = new List<LandblockBuild>();
        var demotedAfterStaticDetach = new List<uint>();
        var shell = new EnvCellShellPlacement(
            0x12360001u,
            geometryId,
            0x0D000001u,
            2,
            ImmutableArray.Create<ushort>(3, 4),
            System.Numerics.Vector3.Zero,
            System.Numerics.Quaternion.Identity,
            System.Numerics.Matrix4x4.Identity,
            new WbBoundingBox(System.Numerics.Vector3.Zero, System.Numerics.Vector3.One),
            new WbBoundingBox(System.Numerics.Vector3.Zero, System.Numerics.Vector3.One));
        var envCells = new EnvCellLandblockBuild(
            id,
            Array.Empty<LoadedCell>(),
            new[] { shell });
        var staticEntity = new WorldEntity
        {
            Id = 1,
            ServerGuid = 0,
            SourceGfxObjOrSetupId = 0x01000010u,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = [new MeshRef(0x01000010u, System.Numerics.Matrix4x4.Identity)],
        };
        var nearBuild = new LandblockBuild(
            new LoadedLandblock(id, new LandBlock(), new[] { staticEntity }),
            envCells);
        var mesh = new AcDream.Core.Terrain.LandblockMeshData(
            Array.Empty<AcDream.Core.Terrain.TerrainVertex>(),
            Array.Empty<uint>());
        outbox.Enqueue(new LandblockStreamResult.Promoted(id, nearBuild, mesh));
        var controller = new StreamingController(
            enqueueLoad: (_, _) => { },
            enqueueUnload: _ => { },
            drainCompletions: max =>
            {
                var batch = new List<LandblockStreamResult>();
                while (batch.Count < max && outbox.Count > 0) batch.Add(outbox.Dequeue());
                return batch;
            },
            applyTerrain: (build, _) => appliedBuilds.Add(build),
            state: state,
            nearRadius: 0,
            farRadius: 3,
            demoteNearLayer: demotedId =>
            {
                Assert.DoesNotContain(staticEntity, state.Entities);
                demotedAfterStaticDetach.Add(demotedId);
            },
            ensureEnvCellMeshes: _ => ensureCalls++);

        controller.Tick(0x12, 0x36);
        Assert.Single(appliedBuilds);
        Assert.NotNull(appliedBuilds[0].EnvCells);
        controller.Tick(0x12, 0x39); // normal Near->Far demotion retires presentation.

        var farBase = new LandblockBuild(
            new LoadedLandblock(id, new LandBlock(), Array.Empty<WorldEntity>()));
        outbox.Enqueue(new LandblockStreamResult.Loaded(
            id,
            LandblockStreamTier.Far,
            farBase,
            mesh));
        controller.Tick(0x12, 0x39);
        for (int i = 0; i < 4 && appliedBuilds.Count < 2; i++)
            controller.Tick(0x12, 0x39);

        Assert.Equal(1, ensureCalls);
        Assert.DoesNotContain(geometryId, meshes.ReferenceCounts);
        Assert.DoesNotContain(staticEntity, state.Entities);
        Assert.Equal(new[] { id }, demotedAfterStaticDetach);
        Assert.True(state.IsLoaded(id));
        Assert.False(state.IsNearTier(id));
        Assert.Equal(2, appliedBuilds.Count);
        Assert.Null(appliedBuilds[1].EnvCells);
        Assert.Empty(appliedBuilds[1].Landblock.Entities);
    }


    [Fact]
    public void TieredWindow_InnerRingFarTierMemberIsNotResident()
    {
        var state = new GpuWorldState();
        StreamingController controller = CreateController(state);

        for (int dx = -2; dx <= 2; dx++)
        for (int dy = -2; dy <= 2; dy++)
        {
            AddPublished(
                state,
                0x12 + dx,
                0x36 + dy,
                dx == 1 && dy == 0
                    ? LandblockStreamTier.Far
                    : LandblockStreamTier.Near);
        }

        Assert.False(
            controller.IsRenderNeighborhoodResident(0x12360022u, 1, 2));
    }

    [Fact]
    public void TieredWindow_OuterRingFarTierMemberIsResident()
    {
        var state = new GpuWorldState();
        StreamingController controller = CreateController(state);

        for (int dx = -2; dx <= 2; dx++)
        for (int dy = -2; dy <= 2; dy++)
        {
            bool inner = Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1;
            AddPublished(
                state,
                0x12 + dx,
                0x36 + dy,
                inner ? LandblockStreamTier.Near : LandblockStreamTier.Far);
        }

        Assert.True(
            controller.IsRenderNeighborhoodResident(0x12360022u, 1, 2));
    }

    [Fact]
    public void TieredWindow_AbsentOuterRingMemberIsNotResident()
    {
        var state = new GpuWorldState();
        StreamingController controller = CreateController(state);

        for (int dx = -2; dx <= 2; dx++)
        for (int dy = -2; dy <= 2; dy++)
        {
            if (dx == 2 && dy == -2)
                continue;
            bool inner = Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1;
            AddPublished(
                state,
                0x12 + dx,
                0x36 + dy,
                inner ? LandblockStreamTier.Near : LandblockStreamTier.Far);
        }

        Assert.False(
            controller.IsRenderNeighborhoodResident(0x12360022u, 1, 2));

        AddPublished(state, 0x14, 0x34, LandblockStreamTier.Far);

        Assert.True(
            controller.IsRenderNeighborhoodResident(0x12360022u, 1, 2));
    }

    [Fact]
    public void TieredWindow_RejectsAFarRadiusBelowTheNearRadius()
    {
        StreamingController controller = CreateController(new GpuWorldState());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => controller.IsRenderNeighborhoodResident(0x1236FFFFu, 3, 2));
    }

    [Fact]
    public void TieredWindow_MapCornerDestinationConvergesAtAWideRadius()
    {
        var state = new GpuWorldState();
        StreamingController controller = CreateController(state);

        for (int x = 0; x <= 4; x++)
        for (int y = 0; y <= 4; y++)
        {
            bool inner = x <= 2 && y <= 2;
            AddPublished(
                state,
                x,
                y,
                inner ? LandblockStreamTier.Near : LandblockStreamTier.Far);
        }

        Assert.True(
            controller.IsRenderNeighborhoodResident(0x00000001u, 2, 4));
    }

    [Fact]
    public void FarPublication_IsRenderReadyThroughTheRealPipeline()
    {
        const uint landblockId = 0x1236FFFFu;
        var meshes = new ReadinessMeshAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        var pipeline = new LandblockPresentationPipeline(
            publishBeforeSpatialCommit: (_, _) => { },
            state);

        var entity = new WorldEntity
        {
            Id = 1,
            ServerGuid = 0,
            SourceGfxObjOrSetupId = 0x01000010u,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = [new MeshRef(0x01000010u, System.Numerics.Matrix4x4.Identity)],
        };
        var source = new LandblockBuild(
            new LoadedLandblock(landblockId, new LandBlock(), new[] { entity }));
        var mesh = new AcDream.Core.Terrain.LandblockMeshData(
            Array.Empty<AcDream.Core.Terrain.TerrainVertex>(),
            Array.Empty<uint>());
        var accepted = new LandblockStreamResult.Loaded(
            landblockId,
            LandblockStreamTier.Near,
            source,
            mesh);

        pipeline.PublishAsFar(accepted, source, mesh);

        Assert.True(state.IsLoaded(landblockId));
        Assert.False(state.IsNearTier(landblockId));
        Assert.Empty(meshes.ReadyIds);
        Assert.True(state.IsRenderReady(landblockId));

        StreamingController controller = CreateController(state);
        Assert.False(
            controller.IsRenderNeighborhoodResident(landblockId, 0, 0));
    }

    [Fact]
    public void NearToFarDemote_LeavesTheLandblockRenderReadyThroughTheRealPipeline()
        => NearToFarDemoteLeavesTheLandblockRenderReadyCore();

    [Fact]
    public void NearToFarDemote_WithALiveServerEntity_StaysRenderReady()
    {
        const uint landblockId = 0x1237FFFFu;
        const ulong staticGfxObjId = 0x01000011ul;
        const ulong serverGfxObjId = 0x01000012ul;
        var meshes = new ReadinessMeshAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        var pipeline = new LandblockPresentationPipeline(
            publishBeforeSpatialCommit: (_, _) => { },
            state);
        meshes.ReadyIds.Add(staticGfxObjId);
        meshes.ReadyIds.Add(serverGfxObjId);

        // An ordinary atlas-tier prop, and a server-spawned entity of the
        // kind DetachNearLayer retains across the demote.
        var staticEntity = new WorldEntity
        {
            Id = 1,
            ServerGuid = 0,
            SourceGfxObjOrSetupId = (uint)staticGfxObjId,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = [new MeshRef((uint)staticGfxObjId, System.Numerics.Matrix4x4.Identity)],
        };
        var serverEntity = new WorldEntity
        {
            Id = 2,
            ServerGuid = 0x50000123u,
            SourceGfxObjOrSetupId = (uint)serverGfxObjId,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = [new MeshRef((uint)serverGfxObjId, System.Numerics.Matrix4x4.Identity)],
        };
        state.AddLandblock(new LoadedLandblock(
            landblockId,
            new LandBlock(),
            new[] { staticEntity, serverEntity }));

        Assert.True(state.IsRenderReady(landblockId));
        Assert.Equal(1, meshes.ReferenceCounts[staticGfxObjId]);
        Assert.DoesNotContain(serverGfxObjId, meshes.ReferenceCounts.Keys);

        pipeline.BeginNearLayerRetirement(landblockId);

        Assert.False(state.IsNearTier(landblockId));
        Assert.True(state.IsLoaded(landblockId));
        // The retained server entity must not have re-entered the desired
        // set: the re-assert stays empty and the block stays render-ready.
        Assert.DoesNotContain(serverGfxObjId, meshes.ReferenceCounts.Keys);
        Assert.DoesNotContain(staticGfxObjId, meshes.ReferenceCounts.Keys);
        Assert.True(state.IsRenderReady(landblockId));
    }

    private static void NearToFarDemoteLeavesTheLandblockRenderReadyCore()
    {
        const uint landblockId = 0x1236FFFFu;
        const ulong gfxObjId = 0x01000010ul;
        var meshes = new ReadinessMeshAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        var pipeline = new LandblockPresentationPipeline(
            publishBeforeSpatialCommit: (_, _) => { },
            state);
        meshes.ReadyIds.Add(gfxObjId);

        var entity = new WorldEntity
        {
            Id = 1,
            ServerGuid = 0,
            SourceGfxObjOrSetupId = (uint)gfxObjId,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = [new MeshRef((uint)gfxObjId, System.Numerics.Matrix4x4.Identity)],
        };
        state.AddLandblock(
            new LoadedLandblock(landblockId, new LandBlock(), new[] { entity }));

        Assert.True(state.IsNearTier(landblockId));
        Assert.True(state.IsRenderReady(landblockId));
        Assert.Equal(1, meshes.ReferenceCounts[gfxObjId]);

        pipeline.BeginNearLayerRetirement(landblockId);

        // The demote really happened: Near layer gone, mesh reference released.
        Assert.False(state.IsNearTier(landblockId));
        Assert.DoesNotContain(gfxObjId, meshes.ReferenceCounts.Keys);
        // ...and the landblock is still loaded and still drawn.
        Assert.True(state.IsLoaded(landblockId));
        // The demoted block must now be indistinguishable from one that
        // arrived as Far: registered, empty desired set, render-ready.
        Assert.True(state.IsRenderReady(landblockId));
    }

    [Fact]
    public void TieredWindow_StaysResidentAfterAnOuterRingDemote()
    {
        var meshes = new ReadinessMeshAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        var pipeline = new LandblockPresentationPipeline(
            publishBeforeSpatialCommit: (_, _) => { },
            state);
        StreamingController controller = CreateController(state);

        for (int dx = -2; dx <= 2; dx++)
        for (int dy = -2; dy <= 2; dy++)
            AddPublished(state, 0x12 + dx, 0x36 + dy);

        Assert.True(controller.IsRenderNeighborhoodResident(0x12360022u, 1, 2));

        pipeline.BeginNearLayerRetirement(0x1434FFFFu);

        Assert.True(state.IsLoaded(0x1434FFFFu));
        Assert.False(state.IsNearTier(0x1434FFFFu));
        Assert.True(controller.IsRenderNeighborhoodResident(0x12360022u, 1, 2));
    }

    [Fact]
    public void NearToFarDemote_LeavesTheLandblockRenderReadyUnderBudgetedRetirement()
    {
        const uint landblockId = 0x1236FFFFu;
        const ulong gfxObjId = 0x01000010ul;
        var meshes = new ReadinessMeshAdapter();
        var state = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        meshes.ReadyIds.Add(gfxObjId);
        var entity = new WorldEntity
        {
            Id = 1,
            ServerGuid = 0,
            SourceGfxObjOrSetupId = (uint)gfxObjId,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = [new MeshRef((uint)gfxObjId, System.Numerics.Matrix4x4.Identity)],
        };
        state.AddLandblock(
            new LoadedLandblock(landblockId, new LandBlock(), new[] { entity }));

        LandblockRetirementCoordinator coordinator =
            LandblockRetirementCoordinator.CreateBudgeted(
                state,
                AdvanceNoopPresentationStep,
                static ticket =>
                {
                    while (AdvanceNoopPresentationStep(ticket)
                        != LandblockRetirementOperationResult.NoWork)
                    {
                    }
                });
        coordinator.BeginNearLayer(landblockId);

        int frames = 0;
        while (coordinator.PendingCount != 0 && frames++ < 64)
        {
            var meter = new StreamingWorkMeter(new StreamingWorkBudget(
                maxUpdateTime: TimeSpan.FromSeconds(1),
                maxCompletionAdmissions: 64,
                maxAdoptedCpuBytes: 64 * 1024 * 1024,
                maxEntityOperations: 64,
                maxGpuUploadBytes: 64 * 1024 * 1024,
                maxGlRetireOperations: 64,
                destinationReserveFraction: 0.75f));
            coordinator.Advance(meter);
            meter.FinishFrame();
        }

        Assert.Equal(0, coordinator.PendingCount);
        Assert.True(state.IsLoaded(landblockId));
        Assert.False(state.IsNearTier(landblockId));
        Assert.DoesNotContain(gfxObjId, meshes.ReferenceCounts.Keys);
        Assert.True(state.IsRenderReady(landblockId));
    }

    private static LandblockRetirementOperationResult AdvanceNoopPresentationStep(
        LandblockRetirementTicket ticket) =>
        ticket.NextIncompleteStage switch
        {
            LandblockRetirementStage.EntityLighting
                or LandblockRetirementStage.EntityTranslucency =>
                ticket.RunEntityStep(
                    ticket.NextIncompleteStage,
                    static _ => true,
                    static _ => { }),
            LandblockRetirementStage.PluginProjection =>
                ticket.RunEntityStep(
                    LandblockRetirementStage.PluginProjection,
                    static entity => entity.ServerGuid == 0,
                    static _ => { }),
            LandblockRetirementStage.Terrain
                or LandblockRetirementStage.Physics
                or LandblockRetirementStage.CellVisibility
                or LandblockRetirementStage.BuildingRegistry
                or LandblockRetirementStage.EnvironmentCells =>
                ticket.RunOnceStep(ticket.NextIncompleteStage, static () => { }),
            _ => LandblockRetirementOperationResult.NoWork,
        };

    private static StreamingController CreateController(GpuWorldState state)
        => new(
            (_, _) => { },
            _ => { },
            _ => Array.Empty<LandblockStreamResult>(),
            (_, _) => { },
            state,
            nearRadius: 1,
            farRadius: 2);

    private static void AddPublished(
        GpuWorldState state,
        int x,
        int y,
        LandblockStreamTier tier = LandblockStreamTier.Near)
    {
        uint id = ((uint)x << 24) | ((uint)y << 16) | 0xFFFFu;
        state.AddLandblock(
            new LoadedLandblock(id, new LandBlock(), Array.Empty<WorldEntity>()),
            tier: tier);
    }

    private sealed class ReadinessMeshAdapter : IWbMeshAdapter
    {
        public HashSet<ulong> ReadyIds { get; } = new();
        public Dictionary<ulong, int> ReferenceCounts { get; } = new();
        public void IncrementRefCount(ulong id) =>
            ReferenceCounts[id] = ReferenceCounts.GetValueOrDefault(id) + 1;
        public void DecrementRefCount(ulong id)
        {
            int next = ReferenceCounts.GetValueOrDefault(id) - 1;
            if (next <= 0) ReferenceCounts.Remove(id);
            else ReferenceCounts[id] = next;
        }
        public bool IsRenderDataReady(ulong id) => ReadyIds.Contains(id);
    }
}
