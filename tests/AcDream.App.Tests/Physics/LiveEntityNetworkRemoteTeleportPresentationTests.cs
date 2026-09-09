using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Net;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.Update;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib.IO;

namespace AcDream.App.Tests.Physics;

public sealed class LiveEntityNetworkRemoteTeleportPresentationTests
{
    private const uint SourceLandblock = 0xB1000000u;
    private const uint SourceCell = SourceLandblock | 0x0001u;
    private const uint DestinationLandblock = 0xB2000000u;
    private const uint DestinationCell = DestinationLandblock | 0x0001u;
    private static readonly Vector3 DestinationWorldOffset = new(192f, 0f, 0f);
    private const uint RemoteGuid = 0x70006001u;
    private const uint OtherPlayerGuid = 0x50006001u;
    private const float SpawnHeight = 7f;
    private const float FootSphereCenterLift = 0.48f;

    [Fact]
    public void TeleportCommit_RenderEntityMatchesResolvedBody_VisibleAndShadowSynced()
    {
        using var fixture = new Fixture();
        fixture.PublishDestinationCollision();
        fixture.ServiceWindow.Allow(DestinationLandblock);
        Vector3 spawnPose = fixture.Entity.Position;
        var destination = new Vector3(12f, 14f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.TeleportUpdate(
            destination, DestinationCell, teleportSequence: 5));

        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            RemoteGuid, out RuntimeEntityRecord canonical));
        Assert.NotNull(canonical.PhysicsBody);
        PhysicsBody body = canonical.PhysicsBody!;
        Vector3 resolved = destination + DestinationWorldOffset
            + new Vector3(0f, 0f, FootSphereCenterLift);
        Assert.Equal(resolved, body.Position);
        Assert.NotEqual(spawnPose, body.Position);

        // Invariant 2: the render entity advances from the RESOLVED body.
        Assert.Equal(body.Position, fixture.Entity.Position);
        Assert.Equal(body.Orientation, fixture.Entity.Rotation);
        Assert.Equal(DestinationCell, fixture.Entity.ParentCellId);
        Assert.Equal(DestinationCell, canonical.FullCellId);

        Assert.True(fixture.Runtime.TryGetRecord(
            RemoteGuid, out LiveEntityRecord liveRecord));
        Assert.True(liveRecord.IsSpatiallyVisible);

        AcDream.Core.Physics.ShadowEntry shadowEntry = Assert.Single(
            fixture.Shadows.AllEntriesForDebug(),
            entry => entry.EntityId == fixture.Entity.Id);
        Assert.Equal(body.Position, shadowEntry.Position);

        fixture.DrainPlacementFifo();
    }

    [Fact]
    public void TeleportRefused_RenderPoseTracksStoredDestination_EntityRemainsVisible()
    {
        using var fixture = new Fixture();
        fixture.PublishDestinationCollision();
        Vector3 spawnPose = fixture.Entity.Position;
        var destination = new Vector3(12f, 14f, SpawnHeight);
        EntityPhysicsHost host = fixture.ArmSticky(stickTargetGuid: 0x70009998u);

        fixture.Controller.OnPosition(fixture.TeleportUpdate(
            destination, DestinationCell, teleportSequence: 5));

        Assert.Equal(0u, host.PositionManager.GetStickyObjectId());

        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            RemoteGuid, out RuntimeEntityRecord canonical));
        PhysicsBody body = canonical.PhysicsBody!;
        Vector3 resolved = destination + DestinationWorldOffset;
        Assert.Equal(resolved, body.Position);
        Assert.NotEqual(spawnPose, body.Position);

        Assert.Equal(body.Position, fixture.Entity.Position);

        Assert.True(fixture.Runtime.TryGetRecord(
            RemoteGuid, out LiveEntityRecord liveRecord));
        Assert.True(liveRecord.IsSpatiallyVisible);

        AcDream.Core.Physics.ShadowEntry shadowEntry = Assert.Single(
            fixture.Shadows.AllEntriesForDebug(),
            entry => entry.EntityId == fixture.Entity.Id);
        Assert.Equal(body.Position, shadowEntry.Position);

        fixture.DrainPlacementFifo();
    }

    [Fact]
    public void StuckNpc_TeleportPacket_RunsTheHookAndPlacesDespiteStickySuppression()
    {
        using var fixture = new Fixture();
        fixture.PublishDestinationCollision();
        fixture.ServiceWindow.Allow(DestinationLandblock);
        EntityPhysicsHost host = fixture.ArmSticky(stickTargetGuid: 0x70009999u);
        var destination = new Vector3(12f, 14f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.TeleportUpdate(
            destination, DestinationCell, teleportSequence: 5));

        // The hook's UnStick action actually ran — proof the sticky lease
        // did not block dispatch, not merely that the placement happened to
        // succeed for some unrelated reason.
        Assert.Equal(0u, host.PositionManager.GetStickyObjectId());

        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            RemoteGuid, out RuntimeEntityRecord canonical));
        Assert.NotNull(canonical.PhysicsBody);
        PhysicsBody body = canonical.PhysicsBody!;
        Vector3 resolved = destination + DestinationWorldOffset
            + new Vector3(0f, 0f, FootSphereCenterLift);
        Assert.Equal(resolved, body.Position);
        Assert.Equal(DestinationCell, canonical.FullCellId);

        fixture.DrainPlacementFifo();
    }

    [Fact]
    public void AirborneOtherPlayer_TeleportPacket_PlacesThroughThePlayerArmWithoutTheLandingBlock()
    {
        using var fixture = new Fixture(OtherPlayerGuid);
        fixture.PublishDestinationCollision();
        fixture.ServiceWindow.Allow(DestinationLandblock);
        fixture.Remote.Airborne = true;
        fixture.Remote.Body.TransientState = TransientStateFlags.Active;
        var destination = new Vector3(12f, 14f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.TeleportUpdate(
            destination,
            DestinationCell,
            teleportSequence: 5,
            guid: OtherPlayerGuid,
            isGrounded: false));

        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            OtherPlayerGuid, out RuntimeEntityRecord canonical));
        Assert.NotNull(canonical.PhysicsBody);
        PhysicsBody body = canonical.PhysicsBody!;
        Vector3 resolved = destination + DestinationWorldOffset
            + new Vector3(0f, 0f, FootSphereCenterLift);
        Assert.Equal(resolved, body.Position);
        Assert.Equal(DestinationCell, canonical.FullCellId);
        // The airborne early return writes NOTHING and returns before the
        // spatial rebucket that follows a real dispatch — resident
        // visibility is proof OnPosition did not take that exit.
        Assert.True(fixture.Runtime.TryGetRecord(
            OtherPlayerGuid, out LiveEntityRecord liveRecord));
        Assert.True(liveRecord.IsSpatiallyVisible);

        fixture.DrainPlacementFifo();
    }

    [Fact]
    public void NpcAirborneSnap_LandingPacket_StillArmsTheLeash()
    {
        using var fixture = new Fixture();
        EntityPhysicsHost host = fixture.InstallHost();
        Assert.Null(host.PositionManager.Constraint);
        fixture.Remote.Airborne = true;
        fixture.Remote.Body.TransientState = TransientStateFlags.Active;
        var landingPos = new Vector3(12f, 14f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.TeleportUpdate(
            landingPos, SourceCell, teleportSequence: 1, isGrounded: true));

        Assert.NotNull(host.PositionManager.Constraint);
    }

    [Fact]
    public void NpcTeleport_DoesNotInstallASynthesizedVelocity()
    {
        using var fixture = new Fixture();
        fixture.PublishDestinationCollision();
        fixture.ServiceWindow.Allow(DestinationLandblock);
        fixture.Remote.LastServerPos = fixture.Entity.Position;
        fixture.Remote.LastServerPosTime =
            (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds - 0.15;
        var destination = new Vector3(12f, 14f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.TeleportUpdate(
            destination, DestinationCell, teleportSequence: 5));

        Assert.False(fixture.Remote.HasServerVelocity);
        Assert.Equal(Vector3.Zero, fixture.Remote.ServerVelocity);

        fixture.DrainPlacementFifo();
    }

    [Fact]
    public void NullClassifiedNpc_WireAirbornePacket_WritesOnlyBookkeepingNoBodyOrShadow()
    {
        using var fixture = new Fixture(nullClassification: true);
        EntityPhysicsHost host = fixture.InstallHost();
        Assert.Null(host.PositionManager.Constraint);
        Vector3 spawnBodyPose = fixture.Remote.Body.Position;
        var wirePos = new Vector3(50f, 50f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.TeleportUpdate(
            wirePos,
            SourceCell,
            teleportSequence: 1,
            isGrounded: false));

        Assert.Equal(spawnBodyPose, fixture.Remote.Body.Position);

        // No shadow republish — still exactly the one spawn-time entry, at
        // the spawn pose, not wirePos.
        AcDream.Core.Physics.ShadowEntry shadowEntry = Assert.Single(
            fixture.Shadows.AllEntriesForDebug(),
            entry => entry.EntityId == fixture.Entity.Id);
        Assert.Equal(spawnBodyPose, shadowEntry.Position);

        Assert.Equal(SourceCell, fixture.Remote.CellId);
        Assert.Equal(wirePos, fixture.Remote.LastServerPos);
        Assert.NotEqual(0d, fixture.Remote.LastServerPosTime);

        Assert.Null(host.PositionManager.Constraint);
    }

    private sealed class Fixture : IDisposable
    {
        internal RuntimeEntityObjectLifetime Lifetime { get; }
        internal LiveEntityRuntime Runtime { get; }
        internal LiveEntityNetworkUpdateController Controller { get; }
        internal RemoteServiceWindow ServiceWindow { get; } = new();
        internal ShadowObjectRegistry Shadows { get; }
        internal WorldEntity Entity { get; }
        internal RemoteMotion Remote { get; private set; } = null!;
        private readonly GpuWorldState _spatial;
        private readonly uint _guid;
        private readonly bool _nullClassification;

        internal Fixture(uint guid = RemoteGuid, bool nullClassification = false)
        {
            _guid = guid;
            _nullClassification = nullClassification;
            var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
            engine.AddLandblock(
                SourceLandblock,
                new TerrainSurface(new byte[81], new float[256]),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                worldOffsetX: 0f,
                worldOffsetY: 0f);
            Lifetime = new RuntimeEntityObjectLifetime(engine);
            Lifetime.BindEventContext(
                static () => new RuntimeGenerationToken(1UL),
                static () => 1UL);
            Shadows = engine.ShadowObjects;

            var spatial = new GpuWorldState();
            spatial.AddLandblock(new LoadedLandblock(
                CanonicalLandblock(SourceLandblock),
                new DatReaderWriter.DBObjs.LandBlock(),
                Array.Empty<WorldEntity>()));
            _spatial = spatial;
            Runtime = new LiveEntityRuntime(
                spatial,
                new NoopResources(),
                NullLiveEntityRuntimeComponentLifecycle.Instance,
                Lifetime);

            var wirePosition = new CreateObject.ServerPosition(
                SourceCell, 10f, 10f, SpawnHeight, 1f, 0f, 0f, 0f);
            var timestamps = new PhysicsTimestamps(
                Position: 1,
                Movement: 1,
                State: 1,
                Vector: 1,
                Teleport: 1,
                ServerControlledMove: 1,
                ForcePosition: 1,
                ObjDesc: 1,
                Instance: 1);
            var physics = new PhysicsSpawnData(
                RawState: (uint)PhysicsStateFlags.ReportCollisions,
                Position: wirePosition,
                Movement: null,
                AnimationFrame: null,
                SetupTableId: 0x02000001u,
                MotionTableId: 0x09000001u,
                SoundTableId: null,
                PhysicsScriptTableId: null,
                Parent: null,
                Children: null,
                Scale: 1f,
                Friction: null,
                Elasticity: null,
                Translucency: null,
                Velocity: null,
                Acceleration: null,
                AngularVelocity: null,
                DefaultScriptType: null,
                DefaultScriptIntensity: null,
                Timestamps: timestamps);
            var spawn = new WorldSession.EntitySpawn(
                _guid,
                wirePosition,
                0x02000001u,
                Array.Empty<CreateObject.AnimPartChange>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.SubPaletteSwap>(),
                null,
                null,
                "remote-teleport-fixture",
                null,
                null,
                0x09000001u,
                PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
                InstanceSequence: 1,
                PositionSequence: 1,
                MovementSequence: 1,
                ServerControlSequence: 1,
                Physics: physics);
            LiveEntityRecord record =
                Runtime.RegisterAndMaterializeProjection(spawn);
            Entity = record.WorldEntity
                ?? throw new InvalidOperationException(
                    "fixture failed to materialize the remote entity");
            Assert.True(Runtime.RebucketLiveEntity(_guid, SourceCell));

            var remote = new RemoteMotion();
            remote.Body.SnapToCell(SourceCell, Entity.Position, Entity.Position);
            remote.CellId = SourceCell;
            Runtime.SetRemoteMotionRuntime(_guid, remote);
            Remote = remote;
            Shadows.Register(
                Entity.Id,
                0x02000001u,
                Entity.Position,
                Entity.Rotation,
                radius: 0.48f,
                worldOffsetX: 0f,
                worldOffsetY: 0f,
                landblockId: SourceLandblock,
                collisionType: ShadowCollisionType.Cylinder,
                cylHeight: 1.835f,
                seedCellId: SourceCell,
                isStatic: false);

            var origin = new LiveWorldOriginState();
            origin.SetPlaceholder(
                (int)((SourceLandblock >> 24) & 0xFFu),
                (int)((SourceLandblock >> 16) & 0xFFu));

            var animatedEntities =
                new LiveEntityAnimationRuntimeView<LiveEntityAnimationState>(
                    new LiveEntityRuntimeSlot());
            var remotePlacementDrive = new RuntimeRemotePlacementDriveController(
                Lifetime,
                new GameRuntimeClock(),
                new NoopCollisionSource(),
                ServiceWindow);
            var acceptedPositionDrive = new RuntimeAcceptedPositionDriveController(
                Lifetime,
                new GameRuntimeClock(),
                new NoopCollisionSource(),
                new LocalPlayerOutboundController(static (_, _, _, _, _, _) => { }),
                static () => new RuntimeGenerationToken(1UL),
                static () => 0x50000099u,
                static () => null,
                static () => false,
                static () => null);
            var identity = new NoopIdentitySource();
            var deletion = new LiveEntityDeletionController(
                Runtime,
                Lifetime,
                new NoopTeardownCoordinator(),
                identity);
            var hydration = new LiveEntityHydrationController(
                Runtime,
                Lifetime,
                new object(),
                new NoopMaterializer(),
                new NoopRelationships(),
                new NoopReadyPublisher(),
                new AlwaysKnownOrigin(),
                new NoopNetworkSink(),
                new NoopTimestampPublisher(),
                identity,
                deletion);
            var entityEffects = new EntityEffectController(
                Runtime,
                new AcDream.Core.Vfx.PhysicsScriptRunner(
                    static _ => null,
                    new AcDream.Core.Physics.AnimationHookRouter(),
                    randomUnit: static () => 0.5),
                new AcDream.Core.Vfx.PhysicsScriptTableResolver(static _ => null),
                new EntityEffectPoseRegistry());

            Controller = new LiveEntityNetworkUpdateController(
                Runtime,
                Lifetime.Objects,
                hydration,
                entityEffects,
                new LiveEntityPresentationController(
                    Runtime,
                    Shadows,
                    (_, _, _) => true,
                    new LiveEntityPartArrayEnterWorldPort(_ => { })),
                new LiveEntityLightController(
                    Runtime,
                    new EntityEffectPoseRegistry(),
                    new AcDream.Core.Lighting.LightingHookSink(
                        new AcDream.Core.Lighting.LightManager(),
                        new EntityEffectPoseRegistry()),
                    static _ => null),
                new EquippedChildRenderController(
                    new NoopDatReaderWriter(),
                    new object(),
                    Lifetime.Objects,
                    Runtime,
                    new EntityEffectPoseRegistry(),
                    static _ => false,
                    static (_, _, _) =>
                        new ExactProjectionWithdrawalOutcome(
                            ExactProjectionWithdrawalDisposition.Superseded,
                            null),
                    Shadows,
                    new PhysicsDataCache(),
                    static (_, _) => { }),
                new ProjectileController(Runtime),
                animatedEntities,
                new RemoteMovementObservationTracker(),
                new RemotePhysicsUpdater(
                    Lifetime.Physics,
                    static (_, _) => (0.48f, 1.835f),
                    static (_, _) => (
                        System.Collections.Immutable
                            .ImmutableArray<FlatCollisionSphere>.Empty,
                        1f, 0.4f, 0.4f),
                    static (_, _, _, _) => { }),
                new RemoteInboundMotionDispatcher(
                    static (_, _, _) => false,
                    static (_, _) => { }),
                new LiveEntityMotionRuntimeController(
                    Runtime,
                    new PhysicsDataCache(),
                    static () => null,
                    new AcDream.Core.Selection.SelectionState(),
                    origin),
                engine,
                new NoopDatReaderWriter(),
                new NoopAnimationLoader(),
                combatTargetController: null,
                origin,
                new NoopTeleportSink(),
                _nullClassification
                    ? new NoopLocalPlayerControllerSource()
                    : new StubLocalPlayerControllerSource(),
                new LocalPlayerOutboundController(static (_, _, _, _, _, _) => { }),
                new NoopPhysicsHostSource(),
                identity,
                new FixedScriptTime(),
                new NoopSessionSource(),
                publishTimestamps: static (_, _) => { },
                new NoopMovementTruthSink(),
                acceptedPositionDrive,
                remotePlacementDrive,
                worldDropProjection: null);
        }

        internal void PublishDestinationCollision()
        {
            var heights = new byte[81];
            Array.Fill(heights, (byte)SpawnHeight);
            var heightTable = new float[256];
            for (int index = 0; index < heightTable.Length; index++)
                heightTable[index] = index;
            Lifetime.Physics.ObserveLocalWorldFrame(
                SourceCell, teleportAdvanced: false);
            Lifetime.Physics.SetPosition.BeginCollisionGeneration(
                DestinationLandblock, 1UL);
            Lifetime.Physics.Engine.AddLandblock(
                DestinationLandblock,
                new TerrainSurface(heights, heightTable),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                worldOffsetX: DestinationWorldOffset.X,
                worldOffsetY: DestinationWorldOffset.Y);
            Lifetime.Physics.SetPosition.CommitCollisionGeneration(
                DestinationLandblock, 1UL, ready: true);

            uint destinationCanonical = CanonicalLandblock(DestinationLandblock);
            if (!_spatial.IsLoaded(destinationCanonical))
            {
                _spatial.AddLandblock(new LoadedLandblock(
                    destinationCanonical,
                    new DatReaderWriter.DBObjs.LandBlock(),
                    Array.Empty<WorldEntity>()));
            }
        }

        private static uint CanonicalLandblock(uint landblockId) =>
            (landblockId & 0xFFFF0000u) | 0xFFFFu;

        internal WorldSession.EntityPositionUpdate TeleportUpdate(
            Vector3 destination,
            uint cellId,
            ushort teleportSequence,
            uint guid = RemoteGuid,
            bool isGrounded = true) => new(
            guid,
            new CreateObject.ServerPosition(
                cellId,
                destination.X,
                destination.Y,
                destination.Z,
                1f, 0f, 0f, 0f),
            Velocity: null,
            PlacementId: null,
            IsGrounded: isGrounded,
            InstanceSequence: 1,
            PositionSequence: 2,
            TeleportSequence: teleportSequence,
            ForcePositionSequence: 0);

        internal EntityPhysicsHost ArmSticky(uint stickTargetGuid)
        {
            EntityPhysicsHost host = InstallHost();
            host.PositionManager.StickTo(stickTargetGuid, radius: 1f, height: 1f);
            Assert.NotEqual(0u, host.PositionManager.GetStickyObjectId());
            return host;
        }

        internal EntityPhysicsHost InstallHost()
        {
            Assert.True(Runtime.TryGetRecord(
                _guid, out LiveEntityRecord liveRecord));
            var host = new EntityPhysicsHost(
                _guid,
                getPosition: () => new AcDream.Core.Physics.Position(
                    Remote.CellId, Remote.Body.Position, Remote.Body.Orientation),
                getVelocity: () => Remote.Body.Velocity,
                getRadius: () => 0.48f,
                inContact: () => Remote.Body.InContact,
                minterpMaxSpeed: () => null,
                curTime: () => 0d,
                physicsTimerTime: () => 0d,
                getObjectA: _ => null,
                handleUpdateTarget: _ => { },
                interruptCurrentMovement: () => { });
            Runtime.InstallPhysicsHost(liveRecord, host);
            Remote.MarkFullPhysicsHostBound();
            return host;
        }

        internal void DrainPlacementFifo()
        {
            while (Lifetime.Physics.SetPosition.TryPeekProjection(
                    out RuntimePlacementProjectionSnapshot head))
            {
                if (!Lifetime.Physics.SetPosition.AcknowledgeProjection(head.Token))
                    break;
            }
        }

        public void Dispose() => Lifetime.Dispose();

        internal sealed class RemoteServiceWindow : IRuntimeRemotePlacementServiceWindow
        {
            private readonly HashSet<uint> _within = [];

            internal void Allow(uint landblockId) =>
                _within.Add((landblockId & 0xFFFF0000u) | 0xFFFFu);

            public bool IsWithinServiceWindow(uint landblockId) =>
                _within.Contains((landblockId & 0xFFFF0000u) | 0xFFFFu);
        }

        private sealed class NoopResources : ILiveEntityResourceLifecycle
        {
            public void Register(WorldEntity entity) { }
            public void Unregister(WorldEntity entity) { }
        }

        private sealed class NoopCollisionSource : IPreparedCollisionSource
        {
            public PreparedAssetPresence ProbeCollision(
                PakAssetType type, uint sourceFileId) =>
                PreparedAssetPresence.Available;

            public PreparedCollisionReadResult<FlatSetupCollision>
                ReadSetupCollision(
                    uint sourceFileId,
                    CancellationToken cancellationToken = default) =>
                PreparedCollisionReadResult<FlatSetupCollision>.Loaded(
                    new FlatSetupCollision(
                        System.Collections.Immutable
                            .ImmutableArray<FlatCollisionCylinder>.Empty,
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

            public PreparedCollisionReadResult<FlatEnvCellTopology>
                ReadEnvCellTopology(
                    uint sourceFileId,
                    CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public PreparedCollisionSourceStats CollisionStats => default;

            public void Dispose() { }
        }

        private sealed class NoopIdentitySource : ILocalPlayerIdentitySource
        {
            public uint ServerGuid => 0x50000099u;
        }

        private sealed class NoopTeardownCoordinator
            : ILiveEntityTeardownCoordinator
        {
            public void TearDown(LiveEntityRecord record) { }
            public void ForgetUnknownOwner(uint serverGuid) { }
        }

        private sealed class NoopMaterializer : ILiveEntityProjectionMaterializer
        {
            public bool TryMaterialize(
                RuntimeEntityRecord expectedCanonical,
                WorldSession.EntitySpawn canonicalSpawn,
                LiveProjectionPurpose purpose,
                ulong expectedCreateIntegrationVersion,
                AcDream.App.Rendering.LiveEntityAppearanceUpdateState?
                    appearanceUpdate = null) =>
                throw new InvalidOperationException(
                    "The fixture pre-materializes the remote entity; " +
                    "TryMaterialize should never be reached for an " +
                    "already-projected accepted Position.");

            public void ResetSessionState() { }
        }

        private sealed class NoopRelationships : ILiveEntityRelationshipProjection
        {
            public void OnSpawn(WorldSession.EntitySpawn spawn) { }
            public void OnParent(ParentEvent.Parsed update) { }
            public void OnCreateParentAccepted(CreateParentUpdate update) { }

            public AcDream.App.Rendering.ChildUnparentDisposition
                OnChildBecameUnparented(uint childGuid) =>
                AcDream.App.Rendering.ChildUnparentDisposition.NotAttached;

            public bool TryApplyAttachedAppearance(
                LiveEntityRecord record, ulong objDescAuthorityVersion) => false;
        }

        private sealed class NoopReadyPublisher : ILiveEntityReadyPublisher
        {
            public bool Publish(LiveEntityReadyCandidate candidate) => true;
        }

        private sealed class AlwaysKnownOrigin : ILiveEntityWorldOriginCoordinator
        {
            public bool IsKnown => true;

            public LiveEntityOriginInitialization TryInitialize(
                WorldSession.EntitySpawn spawn) => new(true, []);
        }

        private sealed class NoopNetworkSink : ILiveEntityNetworkUpdateSink
        {
            public void ApplySameGeneration(SameGenerationCreateObjectEvents events) { }
        }

        private sealed class NoopTimestampPublisher
            : IAcceptedLocalPhysicsTimestampPublisher
        {
            public void Publish(uint serverGuid, AcceptedPhysicsTimestamps timestamps) { }
        }

        private sealed class NoopDatReaderWriter : IDatReaderWriter
        {
            private readonly StubDatabase _portal = new();
            private readonly StubDatabase _highRes = new();
            private readonly StubDatabase _language = new();
            private readonly StubDatabase _cell = new();

            public string SourceDirectory => string.Empty;
            public IDatDatabase Portal => _portal;
            public IDatDatabase Cell => _cell;
            public ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; } =
                new(new Dictionary<uint, IDatDatabase>());
            public IDatDatabase HighRes => _highRes;
            public IDatDatabase Language => _language;
            public IDatDatabase Local => _language;
            public ReadOnlyDictionary<uint, uint> RegionFileMap { get; } =
                new(new Dictionary<uint, uint>());
            public int PortalIteration => 0;
            public int CellIteration => 0;
            public int HighResIteration => 0;
            public int LanguageIteration => 0;

            public bool TryGetFileBytes(
                uint regionId,
                uint fileId,
                ref byte[] bytes,
                out int bytesRead)
            {
                bytesRead = 0;
                return false;
            }

            public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
                Array.Empty<uint>();

            public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) =>
                Array.Empty<IDatReaderWriter.IdResolution>();

            public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
                throw new NotSupportedException();

            public bool TrySave<T>(
                uint regionId,
                T obj,
                int iteration = 0) where T : IDBObj =>
                throw new NotSupportedException();

            [return: MaybeNull]
            public T Get<T>(uint fileId) where T : IDBObj => default;

            public bool TryGet<T>(
                uint fileId,
                [MaybeNullWhen(false)] out T value) where T : IDBObj
            {
                value = default;
                return false;
            }

            public void Dispose() { }
        }

        private sealed class StubDatabase : IDatDatabase
        {
            public DatDatabase Db => throw new NotSupportedException();
            public int Iteration => 0;

            public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
                Array.Empty<uint>();

            public bool TryGet<T>(
                uint fileId,
                [MaybeNullWhen(false)] out T value) where T : IDBObj
            {
                value = default;
                return false;
            }

            public bool TryGetFileBytes(
                uint fileId,
                [MaybeNullWhen(false)] out byte[] value)
            {
                value = null;
                return false;
            }

            public bool TryGetFileBytes(
                uint fileId,
                ref byte[] bytes,
                out int bytesRead)
            {
                bytesRead = 0;
                return false;
            }

            public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
                throw new NotSupportedException();

            public void Dispose() { }
        }

        private sealed class NoopAnimationLoader : IAnimationLoader
        {
            public Animation? LoadAnimation(uint id) => null;
        }

        private sealed class NoopTeleportSink : ILocalPlayerTeleportNetworkSink
        {
            public void OnTeleportStarted(uint sequence) { }

            public void OfferDestination(
                RuntimeTeleportDestination destination,
                bool teleportTimestampAdvanced)
            { }

            public void OnLocalPlayerFirstEntryCompleted() { }

            public void ArmLoginTunnel() { }

            public void RequestLogout() { }

            public void ResetSession() { }

            public void ResetGenerationPresentation() { }
        }

        private sealed class StubLocalPlayerControllerSource
            : IRuntimeLocalPlayerControllerSource
        {
            public PlayerMovementController? Controller { get; } =
                new PlayerMovementController(new PhysicsEngine());
        }

        private sealed class NoopLocalPlayerControllerSource
            : IRuntimeLocalPlayerControllerSource
        {
            public PlayerMovementController? Controller => null;
        }

        private sealed class NoopPhysicsHostSource : ILocalPlayerPhysicsHostSource
        {
            public EntityPhysicsHost? Host => null;
        }

        private sealed class FixedScriptTime : IPhysicsScriptTimeSource
        {
            public double CurrentScriptTime => 1_700_000_000d;
        }

        private sealed class NoopSessionSource : ILiveWorldSessionSource
        {
            public WorldSession? CurrentSession => null;
        }

        private sealed class NoopMovementTruthSink : IMovementTruthDiagnosticSink
        {
            public void OnOutbound(
                string kind,
                uint sequence,
                MovementResult result,
                Vector3 wirePosition,
                uint wireCellId,
                byte contactByte)
            { }

            public void OnServerEcho(
                WorldSession.EntityPositionUpdate update,
                Vector3 serverWorldPosition)
            { }

            public void ResetSession() { }
        }
    }
}
