using System.Numerics;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Physics;

public sealed class ProjectileControllerTests
{
    private const uint Guid = 0x7000A001u;
    private const uint CellA = 0x01010001u;
    private const uint CellB = 0x01020001u;
    private static readonly PhysicsStateFlags MissileState =
        PhysicsStateFlags.ReportCollisions
        | PhysicsStateFlags.Missile
        | PhysicsStateFlags.AlignPath
        | PhysicsStateFlags.PathClipped;

    private sealed class RecordingResources : ILiveEntityResourceLifecycle
    {
        public int Registers { get; private set; }
        public int Unregisters { get; private set; }
        public void Register(WorldEntity entity) => Registers++;
        public void Unregister(WorldEntity entity) => Unregisters++;
    }

    [Fact]
    public void Tick_MovesCanonicalProjectionWithoutRecreatingResources()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        WorldEntity entity = record.WorldEntity!;
        uint localId = entity.Id;

        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 10.0));
        fixture.Controller.Tick(10.1, liveCenterX: 1, liveCenterY: 1, playerWorldPosition: null);

        Assert.Same(entity, record.WorldEntity);
        Assert.Equal(localId, entity.Id);
        Assert.True(entity.Position.X > 10f);
        Assert.Equal(1, fixture.Resources.Registers);
        Assert.Equal(0, fixture.Resources.Unregisters);
        Assert.Equal(1, fixture.Controller.Count);
        Assert.Same(record.ProjectileRuntime!.Body, record.PhysicsBody);
    }

    [Fact]
    public void Hidden_PausesProjectileWithoutClearingActiveOrReplayingClockBacklog()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        WorldEntity entity = record.WorldEntity!;
        fixture.Engine.ShadowObjects.Register(
            entity.Id,
            entity.SourceGfxObjOrSetupId,
            entity.Position,
            entity.Rotation,
            radius: 0.5f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: 0x01010000u,
            state: (uint)MissileState,
            seedCellId: CellA);
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0, 1, 1));
        PhysicsBody body = record.PhysicsBody!;
        Assert.True(body.IsActive);

        record.FinalPhysicsState = MissileState
            | PhysicsStateFlags.Hidden
            | PhysicsStateFlags.IgnoreCollisions;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, record.FinalPhysicsState, 1.1, 1, 1));
        Vector3 hiddenPosition = entity.Position;
        fixture.Controller.Tick(5.0, 1, 1, playerWorldPosition: null);

        Assert.Equal(hiddenPosition, entity.Position);
        Assert.True(body.InWorld);
        Assert.True(body.IsActive);
        Assert.Equal(0.0, record.ObjectClock.PendingSeconds, 8);
        Assert.Equal(0, fixture.Engine.ShadowObjects.TotalRegistered);

        record.FinalPhysicsState = MissileState;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, record.FinalPhysicsState, 5.0, 1, 1));
        fixture.Controller.Tick(5.1, 1, 1, playerWorldPosition: null);

        Assert.True(entity.Position.X > hiddenPosition.X);
        Assert.True(entity.Position.X < hiddenPosition.X + 2f);
        Assert.Equal(1, fixture.Engine.ShadowObjects.TotalRegistered);
    }

    [Fact]
    public void HiddenSharedRemoteProjectile_UsesPositionManagerNarrowTickExactlyOnce()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        WorldEntity entity = record.WorldEntity!;
        Assert.Null(record.AnimationRuntime);
        var remote =
            fixture.Live.GetOrCreateRemoteMotionRuntime(Guid);
        remote.Body.SnapToCell(CellA, entity.Position, entity.Position);
        remote.Body.Orientation = entity.Rotation;
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0, 1, 1));
        Assert.Same(remote.Body, record.ProjectileRuntime!.Body);

        record.FinalPhysicsState = MissileState
            | PhysicsStateFlags.Hidden
            | PhysicsStateFlags.IgnoreCollisions;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, record.FinalPhysicsState, 1.1, 1, 1));
        remote.Interp.Enqueue(
            entity.Position + Vector3.UnitX,
            heading: 0f,
            isMovingTo: false,
            currentBodyPosition: remote.Body.Position);
        var updater = new RemotePhysicsUpdater(
            fixture.Live.Physics,
            (_, _) => (0.48f, 1.835f),
            (_, _) => (System.Collections.Immutable.ImmutableArray<FlatCollisionSphere>.Empty, 1f, 0.4f, 0.4f),
            (_, _, _, _) => { });

        int publishedRoots = 0;
        updater.TickHiddenEntities(
            fixture.Live,
            localPlayerServerGuid: 0x50000001u,
            dt: 0.1f,
            _ => publishedRoots++);
        Vector3 afterNarrowTick = entity.Position;
        fixture.Controller.Tick(1.2, 1, 1, playerWorldPosition: null);

        Assert.True(afterNarrowTick.X > 10f);
        Assert.Equal(afterNarrowTick, entity.Position);
        Assert.Equal(1, publishedRoots);
        Assert.True(fixture.Controller.HandlesMovement(Guid));
    }

    [Fact]
    public void PendingDestinationAndLaterHydration_RetainIdentityAndProjectileOwner()
    {
        var fixture = new Fixture(loadInitialLandblock: true);
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        WorldEntity entity = record.WorldEntity!;
        fixture.Engine.ShadowObjects.Register(
            entity.Id,
            entity.SourceGfxObjOrSetupId,
            entity.Position,
            entity.Rotation,
            radius: 0.5f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: 0x01010000u,
            state: (uint)MissileState,
            seedCellId: CellA,
            isStatic: false);
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 2.0));
        IRuntimeProjectile runtime = record.ProjectileRuntime!;

        runtime.Body.SnapToCell(
            CellB,
            new Vector3(10f, 202f, 5f),
            new Vector3(10f, 10f, 5f));
        runtime.Body.Orientation = Quaternion.Identity;
        Assert.True(fixture.Live.RebucketLiveEntity(Guid, CellB));
        Assert.True(fixture.Controller.SyncPresentationFromResolvedBody(record, 2.1));

        Assert.False(record.IsSpatiallyVisible);
        Assert.True(record.IsSpatiallyProjected);
        Assert.Same(entity, record.WorldEntity);
        Assert.Same(runtime, record.ProjectileRuntime);

        Vector3 pendingPosition = entity.Position;
        fixture.Controller.Tick(
            2.4,
            liveCenterX: 1,
            liveCenterY: 1,
            playerWorldPosition: null);
        Assert.Equal(pendingPosition, entity.Position);
        Assert.False(runtime.Body.InWorld);
        Assert.False(runtime.Body.IsActive);

        fixture.Spatial.AddLandblock(EmptyLandblock(0x0102FFFFu));

        Assert.True(record.IsSpatiallyVisible);
        Assert.True(runtime.Body.InWorld);
        Assert.True(runtime.Body.IsActive);
        Assert.Equal(pendingPosition, entity.Position);
        Assert.Equal(1, fixture.Engine.ShadowObjects.TotalRegistered);
        ShadowEntry restoredShadow = Assert.Single(
            fixture.Engine.ShadowObjects.AllEntriesForDebug());
        Assert.Equal(entity.Id, restoredShadow.EntityId);
        Assert.Equal(pendingPosition, restoredShadow.Position);

        fixture.Controller.Tick(
            2.5,
            liveCenterX: 1,
            liveCenterY: 1,
            playerWorldPosition: pendingPosition);

        Assert.True(record.IsSpatiallyVisible);
        Assert.Same(entity, record.WorldEntity);
        Assert.Same(runtime, record.ProjectileRuntime);
        Assert.Equal(1, fixture.Resources.Registers);
        Vector3 firstReentryTick = entity.Position;
        Assert.True(firstReentryTick.X > pendingPosition.X);

        fixture.Controller.Tick(
            2.6,
            liveCenterX: 1,
            liveCenterY: 1,
            playerWorldPosition: pendingPosition);
        Assert.True(entity.Position.X > firstReentryTick.X);
    }

    [Fact]
    public void LandblockUnload_SuspendsProjectileAtVisibilityEdgeWithoutFrameScan()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        WorldEntity entity = record.WorldEntity!;
        fixture.Engine.ShadowObjects.Register(
            entity.Id,
            entity.SourceGfxObjOrSetupId,
            entity.Position,
            entity.Rotation,
            radius: 0.5f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: 0x01010000u,
            state: (uint)MissileState,
            seedCellId: CellA);
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0, 1, 1));
        Assert.True(record.PhysicsBody!.InWorld);
        Assert.True(record.PhysicsBody.IsActive);
        Assert.Equal(1, fixture.Live.SpatialProjectileRuntimeCount);

        fixture.Spatial.RemoveLandblock(0x0101FFFFu);

        Assert.False(record.IsSpatiallyVisible);
        Assert.Equal(0, fixture.Live.SpatialProjectileRuntimeCount);
        Assert.False(record.PhysicsBody.InWorld);
        Assert.False(record.PhysicsBody.IsActive);
        Assert.Equal(0, fixture.Engine.ShadowObjects.TotalRegistered);

        fixture.Spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        Assert.Equal(1, fixture.Live.SpatialProjectileRuntimeCount);
        Assert.True(record.PhysicsBody.InWorld);
        Assert.True(record.PhysicsBody.IsActive);
        Assert.Equal(1, fixture.Engine.ShadowObjects.TotalRegistered);

        fixture.Controller.Tick(
            1.1,
            1,
            1,
            playerWorldPosition: record.WorldEntity!.Position);

        Assert.True(record.PhysicsBody.InWorld);
        Assert.True(record.PhysicsBody.IsActive);
        Assert.Equal(1, fixture.Engine.ShadowObjects.TotalRegistered);
    }

    [Fact]
    public void LoadedLandblockCrossing_RebucketsSameProjection()
    {
        const uint startCell = 0xA9B40039u;
        const uint destinationCell = 0xAAB40001u;
        var fixture = new Fixture(loadInitialLandblock: false, BuildBoundaryEngine());
        fixture.Spatial.AddLandblock(EmptyLandblock(0xA9B4FFFFu));
        fixture.Spatial.AddLandblock(EmptyLandblock(0xAAB4FFFFu));
        LiveEntityRecord record = fixture.Spawn(
            instance: 1,
            cellId: startCell,
            worldPosition: new Vector3(191f, 10f, 50f),
            cellLocalPosition: new Vector3(191f, 10f, 50f),
            velocity: new Vector3(40f, 0f, 0f));
        WorldEntity entity = record.WorldEntity!;
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(radius: 0.5f), 0.0));

        fixture.Controller.Tick(
            0.05,
            liveCenterX: 0xA9,
            liveCenterY: 0xB4,
            playerWorldPosition: null);

        Assert.Equal(destinationCell, record.FullCellId);
        Assert.Equal(destinationCell, entity.ParentCellId);
        Assert.Equal(193f, entity.Position.X, 3);
        Assert.Same(entity, record.WorldEntity);
        Assert.Equal(1, fixture.Resources.Registers);
    }

    [Fact]
    public void PredictedCrossingIntoPendingLandblock_SuspendsInSameTick()
    {
        const uint startCell = 0xA9B40039u;
        var fixture = new Fixture(loadInitialLandblock: false, BuildBoundaryEngine());
        fixture.Spatial.AddLandblock(EmptyLandblock(0xA9B4FFFFu));
        LiveEntityRecord record = fixture.Spawn(
            instance: 1,
            cellId: startCell,
            worldPosition: new Vector3(191f, 10f, 50f),
            cellLocalPosition: new Vector3(191f, 10f, 50f),
            velocity: new Vector3(40f, 0f, 0f));
        WorldEntity entity = record.WorldEntity!;
        fixture.Engine.ShadowObjects.Register(
            entity.Id,
            entity.SourceGfxObjOrSetupId,
            entity.Position,
            entity.Rotation,
            radius: 0.5f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: 0xA9B4FFFFu,
            seedCellId: startCell,
            isStatic: false);
        Assert.True(fixture.Controller.TryBind(
            record,
            ProjectileSetup(radius: 0.5f),
            0.0,
            liveCenterX: 0xA9,
            liveCenterY: 0xB4));

        fixture.Controller.Tick(
            0.05,
            liveCenterX: 0xA9,
            liveCenterY: 0xB4,
            playerWorldPosition: null);

        Assert.Equal(0xAAB40001u, record.FullCellId);
        Assert.Equal(0xAAB40001u, record.PhysicsBody!.CellPosition.ObjCellId);
        Assert.Equal(record.PhysicsBody.Position, entity.Position);
        Assert.False(record.IsSpatiallyVisible);
        Assert.True(record.IsSpatiallyProjected);
        Assert.False(record.PhysicsBody.InWorld);
        Assert.False(record.PhysicsBody.IsActive);
        Assert.Empty(fixture.Engine.ShadowObjects.GetObjectsInCell(startCell));
        Assert.Empty(fixture.Engine.ShadowObjects.GetObjectsInCell(0xAAB40001u));
        Assert.Same(entity, record.WorldEntity);
        Assert.Equal(1, fixture.Controller.Count);

        record.FinalPhysicsState = PhysicsStateFlags.ReportCollisions;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, record.FinalPhysicsState, 0.06, 0xA9, 0xB4));
        fixture.Spatial.AddLandblock(EmptyLandblock(0xAAB4FFFFu));
        fixture.Controller.Tick(0.07, 0xA9, 0xB4, playerWorldPosition: null);

        Assert.True(record.IsSpatiallyVisible);
        Assert.Equal(0xAAB40001u, record.PhysicsBody.CellPosition.ObjCellId);
        Assert.True(record.PhysicsBody.InWorld);
        Assert.True(record.PhysicsBody.IsActive);
        Assert.Contains(
            fixture.Engine.ShadowObjects.GetObjectsInCell(0xAAB40001u),
            entry => entry.EntityId == entity.Id);
    }

    [Fact]
    public void FreshVectorCorrectionsMutateTheCanonicalBody()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 3);
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 4.0));
        PhysicsBody body = record.PhysicsBody!;

        Assert.True(fixture.Controller.ApplyAuthoritativeVector(
            record,
            new Vector3(7f, 8f, 9f),
            new Vector3(0f, 0f, 2f),
            currentTime: 4.5));
        Assert.Equal(new Vector3(7f, 8f, 9f), body.Velocity);
        Assert.Equal(new Vector3(0f, 0f, 2f), body.Omega);
    }

    [Theory]
    [InlineData(AuthoritativeMutation.Vector)]
    [InlineData(AuthoritativeMutation.State)]
    public void AuthoritativeMutationBetweenQuantumHalvesDiscardsPrediction(
        AuthoritativeMutation mutation)
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0, 1, 1));
        PhysicsBody body = record.PhysicsBody!;
        Assert.True(fixture.Controller.TryBeginQuantum(
            record,
            quantum: 0.05f,
            out ProjectileController.QuantumStep step));

        Vector3 correctedVelocity = new(7f, 8f, 9f);
        Vector3 correctedOmega = new(0f, 0f, 2f);
        PhysicsStateFlags correctedState = MissileState | PhysicsStateFlags.Gravity;
        switch (mutation)
        {
            case AuthoritativeMutation.Vector:
                Assert.True(fixture.Controller.ApplyAuthoritativeVector(
                    record,
                    correctedVelocity,
                    correctedOmega,
                    currentTime: 1.02));
                break;

            case AuthoritativeMutation.State:
                record.FinalPhysicsState = correctedState;
                Assert.True(fixture.Controller.ApplyAuthoritativeState(
                    record,
                    correctedState,
                    currentTime: 1.02,
                    liveCenterX: 1,
                    liveCenterY: 1));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.False(fixture.Controller.CompleteQuantum(step, 1, 1));
        switch (mutation)
        {
            case AuthoritativeMutation.Vector:
                Assert.Equal(correctedVelocity, body.Velocity);
                Assert.Equal(correctedOmega, body.Omega);
                break;
            case AuthoritativeMutation.State:
                Assert.Equal(correctedState, body.State);
                break;
        }
    }

    [Fact]
    public void StaleVectorGateCannotOverwritePredictedBody()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 5, vectorSequence: 10);
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0));
        PhysicsBody body = record.PhysicsBody!;
        Vector3 original = body.Velocity;

        var stale = new VectorUpdate.Parsed(
            Guid,
            new Vector3(99f, 0f, 0f),
            Vector3.Zero,
            InstanceSequence: 5,
            VectorSequence: 9);

        Assert.False(fixture.Live.TryApplyVector(stale, out _));
        Assert.Equal(original, body.Velocity);
    }

    [Fact]
    public void MissileStateRemovalAndReaddition_ReclassifiesWithoutReplacingEntity()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0));
        Vector3 before = record.WorldEntity!.Position;

        IRuntimeProjectile runtime = record.ProjectileRuntime!;
        PhysicsBody body = runtime.Body;
        record.FinalPhysicsState = PhysicsStateFlags.ReportCollisions;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record,
            PhysicsStateFlags.ReportCollisions,
            currentTime: 1.1,
            liveCenterX: 1,
            liveCenterY: 1));
        fixture.Controller.Tick(
            1.2,
            liveCenterX: 1,
            liveCenterY: 1,
            playerWorldPosition: null);

        Assert.Same(runtime, record.ProjectileRuntime);
        Assert.Same(body, record.PhysicsBody);
        Assert.True(body.IsActive);
        Assert.Equal(1, fixture.Controller.Count);
        Assert.True(record.WorldEntity.Position.X > before.X);

        record.FinalPhysicsState = MissileState;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record,
            MissileState,
            currentTime: 1.3,
            liveCenterX: 1,
            liveCenterY: 1));
        Assert.Same(runtime, record.ProjectileRuntime);
        Assert.Same(body, record.PhysicsBody);
        Assert.True(body.IsActive); // State preserves an already-active body.
        Assert.True(fixture.Live.TryGetWorldEntity(Guid, out WorldEntity entity));
        Assert.Same(record.WorldEntity, entity);
        Assert.Equal(1, fixture.Resources.Registers);
    }

    [Fact]
    public void StateReclassificationAfterPredictedCrossing_KeepsCurrentCellAndBody()
    {
        const uint startCell = 0xA9B40039u;
        const uint destinationCell = 0xAAB40001u;
        var fixture = new Fixture(loadInitialLandblock: false, BuildBoundaryEngine());
        fixture.Spatial.AddLandblock(EmptyLandblock(0xA9B4FFFFu));
        fixture.Spatial.AddLandblock(EmptyLandblock(0xAAB4FFFFu));
        LiveEntityRecord record = fixture.Spawn(
            instance: 1,
            cellId: startCell,
            worldPosition: new Vector3(191f, 10f, 50f),
            cellLocalPosition: new Vector3(191f, 10f, 50f),
            velocity: new Vector3(40f, 0f, 0f));
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(0.5f), 0.0));
        IRuntimeProjectile runtime = record.ProjectileRuntime!;

        fixture.Controller.Tick(0.05, 0xA9, 0xB4, playerWorldPosition: null);
        Assert.Equal(destinationCell, record.FullCellId);
        Assert.NotEqual(record.Snapshot.Position!.Value.LandblockId, record.FullCellId);

        Assert.True(fixture.Live.TryApplyState(
            new SetState.Parsed(
                Guid,
                (uint)PhysicsStateFlags.ReportCollisions,
                InstanceSequence: 1,
                StateSequence: 2),
            out _));
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, record.FinalPhysicsState, 0.06, 0xA9, 0xB4));
        Assert.Equal(destinationCell, record.FullCellId);
        Assert.True(fixture.Live.TryApplyState(
            new SetState.Parsed(
                Guid,
                (uint)MissileState,
                InstanceSequence: 1,
                StateSequence: 3),
            out _));
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, MissileState, 0.07, 0xA9, 0xB4));

        Assert.Same(runtime, record.ProjectileRuntime);
        Assert.Same(runtime.Body, record.PhysicsBody);
        Assert.Equal(destinationCell, record.FullCellId);
        Assert.Equal(destinationCell, record.WorldEntity!.ParentCellId);
    }

    [Fact]
    public void FreshVector_ClampsImmediatelyAndRestartsInactiveClock()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0));
        PhysicsBody body = record.PhysicsBody!;
        body.TransientState &= ~TransientStateFlags.Active;
        body.LastUpdateTime = 1.0;

        Assert.True(fixture.Controller.ApplyAuthoritativeVector(
            record,
            new Vector3(100f, 0f, 0f),
            Vector3.Zero,
            currentTime: 20.0));

        Assert.Equal(50f, body.Velocity.Length(), 4);
        Assert.Equal(20.0, body.LastUpdateTime, 8);
        fixture.Controller.Tick(
            20.1,
            liveCenterX: 1,
            liveCenterY: 1,
            playerWorldPosition: null);
        Assert.Equal(15f, record.WorldEntity!.Position.X, 3);
    }

    [Fact]
    public void StateOnlyUpdate_DoesNotRestartStoppedProjectile()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0));
        PhysicsBody body = record.PhysicsBody!;
        body.TransientState &= ~TransientStateFlags.Active;

        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record,
            MissileState | PhysicsStateFlags.Inelastic,
            currentTime: 2.0,
            liveCenterX: 1,
            liveCenterY: 1));

        Assert.False(body.IsActive);

        record.FinalPhysicsState = PhysicsStateFlags.ReportCollisions;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, record.FinalPhysicsState, 2.1, 1, 1));
        Assert.False(body.IsActive);
        record.FinalPhysicsState = MissileState;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, MissileState, 2.2, 1, 1));
        Assert.False(body.IsActive);
    }

    [Fact]
    public void VectorAfterMissileRemoval_StillMutatesRetainedRetailBody()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0));
        PhysicsBody body = record.PhysicsBody!;
        record.FinalPhysicsState = PhysicsStateFlags.ReportCollisions;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, record.FinalPhysicsState, 1.01, 1, 1));

        Assert.True(fixture.Controller.ApplyAuthoritativeVector(
            record,
            new Vector3(100f, 0f, 0f),
            new Vector3(0f, 0f, 3f),
            1.02));

        Assert.Same(body, record.PhysicsBody);
        Assert.Equal(50f, body.Velocity.Length(), 4);
        Assert.Equal(new Vector3(0f, 0f, 3f), body.Omega);

        Vector3 before = body.Position;
        fixture.Controller.Tick(1.1, 1, 1, playerWorldPosition: null);
        Assert.True(body.Position.X > before.X);
        Assert.Equal(body.Position, record.WorldEntity!.Position);
    }

    [Fact]
    public void VectorAfterMissileRemoval_WithRemoteMotionDefersToOrdinaryFunnel()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0));
        PhysicsBody body = record.PhysicsBody!;
        Assert.Same(
            body,
            fixture.Live.GetOrCreateRemoteMotionRuntime(Guid).Body);
        record.FinalPhysicsState = PhysicsStateFlags.ReportCollisions;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, record.FinalPhysicsState, 2.0, 1, 1));
        Vector3 before = body.Velocity;

        Assert.False(fixture.Controller.ApplyAuthoritativeVector(
            record,
            new Vector3(0f, 0f, 12f),
            new Vector3(0f, 0f, 2f),
            3.0));

        Assert.Equal(before, body.Velocity);
        Assert.Same(body, record.RemoteMotionRuntime!.Body);
    }

    [Fact]
    public void MissileReacquisition_AlignsClockWithoutStateOnlyMotion()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0));
        PhysicsBody body = record.PhysicsBody!;
        Vector3 position = body.Position;
        Assert.Same(
            body,
            fixture.Live.GetOrCreateRemoteMotionRuntime(Guid).Body);

        record.FinalPhysicsState = PhysicsStateFlags.ReportCollisions;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, record.FinalPhysicsState, 2.0, 1, 1));
        body.TransientState |= TransientStateFlags.Active;
        body.LastUpdateTime = 9.5;
        body.set_velocity(new Vector3(40f, 0f, 0f));

        record.FinalPhysicsState = MissileState;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, MissileState, 10.0, 1, 1));
        Assert.Equal(10.0, body.LastUpdateTime, 8);
        Assert.True(body.IsActive);

        fixture.Controller.Tick(10.1, 1, 1, playerWorldPosition: null);
        Assert.Equal(position.X + 4f, body.Position.X, 3);
        Assert.Equal(body.Position, record.WorldEntity!.Position);
    }

    [Fact]
    public void MissileReacquisition_AppliesStateWithoutPoisoningClock()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0));
        PhysicsBody body = record.PhysicsBody!;
        record.FinalPhysicsState = PhysicsStateFlags.ReportCollisions;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, record.FinalPhysicsState, 2.0, 1, 1));
        PhysicsStateFlags stateBefore = body.State;
        double clockBefore = body.LastUpdateTime;

        record.FinalPhysicsState = MissileState;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, MissileState, double.NaN, 1, 1));

        Assert.Equal(record.FinalPhysicsState, body.State);
        Assert.NotEqual(stateBefore, body.State);
        Assert.NotEqual(clockBefore, body.LastUpdateTime);
        Assert.Equal(2.0, body.LastUpdateTime, 8);
        Assert.True(double.IsFinite(body.LastUpdateTime));
        Assert.True(body.IsActive);
    }

    [Fact]
    public void Tick_PublishesMovedRootToEffectPoseSource()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        WorldEntity entity = record.WorldEntity!;
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0));
        Assert.True(fixture.Poses.TryGetRootPose(entity.Id, out Matrix4x4 before));

        fixture.Controller.Tick(
            1.1,
            liveCenterX: 1,
            liveCenterY: 1,
            playerWorldPosition: null);

        Assert.True(fixture.Poses.TryGetRootPose(entity.Id, out Matrix4x4 after));
        Assert.NotEqual(before.Translation, after.Translation);
        Assert.Equal(entity.Position, after.Translation);
    }

    [Fact]
    public void RootPoseCallbackGuidReuse_MakesQuantumReportSuperseded()
    {
        Fixture? fixture = null;
        LiveEntityRecord? first = null;
        LiveEntityRecord? replacement = null;
        bool replaced = false;
        fixture = new Fixture(
            publishRootPose: _ =>
            {
                if (replaced)
                    return;
                replaced = true;
                Assert.NotNull(first);
                Assert.True(fixture!.Live.UnregisterLiveEntity(
                    new DeleteObject.Parsed(Guid, first!.Generation),
                    isLocalPlayer: false));
                replacement = fixture.Spawn(instance: 2);
            });
        first = fixture.Spawn(instance: 1);
        Assert.True(fixture.Controller.TryBind(first, ProjectileSetup(), 1.0, 1, 1));

        Assert.False(fixture.Controller.AdvanceQuantum(
            first,
            quantum: 0.05f,
            liveCenterX: 1,
            liveCenterY: 1));

        Assert.True(replaced);
        Assert.NotNull(replacement);
        Assert.True(fixture.Live.TryGetRecord(Guid, out LiveEntityRecord current));
        Assert.Same(replacement, current);
        Assert.Null(current.ProjectileRuntime);
    }

    [Fact]
    public void Tick_PartArrayUsesRetailNinetySixUnitActivationBoundary()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1, hasRenderablePart: true);
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0));

        fixture.Controller.Tick(
            1.1,
            liveCenterX: 1,
            liveCenterY: 1,
            playerWorldPosition: new Vector3(106f, 10f, 5f));
        Assert.Equal(11f, record.WorldEntity!.Position.X, 3);

        fixture.Controller.Tick(
            1.2,
            liveCenterX: 1,
            liveCenterY: 1,
            playerWorldPosition: new Vector3(107.1f, 10f, 5f));
        Assert.Equal(11f, record.WorldEntity.Position.X, 3);
        Assert.False(record.PhysicsBody!.IsActive);
    }

    [Fact]
    public void Bind_UsesCanonicalOutdoorCellAndRehydrationCellTracksPrediction()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(
            instance: 1,
            cellId: CellA,
            worldPosition: new Vector3(30f, 10f, 5f),
            cellLocalPosition: new Vector3(30f, 10f, 5f));

        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0));

        Assert.Equal(record.PhysicsBody!.CellPosition.ObjCellId, record.FullCellId);
        Assert.Equal(record.FullCellId, record.WorldEntity!.ParentCellId);
        Assert.Equal(record.FullCellId, record.ProjectionCellId);
        Assert.NotEqual(record.Snapshot.Position!.Value.LandblockId, record.FullCellId);
    }

    [Fact]
    public void MalformedFreshVectorUpdate_DoesNotPoisonCanonicalBody()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0));
        PhysicsBody body = record.PhysicsBody!;
        Vector3 velocity = body.Velocity;

        Assert.True(fixture.Controller.ApplyAuthoritativeVector(
            record,
            new Vector3(float.NaN, 0f, 0f),
            Vector3.Zero,
            currentTime: 2.0));
        Assert.Equal(velocity, body.Velocity);
    }

    [Fact]
    public void MalformedPayloadGate_RejectsBeforeAuthoritativeSnapshotAdvances()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1, vectorSequence: 4);
        record.FinalPhysicsState = PhysicsStateFlags.ReportCollisions;
        WorldSession.EntitySpawn before = record.Snapshot;

        Assert.False(fixture.Controller.CanAcceptVectorPayload(
            Guid,
            new Vector3(float.NaN, 0f, 0f),
            Vector3.Zero));
        Assert.False(fixture.Controller.CanAcceptPositionPayload(
            Guid,
            new CreateObject.ServerPosition(
                CellA,
                float.PositiveInfinity,
                0f,
                0f,
                1f,
                0f,
                0f,
                0f),
            velocity: null));

        var malformedPosition = new WorldSession.EntityPositionUpdate(
            Guid,
            new CreateObject.ServerPosition(
                CellA,
                20f,
                20f,
                5f,
                1f,
                0f,
                0f,
                0f),
            Velocity: new Vector3(float.NaN, 0f, 0f),
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: 2,
            TeleportSequence: 0,
            ForcePositionSequence: 0);
        Assert.False(fixture.Controller.CanAcceptPositionPayload(
            Guid,
            malformedPosition.Position,
            malformedPosition.Velocity));

        Assert.Equal(before, record.Snapshot);
        Assert.Equal((ushort)1, record.Snapshot.Physics!.Value.Timestamps.Position);
        Assert.Equal((ushort)4, record.Snapshot.Physics!.Value.Timestamps.Vector);

        record.FinalPhysicsState = MissileState;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, MissileState, 2.0, 1, 1));
        Assert.NotNull(record.ProjectileRuntime);
        Assert.True(float.IsFinite(record.PhysicsBody!.Velocity.X));
        Assert.True(float.IsFinite(record.PhysicsBody.Velocity.Y));
        Assert.True(float.IsFinite(record.PhysicsBody.Velocity.Z));
    }

    [Fact]
    public void StateUpdate_AlwaysMutatesExistingCanonicalBodyBeforeProjectileAcquisition()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        record.FinalPhysicsState = PhysicsStateFlags.ReportCollisions;
        var remote =
            fixture.Live.GetOrCreateRemoteMotionRuntime(Guid);

        PhysicsStateFlags ordinaryState =
            PhysicsStateFlags.Gravity | PhysicsStateFlags.IgnoreCollisions;
        record.FinalPhysicsState = ordinaryState;
        Assert.False(fixture.Controller.ApplyAuthoritativeState(
            record, ordinaryState, 1.0, 1, 1));

        Assert.Equal(ordinaryState, remote.Body.State);
        Assert.Null(record.ProjectileRuntime);

        fixture.ResolvedSetup = new Setup();
        PhysicsStateFlags unsupportedMissile =
            ordinaryState | PhysicsStateFlags.Missile;
        record.FinalPhysicsState = unsupportedMissile;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, unsupportedMissile, 1.1, 1, 1));

        Assert.Equal(unsupportedMissile, remote.Body.State);
        Assert.Null(record.ProjectileRuntime);
        Assert.Same(remote.Body, record.PhysicsBody);
    }

    [Fact]
    public void FirstMissileStateWithInvalidLocalClock_RetainsClassificationAndBindsOnce()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        record.FinalPhysicsState = PhysicsStateFlags.ReportCollisions;

        record.FinalPhysicsState = MissileState;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, MissileState, double.NaN, 1, 1));

        Assert.NotNull(record.ProjectileRuntime);
        Assert.Equal(MissileState, record.PhysicsBody!.State);
        Assert.True(double.IsFinite(record.PhysicsBody.LastUpdateTime));

        IRuntimeProjectile runtime = record.ProjectileRuntime!;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, MissileState, 0.1, 1, 1));
        Assert.Same(runtime, record.ProjectileRuntime);
    }

    [Fact]
    public void FirstRemoteMissileStateWithInvalidLocalClock_AdoptsExistingBody()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        record.FinalPhysicsState = PhysicsStateFlags.ReportCollisions;
        var remote =
            fixture.Live.GetOrCreateRemoteMotionRuntime(Guid);
        WorldEntity entity = record.WorldEntity!;
        remote.Body.SnapToCell(CellA, entity.Position, entity.Position);

        record.FinalPhysicsState = MissileState;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, MissileState, double.PositiveInfinity, 1, 1));

        Assert.NotNull(record.ProjectileRuntime);
        Assert.Same(remote.Body, record.ProjectileRuntime!.Body);
        Assert.Equal(MissileState, remote.Body.State);
        Assert.True(double.IsFinite(remote.Body.LastUpdateTime));
    }

    [Fact]
    public void Bind_AppliesRetailFrictionValidationAndElasticityClamp()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(
            instance: 1,
            friction: -0.1f,
            elasticity: 2f);

        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0));

        Assert.Equal(PhysicsBody.DefaultFriction, record.PhysicsBody!.Friction);
        Assert.Equal(0.1f, record.PhysicsBody.Elasticity);
    }

    [Fact]
    public void Bind_AcceptsRetailZeroFrictionBoundary()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(
            instance: 1,
            friction: 0f,
            elasticity: 0f);

        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0));

        Assert.Equal(0f, record.PhysicsBody!.Friction);
        Assert.Equal(0f, record.PhysicsBody.Elasticity);
    }

    [Fact]
    public void RemoteMotionFirst_StateAcquisitionPreservesCurrentBodyAndCanonicalCell()
    {
        const uint startCell = 0xA9B40039u;
        const uint destinationCell = 0xAAB40001u;
        var fixture = new Fixture(loadInitialLandblock: false, BuildBoundaryEngine());
        fixture.Spatial.AddLandblock(EmptyLandblock(0xA9B4FFFFu));
        fixture.Spatial.AddLandblock(EmptyLandblock(0xAAB4FFFFu));
        LiveEntityRecord record = fixture.Spawn(
            instance: 1,
            cellId: startCell,
            worldPosition: new Vector3(191f, 10f, 50f),
            cellLocalPosition: new Vector3(191f, 10f, 50f),
            velocity: new Vector3(40f, 0f, 0f),
            angularVelocity: new Vector3(0f, 0f, 2f));
        WorldEntity entity = record.WorldEntity!;
        fixture.Engine.ShadowObjects.Register(
            entity.Id,
            entity.SourceGfxObjOrSetupId,
            entity.Position,
            entity.Rotation,
            radius: 0.5f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: 0xA9B4FFFFu,
            seedCellId: startCell,
            isStatic: false);
        record.FinalPhysicsState = PhysicsStateFlags.ReportCollisions;
        var remote =
            fixture.Live.GetOrCreateRemoteMotionRuntime(Guid);
        Assert.Equal(new Vector3(40f, 0f, 0f), remote.Body.Velocity);
        Assert.Equal(new Vector3(0f, 0f, 2f), remote.Body.Omega);
        remote.Body.set_velocity(new Vector3(8f, 0f, 0f));
        remote.Body.Omega = new Vector3(0f, 0f, 3f);
        remote.Body.SnapToCell(
            startCell,
            entity.Position,
            new Vector3(191f, 10f, 50f));
        remote.Body.State = record.FinalPhysicsState;
        PhysicsBody body = remote.Body;

        record.FinalPhysicsState = MissileState;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record,
            MissileState,
            currentTime: 1.2,
            liveCenterX: 0xA9,
            liveCenterY: 0xB4));

        Assert.Same(body, record.ProjectileRuntime!.Body);
        Assert.Same(body, record.RemoteMotionRuntime!.Body);
        Assert.Same(body, record.PhysicsBody);
        Assert.Equal(new Vector3(8f, 0f, 0f), body.Velocity);
        Assert.Equal(new Vector3(0f, 0f, 3f), body.Omega);
        Assert.True(body.IsActive);

        fixture.Controller.Tick(1.45, 0xA9, 0xB4, playerWorldPosition: null);
        Assert.Equal(destinationCell, record.FullCellId);
        Assert.Equal(destinationCell, remote.CellId);

        record.FinalPhysicsState = PhysicsStateFlags.ReportCollisions;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, record.FinalPhysicsState, 1.5, 0xA9, 0xB4));
        Assert.Equal(destinationCell, remote.CellId);
        Assert.Equal(destinationCell, record.FullCellId);

        body.Position = new Vector3(191f, 10f, 50f);
        entity.SetPosition(body.Position);
        remote.CellId = startCell;
        Assert.True(fixture.Controller.LeaveWorld(record));
        fixture.Controller.Tick(1.6, 0xA9, 0xB4, playerWorldPosition: null);
        Assert.True(body.InWorld);
        Assert.Contains(
            fixture.Engine.ShadowObjects.GetObjectsInCell(startCell),
            entry => entry.EntityId == entity.Id);
        Assert.DoesNotContain(
            fixture.Engine.ShadowObjects.GetObjectsInCell(destinationCell),
            entry => entry.EntityId == entity.Id);

        record.FinalPhysicsState = MissileState;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, MissileState, 1.7, 0xA9, 0xB4));
        Assert.Equal(startCell, record.FullCellId);
        Assert.Equal(startCell, remote.CellId);
        Assert.Equal(startCell, body.CellPosition.ObjCellId);
    }

    [Fact]
    public void RemoteMotionFirst_ValidatesAdoptedBodyInsteadOfStaleCreateVectors()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(
            instance: 1,
            velocity: new Vector3(float.NaN, 0f, 0f));
        WorldEntity entity = record.WorldEntity!;
        record.FinalPhysicsState = PhysicsStateFlags.ReportCollisions;
        var remote =
            fixture.Live.GetOrCreateRemoteMotionRuntime(Guid);
        remote.Body.SnapToCell(CellA, entity.Position, entity.Position);
        remote.Body.Orientation = entity.Rotation;

        Assert.True(float.IsNaN(record.Snapshot.Physics!.Value.Velocity!.Value.X));
        Assert.Equal(Vector3.Zero, remote.Body.Velocity);

        record.FinalPhysicsState = MissileState;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, MissileState, 1.0, 1, 1));

        Assert.NotNull(record.ProjectileRuntime);
        Assert.Same(remote.Body, record.ProjectileRuntime!.Body);
        Assert.Equal(Vector3.Zero, remote.Body.Velocity);
        Assert.Equal(entity.Position, remote.Body.Position);
    }

    [Fact]
    public void RemoteMotionFirst_RejectsNonFiniteCurrentBodyDespiteValidCreateSnapshot()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        WorldEntity entity = record.WorldEntity!;
        record.FinalPhysicsState = PhysicsStateFlags.ReportCollisions;
        var remote =
            fixture.Live.GetOrCreateRemoteMotionRuntime(Guid);
        remote.Body.Position = entity.Position;
        remote.Body.Orientation = entity.Rotation;
        remote.Body.Velocity = new Vector3(float.PositiveInfinity, 0f, 0f);

        record.FinalPhysicsState = MissileState;
        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, MissileState, 1.0, 1, 1));

        Assert.Null(record.ProjectileRuntime);
        Assert.Same(remote.Body, record.PhysicsBody);
        Assert.True(float.IsPositiveInfinity(remote.Body.Velocity.X));
        Assert.Equal(MissileState, remote.Body.State);
    }

    [Fact]
    public void ResidencePlacementVisibility_RetriesProjectileBindingAfterBodyFrameCommits()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        WorldEntity entity = record.WorldEntity!;
        var remote = fixture.Live.GetOrCreateRemoteMotionRuntime(Guid);

        Assert.Null(record.ProjectileRuntime);
        Assert.True(fixture.Live.WithdrawLiveEntityProjection(Guid));

        remote.Body.Orientation = Quaternion.Identity;
        remote.Body.set_velocity(new Vector3(10f, 0f, 0f));
        remote.Body.SnapToCell(CellA, entity.Position, entity.Position);

        Assert.True(fixture.Live.RebucketLiveEntity(Guid, CellA));

        Assert.NotNull(record.ProjectileRuntime);
        Assert.Same(remote.Body, record.ProjectileRuntime!.Body);
        Assert.True(record.ProjectileRuntime.Body.InWorld);
    }

    [Fact]
    public void SharedBodyCell_IsCanonicalBeforePresentationCellIsProjected()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        WorldEntity entity = record.WorldEntity!;
        var remote = fixture.Live.GetOrCreateRemoteMotionRuntime(Guid);

        Assert.True(fixture.Live.WithdrawLiveEntityProjection(Guid));
        record.CanonicalLandblockId = 0u;
        record.FullCellId = 0u;
        Assert.Equal(0u, record.FullCellId);
        remote.Body.Orientation = Quaternion.Identity;
        remote.Body.set_velocity(new Vector3(10f, 0f, 0f));
        remote.Body.SnapToCell(CellA, entity.Position, entity.Position);

        Assert.True(fixture.Controller.TryBind(
            record,
            ProjectileSetup(),
            currentTime: 1.0,
            liveCenterX: 1,
            liveCenterY: 1));

        Assert.Equal(CellA, record.FullCellId);
        Assert.Same(remote.Body, record.ProjectileRuntime!.Body);
    }

    [Fact]
    public void SharedRemoteMotionCell_DelegatesToCanonicalLiveRecord()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0));
        PhysicsBody body = record.PhysicsBody!;
        var remote =
            fixture.Live.GetOrCreateRemoteMotionRuntime(Guid);
        Assert.Same(body, remote.Body);

        fixture.Live.RebucketLiveEntity(Guid, CellB);
        Assert.Equal(CellB, remote.CellId);

        remote.CellId = CellA;
        Assert.Equal(CellA, record.FullCellId);
        Assert.Equal(CellA, remote.CellId);
        Assert.Same(body, record.RemoteMotionRuntime!.Body);
        Assert.Same(body, record.ProjectileRuntime!.Body);

        var replacement = new AcDream.Runtime.Physics.RemoteMotion(body);
        fixture.Live.SetRemoteMotionRuntime(Guid, replacement);
        remote.CellId = CellB;
        Assert.Equal(CellA, record.FullCellId);
        Assert.Equal(CellA, remote.CellId);
        replacement.CellId = CellB;
        Assert.Equal(CellB, record.FullCellId);
    }

    [Fact]
    public void PickupLeaveWorldAndPositionReentry_RestoresShadowWithoutClockBacklog()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 4);
        WorldEntity entity = record.WorldEntity!;
        Assert.True(fixture.Controller.TryBind(record, ProjectileSetup(), 1.0));
        fixture.Engine.ShadowObjects.Register(
            entity.Id,
            entity.SourceGfxObjOrSetupId,
            entity.Position,
            entity.Rotation,
            radius: 0.5f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: CellA & 0xFFFF0000u,
            seedCellId: CellA,
            isStatic: false);
        Assert.Contains(
            fixture.Engine.ShadowObjects.GetObjectsInCell(CellA),
            entry => entry.EntityId == entity.Id);

        Assert.True(fixture.Live.TryApplyPickup(
            new PickupEvent.Parsed(Guid, InstanceSequence: 4, PositionSequence: 2),
            out _));
        Assert.True(fixture.Live.WithdrawLiveEntityProjection(Guid));
        Assert.True(fixture.Controller.LeaveWorld(record));
        Assert.DoesNotContain(
            fixture.Engine.ShadowObjects.GetObjectsInCell(CellA),
            entry => entry.EntityId == entity.Id);

        var correction = new CreateObject.ServerPosition(
            CellA,
            12f,
            10f,
            5f,
            1f,
            0f,
            0f,
            0f);
        var update = new WorldSession.EntityPositionUpdate(
            Guid,
            correction,
            Velocity: null,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 4,
            PositionSequence: 3,
            TeleportSequence: 0,
            ForcePositionSequence: 0);
        Assert.True(fixture.Live.TryApplyPosition(
            update,
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            out PositionTimestampDisposition disposition,
            out WorldSession.EntitySpawn accepted,
            out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        Assert.Same(
            entity,
            fixture.Live.MaterializeLiveEntity(
                Guid,
                accepted.Position!.Value.LandblockId,
                _ => throw new InvalidOperationException("re-entry recreated the entity")));

        Assert.True(fixture.Controller.ApplyAuthoritativeState(
            record, record.FinalPhysicsState, currentTime: 10.0, 1, 1));
        record.PhysicsBody!.SnapToCell(
            CellA,
            new Vector3(12f, 10f, 5f),
            new Vector3(12f, 10f, 5f));
        record.PhysicsBody.Orientation = Quaternion.Identity;
        record.PhysicsBody.set_velocity(Vector3.Zero);
        Assert.True(fixture.Live.RebucketLiveEntity(Guid, CellA));
        Assert.True(fixture.Controller.SyncPresentationFromResolvedBody(record, 10.0));
        Assert.Equal(0d, record.ObjectClock.PendingSeconds, 8);
        Assert.True(record.ObjectClock.IsActive);
        Assert.True(record.PhysicsBody!.IsActive);
        Assert.Contains(
            fixture.Engine.ShadowObjects.GetObjectsInCell(CellA),
            entry => entry.EntityId == entity.Id);

        fixture.Controller.Tick(
            10.1,
            liveCenterX: 1,
            liveCenterY: 1,
            playerWorldPosition: entity.Position);
        Assert.Equal(12f, entity.Position.X, 3);
        fixture.Controller.Tick(
            10.2,
            liveCenterX: 1,
            liveCenterY: 1,
            playerWorldPosition: entity.Position);
        Assert.Equal(12f, entity.Position.X, 3);
    }

    [Fact]
    public void DeleteAndGuidReuse_IsolateProjectileGenerations()
    {
        var fixture = new Fixture();
        LiveEntityRecord first = fixture.Spawn(instance: 7);
        Assert.True(fixture.Controller.TryBind(first, ProjectileSetup(), 1.0));
        WorldEntity firstEntity = first.WorldEntity!;
        IRuntimeProjectile firstRuntime = first.ProjectileRuntime!;

        Assert.True(fixture.Live.UnregisterLiveEntity(
            new DeleteObject.Parsed(Guid, InstanceSequence: 7),
            isLocalPlayer: false));
        LiveEntityRecord second = fixture.Spawn(instance: 8);
        Assert.True(fixture.Controller.TryBind(second, ProjectileSetup(), 2.0));
        IRuntimeProjectile secondRuntime = second.ProjectileRuntime!;

        Assert.False(fixture.Controller.TryBind(first, ProjectileSetup(), 3.0));

        Assert.NotSame(firstEntity, second.WorldEntity);
        Assert.NotSame(firstRuntime, second.ProjectileRuntime);
        Assert.Same(secondRuntime, second.ProjectileRuntime);
        Assert.Equal(1, fixture.Controller.Count);
        Assert.Equal(2, fixture.Resources.Registers);
        Assert.Equal(1, fixture.Resources.Unregisters);
    }

    [Fact]
    public void TryBind_ReentrantVisibilityGuidReuseNeverBindsOldBodyToReplacement()
    {
        var fixture = new Fixture();
        LiveEntityRecord first = fixture.Spawn(
            instance: 7,
            cellId: CellA,
            worldPosition: new Vector3(10f, 202f, 5f),
            cellLocalPosition: new Vector3(10f, 202f, 5f));
        LiveEntityRecord? replacement = null;
        bool replaced = false;
        fixture.Live.ProjectionVisibilityChanged += (record, visible) =>
        {
            if (replaced || visible || !ReferenceEquals(record, first))
                return;
            replaced = true;
            Assert.True(fixture.Live.UnregisterLiveEntity(
                new DeleteObject.Parsed(Guid, InstanceSequence: 7),
                isLocalPlayer: false));
            replacement = fixture.Spawn(instance: 8);
        };

        Assert.False(fixture.Controller.TryBind(
            first,
            ProjectileSetup(),
            currentTime: 1.0,
            liveCenterX: 1,
            liveCenterY: 1));

        Assert.NotNull(replacement);
        Assert.True(fixture.Live.TryGetRecord(Guid, out var current));
        Assert.Same(replacement, current);
        Assert.Null(current.ProjectileRuntime);
        Assert.Null(current.PhysicsBody);
        Assert.Equal(0, fixture.Controller.Count);
    }

    [Fact]
    public void SyncPresentation_ReentrantGuidReuseNeverTouchesTheReplacement()
    {
        var fixture = new Fixture();
        LiveEntityRecord first = fixture.Spawn(instance: 7);
        Assert.True(fixture.Controller.TryBind(first, ProjectileSetup(), 1.0, 1, 1));
        LiveEntityRecord? replacement = null;
        bool replaced = false;
        fixture.Live.ProjectionVisibilityChanged += (record, visible) =>
        {
            if (replaced || visible || !ReferenceEquals(record, first))
                return;
            replaced = true;
            Assert.True(fixture.Live.UnregisterLiveEntity(
                new DeleteObject.Parsed(Guid, InstanceSequence: 7),
                isLocalPlayer: false));
            replacement = fixture.Spawn(instance: 8);
        };

        fixture.Spatial.RemoveLandblock(0x0101FFFFu);
        Assert.True(replaced);

        // The stale ack for the SUPERSEDED incarnation must refuse.
        Assert.False(fixture.Controller.SyncPresentationFromResolvedBody(first, 1.1));

        Assert.NotNull(replacement);
        Assert.True(fixture.Live.TryGetRecord(Guid, out var current));
        Assert.Same(replacement, current);
        Assert.Null(current.ProjectileRuntime);
        Assert.Null(current.PhysicsBody);
        // The replacement's own spawn position (Fixture.Spawn's default),
        // untouched by the stale ack — the positive half of the assertion,
        // not merely "the ack returned false".
        Assert.Equal(new Vector3(10f, 10f, 5f), current.WorldEntity!.Position);
        Assert.Equal(0, fixture.Controller.Count);
    }

    [Fact]
    public void MissingRetailSphere_FailsWithoutFabricatingCollisionBody()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        string? diagnostic = null;
        fixture.Controller.DiagnosticSink = text => diagnostic = text;

        Assert.False(fixture.Controller.TryBind(record, new Setup(), 1.0));

        Assert.Null(record.ProjectileRuntime);
        Assert.Null(record.PhysicsBody);
        Assert.Contains("one-sphere", diagnostic);
    }

    private sealed class Fixture
    {
        private sealed class SetupResolver : IProjectileSetupResolver
        {
            private readonly Func<Setup?> _resolve;

            internal SetupResolver(Func<Setup?> resolve) => _resolve = resolve;

            public Setup? Resolve(uint setupId) => _resolve();
        }

        private sealed class RootPosePublisher : IEntityRootPosePublisher
        {
            private readonly Action<WorldEntity> _publish;

            internal RootPosePublisher(Action<WorldEntity> publish) => _publish = publish;

            public void UpdateRoot(WorldEntity entity) => _publish(entity);
        }

        internal Fixture(
            bool loadInitialLandblock = true,
            PhysicsEngine? physicsEngine = null,
            Action<WorldEntity>? publishRootPose = null)
        {
            if (loadInitialLandblock)
                Spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
            Engine = physicsEngine ?? new PhysicsEngine();
            Live = LiveEntityRuntimeFixture.Create(
                Spatial,
                Resources,
                Engine);
            Origin.Recenter(1, 1);
            Controller = new ProjectileController(
                Live,
                new SetupResolver(() => ResolvedSetup),
                new RootPosePublisher(
                    publishRootPose ?? new Action<WorldEntity>(
                        entity => { _ = Poses.UpdateRoot(entity); })),
                Origin);
        }

        internal GpuWorldState Spatial { get; } = new();
        internal RecordingResources Resources { get; } = new();
        internal LiveEntityRuntime Live { get; }
        internal PhysicsEngine Engine { get; }
        internal EntityEffectPoseRegistry Poses { get; } = new();
        internal LiveWorldOriginState Origin { get; } = new();
        internal Setup ResolvedSetup { get; set; } = ProjectileSetup();
        internal ProjectileController Controller { get; }

        internal LiveEntityRecord Spawn(
            ushort instance,
            ushort vectorSequence = 1,
            uint cellId = CellA,
            Vector3? worldPosition = null,
            Vector3? cellLocalPosition = null,
            Vector3? velocity = null,
            Vector3? angularVelocity = null,
            float friction = 0f,
            float elasticity = 0f,
            bool hasRenderablePart = false)
        {
            Vector3 world = worldPosition ?? new Vector3(10f, 10f, 5f);
            Vector3 cellLocal = cellLocalPosition ?? world;
            WorldSession.EntitySpawn spawn = ProjectileSpawn(
                instance,
                vectorSequence,
                cellId,
                cellLocal,
                velocity ?? new Vector3(10f, 0f, 0f),
                angularVelocity ?? Vector3.Zero,
                friction,
                elasticity);
            LiveEntityRecord record = Live.RegisterAndMaterializeProjection(
                spawn,
                localId => new WorldEntity
                {
                    Id = localId,
                    ServerGuid = Guid,
                    SourceGfxObjOrSetupId = 0x02000124u,
                    Position = world,
                    Rotation = Quaternion.Identity,
                    MeshRefs = hasRenderablePart
                        ? new[] { new MeshRef(0x01000001u, Matrix4x4.Identity) }
                        : Array.Empty<MeshRef>(),
                    ParentCellId = cellId,
                },
                initializeProjection: exact => exact.HasPartArray = true);
            WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);
            Poses.PublishMeshRefs(entity);
            return record;
        }
    }

    public enum AuthoritativeMutation
    {
        Vector,
        State,
    }

    private static Setup ProjectileSetup(float radius = 0.102f) => new()
    {
        Spheres =
        {
            new Sphere
            {
                Origin = new Vector3(0f, -0.165f, 0.1045f),
                Radius = radius,
            },
        },
    };

    private static WorldSession.EntitySpawn ProjectileSpawn(
        ushort instance,
        ushort vectorSequence,
        uint cellId,
        Vector3 cellLocalPosition,
        Vector3 velocity,
        Vector3 angularVelocity,
        float friction,
        float elasticity)
    {
        var position = new CreateObject.ServerPosition(
            cellId,
            cellLocalPosition.X,
            cellLocalPosition.Y,
            cellLocalPosition.Z,
            1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: vectorSequence,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: instance);
        var physics = new PhysicsSpawnData(
            RawState: (uint)MissileState,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000124u,
            MotionTableId: null,
            SoundTableId: null,
            PhysicsScriptTableId: 0x3400002Bu,
            Parent: null,
            Children: null,
            Scale: 1f,
            Friction: friction,
            Elasticity: elasticity,
            Translucency: null,
            Velocity: velocity,
            Acceleration: new Vector3(999f, 999f, 999f),
            AngularVelocity: angularVelocity,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            Guid,
            position,
            0x02000124u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: 1f,
            Name: "Projectile fixture",
            ItemType: null,
            MotionState: null,
            MotionTableId: null,
            PhysicsState: (uint)MissileState,
            Friction: friction,
            Elasticity: elasticity,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private static LoadedLandblock EmptyLandblock(uint canonicalId) =>
        new(canonicalId, new LandBlock(), Array.Empty<WorldEntity>());

    private static PhysicsEngine BuildBoundaryEngine()
    {
        static TerrainSurface EmptyTerrain()
        {
            var heights = new byte[81];
            var table = new float[256];
            Array.Fill(table, -1000f);
            return new TerrainSurface(heights, table);
        }

        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            0xA9B4FFFFu,
            EmptyTerrain(),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);
        engine.AddLandblock(
            0xAAB4FFFFu,
            EmptyTerrain(),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            192f,
            0f);
        return engine;
    }
}
