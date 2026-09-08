using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Core.Vfx;
using AcDream.Runtime.Entities;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Rendering;

public sealed class LiveEntityAnimationSchedulerTests
{
    private const uint LocalGuid = 0x50000001u;
    private const uint RemoteGuid = 0x70000001u;
    private const uint Cell = 0x01010001u;

    [Fact]
    public void HiddenLocalPose_IsSampledOnlyForMarkedObjectQuantum()
    {
        var (live, record, animation) = BuildHiddenAnimated(LocalGuid);
        LiveEntityAnimationScheduler scheduler = BuildScheduler(
            live,
            localPlayerGuid: LocalGuid);

        Assert.True(scheduler.Tick(
                PhysicsBody.MaxQuantum,
                animation.Entity.Position,
                localHiddenPartPoseDirty: true,
                liveCenterX: 0,
                liveCenterY: 0)
            .TryGetValue(record.ProjectionKey!.Value, out LiveEntityAnimationSchedule marked));
        bool repeated = scheduler.Tick(
                PhysicsBody.MaxQuantum,
                animation.Entity.Position,
                localHiddenPartPoseDirty: false,
                liveCenterX: 0,
                liveCenterY: 0)
            .TryGetValue(record.ProjectionKey!.Value, out LiveEntityAnimationSchedule consumed);

        Assert.True(marked.ComposeParts);
        Assert.NotNull(marked.SequenceFrames);
        Assert.False(repeated);
        Assert.False(consumed.ComposeParts);
    }

    [Fact]
    public void HiddenRemotePose_IsSampledOnceAfterAdmittedHiddenQuantum()
    {
        var (live, record, animation) = BuildHiddenAnimated(RemoteGuid);
        var remote = new AcDream.Runtime.Physics.RemoteMotion
        {
            CellId = Cell,
        };
        remote.Body.Position = animation.Entity.Position;
        remote.Body.Orientation = animation.Entity.Rotation;
        remote.Body.InWorld = true;
        live.SetRemoteMotionRuntime(RemoteGuid, remote);
        LiveEntityAnimationScheduler scheduler = BuildScheduler(
            live,
            localPlayerGuid: LocalGuid);

        Assert.True(scheduler.Tick(
                PhysicsBody.MaxQuantum,
                playerPosition: null,
                localHiddenPartPoseDirty: false,
                liveCenterX: 0,
                liveCenterY: 0)
            .TryGetValue(record.ProjectionKey!.Value, out LiveEntityAnimationSchedule marked));
        bool repeated = scheduler.Tick(
                elapsedSeconds: 0f,
                playerPosition: null,
                localHiddenPartPoseDirty: false,
                liveCenterX: 0,
                liveCenterY: 0)
            .TryGetValue(record.ProjectionKey!.Value, out LiveEntityAnimationSchedule consumed);

        Assert.True(marked.ComposeParts);
        Assert.NotNull(marked.SequenceFrames);
        Assert.False(repeated);
        Assert.False(consumed.ComposeParts);
    }

    [Fact]
    public void VisibleAnimationWithoutRemoteMotion_AppliesCompleteRootFrame()
    {
        var (live, record, animation) = BuildVisibleAnimatedWithPosFrames(RemoteGuid);
        var retainedBodyOwner = BuildRemote(animation.Entity);
        retainedBodyOwner.Body.SnapToCell(
            Cell,
            animation.Entity.Position,
            animation.Entity.Position);
        live.SetRemoteMotionRuntime(RemoteGuid, retainedBodyOwner);
        Assert.True(live.ClearRemoteMotionRuntime(RemoteGuid));
        LiveEntityAnimationScheduler scheduler = BuildScheduler(live, LocalGuid);
        float startX = animation.Entity.Position.X;

        Assert.True(scheduler.Tick(
                0.1f,
                animation.Entity.Position,
                localHiddenPartPoseDirty: false,
                liveCenterX: 0,
                liveCenterY: 0)
            .ContainsKey(record.ProjectionKey!.Value));

        Assert.True(animation.Entity.Position.X > startX + 1f);
    }

