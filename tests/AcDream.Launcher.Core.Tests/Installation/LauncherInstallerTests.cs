using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using AcDream.Launcher.Core.Integrity;
using AcDream.Launcher.Core.Installation;
using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Profiles;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Installation;

public sealed class LauncherInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-installer-tests",
        Guid.NewGuid().ToString("N"));
    private readonly ApplicationPathSet _paths;
    private readonly string _dats;
    private readonly string _bakeExecutable;

    public LauncherInstallerTests()
    {
        _paths = new ApplicationPathSet(
            Path.Combine(_root, "config"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "cache"),
            null);
        _dats = Path.Combine(_root, "retail-dats");
        _bakeExecutable = Path.Combine(_root, "bin", "acdream-bake");
        CreateCompleteDatDirectory(_dats);
        Directory.CreateDirectory(Path.GetDirectoryName(_bakeExecutable)!);
        File.WriteAllText(_bakeExecutable, "fake executable marker");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task FakeChildProgressPublishesVerifiedRecordAndFeedsExactSessionContent()
    {
        BakeProcessRequest? observedRequest = null;
        var runner = new FakeBakeProcessRunner(async (request, output, _) =>
        {
            observedRequest = request;
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
            await File.WriteAllTextAsync(request.OutputPath, "complete prepared package");
            long bytes = new FileInfo(request.OutputPath).Length;
            output("acdream-bake human header\n{\"v\":1,\"e\":\"star");
            output($"ted\",\"bakeToolVersion\":{LauncherInstallRecordStore.CurrentBakeToolVersion},\"outputPath\":\"pak\"}}\n");
            output("{\"v\":1,\"e\":\"progress\",\"phase\":\"mesh\","
                + "\"completed\":25,\"total\":100,\"failures\":0,"
                + "\"elapsedSeconds\":5,\"etaSeconds\":15}\n");
            output("{\"v\":1,\"e\":\"newMetric\",\"value\":1}\n");
            output($"{{\"v\":1,\"e\":\"completed\",\"bakeToolVersion\":{LauncherInstallRecordStore.CurrentBakeToolVersion},"
                + $"\"outputBytes\":{bytes},\"failures\":0}}\n");
            return new BakeProcessResult(0, string.Empty);
        });
        var installer = new LauncherInstaller(
            _paths,
            _bakeExecutable,
            processRunner: runner);
        var progress = new List<LauncherInstallProgress>();

        LauncherInstallResult result = await installer.InstallAsync(
            _dats,
            threads: 7,
            new ImmediateProgress<LauncherInstallProgress>(progress.Add));

        Assert.NotNull(observedRequest);
        Assert.Equal(Path.GetFullPath(_bakeExecutable), observedRequest.ExecutablePath);
        Assert.Equal(Path.GetFullPath(_dats), observedRequest.DatDirectory);
        Assert.Equal(
            LauncherInstaller.GetFullRebuildCandidatePath(
                Path.Combine(_paths.DataDirectory, "pak", "acdream.pak")),
            observedRequest.OutputPath);
        Assert.Equal(
            [
                "--dat-dir", Path.GetFullPath(_dats),
                "--out", LauncherInstaller.GetFullRebuildCandidatePath(
                    Path.Combine(_paths.DataDirectory, "pak", "acdream.pak")),
                "--threads", "7",
                "--progress-json",
            ],
            observedRequest.Arguments);
        Assert.True(BakePublicationGuardPaths.IsValidNonce(
            observedRequest.PublicationNonce));
        Assert.DoesNotContain(
            observedRequest.PublicationNonce!,
            observedRequest.Arguments);
        Assert.Equal(LauncherInstallRecordStore.CurrentBakeToolVersion,
            result.Record.BakeToolVersion);
        Assert.Equal(new FileInfo(result.Record.PreparedAssetPath).Length,
            result.Record.PreparedAssetSize);
        Assert.Equal(
            await FileIntegrity.ComputeSha256HexAsync(result.Record.PreparedAssetPath),
            result.Record.PreparedAssetSha256);
        Assert.Contains(progress, value => value.Phase == LauncherInstallPhase.BakingMeshes);
        Assert.Equal(LauncherInstallPhase.Completed, progress[^1].Phase);
        Assert.False(File.Exists(
            BakePublicationGuardPaths.GetAuthorizationPath(
                result.Record.PreparedAssetPath)));

        var store = new LauncherInstallRecordStore(_paths);
        InstallRecordVerification verification = await store.LoadAndVerifyAsync();
        Assert.True(verification.IsVerified);
        Assert.Equal(result.Record, verification.Record);

        var server = new ServerProfile
        {
            Name = "Local ACE",
            Host = "127.0.0.1",
            Port = 9000,
        };
        var account = new AccountProfile
        {
            Account = "testaccount",
            Password = "credential-never-serialized",
        };
        var character = new CharacterProfile
        {
            Name = "+Acdream",
            Id = "0x5000000A",
            LaunchMode = LaunchMode.Gui,
        };
        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            server,
            account,
            character,
            result.Record,
            _paths,
            "installed-session");
        JsonObject content = JsonNode.Parse(
                SessionConfigComposer.Serialize(composed.Document))!
            ["process"]!["content"]!.AsObject();
        Assert.Equal(result.Record.DatDirectory, (string?)content["datDirectory"]);
        Assert.Equal(
            result.Record.PreparedAssetPath,
            (string?)content["preparedAssetPath"]);
        Assert.DoesNotContain(
            result.Record.PreparedAssetSha256,
            SessionConfigComposer.Serialize(composed.Document),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InsufficientFreeSpaceStopsBeforeBakeAndReportsExactRequirement()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            bool childStarted = false;
            var runner = new FakeBakeProcessRunner((_, _, _) =>
            {
                childStarted = true;
                return Task.FromResult(new BakeProcessResult(0, string.Empty));
            });
            var installer = new LauncherInstaller(
                _paths,
                _bakeExecutable,
                processRunner: runner,
                availableFreeSpace: _ =>
                    LauncherInstaller.FullRebuildRequiredFreeBytes - 1);

            LauncherInstallException error = await Assert.ThrowsAsync<LauncherInstallException>(
                () => installer.InstallAsync(_dats, threads: 2));

            Assert.False(childStarted);
            Assert.Contains("2.0 GiB", error.Message, StringComparison.Ordinal);
            Assert.Contains("active package", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(
                LauncherInstaller.GetFullRebuildCandidatePath(
                    Path.Combine(_paths.DataDirectory, "pak", "acdream.pak"))));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task FullContentMigrationPersistsClientGateAcrossLauncherRestart()
    {
        var runner = new FakeBakeProcessRunner(async (request, output, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
            await File.WriteAllTextAsync(request.OutputPath, "replacement package");
            long bytes = new FileInfo(request.OutputPath).Length;
            output($"{{\"v\":1,\"e\":\"started\",\"bakeToolVersion\":"
                + $"{LauncherInstallRecordStore.CurrentBakeToolVersion}}}\n");
            output($"{{\"v\":1,\"e\":\"completed\",\"bakeToolVersion\":"
                + $"{LauncherInstallRecordStore.CurrentBakeToolVersion},"
                + $"\"outputBytes\":{bytes},\"failures\":0}}\n");
            return new BakeProcessResult(0, string.Empty);
        });
        var installer = new LauncherInstaller(
            _paths,
            _bakeExecutable,
            processRunner: runner);
        var migration = new ContentMigrationPlan(
            LauncherInstallRecordStore.CurrentBakeToolVersion - 1,
            LauncherInstallRecordStore.CurrentBakeToolVersion,
            ContentWorkKind.FullRebuild,
            "fixture full migration");

        LauncherInstallResult result = await installer.ApplyContentUpdateAsync(
            _dats,
            2,
            migration);

        var stateStore = new LauncherContentStateStore(_paths);
        Assert.True(result.Record.RequiresClientCompatibilityConfirmation);
        Assert.True(File.Exists(stateStore.ClientCompatibilityPendingPath));

        var restarted = new LauncherInstaller(
            _paths,
            _bakeExecutable,
            processRunner: runner);
        InstallRecordVerification discovered = await restarted.LoadExistingAsync();
        Assert.True(discovered.IsVerified);
        Assert.True(discovered.Record!.RequiresClientCompatibilityConfirmation);

        restarted.ConfirmClientCompatibility();
        discovered = await restarted.LoadExistingAsync();
        Assert.False(discovered.Record!.RequiresClientCompatibilityConfirmation);
        Assert.False(File.Exists(stateStore.ClientCompatibilityPendingPath));
    }

    [Fact]
    public async Task FailedChildRestoresPriorVerifiedPakAndRecord()
    {
        (LauncherInstaller installer, LauncherInstallRecordStore store, LauncherInstallRecord old) =
            await CreateInstallerWithPriorRecordAsync(
                async (request, output, _) =>
                {
                    await File.WriteAllTextAsync(request.OutputPath, "partial replacement");
                    output($"{{\"v\":1,\"e\":\"started\",\"bakeToolVersion\":{LauncherInstallRecordStore.CurrentBakeToolVersion}}}\n");
                    output("{\"v\":1,\"e\":\"error\",\"message\":\"fixture failed\"}\n");
                    return new BakeProcessResult(9, "human failure detail");
                },
                loadExisting: false);
        string recordBefore = await File.ReadAllTextAsync(store.RecordPath);
        var progress = new List<LauncherInstallProgress>();

        LauncherInstallException exception = await Assert.ThrowsAsync<LauncherInstallException>(
            () => installer.InstallAsync(
                _dats,
                2,
                new ImmediateProgress<LauncherInstallProgress>(progress.Add)));

        Assert.Contains("fixture failed", exception.Message, StringComparison.Ordinal);
        Assert.Equal("previous verified package", await File.ReadAllTextAsync(
            store.PreparedAssetPath));
        Assert.Equal(recordBefore, await File.ReadAllTextAsync(store.RecordPath));
        InstallRecordVerification verification = await store.LoadAndVerifyAsync();
        Assert.True(verification.IsVerified);
        Assert.Equal(old, verification.Record);
        Assert.Equal(LauncherInstallPhase.Failed, progress[^1].Phase);
    }

    [Fact]
    public async Task ContradictoryTerminalCannotReplaceFirstFailureOrPriorInstall()
    {
        (LauncherInstaller installer, LauncherInstallRecordStore store, LauncherInstallRecord old) =
            await CreateInstallerWithPriorRecordAsync(
                async (request, output, _) =>
                {
                    await File.WriteAllTextAsync(request.OutputPath, "contradictory output");
                    long bytes = new FileInfo(request.OutputPath).Length;
                    output($"{{\"v\":1,\"e\":\"started\",\"bakeToolVersion\":{LauncherInstallRecordStore.CurrentBakeToolVersion}}}\n");
                    output("{\"v\":1,\"e\":\"error\",\"message\":\"first failure\"}\n");
                    output($"{{\"v\":1,\"e\":\"completed\",\"bakeToolVersion\":{LauncherInstallRecordStore.CurrentBakeToolVersion},"
                        + $"\"outputBytes\":{bytes},\"failures\":0}}\n");
                    return new BakeProcessResult(0, string.Empty);
                });

        LauncherInstallException exception =
            await Assert.ThrowsAsync<LauncherInstallException>(
                () => installer.InstallAsync(_dats, 2));

        Assert.Contains("after", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "previous verified package",
            await File.ReadAllTextAsync(store.PreparedAssetPath));
        Assert.Equal(old, (await store.LoadAndVerifyAsync()).Record);
    }

    [Fact]
    public async Task CancellationRestoresPriorInstallAndNeverPublishesPartialOutput()
    {
        var enteredChild = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        (LauncherInstaller installer, LauncherInstallRecordStore store, LauncherInstallRecord old) =
            await CreateInstallerWithPriorRecordAsync(
                async (request, _, cancellationToken) =>
                {
                    await File.WriteAllTextAsync(
                        request.OutputPath,
                        "partial replacement",
                        cancellationToken);
                    enteredChild.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return new BakeProcessResult(0, string.Empty);
                });
        var progress = new List<LauncherInstallProgress>();
        using var cancellation = new CancellationTokenSource();

        Task<LauncherInstallResult> operation = installer.InstallAsync(
            _dats,
            3,
            new ImmediateProgress<LauncherInstallProgress>(progress.Add),
            cancellation.Token);
        await enteredChild.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal("previous verified package", await File.ReadAllTextAsync(
            store.PreparedAssetPath));
        Assert.False(File.Exists(LauncherInstallRecordStore.GetBackupPath(
            store.PreparedAssetPath)));
        InstallRecordVerification verification = await store.LoadAndVerifyAsync();
        Assert.True(verification.IsVerified);
        Assert.Equal(old, verification.Record);
        Assert.Equal(LauncherInstallPhase.Cancelled, progress[^1].Phase);
    }

    [Fact]
    public async Task FailedFirstInstallRemovesPartialPakAndCreatesNoRecord()
    {
        var runner = new FakeBakeProcessRunner(async (request, _, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
            await File.WriteAllTextAsync(request.OutputPath, "partial");
            return new BakeProcessResult(1, "failed");
        });
        var installer = new LauncherInstaller(
            _paths,
            _bakeExecutable,
            processRunner: runner);
        var store = new LauncherInstallRecordStore(_paths);

        await Assert.ThrowsAsync<LauncherInstallException>(
            () => installer.InstallAsync(_dats, 1));

        Assert.False(File.Exists(store.PreparedAssetPath));
        Assert.False(File.Exists(store.RecordPath));
    }

    [Fact]
    public async Task CancellationDuringHashRemovesUnrecordedPublishedPackage()
    {
        var hashEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeBakeProcessRunner(async (request, output, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
            await File.WriteAllTextAsync(request.OutputPath, "complete but unverified");
            long bytes = new FileInfo(request.OutputPath).Length;
            output($"{{\"v\":1,\"e\":\"started\",\"bakeToolVersion\":{LauncherInstallRecordStore.CurrentBakeToolVersion}}}\n");
            output($"{{\"v\":1,\"e\":\"completed\",\"bakeToolVersion\":{LauncherInstallRecordStore.CurrentBakeToolVersion},"
                + $"\"outputBytes\":{bytes},\"failures\":0}}\n");
            return new BakeProcessResult(0, string.Empty);
        });
        var store = new LauncherInstallRecordStore(_paths);
        var installer = new LauncherInstaller(
            _paths,
            _bakeExecutable,
            recordStore: store,
            processRunner: runner,
            computeSha256: async (_, cancellationToken) =>
            {
                hashEntered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new string('a', 64);
            });
        using var cancellation = new CancellationTokenSource();

        Task<LauncherInstallResult> operation = installer.InstallAsync(
            _dats,
            2,
            cancellationToken: cancellation.Token);
        await hashEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(File.Exists(store.PreparedAssetPath));
        Assert.False(File.Exists(store.RecordPath));
    }

    [Fact]
    public async Task IndependentInstallersSerializeAndWaitingCancellationTouchesNothing()
    {
        var storeA = new LauncherInstallRecordStore(_paths);
        LauncherInstallRecord old = await CreatePriorRecordAsync(storeA);
        string backupPath = LauncherInstallRecordStore.GetBackupPath(
            storeA.PreparedAssetPath);
        var childEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runnerA = new FakeBakeProcessRunner(async (request, _, _) =>
        {
            await File.WriteAllTextAsync(request.OutputPath, "installer A in progress");
            childEntered.SetResult();
            await releaseChild.Task;
            return new BakeProcessResult(1, "fixture A failed");
        });
        bool runnerBEntered = false;
        var runnerB = new FakeBakeProcessRunner((_, _, _) =>
        {
            runnerBEntered = true;
            return Task.FromResult(new BakeProcessResult(1, "must not run"));
        });
        var installerA = new LauncherInstaller(
            _paths,
            _bakeExecutable,
            recordStore: storeA,
            processRunner: runnerA);
        var installerB = new LauncherInstaller(
            _paths,
            _bakeExecutable,
            recordStore: new LauncherInstallRecordStore(_paths),
            processRunner: runnerB);
        var leaseContended = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        installerB.TransactionLeaseContentionObservedForTest =
            () => leaseContended.TrySetResult();

        Task<LauncherInstallResult> operationA =
            installerA.InstallAsync(_dats, 1);
        await childEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var cancellationB = new CancellationTokenSource();
            Task<LauncherInstallResult> operationB = installerB.InstallAsync(
                _dats,
                1,
                cancellationToken: cancellationB.Token);
            await leaseContended.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(operationB.IsCompleted);
            cancellationB.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operationB);
            Assert.False(runnerBEntered);
            Assert.Equal(
                "previous verified package",
                await File.ReadAllTextAsync(storeA.PreparedAssetPath));
            Assert.False(File.Exists(backupPath));
            Assert.Equal(
                "installer A in progress",
                await File.ReadAllTextAsync(
                    LauncherInstaller.GetFullRebuildCandidatePath(
                        storeA.PreparedAssetPath)));
        }
        finally
        {
            releaseChild.TrySetResult();
        }

        await Assert.ThrowsAsync<LauncherInstallException>(() => operationA);
        Assert.Equal(
            "previous verified package",
            await File.ReadAllTextAsync(storeA.PreparedAssetPath));
        Assert.False(File.Exists(backupPath));
        Assert.Equal(old, (await storeA.LoadAndVerifyAsync()).Record);
    }

    [Fact]
    public void StagingCleanupDeletesOnlyExactBakeTransactionNames()
    {
        var store = new LauncherInstallRecordStore(_paths);
        string outputPath = store.PreparedAssetPath;
        string directory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(directory);
        string owned = BakeOutputStagingContract.CreateStagingPath(
            outputPath,
            Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"));
        string canonical = outputPath;
        string backup = LauncherInstallRecordStore.GetBackupPath(outputPath);
        string oldPattern = Path.Combine(
            directory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        string invalidTransaction = Path.Combine(
            directory,
            $".{Path.GetFileName(outputPath)}.acdream-bake.not-a-guid.tmp");
        string unrelated = Path.Combine(directory, "unrelated.tmp");
        Assert.Equal(
            Path.Combine(
                directory,
                ".acdream.pak.acdream-bake.0123456789abcdef0123456789abcdef.tmp"),
            owned);
        foreach (string path in new[]
                 {
                     owned,
                     canonical,
                     backup,
                     oldPattern,
                     invalidTransaction,
                     unrelated,
                 })
        {
            File.WriteAllText(path, Path.GetFileName(path));
        }

        BakeOutputStagingContract.DeleteOwnedStagingFiles(outputPath);

        Assert.False(File.Exists(owned));
        Assert.True(File.Exists(canonical));
        Assert.True(File.Exists(backup));
        Assert.True(File.Exists(oldPattern));
        Assert.True(File.Exists(invalidTransaction));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task KilledProcessReleasesLeaseAndRestartReclaimsOnlyBakeStaging()
    {
        var store = new LauncherInstallRecordStore(_paths);
        LauncherInstallRecord old = await CreatePriorRecordAsync(store);
        string staging = BakeOutputStagingContract.CreateStagingPath(
            store.PreparedAssetPath,
            Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210"));
        string ready = Path.Combine(_root, "fixture-ready");
        string fixtureDll = GetInstallLeaseFixturePath();
        Assert.True(File.Exists(fixtureDll), $"Missing fixture: {fixtureDll}");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(fixtureDll);
        startInfo.ArgumentList.Add("hold-install-lease");
        startInfo.ArgumentList.Add(
            InstallerTransactionLease.GetLockPath(store.DataDirectory));
        startInfo.ArgumentList.Add(staging);
        startInfo.ArgumentList.Add(ready);
        using Process helper = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start lease fixture.");
        try
        {
            await WaitForFileAsync(ready, helper, TimeSpan.FromSeconds(10));
            Assert.True(File.Exists(staging));

            var blockedInstaller = new LauncherInstaller(
                _paths,
                _bakeExecutable,
                recordStore: new LauncherInstallRecordStore(_paths));
            using var blockedCancellation = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => blockedInstaller.LoadExistingAsync(blockedCancellation.Token));
            Assert.True(File.Exists(staging));
        }
        finally
        {
            if (!helper.HasExited)
            {
                helper.Kill(entireProcessTree: true);
            }

            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        var restarted = new LauncherInstaller(
            _paths,
            _bakeExecutable,
            recordStore: new LauncherInstallRecordStore(_paths));
        InstallRecordVerification recovered = await restarted.LoadExistingAsync();

        Assert.True(recovered.IsVerified);
        Assert.Equal(old, recovered.Record);
        Assert.False(File.Exists(staging));
        Assert.Equal(
            "previous verified package",
            await File.ReadAllTextAsync(store.PreparedAssetPath));
    }

    [Theory]
    [InlineData("holds", 0)]
    [InlineData("late", 17)]
    // Lane=Timing: outcome depends on real elapsed time or OS scheduling.
    // Orphaned-process restart recovery; outcome depends on process scheduling.
    // Observed failing on the Windows runner in CI run 162.
    [Trait("Lane", "Timing")]
    public async Task OrphanBakeCanNeverPublishAfterRestartRecovery(
        string schedule,
        int expectedChildExitCode)
    {
        var store = new LauncherInstallRecordStore(_paths);
        LauncherInstallRecord old = await CreatePriorRecordAsync(store);
        string recordBefore = await File.ReadAllTextAsync(store.RecordPath);
        string control = Path.Combine(_root, "orphan-" + schedule);
        Directory.CreateDirectory(control);
        string ready = Path.Combine(control, "child-ready");
        string release = Path.Combine(control, "child-release");
        string childPid = Path.Combine(control, "child-pid");
        string childExit = Path.Combine(control, "child-exit");
        string fixtureDll = GetInstallLeaseFixturePath();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[]
                 {
                     fixtureDll,
                     "orphan-parent",
                     store.DataDirectory,
                     _dats,
                     _bakeExecutable,
                     schedule,
                     ready,
                     release,
                     childPid,
                     childExit,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process parent = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start orphan parent.");
        int orphanPid = 0;
        try
        {
            await WaitForFileAsync(ready, parent, TimeSpan.FromSeconds(15));
            orphanPid = int.Parse(
                await File.ReadAllTextAsync(childPid),
                System.Globalization.CultureInfo.InvariantCulture);
            using Process orphan = Process.GetProcessById(orphanPid);
            Assert.False(File.Exists(
                LauncherInstallRecordStore.GetBackupPath(
                    store.PreparedAssetPath)));
            string candidatePath = LauncherInstaller.GetFullRebuildCandidatePath(
                store.PreparedAssetPath);
            Assert.True(File.Exists(
                BakePublicationGuardPaths.GetAuthorizationPath(
                    candidatePath)));

            parent.Kill(entireProcessTree: false);
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            var restarted = new LauncherInstaller(
                _paths,
                _bakeExecutable,
                recordStore: new LauncherInstallRecordStore(_paths));
            var publicationContended = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            restarted.PublicationLeaseContentionObservedForTest =
                () => publicationContended.TrySetResult();
            Task<InstallRecordVerification> recovery =
                restarted.LoadExistingAsync();
            InstallRecordVerification recovered;
            if (schedule == "holds")
            {
                await publicationContended.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(recovery.IsCompleted);
                File.WriteAllText(release, "release");
                recovered = await recovery.WaitAsync(TimeSpan.FromSeconds(15));
            }
            else
            {
                recovered = await recovery.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.False(File.Exists(
                    BakePublicationGuardPaths.GetAuthorizationPath(
                        candidatePath)));
                File.WriteAllText(release, "release");
            }

            Assert.True(recovered.IsVerified);
            Assert.Equal(old, recovered.Record);
            string canonicalAfterRecovery =
                await File.ReadAllTextAsync(store.PreparedAssetPath);
            string recordAfterRecovery =
                await File.ReadAllTextAsync(store.RecordPath);
            bool backupAfterRecovery = File.Exists(
                LauncherInstallRecordStore.GetBackupPath(
                    store.PreparedAssetPath));

            await WaitForFileAsync(childExit, TimeSpan.FromSeconds(15));
            Assert.Equal(
                expectedChildExitCode,
                int.Parse(
                    await File.ReadAllTextAsync(childExit),
                    System.Globalization.CultureInfo.InvariantCulture));
            if (schedule == "late")
            {
                Assert.Contains(
                    "no longer authorized",
                    await File.ReadAllTextAsync(childExit + ".error"),
                    StringComparison.OrdinalIgnoreCase);
            }
            await orphan.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(
                canonicalAfterRecovery,
                await File.ReadAllTextAsync(store.PreparedAssetPath));
            Assert.Equal("previous verified package", canonicalAfterRecovery);
            Assert.Equal(recordBefore, recordAfterRecovery);
            Assert.Equal(recordAfterRecovery, await File.ReadAllTextAsync(store.RecordPath));
            Assert.Equal(
                backupAfterRecovery,
                File.Exists(LauncherInstallRecordStore.GetBackupPath(
                    store.PreparedAssetPath)));
            Assert.False(backupAfterRecovery);
            Assert.False(File.Exists(
                BakePublicationGuardPaths.GetAuthorizationPath(
                    candidatePath)));
        }
        finally
        {
            File.WriteAllText(release, "release");
            if (!parent.HasExited)
            {
                parent.Kill(entireProcessTree: false);
                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            if (orphanPid != 0 && !File.Exists(childExit))
            {
                TryKill(orphanPid);
            }
        }
    }

    private async Task<(
        LauncherInstaller Installer,
        LauncherInstallRecordStore Store,
        LauncherInstallRecord Old)> CreateInstallerWithPriorRecordAsync(
        Func<BakeProcessRequest, Action<string>, CancellationToken, Task<BakeProcessResult>> handler,
        bool loadExisting = true)
    {
        var store = new LauncherInstallRecordStore(_paths);
        Directory.CreateDirectory(Path.GetDirectoryName(store.PreparedAssetPath)!);
        await File.WriteAllTextAsync(
            store.PreparedAssetPath,
            "previous verified package");
        var old = new LauncherInstallRecord(
            Path.GetFullPath(_dats),
            Path.GetFullPath(store.PreparedAssetPath),
            await FileIntegrity.ComputeSha256HexAsync(store.PreparedAssetPath),
            new FileInfo(store.PreparedAssetPath).Length,
            LauncherInstallRecordStore.CurrentBakeToolVersion);
        await store.SaveAtomicallyAsync(old);

        var installer = new LauncherInstaller(
            _paths,
            _bakeExecutable,
            recordStore: store,
            processRunner: new FakeBakeProcessRunner(handler));
        if (loadExisting)
        {
            Assert.True((await installer.LoadExistingAsync()).IsVerified);
        }

        return (installer, store, old);
    }

    private async Task<LauncherInstallRecord> CreatePriorRecordAsync(
        LauncherInstallRecordStore store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(store.PreparedAssetPath)!);
        await File.WriteAllTextAsync(
            store.PreparedAssetPath,
            "previous verified package");
        var old = new LauncherInstallRecord(
            Path.GetFullPath(_dats),
            Path.GetFullPath(store.PreparedAssetPath),
            await FileIntegrity.ComputeSha256HexAsync(store.PreparedAssetPath),
            new FileInfo(store.PreparedAssetPath).Length,
            LauncherInstallRecordStore.CurrentBakeToolVersion);
        await store.SaveAtomicallyAsync(old);
        return old;
    }

    private static async Task WaitForFileAsync(
        string path,
        Process process,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Lease fixture exited with {process.ExitCode}: "
                    + await process.StandardError.ReadToEndAsync());
            }

            await Task.Delay(25, cancellation.Token);
        }
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path))
        {
            await Task.Delay(25, cancellation.Token);
        }
    }

    private static void TryKill(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch
        {
        }
    }

    private static string GetInstallLeaseFixturePath()
    {
        string root = FindRepositoryRoot();
        string configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent?.Name
            ?? "Release";
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
        foreach (string start in new[]
                 {
                     AppContext.BaseDirectory,
                     Environment.CurrentDirectory,
                 })
        {
            for (var directory = new DirectoryInfo(start);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static void CreateCompleteDatDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (string fileName in DatDirectoryLocator.RequiredFileNames)
        {
            File.WriteAllText(Path.Combine(directory, fileName), "fixture");
        }
    }

    private sealed class FakeBakeProcessRunner(
        Func<BakeProcessRequest, Action<string>, CancellationToken, Task<BakeProcessResult>> handler)
        : IBakeProcessRunner
    {
        private readonly Func<
            BakeProcessRequest,
            Action<string>,
            CancellationToken,
            Task<BakeProcessResult>> _handler = handler;

        public Task<BakeProcessResult> RunAsync(
            BakeProcessRequest request,
            Action<string> onStandardOutput,
            CancellationToken cancellationToken = default) =>
            _handler(request, onStandardOutput, cancellationToken);
    }

    private sealed class ImmediateProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
