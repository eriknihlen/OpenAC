using System.Globalization;
using System.Net;
using System.Numerics;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Spells;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;
using AcDream.Runtime.World;

namespace AcDream.Runtime.Tests.Support;

internal sealed class NoWindowGameRuntimeHost : IDisposable
{
    private readonly FixtureOperations _operations;
    private readonly GameplayOperations _gameplay;
    private readonly ImmediateResetHost _resetHost = new();
    private readonly IDisposable _hostLease;
    private readonly IDisposable _traceSubscription;
    private EventRoute? _events;
    private CommandRoute? _commands;
    private bool _disposed;

    public NoWindowGameRuntimeHost(
        string user = "runtime-user",
        string password = "runtime-password",
        uint characterId = 0x50000001u,
        string characterName = "Runtime")
    {
        _operations = new FixtureOperations(
            characterId,
            characterName);
        _gameplay = new GameplayOperations();
        Runtime = new GameRuntime(new GameRuntimeDependencies(
            _gameplay,
            _gameplay,
            _gameplay,
            _gameplay,
            SessionOperations: _operations,
            CombatTime: () => _gameplay.Now));
        _gameplay.Bind(Runtime);
        _hostLease = Runtime.AcquireHostLease("no-window test host");
        Trace = new RuntimeTraceRecorder();
        _traceSubscription = Runtime.Subscribe(Trace);
        Session = new LiveSessionHost(
            Runtime.Session,
            new LiveSessionHostBindings(
                new LiveSessionRoutingFactories(
                    CreateEventRoute,
                    CreateCommandRoute),
                (generation) =>
                    Runtime.ResetGeneration(generation, _resetHost),
                new LiveSessionSelectionBindings(
                    id => Runtime.PlayerIdentity.ServerGuid = id,
                    _ => { },
                    Runtime.CommunicationOwner.Chat.SetLocalPlayerGuid,
                    _ => { },
                    _ => { },
                    Runtime.ActionOwner.Combat.Clear),
                new LiveSessionEnteredWorldBindings(
                    _ => { },
                    () => { },
                    () => { },
                    _ => { },
                    () => { }),
                (host, port, connectingUser) =>
                    _operations.RecordConnecting(
                        host,
                        port,
                        connectingUser),
                _operations.RecordConnected,
                _operations.RecordRoster,
                _operations.RecordCharacterEntered),
            new LiveSessionConnectOptions(
                true,
                "127.0.0.1",
                9000,
                user,
                password));
    }

    public GameRuntime Runtime { get; }
    public LiveSessionHost Session { get; }
    public RuntimeTraceRecorder Trace { get; }
    public IReadOnlyList<string> LifecycleTrace => _operations.Trace;
    public IReadOnlyList<string> GameplayTrace => _gameplay.Trace;
    public int ProjectionRetirementCount =>
        _resetHost.ProjectionRetirementCount;
    public int ProjectionDrainCount => _resetHost.ProjectionDrainCount;
    public int ProjectionCompletionCount =>
        _resetHost.ProjectionCompletionCount;

    public RuntimeStateCheckpoint CaptureCheckpoint()
    {
        RuntimeStateCheckpoint checkpoint =
            Runtime.CaptureCheckpoint();
        Trace.AddCheckpoint(
            Runtime.EntityObjects.Events.NextStamp(),
            checkpoint);
        return checkpoint;
    }

    public RuntimeSessionStartResult Start()
    {
        RuntimeLifecycleState previous = Runtime.Lifecycle.State;
        RuntimeSessionStartResult result =
            Session.Start(Runtime.Generation);
        Runtime.EventSink.EmitLifecycle(
            previous,
            Runtime.Lifecycle.State);
        return result;
    }

    public RuntimeSessionStartResult Reconnect()
    {
        RuntimeLifecycleState previous = Runtime.Lifecycle.State;
        RuntimeSessionStartResult result =
            Session.Reconnect(Runtime.Generation);
        Runtime.EventSink.EmitLifecycle(
            previous,
            Runtime.Lifecycle.State);
        return result;
    }

    public RuntimeTeardownAcknowledgement Stop()
    {
        RuntimeLifecycleState previous = Runtime.Lifecycle.State;
        RuntimeTeardownAcknowledgement result =
            Session.Stop(Runtime.Generation);
        Runtime.EventSink.EmitLifecycle(
            previous,
            Runtime.Lifecycle.State);
        return result;
    }

