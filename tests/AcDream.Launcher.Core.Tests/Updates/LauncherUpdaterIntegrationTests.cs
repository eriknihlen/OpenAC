using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Updates;

public sealed class LauncherUpdaterIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-updater-integration-tests",
        Guid.NewGuid().ToString("N"));
    private readonly ApplicationPathSet _paths;
    private readonly string _rid = LauncherRuntimeIdentity.DetectRid();

    public LauncherUpdaterIntegrationTests() => _paths = UpdateTestData.Paths(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalHttpManifestDownloadExtractPromotionAndNextCheckAreCoherent()
    {
        using var server = new LocalHttpFixture();
        byte[] client = UpdateTestData.ClientZip(_rid, "release-2");
        byte[] launcher = UpdateTestData.LauncherZip(_rid, "release-2");
        ConfigureRelease(server, "2.0.0", "1.0.0", _rid, client, launcher);
        using var http = new HttpClient();
        using var source = ReleaseManifestClient.CreateLoopbackFixture(
            server.UriFor("manifest.json"));
        var versions = new ClientVersionStore(_paths);
        var updater = new LauncherUpdater(
            source,
            http,
            versions,
            new LauncherSelfUpdateManager(_paths, http),
            LauncherVersion.Parse("1.0.0"),
            _rid,
            Path.Combine(_root, "launcher"));
        _ = await updater.InitializeAsync();
        var progress = new List<LauncherUpdateProgress>();

        LauncherUpdateCheckResult check = await updater.CheckAsync();
        ClientVersionResolution installed = await updater.InstallClientAsync(
            check,
            new ImmediateProgress(progress.Add));
        LauncherUpdateCheckResult after = await updater.CheckAsync();

        Assert.True(check.IsClientUpdateAvailable);
        Assert.True(check.IsLauncherUpdateAvailable);
        Assert.True(check.IsLauncherMinimumSatisfied);
        Assert.True(installed.IsVerified);
        Assert.Equal("2.0.0", installed.Version!.Value);
        Assert.False(after.IsClientUpdateAvailable);
        Assert.True(after.IsLauncherUpdateAvailable);
        Assert.Contains(progress, item => item.Phase == LauncherUpdatePhase.DownloadingClient);
        Assert.Contains(progress, item => item.Phase == LauncherUpdatePhase.ExtractingClient);
        Assert.Contains(progress, item => item.Phase == LauncherUpdatePhase.ActivatingClient);
        Assert.Equal(LauncherUpdatePhase.Completed, progress[^1].Phase);
        Assert.Empty(Directory.EnumerateFiles(
            versions.AppDirectory,
            ".client-download-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task MinimumLauncherGateAndMissingRidFailBeforePublication()
    {
        using var server = new LocalHttpFixture();
        byte[] client = UpdateTestData.ClientZip(_rid);
        byte[] launcher = UpdateTestData.LauncherZip(_rid);
        ConfigureRelease(server, "3.0.0", "2.0.0", _rid, client, launcher);
        using var http = new HttpClient();
        using var source = ReleaseManifestClient.CreateLoopbackFixture(
            server.UriFor("manifest.json"));
        var versions = new ClientVersionStore(_paths);
        var updater = new LauncherUpdater(
            source,
            http,
            versions,
            new LauncherSelfUpdateManager(_paths, http),
            LauncherVersion.Parse("1.0.0"),
            _rid,
            Path.Combine(_root, "launcher"));
        _ = await updater.InitializeAsync();

        LauncherUpdateCheckResult check = await updater.CheckAsync();
        Assert.False(check.IsLauncherMinimumSatisfied);
        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            updater.InstallClientAsync(check));
        Assert.False(File.Exists(versions.CurrentPointerPath));

        string otherRid = _rid == "win-x64" ? "linux-x64" : "win-x64";
        ConfigureRelease(server, "3.0.0", "1.0.0", otherRid,
            UpdateTestData.ClientZip(otherRid), UpdateTestData.LauncherZip(otherRid));
        LauncherUpdateException missingRid = await Assert.ThrowsAsync<LauncherUpdateException>(
            () => updater.CheckAsync());
        Assert.Contains(_rid, missingRid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunningPredicateAndCrossProcessBarrierRefuseUpdateWithoutNetworkMutation()
    {
        using var server = new LocalHttpFixture();
        byte[] client = UpdateTestData.ClientZip(_rid);
        byte[] launcher = UpdateTestData.LauncherZip(_rid);
        ConfigureRelease(server, "2.0.0", "1.0.0", _rid, client, launcher);
        using var http = new HttpClient();
        using var source = ReleaseManifestClient.CreateLoopbackFixture(
            server.UriFor("manifest.json"));
        var versions = new ClientVersionStore(_paths);
        bool running = true;
        var updater = new LauncherUpdater(
            source,
            http,
            versions,
            new LauncherSelfUpdateManager(_paths, http),
            LauncherVersion.Parse("1.0.0"),
            _rid,
            Path.Combine(_root, "launcher"),
            () => running);
        _ = await updater.InitializeAsync();
        LauncherUpdateCheckResult check = await updater.CheckAsync();

        LauncherUpdateException local = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            updater.InstallClientAsync(check));
        Assert.Contains("Stop every", local.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(versions.CurrentPointerPath));

        running = false;
        using UpdateSessionBarrier.SessionLease session = versions.Barrier.AcquireSession();
        LauncherUpdateException shared = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            updater.InstallClientAsync(check));
        Assert.Contains("session", shared.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(versions.CurrentPointerPath));
    }

    [Fact]
    public async Task CancelledSlowClientDownloadLeavesNoPointerOrStaging()
    {
        using var server = new LocalHttpFixture();
        byte[] client = UpdateTestData.ClientZip(_rid, new string('x', 1_000_000));
        byte[] launcher = UpdateTestData.LauncherZip(_rid);
        server.Add("client.zip", client, chunkSize: 1024, chunkDelay: TimeSpan.FromMilliseconds(2));
        server.Add("launcher.zip", launcher);
        server.Add(
            "manifest.json",
            UpdateTestData.Manifest(
                "2.0.0",
                "1.0.0",
                _rid,
                server.UriFor("client.zip"),
                client,
                server.UriFor("launcher.zip"),
                launcher),
            contentType: "application/json");
        using var http = new HttpClient();
        using var source = ReleaseManifestClient.CreateLoopbackFixture(
            server.UriFor("manifest.json"));
        var versions = new ClientVersionStore(_paths);
        var updater = new LauncherUpdater(
            source,
            http,
            versions,
            new LauncherSelfUpdateManager(_paths, http),
            LauncherVersion.Parse("1.0.0"),
            _rid,
            Path.Combine(_root, "launcher"));
        _ = await updater.InitializeAsync();
        LauncherUpdateCheckResult check = await updater.CheckAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            updater.InstallClientAsync(check, cancellationToken: cancellation.Token));

        Assert.False(File.Exists(versions.CurrentPointerPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            versions.AppDirectory,
            ".client-*",
            SearchOption.TopDirectoryOnly));
    }

    private static void ConfigureRelease(
        LocalHttpFixture server,
        string version,
        string minimum,
        string rid,
        byte[] client,
        byte[] launcher)
    {
        server.Add("client.zip", client);
        server.Add("launcher.zip", launcher);
        server.Add(
            "manifest.json",
            UpdateTestData.Manifest(
                version,
                minimum,
                rid,
                server.UriFor("client.zip"),
                client,
                server.UriFor("launcher.zip"),
                launcher),
            contentType: "application/json");
    }

    private sealed class ImmediateProgress(Action<LauncherUpdateProgress> callback)
        : IProgress<LauncherUpdateProgress>
    {
        public void Report(LauncherUpdateProgress value) => callback(value);
    }
}
