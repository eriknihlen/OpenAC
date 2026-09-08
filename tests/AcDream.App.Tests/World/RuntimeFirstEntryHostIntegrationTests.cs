using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Plugins;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using AcDream.Runtime.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.World;

public sealed class RuntimeFirstEntryHostIntegrationTests
{
    private const uint Cell = 0x01010001u;
    private const uint Guid = 0x70000301u;

    [Fact]
    public void InitialCreate_ResidenceConductorReceipt_BindsWorldVisibilityExactlyOnce()
    {
        using var fixture = new HostFixture(playerGuid: 0u);
        int residencesBegan = 0;
        fixture.EntityObjects.BindInitialResidenceBeginNotification(
            _ => residencesBegan++);
        bool visibleAtMaterialize = true;
        fixture.Materializer.AfterMaterialize = record =>
        {
            visibleAtMaterialize = record.IsSpatiallyProjected
                || record.IsSpatiallyVisible;
        };

        fixture.Controller.OnCreate(Spawn(Guid, Cell));

        Assert.Equal(1, residencesBegan);
        Assert.False(visibleAtMaterialize);
        Assert.True(fixture.Runtime.TryGetRecord(Guid, out LiveEntityRecord record));
        Assert.False(fixture.Runtime.HasActiveInitialCreateResidence(
            record.Canonical));
        Assert.Equal(Cell, record.Canonical.FullCellId);
        Assert.True(record.IsSpatiallyProjected);
        Assert.True(record.IsSpatiallyVisible);
        Assert.NotNull(record.PhysicsBody);
        Assert.Equal((record, true), Assert.Single(fixture.VisibilityEdges));
        AcDream.Plugin.Abstractions.WorldEntitySnapshot snapshot =
            Assert.Single(fixture.WorldState.Entities);
        Assert.Equal(record.WorldEntity!.Position, snapshot.Position);
        Assert.Equal(0, fixture.EntityObjects.Placements.PendingCount);
        Assert.Equal(0, fixture.FirstEntry.PendingCount);
    }

    [Fact]
    public void DeferredParentCreate_StaysInvisibleUntilParentReplay()
    {
        const uint parentGuid = 0x70000302u;
        const uint childGuid = 0x70000303u;
        using var fixture = new HostFixture(playerGuid: 0u);
        int residencesBegan = 0;
        fixture.EntityObjects.BindInitialResidenceBeginNotification(
            _ => residencesBegan++);

        fixture.Controller.OnCreate(ParentedSpawn(childGuid, parentGuid));

        Assert.Equal(0, residencesBegan);
        Assert.False(fixture.Runtime.TryGetCanonical(childGuid, out _));
        Assert.False(fixture.Runtime.TryGetRecord(childGuid, out _));
        Assert.True(fixture.Runtime.ParentAttachments.ContainsDeferredCreate(
            childGuid,
            instanceSequence: 1));
        Assert.Empty(fixture.WorldState.Entities);
        Assert.Empty(fixture.VisibilityEdges);

        fixture.Controller.OnCreate(Spawn(parentGuid, Cell));
        fixture.FirstEntry.DriveAll();

        Assert.Equal(2, residencesBegan);
        Assert.False(fixture.Runtime.ParentAttachments.ContainsDeferredCreate(
            childGuid,
            instanceSequence: 1));
        Assert.True(fixture.Runtime.TryGetCanonical(
            childGuid,
            out RuntimeEntityRecord child));
        Assert.False(fixture.Runtime.HasActiveInitialCreateResidence(child));
        Assert.Equal(0, fixture.FirstEntry.PendingCount);
        Assert.Equal(0u, child.FullCellId);
        Assert.False(fixture.Runtime.TryGetRecord(childGuid, out _));
        Assert.True(fixture.Runtime.TryGetRecord(
            parentGuid,
            out LiveEntityRecord parent));
        Assert.True(parent.IsSpatiallyVisible);
        Assert.Single(fixture.WorldState.Entities);
    }

