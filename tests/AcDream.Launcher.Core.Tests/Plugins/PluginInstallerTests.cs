using System.Diagnostics;
using System.Net;
using System.Text;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Tests.Updates;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Plugins;

public sealed class PluginInstallerTests
{
    private const string Repo = "shaneedwards/openac-plugin-hello";
    private const string Id = "edwards.hello";

    [Fact]
    public async Task InstallRejectsAZipEntryOverTheSharedContractCap()
    {
        using var fixture = new Fixture();
        // 70 MiB: over the contract's 64 MiB per-entry/zip caps, under every invented cap this
        // installer used before it read them off the plan (256/512 MiB).
        byte[] oversizedPayload = new byte[70 * 1024 * 1024];
        Random.Shared.NextBytes(oversizedPayload);
        byte[] manifestBytes = Encoding.UTF8.GetBytes(Fixture.ManifestJson(Id, "0.1.0"));
        byte[] zipBytes = UpdateTestData.CreateZip(
        [
            ("plugin.json", manifestBytes, null),
            ($"{Id}.dll", oversizedPayload, null),
        ]);
        var release = new Fixture.Release(
            Id,
            "0.1.0",
            manifestBytes,
            zipBytes,
            UpdateTestData.Sha256(zipBytes),
            $"{Id}-0.1.0.zip");
        fixture.RegisterRelease(Repo, release);

        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, null, null));
    }

    [Fact]
    public async Task InstallSucceedsAndWritesAConfirmedRecord()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        PluginInstallResult result = await fixture.Installer.InstallOrUpdateAsync(
            Repo,
            catalog: null,
            clientResolution: null);

        Assert.Equal(Id, result.Id);
        Assert.Equal("0.1.0", result.Version);
        Assert.False(result.WasUpdate);
        Assert.True(File.Exists(
            Path.Combine(fixture.Paths.PluginsDirectory, Id, "plugin.json")));
        InstalledPluginRecord? record = fixture.RecordStore.Find(Id);
        Assert.NotNull(record);
        Assert.Equal("0.1.0", record!.Version);
        Assert.Equal("v0.1.0", record.Tag);
        Assert.Null(record.Pending);
    }

    [Fact]
    public async Task InstallLeavesNoEmptyStagingOrTrashFolder()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        await fixture.Installer.InstallOrUpdateAsync(Repo, null, null);

        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, ".staging")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, ".trash")));
    }

    [Fact]
    public async Task UpdateLeavesNoEmptyStagingOrTrashFolder()
    {
        using var fixture = new Fixture();
        var first = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, first);
        await fixture.Installer.InstallOrUpdateAsync(Repo, null, null);

        var second = fixture.BuildRelease(Id, "0.2.0");
        fixture.RegisterRelease(Repo, second);
        await fixture.Installer.InstallOrUpdateAsync(Repo, null, null);

        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, ".staging")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, ".trash")));
    }

    [Fact]
    public async Task RemoveLeavesNoEmptyTrashFolder()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);
        await fixture.Installer.InstallOrUpdateAsync(Repo, null, null);

        fixture.Installer.Remove(Id, deleteStorage: false);

        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, ".trash")));
    }

    [Fact]
    public void RecoverReclaimsLeftoverEmptyStagingAndTrashFolders()
    {
        using var fixture = new Fixture();
        string stagingRoot = Path.Combine(fixture.Paths.PluginsDirectory, ".staging");
        string trashRoot = Path.Combine(fixture.Paths.PluginsDirectory, ".trash");
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(trashRoot);

        fixture.Installer.Recover();

        Assert.False(Directory.Exists(stagingRoot));
        Assert.False(Directory.Exists(trashRoot));
    }

    [Fact]
    public async Task InstallNeverDeletesAStagingFolderSomethingElseIsStillUsing()
    {
        using var fixture = new Fixture();
        string stagingRoot = Path.Combine(fixture.Paths.PluginsDirectory, ".staging");
        string strayDirectory = Path.Combine(stagingRoot, "someone-elses-transaction");
        Directory.CreateDirectory(strayDirectory);
        File.WriteAllText(Path.Combine(strayDirectory, "in-progress.txt"), "still here");
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        await fixture.Installer.InstallOrUpdateAsync(Repo, null, null);

        Assert.True(Directory.Exists(strayDirectory));
        Assert.True(File.Exists(Path.Combine(strayDirectory, "in-progress.txt")));
    }

    [Fact]
    public async Task HashMismatchAbortsAndLeavesNoStaging()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release, shaFileSha256Override: new string('f', 64));

        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, null, null));

        string stagingRoot = Path.Combine(fixture.Paths.PluginsDirectory, ".staging");
        Assert.True(
            !Directory.Exists(stagingRoot)
            || !Directory.EnumerateFileSystemEntries(stagingRoot).Any());
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, Id)));
        Assert.Null(fixture.RecordStore.Find(Id));
    }

    [Fact]
    public async Task RetryAfterFailedDownloadSucceeds()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release, shaFileSha256Override: new string('f', 64));
        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, null, null));

        fixture.RegisterRelease(Repo, release);
        PluginInstallResult result = await fixture.Installer.InstallOrUpdateAsync(Repo, null, null);

        Assert.Equal(Id, result.Id);
        Assert.True(File.Exists(
            Path.Combine(fixture.Paths.PluginsDirectory, Id, "plugin.json")));
    }

    [Fact]
    public async Task ZipManifestMustMatchReleaseManifest()
    {
        using var fixture = new Fixture();
        byte[] releaseManifestBytes = Encoding.UTF8.GetBytes(Fixture.ManifestJson(Id, "0.1.0"));
        byte[] zipManifestBytes = Encoding.UTF8.GetBytes(
            Fixture.ManifestJson(Id, "0.1.0", displayName: "Not The Same"));
        byte[] zipBytes = UpdateTestData.CreateZip(
        [
            ("plugin.json", zipManifestBytes, null),
            ($"{Id}.dll", Encoding.UTF8.GetBytes("binary"), null),
        ]);
        var release = new Fixture.Release(
            Id,
            "0.1.0",
            releaseManifestBytes,
            zipBytes,
            UpdateTestData.Sha256(zipBytes),
            $"{Id}-0.1.0.zip");
        fixture.RegisterRelease(Repo, release);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, null, null));

        Assert.Contains("plugin.json", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, Id)));
    }

    [Fact]
    public async Task UnrecordedFolderWithSameNameIsRefused()
    {
        using var fixture = new Fixture();
        string collidingDirectory = Path.Combine(fixture.Paths.PluginsDirectory, Id);
        Directory.CreateDirectory(collidingDirectory);
        File.WriteAllText(Path.Combine(collidingDirectory, "leftover.txt"), "not a plugin");
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, null, null));

        Assert.Equal(
            $"A folder named {Id} is already in your plugins folder, and the launcher didn't "
            + "install it. Move or delete that folder, then try again.",
            error.Message);
        Assert.DoesNotContain(
            fixture.Paths.PluginsDirectory, error.Message, StringComparison.Ordinal);
        Assert.Contains(
            fixture.Paths.PluginsDirectory,
            error.InnerException!.Message,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(collidingDirectory, "leftover.txt")));
    }

    [Fact]
    public async Task InstallRefusedWhenIdCollidesWithAManualPlugin()
    {
        using var fixture = new Fixture();
        string manualDirectory = Path.Combine(fixture.Paths.PluginsDirectory, "someones-copy");
        Directory.CreateDirectory(manualDirectory);
        File.WriteAllText(
            Path.Combine(manualDirectory, "plugin.json"),
            Fixture.ManifestJson(Id, "0.0.1"));
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, null, null));

        Assert.Contains("manually installed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallRefusedWhenIdCollidesWithABundledPlugin()
    {
        using var fixture = new Fixture();
        string clientDirectory = Path.Combine(fixture.Root, "client");
        Directory.CreateDirectory(Path.Combine(clientDirectory, "plugins", Id));
        File.WriteAllText(
            Path.Combine(clientDirectory, "plugins", Id, "plugin.json"),
            Fixture.ManifestJson(Id, "0.1.0"));
        var clientResolution = new ClientVersionResolution(
            ClientVersionState.Verified,
            "ok",
            LauncherVersion.Parse("0.1.7"),
            clientDirectory,
            null,
            null);
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, null, clientResolution));

        Assert.Contains("client-bundled", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallRefusedWhenIdAlreadyInstalledFromADifferentRepo()
    {
        using var fixture = new Fixture();
        fixture.RecordStore.Records.Add(new InstalledPluginRecord(
            Id,
            "someoneElse/openac-plugin-hello",
            PluginInstallSource.Unlisted,
            "0.1.0",
            "v0.1.0",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            null));
        fixture.RecordStore.Save();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, null, null));

        Assert.Contains("someoneElse/openac-plugin-hello", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallRefusedWhenIdCollidesWithADifferentCaseSpellingOfAManualPlugin()
    {
        using var fixture = new Fixture();
        string manualDirectory = Path.Combine(fixture.Paths.PluginsDirectory, "someones-copy");
        Directory.CreateDirectory(manualDirectory);
        File.WriteAllText(
            Path.Combine(manualDirectory, "plugin.json"),
            Fixture.ManifestJson("Edwards.Hello", "0.0.1"));
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, null, null));

        Assert.Contains("manually installed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateRefusedWhenNotNewerThanInstalled()
    {
        using var fixture = new Fixture();
        var first = fixture.BuildRelease(Id, "0.2.0");
        fixture.RegisterRelease(Repo, first);
        await fixture.Installer.InstallOrUpdateAsync(Repo, null, null);

        var downgrade = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, downgrade);

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            fixture.Installer.InstallOrUpdateAsync(Repo, null, null));

        Assert.Contains("not newer", error.Message, StringComparison.Ordinal);
        Assert.Equal("0.2.0", fixture.RecordStore.Find(Id)!.Version);
    }

    [Fact]
    public async Task InstallRefusedWhileASessionLeaseIsHeld()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);

        string ready = Path.Combine(fixture.Root, "lease.ready");
        string releaseFile = Path.Combine(fixture.Root, "lease.release");
        string fixturePath = GetLeaseFixturePath();
        Assert.True(File.Exists(fixturePath), $"Missing fixture: {fixturePath}");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add(fixturePath);
        startInfo.ArgumentList.Add("hold-update-lease");
        startInfo.ArgumentList.Add("session");
        startInfo.ArgumentList.Add(fixture.Paths.DataDirectory);
        startInfo.ArgumentList.Add(ready);
        startInfo.ArgumentList.Add(releaseFile);
        using Process holder = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the lease fixture.");
        try
        {
            await WaitForFileAsync(ready, holder);

            LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
                fixture.Installer.InstallOrUpdateAsync(Repo, null, null));

            Assert.Equal(PluginInstaller.SessionLeaseRefusal, error.Message);
        }
        finally
        {
            await File.WriteAllTextAsync(releaseFile, "release");
            if (!holder.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                holder.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public void RecoveryReconcilesPendingEntry()
    {
        using var fixture = new Fixture();
        string pluginDirectory = Path.Combine(fixture.Paths.PluginsDirectory, Id);
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(
            Path.Combine(pluginDirectory, "plugin.json"),
            Fixture.ManifestJson(Id, "0.2.0"));
        fixture.RecordStore.Records.Add(new InstalledPluginRecord(
            Id,
            Repo,
            PluginInstallSource.Listed,
            "0.1.0",
            "v0.1.0",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            Pending: new PendingPluginInstall("0.2.0", "v0.2.0", new string('b', 64))));
        fixture.RecordStore.Save();

        fixture.Installer.Recover();

        InstalledPluginRecord? record = fixture.RecordStore.Find(Id);
        Assert.NotNull(record);
        Assert.Null(record!.Pending);
        Assert.Equal("0.2.0", record.Version);
        Assert.Equal("v0.2.0", record.Tag);
    }

    [Fact]
    public void RecoveryKeepsThePreviousVersionWhenAPendingUpdateDidNotComplete()
    {
        using var fixture = new Fixture();
        string pluginDirectory = Path.Combine(fixture.Paths.PluginsDirectory, Id);
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(
            Path.Combine(pluginDirectory, "plugin.json"),
            Fixture.ManifestJson(Id, "0.1.0"));
        fixture.RecordStore.Records.Add(new InstalledPluginRecord(
            Id,
            Repo,
            PluginInstallSource.Listed,
            "0.1.0",
            "v0.1.0",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            Pending: new PendingPluginInstall("0.2.0", "v0.2.0", new string('b', 64))));
        fixture.RecordStore.Save();

        fixture.Installer.Recover();

        InstalledPluginRecord? record = fixture.RecordStore.Find(Id);
        Assert.NotNull(record);
        Assert.Null(record!.Pending);
        Assert.Equal("0.1.0", record.Version);
    }

    [Fact]
    public void RecoveryDropsAnAbandonedNewInstallPendingEntry()
    {
        using var fixture = new Fixture();
        fixture.RecordStore.Records.Add(new InstalledPluginRecord(
            Id,
            Repo,
            PluginInstallSource.Listed,
            Version: null,
            Tag: null,
            ZipSha256: null,
            DateTimeOffset.UtcNow,
            Pending: new PendingPluginInstall("0.1.0", "v0.1.0", new string('a', 64))));
        fixture.RecordStore.Save();

        fixture.Installer.Recover();

        Assert.Null(fixture.RecordStore.Find(Id));
    }

    [Fact]
    public void RecoveryRestoresTrashWhenFolderMissing()
    {
        using var fixture = new Fixture();
        string trashDirectory = Path.Combine(fixture.Paths.PluginsDirectory, ".trash", $"{Id}-abc123");
        Directory.CreateDirectory(trashDirectory);
        File.WriteAllText(Path.Combine(trashDirectory, "plugin.json"), Fixture.ManifestJson(Id, "0.1.0"));

        fixture.Installer.Recover();

        string restored = Path.Combine(fixture.Paths.PluginsDirectory, Id);
        Assert.True(Directory.Exists(restored));
        Assert.True(File.Exists(Path.Combine(restored, "plugin.json")));
        Assert.False(Directory.Exists(trashDirectory));
    }

    [Fact]
    public void RecoveryDiscardsTrashWhenFolderAlreadyExists()
    {
        using var fixture = new Fixture();
        string keptDirectory = Path.Combine(fixture.Paths.PluginsDirectory, Id);
        Directory.CreateDirectory(keptDirectory);
        File.WriteAllText(Path.Combine(keptDirectory, "plugin.json"), Fixture.ManifestJson(Id, "0.2.0"));
        string trashDirectory = Path.Combine(fixture.Paths.PluginsDirectory, ".trash", $"{Id}-abc123");
        Directory.CreateDirectory(trashDirectory);
        File.WriteAllText(Path.Combine(trashDirectory, "plugin.json"), Fixture.ManifestJson(Id, "0.1.0"));

        fixture.Installer.Recover();

        Assert.True(File.Exists(Path.Combine(keptDirectory, "plugin.json")));
        Assert.False(Directory.Exists(trashDirectory));
    }

    [Fact]
    public async Task RemoveDeletesFolderAndRecordButKeepsStorageByDefault()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);
        await fixture.Installer.InstallOrUpdateAsync(Repo, null, null);
        string storageDirectory = Path.Combine(fixture.Paths.ConfigDirectory, "plugins", Id);
        Directory.CreateDirectory(storageDirectory);
        File.WriteAllText(Path.Combine(storageDirectory, "settings.json"), "{}");

        fixture.Installer.Remove(Id, deleteStorage: false);

        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, Id)));
        Assert.Null(fixture.RecordStore.Find(Id));
        Assert.True(Directory.Exists(storageDirectory));
    }

    [Fact]
    public async Task RemoveDeletesStorageOnlyWhenAsked()
    {
        using var fixture = new Fixture();
        var release = fixture.BuildRelease(Id, "0.1.0");
        fixture.RegisterRelease(Repo, release);
        await fixture.Installer.InstallOrUpdateAsync(Repo, null, null);
        string storageDirectory = Path.Combine(fixture.Paths.ConfigDirectory, "plugins", Id);
        Directory.CreateDirectory(storageDirectory);
        File.WriteAllText(Path.Combine(storageDirectory, "settings.json"), "{}");

        fixture.Installer.Remove(Id, deleteStorage: true);

        Assert.False(Directory.Exists(storageDirectory));
    }

    [Fact]
    public void RemoveRefusesAnIdThatIsNotLauncherManaged()
    {
        using var fixture = new Fixture();

        Assert.Throws<LauncherUpdateException>(() => fixture.Installer.Remove(Id, deleteStorage: false));
    }

    private static async Task WaitForFileAsync(string path, Process process)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!File.Exists(path))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Lease fixture exited early with {process.ExitCode}: "
                    + await process.StandardError.ReadToEndAsync());
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Lease fixture did not become ready.");
            }

            await Task.Delay(20);
        }
    }

    private static string GetLeaseFixturePath()
    {
        string root = FindRepositoryRoot();
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        return Path.Combine(
            root,
            "tests",
            "AcDream.Launcher.Core.Tests.Fixtures.InstallLeaseHolder",
            "bin",
            configuration,
            "net10.0",
            "AcDream.Launcher.Core.Tests.Fixtures.InstallLeaseHolder.dll");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(
            Path.GetTempPath(),
            "acdream-plugin-installer-tests",
            Guid.NewGuid().ToString("N"));
        public readonly ApplicationPathSet Paths;
        public readonly RoutingHandler Handler = new();
        public readonly HttpClient HttpClient;
        public readonly InstalledPluginRecordStore RecordStore;
        public readonly PluginInventory Inventory;
        public readonly PluginInstaller Installer;

        public Fixture()
        {
            Paths = new ApplicationPathSet(
                Path.Combine(Root, "config"),
                Path.Combine(Root, "data"),
                Path.Combine(Root, "cache"),
                null);
            HttpClient = new HttpClient(Handler);
            RecordStore = InstalledPluginRecordStore.ForApplicationPaths(Paths);
            Inventory = new PluginInventory(Paths, RecordStore);
            Installer = new PluginInstaller(Paths, HttpClient, RecordStore, Inventory);
        }

        public void Dispose()
        {
            HttpClient.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        public Release BuildRelease(string id, string version, string? entryDll = null)
        {
            entryDll ??= id + ".dll";
            byte[] manifestBytes = Encoding.UTF8.GetBytes(ManifestJson(id, version, entryDll));
            byte[] zipBytes = UpdateTestData.CreateZip(
            [
                ("plugin.json", manifestBytes, null),
                (entryDll, Encoding.UTF8.GetBytes("binary-" + id), null),
            ]);
            string sha256 = UpdateTestData.Sha256(zipBytes);
            return new Release(id, version, manifestBytes, zipBytes, sha256, $"{id}-{version}.zip");
        }

        public void RegisterRelease(
            string repo,
            Release release,
            string? shaFileSha256Override = null)
        {
            string tag = "v" + release.Version;
            Uri latestManifestUri = GitHubReleaseLocator.LatestAsset(repo, "plugin.json");
            Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(repo, tag, "plugin.json");
            Uri shaUri = GitHubReleaseLocator.TaggedAsset(repo, tag, release.ZipName + ".sha256");
            Uri zipUri = GitHubReleaseLocator.TaggedAsset(repo, tag, release.ZipName);

            Handler.EnqueueRedirect(latestManifestUri, taggedManifestUri);
            Handler.EnqueueOk(taggedManifestUri, release.ManifestBytes);
            Handler.EnqueueOk(
                shaUri,
                Encoding.UTF8.GetBytes(
                    $"{shaFileSha256Override ?? release.Sha256}  {release.ZipName}\n"));
            Handler.EnqueueOk(zipUri, release.ZipBytes);
        }

        public static string ManifestJson(
            string id,
            string version,
            string? entryDll = null,
            string? displayName = null) =>
            $$"""
            {
              "id": "{{id}}",
              "displayName": "{{displayName ?? id}}",
              "version": "{{version}}",
              "entryDll": "{{entryDll ?? id + ".dll"}}",
              "apiVersion": 1,
              "minHostVersion": "0.1.0",
              "hosts": ["headless"]
            }
            """;

        public sealed record Release(
            string Id,
            string Version,
            byte[] ManifestBytes,
            byte[] ZipBytes,
            string Sha256,
            string ZipName);
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<HttpResponseMessage>> _routes =
            new(StringComparer.Ordinal);

        public void EnqueueRedirect(Uri from, Uri to) => Route(from).Enqueue(Redirect(to));

        public void EnqueueOk(Uri uri, byte[] body) => Route(uri).Enqueue(Ok(body));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri
                ?? throw new InvalidOperationException("Test request has no URI.");
            if (_routes.TryGetValue(uri.AbsoluteUri, out Queue<HttpResponseMessage>? queue)
                && queue.Count > 0)
            {
                return Task.FromResult(queue.Dequeue());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private Queue<HttpResponseMessage> Route(Uri uri)
        {
            if (!_routes.TryGetValue(uri.AbsoluteUri, out Queue<HttpResponseMessage>? queue))
            {
                queue = new Queue<HttpResponseMessage>();
                _routes[uri.AbsoluteUri] = queue;
            }

            return queue;
        }

        private static HttpResponseMessage Redirect(Uri location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = location;
            return response;
        }

        private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };
    }
}