    [Fact]
    public void ScheduleOwnsPartFramesAcrossLaterSequencerSampling()
    {
        var (live, record, animation) = BuildVisibleAnimatedWithPosFrames(RemoteGuid);
        LiveEntityAnimationScheduler scheduler = BuildScheduler(live, LocalGuid);
        LiveEntityAnimationSchedule schedule = scheduler.Tick(
            0.1f,
            animation.Entity.Position,
            localHiddenPartPoseDirty: false,
            liveCenterX: 0,
            liveCenterY: 0)[record.ProjectionKey!.Value];
        PartTransform retained = schedule.SequenceFrames![0];
        animation.CaptureSequenceFrames(
            [new PartTransform(new Vector3(77f, 76f, 75f), Quaternion.Identity)]);
        foreach (AnimationFrame frame in animation.Animation.PartFrames)
            frame.Frames[0].Origin = new Vector3(99f, 98f, 97f);

        IReadOnlyList<PartTransform> borrowed = animation.Sequencer!.SampleCurrentPose();

        Assert.Equal(new Vector3(99f, 98f, 97f), borrowed[0].Origin);
        Assert.Equal(retained.Origin, schedule.SequenceFrames[0].Origin);
        Assert.NotEqual(animation.SequenceFramesScratch[0].Origin, schedule.SequenceFrames[0].Origin);
        Assert.NotEqual(borrowed[0].Origin, schedule.SequenceFrames[0].Origin);
    }

    [Fact]
    public void BodylessNonWalkableAnimation_SuppressesRootOriginButPreservesOrientation()
    {
        Quaternion rootTurn = Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ,
            MathF.PI / 2f);
        var (live, _, animation) = BuildVisibleAnimatedWithPosFrames(
            RemoteGuid,
            rootTurn);
        Vector3 startPosition = animation.Entity.Position;
        LiveEntityAnimationScheduler scheduler = BuildScheduler(live, LocalGuid);

        scheduler.Tick(
            PhysicsBody.MinQuantum * 1.01f,
            animation.Entity.Position,
            localHiddenPartPoseDirty: false,
            liveCenterX: 0,
            liveCenterY: 0);

