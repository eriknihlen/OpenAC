using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;
using AcDream.Core.Net.Transport;

namespace AcDream.Core.Net;

public sealed class CharacterSelectionRejectedException(
    CharacterError.Parsed error)
    : InvalidOperationException(
        $"The server rejected character entry with error 0x{error.RawErrorCode:X8}.")
{
    public CharacterError.Parsed Error { get; } = error;
}

internal interface IWorldSessionTransport : IDisposable
{
    void Send(ReadOnlySpan<byte> datagram);
    void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram);
    int Receive(
        Span<byte> destination,
        TimeSpan timeout,
        out IPEndPoint? from);
    ValueTask<NetReceiveResult> ReceiveAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken);
}

internal sealed class NetClientWorldSessionTransport(IPEndPoint remote)
    : IWorldSessionTransport
{
    private readonly NetClient _client = new(remote);

    public void Send(ReadOnlySpan<byte> datagram) => _client.Send(datagram);

    public void Send(IPEndPoint endpoint, ReadOnlySpan<byte> datagram) =>
        _client.Send(endpoint, datagram);

    public int Receive(
        Span<byte> destination,
        TimeSpan timeout,
        out IPEndPoint? from) =>
        _client.Receive(destination, timeout, out from);

    public ValueTask<NetReceiveResult> ReceiveAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken) =>
        _client.ReceiveAsync(destination, cancellationToken);

    public void Dispose() => _client.Dispose();
}

public sealed partial class WorldSession : IDisposable
{
    public enum State
    {
        Disconnected,
        Handshaking,
        InCharacterSelect,
        EnteringWorld,
        InWorld,
        Failed,
    }

    public readonly record struct EntitySpawn(
        uint Guid,
        CreateObject.ServerPosition? Position,
        uint? SetupTableId,
        IReadOnlyList<CreateObject.AnimPartChange> AnimPartChanges,
        IReadOnlyList<CreateObject.TextureChange> TextureChanges,
        IReadOnlyList<CreateObject.SubPaletteSwap> SubPalettes,
        uint? BasePaletteId,
        float? ObjScale,
        string? Name,
        uint? ItemType,
        CreateObject.ServerMotionState? MotionState,
        uint? MotionTableId,
        uint? PhysicsState = null,
        uint? ObjectDescriptionFlags = null,
        float? Friction = null,
        float? Elasticity = null,
        uint? Useability = null,
        float? UseRadius = null,
        uint? TargetType = null,
        uint IconId = 0,
        uint IconOverlayId = 0,
        uint IconUnderlayId = 0,
        uint UiEffects = 0,
        uint WeenieClassId = 0,
        int? Value = null,
        int? StackSize = null,
        int? StackSizeMax = null,
        int? Burden = null,
        int? ItemsCapacity = null,
        int? ContainersCapacity = null,
        uint? ContainerId = null,
        uint? WielderId = null,
        uint? ValidLocations = null,
        uint? CurrentWieldedLocation = null,
        uint? Priority = null,
        int? Structure = null,
        int? MaxStructure = null,
        float? Workmanship = null,
        ushort InstanceSequence = 0,
        ushort MovementSequence = 0,
        ushort ServerControlSequence = 0,
        ushort PositionSequence = 0,
        uint? ParentGuid = null,
        uint? ParentLocation = null,
        uint? PlacementId = null,
        byte? RadarBlipColor = null,
        byte? RadarBehavior = null,
        byte? CombatUse = null,
        string? PluralName = null,
        uint? PetOwnerId = null,
        ushort? AmmoType = null,
        uint? SpellId = null,
        uint? CooldownId = null,
        double? CooldownDuration = null,
        PhysicsSpawnData? Physics = null,
        uint? HookItemTypes = null,
        uint? HookType = null,
        uint? MaterialType = null,
        uint? HouseOwnerId = null,
        uint? MonarchId = null,
        HouseRestrictionRecord? Restrictions = null);

    internal static EntitySpawn ToEntitySpawn(CreateObject.Parsed parsed) => new(
        parsed.Guid,
        parsed.Position,
        parsed.SetupTableId,
        parsed.AnimPartChanges,
        parsed.TextureChanges,
        parsed.SubPalettes,
        parsed.BasePaletteId,
        parsed.ObjScale,
        parsed.Name,
        parsed.ItemType,
        parsed.MotionState,
        parsed.MotionTableId,
        parsed.PhysicsState,
        parsed.ObjectDescriptionFlags,
        parsed.Friction,
        parsed.Elasticity,
        parsed.Useability,
        parsed.UseRadius,
        parsed.TargetType,
        parsed.IconId,
        parsed.IconOverlayId,
        parsed.IconUnderlayId,
        parsed.UiEffects,
        parsed.WeenieClassId,
        parsed.Value,
        parsed.StackSize,
        parsed.StackSizeMax,
        parsed.Burden,
        parsed.ItemsCapacity,
        parsed.ContainersCapacity,
        parsed.ContainerId,
        parsed.WielderId,
        parsed.ValidLocations,
        parsed.CurrentWieldedLocation,
        parsed.Priority,
        parsed.Structure,
        parsed.MaxStructure,
        parsed.Workmanship,
        InstanceSequence: parsed.InstanceSequence,
        MovementSequence: parsed.MovementSequence,
        ServerControlSequence: parsed.ServerControlSequence,
        PositionSequence: parsed.PositionSequence,
        ParentGuid: parsed.ParentGuid,
        ParentLocation: parsed.ParentLocation,
        PlacementId: parsed.PlacementId,
        RadarBlipColor: parsed.RadarBlipColor,
        RadarBehavior: parsed.RadarBehavior,
        CombatUse: parsed.CombatUse,
        PluralName: parsed.PluralName,
        PetOwnerId: parsed.PetOwnerId,
        AmmoType: parsed.AmmoType,
        SpellId: parsed.SpellId,
        CooldownId: parsed.CooldownId,
        CooldownDuration: parsed.CooldownDuration,
        Physics: parsed.Physics,
        HookItemTypes: parsed.HookItemTypes,
        HookType: parsed.HookType,
        MaterialType: parsed.MaterialType,
        HouseOwnerId: parsed.HouseOwnerId,
        MonarchId: parsed.MonarchId,
        Restrictions: parsed.Restrictions);

    public event Action<EntitySpawn>? EntitySpawned;

    public event Action<DeleteObject.Parsed>? EntityDeleted;

    public event Action<PickupEvent.Parsed>? EntityPickedUp;

    public readonly record struct EntityMotionUpdate(
        uint Guid,
        CreateObject.ServerMotionState MotionState,
        ushort InstanceSequence,
        ushort MovementSequence,
        ushort ServerControlSequence,
        bool IsAutonomous);

    public event Action<EntityMotionUpdate>? MotionUpdated;

    public readonly record struct EntityPositionUpdate(
        uint Guid,
        CreateObject.ServerPosition Position,
        System.Numerics.Vector3? Velocity,
        uint? PlacementId,
        bool IsGrounded,
        ushort InstanceSequence,
        ushort PositionSequence,
        ushort TeleportSequence,
        ushort ForcePositionSequence);

    /// <summary>
    /// Fires when the session parses a 0xF748 UpdatePosition game message.
    /// </summary>
    public event Action<EntityPositionUpdate>? PositionUpdated;

    public event Action<VectorUpdate.Parsed>? VectorUpdated;

    public event Action<ParentEvent.Parsed>? ParentUpdated;

    public event Action<SetState.Parsed>? StateUpdated;

    public readonly record struct ObjectIntPropertyUpdate(uint Guid, uint Property, int Value);

    public event Action<ObjectIntPropertyUpdate>? ObjectIntPropertyUpdated;

    public readonly record struct PlayerIntPropertyUpdate(uint Property, int Value);

    /// <summary>Fires when the session parses a PrivateUpdatePropertyInt (0x02CD) — one
    /// PropertyInt updated on the player. B-Wire routes EncumbranceVal (5) to the burden bar.</summary>
    public event Action<PlayerIntPropertyUpdate>? PlayerIntPropertyUpdated;

    public readonly record struct PlayerInt64PropertyUpdate(uint Property, long Value);

    public event Action<PlayerInt64PropertyUpdate>? PlayerInt64PropertyUpdated;

    public readonly record struct StackSizeUpdate(uint Guid, int StackSize, int Value);

    /// <summary>Fires when the session parses a SetStackSize (0x0197) top-level GameMessage.</summary>
    public event Action<StackSizeUpdate>? StackSizeUpdated;

    /// <summary>Fires when the session parses an InventoryRemoveObject (0x0024) — the guid left
    /// the player's inventory view.</summary>
    public event Action<uint>? InventoryObjectRemoved;

    public event Action<uint>? TeleportStarted;

    public event Action<ObjDescEvent.Parsed>? AppearanceUpdated;

    public event Action<HearSpeech.Parsed>? SpeechHeard;

    public event Action<EmoteText.Parsed>? EmoteHeard;

    public event Action<SoulEmote.Parsed>? SoulEmoteHeard;

    public event Action<ServerMessage.Parsed>? ServerMessageReceived;

    public event Action<PlayerKilled.Parsed>? PlayerKilledReceived;

    public event Action<TurbineChat.Parsed>? TurbineChatReceived;

    public event Action<SetTurbineChatChannels.Parsed>? TurbineChannelsReceived;

    public event Action<PrivateUpdateVital.ParsedFull>? VitalUpdated;

    public event Action<PrivateUpdateVital.ParsedCurrent>? VitalCurrentUpdated;

    public event Action<PrivateUpdateAttribute.Parsed>? AttributeUpdated;

    public event Action<PrivateUpdateSkill.Parsed>? SkillUpdated;

    public event Action<PlayPhysicsScript>? PlayPhysicsScriptReceived;

    public event Action<PlayPhysicsScriptType>? PlayPhysicsScriptTypeReceived;

    public event Action<SoundEvent>? SoundEventReceived;

    public event Action<uint /*environChangeType*/>? EnvironChanged;

    public event Action<double>? ServerTimeUpdated;

    public double LastServerTimeTicks { get; private set; }

