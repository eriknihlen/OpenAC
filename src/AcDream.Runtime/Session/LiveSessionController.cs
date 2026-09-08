using System.Net;
using System.Net.Sockets;
using AcDream.Core.CharGen;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;

namespace AcDream.Runtime.Session;

public enum LiveSessionStartStatus
{
    Disabled,
    MissingCredentials,
    NoCharacters,
    Connected,
    Deferred,
    Failed,
    AwaitingCharacterSelection,
    ProbeComplete,
}

public readonly record struct LiveSessionOwnershipSnapshot(
    bool IsDisposed,
    bool IsDisposeRequested,
    bool IsInWorld,
    bool HasActiveSession,
    bool HasRetiredSession,
    bool HasPendingInitialReset,
    bool HasPendingOperation,
    int OperationDepth,
    ulong Generation,
    RuntimeTeardownStage LastTeardownStages)
{
    public bool IsConverged =>
        IsDisposed
        && IsDisposeRequested
        && !IsInWorld
        && !HasActiveSession
        && !HasRetiredSession
        && !HasPendingInitialReset
        && !HasPendingOperation
        && OperationDepth == 0
        && LastTeardownStages == RuntimeTeardownStage.Complete;
}

public sealed record LiveSessionCharacterSelection(
    int ActiveIndex,
    uint CharacterId,
    string CharacterName,
    string AccountName);

public sealed record LiveSessionStartResult(
    LiveSessionStartStatus Status,
    LiveSessionCharacterSelection? Selection = null,
    Exception? Error = null);

public readonly record struct LiveSessionRosterEntry(
    uint Id,
    string Name,
    uint SecondsGreyedOut);

public sealed record LiveSessionRosterReport(
    string AccountName,
    int SlotCount,
    IReadOnlyList<LiveSessionRosterEntry> Entries);

public interface ILiveSessionLifecycleHost
{
    LiveSessionBinding BindSession(WorldSession session);
    void ResetSessionState(RuntimeGenerationToken retiringGeneration);
    void ReportConnecting(string host, int port, string user);
    void ReportConnected();
    void ReportRoster(LiveSessionRosterReport roster);
    void ApplySelectedCharacter(LiveSessionCharacterSelection selection);
    void ApplyEnteredWorld(LiveSessionCharacterSelection selection);
    void DetachSession(WorldSession session);

    void ApplyCharacterCreated(RuntimeCharacterCreationIdentity identity) { }

    void ApplyCreationFailed(RuntimeCharacterCreationRejection rejection) { }
}

public sealed class LiveSessionBinding : IDisposable
{
    private readonly Action _activateCommands;
    private readonly Action _deactivateCommands;
    private readonly Action _detachEvents;
    private bool _commandsDeactivated;
    private bool _eventsDetached;
    private bool _commandsActivated;

    public LiveSessionBinding(
        WorldSession session,
        Action activateCommands,
        Action deactivateCommands,
        Action detachEvents)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _activateCommands = activateCommands ?? throw new ArgumentNullException(nameof(activateCommands));
        _deactivateCommands = deactivateCommands ?? throw new ArgumentNullException(nameof(deactivateCommands));
        _detachEvents = detachEvents ?? throw new ArgumentNullException(nameof(detachEvents));
    }

    public WorldSession Session { get; }
    public bool CommandsDeactivated => _commandsDeactivated;
    public bool EventsDetached => _eventsDetached;

    public void ActivateCommands()
    {
        if (_commandsDeactivated || _eventsDetached)
            throw new ObjectDisposedException(nameof(LiveSessionBinding));
        if (_commandsActivated)
            return;
        _activateCommands();
        if (_commandsDeactivated || _eventsDetached)
            return;
        _commandsActivated = true;
    }

    public void Dispose()
    {
        if (!_commandsDeactivated)
        {
            _deactivateCommands();
            _commandsDeactivated = true;
        }
        if (!_eventsDetached)
        {
            _detachEvents();
            _eventsDetached = true;
        }
    }
}

public interface ILiveSessionOperations
{
    IPEndPoint ResolveEndpoint(string host, int port);
    WorldSession CreateSession(IPEndPoint endpoint);
    void Connect(WorldSession session, string user, string password);
    CharacterList.Parsed? GetCharacters(WorldSession session);
    ServerName.Parsed? GetServerInfo(WorldSession session) => session.ServerInfo;
    void StartCharacterSelectionReceive(WorldSession session) =>
        session.StartCharacterSelectionReceive();
    void EnterWorld(WorldSession session, int activeCharacterIndex);

    void EnterWorldByGuid(
        WorldSession session,
        uint characterGuid,
        string accountName) =>
        session.EnterWorld(characterGuid, accountName);

    void DeleteCharacter(
        WorldSession session,
        string accountName,
        int activeCharacterIndex) =>
        session.SendDeleteCharacter(accountName, activeCharacterIndex);
    void RestoreCharacter(WorldSession session, uint characterId) =>
        session.SendRestoreCharacter(characterId);
    void CreateCharacter(
        WorldSession session,
        string accountName,
        CharacterCreate.Request request,
        ReadOnlySpan<uint> skillAdvancementClasses) =>
        session.SendCharacterCreation(accountName, request, skillAdvancementClasses);
    void Tick(WorldSession session);
    void DisposeSession(WorldSession session);

    void RequestCharacterLogOff(WorldSession session) =>
        session.RequestCharacterLogOff();

    void ReturnToCharacterSelect(WorldSession session) =>
        session.ReturnToCharacterSelect();
}

internal sealed class ProductionLiveSessionOperations : ILiveSessionOperations
{
    public static ProductionLiveSessionOperations Instance { get; } = new();

    private ProductionLiveSessionOperations() { }

    public IPEndPoint ResolveEndpoint(string host, int port)
    {
        IPAddress ip;
        if (!IPAddress.TryParse(host, out ip!))
        {
            IPAddress[] addresses = Dns.GetHostAddresses(host);
            ip = Array.Find(
                    addresses,
                    static address => address.AddressFamily == AddressFamily.InterNetwork)
                ?? (addresses.Length != 0
                    ? addresses[0]
                    : throw new InvalidOperationException(
                        $"DNS resolved no addresses for '{host}'"));
            Console.WriteLine($"live: resolved {host} → {ip}");
        }
        return new IPEndPoint(ip, port);
    }

    public WorldSession CreateSession(IPEndPoint endpoint) => new(endpoint);

    public void Connect(WorldSession session, string user, string password) =>
        session.Connect(user, password);

    public CharacterList.Parsed? GetCharacters(WorldSession session) => session.Characters;

    public void StartCharacterSelectionReceive(WorldSession session) =>
        session.StartCharacterSelectionReceive();

