using System.Runtime.ExceptionServices;
using AcDream.Core.Net;
using AcDream.Runtime.Chat;

namespace AcDream.Runtime.Session;

public sealed record LiveSessionRoutingFactories(
    Func<WorldSession, ILiveSessionEventRouting> CreateEvents,
    Func<WorldSession, ILiveSessionCommandRouting> CreateCommands);

public sealed record LiveSessionSelectionBindings(
    Action<uint> SetPlayerIdentity,
    Action<uint> SetVitalsIdentity,
    Action<uint> SetChatIdentity,
    Action<uint> MarkPersistent,
    Action<uint> SetVanishProbeIdentity,
    Action ClearCombat,
    Action? ArmLoginTunnel = null);

public sealed record LiveSessionEnteredWorldBindings(
    Action<string> SetActiveCharacter,
    Action RestoreLayout,
    Action SyncToolbar,
    Action<string> LoadCharacterSettings,
    Action ArmPlayerModeAutoEntry,
    Action? ResumeWorldAudio = null);

public sealed record LiveSessionHostBindings(
    LiveSessionRoutingFactories Routing,
    Action<RuntimeGenerationToken> Reset,
    LiveSessionSelectionBindings Selection,
    LiveSessionEnteredWorldBindings EnteredWorld,
    Action<string, int, string> Connecting,
    Action Connected,
    Action<LiveSessionRosterReport> Roster,
    Action<LiveSessionCharacterSelection> CharacterEntered,
    LoginCommandSequence? LoginCommands = null,
    Action<RuntimeCharacterCreationIdentity>? CharacterCreated = null,
    Action<RuntimeCharacterCreationRejection>? CreationFailed = null);

