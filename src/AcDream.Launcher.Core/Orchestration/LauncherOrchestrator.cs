using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Status;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Orchestration;

public sealed class LauncherOrchestrator : ILauncherOrchestrator
{
    private static readonly TimeSpan ServerSessionHold = TimeSpan.FromMinutes(3);

    private const string FirstRunRequired =
        "Client content is not configured. Complete the first-run setup before launching.";

    private readonly object _gate = new();
    private readonly LauncherProfileStore _profileStore;
    private readonly ApplicationPathSet _paths;
    private readonly LauncherExecutableSet _executables;
    private readonly LauncherPlatformCapabilities _platform;
    private readonly ILauncherSessionConfigService _configService;
    private readonly ILauncherProcessSupervisorFactory _supervisorFactory;
    private readonly IStatusEventSourceFactory _statusSourceFactory;
    private readonly Func<string> _sessionIdFactory;
    private readonly UpdateSessionBarrier _updateSessionBarrier;
    private readonly List<ManagedActivity> _activities = [];

    private LauncherInstallRecord? _installRecord;
    private string _installationStatus;
    private bool _disposed;

    public LauncherOrchestrator(
        LauncherProfileStore profileStore,
        ApplicationPathSet paths,
        LauncherExecutableSet executables,
        LauncherInstallRecord? installRecord = null,
        LauncherPlatformCapabilities? platform = null,
        ILauncherSessionConfigService? configService = null,
        ILauncherProcessSupervisorFactory? supervisorFactory = null,
        IStatusEventSourceFactory? statusSourceFactory = null,
        Func<string>? sessionIdFactory = null,
        string? installationStatus = null,
        UpdateSessionBarrier? updateSessionBarrier = null)
    {
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _executables = executables ?? throw new ArgumentNullException(nameof(executables));
        _installRecord = installRecord;
        _platform = platform ?? LauncherPlatformCapabilities.Detect();
        _configService = configService ?? new LauncherSessionConfigService();
        _supervisorFactory = supervisorFactory ?? new LauncherProcessSupervisorFactory();
        _statusSourceFactory = statusSourceFactory ?? new StatusFileTailerFactory();
        _sessionIdFactory = sessionIdFactory ?? CreateSessionId;
        _updateSessionBarrier = updateSessionBarrier
            ?? new UpdateSessionBarrier(paths.DataDirectory);
        _installationStatus = installationStatus
            ?? (installRecord is null
                ? FirstRunRequired
                : "Client content paths are configured.");
    }

    public event EventHandler? StateChanged;

