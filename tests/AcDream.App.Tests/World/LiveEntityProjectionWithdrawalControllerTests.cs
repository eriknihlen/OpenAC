using System.Numerics;
using AcDream.App.Physics;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Plugins;
using AcDream.Core.World;
using AcDream.Plugin.Abstractions;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.World;

public sealed class LiveEntityProjectionWithdrawalControllerTests
{
    private const uint Guid = 0x70000200u;
    private const uint Cell = 0x01010001u;

    [Fact]
    public void ComponentFailure_StopsBeforeCanonicalWithdrawal_AndRetryConverges()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        fixture.Poses.EffectPoseChanged += ThrowOnPoseChange;

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Controller.Withdraw(Guid, localPlayerGuid: 0u));

        Assert.True(record.IsSpatiallyProjected);
        Assert.Empty(fixture.GameState.Entities);
        Assert.Equal(0, fixture.Poses.Count);
        Assert.DoesNotContain(
            fixture.Physics.ShadowObjects.GetObjectsInCell(Cell),
            entry => entry.EntityId == record.WorldEntity!.Id);
        Assert.Equal(1, fixture.Physics.ShadowObjects.SuspendedRegistrationCount);

        fixture.Poses.EffectPoseChanged -= ThrowOnPoseChange;
        Assert.True(fixture.Controller.Withdraw(Guid, localPlayerGuid: 0u));
        Assert.False(record.IsSpatiallyProjected);

        static void ThrowOnPoseChange(uint _) =>
            throw new InvalidOperationException("fixture pose observer failure");
    }

    [Fact]
    public void RuntimeObserverFailure_CommitsCanonicalWithdrawalAfterComponentsRemoved()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        fixture.Live.ProjectionVisibilityChanged += ThrowOnHidden;

        Assert.ThrowsAny<Exception>(() =>
            fixture.Controller.Withdraw(Guid, localPlayerGuid: 0u));

        Assert.False(record.IsSpatiallyProjected);
        Assert.False(record.IsSpatiallyVisible);
        Assert.Empty(fixture.GameState.Entities);
        Assert.Equal(0, fixture.Poses.Count);

        static void ThrowOnHidden(LiveEntityRecord _, bool visible)
        {
            if (!visible)
                throw new InvalidOperationException("fixture runtime observer failure");
        }
    }

    [Fact]
    public void OrdinaryLeaveWorld_SuspendsRetainedShadow_AndReentryRestoresIt()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        uint localId = record.WorldEntity!.Id;
        Assert.Contains(
            fixture.Physics.ShadowObjects.GetObjectsInCell(Cell),
            entry => entry.EntityId == localId);

        Assert.True(fixture.Controller.Withdraw(Guid, localPlayerGuid: 0u));
        Assert.DoesNotContain(
            fixture.Physics.ShadowObjects.GetObjectsInCell(Cell),
            entry => entry.EntityId == localId);

        Assert.True(fixture.Live.RebucketLiveEntity(Guid, Cell));
        Assert.Contains(
            fixture.Physics.ShadowObjects.GetObjectsInCell(Cell),
            entry => entry.EntityId == localId);
    }

    [Fact]
    public void ComponentCallbackReplacesGuid_DoesNotWithdrawReplacement()
    {
        var fixture = new Fixture();
        LiveEntityRecord old = fixture.Spawn(instance: 1);
        LiveEntityRecord? replacement = null;
        bool replacing = false;
        fixture.LocalShadow.Set(Vector3.One, Quaternion.Identity, Cell);
        fixture.Poses.EffectPoseChanged += OnPoseChanged;

        Assert.False(fixture.Controller.Withdraw(Guid, localPlayerGuid: Guid));

        Assert.NotNull(replacement);
        Assert.True(fixture.Live.TryGetRecord(Guid, out LiveEntityRecord current));
        Assert.Same(replacement, current);
        Assert.True(current.IsSpatiallyProjected);
        Assert.Equal(new Vector3(9f, 9f, 9f), current.WorldEntity!.Position);
        Assert.NotNull(fixture.LocalShadow.Current);
        Assert.NotSame(old, current);

        void OnPoseChanged(uint localId)
        {
            if (replacement is not null || replacing || localId != old.WorldEntity!.Id)
                return;
            replacing = true;
            replacement = fixture.Spawn(instance: 2, position: new Vector3(9f, 9f, 9f));
            fixture.LocalShadow.Set(new Vector3(9f), Quaternion.Identity, Cell);
        }
    }

    [Fact]
    public void ComponentCallbackReprojectsSameIncarnation_DoesNotWithdrawNewPosition()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.Spawn(instance: 1);
        bool reentered = false;
        fixture.Poses.EffectPoseChanged += OnPoseChanged;

        Assert.False(fixture.Controller.Withdraw(Guid, localPlayerGuid: 0u));

        Assert.True(record.IsSpatiallyProjected);
        Assert.True(record.IsSpatiallyVisible);
        Assert.Equal(new Vector3(8f, 7f, 6f), record.WorldEntity!.Position);
        Assert.Contains(fixture.GameState.Entities, entity => entity.Id == record.WorldEntity.Id);
        Assert.Equal(1, fixture.Poses.Count);

        void OnPoseChanged(uint localId)
        {
            if (reentered || localId != record.WorldEntity!.Id)
                return;
            reentered = true;
            var position = new WorldSession.EntityPositionUpdate(
                Guid,
                new CreateObject.ServerPosition(
                    Cell, 8f, 7f, 6f, 1f, 0f, 0f, 0f),
                Velocity: null,
                PlacementId: null,
                IsGrounded: true,
                InstanceSequence: 1,
                PositionSequence: 2,
                TeleportSequence: 0,
                ForcePositionSequence: 0);
            Assert.True(fixture.Live.TryApplyPosition(
                position,
                isLocalPlayer: false,
                forcePositionRotation: null,
                currentLocalVelocity: null,
                out PositionTimestampDisposition disposition,
                out WorldSession.EntitySpawn accepted,
                out _));
            Assert.Equal(PositionTimestampDisposition.Apply, disposition);
            record.WorldEntity.SetPosition(new Vector3(8f, 7f, 6f));
            Assert.True(fixture.Live.RebucketLiveEntity(
                Guid,
                accepted.Position!.Value.LandblockId));
            var snapshot = new WorldEntitySnapshot(
                record.WorldEntity.Id,
                record.WorldEntity.SourceGfxObjOrSetupId,
                record.WorldEntity.Position,
                record.WorldEntity.Rotation);
            fixture.GameState.Add(snapshot);
            fixture.Events.UpsertCurrent(snapshot);
            fixture.Poses.PublishMeshRefs(record.WorldEntity);
        }
    }

    [Fact]
    public void LogicalLocalPlayerTeardownWithoutReplacement_ClearsShadowSnapshot()
    {
        var fixture = new Fixture();
        fixture.Spawn(instance: 1);
        fixture.LocalShadow.Set(Vector3.One, Quaternion.Identity, Cell);

        Assert.True(fixture.Live.UnregisterLiveEntity(
            new DeleteObject.Parsed(Guid, InstanceSequence: 1),
            isLocalPlayer: false));

        Assert.Null(fixture.LocalShadow.Current);
    }

    private sealed class Fixture
    {
        internal Fixture()
        {
            Spatial.AddLandblock(new LoadedLandblock(
                (Cell & 0xFFFF0000u) | 0xFFFFu,
                new LandBlock(),
                Array.Empty<WorldEntity>()));
            var lifecycle = new DeferredLiveEntityRuntimeComponentLifecycle();
            Live = LiveEntityRuntimeFixture.Create(
                Spatial,
                new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }),
                lifecycle,
                Physics);
            Projectiles = new ProjectileController(
                Live);
            Controller = new LiveEntityProjectionWithdrawalController(
                Live,
                Projectiles,
                GameState,
                Events,
                Physics.ShadowObjects,
                Poses,
                LocalShadow);
            Presentation = new LiveEntityPresentationController(
                Live,
                Physics.ShadowObjects,
                (_, _, _) => false,
                new LiveEntityPartArrayEnterWorldPort(_ => { }));
            lifecycle.Bind(new DelegateLiveEntityRuntimeComponentLifecycle(
                record => Controller.LeaveWorld(record, Guid)));
        }

        internal GpuWorldState Spatial { get; } = new();
        internal LiveEntityRuntime Live { get; }
        internal PhysicsEngine Physics { get; } = new();
        internal ProjectileController Projectiles { get; }
        internal WorldGameState GameState { get; } = new();
        internal WorldEvents Events { get; } = new();
        internal EntityEffectPoseRegistry Poses { get; } = new();
        internal LocalPlayerShadowState LocalShadow { get; } = new();
        internal LiveEntityProjectionWithdrawalController Controller { get; }
        internal LiveEntityPresentationController Presentation { get; }

        internal LiveEntityRecord Spawn(ushort instance, Vector3? position = null)
        {
            WorldSession.EntitySpawn spawn = new(
                Guid,
                new CreateObject.ServerPosition(Cell, 1f, 2f, 3f, 1f, 0f, 0f, 0f),
                0x02000200u,
                Array.Empty<CreateObject.AnimPartChange>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.SubPaletteSwap>(),
                BasePaletteId: null,
                ObjScale: 1f,
                Name: "projection fixture",
                ItemType: null,
                MotionState: null,
                MotionTableId: null,
                InstanceSequence: instance);
            // C3c: residence admission requires the flattened parser
            // projections to agree with a nested PhysicsDesc block.
            spawn = spawn.WithConsistentPhysics();
            LiveEntityRecord record = Live.RegisterAndMaterializeProjection(
                spawn,
                id => new WorldEntity
                {
                    Id = id,
                    ServerGuid = Guid,
                    SourceGfxObjOrSetupId = 0x02000200u,
                    Position = position ?? new Vector3(1f, 2f, 3f),
                    Rotation = Quaternion.Identity,
                    MeshRefs = Array.Empty<MeshRef>(),
                    ParentCellId = Cell,
                });
            WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);
            var snapshot = new WorldEntitySnapshot(
                entity.Id,
                entity.SourceGfxObjOrSetupId,
                entity.Position,
                entity.Rotation);
            GameState.Add(snapshot);
            Events.UpsertCurrent(snapshot);
            Poses.PublishMeshRefs(entity);
            Physics.ShadowObjects.Register(
                entity.Id,
                entity.SourceGfxObjOrSetupId,
                entity.Position,
                entity.Rotation,
                radius: 0.5f,
                worldOffsetX: 0f,
                worldOffsetY: 0f,
                landblockId: Cell,
                seedCellId: Cell,
                isStatic: false);
            Assert.True(Presentation.OnLiveEntityReady(Guid));
            return record;
        }
    }
}
