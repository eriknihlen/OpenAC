using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using AcDream.App.Rendering;
using AcDream.App.Streaming;
using AcDream.App.Tests.Architecture;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Streaming;

public sealed class LandblockPhysicsPublisherTests
{
    private const uint FirstLandblock = 0xA9B4FFFFu;
    private const uint AdjacentLandblock = 0xAAB4FFFFu;
    private const uint SetupId = 0x02000042u;
    private const uint SecondSetupId = 0x02000043u;
    private static readonly float[] HeightTable =
        Enumerable.Range(0, 256).Select(index => (float)index).ToArray();

    [Fact]
    public void ReplacementYieldsAtHeldWithdrawAndPlaceWithoutPostEngineReseal()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(
            new PhysicsDataCache());
        RuntimePhysicsState physics = lifetime.Physics;
        var publisher = new LandblockPhysicsPublisher(physics, HeightTable);
        Publish(publisher, Build(FirstLandblock));

        const uint guid = 0x70004101u;
        const uint cell = 0xA9B40001u;
        Vector3 position = new(10f, 10f, 0f);
        RuntimeEntityRecord record = lifetime.RegisterEntity(
            RuntimeSpawn(guid, cell, position)).Canonical!;
        lifetime.Entities.SetFinalPhysicsState(record, PhysicsStateFlags.Gravity);
        lifetime.Entities.SetFullCell(record, cell, FirstLandblock);
        var body = new PhysicsBody
        {
            Position = position,
            Orientation = Quaternion.Identity,
            LastUpdateTime = 1d,
            State = PhysicsStateFlags.Gravity,
            TransientState = TransientStateFlags.Active,
        };
        body.SnapToCell(cell, position, position);
        lifetime.Entities.SetPhysicsBody(record, body);
        record.ObjectClock.Activate();
        physics.AcknowledgeSpatialProjection(record, spatial: true);
        RuntimePlacementProjectionToken seeded = SeedRuntimePlacement(
            physics,
            record,
            cell,
            position);
        Assert.True(physics.SetPosition.AcknowledgeProjection(seeded));

