using System.Text.Json;
using AcDream.Launcher.Core.Integrity;
using AcDream.Launcher.Core.Installation;
using AcDream.Launcher.Core.Launching;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Installation;

[CollectionDefinition(WorkingDirectoryCollection.Name, DisableParallelization = true)]
public sealed class WorkingDirectoryCollection
{
    public const string Name = "Launcher install-record working directory";
}

[Collection(WorkingDirectoryCollection.Name)]
public sealed class LauncherInstallRecordStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-install-record-tests",
        Guid.NewGuid().ToString("N"));
    private readonly ApplicationPathSet _paths;
    private readonly string _dats;

    public LauncherInstallRecordStoreTests()
    {
        _paths = new ApplicationPathSet(
            Path.Combine(_root, "config"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "cache"),
            null);
        _dats = Path.Combine(_root, "retail-dats");
        CreateCompleteDatDirectory(_dats);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AtomicRecordRoundTripVerifiesShaSizeAndBakeToolVersion()
    {
        var store = new LauncherInstallRecordStore(_paths);
        Directory.CreateDirectory(Path.GetDirectoryName(store.PreparedAssetPath)!);
        await File.WriteAllTextAsync(store.PreparedAssetPath, "verified package");
        LauncherInstallRecord record = await CreateRecordAsync(store);

        await store.SaveAtomicallyAsync(record);
        InstallRecordVerification verification = await store.LoadAndVerifyAsync();

        Assert.True(verification.IsVerified);
        Assert.Equal(record, verification.Record);
        Assert.Contains("SHA-256", verification.Status, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_paths.DataDirectory, ".install.json.*.tmp"));
    }

    [Fact]
    public async Task SizeAndShaCorruptionDisableTheInstall()
    {
        var store = new LauncherInstallRecordStore(_paths);
        Directory.CreateDirectory(Path.GetDirectoryName(store.PreparedAssetPath)!);
        await File.WriteAllTextAsync(store.PreparedAssetPath, "original");
        LauncherInstallRecord record = await CreateRecordAsync(store);
        await store.SaveAtomicallyAsync(record);

        await File.WriteAllTextAsync(store.PreparedAssetPath, "different-size");
        InstallRecordVerification size = await store.LoadAndVerifyAsync();
        Assert.Equal(InstallRecordVerificationState.Invalid, size.State);
        Assert.Contains("size changed", size.Status, StringComparison.OrdinalIgnoreCase);

        await File.WriteAllTextAsync(store.PreparedAssetPath, "tampered");
        var sameSizeRecord = record with
        {
            PreparedAssetSize = new FileInfo(store.PreparedAssetPath).Length,
            PreparedAssetSha256 = new string('0', 64),
        };
        await store.SaveAtomicallyAsync(sameSizeRecord);
        InstallRecordVerification sha = await store.LoadAndVerifyAsync();
        Assert.Equal(InstallRecordVerificationState.Invalid, sha.State);
        Assert.Contains("SHA-256", sha.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleBakeToolVersionPromptsForKnownMigrationBeforeHashing()
    {
        int hashCalls = 0;
        var store = new LauncherInstallRecordStore(
            _paths,
            computeSha256: (_, _) =>
            {
                hashCalls++;
                return Task.FromResult(new string('a', 64));
            });
        Directory.CreateDirectory(Path.GetDirectoryName(store.PreparedAssetPath)!);
        await File.WriteAllTextAsync(store.PreparedAssetPath, "package");
        var stale = new LauncherInstallRecord(
            _dats,
            store.PreparedAssetPath,
            new string('a', 64),
            new FileInfo(store.PreparedAssetPath).Length,
            LauncherInstallRecordStore.CurrentBakeToolVersion - 1);
        Directory.CreateDirectory(_paths.DataDirectory);
        await File.WriteAllTextAsync(
            store.RecordPath,
            JsonSerializer.Serialize(stale, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));

        InstallRecordVerification verification = await store.LoadAndVerifyAsync();
        Assert.Equal(
            InstallRecordVerificationState.ContentUpdateRequired,
            verification.State);
        Assert.NotNull(verification.Record);
        Assert.Equal(ContentWorkKind.FullRebuild, verification.RequiredContentWork?.Kind);
        Assert.Contains("World data update", verification.Status, StringComparison.Ordinal);
        Assert.Equal(0, hashCalls);
    }

    [Fact]
    public async Task NullIntegrityMetadataIsReportedAsInvalidInsteadOfThrowing()
    {
        var store = new LauncherInstallRecordStore(_paths);
        Directory.CreateDirectory(_paths.DataDirectory);
        await File.WriteAllTextAsync(
            store.RecordPath,
            JsonSerializer.Serialize(new
            {
                datDirectory = _dats,
                preparedAssetPath = store.PreparedAssetPath,
                preparedAssetSha256 = (string?)null,
                preparedAssetSize = 12,
                bakeToolVersion =
                    LauncherInstallRecordStore.CurrentBakeToolVersion,
                version = LauncherInstallRecord.CurrentRecordVersion,
            }));

        InstallRecordVerification verification = await store.LoadAndVerifyAsync();

        Assert.Equal(InstallRecordVerificationState.Invalid, verification.State);
        Assert.Contains("missing", verification.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingExplicitVersionIsRejectedBeforeAdmission()
    {
        var store = new LauncherInstallRecordStore(_paths);
        Directory.CreateDirectory(_paths.DataDirectory);
        await File.WriteAllTextAsync(
            store.RecordPath,
            JsonSerializer.Serialize(new
            {
                datDirectory = Path.GetFullPath(_dats),
                preparedAssetPath = Path.GetFullPath(store.PreparedAssetPath),
                preparedAssetSha256 = new string('a', 64),
                preparedAssetSize = 12,
                bakeToolVersion =
                    LauncherInstallRecordStore.CurrentBakeToolVersion,
            }));

        InstallRecordVerification verification = await store.LoadAndVerifyAsync();

        Assert.Equal(InstallRecordVerificationState.Invalid, verification.State);
        Assert.Contains("explicit", verification.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveNormalizesCanonicalAbsoluteDatAndPreparedPaths()
    {
        var store = new LauncherInstallRecordStore(_paths);
        Directory.CreateDirectory(Path.GetDirectoryName(store.PreparedAssetPath)!);
        await File.WriteAllTextAsync(store.PreparedAssetPath, "verified package");
        var info = new FileInfo(store.PreparedAssetPath);
        var nonCanonical = new LauncherInstallRecord(
            Path.Combine(_dats, "..", Path.GetFileName(_dats), "."),
            Path.Combine(
                Path.GetDirectoryName(store.PreparedAssetPath)!,
                "..",
                "pak",
                Path.GetFileName(store.PreparedAssetPath)),
            await FileIntegrity.ComputeSha256HexAsync(store.PreparedAssetPath),
            info.Length,
            LauncherInstallRecordStore.CurrentBakeToolVersion);

        await store.SaveAtomicallyAsync(nonCanonical);

        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(store.RecordPath));
        Assert.Equal(
            Path.GetFullPath(_dats),
            document.RootElement.GetProperty("datDirectory").GetString());
        Assert.Equal(
            Path.GetFullPath(store.PreparedAssetPath),
            document.RootElement.GetProperty("preparedAssetPath").GetString());
        Assert.Equal(
            LauncherInstallRecord.CurrentRecordVersion,
            document.RootElement.GetProperty("version").GetInt32());
        Assert.True((await store.LoadAndVerifyAsync()).IsVerified);
    }

    [Fact]
    public async Task RelativeDatRecordCannotChangeMeaningWithWorkingDirectory()
    {
        var store = new LauncherInstallRecordStore(_paths);
        Directory.CreateDirectory(Path.GetDirectoryName(store.PreparedAssetPath)!);
        await File.WriteAllTextAsync(store.PreparedAssetPath, "verified package");
        string alternateWorkingDirectory = Path.Combine(_root, "alternate-cwd");
        string alternateDats = Path.Combine(alternateWorkingDirectory, "retail-dats");
        CreateCompleteDatDirectory(alternateDats);
        var info = new FileInfo(store.PreparedAssetPath);
        var relative = new LauncherInstallRecord(
            "retail-dats",
            Path.GetFullPath(store.PreparedAssetPath),
            await FileIntegrity.ComputeSha256HexAsync(store.PreparedAssetPath),
            info.Length,
            LauncherInstallRecordStore.CurrentBakeToolVersion);
        Directory.CreateDirectory(_paths.DataDirectory);
        await File.WriteAllTextAsync(
            store.RecordPath,
            JsonSerializer.Serialize(relative, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));

        string originalWorkingDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = alternateWorkingDirectory;
            InstallRecordVerification verification =
                await store.LoadAndVerifyAsync();

            Assert.Equal(InstallRecordVerificationState.Invalid, verification.State);
            Assert.Contains("absolute", verification.Status, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.CurrentDirectory = originalWorkingDirectory;
        }
    }

    [Fact]
    public async Task OlderAbsoluteButNonCanonicalDatDocumentIsRejected()
    {
        var store = new LauncherInstallRecordStore(_paths);
        Directory.CreateDirectory(Path.GetDirectoryName(store.PreparedAssetPath)!);
        await File.WriteAllTextAsync(store.PreparedAssetPath, "verified package");
        var info = new FileInfo(store.PreparedAssetPath);
        var nonCanonical = new LauncherInstallRecord(
            Path.Combine(_dats, "..", Path.GetFileName(_dats)),
            Path.GetFullPath(store.PreparedAssetPath),
            await FileIntegrity.ComputeSha256HexAsync(store.PreparedAssetPath),
            info.Length,
            LauncherInstallRecordStore.CurrentBakeToolVersion);
        Directory.CreateDirectory(_paths.DataDirectory);
        await File.WriteAllTextAsync(
            store.RecordPath,
            JsonSerializer.Serialize(nonCanonical, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));

        InstallRecordVerification verification = await store.LoadAndVerifyAsync();

        Assert.Equal(InstallRecordVerificationState.Invalid, verification.State);
        Assert.Contains("canonical", verification.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartupRecoversPriorVerifiedPackageAfterInterruptedReplacement()
    {
        var store = new LauncherInstallRecordStore(_paths);
        Directory.CreateDirectory(Path.GetDirectoryName(store.PreparedAssetPath)!);
        await File.WriteAllTextAsync(store.PreparedAssetPath, "previous-good");
        LauncherInstallRecord record = await CreateRecordAsync(store);
        await store.SaveAtomicallyAsync(record);

        string backup = LauncherInstallRecordStore.GetBackupPath(
            store.PreparedAssetPath);
        File.Move(store.PreparedAssetPath, backup);
        await File.WriteAllTextAsync(store.PreparedAssetPath, "partial-new");

        InstallRecordVerification verification = await store.LoadAndVerifyAsync();

        Assert.True(verification.IsVerified);
        Assert.Equal("previous-good", await File.ReadAllTextAsync(store.PreparedAssetPath));
        Assert.False(File.Exists(backup));
    }

    private async Task<LauncherInstallRecord> CreateRecordAsync(
        LauncherInstallRecordStore store)
    {
        var info = new FileInfo(store.PreparedAssetPath);
        return new LauncherInstallRecord(
            Path.GetFullPath(_dats),
            Path.GetFullPath(store.PreparedAssetPath),
            await FileIntegrity.ComputeSha256HexAsync(store.PreparedAssetPath),
            info.Length,
            LauncherInstallRecordStore.CurrentBakeToolVersion);
    }

    private static void CreateCompleteDatDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (string fileName in DatDirectoryLocator.RequiredFileNames)
        {
            File.WriteAllText(Path.Combine(directory, fileName), "fixture");
        }
    }
}
