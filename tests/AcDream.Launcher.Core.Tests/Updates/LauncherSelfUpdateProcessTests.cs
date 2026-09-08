using System.Diagnostics;
using AcDream.Launcher.Core.Integrity;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Tests.Updates;

public sealed class LauncherSelfUpdateProcessTests : IDisposable
{
    private const string FixtureBaseName =
        "AcDream.Launcher.Core.Tests.Fixtures.InstallLeaseHolder";
    private const string DataEnvironment = "ACDREAM_SELF_UPDATE_FIXTURE_DATA";
    private const string TargetEnvironment = "ACDREAM_SELF_UPDATE_FIXTURE_TARGET";
    private const string HelperPidEnvironment =
        "ACDREAM_SELF_UPDATE_FIXTURE_HELPER_PID";
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        Process,
        ProcessOutputCapture> ProcessOutput = new();

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-self-update-process-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task KilledApplyingPlanRecoversPriorAndContinuesCanonicalWithoutRetryLoop()
    {
        string data = Path.Combine(_root, "data");
        string target = Path.Combine(_root, "launcher");
        string ready = Path.Combine(_root, "crash.ready");
        string launched = Path.Combine(_root, "replacement.ready");
        string helperPidPath = Path.Combine(_root, "helper.pid");
        string[] processLocalSuffix =
        [
            "--config-dir", Path.Combine(_root, "isolated config"),
            "--data-dir", Path.Combine(_root, "isolated data"),
            "--cache-dir", Path.Combine(_root, "isolated cache"),
            "--update-manifest-uri", "http://127.0.0.1:43119/manifest.json",
        ];
        Directory.CreateDirectory(_root);
        string rid = LauncherRuntimeIdentity.DetectRid();
        PreparedLauncher prepared = PrepareLauncherClosure(target, rid);
        string oldHash = await FileIntegrity.ComputeSha256HexAsync(prepared.CanonicalPath);
        using var server = new LocalHttpFixture();
        server.Add("launcher.zip", prepared.NewArchive);
        using var http = new HttpClient();
        var manager = new LauncherSelfUpdateManager(UpdateTestData.Paths(_root), http);
        SelfUpdateStageResult staged = await manager.StageAsync(
            LauncherVersion.Parse("2.0.0"),
            rid,
            new ReleaseArtifact(
                server.UriFor("launcher.zip"),
                UpdateTestData.Sha256(prepared.NewArchive),
                prepared.NewArchive.LongLength),
            target,
            progress: null,
            CancellationToken.None);
        SelfUpdatePlan plan = Assert.IsType<SelfUpdatePlan>(await manager.LoadPendingAsync());

        using Process crash = StartFixture(
            ["crash-self-update", data, target, ready, prepared.CanonicalName]);
        try
        {
            await WaitForFileAsync(ready, crash, TimeSpan.FromSeconds(20));
            Assert.True(File.Exists(prepared.CanonicalPath));
            crash.Kill(entireProcessTree: true);
            await crash.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(File.Exists(prepared.CanonicalPath));
            string boundaryHash = await FileIntegrity.ComputeSha256HexAsync(
                prepared.CanonicalPath);
            Assert.Contains(boundaryHash, new[] { oldHash, prepared.NewCanonicalHash });

            var environment = new Dictionary<string, string>
            {
                [DataEnvironment] = data,
                [TargetEnvironment] = target,
                [HelperPidEnvironment] = helperPidPath,
            };
            using Process canonical = StartProcess(
                prepared.CanonicalPath,
                ["canonical-probe", launched, .. processLocalSuffix],
                environment);
            await canonical.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, canonical.ExitCode);
            await WaitForFileAsync(launched, process: null, TimeSpan.FromSeconds(30));
            await WaitUntilAsync(
                () => !File.Exists(manager.PendingPlanPath),
                TimeSpan.FromSeconds(30),
                "The self-update journal did not converge.");

            Assert.Equal(
                oldHash,
                await FileIntegrity.ComputeSha256HexAsync(prepared.CanonicalPath));
            Assert.False(File.Exists(Path.Combine(
                target,
                LauncherSelfUpdateManager.InstallRecordFileName)));
            Assert.False(Directory.Exists(manager.GetTransactionDirectory(
                plan.TransactionId)));
            using (UpdateSessionBarrier.ExclusiveLease cleanupLease =
                   manager.Barrier.AcquireExclusive())
            {
                Assert.True(manager.CleanupOwnedResidueUnderLease(
                    pending: null,
                    target,
                    cleanupLease));
            }
            Assert.Empty(Directory.EnumerateDirectories(
                target,
                ".acdream-self-update-*",
                SearchOption.TopDirectoryOnly));
            Assert.Null(await manager.LoadPendingAsync());

            string launchMarker = await File.ReadAllTextAsync(launched);
            Assert.EndsWith(
                Environment.NewLine + string.Join(Environment.NewLine, processLocalSuffix),
                launchMarker,
                StringComparison.Ordinal);
            int replacementPid = ParsePid(launchMarker);
            await WaitForProcessExitAsync(replacementPid, TimeSpan.FromSeconds(10));
            Assert.False(File.Exists(helperPidPath));
            if (OperatingSystem.IsLinux())
            {
                Assert.True(
                    (File.GetUnixFileMode(prepared.CanonicalPath)
                        & (UnixFileMode.UserExecute
                            | UnixFileMode.GroupExecute
                            | UnixFileMode.OtherExecute)) != 0);
            }
        }
        finally
        {
            if (!crash.HasExited)
            {
                crash.Kill(entireProcessTree: true);
                await crash.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task CorruptBackupAfterCanonicalCrashNeverLaunchesAndPreservesEvidence()
    {
        CrashedUpdate crashed = await PrepareKilledAfterCanonicalReplaceAsync();
        string backupPath = Path.Combine(
            crashed.Manager.GetTargetTransactionDirectory(crashed.Plan),
            "backup",
            crashed.Prepared.CanonicalName);
        Assert.True(File.Exists(backupPath));
        await File.WriteAllTextAsync(backupPath, "tampered rollback backup");
        string tamperedHash = await FileIntegrity.ComputeSha256HexAsync(backupPath);
        string launched = Path.Combine(_root, "corrupt-backup-launched");
        string helperPidPath = Path.Combine(_root, "corrupt-backup-helper.pid");

        using Process canonical = StartProcess(
            crashed.Prepared.CanonicalPath,
            ["canonical-probe", launched],
            BootstrapEnvironment(crashed, helperPidPath));
        await canonical.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotEqual(0, canonical.ExitCode);
        Assert.False(File.Exists(helperPidPath));

        Assert.False(File.Exists(launched));
        SelfUpdatePlan preserved = Assert.IsType<SelfUpdatePlan>(
            await crashed.Manager.LoadPendingAsync());
        Assert.Equal(SelfUpdatePlanState.Applying, preserved.State);
        Assert.Equal(crashed.Plan.TransactionId, preserved.TransactionId);
        Assert.True(Directory.Exists(
            crashed.Manager.GetTargetTransactionDirectory(crashed.Plan)));
        Assert.Equal(tamperedHash, await FileIntegrity.ComputeSha256HexAsync(backupPath));
        Assert.Equal(
            crashed.Prepared.NewCanonicalHash,
            await FileIntegrity.ComputeSha256HexAsync(crashed.Prepared.CanonicalPath));
        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            crashed.Manager.VerifyRestoredPriorAsync(crashed.Target));
    }

    [Fact]
    public async Task BackupJunctionOrSymlinkAfterCanonicalCrashCannotMutateOutsideOrLaunch()
    {
        CrashedUpdate crashed = await PrepareKilledAfterCanonicalReplaceAsync();
        string swap = crashed.Manager.GetTargetTransactionDirectory(crashed.Plan);
        string backup = Path.Combine(swap, "backup");
        string preservedBackup = Path.Combine(_root, "preserved-backup");
        string outside = Path.Combine(_root, "outside-backup");
        Directory.Move(backup, preservedBackup);
        CopyDirectory(preservedBackup, outside);
        string outsideCanonical = Path.Combine(
            outside,
            crashed.Prepared.CanonicalName);
        string outsideHash = await FileIntegrity.ComputeSha256HexAsync(outsideCanonical);
        CreateDirectoryLink(backup, outside);
        string launched = Path.Combine(_root, "reparse-backup-launched");
        string helperPidPath = Path.Combine(_root, "reparse-backup-helper.pid");

        try
        {
            using Process canonical = StartProcess(
                crashed.Prepared.CanonicalPath,
                ["canonical-probe", launched],
                BootstrapEnvironment(crashed, helperPidPath));
            await canonical.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.NotEqual(0, canonical.ExitCode);
            Assert.False(File.Exists(helperPidPath));

            Assert.False(File.Exists(launched));
            Assert.True(File.Exists(outsideCanonical));
            Assert.Equal(
                outsideHash,
                await FileIntegrity.ComputeSha256HexAsync(outsideCanonical));
            Assert.True(
                (File.GetAttributes(backup) & FileAttributes.ReparsePoint) != 0);
            SelfUpdatePlan preserved = Assert.IsType<SelfUpdatePlan>(
                await crashed.Manager.LoadPendingAsync());
            Assert.Equal(SelfUpdatePlanState.Applying, preserved.State);
            Assert.Equal(
                crashed.Prepared.NewCanonicalHash,
                await FileIntegrity.ComputeSha256HexAsync(
                    crashed.Prepared.CanonicalPath));
        }
        finally
        {
            try
            {
                if ((File.GetAttributes(backup) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(backup);
                }
            }
            catch (FileNotFoundException)
            {
                // The assertion above reports an unexpected missing link.
            }
            catch (DirectoryNotFoundException)
            {
                // The assertion above reports an unexpected missing link.
            }
        }
    }

    [Fact]
    public async Task ConcurrentStartupCannotDeleteAVisibleSlowStageTransaction()
    {
        string data = Path.Combine(_root, "data");
        string target = Path.Combine(_root, "launcher");
        string resultPath = Path.Combine(_root, "startup.result");
        Directory.CreateDirectory(target);
        string rid = LauncherRuntimeIdentity.DetectRid();
        string canonicalName = LauncherName(rid);
        string canonical = Path.Combine(target, canonicalName);
        await File.WriteAllTextAsync(canonical, "running launcher");
        byte[] archive = UpdateTestData.LauncherZip(
            rid,
            new string('s', 2 * 1024 * 1024));
        using var server = new LocalHttpFixture();
        server.Add(
            "slow-launcher.zip",
            archive,
            chunkSize: 4096,
            chunkDelay: TimeSpan.FromMilliseconds(2));
        var observer = new LauncherSelfUpdateManager(
            UpdateTestData.Paths(_root),
            new HttpClient());
        using Process staging = StartFixture(
        [
            "stage-self-update",
            data,
            target,
            "2.0.0",
            rid,
            server.UriFor("slow-launcher.zip").AbsoluteUri,
            UpdateTestData.Sha256(archive),
            archive.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ]);
        try
        {
            await WaitUntilAsync(
                () => Directory.Exists(observer.TransactionsDirectory)
                    && Directory.EnumerateDirectories(observer.TransactionsDirectory).Any(),
                TimeSpan.FromSeconds(10),
                "The slow staging transaction did not become visible.",
                staging);
            Assert.False(File.Exists(observer.PendingPlanPath));
            string transaction = Assert.Single(
                Directory.EnumerateDirectories(observer.TransactionsDirectory));

            using Process startup = StartFixture(
                ["bootstrap-probe", data, target, canonical, resultPath]);
            await startup.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotEqual(0, startup.ExitCode);
            Assert.False(File.Exists(resultPath));
            Assert.True(Directory.Exists(transaction));
            Assert.False(File.Exists(observer.PendingPlanPath));

            await staging.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, staging.ExitCode);
            SelfUpdatePlan plan = Assert.IsType<SelfUpdatePlan>(
                await observer.LoadPendingAsync());
            Assert.Equal(SelfUpdatePlanState.Staged, plan.State);
            Assert.True(Directory.Exists(transaction));
        }
        finally
        {
            if (!staging.HasExited)
            {
                staging.Kill(entireProcessTree: true);
                await staging.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task HelperDefersWithoutRestartWhenSharedSessionLeaseAppears()
    {
        string data = Path.Combine(_root, "data");
        string target = Path.Combine(_root, "launcher");
        string unexpectedLaunch = Path.Combine(_root, "unexpected-launch");
        string helperPid = Path.Combine(_root, "helper.pid");
        Directory.CreateDirectory(target);
        string rid = LauncherRuntimeIdentity.DetectRid();
        PreparedLauncher prepared = PrepareLauncherClosure(target, rid);
        string canonical = prepared.CanonicalPath;
        byte[] archive = prepared.NewArchive;
        using var server = new LocalHttpFixture();
        server.Add("launcher.zip", archive);
        using var http = new HttpClient();
        var manager = new LauncherSelfUpdateManager(UpdateTestData.Paths(_root), http);
        _ = await manager.StageAsync(
            LauncherVersion.Parse("2.0.0"),
            rid,
            new ReleaseArtifact(
                server.UriFor("launcher.zip"),
                UpdateTestData.Sha256(archive),
                archive.LongLength),
            target,
            progress: null,
            CancellationToken.None);
        SelfUpdatePlan plan = Assert.IsType<SelfUpdatePlan>(await manager.LoadPendingAsync());
        using UpdateSessionBarrier.SessionLease session = manager.Barrier.AcquireSession();
        var environment = new Dictionary<string, string>
        {
            [DataEnvironment] = data,
            [TargetEnvironment] = target,
            [HelperPidEnvironment] = helperPid,
        };

        using Process helper = StartProcess(
            manager.GetStagedLauncherPath(plan),
        [
            LauncherSelfUpdateBootstrap.HelperArgument,
            int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            target,
            plan.TransactionId,
            "canonical-probe",
            unexpectedLaunch,
        ], environment);
        await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        string helperError = await ReadStandardErrorAsync(helper);
        string helperOutput = await ReadStandardOutputAsync(helper);
        Assert.True(
            helper.ExitCode == LauncherSelfUpdateBootstrap.UpdateLeaseBusyExitCode,
            $"helper exit {helper.ExitCode}; stdout: {helperOutput}; stderr: {helperError}");
        Assert.True(File.Exists(helperPid));
        Assert.False(File.Exists(unexpectedLaunch));
        Assert.NotEqual(
            prepared.NewCanonicalHash,
            await FileIntegrity.ComputeSha256HexAsync(canonical));
        SelfUpdatePlan deferred = Assert.IsType<SelfUpdatePlan>(
            await manager.LoadPendingAsync());
        Assert.Equal(SelfUpdatePlanState.Staged, deferred.State);
        Assert.Equal(plan.TransactionId, deferred.TransactionId);
    }

    [Fact]
    public async Task SpoofedInternalPrefixesCannotBypassAnyDurablePlanState()
    {
        string data = Path.Combine(_root, "data");
        string target = Path.Combine(_root, "launcher");
        string rid = LauncherRuntimeIdentity.DetectRid();
        PreparedLauncher prepared = PrepareLauncherClosure(target, rid);
        using var server = new LocalHttpFixture();
        server.Add("launcher.zip", prepared.NewArchive);
        using var http = new HttpClient();
        var manager = new LauncherSelfUpdateManager(UpdateTestData.Paths(_root), http);
        _ = await manager.StageAsync(
            LauncherVersion.Parse("2.0.0"),
            rid,
            new ReleaseArtifact(
                server.UriFor("launcher.zip"),
                UpdateTestData.Sha256(prepared.NewArchive),
                prepared.NewArchive.LongLength),
            target,
            progress: null,
            CancellationToken.None);
        SelfUpdatePlan plan = Assert.IsType<SelfUpdatePlan>(await manager.LoadPendingAsync());
        var environment = new Dictionary<string, string>
        {
            [DataEnvironment] = data,
            [TargetEnvironment] = target,
        };

        await AssertInternalSpoofsRejectedAsync(
            prepared.CanonicalPath,
            target,
            plan.TransactionId,
            environment,
            "staged");

        plan = await manager.ApplyPendingAsync(target);
        await SetPlanStateAsync(manager.PendingPlanPath, "applying");
        await AssertInternalSpoofsRejectedAsync(
            prepared.CanonicalPath,
            target,
            plan.TransactionId,
            environment,
            "applying");

        plan = await manager.RecoverApplyingAsync(target);
        plan = await manager.ApplyPendingAsync(target);
        Assert.Equal(SelfUpdatePlanState.AwaitingConfirmation, plan.State);
        await AssertInternalSpoofsRejectedAsync(
            prepared.CanonicalPath,
            target,
            plan.TransactionId,
            environment,
            "awaitingConfirmation");

        plan = await manager.RollbackAwaitingConfirmationAsync(target);
        await AssertInternalSpoofsRejectedAsync(
            prepared.CanonicalPath,
            target,
            plan.TransactionId,
            environment,
            "rolledBack");

        await File.WriteAllTextAsync(manager.PendingPlanPath, "{ambiguous");
        await AssertInternalSpoofsRejectedAsync(
            prepared.CanonicalPath,
            target,
            plan.TransactionId,
            environment,
            "ambiguous");
    }

    private static async Task AssertInternalSpoofsRejectedAsync(
        string canonicalPath,
        string targetDirectory,
        string transactionId,
        IReadOnlyDictionary<string, string> environment,
        string state)
    {
        (string Name, string[] Arguments, int? ExactExit)[] attempts =
        [
            (
                "deferred",
                ["--acdream-self-update-deferred-v1"],
                64),
            (
                "helper",
                [
                    LauncherSelfUpdateBootstrap.HelperArgument,
                    int.MaxValue.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    targetDirectory,
                    transactionId,
                ],
                null),
            (
                "confirm",
                [LauncherSelfUpdateBootstrap.ConfirmArgument, transactionId],
                null),
        ];

        foreach ((string name, string[] arguments, int? exactExit) in attempts)
        {
            using Process process = StartProcess(canonicalPath, arguments, environment);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            string stderr = await ReadStandardErrorAsync(process);
            if (exactExit.HasValue)
            {
                Assert.True(
                    process.ExitCode == exactExit.Value,
                    $"{state}/{name} exited {process.ExitCode}: {stderr}");
            }
            else
            {
                Assert.True(
                    process.ExitCode != 0,
                    $"{state}/{name} unexpectedly succeeded.");
            }
        }
    }

    private static async Task SetPlanStateAsync(string path, string state)
    {
        System.Text.Json.Nodes.JsonObject plan = Assert.IsType<
            System.Text.Json.Nodes.JsonObject>(
            System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path)));
        plan["state"] = state;
        await File.WriteAllTextAsync(path, plan.ToJsonString());
    }

    private PreparedLauncher PrepareLauncherClosure(string target, string rid)
    {
        string fixtureDirectory = GetFixtureDirectory();
        string fixtureAppHost = Path.Combine(
            fixtureDirectory,
            FixtureBaseName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        Assert.True(File.Exists(fixtureAppHost), $"Missing fixture apphost: {fixtureAppHost}");
        Directory.CreateDirectory(target);
        string canonicalName = LauncherName(rid);
        string canonicalPath = Path.Combine(target, canonicalName);
        var archiveEntries = new List<(string Name, byte[] Content, int? UnixAttributes)>();
        foreach (string source in Directory.EnumerateFiles(
                     fixtureDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            string sourceName = Path.GetFileName(source);
            bool isAppHost = PathsEqual(source, fixtureAppHost);
            string targetName = isAppHost ? canonicalName : sourceName;
            byte[] oldContent = File.ReadAllBytes(source);
            byte[] newContent = oldContent;
            if (isAppHost)
            {
                oldContent = [.. oldContent, .. "-old"u8.ToArray()];
                newContent = [.. newContent, .. "-new"u8.ToArray()];
            }

            string targetPath = Path.Combine(target, targetName);
            File.WriteAllBytes(targetPath, oldContent);
            int unixAttributes = 0x81A4;
            if (OperatingSystem.IsLinux())
            {
                UnixFileMode mode = File.GetUnixFileMode(source);
                if (isAppHost)
                {
                    mode |= UnixFileMode.UserExecute;
                }

                File.SetUnixFileMode(targetPath, mode);
                unixAttributes = 0x8000 | (int)mode;
            }
            else if (isAppHost)
            {
                unixAttributes = 0x81ED;
            }

            archiveEntries.Add((targetName, newContent, unixAttributes));
        }

        byte[] archive = UpdateTestData.CreateZip(archiveEntries);
        byte[] newCanonical = Assert.Single(
            archiveEntries,
            entry => entry.Name == canonicalName).Content;
        return new PreparedLauncher(
            canonicalName,
            canonicalPath,
            archive,
            UpdateTestData.Sha256(newCanonical));
    }

    private async Task<CrashedUpdate> PrepareKilledAfterCanonicalReplaceAsync()
    {
        string data = Path.Combine(_root, "data");
        string target = Path.Combine(_root, "launcher");
        string ready = Path.Combine(_root, "crash.ready");
        Directory.CreateDirectory(_root);
        string rid = LauncherRuntimeIdentity.DetectRid();
        PreparedLauncher prepared = PrepareLauncherClosure(target, rid);
        using var server = new LocalHttpFixture();
        server.Add("launcher.zip", prepared.NewArchive);
        using var http = new HttpClient();
        var manager = new LauncherSelfUpdateManager(UpdateTestData.Paths(_root), http);
        _ = await manager.StageAsync(
            LauncherVersion.Parse("2.0.0"),
            rid,
            new ReleaseArtifact(
                server.UriFor("launcher.zip"),
                UpdateTestData.Sha256(prepared.NewArchive),
                prepared.NewArchive.LongLength),
            target,
            progress: null,
            CancellationToken.None);
        SelfUpdatePlan plan = Assert.IsType<SelfUpdatePlan>(await manager.LoadPendingAsync());
        using Process crash = StartFixture(
            ["crash-self-update", data, target, ready, prepared.CanonicalName]);
        await WaitForFileAsync(ready, crash, TimeSpan.FromSeconds(20));
        crash.Kill(entireProcessTree: true);
        await crash.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SelfUpdatePlanState.Applying,
            Assert.IsType<SelfUpdatePlan>(await manager.LoadPendingAsync()).State);
        return new CrashedUpdate(data, target, manager, plan, prepared);
    }

    private static Dictionary<string, string> BootstrapEnvironment(
        CrashedUpdate crashed,
        string helperPidPath) => new()
    {
        [DataEnvironment] = crashed.Data,
        [TargetEnvironment] = crashed.Target,
        [HelperPidEnvironment] = helperPidPath,
    };

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(file));
            }
        }
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not create the test junction.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Could not create the test junction: "
                + process.StandardError.ReadToEnd()
                + process.StandardOutput.ReadToEnd());
        }
    }

    private static Process StartFixture(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null) =>
        StartProcess("dotnet", [GetFixtureDllPath(), .. arguments], environment);

    private static Process StartProcess(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach ((string name, string value) in environment)
            {
                start.Environment[name] = value;
            }
        }

        Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start '{executable}'.");
        ProcessOutput.Add(
            process,
            new ProcessOutputCapture(
                process.StandardOutput.ReadToEndAsync(),
                process.StandardError.ReadToEndAsync()));
        return process;
    }

    private static Task<string> ReadStandardOutputAsync(Process process) =>
        ProcessOutput.TryGetValue(process, out ProcessOutputCapture? capture)
            ? capture.StandardOutput
            : process.StandardOutput.ReadToEndAsync();

    private static Task<string> ReadStandardErrorAsync(Process process) =>
        ProcessOutput.TryGetValue(process, out ProcessOutputCapture? capture)
            ? capture.StandardError
            : process.StandardError.ReadToEndAsync();

    private static async Task WaitForFileAsync(
        string path,
        Process? process,
        TimeSpan timeout) =>
        await WaitUntilAsync(
            () => File.Exists(path),
            timeout,
            $"Timed out waiting for '{path}'.",
            process);

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string failure,
        Process? process = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (process?.HasExited == true)
            {
                throw new InvalidOperationException(
                    $"{failure} Process exited {process.ExitCode}. stdout: "
                    + await ReadStandardOutputAsync(process)
                    + " stderr: "
                    + await ReadStandardErrorAsync(process));
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(failure);
            }

            await Task.Delay(20);
        }
    }

    private static int ParsePid(string marker)
    {
        string value = marker.Split('|', 2)[0];
        return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task WaitForProcessExitAsync(int pid, TimeSpan timeout)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (ArgumentException)
        {
            // It exited before the test opened the process handle.
        }
    }

    private static string LauncherName(string rid) =>
        "acdream-launcher"
        + (rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty);

    private static string GetFixtureDllPath() =>
        Path.Combine(GetFixtureDirectory(), FixtureBaseName + ".dll");

    private sealed record ProcessOutputCapture(
        Task<string> StandardOutput,
        Task<string> StandardError);

    private static string GetFixtureDirectory()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent?.Name ?? "Release";
        return Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "AcDream.Launcher.Core.Tests.Fixtures.InstallLeaseHolder",
            "bin",
            configuration,
            "net10.0");
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

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private sealed record PreparedLauncher(
        string CanonicalName,
        string CanonicalPath,
        byte[] NewArchive,
        string NewCanonicalHash);

    private sealed record CrashedUpdate(
        string Data,
        string Target,
        LauncherSelfUpdateManager Manager,
        SelfUpdatePlan Plan,
        PreparedLauncher Prepared);
}
