using System.Net;
using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Plugins;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;
using AcDream.Runtime.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.World;

public sealed class RuntimeForcePositionRenderCommitTests
{
    private const uint Cell = 0x01010001u;
    private const uint PlayerGuid = 0x7000B201u;
    private static readonly Vector3 ForcedPosition = new(30f, 30f, 0.48f);
    private const uint ForcedCell = 0x0101000Au;

    [Fact]
    public void AcceptedForcePosition_DrivenEndToEnd_MovesRenderEntityFromTheCommittedReceipt()
    {
        using var fixture = new HostFixture();
        fixture.Controller.OnCreate(Spawn(PlayerGuid, Cell));

        PlayerMovementController controller = Assert.IsType<PlayerMovementController>(
            fixture.Movement.Controller);
        Assert.True(fixture.Runtime.TryGetRecord(PlayerGuid, out LiveEntityRecord record));
        WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);
        Vector3 positionBeforeForce = entity.Position;

        WorldSession.EntityPositionUpdate wire = ForceUpdate(ForcedPosition);
        Assert.True(fixture.EntityObjects.TryApplyPosition(
            wire,
            isLocalPlayer: true,
            forcePositionRotation: controller.BodyOrientation,
            currentLocalVelocity: controller.BodyVelocity,
            acknowledgeProjection: null,
            out PositionTimestampDisposition disposition,
            out _,
            out AcceptedPhysicsTimestamps timestamps));
        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);

        RuntimeAcceptedPositionExecutionStatus status =
            fixture.Drive.TryExecuteAcceptedLocalPosition(
                record.Canonical,
                wire,
                disposition,
                timestamps,
                timestamps.PreviousTeleport);

        Assert.Equal(RuntimeAcceptedPositionExecutionStatus.Committed, status);
        Assert.NotEqual(positionBeforeForce, entity.Position);
        Assert.Equal(ForcedPosition, entity.Position);
        Assert.NotEqual(Cell, entity.ParentCellId);
        Assert.Equal(ForcedCell, entity.ParentCellId);
        Assert.True(record.IsSpatiallyProjected);
        Assert.True(record.IsSpatiallyVisible);
    }

    private static WorldSession.EntityPositionUpdate ForceUpdate(
        Vector3 position,
        ushort positionSequence = 2,
        ushort forcePositionSequence = 1) =>
        new(
            PlayerGuid,
            new CreateObject.ServerPosition(
                Cell,
                position.X,
                position.Y,
                position.Z,
                1f,
                0f,
                0f,
                0f),
            Velocity: null,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: positionSequence,
            TeleportSequence: 0,
            ForcePositionSequence: forcePositionSequence);

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
            "force-position fixture",
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

    private sealed class HostFixture : IDisposable
    {
        internal readonly RuntimeEntityObjectLifetime EntityObjects = new();
        internal readonly LiveEntityRuntime Runtime;
        internal readonly LiveEntityHydrationController Controller;
        internal readonly AcDream.Runtime.Session.RuntimeFirstEntryDriveController
            FirstEntry;
        internal readonly RuntimeAcceptedPositionDriveController Drive;
        internal readonly RuntimeLocalPlayerMovementState Movement;
        internal readonly WorldGameState WorldState = new();
        private readonly WorldSession _session;

        internal HostFixture()
        {
            EntityObjects.BindEventContext(
                static () => new RuntimeGenerationToken(1UL),
                static () => 1UL);
            EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
                Cell & 0xFFFF0000u, 1UL);
            EntityObjects.Physics.Engine.AddLandblock(
                Cell & 0xFFFF0000u,
                new TerrainSurface(new byte[81], new float[256]),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                worldOffsetX: 0f,
                worldOffsetY: 0f);
            EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
                Cell & 0xFFFF0000u, 1UL, ready: true);
            EntityObjects.Physics.ObserveLocalWorldFrame(
                Cell, teleportAdvanced: false);

            Movement = new RuntimeLocalPlayerMovementState();
            var runtimeIdentity = new RuntimeLocalPlayerIdentityState();
            var publication = new RuntimeLocalPlayerPhysicsPublicationState(
                EntityObjects.Entities,
                EntityObjects.Physics,
                Movement,
                runtimeIdentity);
            Movement.AttachPhysicsPublication(publication);
            EntityObjects.LocalPlayerFirstEntry.BindPublication(publication);
            runtimeIdentity.ServerGuid = PlayerGuid;

            var spatial = new GpuWorldState();
            spatial.AddLandblock(new LoadedLandblock(
                (Cell & 0xFFFF0000u) | 0xFFFFu,
                new LandBlock(),
                Array.Empty<WorldEntity>()));
            Runtime = new LiveEntityRuntime(
                spatial,
                new NoopResources(),
                EntityObjects);

            FirstEntry = new AcDream.Runtime.Session.RuntimeFirstEntryDriveController(
                EntityObjects,
                new GameRuntimeClock(),
                new UnusedCollisionSource(),
                static () => PlayerMovementConstructionOptions.Fallback,
                static _ => new RuntimeLocalPlayerPhysicsActivationPreparation(
                    0.48f,
                    1.835f,
                    RuntimeLocalPlayerShadowDisposition.ProvenShapeless));

            var localShadowState = new LocalPlayerShadowState();
            var localShadowIdentity = new LocalPlayerIdentityState
            {
                ServerGuid = PlayerGuid,
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
                () => PlayerGuid,
                _ => { },
                [(_, _) => { }]);
            _ = new AcDream.Runtime.Physics.RuntimePlacementProjectionSubscription(
                EntityObjects.Placements,
                static () => new RuntimeGenerationToken(1UL),
                sink);

            var materializer = new HostMaterializer(Runtime);
            var identity = new LocalPlayerIdentityState { ServerGuid = PlayerGuid };
            var dormant = new DormantLiveEntityStore();
            var deletion = new LiveEntityDeletionController(
                Runtime,
                EntityObjects,
                new NoopTeardown(),
                identity,
                dormant);
            Controller = new LiveEntityHydrationController(
                Runtime,
                EntityObjects,
                new object(),
                materializer,
                new NoopRelationships(),
                new AcceptingReady(),
                new KnownOrigin(),
                new NoopNetworkSink(),
                new NoopTimestamps(),
                identity,
                deletion,
                dormant,
                firstEntry: FirstEntry);

            _session = new WorldSession(
                new IPEndPoint(IPAddress.Loopback, 9000),
                new FixtureTransport())
            {
                GameActionCapture = _ => { },
            };
            Drive = new RuntimeAcceptedPositionDriveController(
                EntityObjects,
                new GameRuntimeClock(),
                new UnusedCollisionSource(),
                new LocalPlayerOutboundController((_, _, _, _, _, _) => { }),
                static () => new RuntimeGenerationToken(1UL),
                static () => PlayerGuid,
                () => Movement.Controller,
                static () => true,
                () => _session);
        }

        public void Dispose()
        {
            _session.Dispose();
            try
            {
                Runtime.Clear();
            }
            catch
            {
                // Failure-path assertions are made before Dispose runs.
            }
        }
    }

    private sealed class HostMaterializer(LiveEntityRuntime runtime)
        : ILiveEntityProjectionMaterializer
    {
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

    private sealed class UnusedCollisionSource : IPreparedCollisionSource
    {
        public PreparedAssetPresence ProbeCollision(
            PakAssetType type,
            uint sourceFileId) => PreparedAssetPresence.Available;

        public PreparedCollisionReadResult<FlatSetupCollision> ReadSetupCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<FlatSetupCollision>.Loaded(
                new FlatSetupCollision(
                    System.Collections.Immutable.ImmutableArray<
                        FlatCollisionCylinder>.Empty,
                    [new FlatCollisionSphere(Vector3.Zero, 0.48f)],
                    height: 0f,
                    radius: 0f,
                    stepUpHeight: 0.4f,
                    stepDownHeight: 0.4f));

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
            ReadGfxObjCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
            ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public PreparedCollisionReadResult<FlatEnvCellTopology> ReadEnvCellTopology(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public PreparedCollisionSourceStats CollisionStats => default;

        public void Dispose()
        {
        }
    }

    private sealed class FixtureTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram)
        {
        }

        public void Send(
            IPEndPoint remote,
            ReadOnlySpan<byte> datagram)
        {
        }

        public int Receive(
            Span<byte> destination,
            TimeSpan timeout,
            out IPEndPoint? from)
        {
            from = null;
            return -1;
        }

        public ValueTask<NetReceiveResult> ReceiveAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<NetReceiveResult>(
                new OperationCanceledException(cancellationToken));

        public void Dispose()
        {
        }
    }
}
