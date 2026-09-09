using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Plugins;
using AcDream.Headless.Policies;
using AcDream.Plugin.Abstractions;
using AcDream.Content.CharGen;
using AcDream.Core.Chat;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Hosting;

internal sealed class HeadlessSessionHost : IDisposable
{
    private sealed class SessionCommandRoute : ILiveSessionCommandRouting
    {
        private ILiveSessionCommandRouting? _gameplay;
        private ILiveSessionCommandRouting? _commands;
        private ILiveSessionCommandRouting? _chat;
        private bool _activated;

        internal SessionCommandRoute(
            ILiveSessionCommandRouting gameplay,
            ILiveSessionCommandRouting commands,
            ILiveSessionCommandRouting chat)
        {
            _gameplay = gameplay
                ?? throw new ArgumentNullException(nameof(gameplay));
            _commands = commands
                ?? throw new ArgumentNullException(nameof(commands));
            _chat = chat
                ?? throw new ArgumentNullException(nameof(chat));
        }

        public void Activate()
        {
            if (_activated)
                return;
            if (_gameplay is null || _commands is null || _chat is null)
                throw new ObjectDisposedException(nameof(SessionCommandRoute));

            _gameplay.Activate();
            try
            {
                _commands.Activate();
                _chat.Activate();
                _activated = true;
            }
            catch (Exception activationError)
            {
                try
                {
                    Dispose();
                }
                catch (Exception disposalError)
                {
                    throw new AggregateException(
                        "Headless command-route activation and rollback failed.",
                        activationError,
                        disposalError);
                }

                throw;
            }
        }

        public void Dispose()
        {
            List<Exception>? failures = null;
            TryDispose(ref _chat, ref failures);
            TryDispose(ref _commands, ref failures);
            TryDispose(ref _gameplay, ref failures);
            if (failures is not null)
            {
                throw new AggregateException(
                    "Headless command routes did not detach cleanly.",
                    failures);
            }
        }

        private static void TryDispose(
            ref ILiveSessionCommandRouting? route,
            ref List<Exception>? failures)
        {
            if (route is not { } current)
                return;

            try
            {
                current.Dispose();
                route = null;
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
        }
    }

    private sealed class SessionCommandBridge : IRuntimeSessionCommands
    {
        private HeadlessSessionHost? _owner;

        internal void Bind(HeadlessSessionHost owner)
        {
            if (_owner is not null)
            {
                throw new InvalidOperationException(
                    "The headless session command bridge is already bound.");
            }
            _owner = owner
                ?? throw new ArgumentNullException(nameof(owner));
        }

        public RuntimeSessionStartResult Start(
            RuntimeGenerationToken expectedGeneration) =>
            RequireOwner().StartCore(expectedGeneration, reconnect: false);

        public RuntimeSessionStartResult Reconnect(
            RuntimeGenerationToken expectedGeneration) =>
            RequireOwner().StartCore(expectedGeneration, reconnect: true);

        public RuntimeTeardownAcknowledgement Stop(
            RuntimeGenerationToken expectedGeneration) =>
            RequireOwner()._liveSession.Stop(expectedGeneration);

        private HeadlessSessionHost RequireOwner() =>
            _owner
            ?? throw new InvalidOperationException(
                "The headless session command bridge is not bound.");
    }

    private readonly HeadlessSessionDescriptor _descriptor;
    private readonly HeadlessCredentialSecret _credential;
    private readonly HeadlessDiagnosticWriter _diagnostics;
    private readonly SessionStatusWriter _statusWriter;
    private RuntimeSessionStartStatus? _startOutcome;
    private bool _hasConnected;
    private readonly Dictionary<CharacterOptionId, bool> _declaredCharacterOptions;
    private HeadlessCharacterOptionsSeeder? _optionsSeeder;
    private readonly TimeSpan _reconnectQuiescence;
    private readonly TimeProvider _timeProvider;
    private readonly HeadlessGenerationResetHost _resetHost = new();
    private readonly IDisposable _hostLease;
    private readonly IHeadlessBotPolicy _policy;
    private readonly IDisposable _policySubscription;
    private readonly HeadlessPluginSession _pluginSession;
    private readonly AcDream.Core.Plugins.PluginCommandRegistry _pluginCommands;
    private readonly LiveChatCommandSurface _chatCommandSurface;
    private readonly LiveSessionHost _liveSession;
    private readonly RuntimeLocalPlayerFrameController _localPlayerFrame;
    private readonly HeadlessProcessContentOwner.HeadlessProcessContentLease?
        _contentLease;
    private readonly IRuntimePlacementProjectionSink? _placementSinkOverride;
    private RuntimeFirstEntryDriveController? _firstEntryDrive;
    private RuntimeAcceptedPositionDriveController? _acceptedPositionDrive;
    private AcDream.Core.Net.WorldSession? _currentSession;
    private HeadlessSessionWorldProjection? _worldProjection;
    private RuntimeLiveEntitySessionController? _entities;
    private HeadlessSessionEventRoute? _eventRoute;
    private GameEvents.CharacterConfirmationRequest? _pendingConfirmation;
    private int _disposeStage;
    private long _reconnectDeadline;
    private bool _reconnectPending;
    private ulong _stoppedGeneration;
    private string _accountName = string.Empty;
    private Exception? _fault;
    private bool _faulted;
    private bool _disposed;