    public bool Deliver(Action<GameRuntime> delivery) =>
        Deliver(Runtime.Generation, delivery);

    public bool Deliver(
        RuntimeGenerationToken expectedGeneration,
        Action<GameRuntime> delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        EventRoute? route = _events;
        if (route is null
            || !route.IsActive
            || route.Generation != expectedGeneration
            || expectedGeneration != Runtime.Generation)
        {
            return false;
        }
        delivery(Runtime);
        return true;
    }

    public bool Execute(Action<GameRuntime> command) =>
        Execute(Runtime.Generation, command);

    public bool Execute(
        RuntimeGenerationToken expectedGeneration,
        Action<GameRuntime> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        CommandRoute? route = _commands;
        if (route is null
            || !route.IsActive
            || route.Generation != expectedGeneration
            || expectedGeneration != Runtime.Generation)
        {
            return false;
        }
        command(Runtime);
        return true;
    }

    public bool Move()
    {
        return Execute(runtime =>
        {
            Require(
                runtime.MovementOwner.Execute(
                    RuntimeMovementCommand.ToggleRunLock),
                "movement command was rejected");
            _gameplay.Trace.Add("movement:run");
            runtime.EventSink.EmitMovement(
                runtime.MovementOwner.Snapshot);
            runtime.EventSink.EmitCommand(
                RuntimeCommandDomain.Movement,
                (int)RuntimeMovementCommand.ToggleRunLock,
                RuntimeCommandStatus.Accepted);
        });
    }

    public bool Use(uint serverGuid)
    {
        return Execute(runtime =>
        {
            ItemUseRequestReservation reservation =
                runtime.ActionOwner.Transactions
                    .BeginUseRequestReservation();
            RuntimeInteractionDispatchResult result =
                runtime.ActionOwner.Transactions.TryDispatchUse(
                    serverGuid,
                    ownedByPlayer: false,
                    useable: true,
                    reservation,
                    _gameplay,
                    out _);
            Require(
                result == RuntimeInteractionDispatchResult.Dispatched,
                $"use command was rejected: {result}");
            runtime.ActionOwner.Transactions.CompleteUse(0u);
            runtime.EventSink.EmitCommand(
                RuntimeCommandDomain.Selection,
                (int)RuntimeSelectionCommand.UseSelected,
                RuntimeCommandStatus.Accepted,
                serverGuid);
        });
    }

    public bool Attack()
    {
        return Execute(runtime =>
        {
            runtime.ActionOwner.Combat.SetCombatMode(CombatMode.Melee);
            runtime.ActionOwner.CombatAttack.SetDesiredPower(0.5f);
            runtime.ActionOwner.CombatAttack.PressAttack(
                AttackHeight.Medium);
            _gameplay.Now += 0.5d;
            runtime.ActionOwner.CombatAttack.ReleaseAttack();
            runtime.EventSink.EmitCommand(
                RuntimeCommandDomain.Combat,
                (int)RuntimeCombatCommand.ToggleMode,
                RuntimeCommandStatus.Accepted);
        });
    }

    public bool CastFixtureSpell()
    {
        return Execute(runtime =>
        {
            runtime.CharacterOwner.InstallSpellMetadata(
                FixtureSpellMetadata());
            runtime.CharacterOwner.Spellbook.OnSpellLearned(1u);
            CastRequestResult result =
                runtime.ActionOwner.SpellCast.Cast(1u);
            Require(
                result == CastRequestResult.Sent,
                $"spell command was rejected: {result}");
            runtime.EventSink.EmitCommand(
                RuntimeCommandDomain.Magic,
                operation: 1,
                RuntimeCommandStatus.Accepted,
                primaryObjectId: 1u);
        });
    }

