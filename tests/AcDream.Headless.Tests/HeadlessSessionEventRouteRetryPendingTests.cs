using System.Net;
using System.Numerics;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Spells;
using AcDream.Headless.Hosting;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Tests;

public sealed class HeadlessSessionEventRouteRetryPendingTests
{
    private const uint PlayerGuid = 0x50000001u;
    private const uint Landblock = 0xC1000000u;
    private const uint Cell = Landblock | 0x0001u;
    private const float Height = 6f;

    [Fact]
    public void RetryPending_ReoffersAPreviouslyDeclinedHeadUntilTheSinkAccepts()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        Assert.NotEqual(0UL, runtime.Generation.Value);

        CommitLandblockCollision(runtime, Landblock);
        RuntimeEntityRecord record = CreateRemoteRecord(runtime, 0x70004001u);
        AttachBody(runtime, record, Cell);

        RuntimeEntityPlacementToken token = runtime.EntityObjects.Physics
            .SetPosition.TryBeginExclusiveAuthoredPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        Assert.True(token.IsValid);
        RuntimeSetPositionMoverPreparationStatus status = runtime.EntityObjects
            .Physics.SetPosition.TryPrepareAndSubmitAuthoredPlacement(
                record,
                token,
                RuntimeSetPositionOperationKind.RemoteAuthoritative,
                PhysicsSetPositionFlags.Teleport | PhysicsSetPositionFlags.Slide,
                new UnusedCollisionSource(),
                gameTime: 10d,
                out RuntimeSetPositionOutcome outcome,
                resolveWorldOffsetFromRuntimeFrame: true);
        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared, status);
        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);

        var sink = new DecliningThenAcceptingSink();
        var events = new NoOpEventRoute();
        var route = new HeadlessSessionEventRoute(events, runtime, sink);

        route.Attach();
        Assert.Equal(1, sink.CallCount);
        Assert.True(
            runtime.EntityObjects.Physics.SetPosition.TryPeekProjection(
                out _));

        sink.Accept = true;
        bool retried = route.RetryPending();

        Assert.True(retried);
        Assert.Equal(2, sink.CallCount);
        Assert.False(
            runtime.EntityObjects.Physics.SetPosition.TryPeekProjection(
                out _));

        route.Dispose();
    }

    [Fact]
    public void RetryPending_RefusesOnceRuntimeGenerationHasMovedPastAttach()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;

        CommitLandblockCollision(runtime, Landblock);
        RuntimeEntityRecord record = CreateRemoteRecord(runtime, 0x70004002u);
        AttachBody(runtime, record, Cell);

        RuntimeEntityPlacementToken token = runtime.EntityObjects.Physics
            .SetPosition.TryBeginExclusiveAuthoredPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        Assert.True(token.IsValid);
        RuntimeSetPositionMoverPreparationStatus status = runtime.EntityObjects
            .Physics.SetPosition.TryPrepareAndSubmitAuthoredPlacement(
                record,
                token,
                RuntimeSetPositionOperationKind.RemoteAuthoritative,
                PhysicsSetPositionFlags.Teleport | PhysicsSetPositionFlags.Slide,
                new UnusedCollisionSource(),
                gameTime: 10d,
                out RuntimeSetPositionOutcome outcome,
                resolveWorldOffsetFromRuntimeFrame: true);
        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared, status);
        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);

        var sink = new DecliningThenAcceptingSink();
        var events = new NoOpEventRoute();
        var route = new HeadlessSessionEventRoute(events, runtime, sink);
        route.Attach();
        Assert.Equal(1, sink.CallCount);

        sink.Accept = true;
        RuntimeGenerationToken attachedGeneration = runtime.Generation;
        RuntimeTeardownAcknowledgement stopped =
            started.Live.Stop(attachedGeneration);
        Assert.True(stopped.IsComplete);
        Assert.NotEqual(attachedGeneration, runtime.Generation);

        bool retried = route.RetryPending();

        Assert.False(retried);
        Assert.Equal(1, sink.CallCount);

        route.Dispose();
    }

    // ── Fixture (mirrors RuntimeAcceptedPositionDriveControllerTests) ──────

    private sealed class StartedRuntime : IDisposable
    {
        internal required GameRuntime Runtime { get; init; }
        internal required LiveSessionHost Live { get; init; }

        public void Dispose()
        {
            _ = Live.Stop(Runtime.Generation);
            Runtime.Dispose();
        }
    }

    private static StartedRuntime StartRuntime()
    {
        var operations = new FixtureGameplayOperations();
        var sessionOperations = new FixtureSessionOperations();
        var runtime = new GameRuntime(new GameRuntimeDependencies(
            operations, operations, operations, operations,
            SessionOperations: sessionOperations));
        var resetHost = new FixtureResetHost();
        var options = new LiveSessionConnectOptions(
            true, "127.0.0.1", 9000, "account", "password");
        var live = new LiveSessionHost(
            runtime.Session,
            new LiveSessionHostBindings(
                new LiveSessionRoutingFactories(
                    _ => new NoOpEventRoute(),
                    _ => new NoOpCommandRoute()),
                generation => runtime.ResetGeneration(generation, resetHost),
                new LiveSessionSelectionBindings(
                    id => runtime.PlayerIdentity.ServerGuid = id,
                    _ => { },
                    runtime.CommunicationOwner.Chat.SetLocalPlayerGuid,
                    _ => { },
                    _ => { },
                    runtime.ActionOwner.Combat.Clear),
                new LiveSessionEnteredWorldBindings(
                    _ => { }, () => { }, () => { }, _ => { }, () => { }),
                (_, _, _) => { },
                () => { },
                _ => { },
                _ => { }),
            options);
        LiveSessionStartResult startResult = live.Start(options);
        Assert.Equal(LiveSessionStartStatus.Connected, startResult.Status);
        Assert.NotEqual(0UL, runtime.Generation.Value);
        return new StartedRuntime { Runtime = runtime, Live = live };
    }

    private static void CommitLandblockCollision(
        GameRuntime runtime, uint landblockId)
    {
        var heights = new byte[81];
        Array.Fill(heights, (byte)Height);
        var heightTable = new float[256];
        for (int index = 0; index < heightTable.Length; index++)
            heightTable[index] = index;
        runtime.EntityObjects.Physics.ObserveLocalWorldFrame(
            landblockId | 0x0001u, teleportAdvanced: false);
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            landblockId, 1UL);
        runtime.EntityObjects.Physics.Engine.AddLandblock(
            landblockId,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            landblockId, 1UL, ready: true);
    }

    private static RuntimeEntityRecord CreateRemoteRecord(
        GameRuntime runtime, uint guid)
    {
        RuntimeEntityRecord record = runtime.EntityObjects.RegisterEntity(
            new WorldSession.EntitySpawn(
                Guid: guid,
                Position: new CreateObject.ServerPosition(
                    Cell, 10f, 10f, Height, 1f, 0f, 0f, 0f),
                SetupTableId: null,
                AnimPartChanges: Array.Empty<CreateObject.AnimPartChange>(),
                TextureChanges: Array.Empty<CreateObject.TextureChange>(),
                SubPalettes: Array.Empty<CreateObject.SubPaletteSwap>(),
                BasePaletteId: null,
                ObjScale: null,
                Name: "remote",
                ItemType: null,
                MotionState: null,
                MotionTableId: 0x09000001u))
            .Canonical!;
        runtime.EntityObjects.Entities.SetFinalPhysicsState(
            record, PhysicsStateFlags.Gravity);
        return record;
    }

    private static void AttachBody(
        GameRuntime runtime, RuntimeEntityRecord record, uint cellId)
    {
        runtime.EntityObjects.Entities.SetFullCell(
            record, cellId, (cellId & 0xFFFF0000u) | 0xFFFFu);
        var body = new PhysicsBody
        {
            Position = new Vector3(10f, 10f, Height),
            Orientation = Quaternion.Identity,
            LastUpdateTime = 1d,
            State = PhysicsStateFlags.Gravity,
            TransientState = TransientStateFlags.Active,
        };
        body.SnapToCell(cellId, body.Position, body.Position);
        runtime.EntityObjects.Entities.SetPhysicsBody(record, body);
        record.ObjectClock.Activate();
        runtime.EntityObjects.Physics.AcknowledgeSpatialProjection(
            record, spatial: true);
    }

    private sealed class DecliningThenAcceptingSink
        : IRuntimePlacementProjectionSink
    {
        internal int CallCount { get; private set; }
        internal bool Accept { get; set; }

        public bool TryApply(in RuntimePlacementProjectionSnapshot projection)
        {
            CallCount++;
            return Accept;
        }
    }

    private sealed class NoOpEventRoute : ILiveSessionEventRouting
    {
        public void Attach()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class NoOpCommandRoute : ILiveSessionCommandRouting
    {
        public void Activate()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FixtureSessionOperations : ILiveSessionOperations
    {
        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint) =>
            new(endpoint, new FixtureTransport());

        public void Connect(WorldSession session, string user, string password)
        {
        }

        public CharacterList.Parsed GetCharacters(WorldSession session) =>
            new(
                0u,
                [new CharacterList.Character(PlayerGuid, "Direct", 0u)],
                [],
                11,
                "account",
                true,
                true);

        public void EnterWorld(WorldSession session, int activeCharacterIndex)
        {
        }

        public void Tick(WorldSession session)
        {
        }

        public void DisposeSession(WorldSession session) => session.Dispose();
    }

    private sealed class FixtureTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram)
        {
        }

        public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram)
        {
        }

        public int Receive(
            Span<byte> destination, TimeSpan timeout, out IPEndPoint? from)
        {
            from = null;
            return -1;
        }

        public ValueTask<NetReceiveResult> ReceiveAsync(
            Memory<byte> destination, CancellationToken cancellationToken) =>
            ValueTask.FromException<NetReceiveResult>(
                new OperationCanceledException(cancellationToken));

        public void Dispose()
        {
        }
    }

    private sealed class FixtureResetHost : IRuntimeGenerationResetHost
    {
        public void RetireEntityProjection(RuntimeEntityRecord entity)
        {
        }

        public void DrainEntityProjectionBoundary()
        {
        }

        public void CompleteEntityProjectionRetirement()
        {
        }
    }

    private sealed class FixtureGameplayOperations
        : IRuntimeCombatAttackOperations,
          IRuntimeCombatTargetOperations,
          IRuntimeCombatModeOperations,
          IRuntimeSpellCastOperations
    {
        public bool CanStartAttack() => false;
        public void PrepareAttackRequest()
        {
        }

        public bool SendAttack(AttackHeight height, float power) => false;
        public void SendCancelAttack()
        {
        }

        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => false;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest()
        {
        }

        public void SendChangeCombatMode(CombatMode mode)
        {
        }

        public uint LocalPlayerId => 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;

        public bool IsTargetCompatible(
            uint targetId, SpellMetadata spell, bool showMessage) => false;

        public void StopCompletely()
        {
        }

        public void SendUntargeted(uint spellId)
        {
        }

        public void SendTargeted(uint targetId, uint spellId)
        {
        }

        public void DisplayMessage(string message)
        {
        }

        public void IncrementBusy()
        {
        }
    }

    private sealed class UnusedCollisionSource : IPreparedCollisionSource
    {
        public PreparedAssetPresence ProbeCollision(
            PakAssetType type, uint sourceFileId) =>
            PreparedAssetPresence.Available;

        public PreparedCollisionReadResult<FlatSetupCollision> ReadSetupCollision(
            uint sourceFileId, CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<FlatSetupCollision>.Missing;

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset> ReadGfxObjCollision(
            uint sourceFileId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public PreparedCollisionReadResult<FlatCellStructureCollisionAsset> ReadCellStructureCollision(
            uint sourceFileId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public PreparedCollisionReadResult<FlatEnvCellTopology> ReadEnvCellTopology(
            uint sourceFileId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public PreparedCollisionSourceStats CollisionStats => default;

        public void Dispose()
        {
        }
    }
}