public sealed class LiveSessionHost
    : IRuntimeSessionCommands,
      IRuntimeLiveSessionFramePhase
{
    private sealed class PendingRouteRollback(
        ILiveSessionCommandRouting? commands,
        ILiveSessionEventRouting? events)
    {
        private ILiveSessionCommandRouting? _commands = commands;
        private ILiveSessionEventRouting? _events = events;

        public bool IsComplete => _commands is null && _events is null;

        public void Drain()
        {
            List<Exception>? failures = null;
            TryDispose(ref _commands, ref failures);
            TryDispose(ref _events, ref failures);
            if (failures is not null)
            {
                throw new AggregateException(
                    "Live-session route rollback did not converge.",
                    failures);
            }
        }

        private static void TryDispose<TOwner>(
            ref TOwner? owner,
            ref List<Exception>? failures)
            where TOwner : class, IDisposable
        {
            if (owner is null)
                return;
            try
            {
                owner.Dispose();
                owner = null;
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
        }
    }

    private readonly LiveSessionController _controller;
    private readonly LiveSessionConnectOptions? _connectOptions;
    private readonly LiveSessionRoutingFactories _routing;
    private readonly LiveSessionSelectionBindings _selection;
    private readonly LiveSessionEnteredWorldBindings _enteredWorld;
    private readonly Action<LiveSessionCharacterSelection> _characterEntered;
    private readonly Action<RuntimeGenerationToken> _reset;
    private readonly LiveSessionLifecycleHost _lifecycle;
    private readonly LoginCommandSequence? _loginCommands;
    private PendingRouteRollback? _pendingRouteRollback;

    public LiveSessionHost(
        LiveSessionController controller,
        LiveSessionHostBindings bindings,
        LiveSessionConnectOptions? connectOptions = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _connectOptions = connectOptions;
        ArgumentNullException.ThrowIfNull(bindings);
        _routing = bindings.Routing ?? throw new ArgumentNullException(nameof(bindings.Routing));
        _selection = bindings.Selection ?? throw new ArgumentNullException(nameof(bindings.Selection));
        _enteredWorld = bindings.EnteredWorld
            ?? throw new ArgumentNullException(nameof(bindings.EnteredWorld));
        _characterEntered = bindings.CharacterEntered
            ?? throw new ArgumentNullException(nameof(bindings.CharacterEntered));
        _loginCommands = bindings.LoginCommands;
        ArgumentNullException.ThrowIfNull(_routing.CreateEvents);
        ArgumentNullException.ThrowIfNull(_routing.CreateCommands);
        ArgumentNullException.ThrowIfNull(bindings.Reset);
        ArgumentNullException.ThrowIfNull(bindings.Connecting);
        ArgumentNullException.ThrowIfNull(bindings.Connected);
        ArgumentNullException.ThrowIfNull(bindings.Roster);
        Validate(_selection, _enteredWorld);

        _reset = bindings.Reset;
        _lifecycle = new LiveSessionLifecycleHost(new LiveSessionLifecycleBindings(
            Bind: BindSession,
            Reset: ResetSessionState,
            Connecting: bindings.Connecting,
            Connected: bindings.Connected,
            Roster: bindings.Roster,
            Selected: ApplySelection,
            Entered: ApplyEnteredWorld,
            CharacterCreated: bindings.CharacterCreated,
            CreationFailed: bindings.CreationFailed));
    }

    public WorldSession? CurrentSession => _controller.CurrentSession;
    public IRuntimeConnectionView Connection => _controller.Connection;
    public bool IsInWorld => _controller.IsInWorld;

    public LiveSessionStartResult Start(LiveSessionConnectOptions options) =>
        _controller.Start(options, _lifecycle);

    public LiveSessionStartResult Reconnect(LiveSessionConnectOptions options) =>
        _controller.Reconnect(options, _lifecycle);

    public RuntimeSessionStartResult Start(
        RuntimeGenerationToken expectedGeneration)
    {
        if (!TryValidate(expectedGeneration, out RuntimeSessionStartResult rejected))
            return rejected;
        if (_connectOptions is null)
        {
            return new RuntimeSessionStartResult(
                RuntimeSessionStartStatus.Inactive,
                _controller.Generation);
        }

        return Convert(_controller.Start(_connectOptions, _lifecycle));
    }

    public RuntimeSessionStartResult Reconnect(
        RuntimeGenerationToken expectedGeneration)
    {
        if (!TryValidate(expectedGeneration, out RuntimeSessionStartResult rejected))
            return rejected;
        if (_connectOptions is null)
        {
            return new RuntimeSessionStartResult(
                RuntimeSessionStartStatus.Inactive,
                _controller.Generation);
        }

        return Convert(_controller.Reconnect(_connectOptions, _lifecycle));
    }

    public RuntimeTeardownAcknowledgement Stop(
        RuntimeGenerationToken expectedGeneration) =>
        _controller.Stop(expectedGeneration);

    public void Tick()
    {
        _controller.Tick();
        _loginCommands?.Tick(_controller.Generation, _controller.IsInWorld);
    }

    private LiveSessionBinding BindSession(WorldSession session)
    {
        DrainPendingRouteRollback();

        ILiveSessionEventRouting? events = null;
        ILiveSessionCommandRouting? commands = null;
        try
        {
            events = _routing.CreateEvents(session)
                ?? throw new InvalidOperationException(
                    "The live-session event factory returned null.");
            events.Attach();
            commands = _routing.CreateCommands(session)
                ?? throw new InvalidOperationException(
                    "The live-session command factory returned null.");
            return new LiveSessionBinding(
                session,
                activateCommands: commands.Activate,
                deactivateCommands: commands.Dispose,
                detachEvents: events.Dispose);
        }
        catch (Exception creationError)
        {
            RethrowWithRetryableRollback(creationError, commands, events);
            throw;
        }
    }

    private void ResetSessionState(
        RuntimeGenerationToken retiringGeneration)
    {
        DrainPendingRouteRollback();
        _loginCommands?.Cancel(retiringGeneration);
        _reset(retiringGeneration);
    }

    private void ApplySelection(LiveSessionCharacterSelection selection)
    {
        uint id = selection.CharacterId;
        _selection.SetPlayerIdentity(id);
        _selection.SetVitalsIdentity(id);
        _selection.SetChatIdentity(id);
        _selection.MarkPersistent(id);
        _selection.SetVanishProbeIdentity(id);
        _selection.ClearCombat();
        _selection.ArmLoginTunnel?.Invoke();
    }

    private void ApplyEnteredWorld(LiveSessionCharacterSelection selection)
    {
        string name = selection.CharacterName;
        _enteredWorld.ResumeWorldAudio?.Invoke();
        _enteredWorld.SetActiveCharacter(name);
        _enteredWorld.RestoreLayout();
        _enteredWorld.SyncToolbar();
        _enteredWorld.LoadCharacterSettings(name);
        _enteredWorld.ArmPlayerModeAutoEntry();
        _characterEntered(selection);
        _loginCommands?.EnteredWorld(_controller.Generation);
    }

    private void RethrowWithRetryableRollback(
        Exception creationError,
        ILiveSessionCommandRouting? commands,
        ILiveSessionEventRouting? events)
    {
        _pendingRouteRollback = new PendingRouteRollback(commands, events);
        try
        {
            DrainPendingRouteRollback();
        }
        catch (AggregateException cleanupError)
        {
            var failures = new List<Exception> { creationError };
            failures.AddRange(cleanupError.InnerExceptions);
            throw new AggregateException(
                "Live-session route construction and rollback both failed.",
                failures);
        }

        ExceptionDispatchInfo.Capture(creationError).Throw();
    }

    private void DrainPendingRouteRollback()
    {
        if (_pendingRouteRollback is not { } rollback)
            return;
        rollback.Drain();
        if (rollback.IsComplete)
            _pendingRouteRollback = null;
    }

    private static void Validate(
        LiveSessionSelectionBindings selection,
        LiveSessionEnteredWorldBindings entered)
    {
        ArgumentNullException.ThrowIfNull(selection.SetPlayerIdentity);
        ArgumentNullException.ThrowIfNull(selection.SetVitalsIdentity);
        ArgumentNullException.ThrowIfNull(selection.SetChatIdentity);
        ArgumentNullException.ThrowIfNull(selection.MarkPersistent);
        ArgumentNullException.ThrowIfNull(selection.SetVanishProbeIdentity);
        ArgumentNullException.ThrowIfNull(selection.ClearCombat);
        ArgumentNullException.ThrowIfNull(entered.SetActiveCharacter);
        ArgumentNullException.ThrowIfNull(entered.RestoreLayout);
        ArgumentNullException.ThrowIfNull(entered.SyncToolbar);
        ArgumentNullException.ThrowIfNull(entered.LoadCharacterSettings);
        ArgumentNullException.ThrowIfNull(entered.ArmPlayerModeAutoEntry);
    }

    private bool TryValidate(
        RuntimeGenerationToken expectedGeneration,
        out RuntimeSessionStartResult rejected)
    {
        RuntimeGenerationToken current = _controller.Generation;
        if (expectedGeneration != current)
        {
            rejected = new RuntimeSessionStartResult(
                RuntimeSessionStartStatus.StaleGeneration,
                current);
            return false;
        }

        rejected = default;
        return true;
    }

    private RuntimeSessionStartResult Convert(LiveSessionStartResult result)
    {
        RuntimeSessionStartStatus status = result.Status switch
        {
            LiveSessionStartStatus.Disabled => RuntimeSessionStartStatus.Disabled,
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
            LiveSessionStartStatus.AwaitingCharacterSelection =>
                RuntimeSessionStartStatus.AwaitingCharacterSelection,
            _ => throw new ArgumentOutOfRangeException(
                nameof(result),
                result.Status,
                "Unknown live-session start result."),
        };
        return new RuntimeSessionStartResult(
            status,
            _controller.Generation,
            result.Selection?.CharacterId ?? 0u,
            result.Selection?.CharacterName ?? string.Empty,
            result.Error);
    }
}