    public bool AdvanceProjectile(
        uint serverGuid = 0x70000021u,
        ushort incarnation = 1)
    {
        return Execute(runtime =>
        {
            RuntimeEntityRecord record =
                runtime.EntityObjects.RegisterEntity(
                    ProjectileSpawn(serverGuid, incarnation)).Canonical
                ?? throw new InvalidOperationException(
                    "projectile registration produced no canonical record");
            PhysicsBody body =
                runtime.EntityObjects.Physics.GetOrCreatePhysicsBody(
                    record,
                    static canonical =>
                    {
                        var created = new PhysicsBody
                        {
                            Position = new Vector3(10f, 20f, 5f),
                            Orientation = Quaternion.Identity,
                            InWorld = true,
                            TransientState =
                                TransientStateFlags.Active,
                        };
                        created.SnapToCell(
                            canonical.FullCellId,
                            created.Position,
                            created.Position);
                        return created;
                    });
            _ = runtime.EntityObjects.Physics.BindProjectile(
                record,
                body,
                new ProjectileCollisionSphere(
                    Vector3.Zero,
                    0.25f));
            runtime.EntityObjects.Physics.AcknowledgeSpatialProjection(
                record,
                spatial: true);

            var updater = new RuntimeProjectilePhysicsUpdater(
                runtime.EntityObjects.Physics);
            Require(
                updater.TryBegin(
                    record,
                    quantum: 0.05f,
                    record.ObjectClockEpoch,
                    externalOwnerValid: null,
                    out RuntimeProjectilePhysicsCommit commit),
                "projectile update did not begin");
            _ = updater.Complete(
                commit,
                liveCenterX: 1,
                liveCenterY: 1,
                _ =>
                {
                    _gameplay.Trace.Add("projectile:ack");
                    return true;
                });
        });
    }

    public bool CompletePortal(
        uint destinationCell = 0x8A020164u,
        ushort sequence = 1)
    {
        return Execute(runtime =>
        {
            RuntimeWorldTransitState transit = runtime.TransitOwner;
            Require(
                transit.TryQueueTeleportStart(sequence),
                "portal start was rejected");
            Require(
                transit.ActivateQueuedTeleport(),
                "portal activation was rejected");
            Require(
                transit.OfferTeleportDestination(
                    new RuntimeTeleportDestination(
                        runtime.PlayerIdentity.ServerGuid,
                        InstanceSequence: 1,
                        PositionSequence: 1,
                        TeleportSequence: sequence,
                        ForcePositionSequence: 1,
                        Position: new Position(
                            destinationCell,
                            Vector3.Zero,
                            Quaternion.Identity)),
                    teleportTimestampAdvanced: true),
                "portal destination was rejected");
            Require(
                transit.TryBeginPortalReveal(
                    sequence,
                    destinationCell,
                    out long revealGeneration),
                "portal reveal was rejected");
            EmitPortal(runtime);
            Require(
                transit.TryRegisterHostProjection(
                    revealGeneration,
                    destinationCell,
                    out RuntimeWorldHostProjectionToken projection),
                "portal projection registration was rejected");
            Acknowledge(
                transit,
                projection,
                RuntimeWorldHostAcknowledgementStage
                    .ProjectionRegistered);
            bool indoor = (destinationCell & 0xFFFFu) >= 0x0100u;
            Require(
                transit.AcknowledgeDestinationReadiness(
                    new RuntimeDestinationReadiness(
                        revealGeneration,
                        destinationCell,
                        indoor,
                        IsUnhydratable: false,
                        RequiredRenderRadius: indoor ? 0 : 1,
                        IsRenderNeighborhoodReady: true,
                        AreCompositeTexturesReady: true,
                        IsCollisionReady: true)),
                "portal readiness was rejected");
            EmitPortal(runtime);
            Require(
                transit.AcknowledgePortalMaterialized(
                    revealGeneration,
                    sequence,
                    destinationCell),
                "portal materialization was rejected");
            EmitPortal(runtime);
            DrainPortalAcknowledgements(transit, projection);
            Require(
                transit.RequireDestinationReservationRelease(
                    projection),
                "portal reservation release was rejected");
            DrainPortalAcknowledgements(transit, projection);
            Require(
                transit.AcknowledgeWorldViewportVisible(
                    revealGeneration),
                "portal viewport acknowledgement was rejected");
            EmitPortal(runtime);
            Require(
                transit.Complete(revealGeneration),
                "portal completion was rejected");
            EmitPortal(runtime);
            DrainPortalAcknowledgements(transit, projection);
        });
    }

    public void Advance(double deltaSeconds)
    {
        _ = Runtime.Clock.Advance(deltaSeconds);
        Runtime.Session.Tick();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (Runtime.Session.CurrentSession is not null)
            _ = Stop();
        _traceSubscription.Dispose();
        _hostLease.Dispose();
        Runtime.Dispose();
        _disposed = true;
    }

    private ILiveSessionEventRouting CreateEventRoute(WorldSession session)
    {
        var route = new EventRoute(Runtime.Generation, _operations.Trace);
        _events = route;
        return route;
    }

