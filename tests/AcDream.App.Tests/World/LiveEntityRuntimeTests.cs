using System.Numerics;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Vfx;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Vfx;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using DatPhysicsScript = DatReaderWriter.DBObjs.PhysicsScript;

namespace AcDream.App.Tests.World;

public sealed class LiveEntityRuntimeTests
{
    [Fact]
    public void RuntimeSlot_BindsExactlyOnceWithoutOwningEntityState()
    {
        var spatial = new GpuWorldState();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        var slot = new LiveEntityRuntimeSlot();

        Assert.Null(slot.Current);
        slot.Bind(runtime);

        Assert.Same(runtime, slot.Current);
        Assert.Throws<InvalidOperationException>(() => slot.Bind(runtime));
    }

    private sealed class RecordingResources : ILiveEntityResourceLifecycle
    {
        public int RegisterCount { get; private set; }
        public int UnregisterCount { get; private set; }
        public void Register(WorldEntity entity) => RegisterCount++;
        public void Unregister(WorldEntity entity) => UnregisterCount++;
    }

    private sealed class AnimationRuntime(WorldEntity entity) : ILiveEntityAnimationRuntime
    {
        public WorldEntity Entity { get; } = entity;
        public uint CurrentMotion { get; init; }
    }

    private sealed class EffectProfile : ILiveEntityEffectProfile { }

    private sealed class RemoteMotionRuntime : IRuntimeRemoteMotion
    {
        public PhysicsBody Body { get; } = new();
    }

    private sealed class FailingRegisterResources : ILiveEntityResourceLifecycle
    {
        public int RegisterCount { get; private set; }
        public int UnregisterCount { get; private set; }

        public void Register(WorldEntity entity)
        {
            RegisterCount++;
            throw new InvalidOperationException("fixture registration failure");
        }

        public void Unregister(WorldEntity entity) => UnregisterCount++;
    }

    private sealed class FailingRegisterAndRollbackOnceResources : ILiveEntityResourceLifecycle
    {
        private bool _registerFailurePending = true;
        private bool _unregisterFailurePending = true;
        public int RegisterCount { get; private set; }
        public int UnregisterCount { get; private set; }

        public void Register(WorldEntity entity)
        {
            RegisterCount++;
            if (_registerFailurePending)
            {
                _registerFailurePending = false;
                throw new InvalidOperationException("partial registration failure");
            }
        }

        public void Unregister(WorldEntity entity)
        {
            UnregisterCount++;
            if (_unregisterFailurePending)
            {
                _unregisterFailurePending = false;
                throw new InvalidOperationException("rollback failure");
            }
        }
    }

    private sealed class FailingUnregisterResources(uint failingGuid) : ILiveEntityResourceLifecycle
    {
        public int UnregisterCount { get; private set; }

        public void Register(WorldEntity entity) { }

        public void Unregister(WorldEntity entity)
        {
            UnregisterCount++;
            if (entity.ServerGuid == failingGuid)
                throw new InvalidOperationException("fixture unregister failure");
        }
    }

    private sealed class FailingOnceUnregisterResources(uint failingGuid) : ILiveEntityResourceLifecycle
    {
        private bool _failurePending = true;
        public int UnregisterCount { get; private set; }
        public void Register(WorldEntity entity) { }
        public void Unregister(WorldEntity entity)
        {
            UnregisterCount++;
            if (entity.ServerGuid == failingGuid && _failurePending)
            {
                _failurePending = false;
                throw new InvalidOperationException("fixture one-shot unregister failure");
            }
        }
    }

    private sealed class CallbackTextureLifetime : IEntityTextureLifetime
    {
        public Action? OnRelease { get; set; }
        public int ReleaseCount { get; private set; }
        public void ReleaseOwner(uint localEntityId)
        {
            ReleaseCount++;
            OnRelease?.Invoke();
        }
    }

    private sealed class NullAnimationLoader : IAnimationLoader
    {
        public Animation? LoadAnimation(uint id) => null;
    }

    private sealed class CallbackResources : ILiveEntityResourceLifecycle
    {
        public Action<WorldEntity>? OnRegister { get; set; }
        public Action<WorldEntity>? OnUnregister { get; set; }
        public int RegisterCount { get; private set; }
        public int UnregisterCount { get; private set; }
        public Dictionary<uint, int> UnregisterCountsByGuid { get; } = new();

        public void Register(WorldEntity entity)
        {
            RegisterCount++;
            OnRegister?.Invoke(entity);
        }

        public void Unregister(WorldEntity entity)
        {
            UnregisterCount++;
            UnregisterCountsByGuid[entity.ServerGuid] =
                UnregisterCountsByGuid.GetValueOrDefault(entity.ServerGuid) + 1;
            OnUnregister?.Invoke(entity);
        }
    }

    [Fact]
    public void RegisterRebucketWithdrawAndRestore_UsesOneLogicalCreate()
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        spatial.AddLandblock(EmptyLandblock(0x0102FFFFu));
        var resources = new RecordingResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        WorldSession.EntitySpawn spawn = Spawn(0x70000001u, 1, 1, 0x01010001u);

        LiveEntityRegistrationResult registered = runtime.RegisterLiveEntity(spawn);
        WorldEntity? entity = runtime.MaterializeLiveEntity(
            spawn.Guid,
            spawn.Position!.Value.LandblockId,
            id => Entity(id, spawn.Guid));
        runtime.SetAnimationRuntime(spawn.Guid, new AnimationRuntime(entity!));
        var visibilityEdges = new List<bool>();
        runtime.ProjectionVisibilityChanged += (_, visible) => visibilityEdges.Add(visible);

