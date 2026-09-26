using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Status;
using AcDream.Launcher.Core.Updates;
using AcDream.Launcher.ViewModels;

namespace AcDream.Launcher.Tests;

public sealed partial class LauncherWindowViewModelTests
{
    [Fact]
    public async Task PollRefreshesPlayWhenReconnectDelayExpiresWithoutAnEvent()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        await vm.StartBackgroundInitializationAsync();
        vm.CloseActiveModal();
        var row = vm.Accounts[0].Rows[0];
        core.AccountLaunchCapability = LauncherCapability.Unavailable("Try again in 1 s.");
        vm.PollStatus();
        Assert.False(row.PlayCommand.CanExecute(null));
        int notifications = 0;
        row.PlayCommand.CanExecuteChanged += (_, _) => notifications++;
        core.AccountLaunchCapability = LauncherCapability.Available;
        vm.PollStatus();
        Assert.True(notifications > 0);
        Assert.True(row.PlayCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedStartupIsShownInItsRowAndNotCountedAsStarted()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        await vm.StartBackgroundInitializationAsync();
        vm.CloseActiveModal();
        var row = vm.Accounts[0].Rows[0];
        core.LaunchHandler = _ =>
        {
            core.Session = core.Session with { ServerName = row.ServerName, AccountName = row.AccountName,
                State = LauncherActivityState.Failed, Error = "Client files do not match", ExitCode = 1 };
            return Task.FromResult(core.Session);
        };
        await row.PlayCommand.ExecuteAsync();
        Assert.Contains("Started 0 of 1", vm.OperationStatus);
        Assert.Equal("Client files do not match", row.LaunchError);
        Assert.True(row.HasLaunchError);
        Assert.False(row.IsActive);
        Assert.True(row.PlayCommand.CanExecute(null));
    }

    private static LauncherServerSnapshot BatchServer(string name, params string[] accounts) => new(name, "localhost", 9000,
        accounts.Select(account => new LauncherAccountSnapshot(name, account,
            [new LauncherCharacterSnapshot(name, account, "A character", "123", LaunchMode.Gui, [], [], false, "Ready")], false, "Ready")).ToArray());

    private static FakeLauncherOrchestrator ProfiledOrchestrator()
    {
        static LauncherAccountSnapshot Account(string server, string name, params string[] profiles) =>
            new(server, name, [new LauncherCharacterSnapshot(server, name, "A character", "123", LaunchMode.Gui, [], [], false, "Ready")],
                false, "Ready", AccountProfiles: profiles);
        return new FakeLauncherOrchestrator
        {
            ServersOverride =
            [
                new LauncherServerSnapshot("One", "localhost", 9000, [Account("One", "Alice", "Main"), Account("One", "Bob", "bots")]),
                new LauncherServerSnapshot("Two", "localhost", 9000, [Account("Two", "Alice", "Bots", "Mules")]),
            ],
            Session = FakeLauncherOrchestrator.CreateSession(LauncherActivityState.Exited),
        };
    }

    [Fact]
    public void EachAccountOnEachServerIsItsOwnGroupInProfileOrder()
    {
        using var core = ProfiledOrchestrator();
        using var vm = CreateInitialized(core);

        Assert.Equal(
            [("One", "Alice"), ("One", "Bob"), ("Two", "Alice")],
            vm.Accounts.Select(group => (group.ServerName, group.AccountName)));
        Assert.Equal(["All", "Main", "bots", "Mules"], vm.ProfileFilters.Select(chip => chip.Label));
        Assert.True(vm.ProfileFilters[0].IsSelected);
    }

    [Fact]
    public async Task AProfileChipShowsItsAccountsAndPlaySelectedStartsOnlyThoseTicked()
    {
        using var core = ProfiledOrchestrator();
        using var vm = CreateInitialized(core);
        await vm.StartBackgroundInitializationAsync();
        vm.CloseActiveModal();
        foreach (var row in vm.Accounts.SelectMany(group => group.Rows)) row.IsChecked = true;
        Assert.Equal("Play selected (3)", vm.PlayCheckedText);

        vm.ProfileFilters.Single(chip => chip.Label == "bots").SelectCommand.Execute(null);

        Assert.Equal("bots", vm.ProfileFilter);
        Assert.Equal([false, true, true], vm.Accounts.Select(group => group.IsVisible));
        Assert.Equal("Play selected (2)", vm.PlayCheckedText);
        await vm.LaunchCheckedCommand.ExecuteAsync();
        Assert.Equal([("One", "Bob"), ("Two", "Alice")], core.LaunchRequests.Select(request => (request.Server, request.Account)));

        vm.ProfileFilters[0].SelectCommand.Execute(null);
        Assert.All(vm.Accounts, group => Assert.True(group.IsVisible));
    }