    private static void Acknowledge(
        RuntimeWorldTransitState transit,
        RuntimeWorldHostProjectionToken projection,
        RuntimeWorldHostAcknowledgementStage stage) =>
        Require(
            transit.AcknowledgeHostProjection(
                new RuntimeWorldHostAcknowledgement(
                    projection,
                    stage)),
            $"portal host acknowledgement was rejected: {stage}");

    private static void DrainPortalAcknowledgements(
        RuntimeWorldTransitState transit,
        RuntimeWorldHostProjectionToken projection)
    {
        while (transit.TryGetHostProjection(
            projection,
            out RuntimeWorldHostProjectionSnapshot pending))
        {
            RuntimeWorldHostAcknowledgementStage stages =
                pending.PendingAcknowledgements;
            if ((stages & RuntimeWorldHostAcknowledgementStage
                    .SimulationReleaseProjected) != 0)
            {
                Acknowledge(
                    transit,
                    projection,
                    RuntimeWorldHostAcknowledgementStage
                        .SimulationReleaseProjected);
                continue;
            }
            if ((stages & RuntimeWorldHostAcknowledgementStage
                    .DestinationReservationReleased) != 0)
            {
                Acknowledge(
                    transit,
                    projection,
                    RuntimeWorldHostAcknowledgementStage
                        .DestinationReservationReleased);
                continue;
            }
            if ((stages & RuntimeWorldHostAcknowledgementStage
                    .TerminalProjected) != 0)
            {
                Acknowledge(
                    transit,
                    projection,
                    RuntimeWorldHostAcknowledgementStage
                        .TerminalProjected);
                continue;
            }
            break;
        }
    }

    private static void EmitPortal(GameRuntime runtime) =>
        runtime.EventSink.EmitPortal(runtime.TransitOwner.Snapshot);

    private static SpellTable FixtureSpellMetadata()
    {
        const string header =
            "Spell ID,Name,Flags [Hex],IsUntargetted,TargetMask [Hex]";
        const string row = "1,Runtime Fixture,0x0,true,0x0";
        return SpellTable.LoadFromReader(
            new System.IO.StringReader($"{header}\n{row}"));
    }

    private static WorldSession.EntitySpawn ProjectileSpawn(
        uint guid,
        ushort incarnation)
    {
        var position = new CreateObject.ServerPosition(
            0x01010001u,
            10f,
            20f,
            5f,
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
            Instance: incarnation);
        var physics = new PhysicsSpawnData(
            RawState: (uint)(
                PhysicsStateFlags.ReportCollisions
                | PhysicsStateFlags.Missile),
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: null,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: new Vector3(10f, 0f, 0f),
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
            "runtime projectile",
            null,
            null,
            null,
            PhysicsState: physics.RawState,
            InstanceSequence: incarnation,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private ILiveSessionCommandRouting CreateCommandRoute(
        WorldSession session)
    {
        var route = new CommandRoute(Runtime.Generation, _operations.Trace);
        _commands = route;
        return route;
    }

    private sealed class ImmediateResetHost : IRuntimeGenerationResetHost
    {
        public int ProjectionRetirementCount { get; private set; }
        public int ProjectionDrainCount { get; private set; }
        public int ProjectionCompletionCount { get; private set; }

        public void RetireEntityProjection(RuntimeEntityRecord entity) =>
            ProjectionRetirementCount++;

        public void DrainEntityProjectionBoundary() =>
            ProjectionDrainCount++;

        public void CompleteEntityProjectionRetirement() =>
            ProjectionCompletionCount++;
    }

    private sealed class EventRoute(
        RuntimeGenerationToken generation,
        List<string> trace) : ILiveSessionEventRouting
    {
        public RuntimeGenerationToken Generation { get; } = generation;
        public bool IsActive { get; private set; }

        public void Attach()
        {
            IsActive = true;
            trace.Add($"events+:{Generation.Value}");
        }

        public void Dispose()
        {
            IsActive = false;
            trace.Add($"events-:{Generation.Value}");
        }
    }

    private sealed class CommandRoute(
        RuntimeGenerationToken generation,
        List<string> trace) : ILiveSessionCommandRouting
    {
        public RuntimeGenerationToken Generation { get; } = generation;
        public bool IsActive { get; private set; }

        public void Activate()
        {
            IsActive = true;
            trace.Add($"commands+:{Generation.Value}");
        }

        public void Dispose()
        {
            IsActive = false;
            trace.Add($"commands-:{Generation.Value}");
        }
    }

    private sealed class FixtureOperations(
        uint characterId,
        string characterName) : ILiveSessionOperations
    {
        public List<string> Trace { get; } = [];

        public IPEndPoint ResolveEndpoint(string host, int port)
        {
            Trace.Add($"resolve:{host}:{port}");
            return new IPEndPoint(IPAddress.Loopback, port);
        }

        public WorldSession CreateSession(IPEndPoint endpoint)
        {
            Trace.Add("session+");
            return new WorldSession(endpoint, new FixtureTransport());
        }

        public void Connect(
            WorldSession session,
            string user,
            string password) =>
            Trace.Add($"connect:{user}");

        public CharacterList.Parsed GetCharacters(WorldSession session)
        {
            Trace.Add("characters");
            return new CharacterList.Parsed(
                0u,
                [new CharacterList.Character(
                    characterId,
                    characterName,
                    0u)],
                [],
                11,
                "NoWindow",
                true,
                true);
        }

        public void EnterWorld(
            WorldSession session,
            int activeCharacterIndex) =>
            Trace.Add($"enter:{activeCharacterIndex}");

        public void Tick(WorldSession session) => Trace.Add("tick");

        public void DisposeSession(WorldSession session)
        {
            Trace.Add("session-");
            session.Dispose();
        }

        public void RecordConnecting(
            string host,
            int port,
            string user) =>
            Trace.Add($"connecting:{host}:{port}:{user}");

        public void RecordConnected() => Trace.Add("connected");

        public void RecordRoster(LiveSessionRosterReport roster) =>
            Trace.Add($"roster:{roster.AccountName}");

        public void RecordCharacterEntered(
            LiveSessionCharacterSelection selection) =>
            Trace.Add($"character-entered:{selection.CharacterId}");
    }

    private sealed class FixtureTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram) { }
        public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram) { }

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
            throw new OperationCanceledException(cancellationToken);