    /// <summary>Raised every time the state machine transitions.</summary>
    public event Action<State>? StateChanged;
    public event Action<ConnectionProgress>? ConnectionProgressChanged;
    public ConnectionProgress ConnectionProgress { get; private set; }
    public DddDataVersions? DataVersions { get; set; }
    private bool _dataCheckComplete;
    private bool _dataInterrogationReceived;

    public event Action<CharacterList.Parsed>? CharacterListReceived;
    public event Action? CharacterDeleteAcknowledged;
    public event Action<CharacterRestore.Parsed>? CharacterRestoreReceived;
    public event Action<CharGenVerificationResponse.Parsed>? CharacterCreateResponseReceived;
    public event Action<CharacterError.Parsed>? CharacterErrorReceived;
    public event Action<ServerName.Parsed>? ServerNameReceived;

    public GameEventDispatcher GameEvents { get; } = new();

    public State CurrentState { get; private set; } = State.Disconnected;

    public LinkStatusSnapshot LinkStatus => BuildLinkStatus(
        CurrentState,
        Volatile.Read(ref _lastInboundPacketTicks),
        Stopwatch.GetTimestamp(),
        Stopwatch.Frequency,
        PingRoundTripSeconds,
        _transport?.PacketLossPercentage ?? 0d);

    internal double? PingRoundTripSeconds
    {
        get
        {
            double value = BitConverter.Int64BitsToDouble(
                Volatile.Read(ref _lastPingRoundTripBits));
            return double.IsFinite(value) && value >= 0d ? value : null;
        }
    }

    internal static LinkStatusSnapshot BuildLinkStatus(
        State state,
        long lastInboundPacketTicks,
        long nowTicks,
        long frequency,
        double? roundTripSeconds = null,
        double packetLossPercentage = 0d)
    {
        bool connected = state is not State.Disconnected and not State.Failed;
        if (!connected || frequency <= 0)
            return LinkStatusSnapshot.Disconnected;

        long elapsed = Math.Max(0, nowTicks - lastInboundPacketTicks);
        return new LinkStatusSnapshot(
            true,
            elapsed / (double)frequency,
            packetLossPercentage,
            roundTripSeconds);
    }

    private void RecordPingResponse(long nowTicks)
    {
        long requestTicks = Interlocked.Exchange(ref _lastPingRequestTicks, 0L);
        if (requestTicks <= 0L || nowTicks < requestTicks || Stopwatch.Frequency <= 0)
            return;

        double elapsed = (nowTicks - requestTicks) / (double)Stopwatch.Frequency;
        Volatile.Write(
            ref _lastPingRoundTripBits,
            BitConverter.DoubleToInt64Bits(elapsed));
    }

    public ushort InstanceSequence => _instanceSequence;
    public ushort ServerControlSequence => _serverControlSequence;
    public ushort TeleportSequence => _teleportSequence;
    public ushort ForcePositionSequence => _forcePositionSequence;

    public void PublishAcceptedLocalPhysicsTimestamps(
        ushort instance,
        ushort serverControlledMove,
        ushort teleport,
        ushort forcePosition)
    {
        _instanceSequence = instance;
        _serverControlSequence = serverControlledMove;
        _teleportSequence = teleport;
        _forcePositionSequence = forcePosition;
    }

    public CharacterList.Parsed? Characters { get; private set; }

    public ServerName.Parsed? ServerInfo { get; private set; }
    private CharacterError.Parsed? _lastCharacterSelectionError;

    private enum PendingCharGenVerificationRequest
    {
        None,
        Restore,
        Create,
    }

    private PendingCharGenVerificationRequest _pendingCharGenVerification =
        PendingCharGenVerificationRequest.None;

    private bool _loggedUnexpectedCharGenVerificationResponse;

    private readonly IWorldSessionTransport _net;
    private long _lastInboundPacketTicks = Stopwatch.GetTimestamp();
    private long _lastPingRequestTicks;
    private long _lastPingRoundTripBits = BitConverter.DoubleToInt64Bits(double.NaN);
    private readonly IPEndPoint _loginEndpoint;
    private readonly IPEndPoint _connectEndpoint;
    private readonly FragmentAssembler _assembler = new();

    private static readonly bool DumpOpcodesEnabled =
        Environment.GetEnvironmentVariable("ACDREAM_DUMP_OPCODES") == "1";
    private static readonly bool DumpAppearanceEnabled =
        Environment.GetEnvironmentVariable("ACDREAM_DUMP_APPEARANCE") == "1";
    private readonly System.Collections.Generic.HashSet<uint> _seenUnhandledOpcodes = new();

    private ushort _sessionClientId;
    private ushort _sessionIteration;
    private bool _transportNegotiated;

    internal const double ConnectResponseRetrySeconds = 0.333333333;

    private bool _handshakeConfirmed;

    private ReliableTransport? _transport;

    internal ReliableTransport? Transport => _transport;

    internal (Func<long> GetTimestamp, long Frequency)? TransportClockSource
    { get; set; }

    private ushort _instanceSequence;
    private ushort _serverControlSequence;
    private ushort _teleportSequence;
    private ushort _forcePositionSequence;
    private uint _activeCharacterId;
    private int _characterLogOffConfirmed;
    private int _disposeStarted;

    internal const int MaxInboundDatagramBytes = ushort.MaxValue;

    private readonly Channel<PooledInboundDatagram> _inboundQueue =
        Channel.CreateUnbounded<PooledInboundDatagram>(
            new UnboundedChannelOptions
            { SingleReader = true, SingleWriter = true });
    private Task? _netReceiveTask;
    private readonly CancellationTokenSource _netCancel = new();

    internal readonly record struct PooledInboundDatagram(
        byte[] Buffer,
        int Length)
    {
        public ReadOnlyMemory<byte> Memory =>
            Buffer.AsMemory(0, Length);
    }

    private bool _setStateHexDumped;

    private uint _gameActionSequence;

    public WorldSession(IPEndPoint serverLogin)
        : this(
            serverLogin,
            static endpoint => LossyTransportDecorator.WrapIfConfigured(
                new NetClientWorldSessionTransport(endpoint)))
    {
    }

    internal WorldSession(
        IPEndPoint serverLogin,
        IWorldSessionTransport transport)
        : this(serverLogin, _ => transport)
    {
    }

    internal WorldSession(
        IPEndPoint serverLogin,
        Func<IPEndPoint, IWorldSessionTransport> transportFactory)
    {
        ArgumentNullException.ThrowIfNull(serverLogin);
        ArgumentNullException.ThrowIfNull(transportFactory);
        if (serverLogin.Port == ushort.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(serverLogin),
                "The login endpoint must leave room for the adjacent connect port.");

        _loginEndpoint = serverLogin;
        _connectEndpoint = new IPEndPoint(serverLogin.Address, serverLogin.Port + 1);
        _net = transportFactory(serverLogin)
            ?? throw new InvalidOperationException("The session transport factory returned null.");

        GameEvents.Register(GameEventType.SetTurbineChatChannels, e =>
        {
            var parsed = SetTurbineChatChannels.TryParse(e.Payload.Span);
            if (parsed is not null) TurbineChannelsReceived?.Invoke(parsed.Value);
        });
        GameEvents.Register(GameEventType.PingResponse, e =>
        {
            if (Messages.GameEvents.ParsePingResponse(e.Payload.Span))
                RecordPingResponse(Stopwatch.GetTimestamp());
        });
    }

    public void StartCharacterSelectionReceive()
    {
        if (CurrentState != State.InCharacterSelect)
            throw new InvalidOperationException(
                "character-selection receive requires InCharacterSelect state");

        EnsureNetReceiveLoopStarted();
    }

    public bool IsCharacterLogOffConfirmed =>
        Volatile.Read(ref _characterLogOffConfirmed) != 0;

    public void RequestCharacterLogOff()
    {
        if (CurrentState != State.InWorld || _activeCharacterId == 0)
        {
            throw new InvalidOperationException(
                "character logoff requires an in-world session with an "
                + "active character");
        }

        Interlocked.Exchange(ref _characterLogOffConfirmed, 0);
        SendGameMessage(CharacterLogOff.BuildRequestBody(_activeCharacterId));
    }

    public void ReturnToCharacterSelect()
    {
        if (CurrentState is not (State.InWorld or State.EnteringWorld))
        {
            throw new InvalidOperationException(
                "return-to-character-select requires an in-world session");
        }

        _activeCharacterId = 0;
        _gameActionSequence = 0;
        Interlocked.Exchange(ref _characterLogOffConfirmed, 0);
        Transition(State.InCharacterSelect);
    }

    public void EnterWorld(int characterIndex = 0, TimeSpan? timeout = null)
    {
        if (Characters is null || Characters.Characters.Count == 0)
            throw new InvalidOperationException("Connect() must complete with a non-empty CharacterList");
        EnterWorldSelection selection = SelectCharacterForEnterWorld(
            Characters,
            characterIndex);
        EnterWorldCore(selection.Character.Id, selection.EnterWorldBody, timeout);
    }

    public void EnterWorld(uint characterGuid, string accountName, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(accountName);
        byte[] enterWorldBody = CharacterEnterWorld.BuildEnterWorldBody(characterGuid, accountName);
        EnterWorldCore(characterGuid, enterWorldBody, timeout);
    }

    private void EnterWorldCore(uint characterGuid, byte[] enterWorldBody, TimeSpan? timeout)
    {
        ThrowIfDataCheckFailed();
        if (!_dataCheckComplete)
            throw new InvalidOperationException("The game-data check must finish before entering the world.");
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        _activeCharacterId = characterGuid;
        Transition(State.EnteringWorld);

        SendGameMessage(CharacterEnterWorld.BuildEnterWorldRequestBody());

        _lastCharacterSelectionError = null;
        bool serverReady;
        if (_netReceiveTask is null)
        {
            serverReady = false;
            while (DateTime.UtcNow < deadline
                   && !serverReady
                   && _lastCharacterSelectionError is null)
            {
                bool drained = PumpOnce(out List<uint> opcodes);
                SweepTransport();
                if (!drained)
                    continue;

                foreach (uint opcode in opcodes)
                {
                    if (opcode == 0xF7DFu)
                    {
                        serverReady = true;
                        break;
                    }
                }
            }
        }
        else
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            serverReady = remaining > TimeSpan.Zero
                && WaitForCharacterLogOffConfirmation(
                    _inboundQueue.Reader,
                    remaining,
                    datagram =>
                    {
                        var opcodes = new List<uint>();
                        ProcessDatagram(datagram.Memory, opcodes);
                        return opcodes.Contains(0xF7DFu)
                            || _lastCharacterSelectionError is not null;
                    },
                    ReturnInboundDatagram,
                    SweepTransport,
                    TimeSpan.FromMilliseconds(25));
        }
        if (_lastCharacterSelectionError is { } selectionError)
        {
            Transition(State.InCharacterSelect);
            EnsureNetReceiveLoopStarted();
            throw new CharacterSelectionRejectedException(selectionError);
        }
        if (!serverReady) { Transition(State.Failed); throw new TimeoutException("ServerReady not received"); }

