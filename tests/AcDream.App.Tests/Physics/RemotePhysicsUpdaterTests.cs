using System.Numerics;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Physics;

public sealed class RemotePhysicsUpdaterTests
{
    [Fact]
    public void ShadowPoseGate_TracksTranslationAndSignInvariantOrientation()
    {
        Quaternion turn = Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ,
            MathF.PI / 2f);

        Assert.False(RemotePhysicsUpdater.ShouldSynchronizeShadowPose(
            Vector3.Zero,
            Quaternion.Identity,
            Vector3.Zero,
            Quaternion.Identity));
        Assert.False(RemotePhysicsUpdater.ShouldSynchronizeShadowPose(
            Vector3.Zero,
            turn,
            Vector3.Zero,
            new Quaternion(-turn.X, -turn.Y, -turn.Z, -turn.W)));
        Assert.True(RemotePhysicsUpdater.ShouldSynchronizeShadowPose(
            new Vector3(0.02f, 0f, 0f),
            Quaternion.Identity,
            Vector3.Zero,
            Quaternion.Identity));
        Assert.True(RemotePhysicsUpdater.ShouldSynchronizeShadowPose(
            Vector3.Zero,
            turn,
            Vector3.Zero,
            Quaternion.Identity));
        Assert.True(RemotePhysicsUpdater.ShouldSynchronizeShadow(
            cellChanged: true,
            Vector3.Zero,
            Quaternion.Identity,
            Vector3.Zero,
            Quaternion.Identity));
    }

    [Fact]
    public void Tick_InPlaceCompleteRootTurn_UpdatesOffsetCollisionShadow()
    {
        const uint cellId = 0x01010001u;
        Quaternion turn = Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ,
            MathF.PI / 2f);
        var entity = new WorldEntity
        {
            Id = LiveEntityRuntime.FirstLiveEntityId + 13u,
            ServerGuid = 0x80000013u,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
            ParentCellId = cellId,
        };
        var motion = new AcDream.Runtime.Physics.RemoteMotion
        {
            LastShadowSyncPos = Vector3.Zero,
            LastShadowSyncOrientation = Quaternion.Identity,
        };
        motion.Body.Position = Vector3.Zero;
        motion.Body.Orientation = Quaternion.Identity;
        motion.CellId = cellId;
        RemoteBinding binding = BindRemote(entity, motion, cellId);
        binding.Engine.ShadowObjects.RegisterMultiPart(
            entity.Id,
            entity.Position,
            entity.Rotation,
            [
                ShadowShape.Cylinder(
                    0x01000001u,
                    new Vector3(1f, 0f, 0f),
                    Quaternion.Identity,
                    scale: 1f,
                    radius: 0.25f,
                    cylHeight: 1f),
            ],
            state: (uint)PhysicsStateFlags.ReportCollisions,
            flags: EntityCollisionFlags.None,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: 0x01010000u,
            seedCellId: cellId);
        binding.Updater.Tick(
            motion,
            entity,
            objectScale: 1f,
            sequencer: null,
            animationForVelocityCycle: null,
            dt: 0.1f,
            new MotionDeltaFrame { Orientation = turn },
            liveCenterX: 1,
            liveCenterY: 1,
            ownerRuntime: binding.Live,
            ownerRecord: binding.Record,
            ownerClockEpoch: binding.Record.ObjectClockEpoch);

        ShadowEntry entry = Assert.Single(
            binding.Engine.ShadowObjects.AllEntriesForDebug(),
            candidate => candidate.EntityId == entity.Id);
        Assert.Equal(0f, entry.Position.X, 3);
        Assert.Equal(1f, entry.Position.Y, 3);
        Assert.InRange(
            MathF.Abs(Quaternion.Dot(
                turn,
                Quaternion.Normalize(motion.LastShadowSyncOrientation))),
            0.99999f,
            1.00001f);
    }

    [Fact]
    public void Tick_ComposesCompleteSequenceFrameWithoutOmegaSideChannel()
    {
        var entity = new WorldEntity
        {
            Id = LiveEntityRuntime.FirstLiveEntityId + 10u,
            ServerGuid = 0x80000010u,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = new Vector3(10f, 20f, 30f),
            Rotation = Quaternion.CreateFromAxisAngle(
                Vector3.UnitZ,
                -MathF.PI / 2f),
            MeshRefs = Array.Empty<MeshRef>(),
        };
        var animated = new LiveEntityAnimationState
        {
            Entity = entity,
            Setup = new Setup(),
            Animation = new Animation(),
            LowFrame = 0,
            HighFrame = 0,
            Framerate = 0f,
            Scale = 1f,
            PartTemplate = Array.Empty<LiveAnimationPartTemplate>(),
            PartAvailability = Array.Empty<bool>(),
        };
        var motion = new AcDream.Runtime.Physics.RemoteMotion();
        motion.Body.Position = entity.Position;
        motion.Body.Orientation = entity.Rotation;
        motion.Body.TransientState = TransientStateFlags.Contact
                                   | TransientStateFlags.OnWalkable
                                   | TransientStateFlags.Active;
        var sequenceFrame = new MotionDeltaFrame
        {
            Origin = new Vector3(0f, 0.1f, 0f),
            Orientation = Quaternion.CreateFromAxisAngle(
                Vector3.UnitZ,
                MathF.PI / 2f),
        };
        RemoteBinding binding = BindRemote(entity, motion);
        binding.Updater.Tick(
            motion,
            animated,
            dt: 0.1f,
            sequenceFrame,
            liveCenterX: 0,
            liveCenterY: 0,
            ownerRuntime: binding.Live,
            ownerRecord: binding.Record,
            ownerClockEpoch: binding.Record.ObjectClockEpoch);

        Assert.Equal(10.1f, motion.Body.Position.X, 3);
        Assert.Equal(20f, motion.Body.Position.Y, 3);
        Assert.InRange(
            MathF.Abs(Quaternion.Dot(
                Quaternion.Identity,
                Quaternion.Normalize(motion.Body.Orientation))),
            0.99999f,
            1.00001f);
        Assert.Equal(motion.Body.Orientation, entity.Rotation);
    }

    [Fact]
    public void Tick_HostlessAirborneRemote_SuppressesOriginButPreservesRootOrientation()
    {
        Quaternion initial = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.1f);
        Quaternion rootTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.9f);
        var entity = new WorldEntity
        {
            Id = LiveEntityRuntime.FirstLiveEntityId + 11u,
            ServerGuid = 0x80000011u,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = new Vector3(10f, 20f, 30f),
            Rotation = initial,
            MeshRefs = Array.Empty<MeshRef>(),
        };
        var animated = new LiveEntityAnimationState
        {
            Entity = entity,
            Setup = new Setup(),
            Animation = new Animation(),
            LowFrame = 0,
            HighFrame = 0,
            Framerate = 0f,
            Scale = 1f,
            PartTemplate = Array.Empty<LiveAnimationPartTemplate>(),
            PartAvailability = Array.Empty<bool>(),
        };
        var motion = new AcDream.Runtime.Physics.RemoteMotion
        {
            Airborne = true,
        };
        motion.Body.Position = entity.Position;
        motion.Body.Orientation = initial;
        motion.Body.TransientState = TransientStateFlags.Active;
        var root = new MotionDeltaFrame
        {
            Origin = new Vector3(0f, 10f, 0f),
            Orientation = rootTurn,
        };
        RemoteBinding binding = BindRemote(entity, motion);
        binding.Updater.Tick(
            motion,
            animated,
            0.1f,
            root,
            0,
            0,
            ownerRuntime: binding.Live,
            ownerRecord: binding.Record,
            ownerClockEpoch: binding.Record.ObjectClockEpoch);

        Assert.Equal(new Vector3(10f, 20f, 30f), motion.Body.Position);
        Quaternion expected = Quaternion.Normalize(initial * rootTurn);
        Assert.InRange(
            MathF.Abs(Quaternion.Dot(
                expected,
                Quaternion.Normalize(motion.Body.Orientation))),
            0.99999f,
            1.00001f);
    }

    [Fact]
    public void Tick_ActiveInterpolation_ReplacesRootWithCompleteTargetFrame()
    {
        Quaternion initial = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.1f);
        Quaternion target = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.9f);
        var entity = new WorldEntity
        {
            Id = LiveEntityRuntime.FirstLiveEntityId + 12u,
            ServerGuid = 0x80000012u,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = new Vector3(10f, 20f, 30f),
            Rotation = initial,
            MeshRefs = Array.Empty<MeshRef>(),
        };
        var animated = new LiveEntityAnimationState
        {
            Entity = entity,
            Setup = new Setup(),
            Animation = new Animation(),
            LowFrame = 0,
            HighFrame = 0,
            Framerate = 0f,
            Scale = 1f,
            PartTemplate = Array.Empty<LiveAnimationPartTemplate>(),
            PartAvailability = Array.Empty<bool>(),
        };
        var motion = new AcDream.Runtime.Physics.RemoteMotion();
        motion.Body.Position = entity.Position;
        motion.Body.Orientation = initial;
        motion.Interp.Enqueue(
            entity.Position + Vector3.UnitX,
            target,
            isMovingTo: false,
            currentBodyPosition: entity.Position);
        var root = new MotionDeltaFrame
        {
            Origin = new Vector3(0f, 10f, 0f),
            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f),
        };
        RemoteBinding binding = BindRemote(entity, motion);
        binding.Updater.Tick(
            motion,
            animated,
            0.1f,
            root,
            0,
            0,
            ownerRuntime: binding.Live,
            ownerRecord: binding.Record,
            ownerClockEpoch: binding.Record.ObjectClockEpoch);

        Assert.InRange(motion.Body.Position.X, 10.19f, 10.21f);
        Assert.InRange(
            MathF.Abs(Quaternion.Dot(
                Quaternion.Normalize(target),
                Quaternion.Normalize(motion.Body.Orientation))),
            0.99999f,
            1.00001f);
        Assert.Equal(motion.Body.Orientation, entity.Rotation);
    }

    [Fact]
    public void TickHidden_AppliesPositionManagerOffsetWithoutAdvancingPhysics()
    {
        var motion = new AcDream.Runtime.Physics.RemoteMotion();
        motion.Body.Position = Vector3.Zero;
        motion.Body.Orientation = Quaternion.Identity;
        motion.Body.Velocity = new Vector3(4f, 0f, 5f);
        motion.Body.Acceleration = new Vector3(0f, 0f, -9.8f);
        motion.CellId = 0x01010001u;
        motion.Interp.Enqueue(
            new Vector3(1f, 0f, 0f),
            heading: 0f,
            isMovingTo: false,
            currentBodyPosition: Vector3.Zero);
        var entity = new WorldEntity
        {
            Id = LiveEntityRuntime.FirstLiveEntityId + 1u,
            ServerGuid = 0x70000001u,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
        };

        RemoteBinding binding = BindRemote(
            entity,
            motion,
            motion.CellId);
        var poses = new EntityEffectPoseRegistry();
        poses.Publish(entity, Array.Empty<Matrix4x4>());
        binding.Updater.TickHidden(
            motion,
            entity,
            0.1f,
            ownerRuntime: binding.Live,
            ownerRecord: binding.Record,
            ownerClockEpoch: binding.Record.ObjectClockEpoch);
        Assert.True(poses.UpdateRoot(entity));

        Assert.InRange(motion.Body.Position.X, 0.01f, 1f);
        Assert.Equal(0f, motion.Body.Position.Z);
        Assert.Equal(new Vector3(4f, 0f, 5f), motion.Body.Velocity);
        Assert.Equal(motion.Body.Position, entity.Position);
        Assert.Equal(motion.CellId, entity.ParentCellId);
        Assert.True(poses.TryGetRootPose(entity.Id, out Matrix4x4 root));
        Assert.Equal(entity.Position, root.Translation);
    }

    [Fact]
    public void TickHidden_RunsPartArrayManagerTailWithoutAdvancingSequence()
    {
        var motion = new AcDream.Runtime.Physics.RemoteMotion();
        motion.Body.Orientation = Quaternion.Identity;
        var entity = new WorldEntity
        {
            Id = LiveEntityRuntime.FirstLiveEntityId + 2u,
            ServerGuid = 0x70000002u,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
        };
        RemoteBinding binding = BindRemote(entity, motion);
        AnimationSequencer sequencer = CreateSequencer();
        sequencer.Manager.AddToQueue(MotionTableManager.ReadySentinel, 0);

        binding.Updater.TickHidden(
            motion,
            entity,
            0.1f,
            sequencer.Manager,
            ownerRuntime: binding.Live,
            ownerRecord: binding.Record,
            ownerClockEpoch: binding.Record.ObjectClockEpoch);

        Assert.Empty(sequencer.Manager.PendingAnimations);
    }

    [Fact]
    public void TickHidden_ProcessesPendingHooksBeforeManagerTail()
    {
        var motion = new AcDream.Runtime.Physics.RemoteMotion();
        motion.Body.Orientation = Quaternion.Identity;
        var entity = new WorldEntity
        {
            Id = LiveEntityRuntime.FirstLiveEntityId + 3u,
            ServerGuid = 0x70000003u,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
        };
        RemoteBinding binding = BindRemote(entity, motion);
        AnimationSequencer sequencer = CreateSequencer();
        sequencer.Manager.AddToQueue(MotionTableManager.ReadySentinel, 0);
        var order = new List<string>();

        binding.Updater.TickHidden(
            motion,
            entity,
            0.1f,
            partArrayHandleMovement: sequencer.Manager,
            processAnimationHooks: (_, observed) =>
            {
                Assert.Same(sequencer, observed);
                Assert.NotEmpty(sequencer.Manager.PendingAnimations);
                order.Add("hooks");
            },
            sequencer,
            binding.Live,
            binding.Record,
            binding.Record.ObjectClockEpoch);

        Assert.Equal(["hooks"], order);
        Assert.Empty(sequencer.Manager.PendingAnimations);
    }

    [Fact]
    public void TickHidden_CrossCellCommitUpdatesCanonicalCellBeforeUnhide()
    {
        var motion = new AcDream.Runtime.Physics.RemoteMotion();
        motion.Body.Position = new Vector3(191f, 10f, 50f);
        motion.Body.Orientation = Quaternion.Identity;
        motion.Body.TransientState = TransientStateFlags.Contact
                                   | TransientStateFlags.OnWalkable;
        uint canonicalCell = 0xA9B40039u;
        motion.CellId = canonicalCell;
        motion.Interp.Enqueue(
            new Vector3(193f, 10f, 50f),
            heading: 0f,
            isMovingTo: false,
            currentBodyPosition: motion.Body.Position);
        var entity = new WorldEntity
        {
            Id = LiveEntityRuntime.FirstLiveEntityId + 1u,
            ServerGuid = 0x70000002u,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = motion.Body.Position,
            Rotation = Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
        };
        RemoteBinding binding = BindRemote(
            entity,
            motion,
            canonicalCell);
        PopulateBoundaryEngine(binding.Engine);
        ulong spatialAuthority = binding.Record.Canonical
            .SpatialAuthorityVersion;

        binding.Updater.TickHidden(
            motion,
            entity,
            2f,
            ownerRuntime: binding.Live,
            ownerRecord: binding.Record,
            ownerClockEpoch: binding.Record.ObjectClockEpoch);

        Assert.Equal(0xAAB40001u, binding.Record.FullCellId);
        Assert.Equal(binding.Record.FullCellId, entity.ParentCellId);
        Assert.True(
            binding.Record.Canonical.SpatialAuthorityVersion
            > spatialAuthority);
        Assert.True(entity.Position.X > 192f);
    }

    [Fact]
    public void TickHidden_OutOfContactDoesNotInterpolateOrInventLanding()
    {
        var motion = new AcDream.Runtime.Physics.RemoteMotion
        {
            Airborne = true,
        };
        motion.Body.Position = new Vector3(10f, 10f, 50f);
        motion.Body.Orientation = Quaternion.Identity;
        motion.Body.TransientState = TransientStateFlags.Active;
        motion.CellId = 0xA9B40001u;
        motion.Interp.Enqueue(
            new Vector3(11f, 10f, 50f),
            heading: 0f,
            isMovingTo: false,
            currentBodyPosition: motion.Body.Position);
        var entity = new WorldEntity
        {
            Id = LiveEntityRuntime.FirstLiveEntityId + 3u,
            ServerGuid = 0x70000003u,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = motion.Body.Position,
            Rotation = Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
        };
        RemoteBinding binding = BindRemote(
            entity,
            motion,
            motion.CellId);
        PopulateBoundaryEngine(binding.Engine);

        binding.Updater.TickHidden(
            motion,
            entity,
            0.1f,
            ownerRuntime: binding.Live,
            ownerRecord: binding.Record,
            ownerClockEpoch: binding.Record.ObjectClockEpoch);

        Assert.False(motion.Body.InContact);
        Assert.False(motion.Body.OnWalkable);
        Assert.True(motion.Airborne);
        Assert.Equal(new Vector3(10f, 10f, 50f), motion.Body.Position);
    }

    [Fact]
    public void Tick_CrossCellIntoPending_CommitsRootAndSuspendsShadowBeforeReturning()
    {
        using var fixture = new BoundaryRemoteFixture();
        Vector3 sourceShadowPosition = fixture.Remote.LastShadowSyncPos;
        ulong clockEpoch = fixture.Record.ObjectClockEpoch;

        bool current = fixture.Updater.Tick(
            fixture.Remote,
            fixture.Entity,
            objectScale: 1f,
            sequencer: null,
            animationForVelocityCycle: null,
            dt: 2f,
            rootMotionLocalFrame: new MotionDeltaFrame(),
            liveCenterX: 0,
            liveCenterY: 0,
            ownerRuntime: fixture.Live,
            ownerRecord: fixture.Record,
            ownerClockEpoch: clockEpoch);

        Assert.False(current);
        Assert.Equal(BoundaryRemoteFixture.DestinationCell, fixture.Record.FullCellId);
        Assert.False(fixture.Record.IsSpatiallyVisible);
        Assert.Equal(fixture.Remote.Body.Position, fixture.Entity.Position);
        Assert.Equal(BoundaryRemoteFixture.DestinationCell, fixture.Entity.ParentCellId);
        Assert.True(fixture.Entity.Position.X > 192f);
        Assert.Equal(sourceShadowPosition, fixture.Remote.LastShadowSyncPos);
        Assert.Equal(0, fixture.Engine.ShadowObjects.TotalRegistered);
        Assert.Equal(1, fixture.Engine.ShadowObjects.RetainedRegistrationCount);
        Assert.Equal(1, fixture.Engine.ShadowObjects.SuspendedRegistrationCount);

        fixture.HydrateDestination();

        Assert.True(fixture.Record.IsSpatiallyVisible);
        Assert.Equal(1, fixture.Engine.ShadowObjects.TotalRegistered);
        Assert.Equal(0, fixture.Engine.ShadowObjects.SuspendedRegistrationCount);
        ShadowEntry restored = Assert.Single(
            fixture.Engine.ShadowObjects.AllEntriesForDebug());
        Assert.Equal(fixture.Entity.Id, restored.EntityId);
        Assert.Equal(fixture.Entity.Position, restored.Position);
    }

    [Fact]
    public void AuthoritativeShadowPublisher_AlreadyPendingCannotResurrectShadow()
    {
        using var fixture = new BoundaryRemoteFixture();
        int publications = 0;

        Assert.True(LiveEntityShadowPublisher.TryPublishRemote(
            fixture.Live,
            fixture.Record,
            fixture.Entity,
            fixture.Remote,
            fixture.Record.PositionAuthorityVersion,
            () => publications++));
        Assert.Equal(1, publications);

        Assert.True(fixture.Live.RebucketLiveEntity(
            BoundaryRemoteFixture.Guid,
            BoundaryRemoteFixture.DestinationCell));
        Assert.False(fixture.Record.IsSpatiallyVisible);
        Assert.Equal(0, fixture.Engine.ShadowObjects.TotalRegistered);
        Assert.Equal(1, fixture.Engine.ShadowObjects.SuspendedRegistrationCount);

        Assert.False(LiveEntityShadowPublisher.TryPublishRemote(
            fixture.Live,
            fixture.Record,
            fixture.Entity,
            fixture.Remote,
            fixture.Record.PositionAuthorityVersion,
            () => publications++));
        Assert.Equal(1, publications);
        Assert.Equal(0, fixture.Engine.ShadowObjects.TotalRegistered);
        Assert.Equal(1, fixture.Engine.ShadowObjects.SuspendedRegistrationCount);

        fixture.HydrateDestination();

        Assert.True(fixture.Record.IsSpatiallyVisible);
        Assert.Equal(1, fixture.Engine.ShadowObjects.TotalRegistered);
        Assert.Equal(0, fixture.Engine.ShadowObjects.SuspendedRegistrationCount);
    }

    [Fact]
    public void AuthoritativeShadowPublisher_StaleAuthorityAndGuidReuseCannotPublish()
    {
        using var fixture = new BoundaryRemoteFixture();
        LiveEntityRecord oldRecord = fixture.Record;
        WorldEntity oldEntity = fixture.Entity;
        AcDream.Runtime.Physics.RemoteMotion oldRemote = fixture.Remote;
        ulong oldAuthority = oldRecord.PositionAuthorityVersion;
        int publications = 0;

        Assert.False(LiveEntityShadowPublisher.TryPublishRemote(
            fixture.Live,
            oldRecord,
            oldEntity,
            oldRemote,
            oldAuthority + 1,
            () => publications++));

        var replacement = fixture.ReplaceAtSource();

        Assert.False(LiveEntityShadowPublisher.TryPublishRemote(
            fixture.Live,
            oldRecord,
            oldEntity,
            oldRemote,
            oldAuthority,
            () => publications++));
        Assert.Equal(0, publications);
        Assert.Equal(1, fixture.Engine.ShadowObjects.TotalRegistered);
        Assert.Contains(
            fixture.Engine.ShadowObjects.AllEntriesForDebug(),
            entry => entry.EntityId == replacement.Entity.Id);
        Assert.DoesNotContain(
            fixture.Engine.ShadowObjects.AllEntriesForDebug(),
            entry => entry.EntityId == oldEntity.Id);
    }

    [Fact]
    public void Tick_CrossCellVisibilityCallbackGuidReuse_CannotPublishOldShadow()
    {
        using var fixture = new BoundaryRemoteFixture();
        LiveEntityRecord oldRecord = fixture.Record;
        WorldEntity oldEntity = fixture.Entity;
        AcDream.Runtime.Physics.RemoteMotion oldRemote = fixture.Remote;
        WorldEntity? replacementEntity = null;
        AcDream.Runtime.Physics.RemoteMotion? replacementRemote = null;
        ulong clockEpoch = oldRecord.ObjectClockEpoch;

        fixture.Live.ProjectionVisibilityChanged += (record, visible) =>
        {
            if (visible
                || replacementEntity is not null
                || !ReferenceEquals(record, oldRecord))
            {
                return;
            }

            (LiveEntityRecord replacementRecord,
                replacementEntity,
                replacementRemote) = fixture.ReplaceAtSource();
            Assert.NotSame(oldRecord, replacementRecord);
        };

        bool current = fixture.Updater.Tick(
            oldRemote,
            oldEntity,
            objectScale: 1f,
            sequencer: null,
            animationForVelocityCycle: null,
            dt: 2f,
            rootMotionLocalFrame: new MotionDeltaFrame(),
            liveCenterX: 0,
            liveCenterY: 0,
            ownerRuntime: fixture.Live,
            ownerRecord: oldRecord,
            ownerClockEpoch: clockEpoch);

        Assert.False(current);
        Assert.NotNull(replacementEntity);
        Assert.NotNull(replacementRemote);
        Assert.True(fixture.Live.TryGetRecord(
            BoundaryRemoteFixture.Guid,
            out LiveEntityRecord currentRecord));
        Assert.NotSame(oldRecord, currentRecord);
        Assert.Same(replacementEntity, currentRecord.WorldEntity);
        Assert.Same(replacementRemote, currentRecord.RemoteMotionRuntime);
        Assert.Equal(1, fixture.Engine.ShadowObjects.TotalRegistered);
        Assert.Equal(1, fixture.Engine.ShadowObjects.RetainedRegistrationCount);
        Assert.Equal(0, fixture.Engine.ShadowObjects.SuspendedRegistrationCount);
        Assert.Contains(
            fixture.Engine.ShadowObjects.AllEntriesForDebug(),
            entry => entry.EntityId == replacementEntity.Id);
        Assert.DoesNotContain(
            fixture.Engine.ShadowObjects.AllEntriesForDebug(),
            entry => entry.EntityId == oldEntity.Id);
    }

    [Fact]
    public void TickHiddenEntities_DeletionCallbackCannotAdvanceStaleSnapshotOwner()
    {
        const uint firstGuid = 0x70000011u;
        const uint secondGuid = 0x70000012u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(new LoadedLandblock(
            0x0101FFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var live = LiveEntityRuntimeFixture.Create(
            spatial,
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));
        BindHiddenRemote(live, firstGuid);
        BindHiddenRemote(live, secondGuid);
        Assert.Equal(2, live.SpatialRemoteMotionRuntimeCount);

        var updater = new RemotePhysicsUpdater(
            live.Physics,
            (_, _) => (0.48f, 1.835f),
            (_, _) => (System.Collections.Immutable.ImmutableArray<FlatCollisionSphere>.Empty, 1f, 0.4f, 0.4f),
            (_, _, _, _) => { });
        var published = new List<uint>();
        var partPoseDirty = new List<uint>();
        updater.TickHiddenEntities(
            live,
            localPlayerServerGuid: 0x50000001u,
            dt: 0.1f,
            entity =>
            {
                published.Add(entity.ServerGuid);
                uint other = entity.ServerGuid == firstGuid ? secondGuid : firstGuid;
                Assert.True(live.UnregisterLiveEntity(
                    new DeleteObject.Parsed(other, InstanceSequence: 1),
                    isLocalPlayer: false));
            },
            markPartPoseDirty: partPoseDirty.Add);

        Assert.Single(published);
        Assert.Equal(published, partPoseDirty);
        Assert.Equal(1, live.SpatialRemoteMotionRuntimeCount);
    }

    private static void PopulateBoundaryEngine(PhysicsEngine engine)
    {
        static TerrainSurface FlatTerrain()
        {
            var heights = new byte[81];
            var table = new float[256];
            Array.Fill(table, 50f);
            return new TerrainSurface(heights, table);
        }

        engine.AddLandblock(
            0xA9B4FFFFu,
            FlatTerrain(),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);
        engine.AddLandblock(
            0xAAB4FFFFu,
            FlatTerrain(),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            192f,
            0f);
    }

    private static RemoteBinding BindRemote(
        WorldEntity entity,
        AcDream.Runtime.Physics.RemoteMotion remote,
        uint fullCellId = 0x01010001u)
    {
        entity.ParentCellId = fullCellId;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(new LoadedLandblock(
            (fullCellId & 0xFFFF0000u) | 0xFFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        LiveEntityRuntime live = LiveEntityRuntimeFixture.Create(
            spatial,
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }),
            firstLocalEntityId: entity.Id);
        LiveEntityRecord record = live.RegisterAndMaterializeProjection(
            SpawnRemote(
                entity.ServerGuid,
                fullCellId,
                entity.Position),
            localId =>
            {
                Assert.Equal(entity.Id, localId);
                return entity;
            });
        remote.CellId = fullCellId;
        live.SetRemoteMotionRuntime(entity.ServerGuid, remote);
        return new RemoteBinding(
            live,
            record,
            live.Physics.Engine,
            new RemotePhysicsUpdater(
                live.Physics,
                (_, _) => (0.48f, 1.835f),
                (_, _) => (System.Collections.Immutable.ImmutableArray<FlatCollisionSphere>.Empty, 1f, 0.4f, 0.4f),
                (_, _, _, _) => { }));
    }

    private static WorldSession.EntitySpawn SpawnRemote(
        uint guid,
        uint fullCellId,
        Vector3 position)
    {
        const PhysicsStateFlags state =
            PhysicsStateFlags.ReportCollisions;
        var serverPosition = new CreateObject.ServerPosition(
            fullCellId,
            position.X,
            position.Y,
            position.Z,
            1f,
            0f,
            0f,
            0f);
        var timestamps = new PhysicsTimestamps(
            1,
            1,
            1,
            1,
            0,
            1,
            0,
            1,
            1);
        var physics = new PhysicsSpawnData(
            RawState: (uint)state,
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
            "remote physics fixture",
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

    private sealed record RemoteBinding(
        LiveEntityRuntime Live,
        LiveEntityRecord Record,
        PhysicsEngine Engine,
        RemotePhysicsUpdater Updater);

    private static void BindHiddenRemote(LiveEntityRuntime live, uint guid)
    {
        const uint cellId = 0x01010001u;
        PhysicsStateFlags state = PhysicsStateFlags.Hidden
            | PhysicsStateFlags.IgnoreCollisions;
        var position = new CreateObject.ServerPosition(
            cellId, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
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
        live.RegisterLiveEntity(new WorldSession.EntitySpawn(
            guid,
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "hidden fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: (uint)state,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics));
        WorldEntity entity = live.MaterializeLiveEntity(
            guid,
            cellId,
            id => new WorldEntity
            {
                Id = id,
                ServerGuid = guid,
                SourceGfxObjOrSetupId = 0x02000001u,
                Position = new Vector3(10f, 10f, 5f),
                Rotation = Quaternion.Identity,
                MeshRefs = Array.Empty<MeshRef>(),
                ParentCellId = cellId,
            })!;
        var remote = new AcDream.Runtime.Physics.RemoteMotion();
        remote.Body.Position = entity.Position;
        remote.Body.Orientation = entity.Rotation;
        remote.CellId = cellId;
        remote.Interp.Enqueue(
            entity.Position + Vector3.UnitX,
            heading: 0f,
            isMovingTo: false,
            currentBodyPosition: entity.Position);
        live.SetRemoteMotionRuntime(guid, remote);
    }

    private sealed class BoundaryRemoteFixture : IDisposable
    {
        public const uint Guid = 0x70000071u;
        public const uint SourceCell = 0xA9B40039u;
        public const uint DestinationCell = 0xAAB40001u;

        private LiveEntityPresentationController? _presentation;

        public BoundaryRemoteFixture()
        {
            Engine = null!;
            Spatial.AddLandblock(new LoadedLandblock(
                0xA9B4FFFFu,
                new LandBlock(),
                Array.Empty<WorldEntity>()));
            Live = LiveEntityRuntimeFixture.Create(
                Spatial,
                new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }),
                record =>
                {
                    if (record.WorldEntity is { } entity)
                        Engine.ShadowObjects.Deregister(entity.Id);
                    _presentation?.Forget(record);
                });
            Engine = Live.Physics.Engine;
            PopulateBoundaryEngine(Engine);
            (Record, Entity, Remote) = SpawnAndBind(
                instanceSequence: 1,
                new Vector3(191f, 10f, 50f),
                enqueueDestination: true);
            _presentation = new LiveEntityPresentationController(
                Live,
                Engine.ShadowObjects,
                (_, _, _) => true,
                new LiveEntityPartArrayEnterWorldPort(_ => { }),
                liveCenter: () => (0, 0));
            RegisterShadow(Entity, Remote);
            Assert.True(_presentation.OnLiveEntityReady(Guid));
            Updater = new RemotePhysicsUpdater(
                Live.Physics,
                (_, _) => (0.48f, 1.835f),
                (_, _) => (System.Collections.Immutable.ImmutableArray<FlatCollisionSphere>.Empty, 1f, 0.4f, 0.4f),
                (_, _, _, _) => { });
        }

        public PhysicsEngine Engine { get; }
        public GpuWorldState Spatial { get; } = new();
        public LiveEntityRuntime Live { get; }
        public RemotePhysicsUpdater Updater { get; }
        public LiveEntityRecord Record { get; }
        public WorldEntity Entity { get; }
        public AcDream.Runtime.Physics.RemoteMotion Remote { get; }

        public void HydrateDestination() =>
            Spatial.AddLandblock(new LoadedLandblock(
                0xAAB4FFFFu,
                new LandBlock(),
                Array.Empty<WorldEntity>()));

        public (LiveEntityRecord Record, WorldEntity Entity,
            AcDream.Runtime.Physics.RemoteMotion Remote) ReplaceAtSource()
        {
            Assert.True(Live.UnregisterLiveEntity(
                new DeleteObject.Parsed(Guid, InstanceSequence: 1),
                isLocalPlayer: false));
            var replacement = SpawnAndBind(
                instanceSequence: 2,
                new Vector3(180f, 20f, 50f),
                enqueueDestination: false);
            RegisterShadow(replacement.Entity, replacement.Remote);
            Assert.True(_presentation!.OnLiveEntityReady(Guid));
            return replacement;
        }

        public void Dispose()
        {
            Live.Clear();
            _presentation?.Dispose();
        }

        private (LiveEntityRecord Record, WorldEntity Entity,
            AcDream.Runtime.Physics.RemoteMotion Remote) SpawnAndBind(
            ushort instanceSequence,
            Vector3 position,
            bool enqueueDestination)
        {
            LiveEntityRecord record = Live.RegisterAndMaterializeProjection(
                Spawn(instanceSequence, position),
                    id => new WorldEntity
                    {
                        Id = id,
                        ServerGuid = Guid,
                        SourceGfxObjOrSetupId = 0x02000001u,
                        Position = position,
                        Rotation = Quaternion.Identity,
                        MeshRefs = Array.Empty<MeshRef>(),
                        ParentCellId = SourceCell,
                    });
            WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);
            var remote = new AcDream.Runtime.Physics.RemoteMotion();
            remote.Body.Position = position;
            remote.Body.Orientation = Quaternion.Identity;
            remote.CellId = SourceCell;
            remote.LastShadowSyncPos = position;
            remote.LastShadowSyncOrientation = Quaternion.Identity;
            if (enqueueDestination)
            {
                remote.Interp.Enqueue(
                    new Vector3(193f, 10f, 50f),
                    heading: 0f,
                    isMovingTo: false,
                    currentBodyPosition: position);
            }
            Live.SetRemoteMotionRuntime(Guid, remote);
            return (record, entity, remote);
        }

        private void RegisterShadow(
            WorldEntity entity,
            AcDream.Runtime.Physics.RemoteMotion remote)
        {
            Engine.ShadowObjects.Register(
                entity.Id,
                entity.SourceGfxObjOrSetupId,
                remote.Body.Position,
                remote.Body.Orientation,
                radius: 0.48f,
                worldOffsetX: 0f,
                worldOffsetY: 0f,
                landblockId: 0xA9B40000u,
                collisionType: ShadowCollisionType.Cylinder,
                cylHeight: 1.835f,
                state: (uint)PhysicsStateFlags.ReportCollisions,
                seedCellId: SourceCell,
                isStatic: false);
        }

        private static WorldSession.EntitySpawn Spawn(
            ushort instanceSequence,
            Vector3 position)
        {
            const PhysicsStateFlags state = PhysicsStateFlags.ReportCollisions;
            var serverPosition = new CreateObject.ServerPosition(
                SourceCell,
                position.X,
                position.Y,
                position.Z,
                1f,
                0f,
                0f,
                0f);
            // C3c: residence admission requires the nested block's Instance
            // timestamp to agree with the flattened InstanceSequence.
            var timestamps = new PhysicsTimestamps(
                1, 1, 1, 1, 0, 1, 0, 1, instanceSequence);
            var physics = new PhysicsSpawnData(
                RawState: (uint)state,
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
                Guid,
                serverPosition,
                0x02000001u,
                Array.Empty<CreateObject.AnimPartChange>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.SubPaletteSwap>(),
                null,
                null,
                "boundary remote",
                null,
                null,
                MotionTableId: 0x09000001u,
                PhysicsState: (uint)state,
                InstanceSequence: instanceSequence,
                MovementSequence: 1,
                ServerControlSequence: 1,
                PositionSequence: 1,
                Physics: physics);
        }
    }

    private static AnimationSequencer CreateSequencer() =>
        new(new Setup(), new MotionTable(), new NullAnimationLoader());

    private sealed class NullAnimationLoader : IAnimationLoader
    {
        public Animation? LoadAnimation(uint id) => null;
    }
}