        public void Dispose() { }
    }

    private sealed class GameplayOperations :
        IRuntimeInteractionTransport,
        IRuntimeCombatAttackOperations,
        IRuntimeCombatTargetOperations,
        IRuntimeCombatModeOperations,
        IRuntimeSpellCastOperations
    {
        private GameRuntime? _runtime;
        private uint _sequence;

        public double Now { get; set; } = 10d;
        public List<string> Trace { get; } = [];

        public void Bind(GameRuntime runtime) =>
            _runtime = runtime
                ?? throw new ArgumentNullException(nameof(runtime));

        public bool CanStartAttack() => IsInWorld;
        public void PrepareAttackRequest() =>
            Trace.Add("attack:prepare");
        public bool SendAttack(AttackHeight height, float power)
        {
            Trace.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"attack:{height}:{power:0.0}"));
            return true;
        }
        public void SendCancelAttack() { }
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => true;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => _runtime?.Session.IsInWorld == true;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(CombatMode mode) =>
            Trace.Add($"combat:{mode}");
        public uint LocalPlayerId =>
            _runtime?.PlayerIdentity.ServerGuid ?? 0u;
        public bool CanSend => IsInWorld;
        public bool HasRequiredComponents(uint spellId) => true;
        public bool IsTargetCompatible(
            uint targetId,
            SpellMetadata spell,
            bool showMessage) => true;
        public void StopCompletely() =>
            Trace.Add("cast:stop");
        public void SendUntargeted(uint spellId) =>
            Trace.Add($"cast:{spellId}");
        public void SendTargeted(uint targetId, uint spellId) =>
            Trace.Add($"cast:{targetId}:{spellId}");
        public void DisplayMessage(string message) =>
            Trace.Add($"message:{message}");
        public void IncrementBusy()
        {
            _runtime?.InventoryOwner.Transactions.IncrementBusyCount();
            Trace.Add("cast:busy");
        }

        public bool TrySendUse(uint serverGuid, out uint sequence)
        {
            sequence = ++_sequence;
            Trace.Add($"use:{serverGuid:X8}");
            return true;
        }

        public bool TrySendPickup(
            uint itemGuid,
            uint destinationContainerId,
            int placement,
            out uint sequence)
        {
            sequence = ++_sequence;
            Trace.Add(
                $"pickup:{itemGuid:X8}:{destinationContainerId:X8}:{placement}");
            return true;
        }
    }
}