    [Fact]
    public void LocalLogin_PresentationAttachFailure_RetriesWithoutRuntimeRollback()
    {
        using var fixture = new HostFixture(playerGuid: Guid);
        fixture.VisibilityFailuresRemaining = 1;

        fixture.Controller.OnCreate(Spawn(Guid, Cell));

        AcDream.Runtime.Gameplay.PlayerMovementController controller =
            Assert.IsType<AcDream.Runtime.Gameplay.PlayerMovementController>(
                fixture.Movement.Controller);
        Assert.True(controller.IsRuntimePublished);
        Assert.True(fixture.Runtime.TryGetRecord(Guid, out LiveEntityRecord record));
        Assert.Equal(Cell, record.Canonical.FullCellId);
        Assert.NotNull(record.PhysicsBody);
        Assert.False(fixture.Runtime.HasActiveInitialCreateResidence(
            record.Canonical));
        Assert.Equal(1, fixture.EntityObjects.Placements.PendingCount);
        Assert.Equal((record, true), Assert.Single(fixture.VisibilityEdges));

        Assert.True(fixture.Subscription.RetryPending());

        Assert.Same(controller, fixture.Movement.Controller);
        Assert.True(controller.IsRuntimePublished);
        Assert.Equal(0, fixture.EntityObjects.Placements.PendingCount);
        Assert.True(record.IsSpatiallyProjected);
        Assert.True(record.IsSpatiallyVisible);
        Assert.Equal(2, fixture.VisibilityEdges.Count);
    }

    [Fact]
    public void LocalLogin_FlatGround_ReportsGroundedOutboundContactBit()
    {
        using var fixture = new HostFixture(
            playerGuid: Guid,
            terrainHeight: 4.7f,
            moverSphereOriginZ: 0.475f);

        fixture.Controller.OnCreate(Spawn(Guid, Cell));

        AcDream.Runtime.Gameplay.PlayerMovementController controller =
            Assert.IsType<AcDream.Runtime.Gameplay.PlayerMovementController>(
                fixture.Movement.Controller);
        Assert.True(controller.IsRuntimePublished);
        Assert.True(fixture.Runtime.TryGetRecord(
            Guid,
            out LiveEntityRecord record));
        PhysicsBody body = Assert.IsType<PhysicsBody>(record.PhysicsBody);
        Assert.True(body.InWorld);
        Assert.True(body.InContact);
        Assert.True(body.OnWalkable);
        Assert.True(body.ContactPlaneValid);
        Assert.InRange(body.Position.Z, 4.65f, 4.76f);
        Assert.True(controller.CanSendPositionEvent);
        Assert.True(controller.CaptureMovementResult(
            mouseLookEvent: false).IsOnGround);
        Assert.Equal(0, fixture.FirstEntry.PendingCount);
    }

    [Fact]
    public void LocalLogin_AirborneSpawn_StaysGenuinelyAirborne()
    {
        using var fixture = new HostFixture(
            playerGuid: Guid,
            moverSphereOriginZ: 0.475f);

        fixture.Controller.OnCreate(Spawn(Guid, Cell));

        AcDream.Runtime.Gameplay.PlayerMovementController controller =
            Assert.IsType<AcDream.Runtime.Gameplay.PlayerMovementController>(
                fixture.Movement.Controller);
        Assert.True(controller.IsRuntimePublished);
        Assert.True(fixture.Runtime.TryGetRecord(
            Guid,
            out LiveEntityRecord record));
        PhysicsBody body = Assert.IsType<PhysicsBody>(record.PhysicsBody);
        Assert.True(body.InWorld);
        Assert.False(body.InContact);
        Assert.False(body.OnWalkable);
        Assert.False(controller.CanSendPositionEvent);
        Assert.False(controller.CaptureMovementResult(
            mouseLookEvent: false).IsOnGround);
    }

    [Fact]
    public void GraphicalAndDirectHosts_CommitIdenticalFirstEntryRuntimeFacts()
    {
        // Graphical host: full flipped wiring.
        using var graphical = new HostFixture(playerGuid: 0u);
        graphical.Controller.OnCreate(Spawn(Guid, Cell));
        Assert.True(graphical.Runtime.TryGetCanonical(
            Guid,
            out RuntimeEntityRecord graphicalRecord));

        LiveEntityRuntimeFixture.DrivenLiveEntityRuntime direct =
            LiveEntityRuntimeFixture.CreateDriven(
                new GpuWorldState(),
                new NoopResources());
        RuntimeEntityRecord directRecord = Assert.IsType<RuntimeEntityRecord>(
            direct.Lifetime.RegisterEntityWithInitialResidence(
                Spawn(Guid, Cell),
                isLocalPlayer: false).Canonical);
        Assert.True(direct.Lifetime.ApplyAcceptedSpawn(
            directRecord,
            directRecord.CreateIntegrationVersion,
            directRecord.Snapshot,
            replaceGeneration: false));
        direct.Pump();

        Assert.Equal(
            FirstEntryFacts.Capture(graphicalRecord),
            FirstEntryFacts.Capture(directRecord));
        Assert.False(graphical.Runtime.HasActiveInitialCreateResidence(
            graphicalRecord));
        Assert.Equal(0, direct.FirstEntry.PendingCount);
        Assert.Equal(0, graphical.FirstEntry.PendingCount);
    }