        SendGameMessage(enterWorldBody);

        Transition(State.InWorld);

        EnsureNetReceiveLoopStarted();
    }

    private void EnsureNetReceiveLoopStarted() =>
        _netReceiveTask ??= NetReceiveLoopAsync();

    internal readonly record struct EnterWorldSelection(
        CharacterList.Character Character,
        byte[] EnterWorldBody);

    internal static EnterWorldSelection SelectCharacterForEnterWorld(
        CharacterList.Parsed characters,
        int characterIndex)
    {
        ArgumentNullException.ThrowIfNull(characters);
        if (characterIndex < 0 || characterIndex >= characters.Characters.Count)
            throw new ArgumentOutOfRangeException(nameof(characterIndex));

        CharacterList.Character chosen = characters.Characters[characterIndex];
        if (!CharacterList.IsAvailableActiveIdentity(chosen))
            throw new InvalidOperationException(
                "selected character must be an active, non-greyed identity");

        return new EnterWorldSelection(
            chosen,
            CharacterEnterWorld.BuildEnterWorldBody(
                chosen.Id,
                characters.AccountName));
    }

    private static readonly long InboundBudgetTicks = Stopwatch.Frequency / 1000 * 4;

    public int Tick()
    {
        int processed = 0;
        bool budgetBroke = false;
        long start = Stopwatch.GetTimestamp();
        while (_inboundQueue.Reader.TryRead(
                   out PooledInboundDatagram datagram))
        {
            if (NetDiagnostics.ProbeNet)
                Interlocked.Decrement(ref _probeInboundDepth);
            try
            {
                ProcessDatagram(datagram.Memory);
            }
            finally
            {
                ReturnInboundDatagram(datagram);
            }
            processed++;
            if (InboundBudgetExceeded(CurrentState, start, Stopwatch.GetTimestamp(), InboundBudgetTicks))
            {
                budgetBroke = true;
                break;
            }
        }
        if (NetDiagnostics.ProbeNet)
            ProbeNetTickCadence(start, processed, budgetBroke);
        // N1: the transport sweep runs at the end of EVERY Tick, after the
        // budget break — a deferred inbound tail must not defer a due
        // resend past this frame.
        SweepTransport();
        return processed;
    }

    private void SweepTransport()
    {
        if (!_transportNegotiated)
            return;
        _transport?.Sweep();
    }

    private long _probeLastTickTs;
    private long _probeWindowStartTs;
    private long _probeMaxGapTicks;
    private int _probeProcessedWindow;
    private int _probeBudgetBreaks;
    private int _probeSendWindow;
    private long _probeAckSeenTotal;
    private long _probeResendSeenTotal;
    private long _probeNakOutSeenTotal;
    private long _probeNakInSeenTotal;
    private long _probeRejInSeenTotal;
    private long _probeDupDropSeenTotal;
    private long _probeParkedSeenTotal;
    private long _probeReclaimSeenTotal;
    private int _probeInboundDepth;

    private void ProbeNetTickCadence(long tickStartTs, int processed, bool budgetBroke)
    {
        if (_probeLastTickTs != 0)
        {
            long gap = tickStartTs - _probeLastTickTs;
            if (gap > _probeMaxGapTicks)
                _probeMaxGapTicks = gap;
        }
        _probeLastTickTs = tickStartTs;
        _probeProcessedWindow += processed;
        if (budgetBroke)
            _probeBudgetBreaks++;

        if (_probeWindowStartTs == 0)
        {
            _probeWindowStartTs = tickStartTs;
            return;
        }
        long windowTicks = tickStartTs - _probeWindowStartTs;
        if (windowTicks < Stopwatch.Frequency)
            return;

        double windowSeconds = (double)windowTicks / Stopwatch.Frequency;
        double maxGapMs = _probeMaxGapTicks * 1000.0 / Stopwatch.Frequency;
        int sends = Interlocked.Exchange(ref _probeSendWindow, 0);
        ReliableTransport? transport = _transport;
        TransportStats? stats = transport?.Stats;
        long acks = WindowDelta(stats?.AcksSent ?? 0, ref _probeAckSeenTotal);
        long resends = WindowDelta(
            stats?.ResendsSent ?? 0, ref _probeResendSeenTotal);
        long naksOut = WindowDelta(
            stats?.NaksSent ?? 0, ref _probeNakOutSeenTotal);
        long naksIn = WindowDelta(
            stats?.NakRequestsReceived ?? 0, ref _probeNakInSeenTotal);
        long rejsIn = WindowDelta(
            stats?.RejectsReceived ?? 0, ref _probeRejInSeenTotal);
        long dupDrops = WindowDelta(
            stats?.InboundDupsDropped ?? 0, ref _probeDupDropSeenTotal);
        long parked = WindowDelta(
            stats?.KeysParked ?? 0, ref _probeParkedSeenTotal);
        long reclaimed = WindowDelta(
            stats?.RejectWordsReclaimed ?? 0, ref _probeReclaimSeenTotal);
        Console.WriteLine(FormatNetTickLine(
            windowSeconds,
            _probeProcessedWindow,
            Volatile.Read(ref _probeInboundDepth),
            _probeBudgetBreaks,
            maxGapMs,
            sends,
            acks,
            resends,
            naksOut,
            naksIn,
            rejsIn,
            dupDrops,
            parked,
            reclaimed,
            stats?.CacheDepth ?? 0,
            transport?.Inbound.NakCount ?? 0,
            CurrentState));
        _probeWindowStartTs = tickStartTs;
        _probeMaxGapTicks = 0;
        _probeProcessedWindow = 0;
        _probeBudgetBreaks = 0;
    }

    private static long WindowDelta(long cumulative, ref long seenTotal)
    {
        long delta = cumulative - seenTotal;
        seenTotal = cumulative;
        return delta;
    }

    internal static string FormatNetTickLine(
        double windowSeconds,
        int processed,
        int queueDepth,
        int budgetBreaks,
        double maxGapMs,
        int sends,
        long acks,
        long resends,
        long naksOut,
        long naksIn,
        long rejsIn,
        long dupDrops,
        long parked,
        long reclaimed,
        int cacheDepth,
        int nakSetDepth,
        State state) =>
        $"[net-tick] in/s={processed / windowSeconds:F0}"
        + $" q={queueDepth}"
        + $" budget-breaks={budgetBreaks}"
        + $" maxgap={maxGapMs:F0}ms"
        + $" out/s={sends / windowSeconds:F0}"
        + $" acks/s={acks / windowSeconds:F0}"
        + $" resend/s={resends / windowSeconds:F0}"
        + $" nak-out/s={naksOut / windowSeconds:F0}"
        + $" nak-in/s={naksIn / windowSeconds:F0}"
        + $" rej-in/s={rejsIn / windowSeconds:F0}"
        + $" dup-drop/s={dupDrops / windowSeconds:F0}"
        + $" parked/s={parked / windowSeconds:F0}"
        + $" reclaim/s={reclaimed / windowSeconds:F0}"
        + $" cache={cacheDepth}"
        + $" nakset={nakSetDepth}"
        + $" st={state}";

    internal static bool InboundBudgetExceeded(State state, long startTicks, long nowTicks, long budgetTicks)
        => state == State.InWorld && nowTicks - startTicks >= budgetTicks;

    private async Task NetReceiveLoopAsync()
    {
        byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(
            MaxInboundDatagramBytes);
        try
        {
            while (!_netCancel.Token.IsCancellationRequested)
            {
                byte[]? queuedBuffer = null;
                bool ownershipTransferred = false;
                try
                {
                    NetReceiveResult result =
                        await _net.ReceiveAsync(
                            receiveBuffer.AsMemory(
                                0,
                                MaxInboundDatagramBytes),
                            _netCancel.Token).ConfigureAwait(false);
                    queuedBuffer = ArrayPool<byte>.Shared.Rent(
                        result.Length);
                    receiveBuffer.AsSpan(0, result.Length).CopyTo(
                        queuedBuffer);
                    ownershipTransferred =
                        _inboundQueue.Writer.TryWrite(
                            new PooledInboundDatagram(
                                queuedBuffer,
                                result.Length));
                    if (ownershipTransferred && NetDiagnostics.ProbeNet)
                        Interlocked.Increment(ref _probeInboundDepth);
                }
                catch (System.Net.Sockets.SocketException ex)
                {
                    // Transient, recoverable socket error (e.g. WSAECONNRESET
                    // from a stale peer's ICMP port-unreachable) — log and
                    // keep receiving. Does NOT tear down _inboundQueue.
                    Console.Error.WriteLine(
                        $"[net] receive error (continuing): {ex.SocketErrorCode} {ex.Message}");
                }
                finally
                {
                    if (!ownershipTransferred
                        && queuedBuffer is not null)
                    {
                        ArrayPool<byte>.Shared.Return(queuedBuffer);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
        catch (ObjectDisposedException) { /* NetClient disposed before thread noticed */ }
        finally
        {
            ArrayPool<byte>.Shared.Return(receiveBuffer);
            _inboundQueue.Writer.TryComplete();
        }
    }

    private PooledInboundDatagram? ReceiveBlocking(TimeSpan timeout)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(
            MaxInboundDatagramBytes);
        try
        {
            int length = _net.Receive(
                buffer.AsSpan(0, MaxInboundDatagramBytes),
                timeout,
                out _);
            if (length < 0)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                return null;
            }

            return new PooledInboundDatagram(buffer, length);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private static void ReturnInboundDatagram(
        PooledInboundDatagram datagram) =>
        ArrayPool<byte>.Shared.Return(datagram.Buffer);

    private bool PumpOnce()
    {
        return PumpOnce(out _);
    }

    private bool PumpOnce(out List<uint> opcodesThisCall)
    {
        opcodesThisCall = new List<uint>();
        PooledInboundDatagram? received =
            ReceiveBlocking(TimeSpan.FromMilliseconds(250));
        if (received is null)
            return false;

        PooledInboundDatagram datagram = received.Value;
        try
        {
            ProcessDatagram(
                datagram.Memory,
                opcodesThisCall);
            return true;
        }
        finally
        {
            ReturnInboundDatagram(datagram);
        }
    }

    private void ProcessDatagram(
        ReadOnlyMemory<byte> bytes,
        List<uint>? opcodesOut = null,
        bool dispatchWorldEvents = true)
    {
        if (!PacketCodec.TryParseBorrowed(
                bytes,
                out BorrowedPacket packet,
                out uint headerHash,
                out uint payloadHash,
                out _))
        {
            return;
        }

        PacketHeader serverHeader = packet.Header;
        bool encrypted = serverHeader.HasFlag(
            PacketHeaderFlags.EncryptedChecksum);

        if (serverHeader.Sequence == 0)
        {
            if (encrypted
                || !PacketCodec.VerifyChecksum(
                    in serverHeader,
                    headerHash,
                    payloadHash,
                    isaacKey: null))
            {
                return;
            }
        }
        else if (_transport is { } inboundTransport)
        {
            InboundSequenceTracker.Admission admission =
                inboundTransport.Inbound.Admit(
                    serverHeader.Sequence,
                    encrypted);
            if (admission.Drop)
                return;

            if (!PacketCodec.VerifyChecksum(
                    in serverHeader,
                    headerHash,
                    payloadHash,
                    admission.VerifyKey))
            {
                inboundTransport.Stats.ChecksumFailures++;
                if (encrypted)
                {
                    inboundTransport.Inbound.ReparkKey(
                        serverHeader.Sequence,
                        admission.VerifyKey!.Value,
                        admission.VerifyKeyDrawOrder);
                }

                return;
            }
        }
        else
        {
            if (encrypted
                || !PacketCodec.VerifyChecksum(
                    in serverHeader,
                    headerHash,
                    payloadHash,
                    isaacKey: null))
            {
                return;
            }
        }

        Volatile.Write(ref _lastInboundPacketTicks, Stopwatch.GetTimestamp());
        if (_transport is { } acceptedTransport)
            acceptedTransport.Stats.PacketsReceived++;

        if (!_handshakeConfirmed
            && _transportNegotiated
            && !serverHeader.HasFlag(PacketHeaderFlags.ConnectRequest))
        {
            _handshakeConfirmed = true;
            SetConnectionProgress(new(ConnectionPhase.CheckingData));
        }

        if (_transport is { } transport)
        {
            if ((serverHeader.Flags & PacketHeaderFlags.RequestRetransmit) != 0
                && packet.Optional.RetransmitRequestCount > 0)
            {
                transport.Outbound.OnRetransmitRequest(
                    packet.Optional.RetransmitRequestBytes.Span,
                    packet.Optional.RetransmitRequestCount);
            }

            if ((serverHeader.Flags & PacketHeaderFlags.RejectRetransmit) != 0)
            {
                transport.Stats.RejectsReceived++;
                if (packet.Optional.RejectRetransmitCount > 0)
                {
                    transport.Inbound.OnRejectRetransmit(
                        packet.Optional.RejectRetransmitBytes.Span,
                        packet.Optional.RejectRetransmitCount);
                }

                if (serverHeader.Sequence != 0 && !encrypted)
                {
                    transport.Inbound.OnCleartextRejectSequence(
                        serverHeader.Sequence);
                }
            }

            if ((serverHeader.Flags & PacketHeaderFlags.AckSequence) != 0)
                transport.Outbound.OnAckSequence(packet.Optional.AckSequence);
        }

        if ((serverHeader.Flags & PacketHeaderFlags.TimeSync) != 0)
        {
            double t = packet.Optional.TimeSync;
            if (t > 0)
            {
                LastServerTimeTicks = t;
                ServerTimeUpdated?.Invoke(t);
            }
        }

        foreach (BorrowedMessageFragment frag
                 in packet.Fragments)
        {
            if (!_assembler.TryIngest(
                    frag,
                    out ReadOnlyMemory<byte> bodyMemory,
                    out _)
                || bodyMemory.Length < 4)
            {
                continue;
            }

            ReadOnlySpan<byte> body = bodyMemory.Span;
            uint op = BinaryPrimitives.ReadUInt32LittleEndian(
                body);
            opcodesOut?.Add(op);

            if (CharacterLogOff.IsConfirmation(body))
            {
                Interlocked.Exchange(ref _characterLogOffConfirmed, 1);
                continue;
            }

            if (!dispatchWorldEvents)
                continue;

            if (op == CharacterList.Opcode)
            {
                CharacterList.Parsed parsed;
                try
                {
                    parsed = CharacterList.Parse(body);
                }
                catch
                {
                    // Malformed management messages do not poison the
                    // remaining ordered UIQueue fragments.
                    continue;
                }
                Characters = parsed;
                CharacterListReceived?.Invoke(parsed);
            }
            else if (op == ServerName.Opcode)
            {
                ServerName.Parsed parsed;
                try
                {
                    parsed = ServerName.Parse(body);
                }
                catch
                {
                    // Malformed management messages do not poison the
                    // remaining ordered UIQueue fragments.
                    continue;
                }
                ServerInfo = parsed;
                ServerNameReceived?.Invoke(parsed);
            }
            else if (op == CharacterDelete.Opcode
                && CharacterDelete.IsAcknowledgement(body))
            {
                CharacterDeleteAcknowledged?.Invoke();
            }
            else if (op == CharGenVerificationResponse.ResponseOpcode)
            {
                PendingCharGenVerificationRequest awaited = _pendingCharGenVerification;
                if (awaited == PendingCharGenVerificationRequest.None)
                {
                    if (!_loggedUnexpectedCharGenVerificationResponse)
                    {
                        _loggedUnexpectedCharGenVerificationResponse = true;
                        Console.Error.WriteLine(
                            "[session] unexpected CharacterGenerationVerificationResponse "
                            + "(0xF643) with no outstanding create/restore request — dropped.");
                    }
                    continue;
                }
                _pendingCharGenVerification = PendingCharGenVerificationRequest.None;

                if (awaited == PendingCharGenVerificationRequest.Restore)
                {
                    CharacterRestore.Parsed parsed;
                    try
                    {
                        parsed = CharacterRestore.Parse(body);
                    }
                    catch
                    {
                        continue;
                    }
                    CharacterRestoreReceived?.Invoke(parsed);
                }
                else
                {
                    CharGenVerificationResponse.Parsed parsed;
                    try
                    {
                        parsed = CharGenVerificationResponse.Parse(body);
                    }
                    catch
                    {
                        continue;
                    }
                    CharacterCreateResponseReceived?.Invoke(parsed);
                }
            }
            else if (op == CharacterError.Opcode)
            {
                CharacterError.Parsed parsed;
                try
                {
                    parsed = CharacterError.Parse(body);
                }
                catch
                {
                    continue;
                }
                if (parsed.AsCode == CharacterError.Code.NumErrors)
                    continue;
                _lastCharacterSelectionError = parsed;
                CharacterErrorReceived?.Invoke(parsed);
            }
            else if (op == 0xF7E5u)  // DddInterrogation — server asks "what dat list versions do you have?"
            {
                _dataInterrogationReceived = true;
                SendGameMessage(DddInterrogationResponse.Build(DataVersions), GameMessageGroup.DatabaseQueue);
            }
            else if (op == DddBegin.Opcode)
            {
                try
                {
                    if (DddBegin.Parse(body).RequiresUpdate)
                        SetConnectionProgress(new(ConnectionPhase.Unsupported,
                            new UnsupportedDataUpdateException().Message));
                }
                catch (InvalidDataException error)
                {
                    SetConnectionProgress(new(ConnectionPhase.Failed, error.Message));
                }
            }
            else if (op == 0xF7E2u)
            {
                SetConnectionProgress(new(ConnectionPhase.Unsupported,
                    new UnsupportedDataUpdateException().Message));
            }
            else if (op == 0xF7EAu)
            {
                if (body.Length != 4)
                    SetConnectionProgress(new(ConnectionPhase.Failed,
                        "Invalid game-data completion response."));
                else if (ConnectionProgress.Phase is not (ConnectionPhase.Unsupported or ConnectionPhase.Failed))
                {
                    if (!_dataCheckComplete && _dataInterrogationReceived)
                        SendGameMessage(BitConverter.GetBytes(0xF7EAu), GameMessageGroup.DatabaseQueue);
                    _dataCheckComplete = true;
                }
            }
            else if (op == CreateObject.Opcode)
            {
                var parsed = CreateObject.TryParse(body);
                if (parsed is not null)
                {
                    EntitySpawned?.Invoke(ToEntitySpawn(parsed.Value));
                }
            }
            else if (op == DeleteObject.Opcode)
            {
                var parsed = DeleteObject.TryParse(body);
                if (parsed is not null)
                    EntityDeleted?.Invoke(parsed.Value);
            }
            else if (op == PickupEvent.Opcode)
            {
                var parsed = PickupEvent.TryParse(body);
                if (parsed is not null)
                    EntityPickedUp?.Invoke(parsed.Value);
            }
            else if (op == ParentEvent.Opcode)
            {
                var parsed = ParentEvent.TryParse(body);
                if (parsed is not null)
                    ParentUpdated?.Invoke(parsed.Value);
            }
            else if (op == UpdateMotion.Opcode)
            {
                var motion = UpdateMotion.TryParse(body);
                if (motion is not null)
                {
                    MotionUpdated?.Invoke(new EntityMotionUpdate(
                        motion.Value.Guid,
                        motion.Value.MotionState,
                        motion.Value.InstanceSequence,
                        motion.Value.MovementSequence,
                        motion.Value.ServerControlSequence,
                        motion.Value.IsAutonomous));
                }
            }
            else if (op == UpdatePosition.Opcode)
            {
                var posUpdate = UpdatePosition.TryParse(body);
                if (posUpdate is not null)
                {
                    PositionUpdated?.Invoke(new EntityPositionUpdate(
                        posUpdate.Value.Guid,
                        posUpdate.Value.Position,
                        posUpdate.Value.Velocity,
                        posUpdate.Value.PlacementId,
                        posUpdate.Value.IsGrounded,
                        posUpdate.Value.InstanceSequence,
                        posUpdate.Value.PositionSequence,
                        posUpdate.Value.TeleportSequence,
                        posUpdate.Value.ForcePositionSequence));
                }
            }
            else if (op == VectorUpdate.Opcode)
            {
                var parsed = VectorUpdate.TryParse(body);
                if (parsed is not null)
                    VectorUpdated?.Invoke(parsed.Value);
            }
            else if (op == SetState.Opcode)
            {
                if (AcDream.Core.Physics.PhysicsDiagnostics.ProbeBuildingEnabled
                    && !_setStateHexDumped)
                {
                    _setStateHexDumped = true;
                    var hex = string.Join(" ", body
                        .Slice(0, Math.Min(body.Length, 32))
                        .ToArray()
                        .Select(b => b.ToString("X2")));
                    Console.WriteLine($"[setstate-hex] body.len={body.Length} first-{Math.Min(body.Length, 32)}-bytes: {hex}");
                }

                var parsed = SetState.TryParse(body);
                if (parsed is not null)
                    StateUpdated?.Invoke(parsed.Value);
            }
            else if (op == HearSpeech.LocalOpcode || op == HearSpeech.RangedOpcode)
            {
                var parsed = HearSpeech.TryParse(body);
                if (parsed is not null)
                    SpeechHeard?.Invoke(parsed.Value);
            }
            else if (op == EmoteText.Opcode)
            {
                var parsed = EmoteText.TryParse(body);
                if (parsed is not null)
                    EmoteHeard?.Invoke(parsed.Value);
            }
            else if (op == SoulEmote.Opcode)
            {
                var parsed = SoulEmote.TryParse(body);
                if (parsed is not null)
                    SoulEmoteHeard?.Invoke(parsed.Value);
            }
            else if (op == ServerMessage.Opcode)
            {
                var parsed = ServerMessage.TryParse(body);
                if (parsed is not null)
                    ServerMessageReceived?.Invoke(parsed.Value);
            }
            else if (op == PlayerKilled.Opcode)
            {
                var parsed = PlayerKilled.TryParse(body);
                if (parsed is not null)
                    PlayerKilledReceived?.Invoke(parsed.Value);
            }
            else if (op == TurbineChat.Opcode)
            {
                var parsed = TurbineChat.TryParse(body);
                if (parsed is not null) TurbineChatReceived?.Invoke(parsed.Value);
            }
            else if (op == PrivateUpdateVital.FullOpcode)
            {
                var parsed = PrivateUpdateVital.TryParseFull(body);
                if (parsed is not null)
                    VitalUpdated?.Invoke(parsed.Value);
            }
            else if (op == PrivateUpdateVital.CurrentOpcode)
            {
                var parsed = PrivateUpdateVital.TryParseCurrent(body);
                if (parsed is not null)
                    VitalCurrentUpdated?.Invoke(parsed.Value);
            }
            else if (op == PrivateUpdateAttribute.Opcode)
            {
                var parsed = PrivateUpdateAttribute.TryParse(body);
                if (parsed is not null)
                    AttributeUpdated?.Invoke(parsed.Value);
            }
            else if (op == PrivateUpdateSkill.Opcode)
            {
                var parsed = PrivateUpdateSkill.TryParse(body);
                if (parsed is not null)
                    SkillUpdated?.Invoke(parsed.Value);
            }
            else if (op == PublicUpdatePropertyInt.Opcode)
            {
                var p = PublicUpdatePropertyInt.TryParse(body);
                if (p is not null)
                    ObjectIntPropertyUpdated?.Invoke(
                        new ObjectIntPropertyUpdate(p.Value.Guid, p.Value.Property, p.Value.Value));
            }
            else if (op == PrivateUpdatePropertyInt.Opcode)
            {
                var p = PrivateUpdatePropertyInt.TryParse(body);
                if (p is not null)
                    PlayerIntPropertyUpdated?.Invoke(
                        new PlayerIntPropertyUpdate(p.Value.Property, p.Value.Value));
            }
            else if (op == PrivateUpdatePropertyInt64.Opcode)
            {
                var p = PrivateUpdatePropertyInt64.TryParse(body);
                if (p is not null)
                    PlayerInt64PropertyUpdated?.Invoke(
                        new PlayerInt64PropertyUpdate(p.Value.Property, p.Value.Value));
            }
            else if (op == SetStackSize.Opcode)
            {
                var p = SetStackSize.TryParse(body);
                if (p is not null)
                    StackSizeUpdated?.Invoke(
                        new StackSizeUpdate(p.Value.Guid, p.Value.StackSize, p.Value.Value));
            }
            else if (op == InventoryRemoveObject.Opcode)
            {
                var p = InventoryRemoveObject.TryParse(body);
                if (p is not null) InventoryObjectRemoved?.Invoke(p.Value.Guid);
            }
            else if (op == GameEventEnvelope.Opcode)
            {
                var env = GameEventEnvelope.TryParseBorrowed(
                    bodyMemory);
                if (env is not null) GameEvents.Dispatch(env.Value);
            }
            else if (op == 0xEA60u)  // AdminEnvirons — server pushes a fog preset or sound cue
            {
                if (body.Length >= 8)
                {
                    uint envType = System.Buffers.Binary.BinaryPrimitives
                        .ReadUInt32LittleEndian(body.Slice(4, 4));
                    EnvironChanged?.Invoke(envType);
                }
            }
            else if (op == PlayPhysicsScript.Opcode)
            {
                var script = PlayPhysicsScript.TryParse(body);
                if (script is not null)
                    PlayPhysicsScriptReceived?.Invoke(script.Value);
            }
            else if (op == PlayPhysicsScriptType.Opcode)
            {
                var script = PlayPhysicsScriptType.TryParse(body);
                if (script is not null)
                    PlayPhysicsScriptTypeReceived?.Invoke(script.Value);
            }
            else if (op == SoundEvent.Opcode)
            {
                var sound = SoundEvent.TryParse(body);
                if (sound is not null)
                    SoundEventReceived?.Invoke(sound.Value);
            }
            else if (op == 0xF751u)  // PlayerTeleport — server is moving us through a portal
            {
                if (body.Length >= 6)
                {
                    ushort sequence = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
                        body.Slice(4, 2));
                    TeleportStarted?.Invoke(sequence);
                }
            }
            else if (op == ObjDescEvent.Opcode)
            {
                var parsed = ObjDescEvent.TryParse(body);
                if (parsed is not null)
                {
                    if (DumpAppearanceEnabled)
                    {
                        var md = parsed.Value.ModelData;
                        Console.WriteLine($"appearance: 0xF625 guid=0x{parsed.Value.Guid:X8} basePal=0x{(md.BasePaletteId ?? 0):X8} subPals={md.SubPalettes.Count} texChanges={md.TextureChanges.Count} animParts={md.AnimPartChanges.Count}");
                        foreach (var sp in md.SubPalettes)
                            Console.WriteLine($"  SP id=0x{sp.SubPaletteId:X8} offset={sp.Offset} length={sp.Length}");
                        foreach (var tc in md.TextureChanges)
                            Console.WriteLine($"  TC part={tc.PartIndex:D2} oldTex=0x{tc.OldTexture:X8} -> newTex=0x{tc.NewTexture:X8}");
                        foreach (var apc in md.AnimPartChanges)
                            Console.WriteLine($"  APC part={apc.PartIndex:D2} -> gfx=0x{apc.NewModelId:X8}");
                    }
                    AppearanceUpdated?.Invoke(parsed.Value);
                }
                else if (DumpAppearanceEnabled)
                {
                    Console.WriteLine($"appearance: 0xF625 PARSE FAILED body.len={body.Length}");
                }
            }
            else if (DumpOpcodesEnabled)
            {
                // ACDREAM_DUMP_OPCODES=1 — emit a one-line trace per
                // genuinely-unhandled opcode (deduped to first occurrence).
                // MUST be the LAST else-if so it doesn't intercept handled
                // opcodes when the env var is set.
                if (_seenUnhandledOpcodes.Add(op))
                    Console.WriteLine($"opcodes: unhandled 0x{op:X4} (body.len={body.Length})");
            }
        }
    }

    public void SendGameAction(byte[] gameActionBody)
    {
        if (GameActionCapture is not null)
        {
            GameActionCapture(gameActionBody);
            return;
        }
        SendGameMessage(gameActionBody);
    }

    public void SendDeleteCharacter(string accountName, int activeIndex)
    {
        ArgumentNullException.ThrowIfNull(accountName);
        if (activeIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(activeIndex));
        SendGameMessage(
            CharacterDelete.BuildRequestBody(
                accountName,
                checked((uint)activeIndex)),
            GameMessageGroup.LoginQueue);
    }

    public void SendRestoreCharacter(uint characterId)
    {
        _pendingCharGenVerification = PendingCharGenVerificationRequest.Restore;
        SendControlMessage(CharacterRestore.BuildRequestBody(characterId));
    }

    public void SendCharacterCreation(
        string accountName,
        CharacterCreate.Request request,
        ReadOnlySpan<uint> skillAdvancementClasses)
    {
        byte[] body = CharacterCreate.BuildRequestBody(
            accountName,
            request,
            skillAdvancementClasses);
        _pendingCharGenVerification = PendingCharGenVerificationRequest.Create;
        SendGameMessage(body, GameMessageGroup.LoginQueue);
    }

    internal Action<byte[]>? GameActionCapture { get; set; }

    /// <summary>LA7b unit-test seam for queue-sensitive pre-world sends.</summary>
    internal Action<byte[], GameMessageGroup>? GameMessageCapture { get; set; }

    public uint NextGameActionSequence() => ++_gameActionSequence;

    public void SendTalk(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        uint seq = NextGameActionSequence();
        byte[] body = ChatRequests.BuildTalk(seq, text);
        SendGameAction(body);
    }

    public void SendTell(string targetName, string text)
    {
        ArgumentNullException.ThrowIfNull(targetName);
        ArgumentNullException.ThrowIfNull(text);
        uint seq = NextGameActionSequence();
        byte[] body = ChatRequests.BuildTell(seq, targetName, text);
        SendGameAction(body);
    }

    public void SendChannel(uint channelId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        uint seq = NextGameActionSequence();
        byte[] body = ChatRequests.BuildChatChannel(seq, channelId, text);
        SendGameAction(body);
    }

    public void SendTeleportToLifestone()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(InteractRequests.BuildTeleToLifestone(seq));
    }

    public void SendTeleportToMarketplace()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildMarketplace(seq));
    }

    public void SendTeleportToPkArena()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildPkArena(seq));
    }

    public void SendTeleportToPkLiteArena()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildPkLiteArena(seq));
    }

    public void SendEnterPkLite()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildEnterPkLite(seq));
    }

    public void SendTeleportToHouse()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildHouseRecall(seq));
    }

    public void SendTeleportToMansion()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildMansionRecall(seq));
    }

    public void SendHouseQuery()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildHouseQuery(seq));
    }

    public void SendAbandonContract(uint contractId)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildAbandonContract(seq, contractId));
    }

    public void SendQueryAge()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildQueryAge(seq));
    }

    public void SendQueryBirth()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildQueryBirth(seq));
    }

    public void SendConfirmationResponse(uint confirmationType, uint contextId, bool accepted)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildConfirmationResponse(
            seq, confirmationType, contextId, accepted));
    }

    public void SendSuicide()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildSuicide(seq));
    }

    public void SendSetAfkMode(bool away)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildSetAfkMode(seq, away));
    }

    public void SendSetAfkMessage(string message)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildSetAfkMessage(seq, message));
    }

    public void SendEmote(string message)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildEmote(seq, message));
    }

    public void SendSoulEmote(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildSoulEmote(seq, message));
    }

    public void SendSetSingleCharacterOption(uint optionId, bool value)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(SocialActions.BuildSetSingleCharacterOption(seq, optionId, value));
    }

    public void SendSetCharacterOptions(
        uint options1,
        uint options2,
        IReadOnlyList<ShortcutEntry> shortcuts,
        IReadOnlyList<IReadOnlyList<uint>> favoriteSpells,
        IReadOnlyDictionary<uint, uint> desiredComponents,
        uint spellbookFilters)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(SocialActions.BuildSetCharacterOptions(
            seq,
            options1,
            options2,
            shortcuts,
            favoriteSpells,
            desiredComponents,
            spellbookFilters));
    }

    public void SendSetTitle(uint titleId)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(SocialActions.BuildTitleSet(seq, titleId));
    }

    public void SendAddFriend(string name)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildAddFriend(seq, name));
    }

    public void SendRemoveFriend(uint friendId)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildRemoveFriend(seq, friendId));
    }

    public void SendClearFriends()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildClearFriends(seq));
    }

    public void SendLegacyFriendsListRequest() =>
        SendControlMessage(ClientCommandRequests.BuildLegacyFriendsCommand(0u, string.Empty));

    public void SendModifyCharacterSquelch(bool add, uint characterId, string name, uint messageType)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildModifyCharacterSquelch(
            seq, add, characterId, name, messageType));
    }

    public void SendModifyAccountSquelch(bool add, string name)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildModifyAccountSquelch(seq, add, name));
    }

    public void SendModifyGlobalSquelch(bool add, uint messageType)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildModifyGlobalSquelch(seq, add, messageType));
    }

    public void SendIndexChannels()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildIndexChannels(seq));
    }

    public void SendListChannel(uint channelId)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildListChannel(seq, channelId));
    }

    public void SendOnChannel(uint channelId)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildOnChannel(seq, channelId));
    }

    public void SendOffChannel(uint channelId)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildOffChannel(seq, channelId));
    }

    public void SendRecallAllegianceHometown()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildRecallAllegianceHometown(seq));
    }

    public void SendAllegianceInfoRequest(string playerName)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildAllegianceInfoRequest(seq, playerName));
    }

    public void SendBreakAllegianceBoot(string playerName, bool accountBoot)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildBreakAllegianceBoot(
            seq, playerName, accountBoot));
    }

    public void SendAllegianceChatBoot(string playerName, string reason)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildAllegianceChatBoot(
            seq, playerName, reason));
    }

    public void SendAllegianceChatGag(string playerName, bool enabled)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildAllegianceChatGag(
            seq, playerName, enabled));
    }

    public void SendAddAllegianceBan(string playerName)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildAddAllegianceBan(seq, playerName));
    }

    public void SendRemoveAllegianceBan(string playerName)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildRemoveAllegianceBan(seq, playerName));
    }

    public void SendListAllegianceBans()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildListAllegianceBans(seq));
    }

    public void SendSetAllegianceOfficer(string playerName, uint level)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildSetAllegianceOfficer(
            seq, playerName, level));
    }

    public void SendRemoveAllegianceOfficer(string playerName)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildRemoveAllegianceOfficer(
            seq, playerName));
    }

    public void SendListAllegianceOfficers()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildListAllegianceOfficers(seq));
    }

    public void SendClearAllegianceOfficers()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildClearAllegianceOfficers(seq));
    }

    public void SendSetAllegianceOfficerTitle(uint level, string title)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildSetAllegianceOfficerTitle(
            seq, level, title));
    }

    public void SendListAllegianceOfficerTitles()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildListAllegianceOfficerTitles(seq));
    }

    public void SendClearAllegianceOfficerTitles()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildClearAllegianceOfficerTitles(seq));
    }

    public void SendQueryAllegianceName()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildQueryAllegianceName(seq));
    }

    public void SendSetAllegianceName(string name)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildSetAllegianceName(seq, name));
    }

    public void SendClearAllegianceName()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildClearAllegianceName(seq));
    }

    public void SendAllegianceLockAction(uint action)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildAllegianceLockAction(seq, action));
    }

    public void SendSetAllegianceApprovedVassal(string playerName)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildSetAllegianceApprovedVassal(
            seq, playerName));
    }

    public void SendAllegianceHouseAction(uint action)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildAllegianceHouseAction(seq, action));
    }

    public void SendQueryMotd()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildQueryMotd(seq));
    }

    public void SendSetMotd(string motd)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildSetMotd(seq, motd));
    }

    public void SendClearMotd()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildClearMotd(seq));
    }

    public void SendSetOpenHouseStatus(bool isOpen)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildSetOpenHouseStatus(seq, isOpen));
    }

    public void SendAddPermanentGuest(string playerName)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildAddPermanentGuest(seq, playerName));
    }

    public void SendRemovePermanentGuest(string playerName)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildRemovePermanentGuest(seq, playerName));
    }

    public void SendRemoveAllPermanentGuests()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildRemoveAllPermanentGuests(seq));
    }

    public void SendChangeStoragePermission(string playerName, bool enabled)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildChangeStoragePermission(
            seq, playerName, enabled));
    }

    public void SendAddAllStoragePermission()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildAddAllStoragePermission(seq));
    }

    public void SendRemoveAllStoragePermission()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildRemoveAllStoragePermission(seq));
    }

    public void SendRequestFullGuestList()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildRequestFullGuestList(seq));
    }

    public void SendBootSpecificHouseGuest(string playerName)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildBootSpecificHouseGuest(
            seq, playerName));
    }

    public void SendBootEveryone()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildBootEveryone(seq));
    }

    public void SendSetHooksVisibility(bool visible)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildSetHooksVisibility(seq, visible));
    }

    public void SendModifyAllegianceGuestPermission(bool enabled)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildModifyAllegianceGuestPermission(
            seq, enabled));
    }

    public void SendModifyAllegianceStoragePermission(bool enabled)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildModifyAllegianceStoragePermission(
            seq, enabled));
    }


    public void SendFellowshipCreate(string fellowshipName, bool shareXp)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(SocialActions.BuildFellowshipCreate(seq, fellowshipName, shareXp));
    }

    public void SendFellowshipQuit(bool disband)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(SocialActions.BuildFellowshipQuit(seq, disband));
    }

    public void SendFellowshipDismiss(uint targetGuid)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(SocialActions.BuildFellowshipDismiss(seq, targetGuid));
    }

    public void SendFellowshipRecruit(uint targetGuid)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(SocialActions.BuildFellowshipRecruit(seq, targetGuid));
    }

    public void SendFellowshipUpdateRequest(bool panelOpen)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(SocialActions.BuildFellowshipUpdateRequest(seq, panelOpen));
    }

    public void SendFellowshipAssignNewLeader(uint newLeaderGuid)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(SocialActions.BuildFellowshipAssignNewLeader(seq, newLeaderGuid));
    }

    public void SendFellowshipChangeOpenness(bool isOpen)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(SocialActions.BuildFellowshipChangeOpenness(seq, isOpen));
    }

    public void SendAllegianceSwear(uint patronGuid)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(AllegianceRequests.BuildSwear(seq, patronGuid));
    }

    public void SendAllegianceBreak(uint targetGuid)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(AllegianceRequests.BuildBreak(seq, targetGuid));
    }

    public void SendAllegianceKick(uint vassalGuid)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(AllegianceRequests.BuildKick(seq, vassalGuid));
    }

    public void SendAllegianceUpdateRequest(bool on)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(AllegianceRequests.BuildAllegianceUpdateRequest(seq, on));
    }

    public void SendListAvailableHouses(uint houseType)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildListAvailableHouses(seq, houseType));
    }

    public void SendAddPlayerPermission(string playerName)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildAddPlayerPermission(seq, playerName));
    }

    public void SendRemovePlayerPermission(string playerName)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildRemovePlayerPermission(seq, playerName));
    }

    public void SendAbandonHouse()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildAbandonHouse(seq));
    }

    public void SendClearConsent()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildClearConsent(seq));
    }

    public void SendDisplayConsent()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildDisplayConsent(seq));
    }

    public void SendRemoveConsent(string name)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildRemoveConsent(seq, name));
    }

    public void SendClearDesiredComponents()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildSetDesiredComponentLevel(
            seq, componentId: 0u, amount: uint.MaxValue));
    }

    public void SendSetDesiredComponentLevel(uint componentId, uint amount)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildSetDesiredComponentLevel(
            seq, componentId, amount));
    }

    public void SendAddSpellFavorite(uint spellId, int position, int tabIndex)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildAddSpellFavorite(
            seq, spellId, position, tabIndex));
    }

    public void SendRemoveSpellFavorite(uint spellId, int tabIndex)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildRemoveSpellFavorite(
            seq, spellId, tabIndex));
    }

    public void SendSpellbookFilter(uint filters)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildSpellbookFilter(seq, filters));
    }

    public void SendRemoveSpell(uint spellId)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(ClientCommandRequests.BuildRemoveSpell(seq, spellId));
    }

    public void SendCastUntargetedSpell(uint spellId)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(CastSpellRequest.BuildUntargeted(seq, spellId));
    }

    public void SendCastTargetedSpell(uint targetGuid, uint spellId)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(CastSpellRequest.BuildTargeted(seq, targetGuid, spellId));
    }

    public void SendChangeCombatMode(CombatMode mode)
    {
        uint seq = NextGameActionSequence();
        byte[] body = CharacterActions.BuildChangeCombatMode(
            seq,
            (CharacterActions.CombatMode)(uint)mode);
        SendGameAction(body);
    }

    public void SendRaiseAttribute(uint attrId, ulong xpSpent)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(CharacterActions.BuildRaiseAttribute(seq, attrId, xpSpent));
    }

    public void SendRaiseVital(uint vitalId, ulong xpSpent)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(CharacterActions.BuildRaiseVital(seq, vitalId, xpSpent));
    }

    public void SendRaiseSkill(uint skillId, ulong xpSpent)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(CharacterActions.BuildRaiseSkill(seq, skillId, xpSpent));
    }

    public void SendTrainSkill(uint skillId, uint credits)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(CharacterActions.BuildTrainSkill(seq, skillId, credits));
    }

    public void SendAddShortcut(ShortcutEntry entry)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(InventoryActions.BuildAddShortcut(seq, entry));
    }

    public void SendRemoveShortcut(uint index)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(InventoryActions.BuildRemoveShortcut(seq, index));
    }

    /// <summary>Send DropItem (0x001B) — drop an item on the ground.</summary>
    public void SendDropItem(uint itemGuid)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(InventoryActions.BuildDropItem(seq, itemGuid));
    }


    public void SendOpenTradeNegotiations(uint partnerGuid)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(TradeRequests.BuildOpenTradeNegotiations(seq, partnerGuid));
    }

    public void SendCloseTradeNegotiations()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(TradeRequests.BuildCloseTradeNegotiations(seq));
    }

    public void SendAddToTrade(uint itemGuid, uint tradeSlot = 0u)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(TradeRequests.BuildAddToTrade(seq, itemGuid, tradeSlot));
    }

    public void SendAcceptTrade(
        uint partnerGuid,
        double tradeStamp,
        uint tradeStatus,
        uint initiatorGuid,
        bool initiatorAccepts,
        bool partnerAccepts)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(TradeRequests.BuildAcceptTrade(
            seq, partnerGuid, tradeStamp, tradeStatus,
            initiatorGuid, initiatorAccepts, partnerAccepts));
    }

    public void SendDeclineTrade()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(TradeRequests.BuildDeclineTrade(seq));
    }

    public void SendResetTrade()
    {
        uint seq = NextGameActionSequence();
        SendGameAction(TradeRequests.BuildResetTrade(seq));
    }

    public void SendGiveObject(uint targetGuid, uint itemGuid, uint amount)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(InventoryActions.BuildGiveObjectRequest(
            seq, targetGuid, itemGuid, amount));
    }

    /// <summary>Send GetAndWieldItem (0x001A) — equip an item to an equip slot.</summary>
    public void SendGetAndWieldItem(uint itemGuid, uint equipMask)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(InventoryActions.BuildGetAndWieldItem(seq, itemGuid, equipMask));
    }

    public void SendNoLongerViewingContents(uint containerGuid)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(InventoryActions.BuildNoLongerViewingContents(seq, containerGuid));
    }

    public void SendUse(uint targetGuid)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(InteractRequests.BuildUse(seq, targetGuid));
    }

    public void SendUseWithTarget(uint sourceGuid, uint targetGuid)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(InteractRequests.BuildUseWithTarget(seq, sourceGuid, targetGuid));
    }

    public void SendBuy(uint vendorGuid, uint itemGuid, int amount, uint alternateCurrencyId)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(VendorRequests.BuildBuy(seq, vendorGuid, amount, itemGuid, alternateCurrencyId));
    }

    public void SendBuy(
        uint vendorGuid,
        IReadOnlyList<(int Amount, uint ItemGuid)> items,
        uint alternateCurrencyId)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(VendorRequests.BuildBuy(seq, vendorGuid, items, alternateCurrencyId));
    }

    public void SendSell(uint vendorGuid, IReadOnlyList<(int Amount, uint ItemGuid)> items)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(VendorRequests.BuildSell(seq, vendorGuid, items));
    }

    public void SendAppraise(uint targetGuid)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(AppraiseRequest.Build(seq, targetGuid));
    }

    public void SendSetInscription(uint itemGuid, string inscription)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(InventoryActions.BuildSetInscription(
            seq,
            itemGuid,
            inscription));
    }

    public void SendPutItemInContainer(uint itemGuid, uint containerGuid, int placement)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(InteractRequests.BuildPickUp(seq, itemGuid, containerGuid, placement));
    }

    public void SendStackableMerge(uint sourceGuid, uint targetGuid, uint amount)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(InventoryActions.BuildStackableMerge(seq, sourceGuid, targetGuid, amount));
    }

    public void SendStackableSplitToContainer(
        uint stackGuid, uint containerGuid, uint placement, uint amount)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(InventoryActions.BuildStackableSplitToContainer(
            seq, stackGuid, containerGuid, placement, amount));
    }

    public void SendStackableSplitTo3D(uint stackGuid, uint amount)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(InventoryActions.BuildStackableSplitTo3D(seq, stackGuid, amount));
    }

    public void SendSalvage(uint toolGuid, IReadOnlyList<uint> itemGuids)
    {
        uint seq = NextGameActionSequence();
        SendGameAction(InventoryActions.BuildCreateTinkeringTool(
            seq,
            toolGuid,
            itemGuids));
    }

    public void SendQueryHealth(uint targetGuid)
    {
        uint seq = NextGameActionSequence();
        byte[] body = SocialActions.BuildQueryHealth(seq, targetGuid);
        SendGameAction(body);
    }

    public void SendQueryItemMana(uint itemGuid)
    {
        uint seq = NextGameActionSequence();
        byte[] body = SocialActions.BuildQueryItemMana(seq, itemGuid);
        SendGameAction(body);
    }

    public void RequestLinkStatusPing()
    {
        Volatile.Write(ref _lastPingRequestTicks, Stopwatch.GetTimestamp());
        uint seq = NextGameActionSequence();
        SendGameAction(SocialActions.BuildPingRequest(seq));
    }

    public void SendMeleeAttack(uint targetGuid, AttackHeight attackHeight, float powerLevel)
    {
        uint seq = NextGameActionSequence();
        byte[] body = AttackTargetRequest.BuildMelee(
            seq,
            targetGuid,
            (uint)attackHeight,
            powerLevel);
        SendGameAction(body);
    }

    public void SendMissileAttack(uint targetGuid, AttackHeight attackHeight, float accuracyLevel)
    {
        uint seq = NextGameActionSequence();
        byte[] body = AttackTargetRequest.BuildMissile(
            seq,
            targetGuid,
            (uint)attackHeight,
            accuracyLevel);
        SendGameAction(body);
    }

    public void SendCancelAttack()
    {
        uint seq = NextGameActionSequence();
        byte[] body = AttackTargetRequest.BuildCancel(seq);
        SendGameAction(body);
    }

    public void SendTurbineChatTo(
        uint roomId,
        uint chatType,
        uint dispatchType,
        uint senderGuid,
        string text,
        uint cookie)
    {
        ArgumentNullException.ThrowIfNull(text);

        _ = dispatchType;

        var payload = new TurbineChat.Payload.RequestSendToRoomById(
            ContextId:     cookie,
            RoomId:        roomId,
            Message:       text,
            ExtraDataSize: 0x0Cu,
            SenderId:      senderGuid,
            HResult:       0,
            ChatType:      chatType);

        byte[] body = TurbineChat.Build(
            blobType:      TurbineChat.BlobType.RequestBinary,
            dispatchType:  TurbineChat.DispatchType.SendToRoomById,
            targetType:    1u,
            targetId:      0u,
            transportType: 0u,
            transportId:   0u,
            cookie:        0u,
            payload:       payload);

        SendGameAction(body);
    }

    private void SendGameMessage(byte[] gameMessageBody) =>
        SendGameMessage(gameMessageBody, GameMessageGroup.UIQueue);

    private void SendControlMessage(byte[] gameMessageBody) =>
        SendGameMessage(gameMessageBody, GameMessageGroup.ControlQueue);

    private void SendGameMessage(byte[] gameMessageBody, GameMessageGroup queue)
    {
        if (GameMessageCapture is { } capture)
        {
            capture(gameMessageBody, queue);
            return;
        }
        if (NetDiagnostics.ProbeNet)
            ProbeNetLogOutbound(gameMessageBody, queue);
        try
        {
            ReliableTransport transport = _transport
                ?? throw new InvalidOperationException(
                    "reliable send before transport negotiation — "
                    + "Connect() must seed ISAAC first");
            transport.Outbound.SendGameMessage(gameMessageBody, queue);
        }
        catch (Exception ex) when (ProbeNetLogOutboundFault(ex))
        {
            throw;
        }
        if (NetDiagnostics.ProbeNet)
            Interlocked.Increment(ref _probeSendWindow);
    }

    private void ProbeNetLogOutbound(byte[] body, GameMessageGroup queue)
    {
        uint op = body.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(body)
            : 0u;
        string detail = string.Empty;
        if (op == 0xF7B1 && body.Length >= 12)
        {
            uint gseq = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
            uint act  = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));
            detail = $" act=0x{act:X4} gseq={gseq}";
        }
        Console.WriteLine(
            $"[net-out] op=0x{op:X4}{detail} q={queue}"
            + $" fseq={_transport?.Outbound.FragmentSequence ?? 0}"
            + $" pseq={_transport?.Outbound.PeekNextPacketSequence ?? 0}"
            + $" len={body.Length}"
            + $" tid={Environment.CurrentManagedThreadId} st={CurrentState}");
    }

    private bool ProbeNetLogOutboundFault(Exception ex)
    {
        if (NetDiagnostics.ProbeNet)
        {
            Console.WriteLine(
                $"[net-out-EX] {ex.GetType().Name}: {ex.Message}"
                + $" tid={Environment.CurrentManagedThreadId} st={CurrentState}");
        }
        return false;
    }

    private void SetConnectionProgress(ConnectionProgress progress)
    {
        if (ConnectionProgress.Phase is ConnectionPhase.Unsupported or ConnectionPhase.Failed
            && progress.Phase != ConnectionPhase.Connecting)
            return;
        ConnectionProgress = progress;
        ConnectionProgressChanged?.Invoke(progress);
    }

    private void ThrowIfDataCheckFailed()
    {
        if (ConnectionProgress.Phase == ConnectionPhase.Unsupported)
        {
            Transition(State.Failed);
            throw new UnsupportedDataUpdateException();
        }
        if (ConnectionProgress.Phase == ConnectionPhase.Failed)
        {
            Transition(State.Failed);
            throw new InvalidDataException(ConnectionProgress.Error);
        }
    }

    private void Transition(State next)
    {
        if (CurrentState == next) return;
        CurrentState = next;
        StateChanged?.Invoke(next);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        _pendingCharGenVerification = PendingCharGenVerificationRequest.None;

        SessionShutdownPlan shutdown = BuildShutdownPlan(
            CurrentState,
            _transportNegotiated,
            _activeCharacterId);

        Interlocked.Exchange(ref _characterLogOffConfirmed, 0);
        ShutdownExecutionResult result = ExecuteShutdownWire(
            shutdown,
            _activeCharacterId,
            _sessionClientId,
            _sessionIteration,
            SendGameMessage,
            WaitForCharacterLogOffConfirmation,
            packet => _net.Send(packet),
            TimeSpan.FromSeconds(35));

        if (result.CharacterLogOffSent)
        {
            Console.WriteLine(
                $"[session] graceful logout requested character=0x{_activeCharacterId:X8}");
            if (result.ConfirmationReceived)
                Console.WriteLine("[session] graceful logout confirmed");
            else if (result.CharacterLogOffError is null)
                Console.Error.WriteLine(
                    "[session] graceful logout confirmation timed out; disconnecting transport");
        }
        if (result.CharacterLogOffError is not null)
            Console.Error.WriteLine(
                $"[session] graceful logout failed: {result.CharacterLogOffError.Message}");
        if (result.TransportDisconnectError is not null)
            Console.Error.WriteLine(
                $"[session] transport disconnect failed: {result.TransportDisconnectError.Message}");

        _netCancel.Cancel();
        _netReceiveTask?.GetAwaiter().GetResult();
        _inboundQueue.Writer.TryComplete();
        while (_inboundQueue.Reader.TryRead(
                   out PooledInboundDatagram queued))
        {
            ReturnInboundDatagram(queued);
        }
        _netCancel.Dispose();

        if (NetDiagnostics.ProbeNet && _transport is { } finalTransport)
        {
            TransportStats finalStats = finalTransport.Stats;
            Console.WriteLine(
                $"[net-final] resends={finalStats.ResendsSent}"
                + $" nak-in={finalStats.NakRequestsReceived}"
                + $" nak-out={finalStats.NaksSent}"
                + $" rej-in={finalStats.RejectsReceived}"
                + $" acks-out={finalStats.AcksSent}"
                + $" acks-in={finalStats.AcksConsumed}"
                + $" dup-drop={finalStats.InboundDupsDropped}"
                + $" sanity-drop={finalStats.InboundSanityDrops}"
                + $" cksum-fail={finalStats.ChecksumFailures}"
                + $" parked={finalStats.KeysParked}"
                + $" reclaimed={finalStats.RejectWordsReclaimed}"
                + $" uncached-nak={finalStats.UncachedNakIds}"
                + $" cache={finalStats.CacheDepth}"
                + $" nakset={finalTransport.Inbound.NakCount}");
        }

        _transport?.Dispose();
        _net.Dispose();
        Transition(State.Disconnected);
    }

    internal readonly record struct SessionShutdownPlan(
        bool RequestCharacterLogOff,
        bool SendTransportDisconnect);

    internal readonly record struct ShutdownExecutionResult(
        bool CharacterLogOffSent,
        bool ConfirmationReceived,
        bool TransportDisconnectSent,
        Exception? CharacterLogOffError,
        Exception? TransportDisconnectError);

    internal static SessionShutdownPlan BuildShutdownPlan(
        State state,
        bool transportNegotiated,
        uint activeCharacterId) =>
        new(
            RequestCharacterLogOff:
                transportNegotiated
                && state == State.InWorld
                && activeCharacterId != 0,
            SendTransportDisconnect: transportNegotiated);

    internal static ShutdownExecutionResult ExecuteShutdownWire(
        SessionShutdownPlan plan,
        uint activeCharacterId,
        ushort sessionClientId,
        ushort sessionIteration,
        Action<byte[]> sendGameMessage,
        Func<TimeSpan, bool> waitForConfirmation,
        Action<byte[]> sendTransportDatagram,
        TimeSpan confirmationTimeout)
    {
        ArgumentNullException.ThrowIfNull(sendGameMessage);
        ArgumentNullException.ThrowIfNull(waitForConfirmation);
        ArgumentNullException.ThrowIfNull(sendTransportDatagram);

        bool characterLogOffSent = false;
        bool confirmationReceived = false;
        bool transportDisconnectSent = false;
        Exception? characterError = null;
        Exception? transportError = null;

        if (plan.RequestCharacterLogOff)
        {
            try
            {
                sendGameMessage(CharacterLogOff.BuildRequestBody(activeCharacterId));
                characterLogOffSent = true;
                confirmationReceived = waitForConfirmation(confirmationTimeout);
            }
            catch (Exception error)
            {
                characterError = error;
            }
        }

        if (plan.SendTransportDisconnect)
        {
            try
            {
                sendTransportDatagram(TransportDisconnect.Build(
                    sessionClientId,
                    sessionIteration));
                transportDisconnectSent = true;
            }
            catch (Exception error)
            {
                transportError = error;
            }
        }

        return new ShutdownExecutionResult(
            characterLogOffSent,
            confirmationReceived,
            transportDisconnectSent,
            characterError,
            transportError);
    }

    private bool WaitForCharacterLogOffConfirmation(TimeSpan timeout) =>
        WaitForCharacterLogOffConfirmation(
            _inboundQueue.Reader,
            timeout,
            datagram =>
            {
                ProcessDatagram(
                    datagram.Memory,
                    dispatchWorldEvents: false);
                SweepTransport();
                return Volatile.Read(ref _characterLogOffConfirmed) != 0;
            },
            ReturnInboundDatagram);

    internal static bool WaitForCharacterLogOffConfirmation<T>(
        ChannelReader<T> reader,
        TimeSpan timeout,
        Func<T, bool> processAndCheckConfirmation,
        Action<T>? release = null,
        Action? periodicWork = null,
        TimeSpan? periodicInterval = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(processAndCheckConfirmation);
        TimeSpan cadence = periodicInterval ?? TimeSpan.FromMilliseconds(25);
        if (periodicWork is not null && cadence <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(periodicInterval));
        using var timeoutSource = new CancellationTokenSource(timeout);

        long started = Stopwatch.GetTimestamp();
        bool bounded = timeout >= TimeSpan.Zero;
        bool Expired() => bounded && Stopwatch.GetElapsedTime(started) >= timeout;

        try
        {
            while (!timeoutSource.IsCancellationRequested && !Expired())
            {
                while (reader.TryRead(out T? item))
                {
                    if (timeoutSource.IsCancellationRequested || Expired())
                    {
                        release?.Invoke(item);
                        return false;
                    }

                    bool confirmed;
                    try
                    {
                        confirmed = processAndCheckConfirmation(item);
                    }
                    finally
                    {
                        release?.Invoke(item);
                    }
                    if (confirmed)
                        return true;
                }

                periodicWork?.Invoke();
                if (timeoutSource.IsCancellationRequested || Expired())
                    return false;

                bool canRead;
                if (periodicWork is null)
                {
                    canRead = reader.WaitToReadAsync(timeoutSource.Token)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                }
                else
                {
                    TimeSpan wait = cadence;
                    if (bounded)
                    {
                        TimeSpan remaining = timeout
                            - Stopwatch.GetElapsedTime(started);
                        if (remaining <= TimeSpan.Zero)
                            return false;
                        if (remaining < wait)
                            wait = remaining;
                    }

                    using var sliceSource =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            timeoutSource.Token);
                    sliceSource.CancelAfter(wait);
                    try
                    {
                        canRead = reader.WaitToReadAsync(sliceSource.Token)
                            .AsTask()
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (OperationCanceledException)
                        when (!timeoutSource.IsCancellationRequested
                            && !Expired())
                    {
                        continue;
                    }
                }
                if (!canRead)
                    return false;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ChannelClosedException)
        {
            return false;
        }

        return false;
    }
}
