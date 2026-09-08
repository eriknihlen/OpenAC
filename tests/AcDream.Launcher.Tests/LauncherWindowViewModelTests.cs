using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Installation;
using AcDream.Launcher.Core.Updates;
using AcDream.Launcher.ViewModels;

namespace AcDream.Launcher.Tests;

public sealed class LauncherWindowViewModelTests
{
    [Fact]
    public async Task InitializeProjectsHierarchySessionsAndFutureWorkflowShells()
    {
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = new LauncherWindowViewModel(
            orchestrator,
            new ImmediateUiDispatcher());

        viewModel.Initialize();

        Assert.True(orchestrator.LoadCalled);
        LauncherTreeNodeViewModel server = Assert.Single(viewModel.Servers);
        Assert.Equal(LauncherTreeNodeKind.Server, server.Kind);
        LauncherTreeNodeViewModel account = Assert.Single(server.Children);
        Assert.Equal(LauncherTreeNodeKind.Account, account.Kind);
        LauncherTreeNodeViewModel character = Assert.Single(account.Children);
        Assert.Equal("+Acdream", character.DisplayName);
        Assert.Same(server, viewModel.SelectedNode);

        LauncherSessionRowViewModel session = Assert.Single(viewModel.Sessions);
        Assert.Equal("testaccount", session.Account);
        Assert.Equal("+Acdream", session.Character);
        Assert.Equal("Character select", session.State);
        Assert.True(session.IsActive);

        Assert.True(viewModel.IsInstallationChecking);
        Assert.True(viewModel.ShowInstallationBanner);
        Assert.False(viewModel.IsFirstRunRequired);
        await viewModel.StartBackgroundInitializationAsync();
        Assert.False(viewModel.IsInstallationChecking);
        Assert.True(viewModel.IsFirstRunRequired);
        Assert.Contains("SHA-256", viewModel.FirstRunWizardShell.Body, StringComparison.Ordinal);
        Assert.False(viewModel.UpdatePrompt.IsOpen);
        Assert.Empty(viewModel.UpdatePrompt.Body);
        viewModel.FirstRunWizardShell.OpenCommand.Execute(null);
        Assert.True(viewModel.FirstRunWizardShell.IsOpen);
        viewModel.FirstRunWizardShell.CloseCommand.Execute(null);
        Assert.False(viewModel.FirstRunWizardShell.IsOpen);
    }

