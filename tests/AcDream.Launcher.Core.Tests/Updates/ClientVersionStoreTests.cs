using System.Text;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Updates;

public sealed class ClientVersionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-client-version-tests",
        Guid.NewGuid().ToString("N"));
    private readonly ApplicationPathSet _paths;
    private readonly string _rid;

    public ClientVersionStoreTests()
    {
        _paths = UpdateTestData.Paths(_root);
        _rid = LauncherRuntimeIdentity.DetectRid();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AtomicPromotionPublishesStrictPointerAndRetainsPreviousForRollback()
    {
        var store = new ClientVersionStore(_paths);
        ClientVersionResolution first = await PromoteAsync(store, "1.0.0", "first");
        ClientVersionResolution second = await PromoteAsync(store, "2.0.0", "second");

        Assert.True(first.IsVerified);
        Assert.True(second.IsVerified);
        Assert.Equal("2.0.0", second.Version!.Value);
        Assert.Equal("1.0.0", second.PreviousVersion);
        Assert.True(Directory.Exists(store.GetVersionDirectory(LauncherVersion.Parse("1.0.0"))));
        Assert.True(Directory.Exists(store.GetVersionDirectory(LauncherVersion.Parse("2.0.0"))));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            store.AppDirectory,
            ".client-staging-*",
            SearchOption.TopDirectoryOnly));
        string pointer = await File.ReadAllTextAsync(store.CurrentPointerPath);
        Assert.Contains("\"schemaVersion\": 1", pointer, StringComparison.Ordinal);
        Assert.Contains("\"currentVersion\": \"2.0.0\"", pointer, StringComparison.Ordinal);
        Assert.DoesNotContain(".client-staging", pointer, StringComparison.Ordinal);

        ClientVersionResolution rolledBack = await store.RollbackAsync(_rid);

        Assert.Equal("1.0.0", rolledBack.Version!.Value);
        Assert.Equal("2.0.0", rolledBack.PreviousVersion);
        Assert.Equal("first-gui", await File.ReadAllTextAsync(
            Path.Combine(rolledBack.Directory!, GraphicalHostName)));
    }

    [Fact]
    public async Task TornCurrentPointerRecoversLastDurablePointerAndOwnedTempResidue()
    {
        var store = new ClientVersionStore(_paths);
        _ = await PromoteAsync(store, "1.0.0", "first");
        _ = await PromoteAsync(store, "2.0.0", "second");
        string temp = Path.Combine(
            store.AppDirectory,
            $".current.json.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(temp, "partial temp");
        await File.WriteAllTextAsync(store.CurrentPointerPath, "{\"schemaVersion\":1,");

        var recoveredStore = new ClientVersionStore(_paths);
        ClientVersionResolution recovered = await recoveredStore.LoadAndRecoverAsync(_rid);

        Assert.True(recovered.IsVerified);
        Assert.Equal("1.0.0", recovered.Version!.Value);
        Assert.Contains("Recovered", recovered.Status, StringComparison.Ordinal);
        Assert.False(File.Exists(temp));
        Assert.Contains("\"currentVersion\": \"1.0.0\"", await File.ReadAllTextAsync(
            recoveredStore.CurrentPointerPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptActiveInstallFailsClosedButVerifiedPreviousCanRollback()
    {
        var store = new ClientVersionStore(_paths);
        _ = await PromoteAsync(store, "1.0.0", "first");
        ClientVersionResolution second = await PromoteAsync(store, "2.0.0", "second");
        string graphical = Path.Combine(second.Directory!, GraphicalHostName);
        await File.WriteAllTextAsync(graphical, "tampered!!");

        var restarted = new ClientVersionStore(_paths);
        ClientVersionResolution invalid = await restarted.LoadAndRecoverAsync(_rid);

        Assert.Equal(ClientVersionState.Invalid, invalid.State);
        Assert.Contains("corrupt", invalid.Status, StringComparison.OrdinalIgnoreCase);
        ClientVersionResolution rolledBack = await restarted.RollbackAsync(_rid);
        Assert.Equal("1.0.0", rolledBack.Version!.Value);
    }

    [Fact]
    public async Task StrictPointerAndInstallRecordsRejectUnknownUnrecordedAndWrongRidState()
    {
        var store = new ClientVersionStore(_paths);
        ClientVersionResolution installed = await PromoteAsync(store, "1.0.0", "strict");
        await File.WriteAllTextAsync(
            Path.Combine(installed.Directory!, "unrecorded.dll"),
            "unexpected");

        var restarted = new ClientVersionStore(_paths);
        ClientVersionResolution unrecorded = await restarted.LoadAndRecoverAsync(_rid);
        Assert.Equal(ClientVersionState.Invalid, unrecorded.State);
        Assert.Contains("unrecorded", unrecorded.Status, StringComparison.OrdinalIgnoreCase);

        File.Delete(Path.Combine(installed.Directory!, "unrecorded.dll"));
        string installPath = ClientVersionStore.GetMetadataPath(installed.Directory!);
        string install = await File.ReadAllTextAsync(installPath);
        await File.WriteAllTextAsync(
            installPath,
            install.Replace(
                "\"schemaVersion\": 1",
                "\"schemaVersion\": 1,\"schemaVersion\": 1",
                StringComparison.Ordinal));
        ClientVersionResolution duplicate = await restarted.LoadAndRecoverAsync(_rid);
        Assert.Equal(ClientVersionState.Invalid, duplicate.State);
        Assert.Contains("Duplicate", duplicate.Status, StringComparison.Ordinal);
        await File.WriteAllTextAsync(installPath, install);

        string current = await File.ReadAllTextAsync(store.CurrentPointerPath);
        await File.WriteAllTextAsync(
            store.CurrentPointerPath,
            current.TrimEnd().TrimEnd('}') + ",\"unknown\":true}");
        ClientVersionResolution unknown = await restarted.LoadAndRecoverAsync(_rid);
        Assert.Equal(ClientVersionState.Invalid, unknown.State);

        await File.WriteAllTextAsync(store.CurrentPointerPath, current);
        string otherRid = _rid == "win-x64" ? "linux-x64" : "win-x64";
        ClientVersionResolution wrongRid = await restarted.LoadAndRecoverAsync(otherRid);
        Assert.Equal(ClientVersionState.Invalid, wrongRid.State);
        Assert.Contains("RID", wrongRid.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DynamicExecutableResolverTracksOnlyVerifiedCurrentVersion()
    {
        var store = new ClientVersionStore(_paths);
        LauncherExecutableSet executables = LauncherExecutableSet.FromCurrentVersionStore(store);
        Assert.False(executables.GetAvailability(LaunchMode.Gui).IsAvailable);

        ClientVersionResolution first = await PromoteAsync(store, "1.0.0", "one");
        Assert.Equal(first.Directory, executables.WorkingDirectory);
        Assert.Equal(
            Path.Combine(first.Directory!, GraphicalHostName),
            executables.CreatePlaySpec(LaunchMode.Gui, "session.json").ExecutablePath);

        ClientVersionResolution second = await PromoteAsync(store, "2.0.0", "two");
        Assert.Equal(second.Directory, executables.WorkingDirectory);
        Assert.Equal(
            Path.Combine(second.Directory!, "acdream-headless" + ExecutableSuffix),
            executables.CreateProbeSpec("session.json").ExecutablePath);
    }

    [Fact]
    public async Task ExistingSemanticVersionCannotReplaceActiveContentInPlace()
    {
        var store = new ClientVersionStore(_paths);
        _ = await PromoteAsync(store, "1.0.0", "original");

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            PromoteAsync(store, "1.0.0", "different"));

        Assert.Contains("active client version", error.Message, StringComparison.OrdinalIgnoreCase);
        ClientVersionResolution resolution = await store.LoadAndRecoverAsync(_rid);
        Assert.Equal("original-gui", await File.ReadAllTextAsync(
            Path.Combine(resolution.Directory!, GraphicalHostName)));
    }

    [Fact]
    [Trait("Lane", "Unix")]
    public async Task UnixRejectsNonCanonicalInstallJsonCasing()
    {
        if (!LauncherOperatingSystem.IsUnix)
        {
            throw new PlatformNotSupportedException("Lane=Unix requires a native Unix host.");
        }

        var store = new ClientVersionStore(_paths);
        ClientVersionResolution installed = await PromoteAsync(store, "1.0.0", "linux-case");
        await File.WriteAllTextAsync(
            Path.Combine(installed.Directory!, "INSTALL.JSON"),
            "must-not-be-hidden");

        ClientVersionResolution resolution = await new ClientVersionStore(_paths)
            .LoadAndRecoverAsync(_rid);

        Assert.Equal(ClientVersionState.Invalid, resolution.State);
        if (OperatingSystem.IsLinux())
        {
            Assert.Contains("unrecorded", resolution.Status, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ExclusiveStartupReclaimsOnlyCanonicalGuidOwnedResidue()
    {
        var store = new ClientVersionStore(_paths);
        Directory.CreateDirectory(store.AppDirectory);
        string id = Guid.NewGuid().ToString("N");
        string nearId = id[..31] + "g";
        string exactStaging = Path.Combine(store.AppDirectory, ".client-staging-" + id);
        string exactCorrupt = Path.Combine(store.AppDirectory, ".client-corrupt-" + id);
        string exactDownload = Path.Combine(
            store.AppDirectory,
            ".client-download-" + id + ".zip");
        string nearStaging = exactStaging + "-user";
        string nearCorrupt = Path.Combine(store.AppDirectory, ".client-corrupt-" + nearId);
        string nearDownload = Path.Combine(
            store.AppDirectory,
            ".client-download-" + id + ".zip.user");
        Directory.CreateDirectory(exactStaging);
        Directory.CreateDirectory(exactCorrupt);
        Directory.CreateDirectory(nearStaging);
        Directory.CreateDirectory(nearCorrupt);
        await File.WriteAllTextAsync(exactDownload, "owned");
        await File.WriteAllTextAsync(nearDownload, "preserve");

        _ = await store.LoadAndRecoverAsync(_rid);

        Assert.False(Directory.Exists(exactStaging));
        Assert.False(Directory.Exists(exactCorrupt));
        Assert.False(File.Exists(exactDownload));
        Assert.True(Directory.Exists(nearStaging));
        Assert.True(Directory.Exists(nearCorrupt));
        Assert.True(File.Exists(nearDownload));
    }

    private string ExecutableSuffix => _rid.StartsWith("win-", StringComparison.Ordinal)
        ? ".exe"
        : string.Empty;

    private string GraphicalHostName =>
        PayloadExecutableNames.GraphicalHostForRid(_rid) + ExecutableSuffix;

    private async Task<ClientVersionResolution> PromoteAsync(
        ClientVersionStore store,
        string versionText,
        string marker)
    {
        byte[] archive = UpdateTestData.ClientZip(_rid, marker);
        string zipPath = Path.Combine(_root, $"{versionText}-{marker}.zip");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(zipPath, archive);
        string staging = store.CreateClientStagingDirectory(Guid.NewGuid());
        IReadOnlyList<ExtractedFileRecord> files = await new SafeZipExtractor()
            .ExtractAsync(zipPath, staging);
        using UpdateSessionBarrier.ExclusiveLease lease = store.Barrier.AcquireExclusive();
        return await store.PromoteAndActivateUnderLeaseAsync(
            staging,
            LauncherVersion.Parse(versionText),
            _rid,
            new ReleaseArtifact(
                new Uri($"https://example.test/{versionText}.zip"),
                UpdateTestData.Sha256(archive),
                archive.LongLength),
            files);
    }
}
