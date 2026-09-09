using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;
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
        var row = vm.Accounts[0].Servers[0];
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
        var row = vm.Accounts[0].Servers[0];
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

    private static FakeLauncherOrchestrator BatchOrchestrator() => new()
    {
        ServersOverride = [BatchServer("One", "Alice", "Bob"), BatchServer("Two", "Alice")],
        Session = FakeLauncherOrchestrator.CreateSession(LauncherActivityState.Exited),
    };

    [Fact]
    public void ChangingEndpointClearsHealthFromTheOldEndpoint()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        var row = vm.Accounts[0].Servers[0];
        row.IsServerOnline = true;
        row.ServerStatusText = "Online · 12 players";
        core.ServersOverride = core.ServersOverride!.Select(server => server with { Host = "different.example" }).ToArray();
        core.RaiseStateChanged();
        Assert.Null(row.IsServerOnline);
        Assert.Equal("Not checked", row.ServerStatusText);
    }

    [Fact]
    public async Task CheckedRowsLaunchAllSelectedAccountsWithRequestedModesAndContinueAfterFailure()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        await vm.StartBackgroundInitializationAsync();
        vm.CloseActiveModal();
        LauncherAccountServerRowViewModel[] rows = vm.Accounts.SelectMany(account => account.Servers).ToArray();
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
    public async Task PollingPreservesChecksCharactersAndExpansionAndBatchCannotDoubleStart()
    {
        using var core = BatchOrchestrator();
        using var vm = CreateInitialized(core);
        await vm.StartBackgroundInitializationAsync();
        vm.CloseActiveModal();
        var group = vm.Accounts[0];
        var row = group.Servers[0];
        group.IsExpanded = false;
        row.IsChecked = true;
        row.SelectedCharacter = "A character";
        core.RaiseStateChanged();
        Assert.Same(group, vm.Accounts[0]);
        Assert.Same(row, group.Servers[0]);
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
        var rows = vm.Accounts[0].Servers;
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
        var row = vm.Accounts[0].Servers[0];
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
        foreach (var row in vm.Accounts[0].Servers) row.IsChecked = true;
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
        var row = vm.Accounts[0].Servers[0];
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