        Assert.Equal(startPosition, animation.Entity.Position);
        Assert.InRange(
            MathF.Abs(Quaternion.Dot(
                rootTurn,
                Quaternion.Normalize(animation.Entity.Rotation))),
            0.99999f,
            1.00001f);
    }

    [Fact]
    public void RemoteWithoutAnimation_RemainsInOrdinaryObjectWorkset()
    {
        var (live, record, entity) = BuildMaterialized(
            RemoteGuid,
            PhysicsStateFlags.Gravity);
        var remote = new AcDream.Runtime.Physics.RemoteMotion
        {
            CellId = Cell,
            Airborne = true,
        };
        remote.Body.Position = entity.Position;
        remote.Body.Orientation = entity.Rotation;
        remote.Body.Velocity = new Vector3(3f, 0f, 0f);
        remote.Body.State = PhysicsStateFlags.Gravity;
        remote.Body.TransientState = TransientStateFlags.Active;
        live.SetRemoteMotionRuntime(RemoteGuid, remote);
        LiveEntityAnimationScheduler scheduler = BuildScheduler(live, LocalGuid);
        float startX = entity.Position.X;

        scheduler.Tick(
            0.1f,
            entity.Position,
            localHiddenPartPoseDirty: false,
            liveCenterX: 0,
            liveCenterY: 0);

        Assert.True(entity.Position.X > startX);
        Assert.True(live.IsCurrentSpatialRootObject(record));
    }

    [Fact]
    public void AnimationlessRemote_NearPositionCorrectionIsQueuedAndConsumed()
    {
        var (live, record, entity) = BuildMaterialized(
            RemoteGuid,
            PhysicsStateFlags.ReportCollisions);
        var remote = BuildRemote(entity);
        remote.Interp.Enqueue(
            entity.Position + Vector3.UnitX,
            heading: 0f,
            isMovingTo: false,
            currentBodyPosition: remote.Body.Position);
        live.SetRemoteMotionRuntime(RemoteGuid, remote);
        Assert.Null(record.AnimationRuntime);
        Assert.True(live.IsCurrentSpatialRemoteMotion(record, remote));
        LiveEntityAnimationScheduler scheduler = BuildScheduler(live, LocalGuid);
        float startX = entity.Position.X;

        for (int i = 0; i < 6; i++)
        {
            scheduler.Tick(
                0.1f,
                entity.Position,
                localHiddenPartPoseDirty: false,
                liveCenterX: 1,
                liveCenterY: 1);
        }

        Assert.True(entity.Position.X > startX + 0.5f);
        Assert.True(remote.Interp.IsActive || entity.Position.X >= startX + 0.99f);
        Assert.True(live.IsCurrentSpatialRootObject(record));
    }

    [Fact]
    public void RetainedProjectileWithoutRemote_WhenMissileClears_AdvancesOnceAsOrdinaryBody()
    {
        var (live, record, animation) = BuildVisibleAnimatedWithPosFrames(
            RemoteGuid,
            rootOrigin: Vector3.Zero);
        PhysicsEngine physics = live.Physics.Engine;
        var projectiles = new ProjectileController(live);
        record.FinalPhysicsState = ProjectileState;
        Assert.True(record.ObjectClock.IsActive);
        Assert.True(projectiles.TryBind(
            record,
            ProjectileSetup(),
            currentTime: 1.0,
            liveCenterX: 1,
            liveCenterY: 1));
        Assert.True(record.ObjectClock.IsActive);
        PhysicsBody body = record.PhysicsBody!;
        body.set_velocity(new Vector3(3f, 0f, 0f));
        body.TransientState = TransientStateFlags.Active;

        record.FinalPhysicsState = PhysicsStateFlags.ReportCollisions
            | PhysicsStateFlags.Gravity;
        Assert.True(projectiles.ApplyAuthoritativeState(
            record,
            record.FinalPhysicsState,
            currentTime: 1.1,
            liveCenterX: 1,
            liveCenterY: 1));
        Assert.True(projectiles.HandlesMovement(RemoteGuid));
        float startX = animation.Entity.Position.X;
        int rootPublishes = 0;
        LiveEntityAnimationScheduler scheduler = BuildScheduler(
            live,
            LocalGuid,
            publishRootPose: _ => rootPublishes++,
            physics: physics,
            projectiles: projectiles);

        scheduler.Tick(
            0.1f,
            animation.Entity.Position,
            localHiddenPartPoseDirty: false,
            liveCenterX: 1,
            liveCenterY: 1);

        float distance = animation.Entity.Position.X - startX;
        Assert.InRange(distance, 0.2f, 0.4f);
        Assert.Equal(1, rootPublishes);
        Assert.Same(body, record.PhysicsBody);
        Assert.Same(body, record.ProjectileRuntime!.Body);
    }

    [Fact]
    public void RetainedProjectileWithRemote_WhenMissileClears_TransfersMovementToRemoteOnce()
    {
        var (live, record, animation) = BuildVisibleAnimatedWithPosFrames(
            RemoteGuid,
            rootOrigin: Vector3.Zero);
        PhysicsEngine physics = live.Physics.Engine;
        var projectiles = new ProjectileController(live);
        record.FinalPhysicsState = ProjectileState;
        var remote = new AcDream.Runtime.Physics.RemoteMotion
        {
            CellId = Cell,
            Airborne = false,
        };
        remote.Body.Orientation = animation.Entity.Rotation;
        remote.Body.SnapToCell(
            Cell,
            animation.Entity.Position,
            animation.Entity.Position);
        remote.Body.State = ProjectileState;
        remote.Body.InWorld = true;
        remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact
            | TransientStateFlags.OnWalkable;
        live.SetRemoteMotionRuntime(RemoteGuid, remote);
        Assert.True(record.ObjectClock.IsActive);
        Assert.Equal(Cell, record.FullCellId);
        Assert.True(record.IsSpatiallyVisible);
        Assert.True(projectiles.TryBind(
            record,
            ProjectileSetup(),
            currentTime: 1.0,
            liveCenterX: 1,
            liveCenterY: 1));
        Assert.Equal(Cell, record.FullCellId);
        Assert.True(record.IsSpatiallyVisible);
        Assert.True(record.ObjectClock.IsActive);
        Assert.Same(remote.Body, record.ProjectileRuntime!.Body);

        record.FinalPhysicsState = PhysicsStateFlags.ReportCollisions
            | PhysicsStateFlags.Gravity;
        Assert.True(projectiles.ApplyAuthoritativeState(
            record,
            record.FinalPhysicsState,
            currentTime: 1.1,
            liveCenterX: 1,
            liveCenterY: 1));
        Assert.False(projectiles.HandlesMovement(RemoteGuid));
        remote.Interp.Enqueue(
            animation.Entity.Position + Vector3.UnitX,
            heading: 0f,
            isMovingTo: false,
            currentBodyPosition: remote.Body.Position);
        Assert.True(record.ObjectClock.IsActive);
        Assert.True(remote.Body.IsActive);
        Assert.True(live.IsCurrentSpatialRemoteMotion(record, remote));
        Assert.True(live.IsCurrentSpatialAnimation(record, animation));
        float startX = animation.Entity.Position.X;
        int rootPublishes = 0;
        LiveEntityAnimationScheduler scheduler = BuildScheduler(
            live,
            LocalGuid,
            publishRootPose: _ => rootPublishes++,
            physics: physics,
            projectiles: projectiles);

        IReadOnlyDictionary<RuntimeEntityKey, LiveEntityAnimationSchedule> schedules = scheduler.Tick(
            0.1f,
            animation.Entity.Position,
            localHiddenPartPoseDirty: false,
            liveCenterX: 1,
            liveCenterY: 1);

        Assert.Contains(record.ProjectionKey!.Value, schedules.Keys);
        Assert.Equal(1, rootPublishes);
        float distance = animation.Entity.Position.X - startX;
        Assert.InRange(distance, 0.19f, 0.21f);
        Assert.Same(remote.Body, record.PhysicsBody);
        Assert.Same(remote.Body, record.ProjectileRuntime!.Body);
    }

    [Fact]
    public void DeleteFromFirstQuantumCallback_StopsCatchUpBatchAndPosePublish()
    {
        var (live, record, animation) = BuildVisibleAnimatedWithPosFrames(RemoteGuid);
        int captures = 0;
        LiveEntityAnimationScheduler scheduler = BuildScheduler(
            live,
            LocalGuid,
            (_, _) =>
            {
                captures++;
                live.UnregisterLiveEntity(
                    new DeleteObject.Parsed(RemoteGuid, record.Generation),
                    isLocalPlayer: false);
            });

        IReadOnlyDictionary<RuntimeEntityKey, LiveEntityAnimationSchedule> schedules = scheduler.Tick(
            PhysicsBody.MaxQuantum * 2.5f,
            animation.Entity.Position,
            localHiddenPartPoseDirty: false,
            liveCenterX: 0,
            liveCenterY: 0);

        Assert.Equal(1, captures);
        Assert.Empty(schedules);
        Assert.False(live.TryGetRecord(RemoteGuid, out _));
    }

    [Fact]
    public void VisibleRemoteDeleteInsideHook_StopsBeforeStaleEntityPublication()
    {
        var (live, record, animation) = BuildVisibleAnimatedWithPosFrames(RemoteGuid);
        var remote = BuildRemote(animation.Entity);
        live.SetRemoteMotionRuntime(RemoteGuid, remote);
        Vector3 retainedEntityPosition = animation.Entity.Position;
        int captures = 0;
        int rootPublishes = 0;
        LiveEntityAnimationScheduler scheduler = BuildScheduler(
            live,
            LocalGuid,
            (_, _) =>
            {
                captures++;
                live.UnregisterLiveEntity(
                    new DeleteObject.Parsed(RemoteGuid, record.Generation),
                    isLocalPlayer: false);
            },
            _ => rootPublishes++);

        IReadOnlyDictionary<RuntimeEntityKey, LiveEntityAnimationSchedule> schedules = scheduler.Tick(
            PhysicsBody.MaxQuantum * 2.5f,
            animation.Entity.Position,
            localHiddenPartPoseDirty: false,
            liveCenterX: 0,
            liveCenterY: 0);

        Assert.Equal(1, captures);
        Assert.Equal(0, rootPublishes);
        Assert.Empty(schedules);
        Assert.Equal(retainedEntityPosition, animation.Entity.Position);
    }

    [Fact]
    public void HiddenRemoteDeleteInsideHook_StopsBeforeStaleEntityPublication()
    {
        var (live, record, animation) = BuildHiddenAnimated(RemoteGuid);
        var remote = BuildRemote(animation.Entity);
        live.SetRemoteMotionRuntime(RemoteGuid, remote);
        Vector3 retainedEntityPosition = animation.Entity.Position;
        int captures = 0;
        int rootPublishes = 0;
        LiveEntityAnimationScheduler scheduler = BuildScheduler(
            live,
            LocalGuid,
            (_, _) =>
            {
                captures++;
                live.UnregisterLiveEntity(
                    new DeleteObject.Parsed(RemoteGuid, record.Generation),
                    isLocalPlayer: false);
            },
            _ => rootPublishes++);

        IReadOnlyDictionary<RuntimeEntityKey, LiveEntityAnimationSchedule> schedules = scheduler.Tick(
            PhysicsBody.MaxQuantum * 2.5f,
            animation.Entity.Position,
            localHiddenPartPoseDirty: false,
            liveCenterX: 0,
            liveCenterY: 0);

        Assert.Equal(1, captures);
        Assert.Equal(0, rootPublishes);
        Assert.Empty(schedules);
        Assert.Equal(retainedEntityPosition, animation.Entity.Position);
    }

    [Fact]
    public void HiddenAnimationWithoutRemoteDeleteInsideHook_StopsBeforeManagerTailOrPublication()
    {
        var (live, record, animation) = BuildHiddenAnimated(RemoteGuid);
        int captures = 0;
        int rootPublishes = 0;
        LiveEntityAnimationScheduler scheduler = BuildScheduler(
            live,
            LocalGuid,
            (_, _) =>
            {
                captures++;
                live.UnregisterLiveEntity(
                    new DeleteObject.Parsed(RemoteGuid, record.Generation),
                    isLocalPlayer: false);
            },
            _ => rootPublishes++);

        IReadOnlyDictionary<RuntimeEntityKey, LiveEntityAnimationSchedule> schedules = scheduler.Tick(
            PhysicsBody.MaxQuantum * 2.5f,
            animation.Entity.Position,
            localHiddenPartPoseDirty: false,
            liveCenterX: 0,
            liveCenterY: 0);

        Assert.Equal(1, captures);
        Assert.Equal(0, rootPublishes);
        Assert.Empty(schedules);
        Assert.False(live.TryGetRecord(RemoteGuid, out _));
    }

    [Fact]
    public void WithdrawAndReenterInsideHook_InvalidatesOldCatchUpBatch()
    {
        var (live, record, animation) = BuildVisibleAnimatedWithPosFrames(RemoteGuid);
        ulong startingEpoch = record.ObjectClockEpoch;
        int captures = 0;
        int rootPublishes = 0;
        LiveEntityAnimationScheduler scheduler = BuildScheduler(
            live,
            LocalGuid,
            (_, _) =>
            {
                captures++;
                Assert.True(live.WithdrawLiveEntityProjection(RemoteGuid));
                Assert.Same(
                    animation.Entity,
                    live.MaterializeLiveEntity(
                        RemoteGuid,
                        Cell,
                        _ => throw new InvalidOperationException(
                            "A retained projection must not be reconstructed.")));
            },
            _ => rootPublishes++);

        IReadOnlyDictionary<RuntimeEntityKey, LiveEntityAnimationSchedule> schedules = scheduler.Tick(
            PhysicsBody.MaxQuantum * 2.5f,
            animation.Entity.Position,
            localHiddenPartPoseDirty: false,
            liveCenterX: 0,
            liveCenterY: 0);

        Assert.Equal(1, captures);
        Assert.Equal(0, rootPublishes);
        Assert.Empty(schedules);
        Assert.Equal(startingEpoch + 2, record.ObjectClockEpoch);
        Assert.True(record.ObjectClock.IsActive);
        Assert.Equal(0d, record.ObjectClock.PendingSeconds);
    }

    [Fact]
    public void LocalRootPosePublishesWithoutAnimationAndBetweenObjectQuanta()
    {
        var (live, _, entity) = BuildMaterialized(LocalGuid, PhysicsStateFlags.None);
        var published = new List<Vector3>();
        LiveEntityAnimationScheduler scheduler = BuildScheduler(
            live,
            LocalGuid,
            publishRootPose: current => published.Add(current.Position));

        scheduler.Tick(
            PhysicsBody.MinQuantum * 0.25f,
            entity.Position,
            localHiddenPartPoseDirty: false,
            liveCenterX: 0,
            liveCenterY: 0);
        entity.SetPosition(entity.Position + new Vector3(0.25f, 0f, 0f));
        scheduler.Tick(
            PhysicsBody.MinQuantum * 0.25f,
            entity.Position,
            localHiddenPartPoseDirty: false,
            liveCenterX: 0,
            liveCenterY: 0);

        Assert.Equal(2, published.Count);
        Assert.Equal(entity.Position, published[1]);
    }

    [Fact]
    public void AttachedProjection_IsNotAnIndependentOrdinaryRoot()
    {
        var (live, record, entity) = BuildMaterialized(
            RemoteGuid,
            PhysicsStateFlags.None);
        int rootPublishes = 0;
        LiveEntityAnimationScheduler scheduler = BuildScheduler(
            live,
            LocalGuid,
            publishRootPose: _ => rootPublishes++);

        Assert.Same(
            entity,
            live.MaterializeLiveEntity(
                RemoteGuid,
                Cell,
                _ => throw new InvalidOperationException(),
                LiveEntityProjectionKind.Attached));
        Assert.False(record.ObjectClock.IsActive);
        Assert.False(live.IsCurrentSpatialRootObject(record));

        scheduler.Tick(
            PhysicsBody.MaxQuantum,
            entity.Position,
            localHiddenPartPoseDirty: false,
            liveCenterX: 0,
            liveCenterY: 0);
        Assert.Equal(0, rootPublishes);

        Assert.Same(
            entity,
            live.MaterializeLiveEntity(
                RemoteGuid,
                Cell,
                _ => throw new InvalidOperationException(),
                LiveEntityProjectionKind.World));
        Assert.True(record.ObjectClock.IsActive);
        Assert.True(live.IsCurrentSpatialRootObject(record));

        scheduler.Tick(
            PhysicsBody.MaxQuantum,
            entity.Position,
            localHiddenPartPoseDirty: false,
            liveCenterX: 0,
            liveCenterY: 0);
        Assert.Equal(1, rootPublishes);
    }

    private static AcDream.Runtime.Physics.RemoteMotion BuildRemote(WorldEntity entity)
    {
        var remote = new AcDream.Runtime.Physics.RemoteMotion
        {
            CellId = Cell,
        };
        remote.Body.Position = entity.Position;
        remote.Body.Orientation = entity.Rotation;
        remote.Body.InWorld = true;
        remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact
            | TransientStateFlags.OnWalkable;
        return remote;
    }

    private static LiveEntityAnimationScheduler BuildScheduler(
        LiveEntityRuntime live,
        uint localPlayerGuid,
        Action<uint, AnimationSequencer>? captureHooks = null,
        Action<WorldEntity>? publishRootPose = null,
        PhysicsEngine? physics = null,
        ProjectileController? projectiles = null)
    {
        physics ??= live.Physics.Engine;
        var remotePhysics = new RemotePhysicsUpdater(
            live.Physics,
            (_, _) => (0.48f, 1.835f),
            (_, _) => (System.Collections.Immutable.ImmutableArray<FlatCollisionSphere>.Empty, 1f, 0.4f, 0.4f),
            (_, _, _, _) => { });
        var ordinaryPhysics = new LiveEntityOrdinaryPhysicsUpdater(
            live.Physics,
            (_, _) => (0.48f, 1.835f),
            (_, _) => (System.Collections.Immutable.ImmutableArray<FlatCollisionSphere>.Empty, 1f, 0.4f, 0.4f));
        var identity = new LocalPlayerIdentityState { ServerGuid = localPlayerGuid };
        var poses = new EntityEffectPoseRegistry();
        foreach (LiveEntityRecord record in live.MaterializedRecords)
            poses.PublishMeshRefs(record.WorldEntity!);
        IAnimationHookCaptureSink animationHooks = captureHooks is null
            ? new AnimationHookCaptureSink(new AnimationHookFrameQueue(
                new AnimationHookRouter(),
                poses))
            : new TestAnimationHookCaptureSink(captureHooks);
        projectiles ??= new ProjectileController(live);
        return new LiveEntityAnimationScheduler(
            live,
            identity,
            remotePhysics,
            ordinaryPhysics,
            projectiles,
            new TestRootPosePublisher(poses, publishRootPose),
            animationHooks);
    }

    private sealed class TestAnimationHookCaptureSink(
        Action<uint, AnimationSequencer> capture) : IAnimationHookCaptureSink
    {
        public void Capture(uint ownerLocalId, AnimationSequencer sequencer) =>
            capture(ownerLocalId, sequencer);
    }

    private sealed class TestRootPosePublisher(
        EntityEffectPoseRegistry poses,
        Action<WorldEntity>? published) : IEntityRootPosePublisher
    {
        public void UpdateRoot(WorldEntity entity)
        {
            poses.UpdateRoot(entity);
            published?.Invoke(entity);
        }
    }

    private static (LiveEntityRuntime Live, LiveEntityRecord Record,
        LiveEntityAnimationState Animation) BuildHiddenAnimated(uint guid)
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(new LoadedLandblock(
            0x0101FFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var live = LiveEntityRuntimeFixture.Create(
            spatial,
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));
        LiveEntityRecord record = live.RegisterAndMaterializeProjection(
            Spawn(guid),
            id => new WorldEntity
            {
                Id = id,
                ServerGuid = guid,
                SourceGfxObjOrSetupId = 0x02000001u,
                Position = new Vector3(10f, 10f, 5f),
                Rotation = Quaternion.Identity,
                MeshRefs = Array.Empty<MeshRef>(),
                ParentCellId = Cell,
            });
        WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);
        var setup = new Setup();
        var animation = new LiveEntityAnimationState
        {
            Entity = entity,
            Setup = setup,
            Animation = new Animation(),
            LowFrame = 0,
            HighFrame = 0,
            Framerate = 0f,
            Scale = 1f,
            PartTemplate = Array.Empty<LiveAnimationPartTemplate>(),
            PartAvailability = Array.Empty<bool>(),
            Sequencer = new AnimationSequencer(
                setup,
                new MotionTable(),
                new NullAnimationLoader()),
        };
        record.HasPartArray = true;
        live.SetAnimationRuntime(guid, animation);
        return (live, record, animation);
    }

    private static (LiveEntityRuntime Live, LiveEntityRecord Record,
        LiveEntityAnimationState Animation) BuildVisibleAnimatedWithPosFrames(
            uint guid,
            Quaternion? rootOrientation = null,
            Vector3? rootOrigin = null)
    {
        var (live, record, entity) = BuildMaterialized(guid, PhysicsStateFlags.None);
        const uint animationId = 0x0300AA01u;
        var setup = new Setup();
        setup.Parts.Add(0x0100AA01u);
        setup.DefaultScale.Add(Vector3.One);
        var datAnimation = new Animation
        {
            Flags = AnimationFlags.PosFrames,
        };
        for (int i = 0; i < 4; i++)
        {
            var partFrame = new AnimationFrame(1);
            partFrame.Frames.Add(new Frame
            {
                Origin = Vector3.Zero,
                Orientation = Quaternion.Identity,
            });
            datAnimation.PartFrames.Add(partFrame);
            datAnimation.PosFrames.Add(new Frame
            {
                Origin = rootOrigin ?? Vector3.UnitX,
                Orientation = rootOrientation ?? Quaternion.Identity,
            });
        }
        var loader = new DictionaryAnimationLoader(animationId, datAnimation);
        var sequencer = new AnimationSequencer(setup, new MotionTable(), loader);
        Assert.True(sequencer.InitializeSetupDefaultAnimation(animationId));
        var animation = new LiveEntityAnimationState
        {
            Entity = entity,
            Setup = setup,
            Animation = datAnimation,
            LowFrame = 0,
            HighFrame = 3,
            Framerate = 30f,
            Scale = 1f,
            PartTemplate = new[]
            {
                new LiveAnimationPartTemplate(
                    0x0100AA01u,
                    SurfaceOverrides: null,
                    IsDrawable: true),
            },
            PartAvailability = new[] { true },
            Sequencer = sequencer,
        };
        record.HasPartArray = true;
        live.SetAnimationRuntime(guid, animation);
        return (live, record, animation);
    }

    private static readonly PhysicsStateFlags ProjectileState =
        PhysicsStateFlags.ReportCollisions
        | PhysicsStateFlags.Missile
        | PhysicsStateFlags.AlignPath
        | PhysicsStateFlags.PathClipped;

    private static Setup ProjectileSetup() => new()
    {
        Spheres =
        {
            new Sphere
            {
                Origin = Vector3.Zero,
                Radius = 0.1f,
            },
        },
    };

    private static (LiveEntityRuntime Live, LiveEntityRecord Record, WorldEntity Entity)
        BuildMaterialized(uint guid, PhysicsStateFlags state)
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(new LoadedLandblock(
            0x0101FFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var live = LiveEntityRuntimeFixture.Create(
            spatial,
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));
        LiveEntityRecord record = live.RegisterAndMaterializeProjection(
            Spawn(guid, state),
            id => new WorldEntity
            {
                Id = id,
                ServerGuid = guid,
                SourceGfxObjOrSetupId = 0x02000001u,
                Position = new Vector3(10f, 10f, 5f),
                Rotation = Quaternion.Identity,
                MeshRefs = Array.Empty<MeshRef>(),
                ParentCellId = Cell,
            });
        WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);
        return (live, record, entity);
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        PhysicsStateFlags state = PhysicsStateFlags.Hidden
            | PhysicsStateFlags.IgnoreCollisions)
    {
        var position = new CreateObject.ServerPosition(
            Cell, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(1, 1, 1, 1, 0, 1, 0, 1, 1);
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
            "hidden scheduler fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: (uint)state,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private sealed class NullAnimationLoader : IAnimationLoader
    {
        public Animation? LoadAnimation(uint id) => null;
    }

    private sealed class DictionaryAnimationLoader : IAnimationLoader
    {
        private readonly uint _id;
        private readonly Animation _animation;

        public DictionaryAnimationLoader(uint id, Animation animation)
        {
            _id = id;
            _animation = animation;
        }

        public Animation? LoadAnimation(uint id) => id == _id ? _animation : null;
    }
}