    public void EnterWorld(WorldSession session, int activeCharacterIndex) =>
        session.EnterWorld(activeCharacterIndex);

    public void Tick(WorldSession session) => session.Tick();

    public void DisposeSession(WorldSession session) => session.Dispose();
}

public sealed class LiveSessionController
    : IDisposable,
      IRuntimeLiveSessionFramePhase,
      IRuntimeCharacterSelectionCommands,
      IRuntimeCharacterCreationCommands
{
    private sealed class CharacterSelectionWireBinding : IDisposable
    {
        private WorldSession? _session;
        private readonly Action<CharacterList.Parsed> _roster;
        private readonly Action _delete;
        private readonly Action<CharacterRestore.Parsed> _restore;
        private readonly Action<CharacterError.Parsed> _error;
        private readonly Action<ServerName.Parsed> _worldName;
        private readonly Action<CharGenVerificationResponse.Parsed> _created;

        public CharacterSelectionWireBinding(
            WorldSession session,
            Action<CharacterList.Parsed> roster,
            Action delete,
            Action<CharacterRestore.Parsed> restore,
            Action<CharacterError.Parsed> error,
            Action<ServerName.Parsed> worldName,
            Action<CharGenVerificationResponse.Parsed> created)
        {
            _session = session;
            _roster = roster;
            _delete = delete;
            _restore = restore;
            _error = error;
            _worldName = worldName;
            _created = created;
            session.CharacterListReceived += roster;
            session.CharacterDeleteAcknowledged += delete;
            session.CharacterRestoreReceived += restore;
            session.CharacterErrorReceived += error;
            session.ServerNameReceived += worldName;
            session.CharacterCreateResponseReceived += created;
        }

        public bool IsDisposed => _session is null;

        public void Dispose()
        {
            WorldSession? session = Interlocked.Exchange(ref _session, null);
            if (session is null)
                return;
            session.CharacterListReceived -= _roster;
            session.CharacterDeleteAcknowledged -= _delete;
            session.CharacterRestoreReceived -= _restore;
            session.CharacterErrorReceived -= _error;
            session.ServerNameReceived -= _worldName;
            session.CharacterCreateResponseReceived -= _created;
        }
    }

    private sealed class SessionScope(
        WorldSession session,
        ILiveSessionLifecycleHost host,
        RuntimeGenerationToken generation)
    {
        private int _teardownStage;

        public WorldSession Session { get; } = session;
        public ILiveSessionLifecycleHost Host { get; } = host;
        public RuntimeGenerationToken Generation { get; } = generation;
        public LiveSessionBinding? Binding { get; set; }
        public CharacterSelectionWireBinding? CharacterSelectionBinding
        {
            get;
            set;
        }
        public bool HostAttached { get; set; }
        public RuntimeTeardownStage CompletedStages { get; private set; }

        public void DrainTeardown(ILiveSessionOperations operations)
        {
            if (_teardownStage == 0)
            {
                try
                {
                    Binding?.Dispose();
                    CharacterSelectionBinding?.Dispose();
                }
                finally
                {
                    if (Binding is null || Binding.CommandsDeactivated)
                        CompletedStages |= RuntimeTeardownStage.CommandsInert;
                    if ((Binding is null || Binding.EventsDetached)
                        && (CharacterSelectionBinding is null
                            || CharacterSelectionBinding.IsDisposed))
                    {
                        CompletedStages |= RuntimeTeardownStage.InboundDetached;
                    }
                }
                _teardownStage = 1;
            }
            if (_teardownStage == 1)
            {
                operations.DisposeSession(Session);
                CompletedStages |= RuntimeTeardownStage.TransportDisposed;
                _teardownStage = 2;
            }
            if (_teardownStage == 2)
            {
                if (HostAttached)
                    Host.DetachSession(Session);
                _teardownStage = 3;
            }
            if (_teardownStage == 3)
            {
                Host.ResetSessionState(Generation);
                CompletedStages |= RuntimeTeardownStage.HostReset;
                _teardownStage = 4;
            }
        }

        public bool IsTeardownComplete => _teardownStage == 4;
    }

    private enum PendingKind
    {
        Stop,
        Reconnect,
        Dispose,
    }

    private sealed record PendingOperation(
        PendingKind Kind,
        LiveSessionConnectOptions? Options = null,
        ILiveSessionLifecycleHost? Host = null);

    private sealed record PendingHostReset(
        ILiveSessionLifecycleHost Host,
        RuntimeGenerationToken Generation);

    private readonly object _gate = new();
    private readonly ILiveSessionOperations _operations;
    private SessionScope? _scope;
    private SessionScope? _retiredScope;
    private PendingHostReset? _pendingInitialReset;
    private PendingOperation? _pendingOperation;
    private int _operationDepth;
    private bool _inWorld;
    private bool _disposeRequested;
    private bool _disposed;
    private ulong _generation;
    private RuntimeTeardownStage _lastTeardownStages;

    private int _createsSinceCharacterList;
    private LiveSessionCharacterSelection? _activeSelection;
    private uint _nextLoginCharacterId;
    private Action<WorldSession>? _autoSaveTickHook;
    private Action<WorldSession>? _preLogoffFlushHook;

    public LiveSessionController()
        : this(ProductionLiveSessionOperations.Instance, null)
    {
    }

    public LiveSessionController(
        ILiveSessionOperations operations,
        TimeProvider? timeProvider = null,
        ChargenOptions? chargenOptions = null,
        Random? random = null)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        CharacterSelectionState = new RuntimeCharacterSelectionState(
            timeProvider);
        CharacterCreationState = new RuntimeCharacterCreationState(
            chargenOptions ?? ChargenOptions.Empty, random);
    }

    public RuntimeCharacterSelectionState CharacterSelectionState { get; }

    public IRuntimeCharacterSelectionView CharacterSelection =>
        CharacterSelectionState.View;

    public RuntimeCharacterCreationState CharacterCreationState { get; }

    public IRuntimeCharacterCreationView CharacterCreation =>
        CharacterCreationState.View;

    public WorldSession? CurrentSession
    {
        get { lock (_gate) return _scope?.Session; }
    }

    public bool IsInWorld
    {
        get { lock (_gate) return _inWorld; }
    }

    public ulong SessionGeneration
    {
        get { lock (_gate) return _generation; }
    }

    public RuntimeGenerationToken Generation
    {
        get { lock (_gate) return new RuntimeGenerationToken(_generation); }
    }

    public uint NextLoginCharacterId
    {
        get { lock (_gate) return _nextLoginCharacterId; }
    }

    public bool TrySetNextLogin(uint characterId)
    {
        lock (_gate)
        {
            if (_disposed
                || _disposeRequested
                || _scope is null
                || characterId == 0u
                || !CharacterSelectionState.View.TryGet(
                    characterId,
                    out RuntimeCharacterSelectionEntry character)
                || !character.CanEnter)
            {
                return false;
            }
            _nextLoginCharacterId = characterId;
            return true;
        }
    }

    public bool ClearNextLogin()
    {
        lock (_gate)
        {
            if (_disposed)
                return false;
            _nextLoginCharacterId = 0u;
            return true;
        }
    }

    internal void ConfigureAutoSaveTick(Action<WorldSession> hook) =>
        _autoSaveTickHook = hook ?? throw new ArgumentNullException(nameof(hook));

    internal void ConfigurePreLogoffFlush(Action<WorldSession> hook) =>
        _preLogoffFlushHook = hook ?? throw new ArgumentNullException(nameof(hook));

    public bool IsDisposalComplete
    {
        get { lock (_gate) return _disposed; }
    }

    public LiveSessionOwnershipSnapshot CaptureOwnership()
    {
        lock (_gate)
        {
            return new LiveSessionOwnershipSnapshot(
                _disposed,
                _disposeRequested,
                _inWorld,
                _scope is not null,
                _retiredScope is not null,
                _pendingInitialReset is not null,
                _pendingOperation is not null,
                _operationDepth,
                _generation,
                _lastTeardownStages);
        }
    }

    public LiveSessionStartResult Start(
        LiveSessionConnectOptions options,
        ILiveSessionLifecycleHost host)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(host);
        lock (_gate)
        {
            ThrowIfDisposing();
            if (_operationDepth != 0)
                return new LiveSessionStartResult(LiveSessionStartStatus.Deferred);
            if (_inWorld)
                return ConnectedResult();
            if (_scope is not null)
            {
                return new LiveSessionStartResult(
                    LiveSessionStartStatus.AwaitingCharacterSelection);
            }
            return RunTopLevel(() => StartCore(options, host, resetHost: true));
        }
    }

    public LiveSessionStartResult Reconnect(
        LiveSessionConnectOptions options,
        ILiveSessionLifecycleHost host)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(host);
        lock (_gate)
        {
            ThrowIfDisposing();
            if (_operationDepth != 0)
            {
                Schedule(new PendingOperation(PendingKind.Reconnect, options, host));
                return new LiveSessionStartResult(LiveSessionStartStatus.Deferred);
            }
            return RunTopLevel(() => ReconnectCore(options, host));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_operationDepth != 0)
            {
                Schedule(new PendingOperation(PendingKind.Stop));
                return;
            }
            RunTopLevel(StopCore);
        }
    }

    public RuntimeTeardownAcknowledgement Stop(
        RuntimeGenerationToken expectedGeneration)
    {
        lock (_gate)
        {
            RuntimeGenerationToken current = new(_generation);
            if (_disposed)
            {
                return new RuntimeTeardownAcknowledgement(
                    expectedGeneration,
                    current,
                    RuntimeCommandStatus.Inactive,
                    _lastTeardownStages);
            }
            if (expectedGeneration != current)
            {
                return new RuntimeTeardownAcknowledgement(
                    expectedGeneration,
                    current,
                    RuntimeCommandStatus.StaleGeneration,
                    RuntimeTeardownStage.None);
            }

            try
            {
                Stop();
                return new RuntimeTeardownAcknowledgement(
                    expectedGeneration,
                    new RuntimeGenerationToken(_generation),
                    RuntimeCommandStatus.Accepted,
                    _lastTeardownStages);
            }
            catch (Exception error)
            {
                return new RuntimeTeardownAcknowledgement(
                    expectedGeneration,
                    new RuntimeGenerationToken(_generation),
                    RuntimeCommandStatus.Rejected,
                    _retiredScope?.CompletedStages ?? _lastTeardownStages,
                    error);
            }
        }
    }

    public void Tick()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_scope is null || _operationDepth != 0)
                return;

            RunTopLevel(() =>
            {
                SessionScope scope = _scope;
                ulong generation = _generation;
                try
                {
                    _operations.Tick(scope.Session);
                    if (!IsCurrent(scope, generation))
                        return;
                    CharacterSelectionState.SweepRestoreCorrelation();
                }
                catch (Exception tickError)
                {
                    Exception error = StopAfterFailure(tickError);
                    if (ReferenceEquals(error, tickError))
                        throw;
                    throw error;
                }

                if (_inWorld)
                    InvokeAutoSaveTick(scope.Session);
            });
        }
    }

    private void InvokeAutoSaveTick(WorldSession session)
    {
        if (_autoSaveTickHook is not { } hook)
            return;
        try
        {
            hook(session);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                $"live: auto-save character-options tick failed: {error.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposeRequested = true;
            if (_operationDepth != 0)
            {
                Schedule(new PendingOperation(PendingKind.Dispose));
                return;
            }

            RunTopLevel(DisposeCore);
        }
    }

    private LiveSessionStartResult ReconnectCore(
        LiveSessionConnectOptions options,
        ILiveSessionLifecycleHost host)
    {
        ILiveSessionLifecycleHost? oldHost =
            _scope?.Host
            ?? _retiredScope?.Host
            ?? _pendingInitialReset?.Host;
        ulong generationBeforeStop = _generation;
        try
        {
            StopCore();
        }
        catch (Exception error)
        {
            return new LiveSessionStartResult(LiveSessionStartStatus.Failed, Error: error);
        }
        if (_generation != unchecked(generationBeforeStop + 1))
            return new LiveSessionStartResult(LiveSessionStartStatus.Deferred);
        return StartCore(options, host, resetHost: !ReferenceEquals(oldHost, host));
    }

    private LiveSessionStartResult StartCore(
        LiveSessionConnectOptions options,
        ILiveSessionLifecycleHost host,
        bool resetHost)
    {
        RuntimeGenerationToken resetGeneration = new(_generation);
        ulong generation = ++_generation;
        RuntimeGenerationToken activeGeneration = new(generation);
        CharacterSelectionState.Reset(activeGeneration);
        CharacterCreationState.Reset(activeGeneration);
        _createsSinceCharacterList = 0;
        try
        {
            DrainRetiredScope();
            if (_generation != generation)
                return new LiveSessionStartResult(LiveSessionStartStatus.Deferred);
            if (resetHost)
            {
                ResetHostBeforeStart(
                    host,
                    generation,
                    resetGeneration);
            }
            if (_generation != generation)
                return new LiveSessionStartResult(LiveSessionStartStatus.Deferred);
        }
        catch (Exception error)
        {
            return new LiveSessionStartResult(LiveSessionStartStatus.Failed, Error: error);
        }

        if (!options.Enabled)
            return new LiveSessionStartResult(LiveSessionStartStatus.Disabled);
        if (string.IsNullOrEmpty(options.User) || string.IsNullOrEmpty(options.Password))
            return new LiveSessionStartResult(LiveSessionStartStatus.MissingCredentials);

        CharacterSelectionState.Begin(activeGeneration);
        CharacterCreationState.Begin(activeGeneration);

        SessionScope? scope = null;
        try
        {
            IPEndPoint endpoint = _operations.ResolveEndpoint(options.Host, options.Port);
            if (_generation != generation)
                return new LiveSessionStartResult(LiveSessionStartStatus.Deferred);
            Console.WriteLine($"live: connecting to {endpoint} as {options.User}");
            WorldSession session = _operations.CreateSession(endpoint);
            scope = new SessionScope(
                session,
                host,
                new RuntimeGenerationToken(generation));
            _scope = scope;
            _inWorld = false;
            if (!IsCurrent(scope, generation))
                return new LiveSessionStartResult(LiveSessionStartStatus.Deferred);

            LiveSessionBinding binding = host.BindSession(session);
            scope.Binding = binding;
            scope.HostAttached = true;
            if (!ReferenceEquals(binding.Session, session))
            {
                throw new InvalidOperationException(
                    "The live-session host returned a binding for a different session.");
            }
            if (!IsCurrent(scope, generation))
                return new LiveSessionStartResult(LiveSessionStartStatus.Deferred);

            host.ReportConnecting(options.Host, options.Port, options.User);
            if (!IsCurrent(scope, generation))
                return new LiveSessionStartResult(LiveSessionStartStatus.Deferred);

            _operations.Connect(session, options.User, options.Password);
            if (!IsCurrent(scope, generation))
                return new LiveSessionStartResult(LiveSessionStartStatus.Deferred);
            scope.CharacterSelectionBinding = BindCharacterSelection(
                scope,
                generation);
            host.ReportConnected();
            if (!IsCurrent(scope, generation))
                return new LiveSessionStartResult(LiveSessionStartStatus.Deferred);

            CharacterList.Parsed? characters = _operations.GetCharacters(session);
            if (characters is not null)
            {
                _createsSinceCharacterList = 0;
                LiveSessionRosterReport roster = BuildRosterReport(characters);
                CharacterSelectionState.ApplyRoster(roster);
                host.ReportRoster(roster);
                if (!IsCurrent(scope, generation))
                    return new LiveSessionStartResult(LiveSessionStartStatus.Deferred);
            }

            if (IsCurrent(scope, generation)
                && _operations.GetServerInfo(session) is { } serverInfo)
                CharacterSelectionState.ApplyWorldName(serverInfo.WorldName);

            if (options.Probe && characters is not null)
            {
                Console.WriteLine(
                    "live: probe complete — disconnecting before EnterWorld");
                StopCore();
                return new LiveSessionStartResult(LiveSessionStartStatus.ProbeComplete);
            }

            if (options.AwaitCharacterSelection
                && options.Character is null
                && characters is not null)
            {
                _operations.StartCharacterSelectionReceive(session);
                if (!IsCurrent(scope, generation))
                    return new LiveSessionStartResult(LiveSessionStartStatus.Deferred);
                Console.WriteLine(
                    "live: awaiting character selection before EnterWorld");
                return new LiveSessionStartResult(
                    LiveSessionStartStatus.AwaitingCharacterSelection);
            }

            if (characters is null
                || !TrySelectCharacter(
                    characters,
                    options.Character,
                    out CharacterList.Selection selected))
            {
                Console.WriteLine("live: no available characters on account; disconnecting");
                StopCore();
                return new LiveSessionStartResult(LiveSessionStartStatus.NoCharacters);
            }

            var selection = new LiveSessionCharacterSelection(
                selected.ActiveIndex,
                selected.Character.Id,
                selected.Character.Name,
                characters.AccountName);
            if (!CharacterSelectionState.TryHighlight(selection.CharacterId)
                || !CharacterSelectionState.BeginEnter(out _))
            {
                throw new InvalidOperationException(
                    "Runtime character selection rejected the validated active character.");
            }
            host.ApplySelectedCharacter(selection);
            if (!IsCurrent(scope, generation))
                return new LiveSessionStartResult(LiveSessionStartStatus.Deferred);

            Console.WriteLine(
                $"live: entering world as 0x{selection.CharacterId:X8} {selection.CharacterName}");
            _operations.EnterWorld(session, selection.ActiveIndex);
            if (!IsCurrent(scope, generation))
                return new LiveSessionStartResult(LiveSessionStartStatus.Deferred);

            binding.ActivateCommands();
            if (!IsCurrent(scope, generation))
                return new LiveSessionStartResult(LiveSessionStartStatus.Deferred);
            _inWorld = true;
            _activeSelection = selection;
            CharacterSelectionState.CompleteEnter(selection.CharacterId);
            CharacterCreationState.CompleteEnter();
            host.ApplyEnteredWorld(selection);
            if (!IsCurrent(scope, generation))
                return new LiveSessionStartResult(LiveSessionStartStatus.Deferred);

            Console.WriteLine("live: in world — CreateObject stream active");
            return new LiveSessionStartResult(
                LiveSessionStartStatus.Connected,
                selection);
        }
        catch (Exception startError)
        {
            return new LiveSessionStartResult(
                LiveSessionStartStatus.Failed,
                Error: StopAfterFailure(startError));
        }
    }

    private Exception StopAfterFailure(Exception operationError)
    {
        try
        {
            StopCore();
            return operationError;
        }
        catch (Exception cleanupError)
        {
            return new AggregateException(
                "Live-session operation and cleanup both failed.",
                operationError,
                cleanupError);
        }
    }

    private CharacterSelectionWireBinding BindCharacterSelection(
        SessionScope scope,
        ulong generation) =>
        new(
            scope.Session,
            roster =>
            {
                lock (_gate)
                {
                    if (!IsCurrent(scope, generation))
                        return;
                    _createsSinceCharacterList = 0;
                    LiveSessionRosterReport report = BuildRosterReport(roster);
                    CharacterSelectionState.ApplyRoster(report);
                    scope.Host.ReportRoster(report);
                }
            },
            () =>
            {
                lock (_gate)
                {
                    if (IsCurrent(scope, generation))
                        CharacterSelectionState.ApplyDeleteAcknowledged();
                }
            },
            restore =>
            {
                lock (_gate)
                {
                    if (IsCurrent(scope, generation))
                        CharacterSelectionState.ApplyRestore(restore);
                }
            },
            error =>
            {
                lock (_gate)
                {
                    if (IsCurrent(scope, generation))
                        CharacterSelectionState.ApplyError(error);
                }
            },
            worldName =>
            {
                lock (_gate)
                {
                    if (IsCurrent(scope, generation))
                        CharacterSelectionState.ApplyWorldName(worldName.WorldName);
                }
            },
            created =>
            {
                lock (_gate)
                {
                    if (IsCurrent(scope, generation))
                        HandleCharacterCreationResponse(scope, generation, created);
                }
            });

    public RuntimeCommandResult Highlight(
        RuntimeGenerationToken expectedGeneration,
        uint characterId)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterSelectionCommand(
                expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterSelectionResult(gate);
            RuntimeCommandStatus status =
                CharacterSelectionState.TryHighlight(characterId)
                    ? RuntimeCommandStatus.Accepted
                    : RuntimeCommandStatus.Rejected;
            return CharacterSelectionResult(status, characterId);
        }
    }

    public RuntimeCommandResult Enter(
        RuntimeGenerationToken expectedGeneration)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterSelectionCommand(
                expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterSelectionResult(gate);
            if (_operationDepth != 0)
                return CharacterSelectionResult(RuntimeCommandStatus.Rejected);
            return RunTopLevel(EnterSelectedCore);
        }
    }

    public RuntimeCommandResult RequestDelete(
        RuntimeGenerationToken expectedGeneration)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterSelectionCommand(
                expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterSelectionResult(gate);
            RuntimeCommandStatus status =
                CharacterSelectionState.TryRequestDelete(out uint characterId)
                    ? RuntimeCommandStatus.Accepted
                    : RuntimeCommandStatus.Rejected;
            return CharacterSelectionResult(status, characterId);
        }
    }

    public RuntimeCommandResult ConfirmDelete(
        RuntimeGenerationToken expectedGeneration)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterSelectionCommand(
                expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterSelectionResult(gate);
            if (!CharacterSelectionState.TryTakeDeleteConfirmation(
                    out RuntimeCharacterSelectionEntry character,
                    out string accountName))
            {
                return CharacterSelectionResult(RuntimeCommandStatus.Rejected);
            }

            try
            {
                _operations.DeleteCharacter(
                    _scope!.Session,
                    accountName,
                    character.ActiveIndex);
                return CharacterSelectionResult(
                    RuntimeCommandStatus.Accepted,
                    character.CharacterId);
            }
            catch
            {
                CharacterSelectionState.ApplyError(
                    new CharacterError.Parsed(
                        (uint)CharacterError.Code.Delete));
                return CharacterSelectionResult(
                    RuntimeCommandStatus.Rejected,
                    character.CharacterId);
            }
        }
    }

    public RuntimeCommandResult Restore(
        RuntimeGenerationToken expectedGeneration)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterSelectionCommand(
                expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterSelectionResult(gate);
            if (!CharacterSelectionState.TryBeginRestore(
                    out RuntimeCharacterSelectionEntry character))
            {
                return CharacterSelectionResult(RuntimeCommandStatus.Rejected);
            }

            try
            {
                _operations.RestoreCharacter(
                    _scope!.Session,
                    character.CharacterId);
                return CharacterSelectionResult(
                    RuntimeCommandStatus.Accepted,
                    character.CharacterId);
            }
            catch
            {
                CharacterSelectionState.ApplyError(
                    new CharacterError.Parsed(
                        (uint)CharacterError.Code.Undefined));
                return CharacterSelectionResult(
                    RuntimeCommandStatus.Rejected,
                    character.CharacterId);
            }
        }
    }

    public RuntimeCommandResult Cancel(
        RuntimeGenerationToken expectedGeneration)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterSelectionCommand(
                expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterSelectionResult(gate);
            return CharacterSelectionResult(
                CharacterSelectionState.Cancel()
                    ? RuntimeCommandStatus.Accepted
                    : RuntimeCommandStatus.Rejected);
        }
    }

    public RuntimeCommandResult BeginCharacterLogOff(
        RuntimeGenerationToken expectedGeneration)
    {
        lock (_gate)
        {
            RuntimeGenerationToken current = new(_generation);
            if (expectedGeneration != current)
            {
                return new RuntimeCommandResult(
                    RuntimeCommandStatus.StaleGeneration,
                    current);
            }
            if (_disposed
                || _disposeRequested
                || _scope is null
                || !_inWorld
                || _operationDepth != 0)
            {
                return new RuntimeCommandResult(
                    RuntimeCommandStatus.Inactive,
                    current);
            }

            SessionScope scope = _scope;
            InvokePreLogoffFlush(scope.Session);
            try
            {
                _operations.RequestCharacterLogOff(scope.Session);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    $"live: character-logoff request failed: {error.Message}");
                return new RuntimeCommandResult(
                    RuntimeCommandStatus.Rejected,
                    current);
            }

            return new RuntimeCommandResult(
                RuntimeCommandStatus.Accepted,
                current);
        }
    }

    public RuntimeCommandResult CompleteCharacterLogOff(
        RuntimeGenerationToken expectedGeneration)
    {
        lock (_gate)
        {
            RuntimeGenerationToken current = new(_generation);
            if (expectedGeneration != current)
            {
                return new RuntimeCommandResult(
                    RuntimeCommandStatus.StaleGeneration,
                    current);
            }
            if (_disposed
                || _disposeRequested
                || _scope is null
                || _retiredScope is not null
                || !_inWorld)
            {
                return new RuntimeCommandResult(
                    RuntimeCommandStatus.Inactive,
                    current);
            }
            if (_operationDepth != 0)
            {
                return new RuntimeCommandResult(
                    RuntimeCommandStatus.Rejected,
                    current);
            }

            return RunTopLevel(CompleteCharacterLogOffCore);
        }
    }

    private RuntimeCommandResult CompleteCharacterLogOffCore()
    {
        SessionScope scope = _scope!;
        ILiveSessionLifecycleHost host = scope.Host;
        WorldSession session = scope.Session;
        RuntimeGenerationToken retiring = scope.Generation;
        try
        {
            scope.Binding?.Dispose();
            scope.CharacterSelectionBinding?.Dispose();

            // 2. Detach, then reset the retiring world generation (the
            //    DrainTeardown stage 2→3 ordering, without stage 1's
            //    transport disposal).
            if (scope.HostAttached)
            {
                host.DetachSession(session);
                scope.HostAttached = false;
            }
            host.ResetSessionState(retiring);

            _operations.ReturnToCharacterSelect(session);

            ulong generation = ++_generation;
            var activeGeneration = new RuntimeGenerationToken(generation);
            _inWorld = false;
            _activeSelection = null;
            CharacterSelectionState.Reset(activeGeneration);
            CharacterCreationState.Reset(activeGeneration);
            _createsSinceCharacterList = 0;
            CharacterSelectionState.Begin(activeGeneration);
            CharacterCreationState.Begin(activeGeneration);

            var newScope = new SessionScope(session, host, activeGeneration);
            _scope = newScope;
            LiveSessionBinding binding = host.BindSession(session);
            newScope.Binding = binding;
            newScope.HostAttached = true;
            newScope.CharacterSelectionBinding = BindCharacterSelection(
                newScope,
                generation);

            CharacterList.Parsed? characters =
                _operations.GetCharacters(session);
            if (characters is not null)
            {
                _createsSinceCharacterList = 0;
                LiveSessionRosterReport roster = BuildRosterReport(characters);
                CharacterSelectionState.ApplyRoster(roster);
                host.ReportRoster(roster);
            }
            if (_operations.GetServerInfo(session) is { } serverInfo)
                CharacterSelectionState.ApplyWorldName(serverInfo.WorldName);

            uint nextLogin = _nextLoginCharacterId;
            if (nextLogin != 0u
                && CharacterSelectionState.TryHighlight(nextLogin))
            {
                RuntimeCommandResult entered = EnterSelectedCore();
                if (entered.Status == RuntimeCommandStatus.Accepted)
                    _nextLoginCharacterId = 0u;
                return entered;
            }

            Console.WriteLine(
                "live: character logoff complete — returned to character "
                + "select (session connected)");
            return new RuntimeCommandResult(
                RuntimeCommandStatus.Accepted,
                activeGeneration);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "live: return-to-character-select failed; stopping session: "
                + error.Message);
            _ = StopAfterFailure(error);
            return new RuntimeCommandResult(
                RuntimeCommandStatus.Rejected,
                new RuntimeGenerationToken(_generation));
        }
    }

    private RuntimeCommandResult EnterSelectedCore() =>
        EnterHighlightedCore(static (operations, session, character, _) =>
            operations.EnterWorld(session, character.ActiveIndex));

    private RuntimeCommandResult EnterCreatedCharacterCore(
        RuntimeCharacterCreationIdentity identity) =>
        EnterHighlightedCore((operations, session, _, accountName) =>
            operations.EnterWorldByGuid(session, identity.Guid, accountName));

    private RuntimeCommandResult EnterHighlightedCore(
        Action<ILiveSessionOperations, WorldSession, RuntimeCharacterSelectionEntry, string> sendEnterWorld)
    {
        SessionScope scope = _scope!;
        ulong generation = _generation;
        if (!CharacterSelectionState.BeginEnter(
                out RuntimeCharacterSelectionEntry character))
        {
            return CharacterSelectionResult(RuntimeCommandStatus.Rejected);
        }

        RuntimeCharacterSelectionSnapshot snapshot =
            CharacterSelectionState.Snapshot;
        var selection = new LiveSessionCharacterSelection(
            character.ActiveIndex,
            character.CharacterId,
            character.Name,
            snapshot.AccountName);
        try
        {
            scope.Host.ApplySelectedCharacter(selection);
            if (!IsCurrent(scope, generation))
                return CharacterSelectionResult(RuntimeCommandStatus.Inactive);

            sendEnterWorld(_operations, scope.Session, character, snapshot.AccountName);
            if (!IsCurrent(scope, generation))
                return CharacterSelectionResult(RuntimeCommandStatus.Inactive);

            scope.Binding!.ActivateCommands();
            if (!IsCurrent(scope, generation))
                return CharacterSelectionResult(RuntimeCommandStatus.Inactive);

            _inWorld = true;
            _activeSelection = selection;
            CharacterSelectionState.CompleteEnter(character.CharacterId);
            CharacterCreationState.CompleteEnter();
            scope.Host.ApplyEnteredWorld(selection);
            if (!IsCurrent(scope, generation))
                return CharacterSelectionResult(RuntimeCommandStatus.Inactive);

            Console.WriteLine("live: in world — CreateObject stream active");
            return CharacterSelectionResult(
                RuntimeCommandStatus.Accepted,
                character.CharacterId);
        }
        catch (CharacterSelectionRejectedException rejected)
        {
            if (CharacterSelectionState.Snapshot.Error?.RawCode
                != rejected.Error.RawErrorCode)
            {
                CharacterSelectionState.ApplyError(rejected.Error);
            }
            CharacterSelectionState.ReturnToSelection();
            return CharacterSelectionResult(
                RuntimeCommandStatus.Rejected,
                character.CharacterId);
        }
        catch (Exception error)
        {
            _ = StopAfterFailure(error);
            return CharacterSelectionResult(
                RuntimeCommandStatus.Rejected,
                character.CharacterId);
        }
    }

    private RuntimeCommandStatus ValidateCharacterSelectionCommand(
        RuntimeGenerationToken expectedGeneration)
    {
        RuntimeGenerationToken current = new(_generation);
        if (expectedGeneration != current)
            return RuntimeCommandStatus.StaleGeneration;
        if (_disposed || _disposeRequested || _scope is null || _inWorld)
            return RuntimeCommandStatus.Inactive;
        return CharacterSelectionState.Snapshot.Lifecycle
            == RuntimeCharacterSelectionLifecycle.AwaitingSelection
                ? RuntimeCommandStatus.Accepted
                : RuntimeCommandStatus.Inactive;
    }

    private RuntimeCommandResult CharacterSelectionResult(
        RuntimeCommandStatus status,
        uint characterId = 0u) =>
        new(
            status,
            new RuntimeGenerationToken(_generation),
            characterId);


    private sealed class RosterCollector(List<LiveSessionRosterEntry> entries)
        : IRuntimeCharacterSelectionVisitor
    {
        public void Visit(in RuntimeCharacterSelectionEntry character) =>
            entries.Add(new LiveSessionRosterEntry(
                character.CharacterId,
                character.Name,
                character.SecondsGreyedOut));
    }

    private void HandleCharacterCreationResponse(
        SessionScope scope,
        ulong generation,
        CharGenVerificationResponse.Parsed response)
    {
        CharacterCreationState.ApplyCreationResponse(response);
        RuntimeCharacterCreationSnapshot creation = CharacterCreationState.Snapshot;

        if (creation.LastCreated is { } created)
            scope.Host.ApplyCharacterCreated(created);
        else if (creation.LastRejection is { } rejection)
            scope.Host.ApplyCreationFailed(rejection);

        if (creation.LastCreated is not { } identity)
            return;

        RuntimeCharacterSelectionSnapshot before = CharacterSelectionState.Snapshot;
        var entries = new List<LiveSessionRosterEntry>(before.RosterCount + 1);
        CharacterSelectionState.View.Visit(new RosterCollector(entries));
        entries.Add(new LiveSessionRosterEntry(
            identity.Guid,
            identity.Name,
            SecondsGreyedOut: 0u));
        var report = new LiveSessionRosterReport(
            before.AccountName,
            before.SlotCount,
            entries);

        int wireIndex =
            _operations.GetCharacters(scope.Session)?.Characters.Count
                is int cachedWireCount
            ? cachedWireCount + _createsSinceCharacterList
            : before.RosterCount;
        _createsSinceCharacterList++;
        CharacterSelectionState.AppendCreatedCharacter(
            identity.Guid,
            identity.Name,
            wireIndex);
        scope.Host.ReportRoster(report);
        if (!IsCurrent(scope, generation))
            return;

        if (!CharacterSelectionState.TryHighlight(identity.Guid))
            return;
        if (!IsCurrent(scope, generation))
            return;

        _ = EnterCreatedCharacterCore(identity);
    }

    public RuntimeCommandResult SelectHeritage(
        RuntimeGenerationToken expectedGeneration,
        uint heritageId)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);
            return CharacterCreationResult(
                CharacterCreationState.TrySelectHeritage(heritageId));
        }
    }

    public RuntimeCommandResult SelectGender(
        RuntimeGenerationToken expectedGeneration,
        uint genderKey)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);
            return CharacterCreationResult(
                CharacterCreationState.TrySelectGender(genderKey));
        }
    }

    public RuntimeCommandResult SelectTemplate(
        RuntimeGenerationToken expectedGeneration,
        uint templateIndex)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);
            return CharacterCreationResult(
                CharacterCreationState.TrySelectTemplate(templateIndex));
        }
    }

    public RuntimeCommandResult SetAttribute(
        RuntimeGenerationToken expectedGeneration,
        ChargenAttributeId attributeId,
        int value)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);
            return CharacterCreationResult(
                CharacterCreationState.TrySetAttribute(attributeId, value));
        }
    }

    public RuntimeCommandResult SetAttributeLock(
        RuntimeGenerationToken expectedGeneration,
        ChargenAttributeId attributeId,
        bool locked)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);
            return CharacterCreationResult(
                CharacterCreationState.TrySetAttributeLock(attributeId, locked));
        }
    }

    public RuntimeCommandResult TrainSkill(
        RuntimeGenerationToken expectedGeneration,
        uint skillId)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);
            return CharacterCreationResult(CharacterCreationState.TryTrainSkill(skillId));
        }
    }

    public RuntimeCommandResult SpecializeSkill(
        RuntimeGenerationToken expectedGeneration,
        uint skillId)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);
            return CharacterCreationResult(CharacterCreationState.TrySpecializeSkill(skillId));
        }
    }

    public RuntimeCommandResult UntrainSkill(
        RuntimeGenerationToken expectedGeneration,
        uint skillId)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);
            return CharacterCreationResult(CharacterCreationState.TryUntrainSkill(skillId));
        }
    }

    public RuntimeCommandResult SetAppearanceIndex(
        RuntimeGenerationToken expectedGeneration,
        ChargenAppearanceSlot slot,
        uint index)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);
            return CharacterCreationResult(
                CharacterCreationState.TrySetAppearanceIndex(slot, index));
        }
    }

    public RuntimeCommandResult SetShade(
        RuntimeGenerationToken expectedGeneration,
        ChargenShadeSlot slot,
        double value)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);
            return CharacterCreationResult(CharacterCreationState.TrySetShade(slot, value));
        }
    }

    public RuntimeCommandResult SelectStartArea(
        RuntimeGenerationToken expectedGeneration,
        int startAreaIndex)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);
            return CharacterCreationResult(
                CharacterCreationState.TrySelectStartArea(startAreaIndex));
        }
    }

    public RuntimeCommandResult SetName(
        RuntimeGenerationToken expectedGeneration,
        string name)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);
            return CharacterCreationResult(CharacterCreationState.TrySetName(name ?? string.Empty));
        }
    }

    public RuntimeCommandResult SetSlot(
        RuntimeGenerationToken expectedGeneration,
        uint slot)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);
            return CharacterCreationResult(CharacterCreationState.TrySetSlot(slot));
        }
    }

    public RuntimeCommandResult Finish(
        RuntimeGenerationToken expectedGeneration,
        bool confirmUnspentCredits = false)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);

            SessionScope scope = _scope!;
            RuntimeCharacterSelectionSnapshot selection = CharacterSelectionState.Snapshot;
            if (!CharacterCreationState.TryBeginFinish(
                    selection.RosterCount,
                    selection.SlotCount,
                    out CharacterCreate.Request request,
                    out uint[] skillAdvancementClasses,
                    out _,
                    confirmUnspentCredits))
            {
                return CharacterCreationResult(RuntimeCommandStatus.Rejected);
            }

            try
            {
                _operations.CreateCharacter(
                    scope.Session,
                    selection.AccountName,
                    request,
                    skillAdvancementClasses);
                return CharacterCreationResult(RuntimeCommandStatus.Accepted);
            }
            // F13: narrowed to what SendCharacterCreation's send path
            // actually throws — WorldSession.SendGameMessage's own
            // InvalidOperationException (transport not yet negotiated) and
            // whatever the underlying UDP send raises (SocketException).
            // Anything else is a genuine bug, not a transport hiccup, and
            // should propagate rather than being silently swallowed into a
            // rejection.
            catch (Exception error) when (
                error is InvalidOperationException
                    or System.Net.Sockets.SocketException)
            {
                CharacterCreationState.ApplyCreationResponse(
                    new CharGenVerificationResponse.Parsed(
                        (uint)CharGenVerificationResponse.Code.Undef,
                        null,
                        null,
                        null));
                return CharacterCreationResult(RuntimeCommandStatus.Rejected);
            }
        }
    }

    public RuntimeCommandResult AcknowledgeRejection(
        RuntimeGenerationToken expectedGeneration)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);
            return CharacterCreationResult(
                CharacterCreationState.TryAcknowledgeRejection());
        }
    }

    public RuntimeCommandResult RandomizeCharacter(
        RuntimeGenerationToken expectedGeneration)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);
            return CharacterCreationResult(
                CharacterCreationState.TryRandomizeCharacter());
        }
    }

    public RuntimeCommandResult RandomizeAppearance(
        RuntimeGenerationToken expectedGeneration)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);
            return CharacterCreationResult(
                CharacterCreationState.TryRandomizeAppearance());
        }
    }

    public RuntimeCommandResult RandomizeClothing(
        RuntimeGenerationToken expectedGeneration)
    {
        lock (_gate)
        {
            RuntimeCommandStatus gate = ValidateCharacterCreationCommand(expectedGeneration);
            if (gate != RuntimeCommandStatus.Accepted)
                return CharacterCreationResult(gate);
            return CharacterCreationResult(
                CharacterCreationState.TryRandomizeClothing());
        }
    }

    private RuntimeCommandStatus ValidateCharacterCreationCommand(
        RuntimeGenerationToken expectedGeneration)
    {
        RuntimeGenerationToken current = new(_generation);
        if (expectedGeneration != current)
            return RuntimeCommandStatus.StaleGeneration;
        if (_disposed || _disposeRequested || _scope is null || _inWorld)
            return RuntimeCommandStatus.Inactive;
        return CharacterSelectionState.Snapshot.Lifecycle
            == RuntimeCharacterSelectionLifecycle.AwaitingSelection
                ? RuntimeCommandStatus.Accepted
                : RuntimeCommandStatus.Inactive;
    }

    private RuntimeCommandResult CharacterCreationResult(bool accepted) =>
        CharacterCreationResult(
            accepted ? RuntimeCommandStatus.Accepted : RuntimeCommandStatus.Rejected);

    private RuntimeCommandResult CharacterCreationResult(RuntimeCommandStatus status) =>
        new(status, new RuntimeGenerationToken(_generation));

    private void StopCore()
    {
        if (_inWorld && _scope is { } activeScope)
            InvokePreLogoffFlush(activeScope.Session);

        ++_generation;
        _inWorld = false;
        _activeSelection = null;
        CharacterSelectionState.Reset(new RuntimeGenerationToken(_generation));
        CharacterCreationState.Reset(new RuntimeGenerationToken(_generation));
        _createsSinceCharacterList = 0;
        if (_scope is { } scope)
        {
            _scope = null;
            if (_retiredScope is not null && !ReferenceEquals(_retiredScope, scope))
                throw new InvalidOperationException(
                    "A second live-session scope cannot retire before the first converges.");
            _retiredScope = scope;
        }
        DrainRetiredScope();
        DrainPendingInitialReset();
        if (_retiredScope is null && _pendingInitialReset is null)
            _lastTeardownStages = RuntimeTeardownStage.Complete;
    }

    private void InvokePreLogoffFlush(WorldSession session)
    {
        if (_preLogoffFlushHook is not { } hook)
            return;
        try
        {
            hook(session);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                $"live: pre-logoff character-options flush failed: {error.Message}");
        }
    }

    private void DrainRetiredScope()
    {
        if (_retiredScope is not { } retired)
            return;
        retired.DrainTeardown(_operations);
        if (retired.IsTeardownComplete)
        {
            _lastTeardownStages = retired.CompletedStages;
            _retiredScope = null;
        }
    }

    private void ResetHostBeforeStart(
        ILiveSessionLifecycleHost host,
        ulong generation,
        RuntimeGenerationToken resetGeneration)
    {
        bool requestedHostAlreadyReset = false;
        if (_pendingInitialReset is { } pending)
        {
            pending.Host.ResetSessionState(pending.Generation);
            _pendingInitialReset = null;
            requestedHostAlreadyReset =
                ReferenceEquals(pending.Host, host);
        }
        if (requestedHostAlreadyReset || _generation != generation)
            return;

        try
        {
            host.ResetSessionState(resetGeneration);
        }
        catch
        {
            _pendingInitialReset =
                new PendingHostReset(host, resetGeneration);
            throw;
        }
    }

    private void DrainPendingInitialReset()
    {
        if (_pendingInitialReset is not { } pending)
            return;
        pending.Host.ResetSessionState(pending.Generation);
        _pendingInitialReset = null;
    }

    private void Schedule(PendingOperation operation)
    {
        if (_pendingOperation?.Kind == PendingKind.Dispose)
            return;
        _pendingOperation = operation;
        ++_generation;
        _inWorld = false;
        _scope?.Binding?.Dispose();
    }

    private T RunTopLevel<T>(Func<T> operation)
    {
        _operationDepth++;
        try
        {
            return operation();
        }
        finally
        {
            _operationDepth--;
            if (_operationDepth == 0)
                DrainPendingOperations();
        }
    }

    private void RunTopLevel(Action operation) =>
        RunTopLevel(() =>
        {
            operation();
            return true;
        });

    private void DrainPendingOperations()
    {
        while (_pendingOperation is { } pending)
        {
            _pendingOperation = null;
            _operationDepth++;
            try
            {
                switch (pending.Kind)
                {
                    case PendingKind.Stop:
                        StopCore();
                        break;
                    case PendingKind.Reconnect:
                        _ = ReconnectCore(pending.Options!, pending.Host!);
                        break;
                    case PendingKind.Dispose:
                        DisposeCore();
                        break;
                }
            }
            finally
            {
                _operationDepth--;
            }
        }
    }

    private void DisposeCore()
    {
        StopCore();
        CharacterSelectionState.Dispose();
        CharacterCreationState.Dispose();
        _disposed = true;
    }

    private bool IsCurrent(SessionScope scope, ulong generation) =>
        ReferenceEquals(_scope, scope) && _generation == generation;

    private LiveSessionStartResult ConnectedResult()
        => new(LiveSessionStartStatus.Connected, _activeSelection);

    private static LiveSessionRosterReport BuildRosterReport(
        CharacterList.Parsed characters)
    {
        var entries = new LiveSessionRosterEntry[characters.Characters.Count];
        for (int i = 0; i < entries.Length; i++)
        {
            CharacterList.Character character = characters.Characters[i];
            entries[i] = new LiveSessionRosterEntry(
                character.Id,
                character.Name,
                character.SecondsGreyedOut);
        }
        return new LiveSessionRosterReport(
            characters.AccountName,
            characters.SlotCount,
            entries);
    }

    private static bool TrySelectCharacter(
        CharacterList.Parsed characters,
        LiveSessionCharacterSelector? selector,
        out CharacterList.Selection selection)
    {
        ArgumentNullException.ThrowIfNull(characters);
        if (selector is null)
        {
            return CharacterList.TrySelectFirstAvailable(
                characters,
                out selection);
        }

        int selectorCount =
            (selector.ActiveIndex.HasValue ? 1 : 0)
            + (selector.CharacterId.HasValue ? 1 : 0)
            + (!string.IsNullOrWhiteSpace(selector.CharacterName) ? 1 : 0);
        if (selectorCount != 1)
        {
            selection = default;
            return false;
        }

        for (int index = 0; index < characters.Characters.Count; index++)
        {
            CharacterList.Character character =
                characters.Characters[index];
            if (!CharacterList.IsAvailableActiveIdentity(character))
                continue;
            if (selector.ActiveIndex is { } requestedIndex
                && requestedIndex != index)
            {
                continue;
            }
            if (selector.CharacterId is { } requestedId
                && requestedId != character.Id)
            {
                continue;
            }
            if (selector.CharacterName is { } requestedName
                && !string.Equals(
                    requestedName,
                    character.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            selection = new CharacterList.Selection(index, character);
            return true;
        }

        selection = default;
        return false;
    }

    private void ThrowIfDisposing()
    {
        if (_disposeRequested || _disposed)
            throw new ObjectDisposedException(nameof(LiveSessionController));
    }

}
