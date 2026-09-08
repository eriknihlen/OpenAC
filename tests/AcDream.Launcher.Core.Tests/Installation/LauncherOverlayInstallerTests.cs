using System.Text.Json;
using AcDream.Launcher.Core.Integrity;
using AcDream.Launcher.Core.Installation;
using AcDream.Launcher.Core.Launching;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Installation;

public sealed class LauncherOverlayInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-overlay-installer-tests",
        Guid.NewGuid().ToString("N"));
    private readonly ApplicationPathSet _paths;
    private readonly string _dats;
    private readonly string _bakeExecutable;

    public LauncherOverlayInstallerTests()
    {
        _paths = new ApplicationPathSet(
            Path.Combine(_root, "config"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "cache"),
            null);
        _dats = Path.Combine(_root, "dats");
        foreach (string name in DatDirectoryLocator.RequiredFileNames)
        {
            Directory.CreateDirectory(_dats);
            File.WriteAllText(Path.Combine(_dats, name), name);
        }

        _bakeExecutable = Path.Combine(_root, "bin", "acdream-bake");
        Directory.CreateDirectory(Path.GetDirectoryName(_bakeExecutable)!);
        File.WriteAllText(_bakeExecutable, "fixture");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task FilteredBakePublishesOneOverlayWithoutRewritingBase()
    {
        var recordStore = new LauncherInstallRecordStore(_paths);
        LauncherInstallRecord baseRecord = await CreateBaseRecordAsync(recordStore);
        byte[] baseBefore = await File.ReadAllBytesAsync(
            recordStore.PreparedAssetPath);
        BakeProcessRequest? observed = null;
        var runner = new FakeRunner(async (request, output, _) =>
        {
            observed = request;
            LauncherContentStateStoreTests.WritePakHeader(
                request.OutputPath,
                LauncherInstallRecordStore.CurrentBakeToolVersion);
            long bytes = new FileInfo(request.OutputPath).Length;
            output($"{{\"v\":1,\"e\":\"started\",\"bakeToolVersion\":{LauncherInstallRecordStore.CurrentBakeToolVersion}}}\n");
            output($"{{\"v\":1,\"e\":\"completed\",\"bakeToolVersion\":{LauncherInstallRecordStore.CurrentBakeToolVersion},"
                + $"\"outputBytes\":{bytes},\"failures\":0}}\n");
            await Task.Yield();
            return new BakeProcessResult(0, string.Empty);
        });
        var installer = new LauncherInstaller(
            _paths,
            _bakeExecutable,
            recordStore: recordStore,
            processRunner: runner);
        var migration = new ContentMigrationPlan(
            LauncherInstallRecordStore.CurrentBakeToolVersion - 1,
            LauncherInstallRecordStore.CurrentBakeToolVersion,
            ContentWorkKind.Overlay,
            "bounded fixture update",
            [0x01001234u, 0x02005678u],
            [0xAB]);

        LauncherInstallResult result = await installer.ApplyContentUpdateAsync(
            _dats,
            3,
            migration);

        Assert.Equal(baseBefore, await File.ReadAllBytesAsync(
            recordStore.PreparedAssetPath));
        Assert.NotNull(observed);
        Assert.Equal(
            new[] { "--ids", "0x01001234,0x02005678" },
            observed.Arguments.SkipWhile(value => value != "--ids").Take(2));
        Assert.Equal(
            new[] { "--landblocks", "0xAB" },
            observed.Arguments.SkipWhile(value => value != "--landblocks").Take(2));
        Assert.NotNull(result.Record.PreparedAssetOverlayPath);
        Assert.True(File.Exists(result.Record.PreparedAssetOverlayPath));
        Assert.Equal(
            LauncherInstallRecordStore.CurrentBakeToolVersion,
            result.Record.ResolvedBakeToolVersion);
        Assert.True(result.Record.RequiresClientCompatibilityConfirmation);
        Assert.False(File.Exists(
            new LauncherContentStateStore(_paths).OverlayCandidatePath));

        var restartedInstaller = new LauncherInstaller(
            _paths,
            _bakeExecutable,
            recordStore: recordStore,
            processRunner: runner);
        InstallRecordVerification discovered =
            await restartedInstaller.LoadExistingAsync();
        Assert.True(discovered.IsVerified);
        Assert.True(discovered.Record?.RequiresClientCompatibilityConfirmation);
        Assert.Equal(
            result.Record.PreparedAssetOverlayPath,
            discovered.Record?.PreparedAssetOverlayPath);

        restartedInstaller.ConfirmClientCompatibility();
        discovered = await restartedInstaller.LoadExistingAsync();
        Assert.True(discovered.IsVerified);
        Assert.False(discovered.Record?.RequiresClientCompatibilityConfirmation);

        string json = SessionConfigComposer.Serialize(
            SessionConfigComposer.Compose(
                new() { Name = "server", Host = "localhost", Port = 9000 },
                new() { Account = "account", Password = "secret" },
                new()
                {
                    Name = "character",
                    LaunchMode = AcDream.Launcher.Core.Profiles.LaunchMode.Gui,
                },
                discovered.Record!,
                _paths,
                "overlay-session").Document);
        Assert.Contains("preparedAssetOverlayPath", json, StringComparison.Ordinal);
        Assert.Contains("preparedAssetBaseRecipeVersion", json, StringComparison.Ordinal);
        Assert.DoesNotContain(baseRecord.PreparedAssetSha256, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationLeavesBaseRecordAndSidecarUntouched()
    {
        var recordStore = new LauncherInstallRecordStore(_paths);
        LauncherInstallRecord baseRecord = await CreateBaseRecordAsync(recordStore);
        string recordBefore = await File.ReadAllTextAsync(recordStore.RecordPath);
        byte[] baseBefore = await File.ReadAllBytesAsync(
            recordStore.PreparedAssetPath);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner(async (request, _, cancellationToken) =>
        {
            LauncherContentStateStoreTests.WritePakHeader(
                request.OutputPath,
                LauncherInstallRecordStore.CurrentBakeToolVersion);
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new BakeProcessResult(0, string.Empty);
        });
        var installer = new LauncherInstaller(
            _paths,
            _bakeExecutable,
            recordStore: recordStore,
            processRunner: runner);
        using var cancellation = new CancellationTokenSource();
        Task<LauncherInstallResult> operation = installer.ApplyContentUpdateAsync(
            _dats,
            2,
            new ContentMigrationPlan(
                LauncherInstallRecordStore.CurrentBakeToolVersion - 1,
                LauncherInstallRecordStore.CurrentBakeToolVersion,
                ContentWorkKind.Overlay,
                "bounded fixture update",
                [0x01001234u]),
            cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(baseBefore, await File.ReadAllBytesAsync(
            recordStore.PreparedAssetPath));
        Assert.Equal(recordBefore, await File.ReadAllTextAsync(recordStore.RecordPath));
        var stateStore = new LauncherContentStateStore(_paths);
        Assert.False(File.Exists(stateStore.StatePath));
        Assert.False(File.Exists(stateStore.OverlayCandidatePath));
        Assert.False(File.Exists(stateStore.ClientCompatibilityPendingPath));
        Assert.Equal(
            InstallRecordVerificationState.ContentUpdateRequired,
            (await installer.LoadExistingAsync()).State);
        Assert.Equal(baseRecord, (await recordStore.LoadAndVerifyAsync()).Record);
    }

    private async Task<LauncherInstallRecord> CreateBaseRecordAsync(
        LauncherInstallRecordStore recordStore)
    {
        LauncherContentStateStoreTests.WritePakHeader(
            recordStore.PreparedAssetPath,
            recipe: LauncherInstallRecordStore.CurrentBakeToolVersion - 1);
        LauncherInstallRecord baseRecord =
            await LauncherContentStateStoreTests.RecordAsync(
                recordStore.PreparedAssetPath,
                recipe: LauncherInstallRecordStore.CurrentBakeToolVersion - 1,
                datDirectory: Path.GetFullPath(_dats));
        Directory.CreateDirectory(_paths.DataDirectory);
        await File.WriteAllTextAsync(
            recordStore.RecordPath,
            JsonSerializer.Serialize(baseRecord, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));
        return baseRecord;
    }

    private sealed class FakeRunner(
        Func<BakeProcessRequest, Action<string>, CancellationToken, Task<BakeProcessResult>> handler)
        : IBakeProcessRunner
    {
        public Task<BakeProcessResult> RunAsync(
            BakeProcessRequest request,
            Action<string> onStandardOutput,
            CancellationToken cancellationToken = default) =>
            handler(request, onStandardOutput, cancellationToken);
    }
}