    internal HeadlessSessionHost(
        HeadlessSessionDescriptor descriptor,
        HeadlessCredentialSecret credential,
        HeadlessDiagnosticWriter diagnostics,
        ILiveSessionOperations? sessionOperations = null,
        TimeProvider? timeProvider = null,
        TimeSpan? reconnectQuiescence = null,
        HeadlessProcessContentOwner.HeadlessProcessContentLease?
            contentLease = null,
        IHeadlessBotPolicy? policyOverride = null,
        IRuntimePlacementProjectionSink? placementSinkOverride = null,
        FellowshipAllegianceGateCoordinator? gateCoordinator = null,
        IEnumerable<string>? pluginRoots = null,
        IPluginStorage? vtankProfiles = null)
    {
        _descriptor = descriptor
            ?? throw new ArgumentNullException(nameof(descriptor));
        _credential = credential
            ?? throw new ArgumentNullException(nameof(credential));
        _diagnostics = diagnostics
            ?? throw new ArgumentNullException(nameof(diagnostics));
        _declaredCharacterOptions = ParseDeclaredCharacterOptions(descriptor);
        _placementSinkOverride = placementSinkOverride;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reconnectQuiescence = reconnectQuiescence
            ?? (sessionOperations is null
                ? TimeSpan.FromMilliseconds(2500)
                : TimeSpan.Zero);
        if (_reconnectQuiescence < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reconnectQuiescence));
        }