    private readonly record struct FirstEntryFacts(
        uint ServerGuid,
        ushort Incarnation,
        uint? LocalEntityId,
        uint FullCellId,
        uint CanonicalLandblockId,
        ulong PositionAuthorityVersion,
        ulong PlacementCommitVersion,
        ulong CreateIntegrationVersion,
        ushort SnapshotPositionSequence,
        bool HasBody,
        Vector3 BodyPosition,
        Quaternion BodyOrientation,
        PhysicsStateFlags BodyState,
        bool BodyInWorld)
    {
        internal static FirstEntryFacts Capture(RuntimeEntityRecord record) =>
            new(
                record.ServerGuid,
                record.Incarnation,
                record.LocalEntityId,
                record.FullCellId,
                record.CanonicalLandblockId,
                record.PositionAuthorityVersion,
                record.PlacementCommitVersion,
                record.CreateIntegrationVersion,
                record.Snapshot.PositionSequence,
                record.PhysicsBody is not null,
                record.PhysicsBody?.Position ?? default,
                record.PhysicsBody?.Orientation ?? default,
                record.PhysicsBody?.State ?? default,
                record.PhysicsBody?.InWorld ?? false);
    }

    private static WorldSession.EntitySpawn Spawn(uint guid, uint cell)
    {
        var position = new CreateObject.ServerPosition(
            cell, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
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
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
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
            [],
            [],
            [],
            null,
            null,
            "first entry",
            (uint)ItemType.Creature,
            null,
            0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private static WorldSession.EntitySpawn ParentedSpawn(
        uint guid,
        uint parentGuid)
    {
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
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
            Position: null,
            Movement: null,
            AnimationFrame: 1u,
            SetupTableId: 0x02000001u,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: new PhysicsAttachment(parentGuid, LocationId: 1u),
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
            Position: null,
            SetupTableId: 0x02000001u,
            AnimPartChanges: [],
            TextureChanges: [],
            SubPalettes: [],
            BasePaletteId: null,
            ObjScale: null,
            Name: "deferred child",
            ItemType: (uint)ItemType.Creature,
            MotionState: null,
            MotionTableId: 0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            ParentGuid: parentGuid,
            ParentLocation: 1u,
            PlacementId: 1u,
            Physics: physics);
    }

    private sealed class HostFixture : IDisposable
    {
        internal readonly RuntimeEntityObjectLifetime EntityObjects = new();
        internal readonly LiveEntityRuntime Runtime;
        internal readonly LiveEntityHydrationController Controller;
        internal readonly HostMaterializer Materializer;
        internal readonly AcDream.Runtime.Session.RuntimeFirstEntryDriveController
            FirstEntry;
        internal readonly AcDream.Runtime.Physics
            .RuntimePlacementProjectionSubscription Subscription;
        internal readonly AcDream.Runtime.Gameplay.RuntimeLocalPlayerMovementState
            Movement;
        internal readonly WorldGameState WorldState = new();
        internal readonly List<(LiveEntityRecord Record, bool Visible)>
            VisibilityEdges = [];
        internal int VisibilityFailuresRemaining;

        internal HostFixture(
            uint playerGuid,
            float terrainHeight = 0f,
            float moverSphereOriginZ = 0f)
        {
            EntityObjects.BindEventContext(
                static () => new AcDream.Runtime.RuntimeGenerationToken(1UL),
                static () => 1UL);
            EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
                Cell & 0xFFFF0000u, 1UL);
            EntityObjects.Physics.Engine.AddLandblock(
                Cell & 0xFFFF0000u,
                new TerrainSurface(
                    new byte[81],
                    Enumerable.Repeat(terrainHeight, 256).ToArray()),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                worldOffsetX: 0f,
                worldOffsetY: 0f);
            EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
                Cell & 0xFFFF0000u, 1UL, ready: true);

            EntityObjects.Physics.ObserveLocalWorldFrame(
                Cell,
                teleportAdvanced: false);
            Movement = new AcDream.Runtime.Gameplay
                .RuntimeLocalPlayerMovementState();
            var runtimeIdentity = new AcDream.Runtime.Gameplay
                .RuntimeLocalPlayerIdentityState();
            var publication = new AcDream.Runtime.Gameplay
                .RuntimeLocalPlayerPhysicsPublicationState(
                    EntityObjects.Entities,
                    EntityObjects.Physics,
                    Movement,
                    runtimeIdentity);
            Movement.AttachPhysicsPublication(publication);
            EntityObjects.LocalPlayerFirstEntry.BindPublication(publication);
            runtimeIdentity.ServerGuid = playerGuid;
            var spatial = new GpuWorldState();
            spatial.AddLandblock(new LoadedLandblock(
                (Cell & 0xFFFF0000u) | 0xFFFFu,
                new LandBlock(),
                Array.Empty<WorldEntity>()));
            Runtime = new LiveEntityRuntime(
                spatial,
                new NoopResources(),
                EntityObjects);
            FirstEntry = new AcDream.Runtime.Session
                .RuntimeFirstEntryDriveController(
                    EntityObjects,
                    new AcDream.Runtime.GameRuntimeClock(),
                    new SphereCollisionSource(moverSphereOriginZ),
                    static () => AcDream.Runtime.Gameplay
                        .PlayerMovementConstructionOptions.Fallback,
                    static _ => new AcDream.Runtime.Gameplay
                        .RuntimeLocalPlayerPhysicsActivationPreparation(
                            0.48f,
                            1.835f,
                            AcDream.Runtime.Gameplay
                                .RuntimeLocalPlayerShadowDisposition
                                .ProvenShapeless));
            var localShadowState = new LocalPlayerShadowState();
            var localShadowIdentity = new LocalPlayerIdentityState
            {
                ServerGuid = playerGuid,
            };
            var localShadowOrigin = new LiveWorldOriginState();
            localShadowOrigin.SetPlaceholder(0, 0);
            var localShadowSynchronizer = new LocalPlayerShadowSynchronizer(
                EntityObjects.Physics.Engine,
                Runtime,
                localShadowIdentity,
                localShadowOrigin,
                localShadowState);
            var sink = new RuntimePlacementPresentationSink(
                Runtime,
                new RuntimeWorldTransitState(),
                WorldState,
                new WorldEvents(),
                new EntityEffectPoseRegistry(),
                localShadowSynchronizer,
                () => playerGuid,
                _ => { },
                [
                    (record, visible) =>
                    {
                        VisibilityEdges.Add((record, visible));
                        if (VisibilityFailuresRemaining > 0)
                        {
                            VisibilityFailuresRemaining--;
                            throw new InvalidOperationException(
                                "fixture presentation attach failure");
                        }
                    },
                ]);
            Subscription = new AcDream.Runtime.Physics
                .RuntimePlacementProjectionSubscription(
                    EntityObjects.Placements,
                    static () => new AcDream.Runtime.RuntimeGenerationToken(1UL),
                    sink);
            Materializer = new HostMaterializer(Runtime);
            var identity = new LocalPlayerIdentityState
            {
                ServerGuid = playerGuid,
            };
            var dormant = new DormantLiveEntityStore();
            var teardown = new NoopTeardown();
            var deletion = new LiveEntityDeletionController(
                Runtime,
                EntityObjects,
                teardown,
                identity,
                dormant);
            Controller = new LiveEntityHydrationController(
                Runtime,
                EntityObjects,
                new object(),
                Materializer,
                new NoopRelationships(),
                new AcceptingReady(),
                new KnownOrigin(),
                new NoopNetworkSink(),
                new NoopTimestamps(),
                identity,
                deletion,
                dormant,
                firstEntry: FirstEntry);
        }

