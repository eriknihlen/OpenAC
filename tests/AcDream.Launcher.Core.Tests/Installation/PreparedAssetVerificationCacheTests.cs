using AcDream.Launcher.Core.Installation;
using AcDream.Launcher.Core.Integrity;
using AcDream.Launcher.Core.Launching;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Installation;

[Collection(WorkingDirectoryCollection.Name)]
public sealed class PreparedAssetVerificationCacheTests : IDisposable
{
    private const string PackageContent = "verified package";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-verification-cache-tests",
        Guid.NewGuid().ToString("N"));
    private readonly ApplicationPathSet _paths;
    private readonly string _dats;
    private int _hashCount;

    public PreparedAssetVerificationCacheTests()
    {
        _paths = new ApplicationPathSet(
            Path.Combine(_root, "config"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "cache"),
            null);
        _dats = Path.Combine(_root, "retail-dats");
        Directory.CreateDirectory(_dats);
        foreach (string fileName in DatDirectoryLocator.RequiredFileNames)
        {
            File.WriteAllText(Path.Combine(_dats, fileName), "fixture");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task SecondStartupSkipsTheHashEntirely()
    {
        LauncherInstallRecordStore store = await CreateInstalledStoreAsync();

        Assert.True((await store.LoadAndVerifyAsync()).IsVerified);
        Assert.Equal(1, _hashCount);

        // The whole point of the slice: nothing is read from the package the
        // second time around.
        Assert.True((await store.LoadAndVerifyAsync()).IsVerified);
        Assert.Equal(1, _hashCount);
    }

    [Fact]
    public async Task CacheMissReportsTheExceptionalLongReadBeforeHashing()
    {
        LauncherInstallRecordStore store = await CreateInstalledStoreAsync();
        var statuses = new List<string>();

        Assert.True((await store.LoadAndVerifyAsync(
            progress: new ImmediateProgress(statuses.Add))).IsVerified);

        Assert.Contains(
            statuses,
            status => status.Contains("whole world-data pak", StringComparison.Ordinal)
                && status.Contains("30 seconds", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForcedFullVerificationHashesEvenWithAValidCache()
    {
        LauncherInstallRecordStore store = await CreateInstalledStoreAsync();
        Assert.True((await store.LoadAndVerifyAsync()).IsVerified);
        Assert.Equal(1, _hashCount);

        Assert.True((await store.LoadAndVerifyAsync(forceFullVerification: true))
            .IsVerified);
        Assert.Equal(2, _hashCount);
    }

    [Fact]
    public async Task ATouchedPackageIsHashedAgain()
    {
        LauncherInstallRecordStore store = await CreateInstalledStoreAsync();
        Assert.True((await store.LoadAndVerifyAsync()).IsVerified);
        Assert.Equal(1, _hashCount);

        File.SetLastWriteTimeUtc(
            store.PreparedAssetPath,
            File.GetLastWriteTimeUtc(store.PreparedAssetPath).AddMinutes(5));

        Assert.True((await store.LoadAndVerifyAsync()).IsVerified);
        Assert.Equal(2, _hashCount);
    }

    [Fact]
    public async Task ASilentlyCorruptedPackageOfTheSameSizeStillFailsAndDropsTheCache()
    {
        LauncherInstallRecordStore store = await CreateInstalledStoreAsync();
        Assert.True((await store.LoadAndVerifyAsync()).IsVerified);

        DateTime before = File.GetLastWriteTimeUtc(store.PreparedAssetPath);
        await File.WriteAllTextAsync(
            store.PreparedAssetPath,
            new string('x', PackageContent.Length));
        File.SetLastWriteTimeUtc(store.PreparedAssetPath, before.AddSeconds(1));

        InstallRecordVerification verification = await store.LoadAndVerifyAsync();

        Assert.False(verification.IsVerified);
        Assert.Contains("SHA-256", verification.Status, StringComparison.Ordinal);
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task CorruptionPreservingSizeAndWriteTimeIsCaughtOnlyByFullVerification()
    {
        LauncherInstallRecordStore store = await CreateInstalledStoreAsync();
        Assert.True((await store.LoadAndVerifyAsync()).IsVerified);

        DateTime original = File.GetLastWriteTimeUtc(store.PreparedAssetPath);
        await File.WriteAllTextAsync(
            store.PreparedAssetPath,
            new string('x', PackageContent.Length));
        File.SetLastWriteTimeUtc(store.PreparedAssetPath, original);

        Assert.True((await store.LoadAndVerifyAsync()).IsVerified);

        InstallRecordVerification forced =
            await store.LoadAndVerifyAsync(forceFullVerification: true);

        Assert.False(forced.IsVerified);
        Assert.Contains("SHA-256", forced.Status, StringComparison.Ordinal);
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task AResizedPackageIsRejectedWithoutHashingIt()
    {
        LauncherInstallRecordStore store = await CreateInstalledStoreAsync();
        Assert.True((await store.LoadAndVerifyAsync()).IsVerified);
        Assert.Equal(1, _hashCount);

        await File.WriteAllTextAsync(store.PreparedAssetPath, PackageContent + "!");

        InstallRecordVerification verification = await store.LoadAndVerifyAsync();

        Assert.False(verification.IsVerified);
        Assert.Contains("size changed", verification.Status, StringComparison.Ordinal);
        Assert.Equal(1, _hashCount);
    }

    [Fact]
    public async Task ACacheWhoseDigestDisagreesWithTheRecordIsIgnored()
    {
        LauncherInstallRecordStore store = await CreateInstalledStoreAsync();
        Assert.True((await store.LoadAndVerifyAsync()).IsVerified);
        Assert.Equal(1, _hashCount);

        await File.WriteAllTextAsync(
            CachePath,
            (await File.ReadAllTextAsync(CachePath)).Replace(
                await FileIntegrity.ComputeSha256HexAsync(store.PreparedAssetPath),
                new string('a', 64),
                StringComparison.OrdinalIgnoreCase));

        Assert.True((await store.LoadAndVerifyAsync()).IsVerified);
        Assert.Equal(2, _hashCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{\"version\":9999}")]
    public async Task AnUnreadableCacheDegradesToHashingRatherThanFailing(string content)
    {
        LauncherInstallRecordStore store = await CreateInstalledStoreAsync();
        Assert.True((await store.LoadAndVerifyAsync()).IsVerified);
        Assert.Equal(1, _hashCount);

        await File.WriteAllTextAsync(CachePath, content);

        Assert.True((await store.LoadAndVerifyAsync()).IsVerified);
        Assert.Equal(2, _hashCount);
    }

    [Fact]
    public async Task BackupRecoveryHashesTheBackupEvenWhenTheLiveCacheIsValid()
    {
        LauncherInstallRecordStore store = await CreateInstalledStoreAsync();
        Assert.True((await store.LoadAndVerifyAsync()).IsVerified);
        Assert.Equal(1, _hashCount);

        DateTime verifiedWriteTime = File.GetLastWriteTimeUtc(store.PreparedAssetPath);
        string backup = LauncherInstallRecordStore.GetBackupPath(store.PreparedAssetPath);
        File.Move(store.PreparedAssetPath, backup);
        await File.WriteAllTextAsync(
            store.PreparedAssetPath,
            new string('z', PackageContent.Length));
        File.SetLastWriteTimeUtc(
            store.PreparedAssetPath,
            verifiedWriteTime.AddMinutes(1));
        Assert.NotEqual(
            verifiedWriteTime,
            File.GetLastWriteTimeUtc(store.PreparedAssetPath));

        InstallRecordVerification verification = await store.LoadAndVerifyAsync();

        Assert.True(verification.IsVerified);
        Assert.Equal(PackageContent, await File.ReadAllTextAsync(store.PreparedAssetPath));
        Assert.False(File.Exists(backup));
        // Live package hashed (and failed), then the backup hashed.
        Assert.Equal(3, _hashCount);
    }

    private string CachePath => Path.Combine(
        Path.GetFullPath(_paths.DataDirectory),
        "install.verification.json");

    private async Task<LauncherInstallRecordStore> CreateInstalledStoreAsync()
    {
        var store = new LauncherInstallRecordStore(
            _paths,
            datDirectories: null,
            computeSha256: (path, cancellationToken) =>
            {
                Interlocked.Increment(ref _hashCount);
                return FileIntegrity.ComputeSha256HexAsync(path, cancellationToken);
            });

        Directory.CreateDirectory(Path.GetDirectoryName(store.PreparedAssetPath)!);
        await File.WriteAllTextAsync(store.PreparedAssetPath, PackageContent);
        var info = new FileInfo(store.PreparedAssetPath);
        await store.SaveAtomicallyAsync(new LauncherInstallRecord(
            Path.GetFullPath(_dats),
            Path.GetFullPath(store.PreparedAssetPath),
            await FileIntegrity.ComputeSha256HexAsync(store.PreparedAssetPath),
            info.Length,
            LauncherInstallRecordStore.CurrentBakeToolVersion));
        return store;
    }

    private sealed class ImmediateProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