    public void LoadProfiles()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _profileStore.Load();
        }

        RaiseStateChanged();
    }

    public LauncherStateSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            LauncherServerSnapshot[] servers = _profileStore.Document.Servers
                .Select(CreateServerSnapshotLocked)
                .ToArray();
            LauncherSessionSnapshot[] sessions = _activities
                .OrderByDescending(activity => activity.CreatedAt)
                .Select(activity => activity.ToSnapshot())
                .ToArray();

            return new LauncherStateSnapshot(
                servers,
                sessions,
                _platform,
                _installRecord is not null,
                _installationStatus);
        }
    }

    public LauncherCapability GetLaunchCapability(LaunchMode mode)
    {
        LauncherCapability platformCapability = _platform.ForLaunchMode(mode);
        if (!platformCapability.IsAvailable)
        {
            return platformCapability;
        }

        LauncherCapability executableCapability = _executables.GetAvailability(mode);
        if (!executableCapability.IsAvailable)
        {
            return executableCapability;
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            return _installRecord is null
                ? LauncherCapability.Unavailable(FirstRunRequired)
                : LauncherCapability.Available;
        }
    }

    public LauncherCapability GetAccountLaunchCapability(
        string serverName,
        string accountName,
        LaunchMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);

        LauncherCapability capability = GetLaunchCapability(mode);
        if (!capability.IsAvailable)
        {
            return capability;
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            _ = FindAccountLocked(serverName, accountName);
            ManagedActivity? active = FindActiveActivityLocked(serverName, accountName);
            if (active is not null)
            {
                return LauncherCapability.Unavailable(
                    $"Stop the running {active.Kind.ToString().ToLowerInvariant()} "
                    + "for this account before starting another activity.");
            }

            return GetReconnectHoldLocked(serverName, accountName);
        }
    }

    private LauncherCapability GetReconnectHoldLocked(
        string serverName,
        string accountName)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ManagedActivity? ungraceful = _activities
            .Where(activity =>
                activity.IsTerminal
                && !activity.ExitedGracefully
                && activity.TerminalAt is not null
                && string.Equals(activity.ServerName, serverName, StringComparison.Ordinal)
                && string.Equals(activity.AccountName, accountName, StringComparison.Ordinal))
            .OrderByDescending(activity => activity.TerminalAt)
            .FirstOrDefault();
        if (ungraceful?.TerminalAt is not { } terminalAt)
        {
            return LauncherCapability.Available;
        }

        TimeSpan remaining = ServerSessionHold - (now - terminalAt);
        if (remaining <= TimeSpan.Zero)
        {
            return LauncherCapability.Available;
        }

        int seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        return LauncherCapability.Unavailable(
            $"The last session for this account did not log out cleanly, so the "
            + $"server may still be holding it. Try again in {seconds} s.");
    }

    public LauncherCapability GetProbeCapability(string serverName, string accountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);

        LauncherCapability platformCapability =
            _platform.ForLaunchMode(LaunchMode.Headless);
        if (!platformCapability.IsAvailable)
        {
            return platformCapability;
        }

        LauncherCapability executableCapability =
            _executables.GetAvailability(LaunchMode.Headless);
        if (!executableCapability.IsAvailable)
        {
            return executableCapability;
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            _ = FindAccountLocked(serverName, accountName);
            if (_installRecord is null)
            {
                return LauncherCapability.Unavailable(FirstRunRequired);
            }

            ManagedActivity? active = FindActiveActivityLocked(serverName, accountName);
            return active is null
                ? LauncherCapability.Available
                : LauncherCapability.Unavailable(
                    $"Stop the running {active.Kind.ToString().ToLowerInvariant()} "
                    + "for this account before refreshing its characters.");
        }
    }

    public void SetInstallRecord(LauncherInstallRecord? installRecord)
    {
        SetInstallationState(
            installRecord,
            installRecord is null
                ? FirstRunRequired
                : "Client content SHA-256, size, and bake-tool version verified.");
    }

    public void SetInstallationState(
        LauncherInstallRecord? installRecord,
        string installationStatus)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationStatus);
        lock (_gate)
        {
            ThrowIfDisposed();
            _installRecord = installRecord;
            _installationStatus = installationStatus;
        }

        RaiseStateChanged();
    }

    public void AddServer(string name, string host, int port) =>
        MutateProfiles(() => _profileStore.AddServer(name, host, port));

    public void EditServer(string name, string newName, string newHost, int newPort) =>
        MutateProfiles(() =>
        {
            EnsureServerIdleLocked(name);
            _profileStore.EditServer(
                name,
                newName: newName,
                newHost: newHost,
                newPort: newPort);
        });

    public void RemoveServer(string name) =>
        MutateProfiles(() =>
        {
            EnsureServerIdleLocked(name);
            _profileStore.RemoveServer(name);
        });

    public void AddAccount(string serverName, string accountName, string password) =>
        MutateProfiles(() =>
            _profileStore.AddAccount(serverName, accountName, password));

    public void EditAccount(
        string serverName,
        string accountName,
        string newAccountName,
        string? newPassword) =>
        MutateProfiles(() =>
        {
            EnsureAccountIdleLocked(serverName, accountName);
            _profileStore.EditAccount(
                serverName,
                accountName,
                newAccount: newAccountName,
                newPassword: newPassword);
        });

    public void RemoveAccount(string serverName, string accountName) =>
        MutateProfiles(() =>
        {
            EnsureAccountIdleLocked(serverName, accountName);
            _profileStore.RemoveAccount(serverName, accountName);
        });

    public void AddCharacter(
        string serverName,
        string accountName,
        string characterName,
        string? characterId) =>
        MutateProfiles(() =>
            _profileStore.AddCharacter(
                serverName,
                accountName,
                characterName,
                characterId));

    public void EditCharacterIdentity(
        string serverName,
        string accountName,
        string characterName,
        string newCharacterName,
        string? newCharacterId) =>
        MutateProfiles(() =>
        {
            EnsureCharacterIdleLocked(serverName, accountName, characterName);
            _profileStore.EditCharacter(
                serverName,
                accountName,
                characterName,
                newName: newCharacterName,
                newId: newCharacterId);
        });

    public void UpdateCharacterSettings(
        string serverName,
        string accountName,
        string characterName,
        LaunchMode launchMode,
        IReadOnlyList<string> plugins,
        IReadOnlyList<string> loginCommands) =>
        MutateProfiles(() =>
            _profileStore.EditCharacter(
                serverName,
                accountName,
                characterName,
                launchMode: launchMode,
                plugins: plugins,
                loginCommands: loginCommands));

    public void RemoveCharacter(
        string serverName,
        string accountName,
        string characterName) =>
        MutateProfiles(() =>
        {
            EnsureCharacterIdleLocked(serverName, accountName, characterName);
            _profileStore.RemoveCharacter(serverName, accountName, characterName);
        });

    public Task<LauncherSessionSnapshot> LaunchAsync(
        string serverName,
        string accountName,
        string? characterName,
        LaunchMode mode,
        CancellationToken cancellationToken = default)
    {
        LauncherCapability capability = GetLaunchCapability(mode);
        if (!capability.IsAvailable)
        {
            throw new LauncherOperationException(capability.Reason ?? "Launch is unavailable.");
        }

        StartRequest request;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (FindActiveActivityLocked(serverName, accountName) is not null)
            {
                throw new LauncherOperationException(
                    "A session or character refresh is already running for this account.");
            }

            ServerProfile server = FindServerLocked(serverName);
            AccountProfile account = FindAccountLocked(serverName, accountName);
            CharacterProfile character;
            if (string.IsNullOrWhiteSpace(characterName))
            {
                if (mode != LaunchMode.GuiSelect)
                {
                    throw new LauncherOperationException(
                        "Select a cached character for GUI or headless launch.");
                }

                character = new CharacterProfile
                {
                    Name = string.Empty,
                    LaunchMode = LaunchMode.GuiSelect,
                    Plugins = [],
                    LoginCommands = [],
                };
            }
            else
            {
                character = FindCharacterLocked(
                    serverName,
                    accountName,
                    characterName);
            }
            LauncherInstallRecord install = _installRecord
                ?? throw new LauncherOperationException(FirstRunRequired);

            string sessionId = ReserveSessionIdLocked();
            var activity = new ManagedActivity(
                sessionId,
                LauncherActivityKind.Play,
                server.Name,
                account.Account,
                string.IsNullOrWhiteSpace(characterName) ? null : character.Name,
                mode,
                "Preparing session configuration…");
            _activities.Add(activity);

            request = new StartRequest(
                activity,
                CloneServer(server),
                CloneAccountWithoutCharacters(account),
                CloneCharacter(character, mode),
                install,
                account.Password,
                isProbe: false,
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            activity.StartCancellation = request.Cancellation;
        }

        RaiseStateChanged();
        return StartActivityAsync(request);
    }

    public Task<LauncherSessionSnapshot> ProbeAsync(
        string serverName,
        string accountName,
        CancellationToken cancellationToken = default)
    {
        LauncherCapability capability = GetProbeCapability(serverName, accountName);
        if (!capability.IsAvailable)
        {
            throw new LauncherOperationException(
                capability.Reason ?? "Character refresh is unavailable.");
        }

        StartRequest request;
        lock (_gate)
        {
            ThrowIfDisposed();

            if (FindActiveActivityLocked(serverName, accountName) is not null)
            {
                throw new LauncherOperationException(
                    "A session or character refresh is already running for this account.");
            }

            ServerProfile server = FindServerLocked(serverName);
            AccountProfile account = FindAccountLocked(serverName, accountName);
            LauncherInstallRecord install = _installRecord
                ?? throw new LauncherOperationException(FirstRunRequired);

            string sessionId = ReserveSessionIdLocked();
            var activity = new ManagedActivity(
                sessionId,
                LauncherActivityKind.Probe,
                server.Name,
                account.Account,
                characterName: null,
                launchMode: null,
                "Preparing character refresh…");
            _activities.Add(activity);

            request = new StartRequest(
                activity,
                CloneServer(server),
                CloneAccountWithoutCharacters(account),
                character: null,
                install,
                account.Password,
                isProbe: true,
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            activity.StartCancellation = request.Cancellation;
        }

        RaiseStateChanged();
        return StartActivityAsync(request);
    }

    public async Task StopSessionAsync(
        string sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        ILauncherProcessSupervisor? supervisor;
        CancellationTokenSource? startCancellation;
        lock (_gate)
        {
            ThrowIfDisposed();
            ManagedActivity activity = FindActivityLocked(sessionId);
            if (!activity.IsActive)
            {
                return;
            }

            activity.State = LauncherActivityState.Stopping;
            activity.Status = "Stopping session…";
            supervisor = activity.Supervisor;
            startCancellation = activity.StartCancellation;
        }

        RaiseStateChanged();
        cancellationToken.ThrowIfCancellationRequested();
        startCancellation?.Cancel();

        if (supervisor is null)
        {
            return;
        }

        try
        {
            await Task.Run(
                    () => supervisor.Stop(timeout),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                ManagedActivity activity = FindActivityLocked(sessionId);
                activity.Error = SafeError("Could not stop the session", ex, secret: null);
                activity.Status = activity.Error;
            }

            RaiseStateChanged();
            throw new LauncherOperationException(
                SafeError("Could not stop the session", ex, secret: null));
        }
    }

    public void PollStatus()
    {
        ManagedActivity[] activities;
        lock (_gate)
        {
            ThrowIfDisposed();
            activities = _activities
                .Where(activity => activity.StatusSource is not null)
                .ToArray();
        }

        bool changed = false;
        foreach (ManagedActivity activity in activities)
        {
            IReadOnlyList<StatusEvent> events;
            try
            {
                lock (activity.StatusReadGate)
                {
                    events = activity.StatusSource!.ReadNewEvents();
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (_activities.Contains(activity))
                    {
                        activity.Error = SafeError(
                            "Could not read the host status stream",
                            ex,
                            secret: null);
                        changed = true;
                    }
                }

                continue;
            }

            foreach (StatusEvent statusEvent in events)
            {
                ApplyStatusEvent(activity, statusEvent);
                changed = true;
            }
        }

        if (changed)
        {
            RaiseStateChanged();
        }
    }

    public void ClearFinishedSessions()
    {
        ManagedActivity[] removed;
        lock (_gate)
        {
            ThrowIfDisposed();
            removed = _activities.Where(activity => !activity.IsActive).ToArray();
            foreach (ManagedActivity activity in removed)
            {
                _activities.Remove(activity);
            }
        }

        foreach (ManagedActivity activity in removed)
        {
            DisposeActivity(activity);
        }

        if (removed.Length > 0)
        {
            RaiseStateChanged();
        }
    }

    public void Dispose()
    {
        ManagedActivity[] activities;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            activities = _activities.ToArray();
            _activities.Clear();
        }

        foreach (ManagedActivity activity in activities)
        {
            DisposeActivity(activity);
        }
    }

    private async Task<LauncherSessionSnapshot> StartActivityAsync(StartRequest request)
    {
        try
        {
            await Task.Run(
                    () => StartActivityCore(request),
                    CancellationToken.None)
                .ConfigureAwait(false);
            lock (_gate)
            {
                return request.Activity.ToSnapshot();
            }
        }
        finally
        {
            request.Password = null;
            lock (_gate)
            {
                if (ReferenceEquals(
                        request.Activity.StartCancellation,
                        request.Cancellation))
                {
                    request.Activity.StartCancellation = null;
                }
            }

            request.Cancellation.Dispose();
            request.Activity.StartCompleted.Set();
        }
    }

    private void StartActivityCore(StartRequest request)
    {
        ILauncherProcessSupervisor? supervisor = null;
        string? password = request.Password;
        bool hostStarted = false;
        try
        {
            request.Cancellation.Token.ThrowIfCancellationRequested();

            UpdateSessionBarrier.SessionLease sessionLease =
                _updateSessionBarrier.AcquireSession();
            lock (_gate)
            {
                request.Activity.UpdateSessionLease = sessionLease;
            }

            ComposedSessionConfig composed = request.IsProbe
                ? _configService.ComposeProbeAndWrite(
                    request.Server,
                    request.Account,
                    request.Install,
                    _paths,
                    request.Activity.SessionId)
                : _configService.ComposeAndWrite(
                    request.Server,
                    request.Account,
                    request.Character!,
                    request.Install,
                    _paths,
                    request.Activity.SessionId);

            request.Cancellation.Token.ThrowIfCancellationRequested();

            supervisor = _supervisorFactory.Create();
            EventHandler<LauncherSessionState> stateHandler =
                (_, state) => ApplySupervisorState(request.Activity, state);
            supervisor.StateChanged += stateHandler;
            IStatusEventSource statusSource =
                _statusSourceFactory.Create(composed.StatusFilePath);

            lock (_gate)
            {
                request.Activity.Supervisor = supervisor;
                request.Activity.SupervisorStateHandler = stateHandler;
                request.Activity.StatusSource = statusSource;
                request.Activity.Status = "Starting host process…";
            }

            RaiseStateChanged();
            request.Cancellation.Token.ThrowIfCancellationRequested();

            LauncherProcessSpec processSpec = request.IsProbe
                ? _executables.CreateProbeSpec(
                    composed.ConfigFilePath,
                    composed.StderrLogPath)
                : _executables.CreatePlaySpec(
                    request.Activity.LaunchMode!.Value,
                    composed.ConfigFilePath,
                    composed.StderrLogPath);
            supervisor.Start(processSpec, password);
            hostStarted = true;

            request.Password = null;
            password = null;

            if (request.Cancellation.IsCancellationRequested)
            {
                TryStop(supervisor);
                request.Cancellation.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
            if (supervisor is not null)
            {
                TryStop(supervisor);
            }

            lock (_gate)
            {
                if (!request.Activity.IsTerminal)
                {
                    request.Activity.State = LauncherActivityState.Cancelled;
                    request.Activity.Status = "Operation cancelled.";
                    request.Activity.Error = null;
                }
            }

            RaiseStateChanged();
            throw;
        }
        catch (Exception ex)
        {
            string message = SafeError(
                request.IsProbe
                    ? "Could not refresh characters"
                    : "Could not launch the client",
                ex,
                password);
            lock (_gate)
            {
                if (!request.Activity.IsTerminal)
                {
                    request.Activity.State = LauncherActivityState.Failed;
                    request.Activity.Status = message;
                    request.Activity.Error = message;
                }
            }

            RaiseStateChanged();
            throw new LauncherOperationException(message);
        }
        finally
        {
            request.Password = null;
            if (!hostStarted)
            {
                ReleaseUpdateSessionLease(request.Activity);
            }
        }
    }

    private void ApplySupervisorState(
        ManagedActivity activity,
        LauncherSessionState processState)
    {
        UpdateSessionBarrier.SessionLease? sessionLease = null;
        try
        {
            lock (_gate)
            {
                if (!_activities.Contains(activity))
                {
                    return;
                }

                switch (processState)
                {
                    case LauncherSessionState.Starting:
                        if (activity.State == LauncherActivityState.Starting)
                        {
                            activity.Status = "Starting host process…";
                        }
                        break;
                    case LauncherSessionState.Running:
                        if (activity.State is LauncherActivityState.Starting)
                        {
                            activity.State = LauncherActivityState.Running;
                            activity.Status = "Host process running; waiting for connection…";
                        }
                        break;
                    case LauncherSessionState.Exited:
                        activity.ExitCode ??= activity.Supervisor?.ExitCode;
                        if (!activity.IsTerminal)
                        {
                            activity.State = LauncherActivityState.Exited;
                            activity.Status = activity.HostTerminalStatus
                                ?? (activity.ExitCode is int code
                                    ? $"Host process exited with code {code}."
                                    : "Host process exited.");
                        }
                        else if (activity.State == LauncherActivityState.Exited
                            && activity.HostTerminalStatus is not null)
                        {
                            activity.Status = activity.HostTerminalStatus;
                        }
                        sessionLease = activity.UpdateSessionLease;
                        activity.UpdateSessionLease = null;
                        break;
                }
            }

            sessionLease?.Dispose();
            RaiseStateChanged();
        }
        catch
        {
        }
    }

    private void ApplyStatusEvent(ManagedActivity activity, StatusEvent statusEvent)
    {
        lock (_gate)
        {
            if (!_activities.Contains(activity))
            {
                return;
            }

            if (!string.Equals(
                    statusEvent.SessionId,
                    activity.SessionId,
                    StringComparison.Ordinal))
            {
                activity.Error = "Ignored a status event for a different session id.";
                return;
            }

            if (activity.IsTerminal)
            {
                switch (statusEvent)
                {
                    case ExitedStatusEvent exited:
                        activity.ExitCode ??= exited.Code;
                        activity.HostReportedExit = true;
                        activity.ExitReason ??= exited.Reason;
                        activity.HostTerminalStatus ??=
                            $"Exited: {exited.Reason} (code {exited.Code}).";
                        if (activity.State == LauncherActivityState.Exited)
                        {
                            activity.Status = activity.HostTerminalStatus;
                        }
                        break;
                    case CharacterListStatusEvent roster:
                        ApplyRosterLocked(activity, roster, updateStatus: false);
                        break;
                }

                return;
            }

            switch (statusEvent)
            {
                case StartedStatusEvent:
                    activity.Status = "Host started.";
                    break;
                case ConnectedStatusEvent:
                    if (activity.State != LauncherActivityState.Stopping)
                    {
                        activity.State = LauncherActivityState.Connected;
                    }
                    activity.Status = "Connected; waiting for character roster…";
                    break;
                case CharacterListStatusEvent roster:
                    ApplyRosterLocked(activity, roster);
                    break;
                case EnteredWorldStatusEvent enteredWorld:
                    if (activity.State != LauncherActivityState.Stopping)
                    {
                        activity.State = LauncherActivityState.InWorld;
                    }
                    if (!string.IsNullOrWhiteSpace(enteredWorld.CharacterName))
                    {
                        activity.CharacterName = enteredWorld.CharacterName;
                    }
                    activity.Status = $"In world as {enteredWorld.CharacterName}.";
                    break;
                case PluginLoadedStatusEvent loaded:
                    activity.Status = $"Plugin loaded: {loaded.Plugin}.";
                    break;
                case PluginFailedStatusEvent failed:
                    activity.Error = $"Plugin failed: {failed.Plugin}: {failed.Error}";
                    activity.Status = activity.Error;
                    break;
                case LoginCommandFailedStatusEvent failed:
                    activity.Error =
                        $"Login command {failed.CommandIndex} failed: {failed.Error}";
                    activity.Status = activity.Error;
                    break;
                case DisconnectedStatusEvent disconnected:
                    if (activity.State != LauncherActivityState.Stopping)
                    {
                        activity.State = LauncherActivityState.Disconnected;
                    }
                    activity.Status = $"Disconnected: {disconnected.Reason}.";
                    break;
                case ExitedStatusEvent exited:
                    activity.State = LauncherActivityState.Exited;
                    activity.ExitCode = exited.Code;
                    activity.HostReportedExit = true;
                    activity.ExitReason = exited.Reason;
                    activity.HostTerminalStatus =
                        $"Exited: {exited.Reason} (code {exited.Code}).";
                    activity.Status = activity.HostTerminalStatus;
                    break;
                case MalformedStatusEvent malformed:
                    activity.Error = $"Malformed host status event: {malformed.Error}";
                    break;
                case UnknownStatusEvent unknown:
                    activity.Status = string.IsNullOrWhiteSpace(unknown.E)
                        ? "Ignored an unreadable host status event."
                        : $"Ignored unknown host event '{unknown.E}'.";
                    break;
            }
        }
    }

    private void ApplyRosterLocked(
        ManagedActivity activity,
        CharacterListStatusEvent roster,
        bool updateStatus = true)
    {
        if (!string.Equals(
                roster.AccountName,
                activity.AccountName,
                StringComparison.Ordinal))
        {
            activity.Error =
                "Ignored a character roster whose account did not match the launched account.";
            return;
        }

        try
        {
            _profileStore.ExecuteTransaction(() =>
                _profileStore.MergeRoster(
                    activity.ServerName,
                    activity.AccountName,
                    roster.Characters
                        .Select(character => new CharacterRosterEntry(
                            character.Id,
                            character.Name,
                            character.SecondsGreyedOut))
                        .ToArray()));
            if (updateStatus)
            {
                activity.Status = roster.Characters.Count == 1
                    ? "Character roster refreshed: 1 character."
                    : $"Character roster refreshed: {roster.Characters.Count} characters.";
            }
        }
        catch (Exception ex)
        {
            activity.Error = SafeError(
                "Could not save the refreshed character roster",
                ex,
                secret: null);
            if (updateStatus)
            {
                activity.Status = activity.Error;
            }
        }
    }

    private LauncherServerSnapshot CreateServerSnapshotLocked(ServerProfile server)
    {
        LauncherAccountSnapshot[] accounts = server.Accounts
            .Select(account => CreateAccountSnapshotLocked(server, account))
            .ToArray();
        return new LauncherServerSnapshot(
            server.Name,
            server.Host,
            server.Port,
            accounts);
    }

    private LauncherAccountSnapshot CreateAccountSnapshotLocked(
        ServerProfile server,
        AccountProfile account)
    {
        ManagedActivity? active = FindActiveActivityLocked(server.Name, account.Account);
        LauncherCharacterSnapshot[] characters = account.Characters
            .Select(character =>
            {
                ManagedActivity? characterActivity = _activities
                    .LastOrDefault(candidate =>
                        candidate.IsActive
                        && candidate.Kind == LauncherActivityKind.Play
                        && string.Equals(
                            candidate.ServerName,
                            server.Name,
                            StringComparison.Ordinal)
                        && string.Equals(
                            candidate.AccountName,
                            account.Account,
                            StringComparison.Ordinal)
                        && string.Equals(
                            candidate.CharacterName,
                            character.Name,
                            StringComparison.Ordinal));
                return new LauncherCharacterSnapshot(
                    server.Name,
                    account.Account,
                    character.Name,
                    character.Id,
                    character.LaunchMode,
                    character.Plugins.ToArray(),
                    character.LoginCommands.ToArray(),
                    characterActivity is not null,
                    characterActivity?.Status ?? "Not running");
            })
            .ToArray();

        return new LauncherAccountSnapshot(
            server.Name,
            account.Account,
            characters,
            active is not null,
            active?.Status ?? "Idle");
    }

    private void MutateProfiles(Action mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_gate)
        {
            ThrowIfDisposed();
            _profileStore.ExecuteTransaction(mutation);
        }

        RaiseStateChanged();
    }

    private ServerProfile FindServerLocked(string serverName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        return _profileStore.Document.Servers.Find(server =>
                string.Equals(server.Name, serverName, StringComparison.Ordinal))
            ?? throw new LauncherProfileException($"No server named '{serverName}'.");
    }

    private AccountProfile FindAccountLocked(string serverName, string accountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ServerProfile server = FindServerLocked(serverName);
        return server.Accounts.Find(account =>
                string.Equals(account.Account, accountName, StringComparison.Ordinal))
            ?? throw new LauncherProfileException(
                $"No account '{accountName}' on server '{serverName}'.");
    }

    private CharacterProfile FindCharacterLocked(
        string serverName,
        string accountName,
        string characterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);
        AccountProfile account = FindAccountLocked(serverName, accountName);
        return account.Characters.Find(character =>
                string.Equals(character.Name, characterName, StringComparison.Ordinal))
            ?? throw new LauncherProfileException(
                $"No character '{characterName}' on account '{accountName}'.");
    }

    private ManagedActivity FindActivityLocked(string sessionId) =>
        _activities.Find(activity =>
            string.Equals(activity.SessionId, sessionId, StringComparison.Ordinal))
        ?? throw new LauncherOperationException($"No launcher session '{sessionId}'.");

    private ManagedActivity? FindActiveActivityLocked(
        string serverName,
        string accountName) =>
        _activities.LastOrDefault(activity =>
            activity.IsActive
            && string.Equals(activity.ServerName, serverName, StringComparison.Ordinal)
            && string.Equals(activity.AccountName, accountName, StringComparison.Ordinal));

    private void EnsureServerIdleLocked(string serverName)
    {
        if (_activities.Any(activity =>
                activity.IsActive
                && string.Equals(activity.ServerName, serverName, StringComparison.Ordinal)))
        {
            throw new LauncherOperationException(
                "Stop this server's running launcher sessions before editing or removing it.");
        }
    }

    private void EnsureAccountIdleLocked(string serverName, string accountName)
    {
        if (FindActiveActivityLocked(serverName, accountName) is not null)
        {
            throw new LauncherOperationException(
                "Stop this account's running launcher session before editing or removing it.");
        }
    }

    private void EnsureCharacterIdleLocked(
        string serverName,
        string accountName,
        string characterName)
    {
        if (_activities.Any(activity =>
                activity.IsActive
                && activity.Kind == LauncherActivityKind.Play
                && string.Equals(activity.ServerName, serverName, StringComparison.Ordinal)
                && string.Equals(activity.AccountName, accountName, StringComparison.Ordinal)
                && string.Equals(activity.CharacterName, characterName, StringComparison.Ordinal)))
        {
            throw new LauncherOperationException(
                "Stop this character's running session before editing or removing it.");
        }
    }

    private string ReserveSessionIdLocked()
    {
        string sessionId = _sessionIdFactory();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_activities.Any(activity => string.Equals(
                activity.SessionId,
                sessionId,
                StringComparison.Ordinal)))
        {
            throw new LauncherOperationException(
                $"The launcher generated duplicate session id '{sessionId}'.");
        }

        return sessionId;
    }

    private static ServerProfile CloneServer(ServerProfile source) =>
        new()
        {
            Name = source.Name,
            Host = source.Host,
            Port = source.Port,
        };

    private static AccountProfile CloneAccountWithoutCharacters(AccountProfile source) =>
        new()
        {
            Account = source.Account,
            Password = string.Empty,
        };

    private static CharacterProfile CloneCharacter(
        CharacterProfile source,
        LaunchMode mode) =>
        new()
        {
            Name = source.Name,
            Id = source.Id,
            LaunchMode = mode,
            Plugins = [.. source.Plugins],
            LoginCommands = [.. source.LoginCommands],
        };

    private static string CreateSessionId() =>
        $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";

    private static void TryStop(ILauncherProcessSupervisor supervisor)
    {
        try
        {
            supervisor.Stop(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }
    }

    private static string SafeError(string prefix, Exception exception, string? secret)
    {
        string detail = exception.Message;
        if (!string.IsNullOrEmpty(secret))
        {
            detail = detail.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(detail)
            ? prefix + "."
            : $"{prefix}: {detail}";
    }

    private void RaiseStateChanged()
    {
        Delegate[] subscribers = StateChanged?.GetInvocationList() ?? [];
        foreach (Delegate subscriber in subscribers)
        {
            try
            {
                ((EventHandler)subscriber)(this, EventArgs.Empty);
            }
            catch
            {
            }
        }
    }

    private static void DisposeActivity(ManagedActivity activity)
    {
        activity.StartCancellation?.Cancel();
        activity.StartCompleted.Wait();
        activity.StartCancellation?.Dispose();
        activity.StartCancellation = null;

        if (activity.Supervisor is not null)
        {
            activity.Supervisor.Stop(TimeSpan.FromSeconds(5));
            if (activity.SupervisorStateHandler is not null)
            {
                activity.Supervisor.StateChanged -= activity.SupervisorStateHandler;
            }

            activity.Supervisor.Dispose();
            activity.Supervisor = null;
        }

        ReleaseUpdateSessionLease(activity);
        activity.StartCompleted.Dispose();
    }

    private static void ReleaseUpdateSessionLease(ManagedActivity activity)
    {
        UpdateSessionBarrier.SessionLease? lease =
            Interlocked.Exchange(ref activity.UpdateSessionLease, null);
        lease?.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class ManagedActivity
    {
        public ManagedActivity(
            string sessionId,
            LauncherActivityKind kind,
            string serverName,
            string accountName,
            string? characterName,
            LaunchMode? launchMode,
            string status)
        {
            SessionId = sessionId;
            Kind = kind;
            ServerName = serverName;
            AccountName = accountName;
            CharacterName = characterName;
            LaunchMode = launchMode;
            Status = status;
            CreatedAt = DateTimeOffset.UtcNow;
        }

        public string SessionId { get; }

        public LauncherActivityKind Kind { get; }

        public string ServerName { get; }

        public string AccountName { get; }

        public string? CharacterName { get; set; }

        public LaunchMode? LaunchMode { get; }

        public DateTimeOffset CreatedAt { get; }

        private LauncherActivityState _state = LauncherActivityState.Starting;

        public LauncherActivityState State
        {
            get => _state;
            set
            {
                _state = value;
                if (IsTerminal)
                {
                    TerminalAt ??= DateTimeOffset.UtcNow;
                }
            }
        }

        public string Status { get; set; }

        public int? ExitCode { get; set; }

        public string? Error { get; set; }

        public string? HostTerminalStatus { get; set; }

        /// <summary>LU9: the host's own terminal reason token, set only when
        /// the host actually reported "exited" — so null means it never ran
        /// its own teardown.</summary>
        public string? ExitReason { get; set; }

        /// <summary>LU9: the host reported its own terminal event, as opposed
        /// to the launcher merely observing the process disappear.</summary>
        public bool HostReportedExit { get; set; }

        public DateTimeOffset? TerminalAt { get; set; }

        public bool ExitedGracefully => HostReportedExit && ExitCode == 0;

        public ILauncherProcessSupervisor? Supervisor { get; set; }

        public EventHandler<LauncherSessionState>? SupervisorStateHandler { get; set; }

        public IStatusEventSource? StatusSource { get; set; }

        public CancellationTokenSource? StartCancellation { get; set; }

        public UpdateSessionBarrier.SessionLease? UpdateSessionLease;

        public ManualResetEventSlim StartCompleted { get; } = new(false);

        public object StatusReadGate { get; } = new();

        public bool IsActive => State is not (
            LauncherActivityState.Exited
            or LauncherActivityState.Failed
            or LauncherActivityState.Cancelled);

        public bool IsTerminal => !IsActive;

        public LauncherSessionSnapshot ToSnapshot() =>
            new(
                SessionId,
                Kind,
                ServerName,
                AccountName,
                CharacterName,
                LaunchMode,
                State,
                Status,
                ExitCode,
                Error,
                CreatedAt,
                ExitReason,
                ExitedGracefully);
    }

    private sealed class StartRequest(
        ManagedActivity activity,
        ServerProfile server,
        AccountProfile account,
        CharacterProfile? character,
        LauncherInstallRecord install,
        string password,
        bool isProbe,
        CancellationTokenSource cancellation)
    {
        public ManagedActivity Activity { get; } = activity;

        public ServerProfile Server { get; } = server;

        public AccountProfile Account { get; } = account;

        public CharacterProfile? Character { get; } = character;

        public LauncherInstallRecord Install { get; } = install;

        public string? Password { get; set; } = password;

        public bool IsProbe { get; } = isProbe;

        public CancellationTokenSource Cancellation { get; } = cancellation;
    }
}