        Assert.True(registered.LogicalRegistrationCreated);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Single(spatial.Entities);
        Assert.True(runtime.RebucketLiveEntity(spawn.Guid, 0x01020001u));
        Assert.Equal(1, resources.RegisterCount);
        Assert.Single(spatial.Entities);
        Assert.True(runtime.WithdrawLiveEntityProjection(spawn.Guid));
        Assert.Empty(spatial.Entities);
        Assert.Empty(runtime.VisibleRecords);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(0, resources.UnregisterCount);
        Assert.True(runtime.TryGetAnimationRuntime(entity!.Id, out _));
        Assert.True(runtime.RebucketLiveEntity(spawn.Guid, 0x01010001u));
        Assert.Single(spatial.Entities);
        Assert.Equal(1, resources.RegisterCount);
        Assert.True(runtime.TryGetAnimationRuntime(entity!.Id, out _));
        Assert.Equal(new[] { false, true }, visibilityEdges);
    }

    [Fact]
    public void RuntimePlacementPendingMaterialization_OwnsResourcesWithoutPublishingResidence()
    {
        const uint guid = 0x70000081u;
        const uint cell = 0x01010001u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new CallbackResources();
        LiveEntityRuntime runtime = LiveEntityRuntimeFixture.Create(
            spatial,
            resources);
        LiveEntityRegistrationResult registration = runtime.RegisterLiveEntity(
            Spawn(guid, instance: 1, positionSequence: 1, cell));
        RuntimeEntityRecord canonical = Assert.IsType<RuntimeEntityRecord>(
            registration.Canonical);
        uint acceptedCell = canonical.FullCellId;
        ulong spatialAuthority = canonical.SpatialAuthorityVersion;
        ulong placementCommit = canonical.PlacementCommitVersion;
        ulong clockEpoch = canonical.ObjectClockEpoch;
        bool clockActive = canonical.ObjectClock.IsActive;
        RuntimePhysicsOwnershipSnapshot physicsOwnership =
            runtime.Physics.CaptureOwnership();
        var visibilityEdges = new List<bool>();
        runtime.ProjectionVisibilityChanged += (_, visible) =>
            visibilityEdges.Add(visible);
        resources.OnRegister = entity =>
        {
            Assert.False(entity.IsDrawVisible);
            Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord current));
            Assert.False(current.IsSpatiallyProjected);
            Assert.False(current.IsSpatiallyVisible);
            Assert.False(runtime.RebucketLiveEntity(guid, cell));
        };

        WorldEntity entity = Assert.IsType<WorldEntity>(
            runtime.MaterializeLiveEntity(
                canonical,
                cell,
                id => Entity(id, guid),
                LiveEntityProjectionKind.World,
                initializeProjection: null,
                out LiveEntityRecord? record,
                LiveEntityMaterializationResidence.AwaitRuntimePlacement));

        Assert.NotNull(record);
        Assert.Same(entity, record!.WorldEntity);
        Assert.True(record.ResourcesRegistered);
        Assert.False(record.IsSpatiallyProjected);
        Assert.False(record.IsSpatiallyVisible);
        Assert.False(entity.IsDrawVisible);
        Assert.Equal(acceptedCell, canonical.FullCellId);
        Assert.Equal(spatialAuthority, canonical.SpatialAuthorityVersion);
        Assert.Equal(placementCommit, canonical.PlacementCommitVersion);
        Assert.Equal(clockEpoch, canonical.ObjectClockEpoch);
        Assert.Equal(clockActive, canonical.ObjectClock.IsActive);
        Assert.Equal(physicsOwnership, runtime.Physics.CaptureOwnership());
        Assert.Equal(0, runtime.SpatialRootObjectCount);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Empty(spatial.Entities);
        Assert.Empty(runtime.VisibleRecords);
        Assert.Empty(visibilityEdges);

        WorldEntity retained = Assert.IsType<WorldEntity>(
            runtime.MaterializeLiveEntity(
                canonical,
                cell,
                id => Entity(id, guid),
                LiveEntityProjectionKind.World,
                initializeProjection: null,
                out LiveEntityRecord? retainedRecord,
                LiveEntityMaterializationResidence.AwaitRuntimePlacement));
        Assert.Same(entity, retained);
        Assert.Same(record, retainedRecord);
        Assert.Equal(1, resources.RegisterCount);
        Assert.False(runtime.RebucketLiveEntity(guid, cell));
        Assert.Throws<InvalidOperationException>(() =>
            runtime.MaterializeLiveEntity(
                canonical,
                cell,
                id => Entity(id, guid),
                LiveEntityProjectionKind.World,
                initializeProjection: null,
                out _,
                LiveEntityMaterializationResidence.LegacyImmediate));
        Assert.Empty(spatial.Entities);
    }

    [Fact]
    public void LegacyMaterialization_CannotSwitchToRuntimePlacementResidence()
    {
        const uint guid = 0x70000082u;
        const uint cell = 0x01010001u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new RecordingResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        RuntimeEntityRecord canonical = Assert.IsType<RuntimeEntityRecord>(
            runtime.RegisterLiveEntity(
                Spawn(guid, instance: 1, positionSequence: 1, cell)).Canonical);
        WorldEntity entity = Assert.IsType<WorldEntity>(
            runtime.MaterializeLiveEntity(
                canonical,
                cell,
                id => Entity(id, guid),
                LiveEntityProjectionKind.World,
                initializeProjection: null,
                out LiveEntityRecord? record));

        Assert.Throws<InvalidOperationException>(() =>
            runtime.MaterializeLiveEntity(
                canonical,
                cell,
                id => Entity(id, guid),
                LiveEntityProjectionKind.World,
                initializeProjection: null,
                out _,
                LiveEntityMaterializationResidence.AwaitRuntimePlacement));

        Assert.True(record!.IsSpatiallyProjected);
        Assert.True(record.IsSpatiallyVisible);
        Assert.True(entity.IsDrawVisible);
        Assert.Single(spatial.Entities);
        Assert.Equal(1, resources.RegisterCount);
    }

    [Fact]
    public void StaticRootCommit_HiddenObjectUpdatesPoseWithoutRestoringShadow()
    {
        const uint guid = 0x70000071u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        WorldSession.EntitySpawn spawn = Spawn(
            guid,
            instance: 1,
            positionSequence: 1,
            cell: 0x01010001u,
            state: PhysicsStateFlags.Static | PhysicsStateFlags.ReportCollisions);
        runtime.RegisterLiveEntity(spawn);
        WorldEntity entity = runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid))!;
        entity.Position = new Vector3(12f, 12f, 5f);
        PhysicsBody body = runtime.GetOrCreatePhysicsBody(
            guid,
            _ =>
            {
                var created = new PhysicsBody();
                created.SnapToCell(
                    0x01010001u,
                    entity.Position,
                    new Vector3(12f, 12f, 5f));
                return created;
            });
        var shadows = new ShadowObjectRegistry();
        shadows.RegisterMultiPart(
            entity.Id,
            entity.Position,
            entity.Rotation,
            new[]
            {
                ShadowShape.Cylinder(
                    0x01000001u,
                    Vector3.UnitX,
                    Quaternion.Identity,
                    scale: 1f,
                    radius: 0.5f,
                    cylHeight: 1f),
            },
            state: (uint)PhysicsStateFlags.ReportCollisions,
            flags: EntityCollisionFlags.None,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: 0x01010000u,
            seedCellId: 0x01010001u);
        int poseUpdates = 0;
        var runtimeSlot = new LiveEntityRuntimeSlot();
        runtimeSlot.Bind(runtime);
        var origin = new LiveWorldOriginState();
        origin.Recenter(1, 1);
        var poses = new EntityEffectPoseRegistry();
        poses.PublishMeshRefs(entity);
        poses.EffectPoseChanged += _ => poseUpdates++;
        var committer = new StaticLiveRootCommitter(
            runtimeSlot,
            shadows,
            origin,
            poses);

        body.SetFrameInCurrentCell(
            body.Position,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f));
        entity.Rotation = body.Orientation;
        Assert.True(committer.Commit(entity, body));
        ShadowEntry visible = Assert.Single(shadows.AllEntriesForDebug());
        Assert.InRange(visible.Position.X, 11.999f, 12.001f);
        Assert.InRange(visible.Position.Y, 12.999f, 13.001f);

        Assert.True(runtime.TryApplyState(new SetState.Parsed(
            guid,
            (uint)(PhysicsStateFlags.Static
                | PhysicsStateFlags.Hidden
                | PhysicsStateFlags.IgnoreCollisions),
            InstanceSequence: 1,
            StateSequence: 2), out _));
        Assert.True(shadows.Suspend(entity.Id));
        body.SetFrameInCurrentCell(
            body.Position,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI));
        entity.Rotation = body.Orientation;

        Assert.True(committer.Commit(entity, body));
        Assert.Equal(2, poseUpdates);
        Assert.Empty(shadows.AllEntriesForDebug());
        Assert.Equal(1, shadows.SuspendedRegistrationCount);
    }

    [Fact]
    public void StaticRootCommit_BeforeLiveRuntimeCompositionIsSafeNoOp()
    {
        int poseUpdates = 0;
        var poses = new EntityEffectPoseRegistry();
        poses.EffectPoseChanged += _ => poseUpdates++;
        var committer = new StaticLiveRootCommitter(
            new LiveEntityRuntimeSlot(),
            new ShadowObjectRegistry(),
            new LiveWorldOriginState(),
            poses);

        Assert.False(committer.Commit(
            Entity(0x7F000001u, 0x70000001u),
            new PhysicsBody()));
        Assert.Equal(0, poseUpdates);
    }

    [Fact]
    public void StaticRootCommit_EffectCallbackReplacesGuid_DoesNotRestoreOldShadow()
    {
        const uint guid = 0x70000074u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(
            guid,
            instance: 1,
            positionSequence: 1,
            cell: 0x01010001u,
            state: PhysicsStateFlags.Static | PhysicsStateFlags.ReportCollisions));
        WorldEntity oldEntity = runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid))!;
        oldEntity.Position = new Vector3(12f, 12f, 5f);
        PhysicsBody oldBody = runtime.GetOrCreatePhysicsBody(
            guid,
            _ =>
            {
                var created = new PhysicsBody();
                created.SnapToCell(
                    0x01010001u,
                    oldEntity.Position,
                    oldEntity.Position);
                return created;
            });
        var shadows = new ShadowObjectRegistry();
        shadows.RegisterMultiPart(
            oldEntity.Id,
            oldEntity.Position,
            oldEntity.Rotation,
            new[]
            {
                ShadowShape.Cylinder(
                    0x01000001u,
                    Vector3.UnitX,
                    Quaternion.Identity,
                    scale: 1f,
                    radius: 0.5f,
                    cylHeight: 1f),
            },
            state: (uint)PhysicsStateFlags.ReportCollisions,
            flags: EntityCollisionFlags.None,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: 0x01010000u,
            seedCellId: 0x01010001u);
        var runtimeSlot = new LiveEntityRuntimeSlot();
        runtimeSlot.Bind(runtime);
        var origin = new LiveWorldOriginState();
        origin.Recenter(1, 1);
        var poses = new EntityEffectPoseRegistry();
        poses.PublishMeshRefs(oldEntity);
        poses.EffectPoseChanged += _ =>
        {
            Assert.True(shadows.Suspend(oldEntity.Id));
            Assert.True(runtime.UnregisterLiveEntity(
                new DeleteObject.Parsed(guid, InstanceSequence: 1),
                isLocalPlayer: false));
            runtime.RegisterLiveEntity(Spawn(
                guid,
                instance: 2,
                positionSequence: 1,
                cell: 0x01010001u));
            runtime.MaterializeLiveEntity(
                guid,
                0x01010001u,
                id => Entity(id, guid));
        };
        var committer = new StaticLiveRootCommitter(
            runtimeSlot,
            shadows,
            origin,
            poses);

        oldBody.SetFrameInCurrentCell(
            oldBody.Position,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f));
        oldEntity.Rotation = oldBody.Orientation;

        Assert.False(committer.Commit(oldEntity, oldBody));
        Assert.Empty(shadows.AllEntriesForDebug());
        Assert.Equal(1, shadows.SuspendedRegistrationCount);
        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord replacement));
        Assert.Equal((ushort)2, replacement.Generation);
        Assert.NotSame(oldEntity, replacement.WorldEntity);
    }

    [Fact]
    public void SpatialComponentWorksets_FollowLoadedProjectionWhileLogicalOwnersSurvive()
    {
        const uint guid = 0x70000070u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        WorldSession.EntitySpawn spawn = Spawn(guid, 1, 1, 0x01010001u);
        runtime.RegisterLiveEntity(spawn);
        WorldEntity entity = runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid))!;
        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord record));
        var animation = new AnimationRuntime(entity);
        var remote = new RemoteMotionRuntime();
        runtime.SetAnimationRuntime(guid, animation);
        runtime.SetRemoteMotionRuntime(guid, remote);
        IRuntimeProjectile projectile = runtime.BindProjectileRuntime(
            guid,
            remote.Body,
            new ProjectileCollisionSphere(Vector3.Zero, 0.25f));

        Assert.True(runtime.IsCurrentSpatialAnimation(record, animation));
        Assert.True(runtime.IsCurrentSpatialRemoteMotion(record, remote));
        Assert.True(runtime.IsCurrentSpatialProjectile(record, projectile));

        Assert.True(runtime.TryApplyState(new SetState.Parsed(
            guid,
            (uint)(PhysicsStateFlags.Hidden | PhysicsStateFlags.IgnoreCollisions),
            1,
            2), out _));
        Assert.Equal(1, runtime.SpatialAnimationRuntimeCount);
        Assert.Equal(1, runtime.SpatialRemoteMotionRuntimeCount);
        Assert.Equal(1, runtime.SpatialProjectileRuntimeCount);

        Assert.True(runtime.RebucketLiveEntity(guid, 0x02020001u));
        Assert.Equal(0, runtime.SpatialAnimationRuntimeCount);
        Assert.Equal(0, runtime.SpatialRemoteMotionRuntimeCount);
        Assert.Equal(0, runtime.SpatialProjectileRuntimeCount);
        Assert.True(runtime.TryGetAnimationRuntime(entity.Id, out var retainedAnimation));
        Assert.Same(animation, retainedAnimation);
        Assert.True(runtime.TryGetRemoteMotionRuntime(guid, out var retainedRemote));
        Assert.Same(remote, retainedRemote);
        Assert.True(runtime.TryGetProjectileRuntime(guid, out var retainedProjectile));
        Assert.Same(projectile, retainedProjectile);

        spatial.AddLandblock(EmptyLandblock(0x0202FFFFu));
        Assert.Equal(1, runtime.SpatialAnimationRuntimeCount);
        Assert.Equal(1, runtime.SpatialRemoteMotionRuntimeCount);
        Assert.Equal(1, runtime.SpatialProjectileRuntimeCount);

        Assert.True(runtime.WithdrawLiveEntityProjection(guid));
        Assert.Equal(0, runtime.SpatialAnimationRuntimeCount);
        Assert.Equal(0, runtime.SpatialRemoteMotionRuntimeCount);
        Assert.Equal(0, runtime.SpatialProjectileRuntimeCount);
    }

    [Fact]
    public void AnimationView_EnumeratesSpatialOwnersButRetainsLogicalLookupAndRemoval()
    {
        const uint guid = 0x70000071u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        WorldEntity entity = runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid))!;
        var animation = new AnimationRuntime(entity);
        runtime.SetAnimationRuntime(guid, animation);
        var view = new LiveEntityAnimationRuntimeView<AnimationRuntime>(BoundSlot(runtime));
        var spatialIds = new HashSet<uint>();

        Assert.Equal(1, view.Count);
        view.CopySpatialIdsTo(spatialIds);
        Assert.Equal(entity.Id, Assert.Single(spatialIds));
        Assert.Same(animation, Assert.Single(view).Value);

        Assert.True(runtime.RebucketLiveEntity(guid, 0x02020001u));
        Assert.Equal(0, view.Count);
        view.CopySpatialIdsTo(spatialIds);
        Assert.Empty(spatialIds);
        Assert.Empty(view);
        Assert.True(view.TryGetValue(entity.Id, out AnimationRuntime retained));
        Assert.Same(animation, retained);

        Assert.True(view.Remove(entity.Id));
        Assert.False(view.TryGetValue(entity.Id, out _));
        Assert.Equal(0, runtime.AnimationRuntimeCount);
        Assert.Equal(0, runtime.SpatialAnimationRuntimeCount);
    }

    [Fact]
    public void AnimationView_RebucketCallbackSkipsDisplacedSnapshotOwner()
    {
        const uint firstGuid = 0x70000073u;
        const uint secondGuid = 0x70000074u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        foreach (uint guid in new[] { firstGuid, secondGuid })
        {
            runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
            WorldEntity entity = runtime.MaterializeLiveEntity(
                guid,
                0x01010001u,
                id => Entity(id, guid))!;
            runtime.SetAnimationRuntime(guid, new AnimationRuntime(entity));
        }
        var view = new LiveEntityAnimationRuntimeView<AnimationRuntime>(BoundSlot(runtime));
        var visited = new List<uint>();

        foreach (KeyValuePair<uint, AnimationRuntime> pair in view)
        {
            uint guid = pair.Value.Entity.ServerGuid;
            visited.Add(guid);
            uint other = guid == firstGuid ? secondGuid : firstGuid;
            Assert.True(runtime.RebucketLiveEntity(other, 0x02020001u));
        }

        Assert.Single(visited);
        Assert.Equal(1, runtime.SpatialAnimationRuntimeCount);
        Assert.Equal(2, runtime.AnimationRuntimeCount);
    }

    [Fact]
    public void AnimationView_HotSpatialTraversalDoesNotAllocateAfterWarmup()
    {
        const uint guid = 0x70000075u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        WorldEntity entity = runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid))!;
        runtime.SetAnimationRuntime(guid, new AnimationRuntime(entity));
        var view = new LiveEntityAnimationRuntimeView<AnimationRuntime>(
            BoundSlot(runtime));
        var ids = new HashSet<uint>();

        view.CopySpatialIdsTo(ids);
        foreach (KeyValuePair<uint, AnimationRuntime> _ in view) { }

        int visits = 0;
        ZeroAllocationProbe.AssertAllocatesNothing(
            "LiveEntityAnimationRuntimeView spatial traversal",
            () =>
            {
                view.CopySpatialIdsTo(ids);
                foreach (KeyValuePair<uint, AnimationRuntime> _ in view)
                    visits++;
            });

        Assert.True(visits > 0);
        Assert.Equal(entity.Id, Assert.Single(ids));
    }

    [Fact]
    public void SpatialComponentWorksets_IsolateGuidReuseAcrossVisibilityCallbacks()
    {
        const uint guid = 0x70000072u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        WorldEntity oldEntity = runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid))!;
        var oldAnimation = new AnimationRuntime(oldEntity);
        runtime.SetAnimationRuntime(guid, oldAnimation);

        runtime.ProjectionVisibilityChanged += (record, visible) =>
        {
            if (visible || record.Generation != 1)
                return;

            runtime.RegisterLiveEntity(Spawn(guid, 2, 1, 0x01010002u));
            WorldEntity replacement = runtime.MaterializeLiveEntity(
                guid,
                0x01010002u,
                id => Entity(id, guid))!;
            runtime.SetAnimationRuntime(guid, new AnimationRuntime(replacement));
        };

        Assert.False(runtime.WithdrawLiveEntityProjection(guid));
        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord current));
        Assert.Equal((ushort)2, current.Generation);
        Assert.True(runtime.IsCurrentSpatialAnimation(
            current,
            current.AnimationRuntime!));
        Assert.NotSame(oldAnimation, current.AnimationRuntime);
    }

    [Fact]
    public void CanonicalPhysicsBody_IsCreatedOncePerIncarnationAndLaterReused()
    {
        const uint guid = 0x70000073u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid));
        int factoryCalls = 0;

        PhysicsBody first = runtime.GetOrCreatePhysicsBody(
            guid,
            _ =>
            {
                factoryCalls++;
                return new PhysicsBody();
            });
        PhysicsBody retained = runtime.GetOrCreatePhysicsBody(
            guid,
            _ =>
            {
                factoryCalls++;
                return new PhysicsBody();
            });

        Assert.Same(first, retained);
        Assert.Equal(1, factoryCalls);
        Assert.True(runtime.TryGetRecord(guid, out var firstRecord));
        Assert.Same(first, firstRecord.PhysicsBody);

        Assert.True(runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(guid, InstanceSequence: 1),
            isLocalPlayer: false));
        runtime.RegisterLiveEntity(Spawn(guid, 2, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid));
        PhysicsBody replacement = runtime.GetOrCreatePhysicsBody(
            guid,
            _ =>
            {
                factoryCalls++;
                return new PhysicsBody();
            });

        Assert.NotSame(first, replacement);
        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public void EffectProfile_SurvivesRebucketAndClearsWithLogicalTeardown()
    {
        const uint guid = 0x70000030u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        spatial.AddLandblock(EmptyLandblock(0x0102FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        WorldSession.EntitySpawn spawn = Spawn(guid, 6, 1, 0x01010001u);
        LiveEntityRegistrationResult registration =
            runtime.RegisterLiveEntity(spawn);
        RuntimeEntityRecord canonical = Assert.IsType<RuntimeEntityRecord>(
            registration.Canonical);
        var profile = new EffectProfile();
        runtime.MaterializeLiveEntity(
            canonical,
            spawn.Position!.Value.LandblockId,
            id => Entity(id, guid),
            LiveEntityProjectionKind.World,
            initializeProjection: record => record.EffectProfile = profile,
            out _);

        Assert.True(runtime.RebucketLiveEntity(guid, 0x01020001u));
        Assert.True(runtime.TryGetEffectProfile(guid, out var retained));
        Assert.Same(profile, retained);

        Assert.True(runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(guid, spawn.InstanceSequence),
            isLocalPlayer: false));
        Assert.False(runtime.TryGetEffectProfile(guid, out _));
    }

    [Fact]
    public void InitialChildCreate_PreservesParentEventQueuedForRenderValidation()
    {
        const uint parentGuid = 0x70000020u;
        const uint childGuid = 0x70000021u;
        LiveEntityRuntimeFixture.DrivenLiveEntityRuntime driven =
            LiveEntityRuntimeFixture.CreateDriven(
                new GpuWorldState(),
                new RecordingResources());
        LiveEntityRuntime runtime = driven.Runtime;
        runtime.RegisterLiveEntity(Spawn(parentGuid, 9, 1, 0x01010001u));
        driven.Pump();
        runtime.ParentAttachments.Enqueue(new ParentEvent.Parsed(
            parentGuid, childGuid, 1, 2, 9, 5));
        ResolveParent(runtime, childGuid);

        runtime.RegisterLiveEntity(Spawn(childGuid, 3, 4, 0x01010001u));
        driven.Pump();
        ResolveParent(runtime, childGuid);

        Assert.True(runtime.ParentAttachments.TryGetProjection(
            childGuid, out ParentAttachmentRelation relation));
        Assert.Equal(parentGuid, relation.ParentGuid);
        Assert.True(runtime.TryGetSnapshot(childGuid, out WorldSession.EntitySpawn child));
        Assert.NotNull(child.Position);
        Assert.Null(child.ParentGuid);
        Assert.Equal((ushort)5, child.PositionSequence);
    }

    [Fact]
    public void InitialParentCreate_PreservesCreateObjectRelationWaitingForParent()
    {
        const uint parentGuid = 0x70000022u;
        const uint childGuid = 0x70000023u;
        var runtime = LiveEntityRuntimeFixture.Create(new GpuWorldState(), new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(childGuid, 3, 4, 0x01010001u));
        runtime.ParentAttachments.AcceptCreateObjectRelation(new ParentAttachmentRelation(
            parentGuid, childGuid, 1, 2, 9, 4));

        runtime.RegisterLiveEntity(Spawn(parentGuid, 9, 1, 0x01010001u));

        Assert.True(runtime.ParentAttachments.TryGetProjection(
            childGuid, out ParentAttachmentRelation relation));
        Assert.Equal(parentGuid, relation.ParentGuid);
    }

    [Fact]
    public void SameGenerationCreate_DoesNotReconstructProjection()
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new RecordingResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        WorldSession.EntitySpawn first = Spawn(0x70000002u, 4, 10, 0x01010001u);
        runtime.RegisterLiveEntity(first);
        WorldEntity original = runtime.MaterializeLiveEntity(
            first.Guid,
            first.Position!.Value.LandblockId,
            id => Entity(id, first.Guid))!;

        LiveEntityRegistrationResult refresh = runtime.RegisterLiveEntity(
            Spawn(first.Guid, 4, 11, 0x01010001u));
        WorldEntity retained = runtime.MaterializeLiveEntity(
            first.Guid,
            first.Position.Value.LandblockId,
            _ => throw new InvalidOperationException("same generation reconstructed"))!;

        Assert.False(refresh.LogicalRegistrationCreated);
        Assert.Same(original, retained);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(0, resources.UnregisterCount);
    }

    [Fact]
    public void NewGeneration_TearsDownExactlyOnceAndReusesNoLocalIdentity()
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new RecordingResources();
        int runtimeTearDowns = 0;
        var runtime = LiveEntityRuntimeFixture.Create(
            spatial,
            resources,
            _ => runtimeTearDowns++);
        const uint guid = 0x70000003u;

        runtime.RegisterLiveEntity(Spawn(guid, 9, 1, 0x01010001u));
        WorldEntity old = runtime.MaterializeLiveEntity(
            guid, 0x01010001u, id => Entity(id, guid))!;
        LiveEntityRegistrationResult replacement = runtime.RegisterLiveEntity(
            Spawn(guid, 10, 1, 0x01010001u));
        WorldEntity current = runtime.MaterializeLiveEntity(
            guid, 0x01010001u, id => Entity(id, guid))!;

        Assert.True(replacement.ReplacedExistingGeneration);
        Assert.NotEqual(old.Id, current.Id);
        Assert.Equal(2, resources.RegisterCount);
        Assert.Equal(1, resources.UnregisterCount);
        Assert.Equal(1, runtimeTearDowns);
        Assert.Single(spatial.Entities);
        Assert.Same(current, spatial.Entities[0]);
        Assert.False(runtime.TryGetServerGuid(old.Id, out _));
        Assert.True(runtime.TryGetServerGuid(current.Id, out uint mapped));
        Assert.Equal(guid, mapped);
    }

    [Fact]
    public void AppearanceUpdate_MutatesSnapshotWithoutChangingIdentity()
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new RecordingResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        WorldSession.EntitySpawn spawn = Spawn(0x70000004u, 2, 3, 0x01010001u);
        runtime.RegisterLiveEntity(spawn);
        WorldEntity entity = runtime.MaterializeLiveEntity(
            spawn.Guid, 0x01010001u, id => Entity(id, spawn.Guid))!;

        var update = new ObjDescEvent.Parsed(
            spawn.Guid,
            new CreateObject.ModelData(
                0x04000001u,
                Array.Empty<CreateObject.SubPaletteSwap>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.AnimPartChange>()),
            InstanceSequence: 2,
            ObjDescSequence: 2);
        Assert.True(runtime.TryApplyObjDesc(update, out _));

        Assert.True(runtime.TryGetRecord(spawn.Guid, out LiveEntityRecord record));
        Assert.Same(entity, record.WorldEntity);
        Assert.Equal((uint)0x04000001u, record.Snapshot.BasePaletteId);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(0, resources.UnregisterCount);
    }

    [Fact]
    public void DeleteAndGuidReuse_HaveSeparateLogicalLifetimes()
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new RecordingResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        const uint guid = 0x70000005u;
        runtime.RegisterLiveEntity(Spawn(guid, 3, 1, 0x01010001u));
        WorldEntity first = runtime.MaterializeLiveEntity(
            guid, 0x01010001u, id => Entity(id, guid))!;

        Assert.True(runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(guid, 3),
            isLocalPlayer: false));
        Assert.Empty(spatial.Entities);
        Assert.False(runtime.TryGetRecord(guid, out _));

        runtime.RegisterLiveEntity(Spawn(guid, 3, 1, 0x01010001u));
        WorldEntity second = runtime.MaterializeLiveEntity(
            guid, 0x01010001u, id => Entity(id, guid))!;

        Assert.NotEqual(first.Id, second.Id);
        Assert.False(runtime.TryGetInteractionEligibleRecord(guid, first.Id, out _));
        Assert.True(runtime.TryGetInteractionEligibleRecord(guid, second.Id, out var current));
        Assert.Same(second, current.WorldEntity);
        Assert.Equal(2, resources.RegisterCount);
        Assert.Equal(1, resources.UnregisterCount);
    }

    [Fact]
    public void AttachedProjection_RendersWithoutEnteringTopLevelWorldIndex()
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new RecordingResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        WorldSession.EntitySpawn spawn = Spawn(0x70000006u, 3, 1, 0x01010001u);
        runtime.RegisterLiveEntity(spawn);

        WorldEntity attached = runtime.MaterializeLiveEntity(
            spawn.Guid,
            spawn.Position!.Value.LandblockId,
            id => Entity(id, spawn.Guid),
            LiveEntityProjectionKind.Attached)!;

        Assert.Single(spatial.Entities);
        Assert.Empty(runtime.VisibleRecords);
        Assert.False(runtime.TryMarkWorldSpawnPublished(spawn.Guid));
        Assert.True(runtime.TryGetWorldEntity(spawn.Guid, out WorldEntity resolved));
        Assert.Same(attached, resolved);
        Assert.Equal(1, resources.RegisterCount);
        attached.IsAncestorDrawVisible = false;

        WorldEntity same = runtime.MaterializeLiveEntity(
            spawn.Guid,
            spawn.Position.Value.LandblockId,
            _ => throw new InvalidOperationException("attachment reconstructed"),
            LiveEntityProjectionKind.World)!;

        Assert.Same(attached, same);
        Assert.True(same.IsAncestorDrawVisible);
        Assert.Same(attached, Assert.Single(runtime.VisibleRecords).WorldEntity);
        Assert.True(runtime.TryMarkWorldSpawnPublished(spawn.Guid));
        Assert.False(runtime.TryMarkWorldSpawnPublished(spawn.Guid));
        Assert.Equal(1, resources.RegisterCount);
    }

    [Fact]
    public void PendingToLoadedTransition_DoesNotReplayLogicalRegistration()
    {
        var spatial = new GpuWorldState();
        var resources = new RecordingResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        WorldSession.EntitySpawn spawn = Spawn(0x70000007u, 1, 1, 0x01020001u);
        runtime.RegisterLiveEntity(spawn);
        WorldEntity entity = runtime.MaterializeLiveEntity(
            spawn.Guid,
            spawn.Position!.Value.LandblockId,
            id => Entity(id, spawn.Guid))!;

        Assert.Empty(spatial.Entities);
        Assert.Equal(1, spatial.PendingLiveEntityCount);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Empty(runtime.VisibleRecords);
        Assert.Same(entity, Assert.Single(runtime.MaterializedRecords).WorldEntity);

        spatial.AddLandblock(EmptyLandblock(0x0102FFFFu));
        Assert.Same(entity, Assert.Single(spatial.Entities));
        Assert.Same(entity, Assert.Single(runtime.VisibleRecords).WorldEntity);
        Assert.True(runtime.RebucketLiveEntity(spawn.Guid, 0x01020001u));
        Assert.Same(entity, Assert.Single(spatial.Entities));
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(0, resources.UnregisterCount);
    }

    [Fact]
    public void PendingProjection_RemainsCanonicalAcrossAuthoritativeUpdatesWithoutReregister()
    {
        const uint guid = 0x70000025u;
        var spatial = new GpuWorldState();
        var resources = new RecordingResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        WorldSession.EntitySpawn spawn = Spawn(guid, 2, 1, 0x01010001u);
        runtime.RegisterLiveEntity(spawn);
        WorldEntity entity = runtime.MaterializeLiveEntity(
            guid,
            spawn.Position!.Value.LandblockId,
            id => Entity(id, guid))!;

        Assert.Empty(runtime.VisibleRecords);
        Assert.Same(entity, Assert.Single(runtime.MaterializedRecords).WorldEntity);
        Assert.True(runtime.TryApplyVector(
            new VectorUpdate.Parsed(
                guid,
                new System.Numerics.Vector3(2f, 3f, 4f),
                System.Numerics.Vector3.Zero,
                spawn.InstanceSequence,
                2),
            out _));
        var positionUpdate = new WorldSession.EntityPositionUpdate(
            guid,
            new CreateObject.ServerPosition(
                0x01020001u, 20f, 30f, 6f, 1f, 0f, 0f, 0f),
            null,
            null,
            true,
            spawn.InstanceSequence,
            2,
            0,
            0);
        Assert.True(runtime.TryApplyPosition(
            positionUpdate,
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            out PositionTimestampDisposition disposition,
            out WorldSession.EntitySpawn accepted,
            out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        Assert.True(runtime.RebucketLiveEntity(guid, accepted.Position!.Value.LandblockId));

        Assert.Empty(runtime.VisibleRecords);
        Assert.Same(entity, Assert.Single(runtime.MaterializedRecords).WorldEntity);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(0, resources.UnregisterCount);
        Assert.Equal(1, spatial.PendingLiveEntityCount);
    }

    [Fact]
    public void PickupLeaveWorld_PreservesOwnersButPausesRootUntilPositionReentry()
    {
        const uint guid = 0x70000026u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new RecordingResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        WorldSession.EntitySpawn spawn = Spawn(guid, 4, 10, 0x01010001u);
        runtime.RegisterLiveEntity(spawn);
        WorldEntity entity = runtime.MaterializeLiveEntity(
            guid,
            spawn.Position!.Value.LandblockId,
            id => Entity(id, guid))!;
        var animation = new AnimationRuntime(entity);
        runtime.SetAnimationRuntime(guid, animation);
        var remote = new RemoteMotionRuntime();
        runtime.SetRemoteMotionRuntime(guid, remote);
        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord record));
        ulong initialClockEpoch = record.ObjectClockEpoch;
        Assert.True(record.ObjectClock.IsActive);
        Assert.True(remote.Body.IsActive);
        uint localId = entity.Id;
        Assert.True(runtime.TryMarkWorldSpawnPublished(guid));

        Assert.True(runtime.TryApplyPickup(
            new PickupEvent.Parsed(guid, spawn.InstanceSequence, 11),
            out _));
        Assert.False(record.ObjectClock.IsActive);
        Assert.False(remote.Body.IsActive);
        Assert.Equal(initialClockEpoch + 1, record.ObjectClockEpoch);
        Assert.True(runtime.WithdrawLiveEntityProjection(guid));
        Assert.Equal(initialClockEpoch + 1, record.ObjectClockEpoch);

        Assert.Equal(0u, record.FullCellId);
        Assert.Equal(0u, record.CanonicalLandblockId);
        Assert.False(runtime.ShouldAdvanceRootRuntime(guid));
        Assert.True(runtime.TryGetAnimationRuntime(localId, out var retainedAnimation));
        Assert.Same(animation, retainedAnimation);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(0, resources.UnregisterCount);

        var positionUpdate = new WorldSession.EntityPositionUpdate(
            guid,
            new CreateObject.ServerPosition(
                0x01010002u, 30f, 40f, 7f, 1f, 0f, 0f, 0f),
            null,
            null,
            true,
            spawn.InstanceSequence,
            12,
            0,
            0);
        Assert.True(runtime.TryApplyPosition(
            positionUpdate,
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            out PositionTimestampDisposition disposition,
            out WorldSession.EntitySpawn accepted,
            out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        WorldEntity same = runtime.MaterializeLiveEntity(
            guid,
            accepted.Position!.Value.LandblockId,
            _ => throw new InvalidOperationException("re-entry reconstructed the projection"))!;

        Assert.Same(entity, same);
        Assert.Equal(localId, same.Id);
        Assert.True(record.ObjectClock.IsActive);
        Assert.True(remote.Body.IsActive);
        Assert.Equal(initialClockEpoch + 2, record.ObjectClockEpoch);
        Assert.False(runtime.TryMarkWorldSpawnPublished(guid));
        Assert.True(runtime.ShouldAdvanceRootRuntime(guid));
        Assert.Equal(0x01010002u, record.FullCellId);
        Assert.True(runtime.TryGetAnimationRuntime(localId, out retainedAnimation));
        Assert.Same(animation, retainedAnimation);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(0, resources.UnregisterCount);
    }

    [Fact]
    public void RootRuntimePausesWhilePendingOrFrozenAndResumesInVisibleCell()
    {
        const uint guid = 0x70000027u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        WorldSession.EntitySpawn spawn = Spawn(guid, 1, 1, 0x01010001u);
        runtime.RegisterLiveEntity(spawn);
        runtime.MaterializeLiveEntity(
            guid,
            spawn.Position!.Value.LandblockId,
            id => Entity(id, guid));

        Assert.True(runtime.ShouldAdvanceRootRuntime(guid));
        Assert.True(runtime.RebucketLiveEntity(guid, 0x02020001u));
        Assert.False(runtime.ShouldAdvanceRootRuntime(guid));

        spatial.AddLandblock(EmptyLandblock(0x0202FFFFu));
        Assert.True(runtime.ShouldAdvanceRootRuntime(guid));

        Assert.True(runtime.TryApplyState(
            new SetState.Parsed(
                guid,
                (uint)(PhysicsStateFlags.ReportCollisions | PhysicsStateFlags.Frozen),
                1,
                2),
            out _));
        Assert.False(runtime.ShouldAdvanceRootRuntime(guid));

        Assert.True(runtime.TryApplyState(
            new SetState.Parsed(
                guid,
                (uint)PhysicsStateFlags.ReportCollisions,
                1,
                3),
            out _));
        Assert.True(runtime.ShouldAdvanceRootRuntime(guid));
    }

    [Fact]
    public void ObjectClock_SurvivesLoadedRebucketAndRebasesPendingReentry()
    {
        const uint guid = 0x70000047u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        spatial.AddLandblock(EmptyLandblock(0x0102FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        WorldSession.EntitySpawn spawn = Spawn(guid, 1, 1, 0x01010001u);
        LiveEntityRecord record = RegisterProjection(runtime, spawn);
        RetailObjectQuantumClock clock = record.ObjectClock;
        Assert.Equal(0, clock.Advance(0.02).Count);

        Assert.True(runtime.RebucketLiveEntity(guid, 0x01020001u));
        Assert.Same(clock, record.ObjectClock);
        Assert.True(clock.IsActive);
        Assert.Equal(0.02, clock.PendingSeconds, 8);

        Assert.True(runtime.RebucketLiveEntity(guid, 0x02020001u));
        Assert.False(clock.IsActive);
        Assert.Equal(0.02, clock.PendingSeconds, 8);
        spatial.AddLandblock(EmptyLandblock(0x0202FFFFu));
        Assert.True(clock.IsActive);
        Assert.Equal(0.0, clock.PendingSeconds, 8);

        Assert.Equal(
            RetailObjectActivityResult.Active,
            RetailObjectActivityGate.Evaluate(
                clock,
                body: null,
                lifecycleEligible: runtime.ShouldAdvanceRootRuntime(guid),
                hasPartArray: true,
                isStatic: false,
                objectPosition: Vector3.Zero,
                playerPosition: Vector3.Zero,
                elapsedSeconds: 0.1));
        Assert.Same(clock, record.ObjectClock);
        Assert.Equal(0.0, clock.PendingSeconds, 8);
    }

    [Fact]
    public void PostResidenceRebucket_TakesTheFullLegacyPathIncludingTheClockEdge()
    {
        const uint guid = 0x7000004Au;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        spatial.AddLandblock(EmptyLandblock(0x0102FFFFu));
        LiveEntityRuntimeFixture.DrivenLiveEntityRuntime driven =
            LiveEntityRuntimeFixture.CreateDriven(
                spatial,
                new RecordingResources());
        LiveEntityRuntime runtime = driven.Runtime;
        RuntimeEntityRecord canonical =
            Assert.IsType<RuntimeEntityRecord>(runtime.RegisterLiveEntity(
                Spawn(guid, 1, 1, 0x01010001u)).Canonical);
        runtime.MaterializeLiveEntity(
            canonical,
            0x01010001u,
            id => Entity(id, guid),
            LiveEntityProjectionKind.World,
            initializeProjection: null,
            out _,
            LiveEntityMaterializationResidence.AwaitRuntimePlacement);
        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord record));

        Assert.True(runtime.HasActiveInitialCreateResidence(canonical));
        Assert.False(runtime.RebucketLiveEntity(guid, 0x01020001u));
        Assert.Equal(0u, canonical.FullCellId);
        // C3c-R1 review F5: the tracked-but-undriven entry is visible in
        // the entity-object ownership ledger while it awaits its pump.
        Assert.Equal(
            1,
            driven.Lifetime.CaptureOwnership().FirstEntryDrivePendingCount);

        driven.Pump();
        Assert.False(runtime.HasActiveInitialCreateResidence(canonical));
        Assert.Equal(
            LiveEntityMaterializationResidence.AwaitRuntimePlacement,
            record.MaterializationResidence);
        Assert.Equal(0x01010001u, canonical.FullCellId);

        // C3c-R1 review F5: the drive's tracked entries fold into the
        // entity-object ownership ledger — one pending entry while the
        // residence awaited its pump, zero after.
        Assert.Equal(
            0,
            driven.Lifetime.CaptureOwnership().FirstEntryDrivePendingCount);

        RetailObjectQuantumClock clock = record.ObjectClock;
        Assert.Equal(0, clock.Advance(0.02).Count);
        Assert.True(runtime.RebucketLiveEntity(guid, 0x01020001u));
        Assert.Equal(0x01020001u, canonical.FullCellId);
        Assert.Same(clock, record.ObjectClock);
        Assert.True(clock.IsActive);
        Assert.Equal(0.02, clock.PendingSeconds, 8);

        Assert.True(runtime.RebucketLiveEntity(guid, 0x02020001u));
        Assert.Equal(0x02020001u, canonical.FullCellId);
        Assert.False(clock.IsActive);
        spatial.AddLandblock(EmptyLandblock(0x0202FFFFu));
        Assert.True(clock.IsActive);
        Assert.Equal(0.0, clock.PendingSeconds, 8);
    }

    [Fact]
    public void ResidenceConversionToLegacyImmediate_RefusesWhileTheResidenceIsActive()
    {
        const uint guid = 0x7000004Bu;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        LiveEntityRuntimeFixture.DrivenLiveEntityRuntime driven =
            LiveEntityRuntimeFixture.CreateDriven(
                spatial,
                new RecordingResources());
        LiveEntityRuntime runtime = driven.Runtime;
        RuntimeEntityRecord canonical =
            Assert.IsType<RuntimeEntityRecord>(runtime.RegisterLiveEntity(
                Spawn(guid, 1, 1, 0x01010001u)).Canonical);
        runtime.MaterializeLiveEntity(
            canonical,
            0x01010001u,
            id => Entity(id, guid),
            LiveEntityProjectionKind.World,
            initializeProjection: null,
            out _,
            LiveEntityMaterializationResidence.AwaitRuntimePlacement);
        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord record));

        Assert.Throws<InvalidOperationException>(() =>
            runtime.ConvertMaterializationResidenceToLegacyImmediate(record));
        Assert.Equal(
            LiveEntityMaterializationResidence.AwaitRuntimePlacement,
            record.MaterializationResidence);

        driven.Pump();
        runtime.ConvertMaterializationResidenceToLegacyImmediate(record);
        Assert.Equal(
            LiveEntityMaterializationResidence.LegacyImmediate,
            record.MaterializationResidence);
        runtime.ConvertMaterializationResidenceToLegacyImmediate(record);
        Assert.Equal(
            LiveEntityMaterializationResidence.LegacyImmediate,
            record.MaterializationResidence);
    }

    [Fact]
    public void InitiallyVisibleStaticObject_RebasesWithoutBecomingActive()
    {
        const uint guid = 0x70000049u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        WorldSession.EntitySpawn spawn = Spawn(
            guid,
            1,
            1,
            0x01010001u,
            PhysicsStateFlags.Static);
        LiveEntityRecord record = RegisterProjection(runtime, spawn);
        var remote = new RemoteMotionRuntime();
        runtime.SetRemoteMotionRuntime(guid, remote);

        Assert.False(record.ObjectClock.IsActive);
        Assert.Equal(0.0, record.ObjectClock.PendingSeconds, 8);
        Assert.False(remote.Body.TransientState.HasFlag(TransientStateFlags.Active));
    }

    [Fact]
    public void AuthoritativeVector_WakesOrdinaryClockButPreservesStaticActivity()
    {
        const uint ordinaryGuid = 0x70000052u;
        const uint defensiveGuid = 0x70000053u;
        const uint staticGuid = 0x70000054u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());

        LiveEntityRecord ordinary = RegisterProjection(
            runtime,
            Spawn(ordinaryGuid, 1, 1, 0x01010001u));
        var ordinaryRemote = new RemoteMotionRuntime();
        runtime.SetRemoteMotionRuntime(ordinaryGuid, ordinaryRemote);
        ordinary.ObjectClock.Deactivate();
        ordinaryRemote.Body.TransientState &= ~TransientStateFlags.Active;
        ordinaryRemote.Body.LastUpdateTime = 1.0;

        Assert.True(runtime.TryCommitAuthoritativeVector(
            ordinary,
            ordinaryRemote.Body,
            new Vector3(1f, 2f, 3f),
            new Vector3(4f, 5f, 6f),
            currentTime: 8.0));
        Assert.True(ordinary.ObjectClock.IsActive);
        Assert.True(ordinaryRemote.Body.IsActive);
        Assert.Equal(8.0, ordinaryRemote.Body.LastUpdateTime);
        Assert.Equal(new Vector3(1f, 2f, 3f), ordinaryRemote.Body.Velocity);
        Assert.Equal(new Vector3(4f, 5f, 6f), ordinaryRemote.Body.Omega);

        LiveEntityRecord defensive = RegisterProjection(
            runtime,
            Spawn(defensiveGuid, 1, 1, 0x01010001u));
        var defensiveRemote = new RemoteMotionRuntime();
        runtime.SetRemoteMotionRuntime(defensiveGuid, defensiveRemote);
        Assert.True(defensive.ObjectClock.IsActive);
        defensiveRemote.Body.TransientState &= ~TransientStateFlags.Active;
        defensiveRemote.Body.LastUpdateTime = 2.0;

        Assert.True(runtime.TryCommitAuthoritativeVelocity(
            defensive,
            defensiveRemote.Body,
            Vector3.UnitX,
            currentTime: 9.0));
        Assert.True(defensive.ObjectClock.IsActive);
        Assert.True(defensiveRemote.Body.IsActive);
        Assert.Equal(9.0, defensiveRemote.Body.LastUpdateTime);

        LiveEntityRecord staticRecord = RegisterProjection(
            runtime,
            Spawn(
                staticGuid,
                1,
                1,
                0x01010001u,
                PhysicsStateFlags.Static));
        var staticRemote = new RemoteMotionRuntime();
        runtime.SetRemoteMotionRuntime(staticGuid, staticRemote);
        staticRemote.Body.TransientState &= ~TransientStateFlags.Active;
        staticRemote.Body.LastUpdateTime = 3.0;

        Assert.True(runtime.TryCommitAuthoritativeVector(
            staticRecord,
            staticRemote.Body,
            new Vector3(7f, 8f, 9f),
            new Vector3(10f, 11f, 12f),
            currentTime: 10.0));
        Assert.False(staticRecord.ObjectClock.IsActive);
        Assert.False(staticRemote.Body.IsActive);
        Assert.Equal(3.0, staticRemote.Body.LastUpdateTime);
        Assert.Equal(new Vector3(7f, 8f, 9f), staticRemote.Body.Velocity);
        Assert.Equal(new Vector3(10f, 11f, 12f), staticRemote.Body.Omega);
    }

    [Fact]
    public void ParentAndPickupInvalidateOlderPositionAndVelocityAuthority()
    {
        const uint parentGuid = 0x50000010u;
        const uint childGuid = 0x70000055u;
        var runtime = LiveEntityRuntimeFixture.Create(new GpuWorldState(), new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(parentGuid, 1, 1, 0x01010001u));
        LiveEntityRecord child = RegisterProjection(
            runtime,
            Spawn(childGuid, 1, 1, 0x01010001u));
        ulong initialPosition = child.PositionAuthorityVersion;
        ulong initialVelocity = child.VelocityAuthorityVersion;

        Assert.True(runtime.TryApplyParent(
            new ParentEvent.Parsed(
                parentGuid,
                childGuid,
                ParentLocation: 0x00000001u,
                PlacementId: 0x00000002u,
                ParentInstanceSequence: 1,
                ChildPositionSequence: 2),
            out _));
        Assert.False(runtime.IsCurrentPositionAuthority(child, initialPosition));
        Assert.False(runtime.IsCurrentVelocityAuthority(child, initialVelocity));
        ulong parentedPosition = child.PositionAuthorityVersion;
        ulong parentedVelocity = child.VelocityAuthorityVersion;

        Assert.True(runtime.TryApplyPickup(
            new PickupEvent.Parsed(childGuid, 1, 3),
            out _));
        Assert.False(runtime.IsCurrentPositionAuthority(child, parentedPosition));
        Assert.False(runtime.IsCurrentVelocityAuthority(child, parentedVelocity));
    }

    [Fact]
    public void ObjectClock_WithdrawalRetainsIncarnationButGuidReuseGetsFreshOwner()
    {
        const uint guid = 0x70000048u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        WorldSession.EntitySpawn firstSpawn = Spawn(guid, 1, 1, 0x01010001u);
        LiveEntityRecord first = RegisterProjection(runtime, firstSpawn);
        RetailObjectQuantumClock firstClock = first.ObjectClock;
        Assert.Equal(0, firstClock.Advance(0.02).Count);

        Assert.True(runtime.WithdrawLiveEntityProjection(guid));
        Assert.Same(firstClock, first.ObjectClock);
        Assert.False(firstClock.IsActive);

        Assert.True(runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(guid, InstanceSequence: 1),
            isLocalPlayer: false));
        WorldSession.EntitySpawn secondSpawn = Spawn(guid, 2, 2, 0x01010001u);
        LiveEntityRecord second = RegisterProjection(runtime, secondSpawn);

        Assert.NotSame(first, second);
        Assert.NotSame(firstClock, second.ObjectClock);
        Assert.True(second.ObjectClock.IsActive);
        Assert.Equal(0.0, second.ObjectClock.PendingSeconds, 8);
    }

    [Fact]
    public void PhysicsStateAndRemoteBodyStaySynchronizedAcrossEitherArrivalOrder()
    {
        const uint stateBeforeBindGuid = 0x70000037u;
        const uint bindBeforeStateGuid = 0x70000038u;
        LiveEntityRuntimeFixture.DrivenLiveEntityRuntime driven =
            LiveEntityRuntimeFixture.CreateDriven(
                new GpuWorldState(),
                new RecordingResources());
        LiveEntityRuntime runtime = driven.Runtime;
        RuntimeEntityRecord stateBeforeBindCanonical =
            Assert.IsType<RuntimeEntityRecord>(runtime.RegisterLiveEntity(
                Spawn(stateBeforeBindGuid, 1, 1, 0x01010001u)).Canonical);
        RuntimeEntityRecord bindBeforeStateCanonical =
            Assert.IsType<RuntimeEntityRecord>(runtime.RegisterLiveEntity(
                Spawn(bindBeforeStateGuid, 1, 1, 0x01010001u)).Canonical);

        PhysicsStateFlags firstState = PhysicsStateFlags.Hidden
            | PhysicsStateFlags.Gravity
            | PhysicsStateFlags.ReportCollisions;
        Assert.True(runtime.TryApplyState(
            new SetState.Parsed(stateBeforeBindGuid, (uint)firstState, 1, 2),
            out _));
        runtime.MaterializeLiveEntity(
            stateBeforeBindCanonical,
            0x01010001u,
            id => Entity(id, stateBeforeBindGuid),
            LiveEntityProjectionKind.World,
            initializeProjection: null,
            out _,
            LiveEntityMaterializationResidence.AwaitRuntimePlacement);
        runtime.MaterializeLiveEntity(
            bindBeforeStateCanonical,
            0x01010001u,
            id => Entity(id, bindBeforeStateGuid),
            LiveEntityProjectionKind.World,
            initializeProjection: null,
            out _,
            LiveEntityMaterializationResidence.AwaitRuntimePlacement);
        driven.Pump();
        PhysicsBody lateBody = runtime.GetOrCreatePhysicsBody(
            stateBeforeBindGuid,
            static _ => throw new InvalidOperationException(
                "The conductor-built canonical body should already exist."));
        PhysicsBody earlyBody = runtime.GetOrCreatePhysicsBody(
            bindBeforeStateGuid,
            static _ => throw new InvalidOperationException(
                "The conductor-built canonical body should already exist."));
        PhysicsStateFlags secondState = PhysicsStateFlags.Static
            | PhysicsStateFlags.Ethereal
            | PhysicsStateFlags.NoDraw;
        Assert.True(runtime.TryApplyState(
            new SetState.Parsed(bindBeforeStateGuid, (uint)secondState, 1, 2),
            out _));

        Assert.Equal((firstState & ~PhysicsStateFlags.ReportCollisions)
                     | PhysicsStateFlags.IgnoreCollisions,
            lateBody.State);
        Assert.Equal(secondState, earlyBody.State);
    }

    [Fact]
    public void ParentDrivenChildNoDrawKeepsBoundBodyInCanonicalState()
    {
        const uint guid = 0x70000039u;
        var runtime = LiveEntityRuntimeFixture.Create(new GpuWorldState(), new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid));
        var remote = new RemoteMotionRuntime();
        runtime.SetRemoteMotionRuntime(guid, remote);

        Assert.True(runtime.SetAttachedChildNoDraw(guid, true));
        Assert.True(remote.Body.State.HasFlag(PhysicsStateFlags.NoDraw));

        Assert.True(runtime.SetAttachedChildNoDraw(guid, false));
        Assert.False(remote.Body.State.HasFlag(PhysicsStateFlags.NoDraw));
    }

    [Fact]
    public void InitiallyVisibleCreate_DoesNotManufactureUnHideTransition()
    {
        const uint guid = 0x70000040u;
        var runtime = LiveEntityRuntimeFixture.Create(new GpuWorldState(), new RecordingResources());

        LiveEntityRecord record = RegisterProjection(
            runtime,
            Spawn(guid, 1, 1, 0x01010001u));

        Assert.True(record.TryDequeueStateTransition(out RetailPhysicsStateTransition transition));
        Assert.Equal(RetailHiddenTransition.None, transition.HiddenTransition);
        Assert.Equal(PhysicsStateFlags.ReportCollisions, record.FinalPhysicsState);
    }

    [Fact]
    public void HiddenCreate_SuppressesMeshInteractionButRetainsNarrowObjectTick()
    {
        const uint guid = 0x70000041u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(
            guid,
            1,
            1,
            0x01010001u,
            PhysicsStateFlags.Hidden | PhysicsStateFlags.ReportCollisions));
        WorldEntity entity = runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid))!;

        Assert.False(entity.IsDrawVisible);
        Assert.Empty(runtime.VisibleRecords);
        Assert.Same(entity, Assert.Single(runtime.MaterializedRecords).WorldEntity);
        Assert.Single(spatial.Entities);
        Assert.True(runtime.ShouldAdvanceRootRuntime(guid));
        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord record));
        Assert.Equal(PhysicsStateFlags.None,
            record.FinalPhysicsState & PhysicsStateFlags.ReportCollisions);
        Assert.NotEqual(PhysicsStateFlags.None,
            record.FinalPhysicsState & PhysicsStateFlags.IgnoreCollisions);
    }

    [Fact]
    public void HiddenThenVisibleState_RestoresSameMeshAndInteractionIdentity()
    {
        const uint guid = 0x70000042u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        WorldEntity entity = runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid))!;

        Assert.True(runtime.TryApplyState(new SetState.Parsed(
            guid,
            (uint)(PhysicsStateFlags.Hidden | PhysicsStateFlags.ReportCollisions),
            1,
            2), out _, out RetailPhysicsStateTransition hidden));
        Assert.Equal(RetailHiddenTransition.BecameHidden, hidden.HiddenTransition);
        Assert.False(entity.IsDrawVisible);
        Assert.Empty(runtime.VisibleRecords);
        Assert.False(runtime.TryGetInteractionEligibleEntity(guid, out _));
        Assert.False(runtime.TryGetInteractionEligibleRecord(guid, entity.Id, out _));

        Assert.True(runtime.TryApplyState(new SetState.Parsed(
            guid,
            0u,
            1,
            3), out _, out RetailPhysicsStateTransition visible));
        Assert.Equal(RetailHiddenTransition.BecameVisible, visible.HiddenTransition);
        Assert.True(entity.IsDrawVisible);
        Assert.True(runtime.TryGetInteractionEligibleEntity(guid, out var eligible));
        Assert.True(runtime.TryGetInteractionEligibleRecord(guid, entity.Id, out var eligibleRecord));
        Assert.Same(entity, eligibleRecord.WorldEntity);
        Assert.False(runtime.TryGetInteractionEligibleRecord(guid, entity.Id + 1u, out _));
        Assert.Same(entity, eligible);
        Assert.Same(entity, Assert.Single(runtime.VisibleRecords).WorldEntity);
        Assert.Same(entity, Assert.Single(spatial.Entities));
    }

    [Fact]
    public void PositionAfterPickup_RequiresTeleportHookEvenWithEqualTeleportStamp()
    {
        const uint guid = 0x70000043u;
        LiveEntityRuntimeFixture.DrivenLiveEntityRuntime driven =
            LiveEntityRuntimeFixture.CreateDriven(
                new GpuWorldState(),
                new RecordingResources());
        LiveEntityRuntime runtime = driven.Runtime;
        WorldSession.EntitySpawn spawn = Spawn(guid, 1, 1, 0x01010001u);
        runtime.RegisterLiveEntity(spawn);
        driven.Pump();
        Assert.True(runtime.TryApplyPickup(
            new PickupEvent.Parsed(guid, 1, 2),
            out _));

        var update = new WorldSession.EntityPositionUpdate(
            guid,
            new CreateObject.ServerPosition(
                0x01010002u, 12f, 13f, 5f, 1f, 0f, 0f, 0f),
            null,
            null,
            true,
            1,
            3,
            0,
            0);

        Assert.True(runtime.TryApplyPosition(
            update,
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            out PositionTimestampDisposition disposition,
            out _,
            out AcceptedPhysicsTimestamps timestamps));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        Assert.False(timestamps.TeleportAdvanced);
        Assert.Equal(0u, timestamps.PreMergeCommittedCellId);
    }

    [Fact]
    public void PositionFromPendingProjection_ReportsTheHonestPreMergeCellNotVisibility()
    {
        const uint guid = 0x70000044u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        WorldSession.EntitySpawn spawn = Spawn(guid, 1, 1, 0x01010001u);
        runtime.RegisterLiveEntity(spawn);
        runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid));
        spatial.RemoveLandblock(0x0101FFFFu);

        Assert.True(runtime.TryApplyPosition(
            new WorldSession.EntityPositionUpdate(
                guid,
                new CreateObject.ServerPosition(
                    0x01010002u, 12f, 13f, 5f, 1f, 0f, 0f, 0f),
                null,
                null,
                true,
                1,
                2,
                0,
                0),
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            out PositionTimestampDisposition disposition,
            out _,
            out AcceptedPhysicsTimestamps timestamps));

        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        Assert.False(timestamps.TeleportAdvanced);
        Assert.Equal(0x01010001u, timestamps.PreMergeCommittedCellId);
    }

    [Fact]
    public void LandblockUnload_HidesTopLevelViewAndReloadRestoresSameIdentity()
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new RecordingResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        WorldSession.EntitySpawn spawn = Spawn(0x70000024u, 1, 1, 0x01010001u);
        runtime.RegisterLiveEntity(spawn);
        WorldEntity entity = runtime.MaterializeLiveEntity(
            spawn.Guid,
            0x01010001u,
            id => Entity(id, spawn.Guid))!;

        spatial.RemoveLandblock(0x0101FFFFu);

        Assert.Empty(runtime.VisibleRecords);
        Assert.True(runtime.TryGetRecord(spawn.Guid, out LiveEntityRecord record));
        Assert.True(record.IsSpatiallyProjected);
        Assert.False(record.IsSpatiallyVisible);
        Assert.Equal(1, spatial.PendingLiveEntityCount);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(0, resources.UnregisterCount);

        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        Assert.Same(entity, Assert.Single(runtime.VisibleRecords).WorldEntity);
        Assert.True(record.IsSpatiallyVisible);
        Assert.Equal(1, resources.RegisterCount);
    }

    [Fact]
    public void LoadedToPendingToLoaded_RemovesOldDrawSlotWithoutLogicalRecreate()
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new RecordingResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        WorldSession.EntitySpawn spawn = Spawn(0x70000009u, 1, 1, 0x01010001u);
        runtime.RegisterLiveEntity(spawn);
        WorldEntity entity = runtime.MaterializeLiveEntity(
            spawn.Guid,
            spawn.Position!.Value.LandblockId,
            id => Entity(id, spawn.Guid))!;

        Assert.Same(entity, Assert.Single(spatial.Entities));
        Assert.True(runtime.RebucketLiveEntity(spawn.Guid, 0x01020001u));
        Assert.Empty(spatial.Entities);
        Assert.Equal(1, spatial.PendingLiveEntityCount);
        Assert.Equal(1, resources.RegisterCount);

        spatial.AddLandblock(EmptyLandblock(0x0102FFFFu));
        Assert.Same(entity, Assert.Single(spatial.Entities));
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(0, resources.UnregisterCount);
    }

    [Fact]
    public void SessionClear_IsSymmetricAndIdempotent()
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new RecordingResources();
        int runtimeTearDowns = 0;
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources, _ => runtimeTearDowns++);
        WorldSession.EntitySpawn spawn = Spawn(0x70000008u, 1, 1, 0x01010001u);
        runtime.RegisterLiveEntity(spawn);
        runtime.MaterializeLiveEntity(
            spawn.Guid,
            spawn.Position!.Value.LandblockId,
            id => Entity(id, spawn.Guid));

        runtime.Clear();
        runtime.Clear();

        Assert.Equal(0, runtime.Count);
        Assert.Empty(runtime.VisibleRecords);
        Assert.Empty(spatial.Entities);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(1, resources.UnregisterCount);
        Assert.Equal(1, runtimeTearDowns);
    }

    [Fact]
    public void ResourceRegistrationFailure_RollsBackMaterializedIdentityAndSpatialState()
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new FailingRegisterResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        WorldSession.EntitySpawn spawn = Spawn(0x7000000Au, 1, 1, 0x01010001u);
        runtime.RegisterLiveEntity(spawn);

        Assert.Throws<InvalidOperationException>(() => runtime.MaterializeLiveEntity(
            spawn.Guid,
            spawn.Position!.Value.LandblockId,
            id => Entity(id, spawn.Guid)));

        Assert.False(runtime.TryGetRecord(spawn.Guid, out _));
        RuntimeEntityRecord canonical = CurrentCanonical(runtime, spawn.Guid);
        Assert.NotNull(canonical.LocalEntityId);
        Assert.Equal(0, runtime.MaterializedCount);
        Assert.Empty(runtime.VisibleRecords);
        Assert.Empty(spatial.Entities);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(1, resources.UnregisterCount);
    }

    [Fact]
    public void RegistrationRollbackFailureRetainsTombstoneUntilRetryConverges()
    {
        const uint guid = 0x7000005Au;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new FailingRegisterAndRollbackOnceResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));

        Assert.Throws<AggregateException>(() => runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid)));

        Assert.Equal(0, runtime.Count);
        Assert.Equal(1, runtime.PendingTeardownCount);
        Assert.Equal(1, runtime.MaterializedCount);
        Assert.False(runtime.TryGetRecord(guid, out _));
        spatial.MarkPersistent(guid);

        Assert.Equal(1, runtime.RetryPendingTeardowns());

        Assert.Equal(0, runtime.PendingTeardownCount);
        Assert.Equal(0, runtime.MaterializedCount);
        Assert.Equal(2, resources.UnregisterCount);
        Assert.Equal(1, spatial.PersistentGuidCount);

        LiveEntityRegistrationResult recovered = runtime.RegisterLiveEntity(
            Spawn(guid, 1, 1, 0x01010001u));
        Assert.True(recovered.LogicalRegistrationCreated);
        runtime.MaterializeLiveEntity(guid, 0x01010001u, id => Entity(id, guid));
        spatial.RemoveLandblock(0x0101FFFFu);

        Assert.Equal(1, spatial.PendingRescueCount);
        Assert.Equal(1, spatial.PersistentGuidCount);
    }

    [Fact]
    public void RegistrationRollbackTombstoneIsIsolatedFromSameGuidReplacement()
    {
        const uint guid = 0x7000005Bu;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new FailingRegisterAndRollbackOnceResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        Assert.Throws<AggregateException>(() => runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid)));

        runtime.RegisterLiveEntity(Spawn(guid, 2, 1, 0x01010001u));
        WorldEntity replacement = runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid))!;

        Assert.Equal(1, runtime.RetryPendingTeardowns());
        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord current));
        Assert.Same(replacement, current.WorldEntity);
        Assert.Same(replacement, Assert.Single(runtime.MaterializedRecords).WorldEntity);
        Assert.Equal(0, runtime.PendingTeardownCount);
    }

    [Fact]
    public void SameGenerationCreateCannotRecoverUntilRegistrationTombstoneConverges()
    {
        const uint guid = 0x7000005Cu;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new FailingRegisterAndRollbackOnceResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        WorldSession.EntitySpawn spawn = Spawn(guid, 1, 1, 0x01010001u);
        runtime.RegisterLiveEntity(spawn);
        Assert.Throws<AggregateException>(() => runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid)));

        LiveEntityRegistrationResult retransmit = runtime.RegisterLiveEntity(spawn);

        Assert.False(retransmit.LogicalRegistrationCreated);
        Assert.Null(retransmit.Projection);
        Assert.Equal(0, runtime.Count);
        Assert.Equal(1, runtime.PendingTeardownCount);

        Assert.True(runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(guid, InstanceSequence: 1),
            isLocalPlayer: false));
        Assert.Equal(0, runtime.PendingTeardownCount);
        Assert.False(runtime.TryGetSnapshot(guid, out _));
    }

    [Fact]
    public void SessionClear_AttemptsEveryTeardownAndClearsIdentityWhenOneResourceFails()
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new FailingUnregisterResources(0x7000000Bu);
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        foreach (uint guid in new[] { 0x7000000Bu, 0x7000000Cu })
        {
            WorldSession.EntitySpawn spawn = Spawn(guid, 1, 1, 0x01010001u);
            runtime.RegisterLiveEntity(spawn);
            runtime.MaterializeLiveEntity(
                guid,
                spawn.Position!.Value.LandblockId,
                id => Entity(id, guid));
        }

        Assert.Throws<AggregateException>(() => runtime.Clear());

        Assert.Equal(2, resources.UnregisterCount);
        Assert.Equal(0, runtime.Count);
        Assert.Equal(1, runtime.PendingTeardownCount);
        Assert.Equal(1, runtime.MaterializedCount);
        Assert.Empty(runtime.VisibleRecords);
        Assert.Empty(spatial.Entities);
    }

    [Fact]
    public void Delete_UnregisterFailureRetainsTombstoneAndSameDeleteRetriesCleanup()
    {
        const uint guid = 0x7000004Au;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new FailingOnceUnregisterResources(guid);
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        WorldSession.EntitySpawn spawn = Spawn(guid, 1, 1, 0x01010001u);
        runtime.RegisterLiveEntity(spawn);
        runtime.MaterializeLiveEntity(guid, 0x01010001u, id => Entity(id, guid));
        var delete = new DeleteObject.Parsed(guid, InstanceSequence: 1);

        Assert.Throws<AggregateException>(
            () => runtime.UnregisterLiveEntity(delete, isLocalPlayer: false));

        Assert.Equal(0, runtime.Count);
        Assert.Equal(1, runtime.PendingTeardownCount);
        Assert.Equal(1, runtime.MaterializedCount);
        Assert.Equal(1, resources.UnregisterCount);

        Assert.True(runtime.UnregisterLiveEntity(delete, isLocalPlayer: false));
        Assert.Equal(0, runtime.PendingTeardownCount);
        Assert.Equal(0, runtime.MaterializedCount);
        Assert.Equal(2, resources.UnregisterCount);
    }

    [Fact]
    public void SessionClear_OneShotUnregisterFailure_SecondClearConverges()
    {
        const uint guid = 0x7000004Bu;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new FailingOnceUnregisterResources(guid);
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        WorldSession.EntitySpawn spawn = Spawn(guid, 1, 1, 0x01010001u);
        runtime.RegisterLiveEntity(spawn);
        runtime.MaterializeLiveEntity(guid, 0x01010001u, id => Entity(id, guid));

        Assert.Throws<AggregateException>(() => runtime.Clear());
        Assert.Equal(1, runtime.PendingTeardownCount);
        Assert.Equal(1, runtime.MaterializedCount);

        runtime.Clear();
        Assert.Equal(0, runtime.PendingTeardownCount);
        Assert.Equal(0, runtime.MaterializedCount);
        Assert.Empty(spatial.Entities);
        Assert.Equal(2, resources.UnregisterCount);
    }

    [Fact]
    public void ReentrantDeleteDuringPresentationSuspend_IsQueuedAndNextUpdateCompletesIt()
    {
        const uint guid = 0x7000004Cu;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var textures = new CallbackTextureLifetime();
        var adapter = new EntitySpawnAdapter(
            textures,
            _ => new AnimationSequencer(new Setup(), new MotionTable(), new NullAnimationLoader()));
        var resources = new DelegateLiveEntityResourceLifecycle(
            entity => adapter.OnCreate(entity),
            entity =>
            {
                if (!adapter.OnRemove(entity))
                    throw new InvalidOperationException("presentation owner was not registered");
            });
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.ProjectionVisibilityChanged += (record, visible) =>
        {
            if (record.WorldEntity is { } entity)
                adapter.SetPresentationResident(entity, visible);
        };
        WorldSession.EntitySpawn spawn = Spawn(guid, 1, 1, 0x01010001u);
        runtime.RegisterLiveEntity(spawn);
        WorldEntity entity = runtime.MaterializeLiveEntity(
            guid, 0x01010001u, id => Entity(id, guid))!;
        var delete = new DeleteObject.Parsed(guid, InstanceSequence: 1);
        textures.OnRelease = () =>
        {
            textures.OnRelease = null;
            runtime.UnregisterLiveEntity(delete, isLocalPlayer: false);
        };

        Assert.Throws<AggregateException>(
            () => adapter.SetPresentationResident(entity, resident: false));
        Assert.Equal(1, runtime.PendingTeardownCount);
        Assert.NotNull(adapter.GetState(guid));

        Assert.Equal(1, runtime.RetryPendingTeardowns());
        Assert.Equal(0, runtime.PendingTeardownCount);
        Assert.Null(adapter.GetState(guid));
        Assert.Equal(0, runtime.MaterializedCount);
    }

    [Fact]
    public void GenerationReplacement_InstallsNewRecordBeforeReportingOldCleanupFailure()
    {
        const uint guid = 0x7000000Eu;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new FailingUnregisterResources(guid);
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        WorldSession.EntitySpawn first = Spawn(guid, 1, 1, 0x01010001u);
        runtime.RegisterLiveEntity(first);
        runtime.MaterializeLiveEntity(
            guid,
            first.Position!.Value.LandblockId,
            id => Entity(id, guid));

        LiveEntityRegistrationResult replacement = runtime.RegisterLiveEntity(
            Spawn(guid, 2, 1, 0x01010001u));

        Assert.NotNull(replacement.PriorGenerationCleanupFailure);
        RuntimeEntityRecord canonical = CurrentCanonical(runtime, guid);
        Assert.Equal((ushort)2, canonical.Generation);
        Assert.NotNull(canonical.LocalEntityId);
        Assert.False(runtime.TryGetRecord(guid, out _));
        WorldEntity installed = runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid))!;
        Assert.Equal(guid, installed.ServerGuid);
        Assert.Single(spatial.Entities);
    }

    [Fact]
    public void CanonicalOnlyRebucket_DoesNotOverwriteAuthoritativeFullCell()
    {
        const uint guid = 0x7000000Fu;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        spatial.AddLandblock(EmptyLandblock(0x0102FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010022u));
        WorldEntity entity = runtime.MaterializeLiveEntity(
            guid,
            0x01010022u,
            id => Entity(id, guid))!;

        runtime.RebucketLiveEntity(guid, 0x0102FFFFu);

        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord record));
        Assert.Equal(0x01010022u, record.FullCellId);
        Assert.Equal(0x0102FFFFu, record.CanonicalLandblockId);
        Assert.Equal(0x01010022u, entity.ParentCellId);
        Assert.Null(entity.EffectCellId);
        Assert.Equal(0x01010022u, entity.VisibilityCellId);

        runtime.RebucketLiveEntity(guid, 0x01020033u);
        Assert.Equal(0x01020033u, record.FullCellId);
        Assert.Equal(0x01020033u, entity.ParentCellId);
        Assert.Null(entity.EffectCellId);
        Assert.Equal(0x01020033u, entity.VisibilityCellId);
    }

    [Fact]
    public void AttachedProjection_CrossesLandblocksWithoutChangingIdentityOrResources()
    {
        const uint guid = 0x70000010u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        spatial.AddLandblock(EmptyLandblock(0x0102FFFFu));
        var resources = new RecordingResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        WorldEntity attached = runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid),
            LiveEntityProjectionKind.Attached)!;

        runtime.RebucketLiveEntity(guid, 0x01020001u);

        Assert.Same(attached, Assert.Single(spatial.Entities));
        Assert.Empty(runtime.VisibleRecords);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(0, resources.UnregisterCount);
        var destination = Assert.Single(
            spatial.LandblockEntries,
            entry => entry.LandblockId == 0x0102FFFFu);
        Assert.Same(attached, Assert.Single(destination.Entities));
    }

    [Fact]
    public void LocalIdAllocator_WrapsInsideLiveNamespaceWithoutReturningZero()
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(
            spatial,
            new RecordingResources(),
            firstLocalEntityId: LiveEntityRuntime.LastLiveEntityId);
        WorldSession.EntitySpawn first = Spawn(0x70000011u, 1, 1, 0x01010001u);
        WorldSession.EntitySpawn second = Spawn(0x70000012u, 1, 1, 0x01010001u);
        runtime.RegisterLiveEntity(first);
        runtime.RegisterLiveEntity(second);

        WorldEntity firstEntity = runtime.MaterializeLiveEntity(
            first.Guid, 0x01010001u, id => Entity(id, first.Guid))!;
        WorldEntity secondEntity = runtime.MaterializeLiveEntity(
            second.Guid, 0x01010001u, id => Entity(id, second.Guid))!;

        Assert.Equal(LiveEntityRuntime.LastLiveEntityId, firstEntity.Id);
        Assert.Equal(LiveEntityRuntime.FirstLiveEntityId, secondEntity.Id);
        Assert.NotEqual(0u, secondEntity.Id);
        Assert.True(runtime.TryGetLocalEntityId(second.Guid, out uint resolved));
        Assert.Equal(secondEntity.Id, resolved);
        Assert.Throws<ArgumentOutOfRangeException>(() => LiveEntityRuntimeFixture.Create(
            new GpuWorldState(),
            new RecordingResources(),
            firstLocalEntityId: uint.MaxValue));
    }

    [Fact]
    public void PersistentRescue_DeleteBeforeDrain_ForgetsReferenceAndPersistenceClass()
    {
        const uint guid = 0x7000002Au;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid));
        spatial.MarkPersistent(guid);

        spatial.RemoveLandblock(0x0101FFFFu);

        Assert.Equal(1, spatial.PendingRescueCount);
        Assert.Equal(1, spatial.PersistentGuidCount);
        Assert.True(runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(guid, InstanceSequence: 1),
            isLocalPlayer: false));

        Assert.Equal(0, spatial.PendingRescueCount);
        Assert.Equal(0, spatial.PersistentGuidCount);
        Assert.Empty(spatial.DrainRescued());
        Assert.Equal(0, runtime.Count);
    }

    [Fact]
    public void SessionClear_ForgetsPersistentClassificationWithoutMaterializedRecord()
    {
        var spatial = new GpuWorldState();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        spatial.MarkPersistent(0x7000002Bu);

        runtime.Clear();

        Assert.Equal(0, spatial.PersistentGuidCount);
        Assert.Equal(0, spatial.PendingRescueCount);
    }

    [Fact]
    public void PersistentClassification_SurvivesSameGuidGenerationReplacementUntilSessionReset()
    {
        const uint guid = 0x7000002Du;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid));
        spatial.MarkPersistent(guid);

        runtime.RegisterLiveEntity(Spawn(guid, 2, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid));

        Assert.Equal(1, spatial.PersistentGuidCount);
        spatial.RemoveLandblock(0x0101FFFFu);
        Assert.Equal(1, spatial.PendingRescueCount);

        runtime.Clear();

        Assert.Equal(0, spatial.PersistentGuidCount);
        Assert.Equal(0, spatial.PendingRescueCount);
    }

    [Fact]
    public void RebucketObserverFailure_CommitsCanonicalCellAndProjectionBeforeRethrow()
    {
        const uint guid = 0x70000040u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        WorldEntity entity = runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid))!;
        spatial.LiveProjectionVisibilityChanged += (_, _) =>
            throw new InvalidOperationException("observer failed after spatial commit");

        Assert.Throws<AggregateException>(() =>
            runtime.RebucketLiveEntity(guid, 0x02020022u));

        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord record));
        Assert.Equal(0x02020022u, record.FullCellId);
        Assert.Equal(0x0202FFFFu, record.CanonicalLandblockId);
        Assert.True(record.IsSpatiallyProjected);
        Assert.False(record.IsSpatiallyVisible);
        Assert.Same(entity, Assert.Single(runtime.MaterializedRecords).WorldEntity);
        Assert.Empty(runtime.VisibleRecords);
        Assert.Equal(1, spatial.PendingLiveEntityCount);
        Assert.Equal(0, spatial.PendingVisibilityTransitionCount);
    }

    [Fact]
    public void WithdrawObserverFailure_CommitsCanonicalWithdrawalBeforeRethrow()
    {
        const uint guid = 0x70000041u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid));
        runtime.ProjectionVisibilityChanged += (_, visible) =>
        {
            if (!visible)
                throw new InvalidOperationException("observer failed after withdrawal");
        };

        Assert.Throws<AggregateException>(() =>
            runtime.WithdrawLiveEntityProjection(guid));

        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord record));
        Assert.False(record.IsSpatiallyProjected);
        Assert.False(record.IsSpatiallyVisible);
        Assert.Single(runtime.MaterializedRecords);
        Assert.Empty(runtime.VisibleRecords);
        Assert.Empty(spatial.Entities);
        Assert.Equal(0, spatial.PendingLiveEntityCount);
        Assert.Equal(0, spatial.PendingVisibilityTransitionCount);
    }

    [Fact]
    public void WithdrawVisibilityCallback_GuidReplacementPreservesNewIncarnationProjection()
    {
        const uint guid = 0x70000044u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(guid, 0x01010001u, id => Entity(id, guid));
        bool replaced = false;
        runtime.ProjectionVisibilityChanged += (edgeRecord, visible) =>
        {
            if (replaced || visible || edgeRecord.Generation != 1)
                return;
            replaced = true;
            Assert.True(runtime.UnregisterLiveEntity(
                new DeleteObject.Parsed(guid, InstanceSequence: 1),
                isLocalPlayer: false));
            runtime.RegisterLiveEntity(Spawn(guid, 2, 1, 0x01010011u));
            runtime.MaterializeLiveEntity(guid, 0x01010011u, id => Entity(id, guid));
        };

        Assert.False(runtime.WithdrawLiveEntityProjection(guid));

        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord replacement));
        Assert.Equal((ushort)2, replacement.Generation);
        Assert.Equal(0x01010011u, replacement.FullCellId);
        Assert.True(replacement.IsSpatiallyProjected);
        Assert.True(replacement.IsSpatiallyVisible);
        Assert.Same(replacement.WorldEntity, Assert.Single(runtime.MaterializedRecords).WorldEntity);
        Assert.Same(replacement.WorldEntity, Assert.Single(runtime.VisibleRecords).WorldEntity);
        Assert.Same(replacement.WorldEntity, Assert.Single(spatial.Entities));
    }

    [Fact]
    public void RebucketSpatialCallback_GuidReplacementPreservesNewIncarnationProjection()
    {
        const uint guid = 0x70000045u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        WorldEntity original = runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid))!;
        bool replaced = false;
        spatial.LiveProjectionVisibilityChanged += (key, visible) =>
        {
            if (replaced || visible || key.LocalEntityId != original.Id)
                return;
            replaced = true;
            Assert.True(runtime.UnregisterLiveEntity(
                new DeleteObject.Parsed(guid, InstanceSequence: 1),
                isLocalPlayer: false));
            runtime.RegisterLiveEntity(Spawn(guid, 2, 1, 0x01010012u));
            runtime.MaterializeLiveEntity(guid, 0x01010012u, id => Entity(id, guid));
        };

        Assert.False(runtime.RebucketLiveEntity(guid, 0x02020022u));

        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord replacement));
        Assert.Equal((ushort)2, replacement.Generation);
        Assert.Equal(0x01010012u, replacement.FullCellId);
        Assert.Equal(0x0101FFFFu, replacement.CanonicalLandblockId);
        Assert.True(replacement.IsSpatiallyProjected);
        Assert.True(replacement.IsSpatiallyVisible);
        Assert.Same(replacement.WorldEntity, Assert.Single(runtime.MaterializedRecords).WorldEntity);
        Assert.Same(replacement.WorldEntity, Assert.Single(runtime.VisibleRecords).WorldEntity);
        Assert.Same(replacement.WorldEntity, Assert.Single(spatial.Entities));
        Assert.Equal(0, spatial.PendingLiveEntityCount);
    }

    [Fact]
    public void WithdrawVisibilityCallback_RebucketSameRecordSupersedesOuterWithdrawal()
    {
        const uint guid = 0x70000046u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(guid, 0x01010001u, id => Entity(id, guid));
        bool reprojected = false;
        runtime.ProjectionVisibilityChanged += (_, visible) =>
        {
            if (reprojected || visible)
                return;
            reprojected = true;
            Assert.True(runtime.RebucketLiveEntity(guid, 0x01010022u));
        };

        Assert.False(runtime.WithdrawLiveEntityProjection(guid));

        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord record));
        Assert.Equal(0x01010022u, record.FullCellId);
        Assert.True(record.IsSpatiallyProjected);
        Assert.True(record.IsSpatiallyVisible);
        Assert.Same(record.WorldEntity, Assert.Single(runtime.MaterializedRecords).WorldEntity);
        Assert.Same(record.WorldEntity, Assert.Single(runtime.VisibleRecords).WorldEntity);
        Assert.Same(record.WorldEntity, Assert.Single(spatial.Entities));
    }

    [Fact]
    public void DeferredWithdrawFalseEdge_RebucketSameRecordSupersedesOuterWithdrawal()
    {
        const uint guid = 0x70000075u;
        var spatial = new GpuWorldState();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        WorldEntity pending = runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid))!;
        Assert.False(runtime.TryGetRecord(guid, out LiveEntityRecord before)
            && before.IsSpatiallyVisible);
        bool? nestedWithdrawResult = null;
        bool nestedWithdrawStarted = false;
        bool reprojected = false;
        runtime.ProjectionVisibilityChanged += (_, visible) =>
        {
            if (visible && !nestedWithdrawStarted)
            {
                nestedWithdrawStarted = true;
                nestedWithdrawResult = runtime.WithdrawLiveEntityProjection(guid);
                return;
            }
            if (!visible && !reprojected)
            {
                reprojected = true;
                Assert.True(runtime.RebucketLiveEntity(guid, 0x01010022u));
            }
        };

        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));

        Assert.False(nestedWithdrawResult);
        Assert.True(reprojected);
        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord record));
        Assert.Equal(0x01010022u, record.FullCellId);
        Assert.True(record.IsSpatiallyProjected);
        Assert.True(record.IsSpatiallyVisible);
        Assert.Same(pending, record.WorldEntity);
        Assert.Same(pending, Assert.Single(runtime.MaterializedRecords).WorldEntity);
        Assert.Same(pending, Assert.Single(runtime.VisibleRecords).WorldEntity);
        Assert.Same(pending, Assert.Single(spatial.Entities));
    }

    [Fact]
    public void MaterializeVisibilityCallback_GuidReplacementNeverReturnsOldTombstone()
    {
        const uint guid = 0x70000076u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new FailingOnceUnregisterResources(guid);
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        WorldEntity? replacementEntity = null;
        bool replaced = false;
        runtime.ProjectionVisibilityChanged += (edgeRecord, visible) =>
        {
            if (replaced || !visible || edgeRecord.Generation != 1)
                return;
            replaced = true;
            LiveEntityRegistrationResult replacement = runtime.RegisterLiveEntity(
                Spawn(guid, 2, 1, 0x01010002u));
            Assert.NotNull(replacement.PriorGenerationCleanupFailure);
            replacementEntity = runtime.MaterializeLiveEntity(
                guid,
                0x01010002u,
                id => Entity(id, guid));
        };

        WorldEntity? displaced = runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid));

        Assert.Null(displaced);
        Assert.NotNull(replacementEntity);
        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord current));
        Assert.Equal((ushort)2, current.Generation);
        Assert.Same(replacementEntity, current.WorldEntity);
        Assert.Same(replacementEntity, Assert.Single(runtime.VisibleRecords).WorldEntity);
        Assert.Equal(1, runtime.PendingTeardownCount);
        Assert.Equal(1, runtime.RetryPendingTeardowns());
        Assert.Equal(0, runtime.PendingTeardownCount);
    }

    [Fact]
    public void RebucketSpatialCallback_NewerSameRecordRebucketSupersedesOuterMove()
    {
        const uint guid = 0x70000047u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        spatial.AddLandblock(EmptyLandblock(0x0303FFFFu));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        WorldEntity original = runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid))!;
        bool rebucketed = false;
        spatial.LiveProjectionVisibilityChanged += (key, visible) =>
        {
            if (rebucketed || visible || key.LocalEntityId != original.Id)
                return;
            rebucketed = true;
            Assert.True(runtime.RebucketLiveEntity(guid, 0x03030033u));
        };

        Assert.False(runtime.RebucketLiveEntity(guid, 0x02020022u));

        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord record));
        Assert.Equal(0x03030033u, record.FullCellId);
        Assert.Equal(0x0303FFFFu, record.CanonicalLandblockId);
        Assert.True(record.IsSpatiallyProjected);
        Assert.True(record.IsSpatiallyVisible);
        Assert.Same(record.WorldEntity, Assert.Single(runtime.MaterializedRecords).WorldEntity);
        Assert.Same(record.WorldEntity, Assert.Single(runtime.VisibleRecords).WorldEntity);
        Assert.Same(record.WorldEntity, Assert.Single(spatial.Entities));
        Assert.Equal(0, spatial.PendingLiveEntityCount);
    }

    [Fact]
    public void ReentrantVisibilityGuidReuse_DoesNotApplyQueuedOldEdgeToReplacement()
    {
        const uint guid = 0x7000002Cu;
        var spatial = new GpuWorldState();
        var resources = new RecordingResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid));
        var edges = new List<(ushort Generation, bool Visible)>();
        bool replaced = false;
        runtime.ProjectionVisibilityChanged += (record, visible) =>
        {
            edges.Add((record.Generation, visible));
            if (replaced || record.Generation != 1 || !visible)
                return;
            replaced = true;
            Assert.True(runtime.UnregisterLiveEntity(
                new DeleteObject.Parsed(guid, InstanceSequence: 1),
                isLocalPlayer: false));
            runtime.RegisterLiveEntity(Spawn(guid, 2, 1, 0x01010001u));
            runtime.MaterializeLiveEntity(
                guid,
                0x01010001u,
                id => Entity(id, guid));
        };

        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));

        Assert.Equal([(1, true), (2, true)], edges);
        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord replacement));
        Assert.Equal((ushort)2, replacement.Generation);
        Assert.True(replacement.IsSpatiallyVisible);
        Assert.True(spatial.IsLiveEntityVisible(
            replacement.ProjectionKey!.Value));
        Assert.Equal(2, resources.RegisterCount);
        Assert.Equal(1, resources.UnregisterCount);
    }

    [Fact]
    public void SessionClearInsideVisibilityCallback_DropsOldQueuedEdgesBeforeReuse()
    {
        const uint guid = 0x7000002Eu;
        var spatial = new GpuWorldState();
        var resources = new RecordingResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid));
        var edges = new List<(ushort Generation, bool Visible)>();
        bool reset = false;
        runtime.ProjectionVisibilityChanged += (record, visible) =>
        {
            edges.Add((record.Generation, visible));
            if (reset || record.Generation != 1 || !visible)
                return;
            reset = true;
            runtime.Clear();
            runtime.RegisterLiveEntity(Spawn(guid, 2, 1, 0x01010001u));
            runtime.MaterializeLiveEntity(
                guid,
                0x01010001u,
                id => Entity(id, guid));
        };

        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));

        Assert.Equal([(1, true), (2, true)], edges);
        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord replacement));
        Assert.Equal((ushort)2, replacement.Generation);
        Assert.True(replacement.IsSpatiallyVisible);
        Assert.Equal(0, spatial.PendingVisibilityTransitionCount);
        Assert.Equal(2, resources.RegisterCount);
        Assert.Equal(1, resources.UnregisterCount);
    }

    [Fact]
    public void DeleteCallback_CanRegisterNextGenerationButCannotMaterializeBeforeOldTeardown()
    {
        const uint guid = 0x70000031u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new RecordingResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        WorldEntity old = runtime.MaterializeLiveEntity(
            guid,
            0x01010001u,
            id => Entity(id, guid))!;
        AggregateException error = Assert.Throws<AggregateException>(() =>
            runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(guid, InstanceSequence: 1),
            isLocalPlayer: false,
            beforeTeardown: () =>
            {
                runtime.RegisterLiveEntity(Spawn(guid, 2, 1, 0x01010001u));
                runtime.MaterializeLiveEntity(
                    guid,
                    0x01010001u,
                    id => Entity(id, guid));
            }));

        Assert.Contains(
            error.Flatten().InnerExceptions,
            exception => exception is InvalidOperationException
                && exception.Message.Contains("active logical-lifetime transition", StringComparison.Ordinal));
        RuntimeEntityRecord current = CurrentCanonical(runtime, guid);
        Assert.Equal((ushort)2, current.Generation);
        Assert.NotNull(current.LocalEntityId);
        Assert.False(runtime.TryGetRecord(guid, out _));
        Assert.Empty(spatial.Entities);
        Assert.DoesNotContain(
            runtime.VisibleRecords,
            record => record.ServerGuid == guid);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(1, resources.UnregisterCount);
        Assert.False(runtime.TryGetServerGuid(old.Id, out _));
    }

    [Fact]
    public void GenerationTeardownCallback_NewerReentrantGenerationSupersedesOuterCreate()
    {
        const uint guid = 0x70000032u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new CallbackResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(guid, 0x01010001u, id => Entity(id, guid));
        resources.OnUnregister = _ =>
        {
            resources.OnUnregister = null;
            runtime.RegisterLiveEntity(Spawn(guid, 3, 1, 0x01010001u));
        };

        LiveEntityRegistrationResult outer = runtime.RegisterLiveEntity(
            Spawn(guid, 2, 1, 0x01010001u));

        Assert.Equal(CreateObjectTimestampDisposition.StaleGeneration, outer.Inbound.Disposition);
        Assert.False(outer.LogicalRegistrationCreated);
        RuntimeEntityRecord current = CurrentCanonical(runtime, guid);
        Assert.Equal((ushort)3, current.Generation);
        Assert.NotNull(current.LocalEntityId);
        Assert.False(runtime.TryGetRecord(guid, out _));
        Assert.Empty(spatial.Entities);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(1, resources.UnregisterCount);
    }

    [Fact]
    public void GenerationTeardownCallback_UnrelatedRegistrationDoesNotSupersedeOuterCreate()
    {
        const uint guid = 0x7000003Bu;
        const uint unrelatedGuid = 0x7000003Cu;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new CallbackResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(guid, 0x01010001u, id => Entity(id, guid));
        resources.OnUnregister = _ =>
        {
            resources.OnUnregister = null;
            runtime.RegisterLiveEntity(Spawn(unrelatedGuid, 1, 1, 0x01010001u));
        };

        LiveEntityRegistrationResult outer = runtime.RegisterLiveEntity(
            Spawn(guid, 2, 1, 0x01010001u));

        Assert.Equal(CreateObjectTimestampDisposition.NewGeneration, outer.Inbound.Disposition);
        Assert.True(outer.LogicalRegistrationCreated);
        RuntimeEntityRecord replacement = CurrentCanonical(runtime, guid);
        Assert.Equal((ushort)2, replacement.Generation);
        Assert.NotNull(replacement.LocalEntityId);
        Assert.NotNull(CurrentCanonical(runtime, unrelatedGuid).LocalEntityId);
        Assert.False(runtime.TryGetRecord(guid, out _));
        Assert.False(runtime.TryGetRecord(unrelatedGuid, out _));
        Assert.Equal(2, runtime.Count);
    }

    [Fact]
    public void GenerationTeardownCallback_AcceptedDeletePreventsOuterGenerationResurrection()
    {
        const uint guid = 0x70000035u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new CallbackResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(guid, 0x01010001u, id => Entity(id, guid));
        resources.OnUnregister = _ =>
        {
            resources.OnUnregister = null;
            Assert.True(runtime.UnregisterLiveEntity(
                new DeleteObject.Parsed(guid, InstanceSequence: 2),
                isLocalPlayer: false));
        };

        LiveEntityRegistrationResult outer = runtime.RegisterLiveEntity(
            Spawn(guid, 2, 1, 0x01010001u));

        Assert.Equal(CreateObjectTimestampDisposition.StaleGeneration, outer.Inbound.Disposition);
        Assert.False(outer.LogicalRegistrationCreated);
        Assert.False(runtime.TryGetRecord(guid, out _));
        Assert.False(runtime.TryGetSnapshot(guid, out _));
        Assert.Empty(spatial.Entities);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(1, resources.UnregisterCount);
    }

    [Fact]
    public void GenerationTeardownCallback_DeleteThenRegisterCannotAbaOuterEpoch()
    {
        const uint guid = 0x70000042u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new CallbackResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(guid, 0x01010001u, id => Entity(id, guid));
        resources.OnUnregister = _ =>
        {
            resources.OnUnregister = null;
            Assert.True(runtime.UnregisterLiveEntity(
                new DeleteObject.Parsed(guid, InstanceSequence: 2),
                isLocalPlayer: false));
            runtime.RegisterLiveEntity(Spawn(guid, 3, 1, 0x01010001u));
        };

        LiveEntityRegistrationResult outer = runtime.RegisterLiveEntity(
            Spawn(guid, 2, 1, 0x01010001u));

        Assert.Equal(CreateObjectTimestampDisposition.StaleGeneration, outer.Inbound.Disposition);
        Assert.False(outer.LogicalRegistrationCreated);
        RuntimeEntityRecord current = CurrentCanonical(runtime, guid);
        Assert.Equal((ushort)3, current.Generation);
        Assert.NotNull(current.LocalEntityId);
        Assert.False(runtime.TryGetRecord(guid, out _));
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(1, resources.UnregisterCount);
    }

    [Fact]
    public void GenerationTeardownCallback_NewerGenerationAndFailureIsNeverSilentlyDiscarded()
    {
        const uint guid = 0x70000043u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new CallbackResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(guid, 0x01010001u, id => Entity(id, guid));
        resources.OnUnregister = _ =>
        {
            resources.OnUnregister = null;
            runtime.RegisterLiveEntity(Spawn(guid, 3, 1, 0x01010001u));
            throw new InvalidOperationException("old incarnation cleanup failed");
        };

        AggregateException error = Assert.Throws<AggregateException>(() =>
            runtime.RegisterLiveEntity(Spawn(guid, 2, 1, 0x01010001u)));

        Assert.Contains(
            error.Flatten().InnerExceptions,
            exception => exception is InvalidOperationException
                && exception.Message == "old incarnation cleanup failed");
        RuntimeEntityRecord current = CurrentCanonical(runtime, guid);
        Assert.Equal((ushort)3, current.Generation);
        Assert.NotNull(current.LocalEntityId);
        Assert.False(runtime.TryGetRecord(guid, out _));
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(1, resources.UnregisterCount);
    }

    [Fact]
    public void GenerationTeardownCallback_SessionClearPreventsOuterGenerationResurrection()
    {
        const uint guid = 0x70000036u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new CallbackResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(guid, 0x01010001u, id => Entity(id, guid));
        resources.OnUnregister = _ =>
        {
            resources.OnUnregister = null;
            runtime.Clear();
        };

        LiveEntityRegistrationResult outer = runtime.RegisterLiveEntity(
            Spawn(guid, 2, 1, 0x01010001u));

        Assert.Equal(CreateObjectTimestampDisposition.StaleGeneration, outer.Inbound.Disposition);
        Assert.False(outer.LogicalRegistrationCreated);
        Assert.False(runtime.TryGetRecord(guid, out _));
        Assert.False(runtime.TryGetSnapshot(guid, out _));
        Assert.Empty(spatial.Entities);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(1, resources.UnregisterCount);
    }

    [Fact]
    public void ResourceRegistrationCallback_CannotReplaceIncarnationAndRollsBackOwner()
    {
        const uint guid = 0x70000037u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new CallbackResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        resources.OnRegister = _ =>
            runtime.RegisterLiveEntity(Spawn(guid, 2, 1, 0x01010001u));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            runtime.MaterializeLiveEntity(guid, 0x01010001u, id => Entity(id, guid)));

        Assert.Contains("atomic resource registration", error.Message, StringComparison.Ordinal);
        RuntimeEntityRecord current = CurrentCanonical(runtime, guid);
        Assert.Equal((ushort)1, current.Generation);
        Assert.NotNull(current.LocalEntityId);
        Assert.False(runtime.TryGetRecord(guid, out _));
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(1, resources.UnregisterCount);
        Assert.Equal(0, runtime.MaterializedCount);
        Assert.Empty(spatial.Entities);
    }

    [Fact]
    public void ResourceRegistrationCallback_CannotClearSessionAndRollsBackOwner()
    {
        const uint guid = 0x70000038u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new CallbackResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.RegisterLiveEntity(Spawn(guid, 1, 1, 0x01010001u));
        resources.OnRegister = _ => runtime.Clear();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            runtime.MaterializeLiveEntity(guid, 0x01010001u, id => Entity(id, guid)));

        Assert.Contains("atomic resource registration", error.Message, StringComparison.Ordinal);
        RuntimeEntityRecord current = CurrentCanonical(runtime, guid);
        Assert.NotNull(current.LocalEntityId);
        Assert.False(runtime.TryGetRecord(guid, out _));
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(1, resources.UnregisterCount);
        Assert.Equal(0, runtime.MaterializedCount);
        Assert.Empty(spatial.Entities);
    }

    [Fact]
    public void SessionClear_ReentrantDeleteOfLaterSnapshot_TearsEachRecordDownOnce()
    {
        const uint firstGuid = 0x70000039u;
        const uint secondGuid = 0x7000003Au;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new CallbackResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.RegisterLiveEntity(Spawn(firstGuid, 1, 1, 0x01010001u));
        runtime.RegisterLiveEntity(Spawn(secondGuid, 1, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(firstGuid, 0x01010001u, id => Entity(id, firstGuid));
        runtime.MaterializeLiveEntity(secondGuid, 0x01010001u, id => Entity(id, secondGuid));
        resources.OnUnregister = entity =>
        {
            if (entity.ServerGuid != firstGuid)
                return;
            resources.OnUnregister = null;
            Assert.True(runtime.UnregisterLiveEntity(
                new DeleteObject.Parsed(secondGuid, InstanceSequence: 1),
                isLocalPlayer: false));
        };

        runtime.Clear();

        Assert.Equal(1, resources.UnregisterCountsByGuid[firstGuid]);
        Assert.Equal(1, resources.UnregisterCountsByGuid[secondGuid]);
        Assert.Equal(2, resources.UnregisterCount);
        Assert.Equal(0, runtime.Count);
        Assert.Equal(0, runtime.MaterializedCount);
        Assert.Empty(spatial.Entities);
    }

    [Fact]
    public void SessionClear_RejectsRegistrationFromTeardownCallbackWithoutLeakingOwner()
    {
        const uint oldGuid = 0x70000033u;
        const uint callbackGuid = 0x70000034u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new CallbackResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        runtime.RegisterLiveEntity(Spawn(oldGuid, 1, 1, 0x01010001u));
        runtime.MaterializeLiveEntity(oldGuid, 0x01010001u, id => Entity(id, oldGuid));
        resources.OnUnregister = _ =>
        {
            resources.OnUnregister = null;
            runtime.RegisterLiveEntity(Spawn(callbackGuid, 1, 1, 0x01010001u));
            runtime.MaterializeLiveEntity(
                callbackGuid,
                0x01010001u,
                id => Entity(id, callbackGuid));
        };

        AggregateException error = Assert.Throws<AggregateException>(() => runtime.Clear());

        Assert.Contains(
            error.Flatten().InnerExceptions,
            exception => exception is InvalidOperationException
                && exception.Message.Contains("while the session lifetime is clearing", StringComparison.Ordinal));
        Assert.Equal(0, runtime.Count);
        Assert.Equal(1, runtime.PendingTeardownCount);
        Assert.Equal(1, runtime.MaterializedCount);
        Assert.Empty(runtime.VisibleRecords);
        Assert.Empty(spatial.Entities);
        Assert.Equal(0, spatial.PendingLiveEntityCount);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(1, resources.UnregisterCount);

        runtime.Clear();
        Assert.Equal(0, runtime.PendingTeardownCount);
        Assert.Equal(0, runtime.MaterializedCount);
        Assert.Empty(runtime.MaterializedRecords);
        Assert.Equal(2, resources.UnregisterCount);
    }

    [Fact]
    public void Delete_CleansLogicalRecordEvenWhenPreTeardownCallbackFails()
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
        var resources = new RecordingResources();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, resources);
        WorldSession.EntitySpawn spawn = Spawn(0x7000000Du, 4, 1, 0x01010001u);
        runtime.RegisterLiveEntity(spawn);
        runtime.MaterializeLiveEntity(
            spawn.Guid,
            spawn.Position!.Value.LandblockId,
            id => Entity(id, spawn.Guid));

        Assert.Throws<AggregateException>(() => runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(spawn.Guid, spawn.InstanceSequence),
            isLocalPlayer: false,
            beforeTeardown: () => throw new InvalidOperationException("fixture callback failure")));

        Assert.Equal(0, runtime.Count);
        Assert.Empty(runtime.VisibleRecords);
        Assert.Empty(spatial.Entities);
        Assert.Equal(1, resources.UnregisterCount);
    }

    private static LoadedLandblock EmptyLandblock(uint canonicalId) =>
        new(canonicalId, new LandBlock(), Array.Empty<WorldEntity>());

    private static LiveEntityRuntimeSlot BoundSlot(LiveEntityRuntime runtime)
    {
        var slot = new LiveEntityRuntimeSlot();
        slot.Bind(runtime);
        return slot;
    }

    private static void ResolveParent(LiveEntityRuntime runtime, uint childGuid) =>
        runtime.ParentAttachments.Resolve(
            childGuid,
            guid => runtime.TryGetSnapshot(guid, out _),
            guid => runtime.TryGetSnapshot(guid, out WorldSession.EntitySpawn spawn)
                ? spawn.InstanceSequence
                : null,
            update => runtime.TryApplyParent(update, out _));

    private static LiveEntityRecord RegisterProjection(
        LiveEntityRuntime runtime,
        WorldSession.EntitySpawn spawn)
    {
        LiveEntityRegistrationResult registration =
            runtime.RegisterLiveEntity(spawn);
        RuntimeEntityRecord canonical = Assert.IsType<RuntimeEntityRecord>(
            registration.Canonical);
        WorldEntity entity = Assert.IsType<WorldEntity>(
            runtime.MaterializeLiveEntity(
                canonical,
                spawn.Position?.LandblockId ?? 0u,
                id => Entity(id, spawn.Guid),
                LiveEntityProjectionKind.World,
                initializeProjection: null,
                out LiveEntityRecord? record));
        Assert.Equal(entity.Id, canonical.LocalEntityId);
        return Assert.IsType<LiveEntityRecord>(record);
    }

    private static RuntimeEntityRecord CurrentCanonical(
        LiveEntityRuntime runtime,
        uint serverGuid)
    {
        Assert.True(runtime.TryGetCanonical(serverGuid, out RuntimeEntityRecord canonical));
        return canonical;
    }

    private static WorldEntity Entity(uint id, uint guid) => new()
    {
        Id = id,
        ServerGuid = guid,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = System.Numerics.Vector3.Zero,
        Rotation = System.Numerics.Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort instance,
        ushort positionSequence,
        uint cell,
        PhysicsStateFlags state = PhysicsStateFlags.ReportCollisions)
    {
        var position = new CreateObject.ServerPosition(
            cell, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(
            positionSequence, 1, 1, 1, 0, 1, 0, 1, instance);
        var physics = new PhysicsSpawnData(
            RawState: (uint)state,
            Position: position,
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
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: (uint)state,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: positionSequence,
            Physics: physics);
    }
}
