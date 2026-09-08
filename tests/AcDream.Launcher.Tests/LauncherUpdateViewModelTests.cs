using AcDream.Launcher.Core.Updates;
using AcDream.Launcher.ViewModels;

namespace AcDream.Launcher.Tests;

public sealed class LauncherUpdateViewModelTests
{
    [Fact]
    public async Task NothingOutOfDateNeverShowsTheDialog()
    {
        var updater = new FakeUpdater
        {
            ClientUpdateAvailable = false,
            LauncherUpdateAvailable = false,
        };
        using LauncherUpdateViewModel viewModel = Create(updater);

        await viewModel.StartupCheckAsync();

        Assert.False(viewModel.IsOpen);
        Assert.Equal(0, updater.InstallCalls);
        Assert.Equal(0, updater.StageCalls);
    }

    [Fact]
    public async Task AClientUpdateAsksOnceAndInstallsOnUpdate()
    {
        var updater = new FakeUpdater
        {
            ClientUpdateAvailable = true,
            LauncherUpdateAvailable = false,
        };
        int clientChanged = 0;
        using LauncherUpdateViewModel viewModel = Create(
            updater,
            onClientChanged: () => clientChanged++);

        await viewModel.StartupCheckAsync();

        Assert.True(viewModel.IsOpen);
        Assert.Contains("game", viewModel.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2.0.0", viewModel.Body, StringComparison.Ordinal);

        await viewModel.UpdateCommand.ExecuteAsync();

        Assert.Equal(1, updater.InstallCalls);
        Assert.Equal(0, updater.StageCalls);
        Assert.Equal(1, clientChanged);
        Assert.False(viewModel.IsOpen);
        Assert.Null(viewModel.Error);
    }

    [Fact]
    public async Task ALauncherUpdateStagesThenRestartsWithoutTouchingTheClient()
    {
        var updater = new FakeUpdater
        {
            ClientUpdateAvailable = true,
            LauncherUpdateAvailable = true,
        };
        int shutdowns = 0;
        int applyCalls = 0;
        using LauncherUpdateViewModel viewModel = Create(
            updater,
            applyLauncherUpdateAsync: _ =>
            {
                applyCalls++;
                return Task.FromResult(true);
            },
            requestShutdown: () => shutdowns++);

        await viewModel.StartupCheckAsync();
        Assert.True(viewModel.IsOpen);

        await viewModel.UpdateCommand.ExecuteAsync();

        Assert.Equal(1, updater.StageCalls);
        Assert.Equal(1, applyCalls);
        Assert.Equal(1, shutdowns);
        Assert.Equal(0, updater.InstallCalls);
    }

    [Fact]
    public async Task ALauncherUpdateThatCannotRestartTellsTheUserToReopen()
    {
        var updater = new FakeUpdater
        {
            ClientUpdateAvailable = false,
            LauncherUpdateAvailable = true,
        };
        int shutdowns = 0;
        using LauncherUpdateViewModel viewModel = Create(
            updater,
            applyLauncherUpdateAsync: _ => Task.FromResult(false),
            requestShutdown: () => shutdowns++);

        await viewModel.StartupCheckAsync();
        await viewModel.UpdateCommand.ExecuteAsync();

        Assert.Equal(1, updater.StageCalls);
        Assert.Equal(0, shutdowns);
        Assert.Contains("reopen", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUnreachableFeedStaysSilent()
    {
        var updater = new FakeUpdater
        {
            CheckHandler = _ => Task.FromException<LauncherUpdateCheckResult>(
                new LauncherUpdateException("The update feed is unreachable.")),
        };
        using LauncherUpdateViewModel viewModel = Create(updater);

        await viewModel.StartupCheckAsync();

        Assert.False(viewModel.IsOpen);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task NotNowClosesWithoutUpdatingAnything()
    {
        var updater = new FakeUpdater { ClientUpdateAvailable = true };
        using LauncherUpdateViewModel viewModel = Create(updater);

        await viewModel.StartupCheckAsync();
        Assert.True(viewModel.IsOpen);

        viewModel.NotNowCommand.Execute(null);

        Assert.False(viewModel.IsOpen);
        Assert.Equal(0, updater.InstallCalls);
        Assert.Equal(0, updater.StageCalls);
    }

    [Fact]
    public async Task AnotherStartupQuestionDefersButDoesNotLoseTheUpdatePrompt()
    {
        var updater = new FakeUpdater { ClientUpdateAvailable = true };
        bool canOpen = false;
        using var viewModel = new LauncherUpdateViewModel(
            updater,
            new ImmediateUiDispatcher(),
            () => { },
            canOpen: () => canOpen,
            canMutate: () => true);

        await viewModel.StartupCheckAsync();
        Assert.False(viewModel.IsOpen);

        canOpen = true;
        viewModel.TryOpenPendingUpdate();

        Assert.True(viewModel.IsOpen);
    }

    [Fact]
    public async Task UpdatingIsRefusedWhileASessionIsRunning()
    {
        var updater = new FakeUpdater { ClientUpdateAvailable = true };
        using LauncherUpdateViewModel viewModel = Create(updater, canMutate: () => false);

        await viewModel.StartupCheckAsync();

        Assert.True(viewModel.IsOpen);
        Assert.False(viewModel.UpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task AFailedInstallReportsTheReasonAndLeavesTheDialogOpen()
    {
        var updater = new FakeUpdater
        {
            ClientUpdateAvailable = true,
            InstallHandler = (_, _) => Task.FromException<ClientVersionResolution>(
                new LauncherUpdateException("The download did not match its digest.")),
        };
        using LauncherUpdateViewModel viewModel = Create(updater);

        await viewModel.StartupCheckAsync();
        await viewModel.UpdateCommand.ExecuteAsync();

        Assert.True(viewModel.IsOpen);
        Assert.True(viewModel.HasError);
        Assert.Contains("digest", viewModel.Error!, StringComparison.Ordinal);
    }

    private static LauncherUpdateViewModel Create(
        FakeUpdater updater,
        Action? onClientChanged = null,
        Func<bool>? canMutate = null,
        Func<CancellationToken, Task<bool>>? applyLauncherUpdateAsync = null,
        Action? requestShutdown = null) =>
        new(
            updater,
            new ImmediateUiDispatcher(),
            onClientChanged ?? (() => { }),
            canOpen: () => true,
            canMutate: canMutate ?? (() => true),
            applyLauncherUpdateAsync: applyLauncherUpdateAsync,
            requestShutdown: requestShutdown);

    private sealed class FakeUpdater : ILauncherUpdater
    {
        private static readonly LauncherVersion One = LauncherVersion.Parse("1.0.0");
        private static readonly LauncherVersion Two = LauncherVersion.Parse("2.0.0");

        public ClientVersionResolution CurrentClient { get; private set; } =
            Resolution(One);

        public bool ClientUpdateAvailable { get; init; }

        public bool LauncherUpdateAvailable { get; init; }

        public bool MinimumSatisfied { get; init; } = true;

        public int InstallCalls { get; private set; }

        public int StageCalls { get; private set; }

        public Func<CancellationToken, Task<LauncherUpdateCheckResult>>? CheckHandler
        {
            get;
            init;
        }

        public Func<
            IProgress<LauncherUpdateProgress>?,
            CancellationToken,
            Task<ClientVersionResolution>>? InstallHandler { get; init; }

        public Task<ClientVersionResolution> InitializeAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CurrentClient);

        public Task<LauncherUpdateCheckResult> CheckAsync(
            CancellationToken cancellationToken = default) =>
            CheckHandler?.Invoke(cancellationToken) ?? Task.FromResult(CreateCheck());

        public async Task<ClientVersionResolution> InstallClientAsync(
            LauncherUpdateCheckResult check,
            IProgress<LauncherUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            InstallCalls++;
            if (InstallHandler is not null)
            {
                return await InstallHandler(progress, cancellationToken);
            }

            progress?.Report(new LauncherUpdateProgress(
                LauncherUpdatePhase.DownloadingClient,
                "Downloading fixture.",
                5,
                10));
            CurrentClient = Resolution(Two);
            return CurrentClient;
        }

        public Task<SelfUpdateStageResult> StageLauncherAsync(
            LauncherUpdateCheckResult check,
            IProgress<LauncherUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            StageCalls++;
            progress?.Report(new LauncherUpdateProgress(
                LauncherUpdatePhase.StagingLauncher,
                "Staged fixture.",
                1,
                1));
            return Task.FromResult(new SelfUpdateStageResult(
                Two,
                "pending.json",
                "Launcher staged."));
        }

        public Task<ClientVersionResolution> RollbackClientAsync(
            IProgress<LauncherUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CurrentClient);

        private LauncherUpdateCheckResult CreateCheck()
        {
            var artifact = new ReleaseArtifact(
                new Uri("https://example.test/release.zip"),
                new string('a', 64),
                100);
            var manifest = new ReleaseManifest(
                Two,
                MinimumSatisfied ? One : Two,
                new Dictionary<string, ReleaseArtifact> { ["win-x64"] = artifact },
                new Dictionary<string, ReleaseArtifact> { ["win-x64"] = artifact });
            return new LauncherUpdateCheckResult(
                manifest,
                "win-x64",
                One,
                CurrentClient.Version,
                ClientUpdateAvailable,
                LauncherUpdateAvailable,
                MinimumSatisfied,
                "Fixture check.");
        }

        private static ClientVersionResolution Resolution(LauncherVersion version) =>
            new(
                ClientVersionState.Verified,
                "Fixture client verified.",
                version,
                Path.Combine("fixture", version.Value),
                null,
                null);
    }
}