        GameRuntime? runtimeRef = null;
        IDisposable? hostLease = null;
        IHeadlessBotPolicy? policy = null;
        IDisposable? policySubscription = null;
        HeadlessPluginSession? pluginSession = null;
        try
        {
            var gameplay = new HeadlessGameplayOperations();
            var runtime = new GameRuntime(new GameRuntimeDependencies(
                gameplay,
                gameplay,
                gameplay,
                gameplay,
                TimeProvider: _timeProvider,
                Log: message => diagnostics.Message(
                    descriptor.Id,
                    message),
                SessionOperations: sessionOperations,
                CombatTime: () =>
                    runtimeRef?.Clock.SimulationTimeSeconds ?? 0d));
            runtimeRef = runtime;
            if (contentLease is { } content)
            {
                runtime.CharacterOwner.InstallSpellMetadata(
                    content.MagicCatalog.SpellTable);
                runtime.Session.CharacterCreationState.InstallOptions(
                    ChargenTableReader.Load(content.Dats));
            }
            gameplay.Bind(
                runtime,
                contentLease?.MagicCatalog,
                () => _accountName);

            var bridge = new SessionCommandBridge();
            var commands = new DirectGameRuntimeCommandAdapter(
                runtime,
                bridge);
            var statusWriter = new SessionStatusWriter(descriptor.StatusFile);
            var pluginCommands = new AcDream.Core.Plugins.PluginCommandRegistry(
                (verb, error) => diagnostics.Failure(
                    descriptor.Id,
                    $"plugin-command-{verb}",
                    error));
            var chatCommandSurface = new LiveChatCommandSurface(
                pluginCommands.TryHandle);
            var loginCommands = new LoginCommandSequence(
                descriptor.LoginCommands,
                TimeSpan.FromMilliseconds(descriptor.LoginCommandDelayMs),
                new RuntimeChatCommandFeedback(runtime.CommunicationOwner),
                chatCommandSurface,
                failure => statusWriter.LoginCommandFailed(
                    descriptor.Id,
                    failure.CommandIndex,
                    failure.Command,
                    failure.Error),
                _timeProvider);
            bool SubmitChatText(string text)
            {
                SubmitOutcome outcome = ChatCommandRouter.Submit(
                    text,
                    new RuntimeChatCommandFeedback(runtime.CommunicationOwner),
                    chatCommandSurface,
                    ChatChannelKind.Say);
                return outcome is not (SubmitOutcome.Empty
                    or SubmitOutcome.UnknownCommand
                    or SubmitOutcome.Dropped);
            }
            pluginSession = HeadlessPluginSession.Create(
                runtime,
                diagnostics,
                statusWriter,
                descriptor.Id,
                pluginRoots ?? [],
                descriptor.Plugins,
                pluginCommands,
                vtankProfiles,
                descriptor.PluginSettings,
                SubmitChatText);
            var liveSession = new LiveSessionHost(
                runtime.Session,
                new LiveSessionHostBindings(
                    new LiveSessionRoutingFactories(
                        CreateEventRoute,
                        session => new SessionCommandRoute(
                            gameplay.CreateRoute(session),
                            commands.CreateRoute(session),
                            chatCommandSurface.Attach(
                                new LiveChatCommandRoute(
                                    CreateChatCommandBindings(
                                        session,
                                        runtime))))),
                    generation =>
                        runtime.ResetGeneration(generation, _resetHost),
                    new LiveSessionSelectionBindings(
                        id => runtime.PlayerIdentity.ServerGuid = id,
                        _ => { },
                        runtime.CommunicationOwner.Chat.SetLocalPlayerGuid,
                        _ => { },
                        _ => { },
                        runtime.ActionOwner.Combat.Clear),
                    new LiveSessionEnteredWorldBindings(
                        name =>
                        {
                            ActiveCharacterName = name;
                            if (descriptor.Policy?.Role
                                    == HeadlessBotPolicyRole.Recruit
                                && gateCoordinator is not null)
                            {
                                gateCoordinator.RecruitCharacterName = name;
                            }
                        },
                        () => { },
                        () => { },
                        _ => { },
                        () => { }),
                    (host, port, user) =>
                        diagnostics.Message(
                            descriptor.Id,
                            $"connecting:{host}:{port}:{user}",
                            runtime.Generation.Value),
                    () =>
                    {
                        diagnostics.Message(
                            descriptor.Id,
                            "connected",
                            runtime.Generation.Value);
                        statusWriter.Connected(descriptor.Id);
                        _hasConnected = true;
                    },
                    roster => statusWriter.CharacterList(descriptor.Id, roster),
                    selection => statusWriter.EnteredWorld(
                        descriptor.Id,
                        selection.CharacterId,
                        selection.CharacterName),
                    loginCommands,
                    CharacterCreated: identity => statusWriter.CharacterCreated(
                        descriptor.Id,
                        identity.Guid,
                        identity.Name),
                    CreationFailed: rejection => statusWriter.CreationFailed(
                        descriptor.Id,
                        rejection.RawCode,
                        rejection.Reason,
                        rejection.AttemptedName)));

            Runtime = runtime;
            Commands = commands;
            _liveSession = liveSession;
            _pluginCommands = pluginCommands;
            _chatCommandSurface = chatCommandSurface;
            _statusWriter = statusWriter;
            _localPlayerFrame =
                runtime.CreateLocalPlayerFrameController(
                    new HeadlessLocalPlayerFrameHost(
                        runtime,
                        liveSession),
                    new HeadlessMovementInputSource(
                        runtime.MovementOwner));
            _contentLease = contentLease;
            bridge.Bind(this);

            hostLease = runtime.AcquireHostLease(
                $"headless:{descriptor.Id}");
            policy = policyOverride
                ?? (descriptor.Mode == HeadlessSessionMode.Probe
                    ? new ProbeHeadlessBotPolicy()
                    : HeadlessBotPolicyFactory.Create(
                        descriptor.Policy!,
                        runtime,
                        () => _pendingConfirmation,
                        RespondToConfirmation,
                        gateCoordinator));
            policySubscription = runtime.Subscribe(policy);
            diagnostics.Lifecycle(
                descriptor.Id,
                "constructed",
                runtime);

            _hostLease = hostLease;
            _policy = policy;
            _policySubscription = policySubscription;
            _pluginSession = pluginSession;
        }
        catch
        {
            pluginSession?.Dispose();
            policySubscription?.Dispose();
            policy?.Dispose();
            hostLease?.Dispose();
            contentLease?.Dispose();
            credential.Dispose();
            runtimeRef?.Dispose();
            throw;
        }
    }

    internal GameRuntime Runtime { get; }
    internal DirectGameRuntimeCommandAdapter Commands { get; }
    internal HeadlessCharacterOptionsSeeder? OptionsSeeder => _optionsSeeder;
    internal HeadlessPluginSession Plugins => _pluginSession;
    internal AcDream.Core.Plugins.PluginCommandRegistry PluginCommands =>
        _pluginCommands;
    internal string SessionId => _descriptor.Id;
    internal Action? ConsolePump { get; set; }
    internal string ActiveCharacterName { get; private set; } =
        string.Empty;
    internal bool IsPolicyComplete =>
        _faulted || _policy.IsComplete;
    internal bool IsFaulted => _faulted;
    internal Exception? Fault => _fault;
    internal bool IsReconnectPending => _reconnectPending;
    internal HeadlessProcessContentOwner.HeadlessProcessContentLease?
        Content => _contentLease;
    internal long ReconnectDeadline => _reconnectPending
        ? _reconnectDeadline
        : throw new InvalidOperationException(
            "The headless session has no pending reconnect.");

    internal GameEvents.CharacterConfirmationRequest? PendingConfirmation =>
        _pendingConfirmation;

    internal void RespondToConfirmation(bool accepted)
    {
        if (_pendingConfirmation is not { } request)
        {
            throw new InvalidOperationException(
                "No confirmation request is pending.");
        }
        _currentSession?.SendConfirmationResponse(
            request.Type,
            request.ContextId,
            accepted);
        _pendingConfirmation = null;
    }

    internal SubmitOutcome SubmitConsoleLine(string line) =>
        ChatCommandRouter.Submit(
            line,
            new RuntimeChatCommandFeedback(Runtime.CommunicationOwner),
            _chatCommandSurface,
            ChatChannelKind.Say);

    internal RuntimeSessionStartResult Start()
    {
        _statusWriter.Started(_descriptor.Id);
        _pluginSession.Start();
        RuntimeSessionStartResult result =
            Commands.Session.Start(Runtime.Generation);
        _startOutcome = result.Status;
        return result;
    }

    internal RuntimeSessionStartResult Reconnect() =>
        Commands.Session.Reconnect(Runtime.Generation);

    internal void Tick(double deltaSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reconnectPending)
            return;
        _ = Runtime.Clock.Advance(deltaSeconds);
        _localPlayerFrame.AdvanceBeforeNetwork(
            checked((float)deltaSeconds));
        _liveSession.Tick();
        _worldProjection?.PumpFirstEntry();
        _entities?.PumpPortalCompletion();
        _eventRoute?.RetryPending();
        _localPlayerFrame.RunPostNetworkCommandPhase();
        Runtime.ActionOwner.CombatAttack.Tick();
        _policy.Tick(Runtime, Commands);
        _pluginSession.Host.FireTick(deltaSeconds);
        ConsolePump?.Invoke();
    }

    internal RuntimeTeardownAcknowledgement Stop(string reason = "stopped")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        RuntimeTeardownAcknowledgement result =
            Commands.Session.Stop(Runtime.Generation);
        _currentSession = null;
        if (_hasConnected)
        {
            _hasConnected = false;
            _statusWriter.Disconnected(_descriptor.Id, reason);
        }
        return result;
    }

    internal void Quarantine(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (_faulted)
            return;

        _faulted = true;
        _fault = error;
        _reconnectPending = false;
        _reconnectDeadline = 0L;
        _diagnostics.Failure(
            _descriptor.Id,
            "quarantined",
            error);
        try
        {
            _policySubscription.Dispose();
            RuntimeTeardownAcknowledgement stopped = Stop();
            if (!stopped.IsComplete)
            {
                _fault = new AggregateException(
                    error,
                    stopped.Error
                    ?? new InvalidOperationException(
                        $"Headless session '{_descriptor.Id}' did not quiesce after a fault."));
            }
        }
        catch (Exception teardownError)
        {
            _fault = new AggregateException(error, teardownError);
        }
    }

    internal RuntimeSessionStartResult CompletePendingReconnect(
        long nowTimestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_reconnectPending)
        {
            return new RuntimeSessionStartResult(
                RuntimeSessionStartStatus.Inactive,
                Runtime.Generation);
        }
        if (nowTimestamp < _reconnectDeadline)
        {
            return new RuntimeSessionStartResult(
                RuntimeSessionStartStatus.Deferred,
                Runtime.Generation);
        }

        _reconnectPending = false;
        _reconnectDeadline = 0L;
        return StartLive(reconnect: true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        while (!_disposed)
        {
            switch (_disposeStage)
            {
                case 0:
                {
                    _reconnectPending = false;
                    _reconnectDeadline = 0L;
                    RuntimeTeardownAcknowledgement stopped = Stop();
                    if (!stopped.IsComplete)
                    {
                        throw stopped.Error
                            ?? new InvalidOperationException(
                                $"Headless session '{_descriptor.Id}' did not complete teardown.");
                    }
                    _stoppedGeneration =
                        stopped.CurrentGeneration.Value;
                    _disposeStage++;
                    break;
                }
                case 1:
                    _diagnostics.Lifecycle(
                        _descriptor.Id,
                        "stopped",
                        Runtime);
                    _disposeStage++;
                    break;
                case 2:
                    _policySubscription.Dispose();
                    _disposeStage++;
                    break;
                case 3:
                    _policy.Dispose();
                    _disposeStage++;
                    break;
                case 4:
                    _pluginSession.Dispose();
                    _disposeStage++;
                    break;
                case 5:
                    _hostLease.Dispose();
                    _disposeStage++;
                    break;
                case 6:
                    _credential.Dispose();
                    _disposeStage++;
                    break;
                case 7:
                    Runtime.Dispose();
                    _disposeStage++;
                    break;
                case 8:
                    _contentLease?.Dispose();
                    _disposeStage++;
                    break;
                case 9:
                    _diagnostics.Message(
                        _descriptor.Id,
                        "disposed",
                        _stoppedGeneration);
                    (int exitCode, string exitReason) =
                        ResolveTerminalStatus();
                    _statusWriter.Exited(
                        _descriptor.Id,
                        exitCode,
                        exitReason);
                    _disposeStage++;
                    _disposed = true;
                    break;
                default:
                    throw new InvalidOperationException(
                        "Unknown headless session teardown stage.");
            }
        }
    }

    private (int Code, string Reason) ResolveTerminalStatus()
    {
        if (_faulted)
        {
            return (
                (int)HeadlessExitCode.RuntimeError,
                "runtime-fault");
        }

        return _startOutcome switch
        {
            RuntimeSessionStartStatus.ProbeComplete =>
                ((int)HeadlessExitCode.Success, "probe"),
            null or RuntimeSessionStartStatus.Connected =>
                ((int)HeadlessExitCode.Success, "graceful"),
            _ =>
                ((int)HeadlessExitCode.ConnectionError, "connection-error"),
        };
    }

    private RuntimeSessionStartResult StartCore(
        RuntimeGenerationToken expectedGeneration,
        bool reconnect)
    {
        if (expectedGeneration != Runtime.Generation)
        {
            return new RuntimeSessionStartResult(
                RuntimeSessionStartStatus.StaleGeneration,
                Runtime.Generation);
        }
        if (_reconnectPending)
        {
            return new RuntimeSessionStartResult(
                RuntimeSessionStartStatus.Deferred,
                Runtime.Generation);
        }

        if (reconnect)
        {
            RuntimeTeardownAcknowledgement stopped = Stop("reconnect");
            if (!stopped.IsComplete)
            {
                return new RuntimeSessionStartResult(
                    RuntimeSessionStartStatus.Failed,
                    stopped.CurrentGeneration,
                    Error: stopped.Error
                        ?? new InvalidOperationException(
                            "The prior headless session did not quiesce before reconnect."));
            }

            if (_reconnectQuiescence > TimeSpan.Zero)
            {
                _reconnectDeadline = HeadlessMonotonicTime.Add(
                    _timeProvider,
                    _timeProvider.GetTimestamp(),
                    _reconnectQuiescence);
                _reconnectPending = true;
                _diagnostics.Lifecycle(
                    _descriptor.Id,
                    "reconnect-deferred",
                    Runtime);
                return new RuntimeSessionStartResult(
                    RuntimeSessionStartStatus.Deferred,
                    Runtime.Generation);
            }
        }

        return StartLive(reconnect);
    }

    private RuntimeSessionStartResult StartLive(bool reconnect)
    {
        string password = _credential.Reveal();
        try
        {
            LiveSessionConnectOptions options = new(
                Enabled: true,
                _descriptor.Endpoint.Host,
                _descriptor.Endpoint.Port,
                _descriptor.Account,
                password,
                MapCharacterSelector(_descriptor.Character),
                Probe: _descriptor.Mode == HeadlessSessionMode.Probe);
            LiveSessionStartResult result = _liveSession.Start(options);
            if (result.Selection is { } selection)
                _accountName = selection.AccountName;
            RuntimeSessionStartResult converted = Convert(result);
            if (converted.Error is { } error)
            {
                _diagnostics.Failure(
                    _descriptor.Id,
                    reconnect ? "reconnect" : "start",
                    error);
            }
            _diagnostics.Lifecycle(
                _descriptor.Id,
                reconnect ? "reconnect-result" : "start-result",
                Runtime);
            return converted;
        }
        finally
        {
            password = string.Empty;
        }
    }

    private LiveChatCommandBindings CreateChatCommandBindings(
        AcDream.Core.Net.WorldSession session,
        GameRuntime runtime) => new(
        ExecuteClientCommand: command =>
            ExecuteHeadlessClientCommand(session, runtime, command),
        Communication: runtime.CommunicationOwner,
        Chat: runtime.CommunicationOwner.Chat,
        TurbineChat: runtime.CommunicationOwner.TurbineChat,
        CharacterState: runtime.CharacterOwner,
        PlayerGuid: () => runtime.PlayerIdentity.ServerGuid,
        SendTalk: session.SendTalk,
        SendTell: session.SendTell,
        SendChannel: session.SendChannel,
        SendTurbineChat: session.SendTurbineChatTo,
        Log: message => _diagnostics.Message(
            _descriptor.Id,
            message,
            runtime.Generation.Value));

    private static void ExecuteHeadlessClientCommand(
        AcDream.Core.Net.WorldSession session,
        GameRuntime runtime,
        ExecuteClientCommandCmd command)
    {
        switch (command.Command)
        {
            case ClientCommandId.LifestoneRecall:
                session.SendTeleportToLifestone();
                return;
            case ClientCommandId.MarketplaceRecall:
                session.SendTeleportToMarketplace();
                return;
            case ClientCommandId.PkArenaRecall:
                session.SendTeleportToPkArena();
                return;
            case ClientCommandId.PkLiteArenaRecall:
                session.SendTeleportToPkLiteArena();
                return;
            case ClientCommandId.EnterPkLite:
                session.SendEnterPkLite();
                return;
            case ClientCommandId.HouseRecall:
                session.SendTeleportToHouse();
                return;
            case ClientCommandId.MansionRecall:
                session.SendTeleportToMansion();
                return;
            case ClientCommandId.QueryAge:
                session.SendQueryAge();
                return;
            case ClientCommandId.QueryBirth:
                session.SendQueryBirth();
                return;
            case ClientCommandId.TogglePersistentDaylight:
            {
                bool enabled = !runtime.CharacterOwner.Options.GetOptionBit(
                    CharacterOptionId.PersistentAtDay);
                _ = runtime.CharacterOwner.Options.TrySetOption(
                    (uint)CharacterOptionId.PersistentAtDay,
                    enabled,
                    session.SendSetSingleCharacterOption);
                return;
            }
            case ClientCommandId.Emote
                when !string.IsNullOrWhiteSpace(command.Arguments):
                session.SendEmote(command.Arguments.Trim());
                return;
            case ClientCommandId.ClearChat:
                runtime.CommunicationOwner.Chat.Clear();
                return;
            case ClientCommandId.ChatToggle:
                session.SendModifyGlobalSquelch(
                    command.Arguments.Equals(
                        "off",
                        StringComparison.OrdinalIgnoreCase),
                    2u);
                return;
            case ClientCommandId.NoTellToggle:
                session.SendModifyGlobalSquelch(
                    command.Arguments.Equals(
                        "on",
                        StringComparison.OrdinalIgnoreCase),
                    3u);
                return;
            case ClientCommandId.IndexChannels:
                session.SendIndexChannels();
                return;
            case ClientCommandId.ListChannel:
                SendResolvedChannel(
                    command.Arguments,
                    session.SendListChannel);
                return;
            case ClientCommandId.OnChannel:
                SendResolvedChannel(
                    command.Arguments,
                    session.SendOnChannel);
                return;
            case ClientCommandId.OffChannel:
                SendResolvedChannel(
                    command.Arguments,
                    session.SendOffChannel);
                return;
            case ClientCommandId.AllegianceHometown:
                session.SendRecallAllegianceHometown();
                return;
            case ClientCommandId.AllegianceInfo:
            case ClientCommandId.AllegianceBoot:
            case ClientCommandId.AllegianceBan:
            case ClientCommandId.AllegianceChat:
            case ClientCommandId.AllegianceBroadcast:
            case ClientCommandId.AllegianceOfficer:
            case ClientCommandId.AllegianceOfficerTitle:
            case ClientCommandId.AllegianceName:
            case ClientCommandId.AllegianceLock:
            case ClientCommandId.AllegianceHouse:
            case ClientCommandId.AllegianceMotd:
            case ClientCommandId.AllegianceUnrecognizedSubcommand:
            case ClientCommandId.HouseOpenStatus:
            case ClientCommandId.HouseStorage:
            case ClientCommandId.HouseBoot:
            case ClientCommandId.HouseBootAll:
            case ClientCommandId.HouseGuests:
            case ClientCommandId.HouseHooks:
            case ClientCommandId.HouseUnrecognizedSubcommand:
                _ = CreateAdministrationDispatcher(session, runtime).TryExecute(
                    command.Command,
                    command.Arguments);
                return;
            case ClientCommandId.Permit:
                ExecutePermit(session, command.Arguments);
                return;
            case ClientCommandId.HouseAvailableList
                when RetailClientCommandCatalog.TryResolveHouseType(
                    command.Arguments,
                    out uint houseType):
                session.SendListAvailableHouses(houseType);
                return;
            case ClientCommandId.JoinChannel
                when RetailClientCommandCatalog.TryResolveJoinLeaveOption(
                    command.Arguments,
                    out uint joinOption):
                _ = runtime.CharacterOwner.Options.TrySetOption(
                    joinOption,
                    true,
                    session.SendSetSingleCharacterOption);
                return;
            case ClientCommandId.LeaveChannel
                when RetailClientCommandCatalog.TryResolveJoinLeaveOption(
                    command.Arguments,
                    out uint leaveOption):
                _ = runtime.CharacterOwner.Options.TrySetOption(
                    leaveOption,
                    false,
                    session.SendSetSingleCharacterOption);
                return;
            default:
                throw new NotSupportedException(
                    $"Client command '{command.Command}' is not available "
                    + "in the headless host.");
        }

        static void SendResolvedChannel(
            string arguments,
            Action<uint> send)
        {
            if (!RetailChannelTagTable.TryResolve(
                    arguments.Trim(),
                    out uint channelId))
            {
                throw new InvalidOperationException(
                    $"Chat channel '{arguments.Trim()}' does not exist.");
            }
            send(channelId);
        }

        static void ExecutePermit(
            AcDream.Core.Net.WorldSession activeSession,
            string arguments)
        {
            string[] parts = arguments.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            string name = string.Join(' ', parts, 1, parts.Length - 1);
            if (parts[0].Equals(
                    "add",
                    StringComparison.OrdinalIgnoreCase))
            {
                activeSession.SendAddPlayerPermission(name);
            }
            else
            {
                activeSession.SendRemovePlayerPermission(name);
            }
        }
    }

    private static RetailAdministrationCommandDispatcher
        CreateAdministrationDispatcher(
            AcDream.Core.Net.WorldSession session,
            GameRuntime runtime) => new(
            new RetailAdministrationCommandDispatcher.FeedbackBindings(
                ShowSystemMessage: text => runtime.CommunicationOwner.AddText(
                    text, RetailLogTextType.Default),
                ShowClientLocalMessage: text => runtime.CommunicationOwner.AddText(
                    text, RetailLogTextType.ClientLocal),
                SetSingleCharacterOption: (optionId, enabled) =>
                    _ = runtime.CharacterOwner.Options.TrySetOption(
                        optionId,
                        enabled,
                        session.SendSetSingleCharacterOption),
                RequestAllegianceInfo: session.SendAllegianceInfoRequest),
            new RetailAdministrationCommandDispatcher.ActionBindings(
                BreakAllegianceBoot: session.SendBreakAllegianceBoot,
                AllegianceChatBoot: session.SendAllegianceChatBoot,
                AllegianceChatGag: session.SendAllegianceChatGag,
                AllegianceBroadcast: text => session.SendChannel(0x02000000u, text),
                ListAllegianceBans: session.SendListAllegianceBans,
                AddAllegianceBan: session.SendAddAllegianceBan,
                RemoveAllegianceBan: session.SendRemoveAllegianceBan,
                ListAllegianceOfficers: session.SendListAllegianceOfficers,
                ClearAllegianceOfficers: session.SendClearAllegianceOfficers,
                SetAllegianceOfficer: session.SendSetAllegianceOfficer,
                RemoveAllegianceOfficer: session.SendRemoveAllegianceOfficer,
                ListAllegianceOfficerTitles: session.SendListAllegianceOfficerTitles,
                ClearAllegianceOfficerTitles: session.SendClearAllegianceOfficerTitles,
                SetAllegianceOfficerTitle: session.SendSetAllegianceOfficerTitle,
                QueryAllegianceName: session.SendQueryAllegianceName,
                SetAllegianceName: session.SendSetAllegianceName,
                ClearAllegianceName: session.SendClearAllegianceName,
                AllegianceLockAction: session.SendAllegianceLockAction,
                SetAllegianceApprovedVassal: session.SendSetAllegianceApprovedVassal,
                AllegianceHouseAction: session.SendAllegianceHouseAction,
                QueryMotd: session.SendQueryMotd,
                SetMotd: session.SendSetMotd,
                ClearMotd: session.SendClearMotd,
                SetOpenHouseStatus: session.SendSetOpenHouseStatus,
                AddPermanentGuest: session.SendAddPermanentGuest,
                RemovePermanentGuest: session.SendRemovePermanentGuest,
                RemoveAllPermanentGuests: session.SendRemoveAllPermanentGuests,
                ChangeStoragePermission: session.SendChangeStoragePermission,
                AddAllStoragePermission: session.SendAddAllStoragePermission,
                RemoveAllStoragePermission: session.SendRemoveAllStoragePermission,
                RequestFullGuestList: session.SendRequestFullGuestList,
                BootSpecificHouseGuest: session.SendBootSpecificHouseGuest,
                BootEveryone: session.SendBootEveryone,
                SetHooksVisibility: session.SendSetHooksVisibility,
                ModifyAllegianceGuestPermission:
                    session.SendModifyAllegianceGuestPermission,
                ModifyAllegianceStoragePermission:
                    session.SendModifyAllegianceStoragePermission));

    private ILiveSessionEventRouting CreateEventRoute(
        AcDream.Core.Net.WorldSession session)
    {
        _currentSession = session;
        _pendingConfirmation = null;
        _optionsSeeder = new HeadlessCharacterOptionsSeeder(
            _declaredCharacterOptions,
            Runtime,
            Commands.Character);
        IRuntimeDirectWorldProjection? worldProjection = null;
        if (_contentLease is { } content)
        {
            _firstEntryDrive ??= new RuntimeFirstEntryDriveController(
                Runtime.EntityObjects,
                Runtime.Clock,
                content.PreparedCollision,
                () => PlayerMovementConstructionOptions.From(
                    Runtime.CharacterOwner.MovementSkills.Snapshot),
                static _ => new RuntimeLocalPlayerPhysicsActivationPreparation(
                    Radius: 0.48f,
                    Height: 1.835f,
                    RuntimeLocalPlayerShadowDisposition.ProvenShapeless));
            PhysicsDiagnostics.LocalTeleportHostKind = "headless";
            _acceptedPositionDrive ??= new RuntimeAcceptedPositionDriveController(
                Runtime.EntityObjects,
                Runtime.Clock,
                content.PreparedCollision,
                new LocalPlayerOutboundController((_, _, _, _, _, _) => { }),
                () => Runtime.Generation,
                () => Runtime.PlayerIdentity.ServerGuid,
                () => Runtime.MovementOwner.Controller,
                () => Runtime.CharacterOwner.UsePositionFromServer,
                () => _currentSession,
                () => Runtime.MovementOwner,
                isPortalAuthorityCurrent: portal => Runtime.TransitOwner
                    .CanPlacePortalDestination(
                        portal.RevealGeneration,
                        portal.TeleportSequence,
                        portal.Projection.DestinationCell));
            var projection = new HeadlessSessionWorldProjection(
                Runtime,
                content,
                _firstEntryDrive,
                _acceptedPositionDrive,
                onNonQuiescentStall: message => _diagnostics.Message(
                    _descriptor.Id,
                    message,
                    Runtime.Generation.Value));
            _worldProjection = projection;
            worldProjection = projection;
        }
        var entities = new RuntimeLiveEntitySessionController(
            Runtime,
            session,
            message => _diagnostics.Message(
                _descriptor.Id,
                message,
                Runtime.Generation.Value),
            worldProjection,
            _acceptedPositionDrive,
            // OP7: the first two of three production LoginComplete send
            // sites — see RuntimeLiveEntitySessionController's own doc.
            onLoginCompleteSent: () => _optionsSeeder?.NoteLoginCompleteSent());
        _entities = entities;
        var route = new LiveSessionEventRouter(
            session,
            entities.CreateSink(),
            new LiveEnvironmentSessionSink(
                change =>
                    _ = Runtime.EnvironmentOwner
                        .ApplyAdminEnvirons(change),
                Runtime.EnvironmentOwner.SynchronizeFromServer),
            new LiveInventorySessionBindings(
                Runtime.InventoryOwner.Objects,
                () => Runtime.PlayerIdentity.ServerGuid,
                Runtime.InventoryOwner.Shortcuts.Load,
                error =>
                {
                    Runtime.InventoryOwner.ExternalContainers
                        .ApplyUseDone(error);
                    Runtime.ActionOwner.SpellCast.CompleteUse(error);
                    Runtime.ActionOwner.Transactions.CompleteUse(error);
                },
                Runtime.InventoryOwner.ItemMana,
                Runtime.InventoryOwner.ExternalContainers,
                appraisal =>
                    Runtime.ActionOwner.Transactions
                        .AcceptAppraisalResponse(appraisal.Guid),
                Vendor: Runtime.InventoryOwner.Vendor),
            new LiveCharacterSessionBindings(
                Runtime.ActionOwner.Combat,
                Runtime.CharacterOwner,
                ResolveSkillFormulaBonus: null,
                OnSkillsUpdated: null,
                OnConfirmationRequest: request =>
                {
                    Console.WriteLine(
                        $"[fa6-diag] OnConfirmationRequest received type="
                        + $"{request.Type} context={request.ContextId} "
                        + $"text='{request.Message}'");
                    _pendingConfirmation = request;
                },
                OnConfirmationDone: null,
                ClientTime: () =>
                    Runtime.Clock.SimulationTimeSeconds,
                OnMovementStatsUpdated: null,
                OnCharacterOptionsChanged: (_, _) =>
                    _optionsSeeder?.NoteOptionsSeeded()),
            new LiveSocialSessionBindings(
                Runtime.CommunicationOwner.Chat,
                Runtime.CommunicationOwner.TurbineChat,
                Runtime.CommunicationOwner.Friends,
                Runtime.CommunicationOwner.Squelch,
                (text, type) => Runtime.CommunicationOwner.AddText(text, type),
                Fellowship: Runtime.FellowshipOwner,
                Allegiance: Runtime.AllegianceOwner,
                House: Runtime.HouseOwner,
                Contracts: Runtime.ContractsOwner,
                PlayerGuid: () => Runtime.PlayerIdentity.ServerGuid));
        var eventRoute = new HeadlessSessionEventRoute(
            route,
            Runtime,
            _placementSinkOverride
                ?? new HeadlessRuntimePlacementProjectionSink(Runtime),
            _firstEntryDrive,
            _ =>
            {
                session.SendGameAction(GameActionLoginComplete.Build());
                _optionsSeeder?.NoteLoginCompleteSent();
                session.SendHouseQuery();
            },
            _acceptedPositionDrive);
        _eventRoute = eventRoute;
        return eventRoute;
    }

    private static Dictionary<CharacterOptionId, bool> ParseDeclaredCharacterOptions(
        HeadlessSessionDescriptor descriptor)
    {
        var declared = new Dictionary<CharacterOptionId, bool>();
        if (descriptor.CharacterOptions is not { } options)
            return declared;
        foreach (KeyValuePair<string, bool> pair in options)
        {
            declared[Enum.Parse<CharacterOptionId>(pair.Key, ignoreCase: false)] =
                pair.Value;
        }
        return declared;
    }

    private static LiveSessionCharacterSelector? MapCharacterSelector(
        HeadlessCharacterSelector? selector) =>
        selector is null
            ? null
            : new(
                selector.Index,
                selector.Id,
                selector.Name);

    private RuntimeSessionStartResult Convert(
        LiveSessionStartResult result)
    {
        RuntimeSessionStartStatus status = result.Status switch
        {
            LiveSessionStartStatus.Disabled =>
                RuntimeSessionStartStatus.Disabled,
            LiveSessionStartStatus.MissingCredentials =>
                RuntimeSessionStartStatus.MissingCredentials,
            LiveSessionStartStatus.NoCharacters =>
                RuntimeSessionStartStatus.NoCharacters,
            LiveSessionStartStatus.Connected =>
                RuntimeSessionStartStatus.Connected,
            LiveSessionStartStatus.Deferred =>
                RuntimeSessionStartStatus.Deferred,
            LiveSessionStartStatus.Failed =>
                RuntimeSessionStartStatus.Failed,
            LiveSessionStartStatus.ProbeComplete =>
                RuntimeSessionStartStatus.ProbeComplete,
            _ => throw new ArgumentOutOfRangeException(
                nameof(result),
                result.Status,
                "Unknown live-session start result."),
        };
        return new RuntimeSessionStartResult(
            status,
            Runtime.Generation,
            result.Selection?.CharacterId ?? 0u,
            result.Selection?.CharacterName ?? string.Empty,
            result.Error);
    }
}
