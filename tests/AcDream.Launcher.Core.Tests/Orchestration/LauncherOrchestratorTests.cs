using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Status;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Orchestration;

public sealed class LauncherOrchestratorTests : IDisposable
{
    private const string Password = "launcher-only-secret";
    private readonly string _root;
    private readonly ApplicationPathSet _paths;

    public LauncherOrchestratorTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "acdream-la4-orchestrator-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new ApplicationPathSet(
            Path.Combine(_root, "config"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "cache"),
            null);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AssetVersionCrashProducesActionableErrorWithoutExposingStderr()
    {
        var supervisors = new FakeSupervisorFactory();
        using var orchestrator = CreateOrchestrator(supervisorFactory: supervisors);
        await orchestrator.LaunchAsync("Local ACE", "testaccount", "+Acdream", LaunchMode.Gui);
        var supervisor = Assert.Single(supervisors.Created);
        string path = supervisor.Spec!.StderrLogPath!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "prepared asset package does not match: bake tool 10 != 6 " + Password);
        supervisor.Exit(1);
        string error = Assert.Single(orchestrator.GetSnapshot().Sessions).Error!;
        Assert.Contains("Update the client", error);
        Assert.DoesNotContain(Password, error);
    }

    [Fact]
    public async Task RunningHostHoldsSharedUpdateLeaseUntilProcessTerminalState()
    {
        var supervisors = new FakeSupervisorFactory();
        var barrier = new UpdateSessionBarrier(_paths.DataDirectory);
        using LauncherOrchestrator orchestrator = CreateOrchestrator(
            supervisorFactory: supervisors,
            updateSessionBarrier: barrier);

        _ = await orchestrator.LaunchAsync(
            "Local ACE",
            "testaccount",
            "+Acdream",
            LaunchMode.Gui);

        Assert.Throws<LauncherUpdateException>(barrier.AcquireExclusive);
        Assert.Single(supervisors.Created).Exit(0);
        using UpdateSessionBarrier.ExclusiveLease update = barrier.AcquireExclusive();
    }

    [Fact]
    public async Task DisposeKeepsUpdateLeaseUntilLiveChildIsObservedTerminal()
    {
        var supervisors = new BlockingStopSupervisorFactory();
        var barrier = new UpdateSessionBarrier(_paths.DataDirectory);
        LauncherOrchestrator orchestrator = CreateOrchestrator(
            supervisorFactory: supervisors,
            updateSessionBarrier: barrier);
        try
        {
            _ = await orchestrator.LaunchAsync(
                "Local ACE",
                "testaccount",
                "+Acdream",
                LaunchMode.Headless);
            BlockingStopSupervisor supervisor = Assert.Single(supervisors.Created);

            Task disposal = Task.Run(orchestrator.Dispose);
            Assert.True(supervisor.StopEntered.Wait(TimeSpan.FromSeconds(5)));

            Assert.False(disposal.IsCompleted);
            Assert.Throws<LauncherUpdateException>(barrier.AcquireExclusive);

            supervisor.AllowTerminal.Set();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            using UpdateSessionBarrier.ExclusiveLease update = barrier.AcquireExclusive();
            Assert.Equal(LauncherSessionState.Exited, supervisor.State);
            Assert.True(supervisor.Disposed);
        }
        finally
        {
            supervisors.AllowEveryStop();
            orchestrator.Dispose();
        }
    }

    [Fact]
    public void SnapshotProjectsTheFullHierarchyWithoutTheCredential()
    {
        using LauncherOrchestrator orchestrator = CreateOrchestrator();

        LauncherStateSnapshot snapshot = orchestrator.GetSnapshot();

        LauncherServerSnapshot server = Assert.Single(snapshot.Servers);
        LauncherAccountSnapshot account = Assert.Single(server.Accounts);
        LauncherCharacterSnapshot character = Assert.Single(account.Characters);
        Assert.Equal("Local ACE", server.Name);
        Assert.Equal("testaccount", account.AccountName);
        Assert.Equal("+Acdream", character.Name);

        string serialized = System.Text.Json.JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain(Password, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(LauncherAccountSnapshot).GetProperties(),
            property => property.Name.Contains("password", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LaunchComposesTheSelectedModeAndSpawnsThroughTheInjectedSeam()
    {
        var config = new RecordingConfigService();
        var supervisors = new FakeSupervisorFactory();
        var statusSources = new QueueStatusSourceFactory();
        using LauncherOrchestrator orchestrator = CreateOrchestrator(
            configService: config,
            supervisorFactory: supervisors,
            statusSourceFactory: statusSources);

        LauncherSessionSnapshot launched = await orchestrator.LaunchAsync(
            "Local ACE",
            "testaccount",
            "+Acdream",
            LaunchMode.Gui);

        Assert.Equal(LauncherActivityState.Running, launched.State);
        Assert.Equal(1, config.PlayCallCount);
        Assert.Equal(0, config.ProbeCallCount);
        Assert.Equal(LaunchMode.Gui, config.LastCharacter!.LaunchMode);
        Assert.Equal(["ExamplePlugin"], config.LastCharacter.Plugins);
        Assert.Equal(["/vt start"], config.LastCharacter.LoginCommands);
        Assert.Equal(string.Empty, config.LastAccountPassword);

        FakeSupervisor supervisor = Assert.Single(supervisors.Created);
        Assert.Equal("gui-host", supervisor.Spec!.ExecutablePath);
        Assert.Equal(
            ["--session-config", config.LastComposed!.ConfigFilePath],
            supervisor.Spec.Arguments);
        Assert.Equal(Password, supervisor.PasswordWrittenToStdin);
        Assert.DoesNotContain(
            Password,
            string.Join(' ', supervisor.Spec.Arguments),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Password,
            SessionConfigComposer.Serialize(config.LastComposed.Document),
            StringComparison.Ordinal);

        QueueStatusSource source = Assert.Single(statusSources.Created);
        source.Enqueue(Connected("s1"));
        source.Enqueue(EnteredWorld("s1", "+Acdream"));
        orchestrator.PollStatus();

        LauncherSessionSnapshot inWorld = Assert.Single(orchestrator.GetSnapshot().Sessions);
        Assert.Equal(LauncherActivityState.InWorld, inWorld.State);
        Assert.Contains("+Acdream", inWorld.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginCommandFailureIsVisibleButDoesNotMakeTheSessionTerminal()
    {
        var statusSources = new QueueStatusSourceFactory();
        using LauncherOrchestrator orchestrator = CreateOrchestrator(
            statusSourceFactory: statusSources);
        _ = await orchestrator.LaunchAsync(
            "Local ACE",
            "testaccount",
            "+Acdream",
            LaunchMode.Headless);

        QueueStatusSource source = Assert.Single(statusSources.Created);
        source.Enqueue(Connected("s1"));
        source.Enqueue(EnteredWorld("s1", "+Acdream"));
        source.Enqueue(new LoginCommandFailedStatusEvent
        {
            V = 1,
            E = "loginCommandFailed",
            T = DateTimeOffset.UtcNow,
            SessionId = "s1",
            CommandIndex = 2,
            Command = "/version",
            Error = "not available in the headless host",
        });

        orchestrator.PollStatus();

        LauncherSessionSnapshot session = Assert.Single(
            orchestrator.GetSnapshot().Sessions);
        Assert.Equal(LauncherActivityState.InWorld, session.State);
        Assert.Equal(
            "Login command 2 failed: not available in the headless host",
            session.Error);
        Assert.Equal(session.Error, session.Status);
        Assert.Null(session.ExitCode);
    }

    [Fact]
    public async Task AccountGuiSelectDoesNotRequireACachedCharacterOrEmitASelector()
    {
        var config = new RecordingConfigService();
        var supervisors = new FakeSupervisorFactory();
        using LauncherOrchestrator orchestrator = CreateOrchestrator(
            includeCharacter: false,
            configService: config,
            supervisorFactory: supervisors);

        Assert.True(orchestrator.GetAccountLaunchCapability(
            "Local ACE",
            "testaccount",
            LaunchMode.GuiSelect).IsAvailable);

        LauncherSessionSnapshot launched = await orchestrator.LaunchAsync(
            "Local ACE",
            "testaccount",
            characterName: null,
            LaunchMode.GuiSelect);

        Assert.Null(launched.CharacterName);
        Assert.Equal(LaunchMode.GuiSelect, config.LastCharacter!.LaunchMode);
        Assert.Equal(string.Empty, config.LastCharacter.Name);
        string json = SessionConfigComposer.Serialize(config.LastComposed!.Document);
        Assert.DoesNotContain("\"character\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, json, StringComparison.Ordinal);
        Assert.Equal("gui-host", Assert.Single(supervisors.Created).Spec!.ExecutablePath);
    }

    [Theory]
    [InlineData(LaunchMode.Gui)]
    [InlineData(LaunchMode.Headless)]
    public async Task AccountLaunchWithoutACharacterOnlyAcceptsGuiSelect(LaunchMode mode)
    {
        var config = new RecordingConfigService();
        var supervisors = new FakeSupervisorFactory();
        using LauncherOrchestrator orchestrator = CreateOrchestrator(
            includeCharacter: false,
            configService: config,
            supervisorFactory: supervisors);

        await Assert.ThrowsAsync<LauncherOperationException>(() =>
            orchestrator.LaunchAsync(
                "Local ACE",
                "testaccount",
                characterName: null,
                mode));

        Assert.Equal(0, config.PlayCallCount);
        Assert.Empty(supervisors.Created);
    }

    [Fact]
    public async Task ProbeIsRefusedWhileTheAccountHasARunningLauncherActivity()
    {
        using LauncherOrchestrator orchestrator = CreateOrchestrator();
        await orchestrator.LaunchAsync(
            "Local ACE",
            "testaccount",
            "+Acdream",
            LaunchMode.Headless);

        LauncherCapability capability = orchestrator.GetProbeCapability(
            "Local ACE",
            "testaccount");

        Assert.False(capability.IsAvailable);
        Assert.Contains("Stop", capability.Reason, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<LauncherOperationException>(() =>
            orchestrator.ProbeAsync("Local ACE", "testaccount"));
    }

    [Fact]
    public async Task LaunchIsRefusedWhileTheAccountProbeIsRunning()
    {
        using LauncherOrchestrator orchestrator = CreateOrchestrator();
        await orchestrator.ProbeAsync("Local ACE", "testaccount");

        LauncherCapability capability = orchestrator.GetAccountLaunchCapability(
            "Local ACE",
            "testaccount",
            LaunchMode.Headless);

        Assert.False(capability.IsAvailable);
        await Assert.ThrowsAsync<LauncherOperationException>(() =>
            orchestrator.LaunchAsync(
                "Local ACE",
                "testaccount",
                "+Acdream",
                LaunchMode.Headless));
    }

    [Fact]
    public async Task ConcurrentPlayReservationsAllowExactlyOneActivityPerAccount()
    {
        using LauncherOrchestrator orchestrator = CreateOrchestrator();

        Task<LauncherSessionSnapshot>[] attempts = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(async () => await orchestrator.LaunchAsync(
                "Local ACE",
                "testaccount",
                "+Acdream",
                LaunchMode.Headless)))
            .ToArray();

        try
        {
            await Task.WhenAll(attempts);
        }
        catch (LauncherOperationException)
        {
            // The losing reservation is the behavior under test.
        }

        Task<LauncherSessionSnapshot> successful = Assert.Single(
            attempts,
            attempt => attempt.Status == TaskStatus.RanToCompletion);
        Task<LauncherSessionSnapshot> rejected = Assert.Single(attempts, attempt =>
            attempt.Exception?.GetBaseException() is LauncherOperationException);
        Assert.True(successful.IsCompletedSuccessfully);
        Assert.True(rejected.IsFaulted);
        Assert.Single(orchestrator.GetSnapshot().Sessions);
    }

    [Fact]
    public async Task AnOrdinaryLoginFoldsTheReportedRosterIntoTheStore()
    {
        var statusSources = new QueueStatusSourceFactory();
        using LauncherOrchestrator orchestrator = CreateOrchestrator(
            includeCharacter: false,
            statusSourceFactory: statusSources);

        _ = await orchestrator.LaunchAsync(
            "Local ACE",
            "testaccount",
            characterName: null,
            LaunchMode.GuiSelect);

        QueueStatusSource source = Assert.Single(statusSources.Created);
        source.Enqueue(new CharacterListStatusEvent
        {
            V = 1,
            E = "characterList",
            T = DateTimeOffset.UtcNow,
            SessionId = "s1",
            AccountName = "testaccount",
            SlotCount = 6,
            Characters =
            [
                new StatusCharacterEntry(0x5000000Au, "+Acdream", 0),
                new StatusCharacterEntry(0x5000000Bu, "+Second", 0),
            ],
        });
        orchestrator.PollStatus();

        LauncherAccountSnapshot account = Assert.Single(
            Assert.Single(orchestrator.GetSnapshot().Servers).Accounts);
        Assert.Equal(2, account.Characters.Count);

        // Persisted, so the tree still shows them on the next launcher start.
        var reloaded = new LauncherProfileStore(
            Path.Combine(_paths.ConfigDirectory, "launcher-profiles.json"));
        Assert.True(reloaded.Load());
        Assert.Equal(
            2,
            reloaded.Document.Servers.Single().Accounts.Single().Characters.Count);
    }

    [Fact]
    public async Task ProbeUsesTheProbeShapeAndFoldsTheReportedRosterIntoTheStore()
    {
        var config = new RecordingConfigService();
        var statusSources = new QueueStatusSourceFactory();
        using LauncherOrchestrator orchestrator = CreateOrchestrator(
            includeCharacter: false,
            configService: config,
            statusSourceFactory: statusSources);

        LauncherSessionSnapshot probe = await orchestrator.ProbeAsync(
            "Local ACE",
            "testaccount");

        Assert.Equal(LauncherActivityKind.Probe, probe.Kind);
        Assert.Equal(0, config.PlayCallCount);
        Assert.Equal(1, config.ProbeCallCount);
        string json = SessionConfigComposer.Serialize(config.LastComposed!.Document);
        Assert.Contains("\"mode\": \"probe\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"character\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, json, StringComparison.Ordinal);

        QueueStatusSource source = Assert.Single(statusSources.Created);
        source.Enqueue(new CharacterListStatusEvent
        {
            V = 1,
            E = "characterList",
            T = DateTimeOffset.UtcNow,
            SessionId = "s1",
            AccountName = "testaccount",
            SlotCount = 6,
            Characters =
            [
                new StatusCharacterEntry(0x5000000Au, "+Acdream", 0),
                new StatusCharacterEntry(0x5000000Bu, "+Second", 0),
            ],
        });
        orchestrator.PollStatus();

        LauncherAccountSnapshot account = Assert.Single(
            Assert.Single(orchestrator.GetSnapshot().Servers).Accounts);
        Assert.Equal(2, account.Characters.Count);
        Assert.Contains(account.Characters, character => character.Name == "+Acdream");
        Assert.Contains(account.Characters, character => character.Name == "+Second");

        var reloaded = new LauncherProfileStore(
            Path.Combine(_paths.ConfigDirectory, "launcher-profiles.json"));
        Assert.True(reloaded.Load());
        Assert.Equal(
            2,
            reloaded.Document.Servers.Single().Accounts.Single().Characters.Count);
    }

    [Fact]
    public async Task UnsupportedPlatformRejectsBothGraphicalModesBeforeCompositionOrSpawnWithUnsupportedPlatformExplanation()
    {
        var config = new RecordingConfigService();
        var supervisors = new FakeSupervisorFactory();
        using LauncherOrchestrator orchestrator = CreateOrchestrator(
            platform: UnsupportedPlatformCapabilities(),
            configService: config,
            supervisorFactory: supervisors);

        foreach (LaunchMode mode in new[] { LaunchMode.Gui, LaunchMode.GuiSelect })
        {
            LauncherOperationException exception = await Assert.ThrowsAsync<LauncherOperationException>(() =>
                orchestrator.LaunchAsync(
                    "Local ACE",
                    "testaccount",
                    "+Acdream",
                    mode));
            Assert.Contains("supported on Windows and Linux", exception.Message, StringComparison.Ordinal);
        }

        Assert.False(orchestrator.GetLaunchCapability(LaunchMode.Headless).IsAvailable);
        Assert.Equal(0, config.PlayCallCount);
        Assert.Empty(supervisors.Created);
    }

    [Fact]
    public async Task MissingCoDeployedHostsDisableActionsBeforeCompositionOrSpawn()
    {
        var config = new RecordingConfigService();
        var supervisors = new FakeSupervisorFactory();
        var executables = new LauncherExecutableSet(
            "missing-gui",
            "missing-headless",
            fileExists: _ => false);
        using LauncherOrchestrator orchestrator = CreateOrchestrator(
            configService: config,
            supervisorFactory: supervisors,
            executables: executables);

        LauncherCapability gui = orchestrator.GetAccountLaunchCapability(
            "Local ACE",
            "testaccount",
            LaunchMode.GuiSelect);
        LauncherCapability headless = orchestrator.GetAccountLaunchCapability(
            "Local ACE",
            "testaccount",
            LaunchMode.Headless);
        LauncherCapability probe = orchestrator.GetProbeCapability(
            "Local ACE",
            "testaccount");

        Assert.False(gui.IsAvailable);
        Assert.Contains("missing-gui", gui.Reason, StringComparison.Ordinal);
        Assert.False(headless.IsAvailable);
        Assert.Contains("missing-headless", headless.Reason, StringComparison.Ordinal);
        Assert.False(probe.IsAvailable);
        await Assert.ThrowsAsync<LauncherOperationException>(() =>
            orchestrator.LaunchAsync(
                "Local ACE",
                "testaccount",
                "+Acdream",
                LaunchMode.Headless));
        Assert.Equal(0, config.PlayCallCount);
        Assert.Empty(supervisors.Created);
    }

    [Fact]
    public async Task ProcessExitIsTerminalAndLateStatusCannotResurrectTheAccount()
    {
        var supervisors = new FakeSupervisorFactory();
        var statusSources = new QueueStatusSourceFactory();
        using LauncherOrchestrator orchestrator = CreateOrchestrator(
            supervisorFactory: supervisors,
            statusSourceFactory: statusSources);
        await orchestrator.LaunchAsync(
            "Local ACE",
            "testaccount",
            "+Acdream",
            LaunchMode.Headless);

        Assert.Single(supervisors.Created).Exit(23);
        QueueStatusSource source = Assert.Single(statusSources.Created);
        source.Enqueue(Connected("s1"));
        source.Enqueue(EnteredWorld("s1", "+Acdream"));
        source.Enqueue(Exited("s1", 23, "host crash detail"));
        orchestrator.PollStatus();

        LauncherSessionSnapshot session = Assert.Single(orchestrator.GetSnapshot().Sessions);
        Assert.Equal(LauncherActivityState.Exited, session.State);
        Assert.Equal(23, session.ExitCode);
        Assert.Contains("host crash detail", session.Status, StringComparison.Ordinal);
        Assert.True(orchestrator.GetProbeCapability("Local ACE", "testaccount").IsAvailable);
    }

    [Fact]
    public async Task HostExitReasonSurvivesTheLaterProcessExitCallback()
    {
        var supervisors = new FakeSupervisorFactory();
        var statusSources = new QueueStatusSourceFactory();
        using LauncherOrchestrator orchestrator = CreateOrchestrator(
            supervisorFactory: supervisors,
            statusSourceFactory: statusSources);
        await orchestrator.LaunchAsync(
            "Local ACE",
            "testaccount",
            "+Acdream",
            LaunchMode.Headless);

        Assert.Single(statusSources.Created).Enqueue(
            Exited("s1", 0, "graceful host shutdown"));
        orchestrator.PollStatus();
        Assert.Single(supervisors.Created).Exit(0);

        LauncherSessionSnapshot session = Assert.Single(orchestrator.GetSnapshot().Sessions);
        Assert.Equal(LauncherActivityState.Exited, session.State);
        Assert.Contains("graceful host shutdown", session.Status, StringComparison.Ordinal);
        Assert.True(orchestrator.GetAccountLaunchCapability(
            "Local ACE",
            "testaccount",
            LaunchMode.Headless).IsAvailable);
    }

    [Fact]
    public async Task StartFailureIsVisibleButRedactsTheCredentialEverywhere()
    {
        var supervisors = new FakeSupervisorFactory(
            startExceptionFactory: password =>
                new IOException($"simulated pipe failure containing {password}"));
        using LauncherOrchestrator orchestrator = CreateOrchestrator(
            supervisorFactory: supervisors);

        LauncherOperationException exception = await Assert.ThrowsAsync<LauncherOperationException>(
            () => orchestrator.LaunchAsync(
                "Local ACE",
                "testaccount",
                "+Acdream",
                LaunchMode.Headless));

        Assert.DoesNotContain(Password, exception.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", exception.Message, StringComparison.Ordinal);
        LauncherSessionSnapshot failed = Assert.Single(orchestrator.GetSnapshot().Sessions);
        Assert.Equal(LauncherActivityState.Failed, failed.State);
        Assert.DoesNotContain(Password, failed.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreCancelledLaunchNeverComposesOrSpawnsAndEndsCancelled()
    {
        var config = new RecordingConfigService();
        var supervisors = new FakeSupervisorFactory();
        using LauncherOrchestrator orchestrator = CreateOrchestrator(
            configService: config,
            supervisorFactory: supervisors);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.LaunchAsync(
                "Local ACE",
                "testaccount",
                "+Acdream",
                LaunchMode.Headless,
                cancellation.Token));

        Assert.Equal(0, config.PlayCallCount);
        Assert.Empty(supervisors.Created);
        Assert.Equal(
            LauncherActivityState.Cancelled,
            Assert.Single(orchestrator.GetSnapshot().Sessions).State);
    }

    [Fact]
    public async Task StopRunsThroughTheSupervisorOffThreadAndMakesTheAccountProbeableAgain()
    {
        var supervisors = new FakeSupervisorFactory();
        using LauncherOrchestrator orchestrator = CreateOrchestrator(
            supervisorFactory: supervisors);
        LauncherSessionSnapshot launched = await orchestrator.LaunchAsync(
            "Local ACE",
            "testaccount",
            "+Acdream",
            LaunchMode.Headless);

        await orchestrator.StopSessionAsync(
            launched.SessionId,
            TimeSpan.FromMilliseconds(10));

        FakeSupervisor supervisor = Assert.Single(supervisors.Created);
        Assert.Equal(1, supervisor.StopCallCount);
        Assert.Equal(
            LauncherActivityState.Exited,
            Assert.Single(orchestrator.GetSnapshot().Sessions).State);
        Assert.True(orchestrator.GetProbeCapability(
            "Local ACE",
            "testaccount").IsAvailable);
    }

    private LauncherOrchestrator CreateOrchestrator(
        bool includeCharacter = true,
        LauncherPlatformCapabilities? platform = null,
        ILauncherSessionConfigService? configService = null,
        ILauncherProcessSupervisorFactory? supervisorFactory = null,
        IStatusEventSourceFactory? statusSourceFactory = null,
        LauncherExecutableSet? executables = null,
        UpdateSessionBarrier? updateSessionBarrier = null)
    {
        string profilePath = Path.Combine(
            _paths.ConfigDirectory,
            "launcher-profiles.json");
        var store = new LauncherProfileStore(profilePath);
        store.Load();
        store.AddServer("Local ACE", "127.0.0.1", 9000);
        store.AddAccount("Local ACE", "testaccount", Password);
        if (includeCharacter)
        {
            store.AddCharacter(
                "Local ACE",
                "testaccount",
                "+Acdream",
                "0x5000000A",
                LaunchMode.GuiSelect,
                ["ExamplePlugin"],
                ["/vt start"]);
        }
        store.Save();

        int nextSession = 0;
        var orchestrator = new LauncherOrchestrator(
            store,
            _paths,
            executables ?? new LauncherExecutableSet(
                "gui-host",
                "headless-host",
                fileExists: _ => true,
                hasUnixExecutePermission: _ => true),
            new LauncherInstallRecord("dats", "pak"),
            platform ?? WindowsCapabilities(),
            configService,
            supervisorFactory ?? new FakeSupervisorFactory(),
            statusSourceFactory ?? new QueueStatusSourceFactory(),
            () => $"s{Interlocked.Increment(ref nextSession)}",
            updateSessionBarrier: updateSessionBarrier);
        orchestrator.LoadProfiles();
        return orchestrator;
    }

    private static LauncherPlatformCapabilities WindowsCapabilities() =>
        new(true, false, true, true, "Windows", null);

    private static LauncherPlatformCapabilities UnsupportedPlatformCapabilities() =>
        new(
            false,
            false,
            false,
            false,
            "Unsupported",
            LauncherPlatformCapabilities.UnsupportedPlatformGraphicalLaunchDisabledReason);

    private static ConnectedStatusEvent Connected(string sessionId) =>
        new()
        {
            V = 1,
            E = "connected",
            T = DateTimeOffset.UtcNow,
            SessionId = sessionId,
        };

    private static EnteredWorldStatusEvent EnteredWorld(
        string sessionId,
        string characterName) =>
        new()
        {
            V = 1,
            E = "enteredWorld",
            T = DateTimeOffset.UtcNow,
            SessionId = sessionId,
            CharacterId = 0x5000000Au,
            CharacterName = characterName,
        };

    private static ExitedStatusEvent Exited(
        string sessionId,
        int code,
        string reason) =>
        new()
        {
            V = 1,
            E = "exited",
            T = DateTimeOffset.UtcNow,
            SessionId = sessionId,
            Code = code,
            Reason = reason,
        };

    private sealed class RecordingConfigService : ILauncherSessionConfigService
    {
        public int PlayCallCount { get; private set; }

        public int ProbeCallCount { get; private set; }

        public CharacterProfile? LastCharacter { get; private set; }

        public string? LastAccountPassword { get; private set; }

        public ComposedSessionConfig? LastComposed { get; private set; }

        public ComposedSessionConfig ComposeAndWrite(
            ServerProfile server,
            AccountProfile account,
            CharacterProfile character,
            LauncherInstallRecord install,
            ApplicationPathSet paths,
            string sessionId,
            int? loginCommandDelayMs = null)
        {
            PlayCallCount++;
            LastCharacter = character;
            LastAccountPassword = account.Password;
            LastComposed = SessionConfigComposer.Compose(
                server,
                account,
                character,
                install,
                paths,
                sessionId,
                loginCommandDelayMs);
            return LastComposed;
        }

        public ComposedSessionConfig ComposeProbeAndWrite(
            ServerProfile server,
            AccountProfile account,
            LauncherInstallRecord install,
            ApplicationPathSet paths,
            string sessionId)
        {
            ProbeCallCount++;
            LastAccountPassword = account.Password;
            LastComposed = SessionConfigComposer.ComposeProbe(
                server,
                account,
                install,
                paths,
                sessionId);
            return LastComposed;
        }
    }

    private sealed class FakeSupervisorFactory(
        Func<string?, Exception>? startExceptionFactory = null)
        : ILauncherProcessSupervisorFactory
    {
        public List<FakeSupervisor> Created { get; } = [];

        public ILauncherProcessSupervisor Create()
        {
            var supervisor = new FakeSupervisor(startExceptionFactory);
            Created.Add(supervisor);
            return supervisor;
        }
    }

    private sealed class FakeSupervisor(Func<string?, Exception>? startExceptionFactory)
        : ILauncherProcessSupervisor
    {
        public LauncherSessionState State { get; private set; } =
            LauncherSessionState.Starting;

        public int? ExitCode { get; private set; }

        public LauncherProcessSpec? Spec { get; private set; }

        public string? PasswordWrittenToStdin { get; private set; }

        public int StopCallCount { get; private set; }

        public event EventHandler<LauncherSessionState>? StateChanged;

        public void Start(LauncherProcessSpec spec, string? password)
        {
            Spec = spec;
            PasswordWrittenToStdin = password;
            if (startExceptionFactory is not null)
            {
                throw startExceptionFactory(password);
            }

            State = LauncherSessionState.Running;
            StateChanged?.Invoke(this, State);
        }

        public void Stop(TimeSpan timeout)
        {
            StopCallCount++;
            State = LauncherSessionState.Exited;
            ExitCode = 0;
            StateChanged?.Invoke(this, State);
        }

        public void Exit(int code)
        {
            State = LauncherSessionState.Exited;
            ExitCode = code;
            StateChanged?.Invoke(this, State);
        }

        public void Dispose()
        {
        }
    }

    private sealed class BlockingStopSupervisorFactory : ILauncherProcessSupervisorFactory
    {
        public List<BlockingStopSupervisor> Created { get; } = [];

        public ILauncherProcessSupervisor Create()
        {
            var supervisor = new BlockingStopSupervisor();
            Created.Add(supervisor);
            return supervisor;
        }

        public void AllowEveryStop()
        {
            foreach (BlockingStopSupervisor supervisor in Created)
            {
                supervisor.AllowTerminal.Set();
            }
        }
    }

    private sealed class BlockingStopSupervisor : ILauncherProcessSupervisor
    {
        public ManualResetEventSlim StopEntered { get; } = new(false);

        public ManualResetEventSlim AllowTerminal { get; } = new(false);

        public LauncherSessionState State { get; private set; } =
            LauncherSessionState.Starting;

        public int? ExitCode { get; private set; }

        public bool Disposed { get; private set; }

        public event EventHandler<LauncherSessionState>? StateChanged;

        public void Start(LauncherProcessSpec spec, string? password)
        {
            State = LauncherSessionState.Running;
            StateChanged?.Invoke(this, State);
        }

        public void Stop(TimeSpan timeout)
        {
            StopEntered.Set();
            AllowTerminal.Wait();
            State = LauncherSessionState.Exited;
            ExitCode = 0;
            StateChanged?.Invoke(this, State);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class QueueStatusSourceFactory : IStatusEventSourceFactory
    {
        public List<QueueStatusSource> Created { get; } = [];

        public IStatusEventSource Create(string path)
        {
            var source = new QueueStatusSource();
            Created.Add(source);
            return source;
        }
    }

    private sealed class QueueStatusSource : IStatusEventSource
    {
        private readonly Queue<StatusEvent> _events = [];

        public void Enqueue(StatusEvent statusEvent) => _events.Enqueue(statusEvent);

        public IReadOnlyList<StatusEvent> ReadNewEvents()
        {
            var result = new List<StatusEvent>();
            while (_events.TryDequeue(out StatusEvent? statusEvent))
            {
                if (statusEvent is not null)
                {
                    result.Add(statusEvent);
                }
            }

            return result;
        }
    }
}
