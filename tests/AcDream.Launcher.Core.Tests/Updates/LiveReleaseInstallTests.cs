using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Updates;

public sealed class LiveReleaseInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-live-release-install",
        Guid.NewGuid().ToString("N"));
    private readonly ApplicationPathSet _paths;
    private readonly string _rid = LauncherRuntimeIdentity.DetectRid();

    public LiveReleaseInstallTests() => _paths = UpdateTestData.Paths(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    [Trait("Lane", "Live")]
    public async Task InstallsTheAdvertisedClientFromTheLiveRelease()
    {
        using var http = new HttpClient();
        using var source = new ReleaseManifestClient(TimeSpan.FromSeconds(30));
        var versions = new ClientVersionStore(_paths);
        var updater = new LauncherUpdater(
            source,
            http,
            versions,
            new LauncherSelfUpdateManager(_paths, http),
            LauncherVersion.Parse("0.0.1"),
            _rid,
            Path.Combine(_root, "launcher"));

        _ = await updater.InitializeAsync();

        LauncherUpdateCheckResult check = await updater.CheckAsync();
        Assert.True(
            check.IsClientUpdateAvailable,
            $"The live feed advertised no client for RID '{_rid}': {check.Status}");

        ClientVersionResolution installed = await updater.InstallClientAsync(check);

        Assert.True(installed.IsVerified, installed.Status);
        Assert.Equal(check.Manifest.Version, installed.Version);
        Assert.NotNull(installed.Directory);

        // The launcher resolves its hosts out of the activated directory, so
        // assert what it will actually look for rather than merely "files exist".
        var executables = LauncherExecutableSet.FromCurrentVersionStore(versions);
        Assert.True(
            executables.GetAvailability(LaunchMode.Gui).IsAvailable,
            "The graphical client is not launchable from the installed release.");
        Assert.True(
            executables.GetAvailability(LaunchMode.Headless).IsAvailable,
            "The headless host is not launchable from the installed release.");

        Assert.True(File.Exists(versions.CurrentPointerPath));
        Assert.Contains(
            check.Manifest.Version.Value,
            await File.ReadAllTextAsync(versions.CurrentPointerPath),
            StringComparison.Ordinal);
    }
}