    private static FakeLauncherOrchestrator BatchOrchestrator() => new()
    {
        ServersOverride = [BatchServer("One", "Alice", "Bob"), BatchServer("Two", "Alice")],
        Session = FakeLauncherOrchestrator.CreateSession(LauncherActivityState.Exited),
    };

    [Fact]
    public void AnInactiveWindowNeverChecksServers()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        var health = new CountingServerHealth();
        vm.ConfigureServerHealth(health);

        vm.PollServerHealth(windowIsActive: false, DateTimeOffset.UtcNow);
        vm.PollServerHealth(windowIsActive: false, DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Equal(0, health.Checks);
    }

    [Fact]
    public void AnActiveWindowChecksEveryTenSeconds()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        var health = new CountingServerHealth();
        vm.ConfigureServerHealth(health);
        DateTimeOffset start = DateTimeOffset.UtcNow;

        vm.PollServerHealth(windowIsActive: true, start);
        int afterFirst = health.Checks;
        vm.PollServerHealth(windowIsActive: true, start.AddSeconds(5));
        int afterFiveSeconds = health.Checks;
        vm.PollServerHealth(windowIsActive: true, start.AddSeconds(11));

        Assert.True(afterFirst > 0);
        Assert.Equal(afterFirst, afterFiveSeconds);
        Assert.Equal(afterFirst * 2, health.Checks);
    }

    [Fact]
    public void ReactivatingPastTheIntervalChecksOnTheNextPoll()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        var health = new CountingServerHealth();
        vm.ConfigureServerHealth(health);
        DateTimeOffset start = DateTimeOffset.UtcNow;

        vm.PollServerHealth(windowIsActive: true, start);
        int afterFirst = health.Checks;
        vm.PollServerHealth(windowIsActive: false, start.AddSeconds(30));
        Assert.Equal(afterFirst, health.Checks);

        vm.PollServerHealth(windowIsActive: true, start.AddSeconds(30));

        Assert.Equal(afterFirst * 2, health.Checks);
    }

    [Fact]
    public void ABackgroundCheckLeavesTheCheckServersButtonEnabled()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        var health = new CountingServerHealth { Pending = new TaskCompletionSource<ServerHealthSnapshot>() };
        vm.ConfigureServerHealth(health);

        int canExecuteChanges = 0;
        vm.CheckServersCommand.CanExecuteChanged += (_, _) => canExecuteChanges++;
        vm.PollServerHealth(windowIsActive: true, DateTimeOffset.UtcNow);

        Assert.True(health.Checks > 0);
        Assert.True(vm.CheckServersCommand.CanExecute(null));
        Assert.Equal(0, canExecuteChanges);
    }

    [Fact]
    public void ConfiguringServerHealthEnablesTheCheckServersButton()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        Assert.False(vm.CheckServersCommand.CanExecute(null));
        bool notified = false;
        vm.CheckServersCommand.CanExecuteChanged += (_, _) => notified = vm.CheckServersCommand.CanExecute(null);

        vm.ConfigureServerHealth(new CountingServerHealth());

        Assert.True(notified);
    }

    [Fact]
    public void ABackgroundCheckDoesNotStartWhileOneIsStillRunning()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        var health = new CountingServerHealth { Pending = new TaskCompletionSource<ServerHealthSnapshot>() };
        vm.ConfigureServerHealth(health);
        DateTimeOffset start = DateTimeOffset.UtcNow;

        vm.PollServerHealth(windowIsActive: true, start);
        int afterFirst = health.Checks;
        vm.PollServerHealth(windowIsActive: true, start.AddMinutes(1));

        Assert.Equal(afterFirst, health.Checks);
    }

    private sealed class CountingServerHealth : IServerHealthService
    {
        public int Checks { get; private set; }

        public TaskCompletionSource<ServerHealthSnapshot>? Pending { get; init; }

        public Task<ServerHealthSnapshot> CheckAsync(string host, int port, string serverName,
            CancellationToken cancellationToken = default)
        {
            Checks++;
            return Pending?.Task ?? Task.FromResult(new ServerHealthSnapshot(true, 1, 0, false, DateTimeOffset.UtcNow));
        }
    }

    [Theory]
    [InlineData("0.1.10", "0.1.8", "launcher v0.1.10\nclient v0.1.8")]
    [InlineData("0.2.0-beta.1+3a71d75", "0.2.0+abc", "launcher v0.2.0-beta.1\nclient v0.2.0")]
    [InlineData("0.1.10", null, "launcher v0.1.10\nclient not installed")]
    public void VersionTextShowsLauncherAndClientWithoutBuildMetadata(
        string launcher, string? client, string expected)
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        ClientVersionResolution resolution = new(
            client is null ? ClientVersionState.Missing : ClientVersionState.Verified,
            string.Empty,
            client is null ? null : LauncherVersion.Parse(client),
            null,
            null,
            null);

        vm.ConfigureVersions(launcher, () => resolution);

        Assert.Equal(expected, vm.VersionText);
    }

    [Fact]
    public void ChangingEndpointClearsHealthFromTheOldEndpoint()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        var row = vm.Accounts[0].Rows[0];
        row.IsServerOnline = true;
        row.ServerStatusText = "Online · 12 players";
        core.ServersOverride = core.ServersOverride!.Select(server => server with { Host = "different.example" }).ToArray();
        core.RaiseStateChanged();
        Assert.Null(row.IsServerOnline);
        Assert.Equal("Not checked", row.ServerStatusText);
        Assert.Equal(0, row.PingBars);
    }

    [Theory]
    [InlineData(12, 3)]
    [InlineData(80, 3)]
    [InlineData(81, 2)]
    [InlineData(200, 2)]
    [InlineData(201, 1)]
    public void ThePingMeterFillsFewerBarsAsTheReplyGetsSlower(double milliseconds, int bars)
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        var row = vm.Accounts[0].Rows[0];
        row.IsServerOnline = true;
        row.LatencyMilliseconds = milliseconds;
        Assert.Equal(bars, row.PingBars);
        Assert.True(row.HasLatency);
    }

    [Fact]
    public void PickingACharacterOrLaunchModeIsSavedAsItIsPicked()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        var row = vm.Accounts[0].Rows[0];

        row.SelectedCharacter = "A character";
        Assert.Equal(("A character", LaunchMode.Gui), core.SavedRowSelection);

        row.SelectedLaunchMode = "Headless";
        Assert.Equal(("A character", LaunchMode.Headless), core.SavedRowSelection);
    }

    [Fact]
    public void ASavedSelectionComesBackWhenTheRowIsBuilt()
    {
        using var core = BatchOrchestrator();
        core.ServersOverride = core.ServersOverride!
            .Select(server => server with
            {
                Accounts = server.Accounts
                    .Select(account => account with { SelectedCharacter = "A character", SelectedLaunchMode = LaunchMode.Headless })
                    .ToArray(),
            }).ToArray();
        using var vm = CreateInitialized(core);
        var row = vm.Accounts[0].Rows[0];
        Assert.Equal("A character", row.SelectedCharacter);
        Assert.Equal("Headless", row.SelectedLaunchMode);
        Assert.Null(core.SavedRowSelection);
    }

    [Fact]
    public void TheCharacterBoxShowsWhoIsInWorldAndReturnsToTheSavedChoiceAfterwards()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        var row = vm.Accounts[0].Rows[0];
        Assert.Equal(LauncherAccountServerRowViewModel.CharacterSelect, row.DisplayedCharacter);

        core.Session = core.Session with
        {
            ServerName = row.ServerName,
            AccountName = row.AccountName,
            CharacterName = "A character",
            State = LauncherActivityState.InWorld,
        };
        core.RaiseStateChanged();
        Assert.Equal("A character", row.DisplayedCharacter);
        Assert.Equal(LauncherAccountServerRowViewModel.CharacterSelect, row.SelectedCharacter);

        core.Session = core.Session with { State = LauncherActivityState.Exited, ExitCode = 0 };
        core.RaiseStateChanged();
        Assert.Equal(LauncherAccountServerRowViewModel.CharacterSelect, row.DisplayedCharacter);
    }

    [Fact]
    public void ThePingMeterIsEmptyForAServerThatDidNotAnswer()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        var row = vm.Accounts[0].Rows[0];
        row.LatencyMilliseconds = 15;
        row.IsServerOnline = false;
        Assert.Equal(0, row.PingBars);
    }

    [Fact]
    public async Task CheckedRowsLaunchAllSelectedAccountsWithRequestedModesAndContinueAfterFailure()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        await vm.StartBackgroundInitializationAsync();
        vm.CloseActiveModal();
        LauncherAccountServerRowViewModel[] rows = vm.Accounts.SelectMany(account => account.Rows).ToArray();
        foreach (var row in rows) row.IsChecked = true;
        rows[1].SelectedCharacter = "A character";
        rows[1].SelectedLaunchMode = "Headless";
        int calls = 0;
        core.LaunchHandler = _ => ++calls == 1 ? throw new LauncherOperationException("Failed to start") : Task.FromResult(core.Session);
        await vm.LaunchCheckedCommand.ExecuteAsync();
        Assert.Equal(3, core.LaunchRequests.Count);
        Assert.Equal(LaunchMode.GuiSelect, core.LaunchRequests[0].Mode);
        Assert.Null(core.LaunchRequests[0].Character);
        Assert.Equal(LaunchMode.Headless, core.LaunchRequests[1].Mode);
        Assert.Equal("A character", core.LaunchRequests[1].Character);
        Assert.Contains("Started 2 of 3", vm.OperationStatus);
        Assert.Contains("Failed to start", vm.LastError);
    }

    [Fact]
    public async Task PlayableCheckedRowsFollowsWhatIsCheckedAndReady()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        await vm.StartBackgroundInitializationAsync();
        vm.CloseActiveModal();
        LauncherAccountServerRowViewModel row = vm.Accounts[0].Rows[0];

        Assert.False(vm.HasPlayableCheckedRows);

        row.IsChecked = true;
        core.RaiseStateChanged();

        Assert.Equal(row.CanPlay, vm.HasPlayableCheckedRows);

        row.IsChecked = false;
        core.RaiseStateChanged();

        Assert.False(vm.HasPlayableCheckedRows);
    }

    [Fact]
    public async Task PollingPreservesChecksCharactersAndExpansionAndBatchCannotDoubleStart()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        await vm.StartBackgroundInitializationAsync();
        vm.CloseActiveModal();
        var group = vm.Accounts[0];
        var row = group.Rows[0];
        group.IsExpanded = false;
        row.IsChecked = true;
        row.SelectedCharacter = "A character";
        core.RaiseStateChanged();
        Assert.Same(group, vm.Accounts[0]);
        Assert.Same(row, group.Rows[0]);
        Assert.False(group.IsExpanded);
        Assert.True(row.IsChecked);
        Assert.Equal("A character", row.SelectedCharacter);
        var pending = new TaskCompletionSource<LauncherSessionSnapshot>();
        core.LaunchHandler = _ => pending.Task;
        Task launching = vm.LaunchCheckedCommand.ExecuteAsync();
        core.RaiseStateChanged();
        await vm.LaunchCheckedCommand.ExecuteAsync();
        await row.PlayCommand.ExecuteAsync();
        Assert.Single(core.LaunchRequests);
        pending.SetResult(core.Session);
        await launching;
    }

    [Fact]
    public async Task MixedCheckedRowsReportInvalidSelectionWithoutDroppingReadyLaunches()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        await vm.StartBackgroundInitializationAsync();
        vm.CloseActiveModal();
        var rows = vm.Accounts.SelectMany(account => account.Rows).ToArray();
        rows[0].IsChecked = true;
        rows[1].IsChecked = true;
        rows[1].SelectedLaunchMode = "Headless";
        await vm.LaunchCheckedCommand.ExecuteAsync();
        Assert.Single(core.LaunchRequests);
        Assert.Contains("Choose a character", vm.LastError);
        Assert.Contains("Started 1 of 2", vm.OperationStatus);
    }
    [Fact]
    public void RosterRefreshKeepsChosenCharacterWhenSelectorTemporarilyClearsBinding()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        var row = vm.Accounts[0].Rows[0];
        row.SelectedCharacter = "A character";
        row.CharacterChoices.CollectionChanged += (_, _) => row.SelectedCharacter = null!;
        var server = core.ServersOverride![0];
        var account = server.Accounts[0];
        core.ServersOverride = [server with { Accounts = [account with
        {
            Characters = [.. account.Characters, account.Characters[0] with { Name = "Another character" }],
        }, server.Accounts[1]] }, core.ServersOverride[1]];
        core.RaiseStateChanged();
        Assert.Equal("A character", row.SelectedCharacter);
        Assert.Contains("Another character", row.CharacterChoices);
    }

    [Fact]
    public async Task ClosingDuringBatchCancelsRemainingLaunchesWithoutReadingDisposedCancellationSource()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        await vm.StartBackgroundInitializationAsync();
        vm.CloseActiveModal();
        foreach (var row in vm.Accounts.SelectMany(account => account.Rows)) row.IsChecked = true;
        var pending = new TaskCompletionSource<LauncherSessionSnapshot>();
        core.LaunchHandler = _ => pending.Task;
        Task launching = vm.LaunchCheckedCommand.ExecuteAsync();
        vm.Dispose();
        pending.SetResult(core.Session);
        await launching;
        Assert.Single(core.LaunchRequests);
    }
    [Fact]
    public async Task HeadlessRequiresCharacterAndFreshActiveStateBlocksStaleRowLaunch()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        await vm.StartBackgroundInitializationAsync();
        vm.CloseActiveModal();
        var row = vm.Accounts[0].Rows[0];
        row.IsChecked = true;
        row.SelectedLaunchMode = "Headless";
        Assert.False(row.CanPlay);
        Assert.Contains("Choose a character", row.DisabledReason);
        row.SelectedCharacter = "A character";
        Assert.True(row.CanPlay);
        core.Session = core.Session with { ServerName = row.ServerName, AccountName = row.AccountName, State = LauncherActivityState.Running };
        Assert.False(row.PlayCommand.CanExecute(null));
        await vm.LaunchCheckedCommand.ExecuteAsync();
        Assert.Empty(core.LaunchRequests);
    }
}