        public void Dispose()
        {
            try
            {
                Runtime.Clear();
            }
            catch
            {
                // Failure-path tests assert their own exceptions.
            }
        }
    }

    private sealed class HostMaterializer(LiveEntityRuntime runtime)
        : ILiveEntityProjectionMaterializer
    {
        internal Action<LiveEntityRecord>? AfterMaterialize { get; set; }

        public bool TryMaterialize(
            RuntimeEntityRecord expectedCanonical,
            WorldSession.EntitySpawn canonicalSpawn,
            LiveProjectionPurpose purpose,
            ulong expectedCreateIntegrationVersion,
            AcDream.App.Rendering.LiveEntityAppearanceUpdateState? appearanceUpdate = null)
        {
            if (canonicalSpawn.Position is not { } position
                || canonicalSpawn.SetupTableId is null)
            {
                return false;
            }

            WorldEntity? entity = runtime.MaterializeLiveEntity(
                expectedCanonical,
                position.LandblockId,
                id => new WorldEntity
                {
                    Id = id,
                    ServerGuid = canonicalSpawn.Guid,
                    SourceGfxObjOrSetupId = canonicalSpawn.SetupTableId.Value,
                    Position = new Vector3(
                        position.PositionX,
                        position.PositionY,
                        position.PositionZ),
                    Rotation = Quaternion.Identity,
                    MeshRefs = [],
                    ParentCellId = position.LandblockId,
                },
                LiveEntityProjectionKind.World,
                initializeProjection: null,
                out LiveEntityRecord? record,
                LiveEntityMaterializationResidence.AwaitRuntimePlacement);
            if (entity is null || record is null)
                return false;
            if (runtime.IsCurrentCreateIntegration(
                    expectedCanonical,
                    expectedCreateIntegrationVersion)
                && expectedCanonical.FullCellId != 0u
                && !runtime.HasActiveInitialCreateResidence(expectedCanonical)
                && !runtime.RebucketLiveEntity(
                    canonicalSpawn.Guid,
                    expectedCanonical.FullCellId))
            {
                return false;
            }
            AfterMaterialize?.Invoke(record);
            return runtime.IsCurrentRecord(record);
        }

        public void ResetSessionState()
        {
        }
    }

    private sealed class NoopResources : ILiveEntityResourceLifecycle
    {
        public void Register(WorldEntity entity)
        {
        }

        public void Unregister(WorldEntity entity)
        {
        }
    }

    private sealed class NoopTeardown : ILiveEntityTeardownCoordinator
    {
        public void TearDown(LiveEntityRecord record)
        {
        }

        public void ForgetUnknownOwner(uint serverGuid)
        {
        }
    }

    private sealed class NoopRelationships : ILiveEntityRelationshipProjection
    {
        public void OnSpawn(WorldSession.EntitySpawn spawn)
        {
        }

        public void OnParent(ParentEvent.Parsed update)
        {
        }

        public void OnCreateParentAccepted(CreateParentUpdate update)
        {
        }

        public ChildUnparentDisposition OnChildBecameUnparented(uint childGuid) =>
            ChildUnparentDisposition.Completed;

        public bool TryApplyAttachedAppearance(
            LiveEntityRecord record,
            ulong objDescAuthorityVersion) => false;
    }

    private sealed class AcceptingReady : ILiveEntityReadyPublisher
    {
        public bool Publish(LiveEntityReadyCandidate candidate) => true;
    }

    private sealed class KnownOrigin : ILiveEntityWorldOriginCoordinator
    {
        public bool IsKnown => true;

        public LiveEntityOriginInitialization TryInitialize(
            WorldSession.EntitySpawn spawn) => new(true, []);
    }

    private sealed class NoopNetworkSink : ILiveEntityNetworkUpdateSink
    {
        public void ApplySameGeneration(SameGenerationCreateObjectEvents events)
        {
        }
    }

    private sealed class NoopTimestamps : IAcceptedLocalPhysicsTimestampPublisher
    {
        public void Publish(uint serverGuid, AcceptedPhysicsTimestamps timestamps)
        {
        }
    }

    private sealed class SphereCollisionSource(float sphereOriginZ = 0f)
        : AcDream.Content.IPreparedCollisionSource
    {
        public AcDream.Content.PreparedAssetPresence ProbeCollision(
            AcDream.Content.Pak.PakAssetType type,
            uint sourceFileId) =>
            AcDream.Content.PreparedAssetPresence.Available;

        public AcDream.Content.PreparedCollisionReadResult<
            FlatSetupCollision> ReadSetupCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            AcDream.Content.PreparedCollisionReadResult<FlatSetupCollision>
                .Loaded(new FlatSetupCollision(
                    System.Collections.Immutable.ImmutableArray<
                        FlatCollisionCylinder>.Empty,
                    [new FlatCollisionSphere(
                        new Vector3(0f, 0f, sphereOriginZ),
                        0.48f)],
                    height: 0f,
                    radius: 0f,
                    stepUpHeight: 0.4f,
                    stepDownHeight: 0.4f));

        public AcDream.Content.PreparedCollisionReadResult<
            FlatGfxObjCollisionAsset> ReadGfxObjCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionReadResult<
            FlatCellStructureCollisionAsset> ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionReadResult<
            FlatEnvCellTopology> ReadEnvCellTopology(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionSourceStats CollisionStats =>
            default;

        public void Dispose()
        {
        }
    }
}