    [Fact]
    public async Task BackgroundStartupWaitsForWindowSignalAndOrdersContentBeforeUpdates()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            Session = FakeLauncherOrchestrator.CreateSession(
                LauncherActivityState.Exited,
                "Exited cleanly."),
        };
        var contentCompletion = new TaskCompletionSource<InstallRecordVerification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = new FakeLauncherInstaller
        {
            LoadExistingHandler = _ => contentCompletion.Task,
        };
        var updater = new StartupOrderUpdater();
        using var viewModel = new LauncherWindowViewModel(
            orchestrator,
            new ImmediateUiDispatcher(),
            installer,
            updater);

        viewModel.Initialize();

        Assert.Empty(installer.LoadExistingCalls);
        Assert.Equal(0, updater.InitializeCalls);
        Assert.Equal(0, updater.CheckCalls);
        Assert.True(viewModel.IsInstallationChecking);

        Task startup = viewModel.StartBackgroundInitializationAsync();
        Assert.Equal([false], installer.LoadExistingCalls);
        Assert.Equal(0, updater.InitializeCalls);
        Assert.Equal(0, updater.CheckCalls);
        Assert.False(startup.IsCompleted);

        contentCompletion.SetResult(new InstallRecordVerification(
            InstallRecordVerificationState.Verified,
            installer.Record,
            "Client content verified without a startup hash."));
        await startup;

        Assert.Equal(installer.Record, orchestrator.InstalledRecord);
        Assert.False(viewModel.IsInstallationChecking);
        Assert.False(viewModel.ShowInstallationBanner);
        Assert.Equal(1, updater.InitializeCalls);
        Assert.Equal(1, updater.CheckCalls);
        Assert.Same(startup, viewModel.StartBackgroundInitializationAsync());
    }

    [Fact]
    public async Task StartupStatusReportsWhenTheUpdateFeedIsUnreachable()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            Session = FakeLauncherOrchestrator.CreateSession(
                LauncherActivityState.Exited,
                "Exited cleanly."),
        };
        var installer = new FakeLauncherInstaller();
        var updater = new StartupOrderUpdater
        {
            CheckException = new LauncherUpdateException("The update feed is unreachable."),
        };
        using var viewModel = new LauncherWindowViewModel(
            orchestrator,
            new ImmediateUiDispatcher(),
            installer,
            updater);

        viewModel.Initialize();
        await viewModel.StartBackgroundInitializationAsync();

        Assert.Equal(
            "Update check unavailable; the launcher works offline.",
            viewModel.OperationStatus);
        Assert.Null(viewModel.LastError);
    }

    [Fact]
    public async Task StartupStatusReportsUpToDateWhenNothingIsAvailable()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            Session = FakeLauncherOrchestrator.CreateSession(
                LauncherActivityState.Exited,
                "Exited cleanly."),
        };
        var installer = new FakeLauncherInstaller();
        var updater = new StartupOrderUpdater { ClientUpdateAvailable = false };
        using var viewModel = new LauncherWindowViewModel(
            orchestrator,
            new ImmediateUiDispatcher(),
            installer,
            updater);

        viewModel.Initialize();
        await viewModel.StartBackgroundInitializationAsync();

        Assert.StartsWith("Up to date", viewModel.OperationStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupStatusReportsAnAvailableUpdate()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            Session = FakeLauncherOrchestrator.CreateSession(
                LauncherActivityState.Exited,
                "Exited cleanly."),
        };
        var installer = new FakeLauncherInstaller();
        var updater = new StartupOrderUpdater { ClientUpdateAvailable = true };
        using var viewModel = new LauncherWindowViewModel(
            orchestrator,
            new ImmediateUiDispatcher(),
            installer,
            updater);

        viewModel.Initialize();
        await viewModel.StartBackgroundInitializationAsync();

        Assert.Equal("Update available.", viewModel.OperationStatus);
    }

    [Fact]
    public async Task RequiredWorldDataWorkIsExplainedAndNeverStartsWithoutConfirmation()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            Session = FakeLauncherOrchestrator.CreateSession(
                LauncherActivityState.Exited,
                "Exited cleanly."),
        };
        var installer = new FakeLauncherInstaller();
        var stale = installer.Record with
        {
            BakeToolVersion = LauncherInstallRecordStore.CurrentBakeToolVersion - 1,
            PreparedAssetSize = 28L * 1024 * 1024 * 1024,
        };
        ContentMigrationPlan migration = ContentMigrationCatalog.Resolve(
            stale.BakeToolVersion,
            LauncherInstallRecordStore.CurrentBakeToolVersion);
        installer.NextVerification = new InstallRecordVerification(
            InstallRecordVerificationState.ContentUpdateRequired,
            stale,
            $"World data update required: {migration.Reason}.",
            migration);
        using var viewModel = new LauncherWindowViewModel(
            orchestrator,
            new ImmediateUiDispatcher(),
            installer,
            new StartupOrderUpdater());

        viewModel.Initialize();
        await viewModel.StartBackgroundInitializationAsync();

        Assert.Same(stale, orchestrator.InstalledRecord);
        Assert.True(viewModel.FirstRunWizardShell.IsOpen);
        Assert.True(viewModel.FirstRunWizardShell.IsContentUpdate);
        Assert.Equal("World data update required", viewModel.InstallationBannerTitle);
        Assert.Contains("complete replacement pak", viewModel.FirstRunWizardShell.Body);
        Assert.Contains("existing package stays", viewModel.FirstRunWizardShell.Body);
        Assert.Contains("2 GiB", viewModel.FirstRunWizardShell.Body);
        Assert.Equal("Rebuild world data", viewModel.FirstRunWizardShell.StartActionText);
        Assert.Null(installer.InstallRequest);

        viewModel.FirstRunWizardShell.CloseCommand.Execute(null);
        Assert.Null(installer.InstallRequest);
        viewModel.FirstRunWizardShell.OpenCommand.Execute(null);
        await viewModel.FirstRunWizardShell.StartCommand.ExecuteAsync();

        Assert.NotNull(installer.InstallRequest);
        Assert.Equal(installer.Record, orchestrator.InstalledRecord);
        Assert.True(viewModel.FirstRunWizardShell.IsCompleted);
    }

    [Fact]
    public async Task PreparedWorldDataWaitsForMatchingClientAndNotNowCannotPublishIt()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            Session = FakeLauncherOrchestrator.CreateSession(
                LauncherActivityState.Exited,
                "Exited cleanly."),
        };
        var installer = new FakeLauncherInstaller();
        var stale = installer.Record with
        {
            BakeToolVersion = LauncherInstallRecordStore.CurrentBakeToolVersion - 1,
        };
        ContentMigrationPlan migration = ContentMigrationCatalog.Resolve(
            stale.BakeToolVersion,
            LauncherInstallRecordStore.CurrentBakeToolVersion);
        installer.NextVerification = new InstallRecordVerification(
            InstallRecordVerificationState.ContentUpdateRequired,
            stale,
            $"World data update required: {migration.Reason}.",
            migration);
        var updater = new StartupOrderUpdater { ClientUpdateAvailable = true };
        using var viewModel = new LauncherWindowViewModel(
            orchestrator,
            new ImmediateUiDispatcher(),
            installer,
            updater);

        viewModel.Initialize();
        await viewModel.StartBackgroundInitializationAsync();
        await viewModel.FirstRunWizardShell.StartCommand.ExecuteAsync();

        Assert.Null(orchestrator.InstalledRecord);
        Assert.Contains(
            "Play stays disabled",
            viewModel.FirstRunWizardShell.CompletedBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "matching game update",
            orchestrator.InstallationStatus,
            StringComparison.OrdinalIgnoreCase);

        viewModel.FirstRunWizardShell.AcknowledgeCompletionCommand.Execute(null);
        Assert.True(viewModel.UpdatePrompt.IsOpen);
        viewModel.UpdatePrompt.NotNowCommand.Execute(null);
        Assert.Null(orchestrator.InstalledRecord);
        Assert.Equal("Game update required", viewModel.InstallationBannerTitle);

        viewModel.UpdatePrompt.TryOpenPendingUpdate();
        await viewModel.UpdatePrompt.UpdateCommand.ExecuteAsync();

        Assert.Equal(1, updater.InstallCalls);
        Assert.Equal(installer.Record, orchestrator.InstalledRecord);
        Assert.False(viewModel.ShowInstallationBanner);
    }

    [Fact]
    public async Task ContentBuiltDuringStartupWaitsForCompatibilityCheckResult()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            Session = FakeLauncherOrchestrator.CreateSession(
                LauncherActivityState.Exited,
                "Exited cleanly."),
        };
        var installer = new FakeLauncherInstaller();
        var stale = installer.Record with
        {
            BakeToolVersion = LauncherInstallRecordStore.CurrentBakeToolVersion - 1,
        };
        ContentMigrationPlan migration = ContentMigrationCatalog.Resolve(
            stale.BakeToolVersion,
            LauncherInstallRecordStore.CurrentBakeToolVersion);
        installer.NextVerification = new InstallRecordVerification(
            InstallRecordVerificationState.ContentUpdateRequired,
            stale,
            $"World data update required: {migration.Reason}.",
            migration);
        var checkGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var updater = new StartupOrderUpdater { CheckGate = checkGate };
        using var viewModel = new LauncherWindowViewModel(
            orchestrator,
            new ImmediateUiDispatcher(),
            installer,
            updater);

        viewModel.Initialize();
        Task startup = viewModel.StartBackgroundInitializationAsync();
        Assert.True(viewModel.FirstRunWizardShell.IsOpen);
        Assert.False(startup.IsCompleted);

        await viewModel.FirstRunWizardShell.StartCommand.ExecuteAsync();
        Assert.Null(orchestrator.InstalledRecord);

        checkGate.SetResult(true);
        await startup;

        Assert.Equal(installer.Record, orchestrator.InstalledRecord);
        Assert.Contains(
            "play now",
            viewModel.FirstRunWizardShell.CompletedBody,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestartRestoresPendingClientGateUntilClientInstallCompletes()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            Session = FakeLauncherOrchestrator.CreateSession(
                LauncherActivityState.Exited,
                "Exited cleanly."),
        };
        var installer = new FakeLauncherInstaller();
        LauncherInstallRecord gatedRecord = installer.Record with
        {
            RequiresClientCompatibilityConfirmation = true,
        };
        installer.NextVerification = new InstallRecordVerification(
            InstallRecordVerificationState.Verified,
            gatedRecord,
            "World data is verified; matching client confirmation is pending.");
        var updater = new StartupOrderUpdater { ClientUpdateAvailable = true };
        using var viewModel = new LauncherWindowViewModel(
            orchestrator,
            new ImmediateUiDispatcher(),
            installer,
            updater);

        viewModel.Initialize();
        await viewModel.StartBackgroundInitializationAsync();

        Assert.Null(orchestrator.InstalledRecord);
        Assert.Equal("Game update required", viewModel.InstallationBannerTitle);
        Assert.True(viewModel.UpdatePrompt.IsOpen);

        await viewModel.UpdatePrompt.UpdateCommand.ExecuteAsync();

        Assert.Equal(1, installer.ConfirmCompatibilityCalls);
        Assert.NotNull(orchestrator.InstalledRecord);
        Assert.False(
            orchestrator.InstalledRecord!.RequiresClientCompatibilityConfirmation);
    }

    [Fact]
    public void ProfileCommandsExposeServerAccountCharacterCrudDialogsAndClearPasswords()
    {
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);

        viewModel.AddServerCommand.Execute(null);
        Assert.Equal(ProfileEditorKind.AddServer, viewModel.EditorDialog.Kind);
        viewModel.EditorDialog.Name = "Remote ACE";
        viewModel.EditorDialog.Host = "ace.example.test";
        viewModel.EditorDialog.Port = "9001";
        viewModel.EditorDialog.SubmitCommand.Execute(null);
        Assert.Equal(("Remote ACE", "ace.example.test", 9001), orchestrator.AddedServer);

        SelectServer(viewModel);
        viewModel.AddAccountCommand.Execute(null);
        Assert.Equal(ProfileEditorKind.AddAccount, viewModel.EditorDialog.Kind);
        viewModel.EditorDialog.Name = "second-account";
        viewModel.EditorDialog.Password = "one-use-secret";
        viewModel.EditorDialog.SubmitCommand.Execute(null);
        Assert.Equal(
            ("Local ACE", "second-account", "one-use-secret"),
            orchestrator.AddedAccount);
        Assert.Equal(string.Empty, viewModel.EditorDialog.Password);

        SelectAccount(viewModel);
        viewModel.AddCharacterCommand.Execute(null);
        Assert.Equal(ProfileEditorKind.AddCharacter, viewModel.EditorDialog.Kind);
        viewModel.EditorDialog.Name = "+Second";
        viewModel.EditorDialog.CharacterId = "0x5000000B";
        viewModel.EditorDialog.SubmitCommand.Execute(null);
        Assert.Equal(
            ("Local ACE", "testaccount", "+Second", "0x5000000B"),
            orchestrator.AddedCharacter);

        SelectCharacter(viewModel);
        viewModel.EditSelectedCommand.Execute(null);
        Assert.Equal(ProfileEditorKind.EditCharacter, viewModel.EditorDialog.Kind);
        viewModel.EditorDialog.Name = "+Renamed";
        viewModel.EditorDialog.CharacterId = "0x5000000C";
        viewModel.EditorDialog.SubmitCommand.Execute(null);
        Assert.Equal(
            ("Local ACE", "testaccount", "+Acdream", "+Renamed", "0x5000000C"),
            orchestrator.EditedCharacter);

        SelectCharacter(viewModel);
        viewModel.RemoveSelectedCommand.Execute(null);
        Assert.Equal(ProfileEditorKind.Remove, viewModel.EditorDialog.Kind);
        viewModel.EditorDialog.SubmitCommand.Execute(null);
        Assert.Equal(
            ("Local ACE", "testaccount", "+Acdream"),
            orchestrator.RemovedCharacter);
    }

    [Fact]
    public void ServerAndAccountEditRemoveDialogsRouteEveryMutationThroughCore()
    {
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);

        SelectServer(viewModel);
        viewModel.EditSelectedCommand.Execute(null);
        Assert.Equal(ProfileEditorKind.EditServer, viewModel.EditorDialog.Kind);
        Assert.Equal("127.0.0.1", viewModel.EditorDialog.Host);
        viewModel.EditorDialog.Name = "Renamed ACE";
        viewModel.EditorDialog.Host = "renamed.example.test";
        viewModel.EditorDialog.Port = "9010";
        viewModel.EditorDialog.SubmitCommand.Execute(null);
        Assert.Equal(
            ("Local ACE", "Renamed ACE", "renamed.example.test", 9010),
            orchestrator.EditedServer);

        SelectAccount(viewModel);
        viewModel.EditSelectedCommand.Execute(null);
        Assert.Equal(ProfileEditorKind.EditAccount, viewModel.EditorDialog.Kind);
        viewModel.EditorDialog.Name = "renamed-account";
        viewModel.EditorDialog.Password = "replacement-secret";
        viewModel.EditorDialog.SubmitCommand.Execute(null);
        Assert.Equal(
            ("Local ACE", "testaccount", "renamed-account", "replacement-secret"),
            orchestrator.EditedAccount);
        Assert.Equal(string.Empty, viewModel.EditorDialog.Password);

        SelectAccount(viewModel);
        viewModel.RemoveSelectedCommand.Execute(null);
        viewModel.EditorDialog.SubmitCommand.Execute(null);
        Assert.Equal(("Local ACE", "testaccount"), orchestrator.RemovedAccount);

        SelectServer(viewModel);
        viewModel.RemoveSelectedCommand.Execute(null);
        viewModel.EditorDialog.SubmitCommand.Execute(null);
        Assert.Equal("Local ACE", orchestrator.RemovedServer);
    }

    [Fact]
    public async Task CharacterSettingsAndLaunchActionsPreserveTheirTypedSemantics()
    {
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        SelectCharacter(viewModel);

        viewModel.CharacterLaunchMode = LaunchMode.Headless;
        viewModel.CharacterPluginsText = "Plugin.One\nPlugin.Two\nPlugin.One";
        viewModel.CharacterLoginCommandsText = " /tell someone, hi \n/vt start\n/tell someone, hi";
        viewModel.SaveCharacterSettingsCommand.Execute(null);

        Assert.NotNull(orchestrator.SettingsUpdate);
        Assert.Equal(LaunchMode.Headless, orchestrator.SettingsUpdate.Value.Mode);
        Assert.Equal(["Plugin.One", "Plugin.Two"], orchestrator.SettingsUpdate.Value.Plugins);
        Assert.Equal(
            ["/tell someone, hi", "/vt start", "/tell someone, hi"],
            orchestrator.SettingsUpdate.Value.Commands);

        await viewModel.LaunchHeadlessCommand.ExecuteAsync();
        Assert.Equal(
            ("Local ACE", "testaccount", "+Acdream", LaunchMode.Headless),
            orchestrator.LaunchRequest);
        Assert.Equal("Headless session started for +Acdream.", viewModel.OperationStatus);
    }

    [Fact]
    public async Task AccountGuiSelectWorksWithoutAnyCachedCharacter()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            IncludeCharacter = false,
        };
        using var viewModel = CreateInitialized(orchestrator);
        SelectAccount(viewModel);

        Assert.Empty(viewModel.SelectedNode!.Children);
        Assert.True(viewModel.CanLaunchAccountGuiSelect);
        await viewModel.LaunchAccountGuiSelectCommand.ExecuteAsync();

        Assert.Equal(
            ("Local ACE", "testaccount", (string?)null, LaunchMode.GuiSelect),
            orchestrator.LaunchRequest);
        Assert.Equal(
            "Character-select session started for testaccount.",
            viewModel.OperationStatus);
    }


    [Fact]
    public void UnsupportedPlatformDisablesGuiAndHeadlessAndExplainsWhy()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            Platform = UnsupportedPlatform(),
        };
        using var viewModel = CreateInitialized(orchestrator);
        SelectCharacter(viewModel);

        Assert.True(viewModel.ShowGraphicalLaunchNotice);
        Assert.False(viewModel.CanLaunchGui);
        Assert.False(viewModel.CanLaunchAccountGuiSelect);
        Assert.False(viewModel.CanLaunchHeadless);
        Assert.Equal(
            LauncherPlatformCapabilities.UnsupportedPlatformGraphicalLaunchDisabledReason,
            viewModel.GraphicalLaunchNotice);
        Assert.Contains(
            "supported on Windows and Linux",
            viewModel.GuiLaunchDisabledReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxPlatformShowsNoGraphicalLaunchNoticeAndEnablesGui()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            Platform = LinuxPlatform(),
        };
        using var viewModel = CreateInitialized(orchestrator);
        SelectCharacter(viewModel);

        Assert.False(viewModel.ShowGraphicalLaunchNotice);
        Assert.True(viewModel.CanLaunchGui);
    }

    [Fact]
    public void MissingCoDeployedHostReasonIsVisibleForCharacterActions()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            AccountLaunchCapability = LauncherCapability.Unavailable(
                "The co-deployed host is missing; reinstall or update the client."),
        };
        using var viewModel = CreateInitialized(orchestrator);
        SelectCharacter(viewModel);

        Assert.False(viewModel.CanLaunchGui);
        Assert.False(viewModel.CanLaunchHeadless);
        Assert.True(viewModel.ShowGuiLaunchDisabledReason);
        Assert.True(viewModel.ShowHeadlessLaunchDisabledReason);
        Assert.Contains("missing", viewModel.GuiLaunchDisabledReason, StringComparison.Ordinal);
        Assert.Contains("missing", viewModel.HeadlessLaunchDisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchErrorAndCancellationBecomeSafeVisibleState()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            LaunchHandler = _ => throw new LauncherOperationException("spawn failed safely"),
        };
        using var viewModel = CreateInitialized(orchestrator);
        SelectCharacter(viewModel);

        await viewModel.LaunchGuiCommand.ExecuteAsync();

        Assert.Equal("spawn failed safely", viewModel.LastError);
        Assert.Equal("Operation failed.", viewModel.OperationStatus);
        Assert.False(viewModel.IsBusy);

        orchestrator.LaunchHandler = async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return FakeLauncherOrchestrator.CreateSession();
        };
        Task launch = viewModel.LaunchGuiCommand.ExecuteAsync();
        Assert.True(viewModel.IsBusy);
        Assert.True(viewModel.CancelOperationCommand.CanExecute(null));
        viewModel.CancelOperationCommand.Execute(null);
        await launch;

        Assert.Equal("Operation cancelled.", viewModel.OperationStatus);
        Assert.False(viewModel.HasError);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public void ProfileDialogRedactsARejectedCredentialAndClearsItOnClose()
    {
        var dialog = new ProfileEditorDialogViewModel();
        dialog.Open(
            ProfileEditorKind.AddAccount,
            "Add account",
            candidate => throw new InvalidOperationException(
                $"rejected {candidate.Password}"));
        dialog.Password = "do-not-display";

        dialog.SubmitCommand.Execute(null);

        Assert.True(dialog.IsOpen);
        Assert.DoesNotContain("do-not-display", dialog.Error ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("[redacted]", dialog.Error ?? string.Empty, StringComparison.Ordinal);
        dialog.CancelCommand.Execute(null);
        Assert.Equal(string.Empty, dialog.Password);
        Assert.False(dialog.IsOpen);
    }

    [Fact]
    public void ModalShellsBlockBackgroundCommandsAndAreMutuallyExclusive()
    {
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);

        Assert.True(viewModel.AddServerCommand.CanExecute(null));
        viewModel.FirstRunWizardShell.OpenCommand.Execute(null);
        Assert.True(viewModel.IsModalOpen);
        Assert.False(viewModel.AddServerCommand.CanExecute(null));
        // LU3: there is no user-openable update panel any more — the update
        // question only ever appears by itself, at startup.
        Assert.False(viewModel.UpdatePrompt.IsOpen);
        Assert.False(Assert.Single(viewModel.Sessions).StopCommand.CanExecute(null));

        viewModel.AddServerCommand.Execute(null);
        Assert.False(viewModel.EditorDialog.IsOpen);
        Assert.Null(orchestrator.AddedServer);

        viewModel.CloseActiveModal();
        Assert.False(viewModel.IsModalOpen);
        Assert.True(viewModel.AddServerCommand.CanExecute(null));

        viewModel.AddServerCommand.Execute(null);
        Assert.True(viewModel.EditorDialog.IsOpen);
        Assert.False(viewModel.FirstRunWizardShell.OpenCommand.CanExecute(null));
        Assert.False(viewModel.ClearFinishedSessionsCommand.CanExecute(null));
        viewModel.CloseActiveModal();
        Assert.False(viewModel.EditorDialog.IsOpen);
    }

    [Fact]
    public async Task FirstRunWizardAutoDetectsValidatesAndPublishesVerifiedInstall()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            Session = FakeLauncherOrchestrator.CreateSession(
                LauncherActivityState.Exited,
                "Exited cleanly."),
        };
        var installer = new FakeLauncherInstaller();
        using var viewModel = CreateInitialized(orchestrator, installer);

        Assert.Empty(installer.LoadExistingCalls);

        viewModel.FirstRunWizardShell.OpenCommand.Execute(null);

        Assert.Equal(installer.DetectedDirectory, viewModel.FirstRunWizardShell.DatDirectory);
        Assert.True(viewModel.FirstRunWizardShell.IsDatDirectoryValid);
        Assert.True(viewModel.FirstRunWizardShell.StartCommand.CanExecute(null));

        viewModel.FirstRunWizardShell.SelectDatDirectory("incomplete-manual-path");
        Assert.False(viewModel.FirstRunWizardShell.IsDatDirectoryValid);
        viewModel.FirstRunWizardShell.SelectDatDirectory(installer.DetectedDirectory);
        Assert.True(viewModel.FirstRunWizardShell.IsDatDirectoryValid);

        viewModel.FirstRunWizardShell.ThreadsText = "3";
        await viewModel.FirstRunWizardShell.StartCommand.ExecuteAsync();

        Assert.Equal((installer.DetectedDirectory, 3), installer.InstallRequest);
        Assert.Equal(installer.Record, orchestrator.InstalledRecord);
        Assert.False(viewModel.IsFirstRunRequired);
        Assert.Equal(LauncherInstallPhase.Completed, viewModel.FirstRunWizardShell.Phase);
        Assert.Equal(100, viewModel.FirstRunWizardShell.ProgressPercent);
        Assert.False(viewModel.FirstRunWizardShell.HasError);
    }

    [Fact]
    public async Task FirstRunWizardCancellationAndFailureRemainVisibleAndPublishNothing()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            Session = FakeLauncherOrchestrator.CreateSession(
                LauncherActivityState.Exited,
                "Exited cleanly."),
        };
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = new FakeLauncherInstaller
        {
            InstallHandler = async (_, _, progress, cancellationToken) =>
            {
                progress?.Report(new LauncherInstallProgress(
                    LauncherInstallPhase.BakingMeshes,
                    "Baking mesh assets.",
                    1,
                    10));
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            },
        };
        using var viewModel = CreateInitialized(orchestrator, installer);
        viewModel.FirstRunWizardShell.OpenCommand.Execute(null);

        Task install = viewModel.FirstRunWizardShell.StartCommand.ExecuteAsync();
        await entered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(viewModel.FirstRunWizardShell.CancelCommand.CanExecute(null));
        Assert.False(viewModel.FirstRunWizardShell.CanEditInputs);
        viewModel.FirstRunWizardShell.CancelCommand.Execute(null);
        await install;

        Assert.Equal(LauncherInstallPhase.Cancelled, viewModel.FirstRunWizardShell.Phase);
        Assert.Null(orchestrator.InstalledRecord);
        Assert.False(viewModel.FirstRunWizardShell.HasError);

        installer.InstallHandler = (_, _, _, _) =>
            Task.FromException<LauncherInstallResult>(
                new LauncherInstallException("fixture bake failed"));
        await viewModel.FirstRunWizardShell.StartCommand.ExecuteAsync();

        Assert.Equal(LauncherInstallPhase.Failed, viewModel.FirstRunWizardShell.Phase);
        Assert.Contains(
            "fixture bake failed",
            viewModel.FirstRunWizardShell.Error ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Null(orchestrator.InstalledRecord);
    }

    [Fact]
    public async Task ActiveSessionStopAndFinishedSessionClearUseCoreOwnership()
    {
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);

        LauncherSessionRowViewModel session = Assert.Single(viewModel.Sessions);
        await session.StopCommand.ExecuteAsync();
        Assert.Equal("session-1", orchestrator.StoppedSessionId);

        orchestrator.Session = FakeLauncherOrchestrator.CreateSession(
            LauncherActivityState.Exited,
            "Exited cleanly.");
        orchestrator.RaiseStateChanged();
        Assert.True(viewModel.ClearFinishedSessionsCommand.CanExecute(null));
        viewModel.ClearFinishedSessionsCommand.Execute(null);
        Assert.True(orchestrator.ClearCalled);
    }

    [Fact]
    public async Task FirstRunSetupEndsWithACompletionPanelThatOkReturnsFrom()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            Session = FakeLauncherOrchestrator.CreateSession(
                LauncherActivityState.Exited,
                "Exited cleanly."),
        };
        var installer = new FakeLauncherInstaller();
        using var viewModel = CreateInitialized(orchestrator, installer);

        viewModel.FirstRunWizardShell.OpenCommand.Execute(null);
        Assert.False(viewModel.FirstRunWizardShell.IsCompleted);
        Assert.True(viewModel.FirstRunWizardShell.ShowSetupForm);

        await viewModel.FirstRunWizardShell.StartCommand.ExecuteAsync();

        Assert.True(viewModel.FirstRunWizardShell.IsCompleted);
        Assert.False(viewModel.FirstRunWizardShell.ShowSetupForm);
        Assert.True(viewModel.FirstRunWizardShell.IsOpen);
        // The record is already published, so the launcher behind the dialog
        // is in its launch-enabled state before OK is pressed.
        Assert.NotNull(orchestrator.InstalledRecord);

        viewModel.FirstRunWizardShell.AcknowledgeCompletionCommand.Execute(null);

        Assert.False(viewModel.FirstRunWizardShell.IsOpen);
        Assert.False(viewModel.FirstRunWizardShell.IsCompleted);
        Assert.True(viewModel.FirstRunWizardShell.ShowSetupForm);
    }

    [Fact]
    public async Task AFailedFirstRunSetupNeverShowsTheCompletionPanel()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            Session = FakeLauncherOrchestrator.CreateSession(
                LauncherActivityState.Exited,
                "Exited cleanly."),
        };
        var installer = new FakeLauncherInstaller
        {
            InstallHandler = (_, _, _, _) =>
                Task.FromException<LauncherInstallResult>(
                    new LauncherInstallException("The bake tool exited with code 3.")),
        };
        using var viewModel = CreateInitialized(orchestrator, installer);

        viewModel.FirstRunWizardShell.OpenCommand.Execute(null);
        await viewModel.FirstRunWizardShell.StartCommand.ExecuteAsync();

        Assert.False(viewModel.FirstRunWizardShell.IsCompleted);
        Assert.True(viewModel.FirstRunWizardShell.ShowSetupForm);
        Assert.True(viewModel.FirstRunWizardShell.HasError);
        Assert.Null(orchestrator.InstalledRecord);
    }

    /// <summary>
    /// LU1. Ordinary startup trusts a remembered digest so the window is not
    /// held behind a multi-second hash of a ~28 GiB file; "Verify files" is
    /// the deliberate way to make it read the whole package again, so it must
    /// force the full hash rather than hit the same fast path.
    /// </summary>
    [Fact]
    public async Task VerifyFilesForcesAFullHashAndPublishesTheResult()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            Session = FakeLauncherOrchestrator.CreateSession(
                LauncherActivityState.Exited,
                "Exited cleanly."),
        };
        var record = new LauncherInstallRecord(
            "C:/dats",
            "C:/data/pak/acdream.pak",
            new string('a', 64),
            4096,
            4);
        var installer = new FakeLauncherInstaller
        {
            NextVerification = new InstallRecordVerification(
                InstallRecordVerificationState.Verified,
                record,
                "Client content verified."),
        };
        using var viewModel = CreateInitialized(orchestrator, installer);

        await viewModel.VerifyContentCommand.ExecuteAsync();

        Assert.Equal([true], installer.LoadExistingCalls);
        Assert.Same(record, orchestrator.InstalledRecord);
        Assert.Equal("Client content verified.", viewModel.OperationStatus);
        Assert.Null(viewModel.LastError);
    }

    [Fact]
    public async Task VerifyFilesClearsTheInstallRecordWhenVerificationFails()
    {
        using var orchestrator = new FakeLauncherOrchestrator
        {
            Session = FakeLauncherOrchestrator.CreateSession(
                LauncherActivityState.Exited,
                "Exited cleanly."),
        };
        var installer = new FakeLauncherInstaller
        {
            NextVerification = new InstallRecordVerification(
                InstallRecordVerificationState.Invalid,
                null,
                "The prepared package SHA-256 does not match the install record."),
        };
        using var viewModel = CreateInitialized(orchestrator, installer);

        await viewModel.VerifyContentCommand.ExecuteAsync();

        Assert.Null(orchestrator.InstalledRecord);
        Assert.Contains("SHA-256", viewModel.OperationStatus, StringComparison.Ordinal);
        Assert.Contains("SHA-256", viewModel.LastError!, StringComparison.Ordinal);
    }

    private static LauncherWindowViewModel CreateInitialized(
        FakeLauncherOrchestrator orchestrator,
        ILauncherInstaller? installer = null)
    {
        var viewModel = new LauncherWindowViewModel(
            orchestrator,
            new ImmediateUiDispatcher(),
            installer);
        viewModel.Initialize();
        return viewModel;
    }

    private static void SelectServer(LauncherWindowViewModel viewModel) =>
        viewModel.SelectedNode = Assert.Single(viewModel.Servers);

    private static void SelectAccount(LauncherWindowViewModel viewModel) =>
        viewModel.SelectedNode = Assert.Single(Assert.Single(viewModel.Servers).Children);

    private static void SelectCharacter(LauncherWindowViewModel viewModel) =>
        viewModel.SelectedNode = Assert.Single(
            Assert.Single(Assert.Single(viewModel.Servers).Children).Children);

    private static LauncherPlatformCapabilities UnsupportedPlatform() => new(
        IsWindows: false,
        IsLinux: false,
        CanRunHeadless: false,
        CanLaunchGraphicalClient: false,
        PlatformName: "Unsupported",
        GraphicalLaunchDisabledReason:
            LauncherPlatformCapabilities.UnsupportedPlatformGraphicalLaunchDisabledReason);

    private static LauncherPlatformCapabilities LinuxPlatform() => new(
        IsWindows: false,
        IsLinux: true,
        CanRunHeadless: true,
        CanLaunchGraphicalClient: true,
        PlatformName: "Linux",
        GraphicalLaunchDisabledReason: null);

    private sealed class FakeLauncherOrchestrator : ILauncherOrchestrator
    {
        public event EventHandler? StateChanged;

        public bool LoadCalled { get; private set; }

        public bool ClearCalled { get; private set; }

        public LauncherPlatformCapabilities Platform { get; init; } = new(
            IsWindows: true,
            IsLinux: false,
            CanRunHeadless: true,
            CanLaunchGraphicalClient: true,
            PlatformName: "Windows",
            GraphicalLaunchDisabledReason: null);

        public LauncherCapability ProbeCapability { get; set; } = LauncherCapability.Available;

        public LauncherCapability? AccountLaunchCapability { get; set; }

        public bool IncludeCharacter { get; init; } = true;

        public LauncherSessionSnapshot Session { get; set; } = CreateSession();

        public Func<CancellationToken, Task<LauncherSessionSnapshot>>? LaunchHandler { get; set; }

        public (string Name, string Host, int Port)? AddedServer { get; private set; }

        public (string Name, string NewName, string NewHost, int NewPort)? EditedServer { get; private set; }

        public string? RemovedServer { get; private set; }

        public (string Server, string Account, string Password)? AddedAccount { get; private set; }

        public (string Server, string Account, string NewAccount, string? Password)? EditedAccount { get; private set; }

        public (string Server, string Account)? RemovedAccount { get; private set; }

        public (string Server, string Account, string Character, string? Id)? AddedCharacter { get; private set; }

        public (string Server, string Account, string Character, string NewName, string? Id)? EditedCharacter { get; private set; }

        public (string Server, string Account, string Character)? RemovedCharacter { get; private set; }

        public (LaunchMode Mode, IReadOnlyList<string> Plugins, IReadOnlyList<string> Commands)? SettingsUpdate { get; private set; }

        public (string Server, string Account, string? Character, LaunchMode Mode)? LaunchRequest { get; private set; }

        public (string Server, string Account)? ProbeRequest { get; private set; }

        public string? StoppedSessionId { get; private set; }

        public LauncherInstallRecord? InstalledRecord { get; private set; }

        public string InstallationStatus { get; private set; } =
            "No installed client is configured.";

        public void LoadProfiles() => LoadCalled = true;

        public LauncherStateSnapshot GetSnapshot() => new(
            [CreateServerSnapshot()],
            [Session],
            Platform,
            IsInstallationReady: InstalledRecord is not null,
            InstallationStatus);

        public LauncherCapability GetLaunchCapability(LaunchMode mode) =>
            Platform.ForLaunchMode(mode);

        public LauncherCapability GetAccountLaunchCapability(
            string serverName,
            string accountName,
            LaunchMode mode) =>
            AccountLaunchCapability ?? GetLaunchCapability(mode);

        public LauncherCapability GetProbeCapability(string serverName, string accountName) =>
            ProbeCapability;

        public void SetInstallRecord(LauncherInstallRecord? installRecord)
        {
            InstalledRecord = installRecord;
            InstallationStatus = installRecord is null
                ? "No installed client is configured."
                : "Client content verified.";
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetInstallationState(
            LauncherInstallRecord? installRecord,
            string installationStatus)
        {
            InstalledRecord = installRecord;
            InstallationStatus = installationStatus;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void AddServer(string name, string host, int port) =>
            AddedServer = (name, host, port);

        public void EditServer(string name, string newName, string newHost, int newPort) =>
            EditedServer = (name, newName, newHost, newPort);

        public void RemoveServer(string name) => RemovedServer = name;

        public void AddAccount(string serverName, string accountName, string password) =>
            AddedAccount = (serverName, accountName, password);

        public void EditAccount(
            string serverName,
            string accountName,
            string newAccountName,
            string? newPassword) =>
            EditedAccount = (serverName, accountName, newAccountName, newPassword);

        public void RemoveAccount(string serverName, string accountName) =>
            RemovedAccount = (serverName, accountName);

        public void AddCharacter(
            string serverName,
            string accountName,
            string characterName,
            string? characterId) =>
            AddedCharacter = (serverName, accountName, characterName, characterId);

        public void EditCharacterIdentity(
            string serverName,
            string accountName,
            string characterName,
            string newCharacterName,
            string? newCharacterId) =>
            EditedCharacter = (
                serverName,
                accountName,
                characterName,
                newCharacterName,
                newCharacterId);

        public void UpdateCharacterSettings(
            string serverName,
            string accountName,
            string characterName,
            LaunchMode launchMode,
            IReadOnlyList<string> plugins,
            IReadOnlyList<string> loginCommands) =>
            SettingsUpdate = (launchMode, plugins, loginCommands);

        public void RemoveCharacter(
            string serverName,
            string accountName,
            string characterName) =>
            RemovedCharacter = (serverName, accountName, characterName);

        public Task<LauncherSessionSnapshot> LaunchAsync(
            string serverName,
            string accountName,
            string? characterName,
            LaunchMode mode,
            CancellationToken cancellationToken = default)
        {
            LaunchRequest = (serverName, accountName, characterName, mode);
            return LaunchHandler?.Invoke(cancellationToken) ?? Task.FromResult(Session);
        }

        public Task<LauncherSessionSnapshot> ProbeAsync(
            string serverName,
            string accountName,
            CancellationToken cancellationToken = default)
        {
            ProbeRequest = (serverName, accountName);
            return Task.FromResult(Session with { Kind = LauncherActivityKind.Probe });
        }

        public Task StopSessionAsync(
            string sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            StoppedSessionId = sessionId;
            return Task.CompletedTask;
        }

        public void PollStatus()
        {
        }

        public void ClearFinishedSessions() => ClearCalled = true;

        public void Dispose()
        {
        }

        public void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

        public static LauncherSessionSnapshot CreateSession(
            LauncherActivityState state = LauncherActivityState.Connected,
            string status = "Connected.") => new(
                "session-1",
                LauncherActivityKind.Play,
                "Local ACE",
                "testaccount",
                "+Acdream",
                LaunchMode.Gui,
                state,
                status,
                ExitCode: state == LauncherActivityState.Exited ? 0 : null,
                Error: null,
                CreatedAt: DateTimeOffset.UnixEpoch);

        private LauncherServerSnapshot CreateServerSnapshot() => new(
            "Local ACE",
            "127.0.0.1",
            9000,
            [
                new LauncherAccountSnapshot(
                    "Local ACE",
                    "testaccount",
                    IncludeCharacter
                        ?
                        [
                            new LauncherCharacterSnapshot(
                                "Local ACE",
                                "testaccount",
                                "+Acdream",
                                "0x5000000A",
                                LaunchMode.GuiSelect,
                                ["Existing.Plugin"],
                                ["/tell someone, ready"],
                                HasRunningSession: true,
                                SessionStatus: "Connected."),
                        ]
                        : [],
                    HasRunningActivity: true,
                    ActivityStatus: "Connected."),
            ]);
    }

    private sealed class FakeLauncherInstaller : ILauncherInstaller
    {
        public string DetectedDirectory { get; } = Path.GetFullPath("retail-dats");

        public LauncherInstallRecord Record { get; }

        public (string DatDirectory, int Threads)? InstallRequest { get; private set; }

        public int ConfirmCompatibilityCalls { get; private set; }

        public Func<
            string,
            int,
            IProgress<LauncherInstallProgress>?,
            CancellationToken,
            Task<LauncherInstallResult>>? InstallHandler { get; set; }

        public FakeLauncherInstaller()
        {
            Record = new LauncherInstallRecord(
                DetectedDirectory,
                Path.GetFullPath("data/pak/acdream.pak"),
                new string('a', 64),
                123,
                LauncherInstallRecordStore.CurrentBakeToolVersion);
        }

        public IReadOnlyList<DatDirectoryValidation> DetectDatDirectories() =>
        [
            ValidateDatDirectory(DetectedDirectory),
        ];

        public DatDirectoryValidation ValidateDatDirectory(string? directory) =>
            string.Equals(directory, DetectedDirectory, StringComparison.Ordinal)
                ? new DatDirectoryValidation(
                    DetectedDirectory,
                    true,
                    "All four required retail DAT files were found.",
                    [])
                : new DatDirectoryValidation(
                    directory ?? string.Empty,
                    false,
                    "The DAT directory is incomplete.",
                    DatDirectoryLocator.RequiredFileNames);

        public InstallRecordVerification NextVerification { get; set; } =
            new(
                InstallRecordVerificationState.Missing,
                null,
                "Client content is not installed.");

        public List<bool> LoadExistingCalls { get; } = [];

        public Func<CancellationToken, Task<InstallRecordVerification>>?
            LoadExistingHandler { get; set; }

        public Task<InstallRecordVerification> LoadExistingAsync(
            CancellationToken cancellationToken = default,
            bool forceFullVerification = false)
        {
            LoadExistingCalls.Add(forceFullVerification);
            return LoadExistingHandler is null
                ? Task.FromResult(NextVerification)
                : LoadExistingHandler(cancellationToken);
        }

        public Task<LauncherInstallResult> InstallAsync(
            string datDirectory,
            int threads,
            IProgress<LauncherInstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstallRequest = (datDirectory, threads);
            if (InstallHandler is not null)
            {
                return InstallHandler(
                    datDirectory,
                    threads,
                    progress,
                    cancellationToken);
            }

            progress?.Report(new LauncherInstallProgress(
                LauncherInstallPhase.BakingMeshes,
                "Baking mesh assets.",
                5,
                10));
            progress?.Report(new LauncherInstallProgress(
                LauncherInstallPhase.VerifyingPackage,
                "Verifying package."));
            return Task.FromResult(new LauncherInstallResult(Record));
        }

        public void ConfirmClientCompatibility() => ConfirmCompatibilityCalls++;
    }

    private sealed class StartupOrderUpdater : ILauncherUpdater
    {
        private static readonly LauncherVersion Version = LauncherVersion.Parse("1.0.0");
        private readonly ClientVersionResolution _resolution = new(
            ClientVersionState.Missing,
            "No versioned client is installed.",
            null,
            null,
            null,
            null);

        public int InitializeCalls { get; private set; }

        public int CheckCalls { get; private set; }

        public int InstallCalls { get; private set; }

        public bool ClientUpdateAvailable { get; init; }

        public TaskCompletionSource<bool>? CheckGate { get; init; }

        public Exception? CheckException { get; init; }

        public ClientVersionResolution CurrentClient => _resolution;

        public Task<ClientVersionResolution> InitializeAsync(
            CancellationToken cancellationToken = default)
        {
            InitializeCalls++;
            return Task.FromResult(_resolution);
        }

        public async Task<LauncherUpdateCheckResult> CheckAsync(
            CancellationToken cancellationToken = default)
        {
            CheckCalls++;
            if (CheckGate is not null)
            {
                _ = await CheckGate.Task.WaitAsync(cancellationToken);
            }

            if (CheckException is not null)
            {
                throw CheckException;
            }

            var artifact = new ReleaseArtifact(
                new Uri("https://updates.example.test/acdream.zip"),
                new string('a', 64),
                1);
            var manifest = new ReleaseManifest(
                Version,
                Version,
                new Dictionary<string, ReleaseArtifact> { ["win-x64"] = artifact },
                new Dictionary<string, ReleaseArtifact> { ["win-x64"] = artifact });
            return new LauncherUpdateCheckResult(
                manifest,
                "win-x64",
                Version,
                Version,
                IsClientUpdateAvailable: ClientUpdateAvailable,
                IsLauncherUpdateAvailable: false,
                IsLauncherMinimumSatisfied: true,
                "Everything is current.");
        }

        public Task<ClientVersionResolution> InstallClientAsync(
            LauncherUpdateCheckResult check,
            IProgress<LauncherUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstallCalls++;
            return Task.FromResult(_resolution);
        }

        public Task<SelfUpdateStageResult> StageLauncherAsync(
            LauncherUpdateCheckResult check,
            IProgress<LauncherUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ClientVersionResolution> RollbackClientAsync(
            IProgress<LauncherUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