        LandblockPhysicsPublication receipt = Begin(
            publisher,
            Build(FirstLandblock));
        Assert.False(publisher.CompletePublication(receipt));
        Assert.False(receipt.EngineMutationCommitted);
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot withdrawal));
        Assert.Equal(RuntimePlacementProjectionKind.Withdraw, withdrawal.Kind);

        Assert.True(physics.SetPosition.AcknowledgeProjection(withdrawal.Token));
        Assert.False(publisher.CompletePublication(receipt));
        Assert.True(receipt.EngineMutationCommitted);
        Assert.True(receipt.SealCommitted);
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot placement));
        Assert.Equal(RuntimePlacementProjectionKind.Place, placement.Kind);

        Assert.False(publisher.CompletePublication(receipt));
        Assert.True(receipt.SealCommitted);
        Assert.True(receipt.EngineMutationCommitted);

        Assert.True(physics.SetPosition.AcknowledgeProjection(placement.Token));
        Assert.True(publisher.CompletePublication(receipt));
        Assert.True(receipt.CompletionCommitted);

        LandblockPhysicsPublication cancelled = Begin(
            publisher,
            Build(FirstLandblock));
        Assert.False(publisher.CompletePublication(cancelled));
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot cancelWithdrawal));
        Assert.True(physics.SetPosition.AcknowledgeProjection(
            cancelWithdrawal.Token));
        Assert.False(publisher.CompletePublication(cancelled));
        Assert.True(cancelled.EngineMutationCommitted);
        Assert.True(cancelled.SealCommitted);
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot cancelPlacement));

        Assert.False(cancelled.TryCancel());
        Assert.True(cancelled.CancellationRequested);
        Assert.True(cancelled.SealCommitted);
        Assert.True(physics.SetPosition.AcknowledgeProjection(
            cancelPlacement.Token));
        Assert.True(cancelled.TryCancel());
        RuntimePhysicsOwnershipSnapshot ownership = physics.CaptureOwnership();
        Assert.Equal(0, ownership.CollisionPrefixMutationCount);
        Assert.Equal(0, ownership.CollisionAdmissionCount);
    }

    [Fact]
    public void Constructor_ClonesHeightTableAndRejectsIncompleteInput()
    {
        Assert.Throws<ArgumentException>(() => new LandblockPhysicsPublisher(
            new RuntimeEntityObjectLifetime().Physics,
            new float[255]));

        float[] mutable = HeightTable.ToArray();
        var lifetime = new RuntimeEntityObjectLifetime(
            new PhysicsDataCache());
        PhysicsEngine engine = lifetime.Physics.Engine;
        var publisher = new LandblockPhysicsPublisher(
            lifetime.Physics,
            mutable);
        mutable[0] = 999f;

        LandblockPhysicsPublication receipt = Begin(
            publisher,
            Build(FirstLandblock));
        publisher.CompletePublication(receipt);

        Assert.Equal(1, engine.LandblockCount);
    }

    [Fact]
    public void FarPublication_IsTerrainOnlyAndDemotionPreservesIt()
    {
        var fixture = Fixture();
        LandblockPhysicsPublication receipt = Begin(
            fixture.Publisher,
            Build(FirstLandblock, bundle: PhysicsDatBundle.Empty));
        fixture.Publisher.CompletePublication(receipt);

        Assert.Equal(1, fixture.Engine.LandblockCount);
        Assert.True(fixture.Engine.IsLandblockTerrainResident(FirstLandblock));
        Assert.Equal(0, fixture.Engine.ShadowObjects.RetainedRegistrationCount);

        fixture.Publisher.DemoteToTerrain(FirstLandblock);

        Assert.Equal(1, fixture.Engine.LandblockCount);
        Assert.True(fixture.Engine.IsLandblockTerrainResident(FirstLandblock));
        Assert.Equal(1, fixture.Publisher.Diagnostics.DemotionCount);

        fixture.Publisher.RemoveLandblock(FirstLandblock);
        Assert.Equal(0, fixture.Engine.LandblockCount);
        Assert.False(fixture.Engine.IsLandblockTerrainResident(FirstLandblock));
        Assert.Equal(1, fixture.Publisher.Diagnostics.FullRemovalCount);
    }

    [Fact]
    public void BeginPublication_CommitsCellPortalAndBuildingPhysicsFromOneBundle()
    {
        var fixture = Fixture();
        const uint envCellId = 0xA9B40100u;
        PhysicsDatBundle bundle = CellPortalAndBuildingBundle(envCellId);

        LandblockPhysicsPublication receipt = Begin(
            fixture.Publisher,
            Build(
                FirstLandblock,
                bundle: bundle,
                origin: new LandblockBuildOrigin(0xA8, 0xB4)));

        Assert.Equal(new Vector3(192f, 0f, 0f), receipt.Origin);
        LandblockPhysicsPublisherDiagnostics diagnostics =
            fixture.Publisher.Diagnostics;
        Assert.Equal(1, diagnostics.CellSurfaceCount);
        Assert.Equal(1, diagnostics.PortalPlaneCount);
        Assert.Equal(1, diagnostics.BuildingCount);
        Assert.Null(fixture.Cache.CellGraph.GetVisible(envCellId));
        Assert.Empty(fixture.Cache.BuildingIds);

        fixture.Publisher.CompletePublication(receipt);

        Assert.NotNull(fixture.Cache.CellGraph.GetVisible(envCellId));
        BuildingPhysics building = Assert.Single(
            fixture.Cache.BuildingIds.Select(id => fixture.Cache.GetBuilding(id)!));
        Assert.Equal(new Vector3(204f, 12f, 0f), building.WorldTransform.Translation);
        Assert.Equal(0x01000077u, building.ModelId);
    }

    [Fact]
    public void DualPublication_InstallsExactPreparedClosureAndReleasesCellOwner()
    {
        const uint envCellId = 0xA9B40100u;
        const uint gfxObjId = 0x01000077u;
        var fixture = Fixture();
        PhysicsDatBundle baseBundle =
            CellPortalAndBuildingBundle(envCellId);
        DatReaderWriter.DBObjs.Environment environment =
            Assert.Single(baseBundle.Environments.Values);
        CellStruct cellStruct = Assert.Single(environment.Cells.Values);
        cellStruct.PhysicsBSP = new PhysicsBSPTree
        {
            Root = new PhysicsBSPNode
            {
                Type = DatReaderWriter.Enums.BSPNodeType.Leaf,
                BoundingSphere = new Sphere
                {
                    Origin = Vector3.Zero,
                    Radius = 10f,
                },
            },
        };
        cellStruct.CellBSP = new CellBSPTree
        {
            Root = new CellBSPNode
            {
                Type = DatReaderWriter.Enums.BSPNodeType.Leaf,
                LeafIndex = 1,
            },
        };
        EnvCell envCell = baseBundle.EnvCells[envCellId];
        var setup = new Setup();
        setup.CylSpheres.Add(new CylSphere
        {
            Origin = new Vector3(0f, 0f, 0.6f),
            Radius = 0.4f,
            Height = 1.2f,
        });
        GfxObj gfx = PhysicsGfx();
        var bundle = new PhysicsDatBundle(
            baseBundle.Info,
            baseBundle.EnvCells,
            baseBundle.Environments,
            new Dictionary<uint, Setup> { [SetupId] = setup },
            new Dictionary<uint, GfxObj> { [gfxObjId] = gfx });
        FlatGfxObjCollisionAsset flatGfx =
            FlatCollisionAssetBuilder.FlattenGfxObj(gfx);
        FlatSetupCollision flatSetup =
            FlatCollisionAssetBuilder.FlattenSetup(setup);
        FlatCellStructureCollisionAsset flatCell =
            FlatCollisionAssetBuilder.FlattenCellStructure(cellStruct);
        FlatEnvCellTopology flatTopology =
            FlatCollisionAssetBuilder.FlattenEnvCellTopology(
                envCellId,
                envCell,
                flatCell.PortalPolygons);
        var collisions = new LandblockCollisionBuild(
            ImmutableDictionary<uint, FlatGfxObjCollisionAsset>.Empty
                .Add(gfxObjId, flatGfx),
            ImmutableDictionary<uint, FlatSetupCollision>.Empty
                .Add(SetupId, flatSetup),
            ImmutableDictionary<uint, FlatCellStructureCollisionAsset>.Empty
                .Add(envCellId, flatCell),
            ImmutableDictionary<uint, FlatEnvCellTopology>.Empty
                .Add(envCellId, flatTopology),
            [gfxObjId],
            [SetupId],
            [envCellId]);
        WorldEntity entity = new()
        {
            Id = 0x80A9B401u,
            SourceGfxObjOrSetupId = SetupId,
            Position = new Vector3(12f, 12f, 0f),
            Rotation = Quaternion.Identity,
            MeshRefs = [new MeshRef(gfxObjId, Matrix4x4.Identity)],
        };

        Publish(
            fixture.Publisher,
            Build(
                FirstLandblock,
                [entity],
                bundle,
                collisions: collisions));

        Assert.Same(flatGfx, fixture.Cache.GetFlatGfxObj(gfxObjId));
        Assert.Same(
            flatGfx.PhysicsBsp,
            fixture.Cache.GetGfxObj(gfxObjId)!.FlatPhysicsBsp);
        Assert.Same(flatSetup, fixture.Cache.GetFlatSetup(SetupId));
        Assert.Same(
            flatSetup,
            fixture.Cache.GetSetup(SetupId)!.FlatCollision);
        Assert.Same(flatCell, fixture.Cache.GetFlatCellStruct(envCellId));
        Assert.Same(flatTopology, fixture.Cache.GetFlatEnvCell(envCellId));
        Assert.Same(
            flatCell.PhysicsBsp,
            fixture.Cache.GetCellStruct(envCellId)!.FlatPhysicsBsp);

        fixture.Publisher.RemoveLandblock(FirstLandblock);

        Assert.Equal(0, fixture.Cache.CellStructCount);
        Assert.Equal(0, fixture.Cache.FlatCellStructCount);
        Assert.Equal(0, fixture.Cache.FlatEnvCellCount);
        Assert.Equal(0, fixture.Engine.ShadowObjects.RetainedRegistrationCount);
    }

    [Fact]
    public void DualPublication_DemotionRehydrateRevisitAndRemovalOwnFlatCellsExactlyOnce()
    {
        const uint firstCellId = 0xA9B40100u;
        const uint adjacentCellId = 0xAAB40100u;
        var fixture = Fixture();
        PhysicsDatBundle firstBundle =
            CellPortalAndBuildingBundle(firstCellId);
        PhysicsDatBundle adjacentBundle =
            CellPortalAndBuildingBundle(adjacentCellId);
        LandblockCollisionBuild firstCollision =
            FlatCellClosure(firstBundle, firstCellId);
        LandblockCollisionBuild adjacentCollision =
            FlatCellClosure(adjacentBundle, adjacentCellId);

        Publish(
            fixture.Publisher,
            Build(
                FirstLandblock,
                bundle: firstBundle,
                collisions: firstCollision));
        Publish(
            fixture.Publisher,
            Build(
                AdjacentLandblock,
                bundle: adjacentBundle,
                collisions: adjacentCollision));

        Assert.Same(
            firstCollision.CellStructures[firstCellId],
            fixture.Cache.GetFlatCellStruct(firstCellId));
        Assert.Same(
            adjacentCollision.CellStructures[adjacentCellId],
            fixture.Cache.GetFlatCellStruct(adjacentCellId));
        Assert.Equal(2, fixture.Cache.FlatCellStructCount);
        Assert.Equal(2, fixture.Cache.FlatEnvCellCount);

        fixture.Publisher.DemoteToTerrain(FirstLandblock);

        Assert.Null(fixture.Cache.GetFlatCellStruct(firstCellId));
        Assert.Null(fixture.Cache.GetFlatEnvCell(firstCellId));
        Assert.Same(
            adjacentCollision.CellStructures[adjacentCellId],
            fixture.Cache.GetFlatCellStruct(adjacentCellId));
        Assert.Equal(1, fixture.Cache.FlatCellStructCount);
        Assert.Equal(1, fixture.Cache.FlatEnvCellCount);

        LandblockCollisionBuild rehydratedCollision =
            FlatCellClosure(firstBundle, firstCellId);
        Publish(
            fixture.Publisher,
            Build(
                FirstLandblock,
                bundle: firstBundle,
                collisions: rehydratedCollision));

        Assert.Same(
            rehydratedCollision.CellStructures[firstCellId],
            fixture.Cache.GetFlatCellStruct(firstCellId));
        Assert.Equal(2, fixture.Cache.FlatCellStructCount);
        Assert.Equal(2, fixture.Cache.FlatEnvCellCount);

        LandblockCollisionBuild revisitCollision =
            FlatCellClosure(firstBundle, firstCellId);
        Publish(
            fixture.Publisher,
            Build(
                FirstLandblock,
                bundle: firstBundle,
                collisions: revisitCollision));

        Assert.Same(
            revisitCollision.CellStructures[firstCellId],
            fixture.Cache.GetFlatCellStruct(firstCellId));
        Assert.Equal(2, fixture.Cache.FlatCellStructCount);
        Assert.Equal(2, fixture.Cache.FlatEnvCellCount);

        fixture.Publisher.RemoveLandblock(FirstLandblock);
        fixture.Publisher.RemoveLandblock(AdjacentLandblock);

        Assert.Equal(0, fixture.Cache.CellStructCount);
        Assert.Equal(0, fixture.Cache.FlatCellStructCount);
        Assert.Equal(0, fixture.Cache.FlatEnvCellCount);
        Assert.Equal(0, fixture.Engine.LandblockCount);
    }

    [Fact]
    public void DualPublication_CancelledPreparedReceiptPublishesNeitherView()
    {
        const uint envCellId = 0xA9B40100u;
        var fixture = Fixture();
        PhysicsDatBundle bundle =
            CellPortalAndBuildingBundle(envCellId);
        CellStruct sourceCell =
            bundle.Environments.Values.Single().Cells.Values.Single();
        sourceCell.PhysicsBSP = new PhysicsBSPTree
        {
            Root = new PhysicsBSPNode
            {
                Type = DatReaderWriter.Enums.BSPNodeType.Leaf,
                BoundingSphere = new Sphere
                {
                    Origin = Vector3.Zero,
                    Radius = 10f,
                },
            },
        };
        LandblockCollisionBuild collision =
            FlatCellClosure(bundle, envCellId);

        LandblockPhysicsPublication cancelled =
            fixture.Publisher.PreparePublication(
                RenderReceipt(Build(
                    FirstLandblock,
                    bundle: bundle,
                    collisions: collision)));

        Assert.Equal(0, fixture.Engine.LandblockCount);
        Assert.Equal(0, fixture.Cache.CellStructCount);
        Assert.Equal(0, fixture.Cache.FlatCellStructCount);
        Assert.Equal(0, fixture.Cache.FlatEnvCellCount);
        Assert.Equal(0, fixture.Engine.ShadowObjects.RetainedRegistrationCount);

        LandblockPhysicsPublication accepted =
            fixture.Publisher.PreparePublication(
                RenderReceipt(Build(
                    FirstLandblock,
                    bundle: bundle,
                    collisions: collision)));
        fixture.Publisher.BeginPublication(accepted);
        fixture.Publisher.CompletePublication(accepted);

        Assert.Same(
            collision.CellStructures[envCellId],
            fixture.Cache.GetFlatCellStruct(envCellId));
        Assert.Equal(1, fixture.Cache.CellStructCount);
        Assert.Equal(1, fixture.Cache.FlatCellStructCount);
        Assert.Equal(1, fixture.Cache.FlatEnvCellCount);
    }

    [Fact]
    public void CompletePublication_PhysicsBspSuppressesSetupCylinderFallback()
    {
        const uint gfxObjId = 0x01000077u;
        var fixture = Fixture();
        CacheCylinderSetup(fixture.Cache);
        fixture.Cache.RegisterGfxObjForTest(gfxObjId, BspGfx(radius: 1.25f));
        WorldEntity entity = new()
        {
            Id = 0x80A9B401u,
            SourceGfxObjOrSetupId = SetupId,
            Position = new Vector3(12f, 12f, 0f),
            Rotation = Quaternion.Identity,
            MeshRefs = [new MeshRef(gfxObjId, Matrix4x4.Identity)],
        };

        Publish(fixture.Publisher, Build(FirstLandblock, [entity]));

        ShadowEntry entry = Assert.Single(
            fixture.Engine.ShadowObjects.AllEntriesForDebug());
        Assert.Equal(ShadowCollisionType.BSP, entry.CollisionType);
        Assert.Equal(1, fixture.Publisher.Diagnostics.StaticBspOwnerCount);
        Assert.Equal(0, fixture.Publisher.Diagnostics.StaticCylinderOwnerCount);
    }

    [Fact]
    public void CompletePublication_SphereOnlySetup_MatchesFromSetupShapeForShape()
    {
        var fixture = Fixture();
        var setup = new Setup();
        setup.Spheres.Add(new Sphere
        {
            Origin = new Vector3(0.3f, -0.2f, 0.9f),
            Radius = 0.55f,
        });
        setup.Spheres.Add(new Sphere
        {
            Origin = new Vector3(-0.1f, 0.4f, 1.4f),
            Radius = 0.25f,
        });
        fixture.Cache.CacheSetup(SetupId, setup);

        const float entScale = 1.3f;
        WorldEntity entity = new()
        {
            Id = 0x80A9B401u,
            SourceGfxObjOrSetupId = SetupId,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = entScale,
            MeshRefs = Array.Empty<MeshRef>(),
        };

        Publish(fixture.Publisher, Build(FirstLandblock, [entity]));

        IReadOnlyList<ShadowShape> expected =
            ShadowShapeBuilder.FromSetup(setup, entScale, _ => false);
        Assert.Equal(2, expected.Count);
        Assert.All(
            expected,
            shape => Assert.Equal(ShadowCollisionType.Sphere, shape.CollisionType));

        ShadowShape[] expectedOrdered =
            expected.OrderBy(shape => shape.Radius).ToArray();
        ShadowEntry[] entries = fixture.Engine.ShadowObjects.AllEntriesForDebug()
            .Where(entry => entry.EntityId == entity.Id)
            .OrderBy(entry => entry.Radius)
            .ToArray();

        Assert.Equal(expectedOrdered.Length, entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            Assert.Equal(ShadowCollisionType.Sphere, entries[i].CollisionType);
            Assert.Equal(expectedOrdered[i].LocalPosition, entries[i].Position);
            Assert.Equal(expectedOrdered[i].Radius, entries[i].Radius);
            Assert.Equal(expectedOrdered[i].Scale, entries[i].Scale);
            Assert.Equal(expectedOrdered[i].CylHeight, entries[i].CylHeight);
        }
    }

    [Fact]
    public void CompletePublication_SphereOnlySetup_MoverGrazesShoulderAndPassesThroughUnobstructed()
    {
        var fixture = Fixture();
        var setup = new Setup();
        setup.Spheres.Add(new Sphere
        {
            Origin = new Vector3(0f, 0f, 2.0f),
            Radius = 1.0f,
        });
        fixture.Cache.CacheSetup(SetupId, setup);

        WorldEntity entity = new()
        {
            Id = 0x80A9B401u,
            SourceGfxObjOrSetupId = SetupId,
            Position = new Vector3(12f, 12f, 0f),
            Rotation = Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
        };
        Publish(fixture.Publisher, Build(FirstLandblock, [entity]));

        ShadowEntry registered = Assert.Single(
            fixture.Engine.ShadowObjects.AllEntriesForDebug());
        Assert.Equal(ShadowCollisionType.Sphere, registered.CollisionType);

        var body = MakeGroundedBody(new Vector3(10.7f, 13.05f, 0f));
        Vector3 target = new(13.3f, 13.05f, 0f);

        ResolveResult result = fixture.Engine.ResolveWithTransition(
            body.Position, target, 0xA9B40001u,
            sphereRadius: 0.15f, sphereHeight: 3.0f,
            stepUpHeight: 0.60f, stepDownHeight: 0.04f,
            isOnGround: true, body: body,
            moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
            movingEntityId: 0u);

        Assert.True(
            MathF.Abs(result.Position.X - target.X) < 0.05f
                && MathF.Abs(result.Position.Y - target.Y) < 0.05f,
            "Mover must clear the boulder unobstructed (true-sphere curve-hit "
            + $"clears at this height/offset); got {result.Position}, wanted {target}.");

        var throughBody = MakeGroundedBody(new Vector3(10.7f, 12f, 0f));
        ResolveResult blockedResult = fixture.Engine.ResolveWithTransition(
            throughBody.Position, new Vector3(13.3f, 12f, 0f), 0xA9B40001u,
            sphereRadius: 0.15f, sphereHeight: 3.0f,
            stepUpHeight: 0.60f, stepDownHeight: 0.04f,
            isOnGround: true, body: throughBody,
            moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
            movingEntityId: 0u);
        Assert.True(
            MathF.Abs(blockedResult.Position.X - 13.3f) >= 0.05f,
            "Aiming straight through the boulder's centre must NOT arrive at "
            + $"the far side; got {blockedResult.Position} — the sphere is "
            + "either unregistered in cell 0xA9B40001 or not colliding.");
    }

    private static PhysicsBody MakeGroundedBody(Vector3 position)
    {
        var floorPlane = new Plane(Vector3.UnitZ, 0f);
        var floorVerts = new[]
        {
            new Vector3(-100f, -100f, 0f),
            new Vector3( 100f, -100f, 0f),
            new Vector3( 100f,  100f, 0f),
            new Vector3(-100f,  100f, 0f),
        };
        return new PhysicsBody
        {
            Position             = position,
            Orientation          = Quaternion.Identity,
            ContactPlaneValid    = true,
            ContactPlane         = floorPlane,
            ContactPlaneCellId   = 0xA9B40001u,
            WalkablePolygonValid = true,
            WalkablePlane        = floorPlane,
            WalkableVertices     = floorVerts,
            WalkableUp           = Vector3.UnitZ,
            TransientState       = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
        };
    }

    [Fact]
    public void CompletePublication_BuildingShellNeverRegistersSetupCollision()
    {
        var fixture = Fixture();
        CacheCylinderSetup(fixture.Cache);
        WorldEntity shell = new()
        {
            Id = 0xC0A9B401u,
            SourceGfxObjOrSetupId = SetupId,
            Position = new Vector3(12f, 12f, 0f),
            Rotation = Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
            IsBuildingShell = true,
        };

        Publish(
            fixture.Publisher,
            Build(
                FirstLandblock,
                [shell],
                CellPortalAndBuildingBundle(0xA9B40100u)));

        Assert.Equal(0, fixture.Engine.ShadowObjects.RetainedRegistrationCount);
        Assert.Empty(fixture.Engine.ShadowObjects.AllEntriesForDebug());
        Assert.Single(fixture.Cache.BuildingIds);
    }

    [Fact]
    public void CompletePublication_MultipleSetupShapesShareOneLogicalOwner()
    {
        var fixture = Fixture();
        CacheCylinderSetup(fixture.Cache, SetupId, shapeCount: 5);
        CacheCylinderSetup(fixture.Cache, SecondSetupId);
        const uint firstId = 0x80A9B401u;
        const uint collidingLegacyId = 0xC0A9B401u;
        WorldEntity first = CylinderEntity(
            firstId,
            new Vector3(12f, 12f, 0f));
        WorldEntity second = CylinderEntity(
            collidingLegacyId,
            new Vector3(36f, 12f, 0f),
            SecondSetupId);

        Publish(
            fixture.Publisher,
            Build(FirstLandblock, [first, second]));

        Assert.Equal(2, fixture.Engine.ShadowObjects.RetainedRegistrationCount);
        ShadowEntry[] entries = fixture.Engine.ShadowObjects
            .AllEntriesForDebug()
            .ToArray();
        Assert.Equal(6, entries.Length);
        Assert.Equal(
            [firstId, collidingLegacyId],
            entries.Select(entry => entry.EntityId).Distinct().Order().ToArray());
    }

    [Fact]
    public void CompletePublication_RunsPerEntityCallbackBeforeCollisionAndIsIdempotent()
    {
        var fixture = Fixture();
        WorldEntity entity = CylinderEntity(
            0x80A9B401u,
            new Vector3(12f, 12f, 0f));
        CacheCylinderSetup(fixture.Cache);
        LandblockPhysicsPublication receipt = Begin(
            fixture.Publisher,
            Build(FirstLandblock, [entity]));
        int callbackCount = 0;

        fixture.Publisher.CompletePublication(receipt, observed =>
        {
            Assert.Same(entity, observed);
            Assert.Equal(0, fixture.Engine.ShadowObjects.RetainedRegistrationCount);
            callbackCount++;
        });
        fixture.Publisher.CompletePublication(receipt, _ => callbackCount++);

        Assert.Equal(1, callbackCount);
        Assert.Equal(1, fixture.Engine.ShadowObjects.RetainedRegistrationCount);
        ShadowEntry entry = Assert.Single(
            fixture.Engine.ShadowObjects.AllEntriesForDebug());
        Assert.Equal(entity.Id, entry.EntityId);
        Assert.Equal(ShadowCollisionType.Cylinder, entry.CollisionType);
        Assert.Equal(1, fixture.Publisher.Diagnostics.CompleteCount);
        Assert.Equal(1, fixture.Publisher.Diagnostics.RefloodCount);
    }

    [Fact]
    public void CompletePublication_CallbackFailureCanRetryWithoutStackingCollision()
    {
        var fixture = Fixture();
        WorldEntity entity = CylinderEntity(
            0x80A9B401u,
            new Vector3(12f, 12f, 0f));
        CacheCylinderSetup(fixture.Cache);
        LandblockPhysicsPublication receipt = Begin(
            fixture.Publisher,
            Build(FirstLandblock, [entity]));
        int attempts = 0;

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Publisher.CompletePublication(receipt, _ =>
            {
                attempts++;
                throw new InvalidOperationException("injected static presentation failure");
            }));
        fixture.Publisher.CompletePublication(receipt, _ => attempts++);

        Assert.Equal(2, attempts);
        Assert.Equal(1, fixture.Engine.ShadowObjects.RetainedRegistrationCount);
        Assert.Single(fixture.Engine.ShadowObjects.AllEntriesForDebug());
        Assert.Equal(1, fixture.Publisher.Diagnostics.CompleteCount);
    }

    [Fact]
    public void NearReapply_ReplacesSameStaticOwnerWithoutDuplication()
    {
        var fixture = Fixture();
        WorldEntity first = CylinderEntity(
            0x80A9B401u,
            new Vector3(12f, 12f, 0f));
        WorldEntity moved = CylinderEntity(
            first.Id,
            new Vector3(36f, 12f, 0f));
        CacheCylinderSetup(fixture.Cache);

        LandblockPhysicsPublication firstReceipt = Begin(
            fixture.Publisher,
            Build(FirstLandblock, [first]));
        fixture.Publisher.CompletePublication(firstReceipt);
        LandblockPhysicsPublication secondReceipt = Begin(
            fixture.Publisher,
            Build(FirstLandblock, [moved]));
        ShadowEntry duringPreparation = Assert.Single(
            fixture.Engine.ShadowObjects.AllEntriesForDebug());
        Assert.Equal(
            first.Position + new Vector3(0f, 0f, 0.6f),
            duringPreparation.Position);
        fixture.Publisher.CompletePublication(secondReceipt);

        Assert.Equal(1, fixture.Engine.LandblockCount);
        Assert.Equal(1, fixture.Engine.ShadowObjects.RetainedRegistrationCount);
        ShadowEntry entry = Assert.Single(
            fixture.Engine.ShadowObjects.AllEntriesForDebug());
        Assert.Equal(moved.Position + new Vector3(0f, 0f, 0.6f), entry.Position);
        Assert.Equal(2, fixture.Publisher.Diagnostics.BeginCount);
        Assert.Equal(2, fixture.Publisher.Diagnostics.CompleteCount);
    }

    [Fact]
    public void CancelledPublicationDisposesOnlyItsPrivateCollisionGeneration()
    {
        var fixture = Fixture();
        Publish(fixture.Publisher, Build(FirstLandblock));
        Assert.True(fixture.Engine.IsLandblockTerrainResident(FirstLandblock));

        LandblockPhysicsPublication pending = Begin(
            fixture.Publisher,
            Build(AdjacentLandblock));
        Assert.False(pending.PreparedGeneration.IsDisposed);

        pending.Dispose();

        Assert.True(pending.PreparedGeneration.IsDisposed);
        Assert.True(fixture.Engine.IsLandblockTerrainResident(FirstLandblock));
        Assert.False(fixture.Engine.IsLandblockTerrainResident(AdjacentLandblock));
        Assert.Throws<ObjectDisposedException>(() =>
            fixture.Publisher.CompletePublication(pending));
    }

    [Fact]
    public void NearReapply_RemovesOmittedStaticAcrossSeamAndPreservesNeighborOwner()
    {
        var fixture = Fixture();
        CacheCylinderSetup(fixture.Cache);
        WorldEntity omitted = CylinderEntity(
            0x80A9B401u,
            new Vector3(191.5f, 12f, 0f));
        WorldEntity neighbor = CylinderEntity(
            0x80AAB401u,
            new Vector3(192.5f, 12f, 0f));
        Publish(
            fixture.Publisher,
            Build(FirstLandblock, [omitted]));
        Publish(
            fixture.Publisher,
            Build(AdjacentLandblock, [neighbor]));

        Publish(
            fixture.Publisher,
            Build(FirstLandblock, Array.Empty<WorldEntity>()));

        Assert.Equal(1, fixture.Engine.ShadowObjects.RetainedRegistrationCount);
        ShadowEntry survivor = Assert.Single(
            fixture.Engine.ShadowObjects.AllEntriesForDebug());
        Assert.Equal(neighbor.Id, survivor.EntityId);
        Assert.Contains(
            fixture.Engine.ShadowObjects.GetObjectsInCell(0xAAB40001u),
            entry => entry.EntityId == neighbor.Id);
        Assert.DoesNotContain(
            fixture.Engine.ShadowObjects.AllEntriesForDebug(),
            entry => entry.EntityId == omitted.Id);
    }

    [Fact]
    public void NearReapply_RebasesCellAndBuildingAndRemovesMissingSnapshotData()
    {
        const uint envCellId = 0xA9B40100u;
        var fixture = Fixture();
        PhysicsDatBundle populated = CellPortalAndBuildingBundle(envCellId);

        Publish(
            fixture.Publisher,
            Build(FirstLandblock, bundle: populated));
        fixture.Engine.UpdatePlayerCurrCell(envCellId);
        AcDream.Core.World.Cells.ObjCell originalCurrent =
            fixture.Cache.CellGraph.CurrCell!;
        Assert.Equal(
            Vector3.Zero,
            fixture.Cache.CellGraph.GetVisible(envCellId)!.WorldTransform.Translation);
        Assert.Equal(
            new Vector3(12f, 12f, 0f),
            Assert.Single(fixture.Cache.BuildingIds
                .Select(id => fixture.Cache.GetBuilding(id)!))
                .WorldTransform.Translation);

        Publish(
            fixture.Publisher,
            Build(
                FirstLandblock,
                bundle: populated,
                origin: new LandblockBuildOrigin(0xA8, 0xB4)));
        Assert.Equal(
            new Vector3(192f, 0f, 0f),
            fixture.Cache.CellGraph.GetVisible(envCellId)!.WorldTransform.Translation);
        Assert.NotSame(originalCurrent, fixture.Cache.CellGraph.CurrCell);
        Assert.Same(
            fixture.Cache.CellGraph.GetVisible(envCellId),
            fixture.Cache.CellGraph.CurrCell);
        Assert.Equal(
            new Vector3(204f, 12f, 0f),
            Assert.Single(fixture.Cache.BuildingIds
                .Select(id => fixture.Cache.GetBuilding(id)!))
                .WorldTransform.Translation);

        Publish(
            fixture.Publisher,
            Build(FirstLandblock, bundle: PhysicsDatBundle.Empty));

        Assert.Null(fixture.Cache.CellGraph.GetVisible(envCellId));
        Assert.Null(fixture.Cache.CellGraph.CurrCell);
        Assert.Empty(fixture.Cache.BuildingIds);
    }

    [Fact]
    public void NearReapply_DoesNotRetireAdjacentCellOrBuildingCache()
    {
        const uint firstCellId = 0xA9B40100u;
        const uint adjacentCellId = 0xAAB40100u;
        var fixture = Fixture();
        Publish(
            fixture.Publisher,
            Build(
                FirstLandblock,
                bundle: CellPortalAndBuildingBundle(firstCellId)));
        Publish(
            fixture.Publisher,
            Build(
                AdjacentLandblock,
                bundle: CellPortalAndBuildingBundle(adjacentCellId)));

        Publish(
            fixture.Publisher,
            Build(FirstLandblock, bundle: PhysicsDatBundle.Empty));

        Assert.Null(fixture.Cache.CellGraph.GetVisible(firstCellId));
        Assert.NotNull(fixture.Cache.CellGraph.GetVisible(adjacentCellId));
        Assert.DoesNotContain(
            fixture.Cache.BuildingIds,
            id => (id & 0xFFFF0000u) == 0xA9B40000u);
        Assert.Contains(
            fixture.Cache.BuildingIds,
            id => (id & 0xFFFF0000u) == 0xAAB40000u);
    }

    [Fact]
    public void AdjacentLandblockDemotion_DoesNotEraseNeighborStaticOwner()
    {
        var fixture = Fixture();
        CacheCylinderSetup(fixture.Cache);
        WorldEntity first = CylinderEntity(
            0x80A9B401u,
            new Vector3(191.5f, 12f, 0f));
        WorldEntity neighbor = CylinderEntity(
            0x80AAB401u,
            new Vector3(192.5f, 12f, 0f));

        Publish(
            fixture.Publisher,
            Build(FirstLandblock, [first]));
        Publish(
            fixture.Publisher,
            Build(AdjacentLandblock, [neighbor]));
        Assert.Equal(2, fixture.Engine.ShadowObjects.RetainedRegistrationCount);
        Assert.Contains(
            fixture.Engine.ShadowObjects.GetObjectsInCell(0xAAB40001u),
            entry => entry.EntityId == neighbor.Id);

        fixture.Publisher.DemoteToTerrain(FirstLandblock);

        Assert.Equal(1, fixture.Engine.ShadowObjects.RetainedRegistrationCount);
        ShadowEntry surviving = Assert.Single(
            fixture.Engine.ShadowObjects.AllEntriesForDebug());
        Assert.Equal(neighbor.Id, surviving.EntityId);
        Assert.True(fixture.Engine.IsLandblockTerrainResident(FirstLandblock));
        Assert.True(fixture.Engine.IsLandblockTerrainResident(AdjacentLandblock));
    }

    [Fact]
    public void FullRemoval_DoesNotEraseAdjacentLandblockCollision()
    {
        var fixture = Fixture();
        CacheCylinderSetup(fixture.Cache);
        WorldEntity first = CylinderEntity(
            0x80A9B401u,
            new Vector3(191.5f, 12f, 0f));
        WorldEntity neighbor = CylinderEntity(
            0x80AAB401u,
            new Vector3(192.5f, 12f, 0f));
        Publish(
            fixture.Publisher,
            Build(
                FirstLandblock,
                [first],
                CellPortalAndBuildingBundle(0xA9B40100u)));
        Publish(
            fixture.Publisher,
            Build(
                AdjacentLandblock,
                [neighbor],
                CellPortalAndBuildingBundle(0xAAB40100u)));

        fixture.Publisher.RemoveLandblock(FirstLandblock);

        Assert.Equal(1, fixture.Engine.LandblockCount);
        Assert.False(fixture.Engine.IsLandblockTerrainResident(FirstLandblock));
        Assert.True(fixture.Engine.IsLandblockTerrainResident(AdjacentLandblock));
        Assert.Equal(1, fixture.Engine.ShadowObjects.RetainedRegistrationCount);
        Assert.Equal(
            neighbor.Id,
            Assert.Single(fixture.Engine.ShadowObjects.AllEntriesForDebug()).EntityId);
        uint survivingBuilding = Assert.Single(fixture.Cache.BuildingIds);
        Assert.Equal(0xAAB40000u, survivingBuilding & 0xFFFF0000u);
    }

    [Fact]
    public void CompletePublication_ForeignReceiptIsRejectedWithoutConsumingIt()
    {
        var first = Fixture();
        var second = Fixture();
        LandblockPhysicsPublication receipt = Begin(
            first.Publisher,
            Build(FirstLandblock));

        Assert.Throws<ArgumentException>(() =>
            second.Publisher.CompletePublication(receipt));
        Assert.Equal(0, second.Publisher.Diagnostics.CompleteCount);

        first.Publisher.CompletePublication(receipt);
        Assert.Equal(1, first.Publisher.Diagnostics.CompleteCount);
    }

    [Fact]
    public void PublisherSurface_HasNoDatReaderResidencyOrLiveOriginDependency()
    {
        Type type = typeof(LandblockPhysicsPublisher);
        Type[] dependencies = type
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .Concat(type.GetConstructors().SelectMany(constructor =>
                constructor.GetParameters().Select(parameter => parameter.ParameterType)))
            .ToArray();

        Assert.DoesNotContain(dependencies, dependency =>
            dependency.FullName?.Contains("DatReader", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(dependencies, dependency =>
            dependency.FullName?.Contains("StreamingRegion", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(dependencies, dependency =>
            dependency.FullName?.Contains("LiveWorldOrigin", StringComparison.Ordinal) == true);

        MethodInfo begin = Assert.Single(
            type.GetMethods(),
            method => method.Name == nameof(LandblockPhysicsPublisher.BeginPublication)
                && method.ReturnType == typeof(LandblockPhysicsPublication));
        ParameterInfo parameter = Assert.Single(begin.GetParameters());
        Assert.Equal(typeof(LandblockRenderPublication), parameter.ParameterType);
        Assert.DoesNotContain(type.GetConstructors().SelectMany(constructor =>
            constructor.GetParameters()), parameterInfo =>
                parameterInfo.ParameterType == typeof(PhysicsDataCache));
    }

    [Fact]
    public void GameWindow_HasNoLandblockPhysicsPublicationBodies()
    {
        IReadOnlyList<CompiledCall> windowCalls =
            CompiledCallGraph.ReadDeclared(typeof(GameWindow));
        AssertNoCall(windowCalls, typeof(PhysicsDataCache), nameof(PhysicsDataCache.CacheCellStruct));
        AssertNoCall(windowCalls, typeof(PhysicsDataCache), nameof(PhysicsDataCache.CacheBuilding));
        AssertNoCall(
            windowCalls,
            typeof(ShadowShapeBuilder),
            nameof(ShadowShapeBuilder.FromLandblockBspParts));
        AssertNoCall(
            windowCalls,
            typeof(ShadowObjectRegistry),
            nameof(ShadowObjectRegistry.RefloodLandblock));
        AssertNoCall(
            windowCalls,
            typeof(PhysicsEngine),
            "DemoteLandblockToTerrain");
        AssertNoCall(windowCalls, typeof(PhysicsEngine), "RemoveLandblock");

        IReadOnlyList<CompiledCall> publisherCalls =
            CompiledCallGraph.ReadDeclared(typeof(LandblockPhysicsPublisher));
        MethodInfo advance = typeof(LandblockPhysicsPublisher).GetMethod(
            "AdvanceBeginOne",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<CompiledCall> advanceCalls = CompiledCallGraph.Read(advance);
        int stagedCache = CompiledCallGraph.IndexOf(
            advanceCalls,
            typeof(LandblockPhysicsPublication),
            "get_StagingCache");
        int removeCells = CompiledCallGraph.IndexOf(
            advanceCalls,
            typeof(PhysicsDataCache),
            nameof(PhysicsDataCache.RemoveCellsForLandblock));
        int removeBuildings = CompiledCallGraph.IndexOf(
            advanceCalls,
            typeof(PhysicsDataCache),
            nameof(PhysicsDataCache.RemoveBuildingsForLandblock));
        int secondStagedCache = CompiledCallGraph.IndexOf(
            advanceCalls,
            typeof(LandblockPhysicsPublication),
            "get_StagingCache",
            removeCells + 1);
        Assert.True(stagedCache >= 0);
        Assert.True(stagedCache < removeCells);
        Assert.True(removeCells < secondStagedCache);
        Assert.True(secondStagedCache < removeBuildings);
        AssertNoCall(
            publisherCalls,
            typeof(ShadowObjectRegistry),
            nameof(ShadowObjectRegistry.RefloodLandblock));
        Assert.Contains(
            publisherCalls,
            call => call.Target.DeclaringType == typeof(RuntimePhysicsState)
                && call.Target.Name == "CommitCollisionGeneration");
        AssertNoCall(
            publisherCalls,
            typeof(RuntimePhysicsState),
            "RestartCollisionRetainedOwnerCapture");
    }

    private static void Publish(
        LandblockPhysicsPublisher publisher,
        LandblockBuild build)
    {
        LandblockPhysicsPublication receipt = Begin(publisher, build);
        publisher.CompletePublication(receipt);
    }

    private static LandblockPhysicsPublication Begin(
        LandblockPhysicsPublisher publisher,
        LandblockBuild build) =>
        publisher.BeginPublication(RenderReceipt(build));

    private static RuntimePlacementProjectionToken SeedRuntimePlacement(
        RuntimePhysicsState physics,
        RuntimeEntityRecord record,
        uint cell,
        Vector3 position)
    {
        Type coreAssemblyMarker = typeof(PhysicsEngine);
        Type requestType = coreAssemblyMarker.Assembly.GetType(
            "AcDream.Core.Physics.PhysicsSetPositionRequest",
            throwOnError: true)!;
        Type flagsType = coreAssemblyMarker.Assembly.GetType(
            "AcDream.Core.Physics.PhysicsSetPositionFlags",
            throwOnError: true)!;
        Type placementClassType = coreAssemblyMarker.Assembly.GetType(
            "AcDream.Core.Physics.PhysicsPlacementClass",
            throwOnError: true)!;
        object request = Activator.CreateInstance(
            requestType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                position,
                Quaternion.Identity,
                cell,
                position,
                ImmutableArray<FlatCollisionSphere>.Empty,
                1f,
                0.4f,
                0.4f,
                PhysicsStateFlags.None,
                ObjectInfoState.None,
                0u,
                Enum.ToObject(placementClassType, 0),
                Enum.ToObject(flagsType, 0x011u),
                Vector3.Zero,
                0f,
                0f,
                0u,
                cell,
            ],
            culture: null)!;

        Type runtimeAssemblyMarker = typeof(RuntimePhysicsState);
        Type commandType = runtimeAssemblyMarker.Assembly.GetType(
            "AcDream.Runtime.Physics.RuntimeSetPositionCommand",
            throwOnError: true)!;
        Type kindType = runtimeAssemblyMarker.Assembly.GetType(
            "AcDream.Runtime.Physics.RuntimeSetPositionOperationKind",
            throwOnError: true)!;
        object command = Activator.CreateInstance(
            commandType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                request,
                Enum.ToObject(kindType, 2),
                10d,
                0UL,
                0f,
                0f,
                default(RuntimePortalPlacementAuthority),
            ],
            culture: null)!;
        MethodInfo apply = physics.SetPosition.GetType().GetMethod(
            "Apply",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("Runtime SetPosition.Apply");
        object outcome = apply.Invoke(
            physics.SetPosition,
            [record, record.PositionAuthorityVersion, command])!;
        return (RuntimePlacementProjectionToken)(outcome.GetType().GetProperty(
            "Projection",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(outcome)
            ?? throw new MissingMemberException("Runtime placement projection"));
    }

    private static WorldSession.EntitySpawn RuntimeSpawn(
        uint guid,
        uint cell,
        Vector3 position)
    {
        var serverPosition = new CreateObject.ServerPosition(
            cell,
            position.X,
            position.Y,
            position.Z,
            1f,
            0f,
            0f,
            0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: 1);
        var spawnPhysics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.Gravity,
            Position: serverPosition,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            serverPosition,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "app-collision-publication-fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.Gravity,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: spawnPhysics);
    }

    private static LandblockRenderPublication RenderReceipt(LandblockBuild build)
    {
        var publisher = new LandblockRenderPublisher(
            publishTerrain: (_, _, _) => { },
            removeTerrain: _ => { },
            cellVisibility: new AcDream.App.Rendering.CellVisibility(),
            worldState: new GpuWorldState());
        return publisher.BeginPublication(
            build,
            new AcDream.Core.Terrain.LandblockMeshData(
                Array.Empty<AcDream.Core.Terrain.TerrainVertex>(),
                Array.Empty<uint>()));
    }

    private static (
        LandblockPhysicsPublisher Publisher,
        PhysicsEngine Engine,
        PhysicsDataCache Cache) Fixture()
    {
        var lifetime = new RuntimeEntityObjectLifetime(
            new PhysicsDataCache());
        PhysicsEngine engine = lifetime.Physics.Engine;
        PhysicsDataCache cache = lifetime.Physics.DataCache;
        return (
            new LandblockPhysicsPublisher(lifetime.Physics, HeightTable),
            engine,
            cache);
    }

    private static void CacheCylinderSetup(
        PhysicsDataCache cache,
        uint setupId = SetupId,
        int shapeCount = 1)
    {
        var setup = new Setup();
        for (int index = 0; index < shapeCount; index++)
        {
            setup.CylSpheres.Add(
                new CylSphere
                {
                    Radius = 0.4f + index * 0.01f,
                    Height = 1.2f,
                    Origin = new Vector3(index * 0.1f, 0f, 0.6f),
                });
        }
        cache.CacheSetup(setupId, setup);
    }

    private static GfxObjPhysics BspGfx(float radius) => new()
    {
        BSP = new PhysicsBSPTree
        {
            Root = new PhysicsBSPNode
            {
                Type = DatReaderWriter.Enums.BSPNodeType.Leaf,
            },
        },
        BoundingSphere = new Sphere
        {
            Origin = Vector3.Zero,
            Radius = radius,
        },
        PhysicsPolygons = new Dictionary<ushort, Polygon>(),
        Vertices = new VertexArray(),
        Resolved = new Dictionary<ushort, ResolvedPolygon>(),
    };

    [Fact]
    public void CompletePublication_NonCollidingStaticRegistersRenderOnly()
    {
        const uint gfxObjId = 0x01000079u;
        var fixture = Fixture();
        fixture.Cache.RegisterGfxObjForTest(gfxObjId, RenderOnlyGfx(radius: 0.8f));
        WorldEntity entity = new()
        {
            Id = 0x80A9B402u,
            SourceGfxObjOrSetupId = gfxObjId,
            Position = new Vector3(12f, 12f, 0f),
            Rotation = Quaternion.Identity,
            MeshRefs = [new MeshRef(gfxObjId, Matrix4x4.Identity)],
        };

        Publish(fixture.Publisher, Build(FirstLandblock, [entity]));

        ShadowObjectRegistry shadows = fixture.Engine.ShadowObjects;
        Assert.Empty(shadows.AllEntriesForDebug());
        Assert.True(shadows.TryGetRetailCellArray(entity.Id, out var cells));
        Assert.NotEmpty(cells);
        Assert.Equal(RetailCellArrayRoute.BoundingBox, shadows.GetRetailCellArrayRoute(entity.Id));
        int matching = 0;
        foreach (RetailPartEntry part in shadows.GetRetailPartEntriesInCell(cells[0]))
        {
            if (part.EntityId != entity.Id)
                continue;
            matching++;
            Assert.Equal(gfxObjId, part.GfxObjId);
        }
        Assert.Equal(1, matching);
        Assert.Equal(0, fixture.Publisher.Diagnostics.StaticBspOwnerCount);
        Assert.Equal(0, fixture.Publisher.Diagnostics.StaticCylinderOwnerCount);
    }

    /// <summary>A GfxObj with visual bounds and NO physics BSP — a purely
    /// decorative static part.</summary>
    private static GfxObjPhysics RenderOnlyGfx(float radius) => new()
    {
        BoundingSphere = new Sphere
        {
            Origin = Vector3.Zero,
            Radius = radius,
        },
        PhysicsPolygons = new Dictionary<ushort, Polygon>(),
        Vertices = new VertexArray(),
        Resolved = new Dictionary<ushort, ResolvedPolygon>(),
        VisualBounds = new FlatGfxObjVisualBounds(
            new Vector3(-radius),
            new Vector3(radius),
            Vector3.Zero,
            radius,
            new Vector3(radius)),
    };

    private static GfxObj PhysicsGfx() => new()
    {
        Flags = DatReaderWriter.Enums.GfxObjFlags.HasPhysics,
        PhysicsBSP = new PhysicsBSPTree
        {
            Root = new PhysicsBSPNode
            {
                Type = DatReaderWriter.Enums.BSPNodeType.Leaf,
                BoundingSphere = new Sphere
                {
                    Origin = Vector3.Zero,
                    Radius = 1.25f,
                },
            },
        },
        PhysicsPolygons = new Dictionary<ushort, Polygon>(),
        VertexArray = new VertexArray(),
    };

    private static PhysicsDatBundle CellPortalAndBuildingBundle(uint envCellId)
    {
        var cellStruct = new CellStruct
        {
            VertexArray = new VertexArray
            {
                Vertices = new Dictionary<ushort, SWVertex>
                {
                    [0] = new SWVertex { Origin = Vector3.Zero },
                    [1] = new SWVertex { Origin = Vector3.UnitY },
                    [2] = new SWVertex { Origin = Vector3.UnitZ },
                },
            },
            PhysicsPolygons = new Dictionary<ushort, Polygon>(),
            Polygons = new Dictionary<ushort, Polygon>
            {
                [0] = new Polygon { VertexIds = [0, 1, 2] },
            },
            CellBSP = new CellBSPTree
            {
                Root = new CellBSPNode
                {
                    Type = DatReaderWriter.Enums.BSPNodeType.Leaf,
                },
            },
        };
        var environment = new DatReaderWriter.DBObjs.Environment
        {
            Id = 0x0D000001u,
            Cells = { [1] = cellStruct },
        };
        var envCell = new EnvCell
        {
            Id = envCellId,
            EnvironmentId = 1,
            CellStructure = 1,
            Position = new Frame { Orientation = Quaternion.Identity },
            CellPortals =
            {
                new CellPortal
                {
                    PolygonId = 0,
                    OtherCellId = 0xFFFF,
                },
            },
        };
        var info = new LandBlockInfo
        {
            NumCells = 1,
            Buildings =
            {
                new BuildingInfo
                {
                    ModelId = 0x01000077u,
                    Frame = new Frame
                    {
                        Origin = new Vector3(12f, 12f, 0f),
                        Orientation = Quaternion.Identity,
                    },
                },
            },
        };
        return new PhysicsDatBundle(
            info,
            new Dictionary<uint, EnvCell> { [envCellId] = envCell },
            new Dictionary<uint, DatReaderWriter.DBObjs.Environment>
            {
                [environment.Id] = environment,
            },
            new Dictionary<uint, Setup>(),
            new Dictionary<uint, GfxObj>());
    }

    [Fact]
    public void ReloadedSeamAtomicallyRestoresAdjacentStaticAndClearsRepairMarker()
    {
        var fixture = Fixture();
        CacheCylinderSetup(fixture.Cache);
        WorldEntity neighbor = CylinderEntity(
            0x80AAB401u,
            new Vector3(192.25f, 12f, 0f));
        Publish(fixture.Publisher, Build(FirstLandblock));
        Publish(fixture.Publisher, Build(AdjacentLandblock, [neighbor]));
        Assert.True(fixture.Engine.ShadowObjects.HasOwnerRowsInLandblock(
            neighbor.Id,
            FirstLandblock));

        fixture.Publisher.RemoveLandblock(FirstLandblock);

        Assert.Equal(1, fixture.Engine.ShadowObjects.WithdrawnPrefixMarkerCount);
        Assert.False(fixture.Engine.ShadowObjects.HasOwnerRowsInLandblock(
            neighbor.Id,
            FirstLandblock));
        LandblockPhysicsPublication pending = Begin(
            fixture.Publisher,
            Build(FirstLandblock));
        Assert.Equal(1, fixture.Engine.ShadowObjects.WithdrawnPrefixMarkerCount);

        fixture.Publisher.CompletePublication(pending);

        Assert.Equal(0, fixture.Engine.ShadowObjects.WithdrawnPrefixMarkerCount);
        Assert.True(fixture.Engine.ShadowObjects.HasOwnerRowsInLandblock(
            neighbor.Id,
            FirstLandblock));
        Assert.True(fixture.Engine.ShadowObjects.HasOwnerRowsInLandblock(
            neighbor.Id,
            AdjacentLandblock));
        Assert.Equal(1, fixture.Engine.ShadowObjects.RetainedRegistrationCount);
    }

    private static LandblockCollisionBuild FlatCellClosure(
        PhysicsDatBundle bundle,
        uint envCellId)
    {
        EnvCell envCell = bundle.EnvCells[envCellId];
        DatReaderWriter.DBObjs.Environment environment =
            bundle.Environments.Values.Single();
        CellStruct cellStruct = environment.Cells[envCell.CellStructure];
        FlatCellStructureCollisionAsset flatCell =
            FlatCollisionAssetBuilder.FlattenCellStructure(cellStruct);
        FlatEnvCellTopology topology =
            FlatCollisionAssetBuilder.FlattenEnvCellTopology(
                envCellId,
                envCell,
                flatCell.PortalPolygons);
        return new LandblockCollisionBuild(
            ImmutableDictionary<uint, FlatGfxObjCollisionAsset>.Empty,
            ImmutableDictionary<uint, FlatSetupCollision>.Empty,
            ImmutableDictionary<uint, FlatCellStructureCollisionAsset>.Empty
                .Add(envCellId, flatCell),
            ImmutableDictionary<uint, FlatEnvCellTopology>.Empty
                .Add(envCellId, topology),
            [],
            [],
            [envCellId]);
    }

    private static WorldEntity CylinderEntity(
        uint id,
        Vector3 position,
        uint setupId = SetupId) => new()
    {
        Id = id,
        SourceGfxObjOrSetupId = setupId,
        Position = position,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };

    private static LandblockBuild Build(
        uint landblockId,
        IReadOnlyList<WorldEntity>? entities = null,
        PhysicsDatBundle? bundle = null,
        LandblockBuildOrigin? origin = null,
        LandblockCollisionBuild? collisions = null)
    {
        var landblockInfo = new LandBlockInfo();
        PhysicsDatBundle physics = bundle ?? new PhysicsDatBundle(
            landblockInfo,
            new Dictionary<uint, EnvCell>(),
            new Dictionary<uint, DatReaderWriter.DBObjs.Environment>(),
            new Dictionary<uint, Setup>(),
            new Dictionary<uint, GfxObj>());
        return new LandblockBuild(
            new LoadedLandblock(
                landblockId,
                FlatHeightmap(),
                entities ?? Array.Empty<WorldEntity>(),
                physics),
            Origin: origin ?? new LandblockBuildOrigin(0xA9, 0xB4),
            Collisions: collisions);
    }

    private static LandBlock FlatHeightmap() => new()
    {
        Terrain = new TerrainInfo[81],
        Height = new byte[81],
    };

    private static void AssertNoCall(
        IReadOnlyList<CompiledCall> calls,
        Type declaringType,
        string methodName)
    {
        Assert.DoesNotContain(
            calls,
            call => call.Target.DeclaringType == declaringType
                && call.Target.Name == methodName);
    }
}
