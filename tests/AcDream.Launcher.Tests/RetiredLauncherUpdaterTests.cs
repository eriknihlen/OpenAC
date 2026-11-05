using AcDream.Launcher.Core.Updates;
using AcDream.Launcher.ViewModels;

namespace AcDream.Launcher.Tests;

/// <summary>The retired launcher installs the last release's client and nothing else.</summary>
public sealed class RetiredLauncherUpdaterTests
{
    [Fact]
    public async Task AnOlderClientIsOfferedTheLastReleaseAndInstallsIt()
    {
        var feed = new LastReleaseFeedFake { Installed = LauncherVersion.Parse("0.1.32") };
        using LauncherUpdateViewModel viewModel = Create(feed);

        await viewModel.StartupCheckAsync();

        Assert.True(viewModel.StartupCheckSucceeded);
        Assert.True(viewModel.IsClientUpdateAvailable);
        Assert.True(viewModel.IsOpen);
        Assert.Contains("0.1.33", viewModel.Body, StringComparison.Ordinal);

        await viewModel.UpdateCommand.ExecuteAsync();

        Assert.Equal(1, feed.InstallCalls);
        Assert.Equal(LauncherVersion.Parse("0.1.33"), feed.Installed);
    }

    [Fact]
    public async Task NoClientIsOfferedTheLastRelease()
    {
        var feed = new LastReleaseFeedFake { Installed = null };
        using LauncherUpdateViewModel viewModel = Create(feed);

        await viewModel.StartupCheckAsync();

        Assert.True(viewModel.IsClientUpdateAvailable);
        Assert.True(viewModel.IsOpen);
    }

    [Fact]
    public async Task TheLastReleaseInstalledSaysRetiredAndNothingElse()
    {
        var feed = new LastReleaseFeedFake { Installed = LauncherVersion.Parse("0.1.33") };
        using LauncherUpdateViewModel viewModel = Create(feed);

        await viewModel.StartupCheckAsync();

        Assert.True(viewModel.StartupCheckSucceeded);
        Assert.Equal("This client is retired; it gets no more updates.", viewModel.Status);
        Assert.False(viewModel.IsClientUpdateAvailable);
        Assert.False(viewModel.IsLauncherUpdateAvailable);
        Assert.False(viewModel.IsOpen);
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("0.1.34")]
    [InlineData("0.1.32")]
    public async Task AManifestForAnyOtherVersionIsRefused(string version)
    {
        var feed = new LastReleaseFeedFake
        {
            Installed = LauncherVersion.Parse("0.1.30"),
            ManifestVersion = LauncherVersion.Parse(version),
        };
        var updater = new RetiredLauncherUpdater(feed);
        CancellationToken token = TestContext.Current.CancellationToken;

        _ = await Assert.ThrowsAsync<LauncherUpdateException>(() => updater.CheckAsync(token));
        LauncherUpdateCheckResult foreign = await feed.CheckAsync(token);
        _ = await Assert.ThrowsAsync<LauncherUpdateException>(
            () => updater.InstallClientAsync(foreign, cancellationToken: token));
        Assert.Equal(0, feed.InstallCalls);

        using LauncherUpdateViewModel viewModel = Create(feed);
        await viewModel.StartupCheckAsync();
        Assert.False(viewModel.StartupCheckSucceeded);
        Assert.False(viewModel.IsClientUpdateAvailable);
        Assert.False(viewModel.IsOpen);
    }

    [Fact]
    public async Task ALauncherUpdateIsNeverOfferedOrStaged()
    {
        var feed = new LastReleaseFeedFake
        {
            Installed = LauncherVersion.Parse("0.1.32"),
            LauncherUpdateAvailable = true,
        };
        var updater = new RetiredLauncherUpdater(feed);
        CancellationToken token = TestContext.Current.CancellationToken;

        LauncherUpdateCheckResult check = await updater.CheckAsync(token);
        Assert.False(check.IsLauncherUpdateAvailable);
        Assert.True(check.IsClientUpdateAvailable);
        _ = await Assert.ThrowsAsync<LauncherUpdateException>(
            () => updater.StageLauncherAsync(check, cancellationToken: token));

        using LauncherUpdateViewModel viewModel = Create(feed);
        await viewModel.StartupCheckAsync();
        Assert.False(viewModel.IsLauncherUpdateAvailable);
        await viewModel.UpdateCommand.ExecuteAsync();

        Assert.Equal(0, feed.StageCalls);
        Assert.Equal(1, feed.InstallCalls);
    }

    private static LauncherUpdateViewModel Create(LastReleaseFeedFake feed) => new(
        new RetiredLauncherUpdater(feed),
        new ImmediateUiDispatcher(),
        () => { },
        canOpen: () => true,
        canMutate: () => true);
}

/// <summary>An updater reading a release manifest of a chosen version, deciding availability the way
/// the real one does: a client is offered when none is installed or the installed one is older.</summary>
internal sealed class LastReleaseFeedFake : ILauncherUpdater
{
    private static readonly LauncherVersion LauncherRunning = LauncherVersion.Parse("0.1.33");

    public LauncherVersion? Installed { get; set; }

    public LauncherVersion ManifestVersion { get; init; } = LauncherVersion.Parse("0.1.33");

    public bool LauncherUpdateAvailable { get; init; }

    public int InitializeCalls { get; private set; }

    public int CheckCalls { get; private set; }

    public int InstallCalls { get; private set; }

    public int StageCalls { get; private set; }

    public ClientVersionResolution CurrentClient => Installed is null
        ? new ClientVersionResolution(
            ClientVersionState.Missing, "No client is installed.", null, null, null, null)
        : new ClientVersionResolution(
            ClientVersionState.Verified,
            "Fixture client verified.",
            Installed,
            Path.Combine("fixture", Installed.Value),
            null,
            null);

    public Task<ClientVersionResolution> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        InitializeCalls++;
        return Task.FromResult(CurrentClient);
    }

    public Task<LauncherUpdateCheckResult> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        CheckCalls++;
        var artifact = new ReleaseArtifact(
            new Uri("https://example.test/release.zip"),
            new string('a', 64),
            100);
        var manifest = new ReleaseManifest(
            ManifestVersion,
            ManifestVersion,
            new Dictionary<string, ReleaseArtifact> { ["win-x64"] = artifact },
            new Dictionary<string, ReleaseArtifact> { ["win-x64"] = artifact });
        bool clientAvailable = Installed is null || ManifestVersion > Installed;
        return Task.FromResult(new LauncherUpdateCheckResult(
            manifest,
            "win-x64",
            LauncherRunning,
            Installed,
            clientAvailable,
            LauncherUpdateAvailable,
            IsLauncherMinimumSatisfied: true,
            "Fixture check."));
    }

    public Task<ClientVersionResolution> InstallClientAsync(
        LauncherUpdateCheckResult check,
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        InstallCalls++;
        Installed = check.Manifest.Version;
        return Task.FromResult(CurrentClient);
    }

    public Task<SelfUpdateStageResult> StageLauncherAsync(
        LauncherUpdateCheckResult check,
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        StageCalls++;
        return Task.FromResult(new SelfUpdateStageResult(
            check.Manifest.Version,
            "pending.json",
            "Launcher staged."));
    }

    public Task<ClientVersionResolution> RollbackClientAsync(
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CurrentClient);
}
