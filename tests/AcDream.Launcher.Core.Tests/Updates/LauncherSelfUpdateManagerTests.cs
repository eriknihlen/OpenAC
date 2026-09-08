using AcDream.Launcher.Core.Updates;
using System.Text.Json.Nodes;

namespace AcDream.Launcher.Core.Tests.Updates;

public sealed class LauncherSelfUpdateManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-self-update-tests",
        "target & $(literal)-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task StartupWithoutPlanReclaimsOnlyExactOwnedResidue()
    {
        using var harness = new Harness(_root);
        string orphan = harness.Manager.GetTransactionDirectory(Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        await File.WriteAllTextAsync(Path.Combine(orphan, "partial"), "partial");
        Directory.CreateDirectory(harness.Manager.RootDirectory);
        string temporary = Path.Combine(
            harness.Manager.RootDirectory,
            $".pending.json.{Guid.NewGuid():N}.tmp");
        string unrelated = Path.Combine(harness.Manager.RootDirectory, "pending.user.tmp");
        await File.WriteAllTextAsync(temporary, "partial");
        await File.WriteAllTextAsync(unrelated, "preserve");

        Assert.Null(await harness.Manager.LoadPendingAsync());
        using UpdateSessionBarrier.ExclusiveLease lease =
            harness.Manager.Barrier.AcquireExclusive();
        Assert.True(harness.Manager.CleanupOwnedResidueUnderLease(
            pending: null,
            harness.Target,
            lease));

        Assert.False(Directory.Exists(orphan));
        Assert.False(File.Exists(temporary));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task VerifiedStageIsDurableAndDoesNotTouchRunningTarget()
    {
        using var harness = new Harness(_root);

        SelfUpdateStageResult result = await harness.StageAsync();
        SelfUpdatePlan plan = Assert.IsType<SelfUpdatePlan>(
            await harness.Manager.LoadPendingAsync());

        Assert.Equal("2.0.0", result.Version.Value);
        Assert.Equal(SelfUpdatePlanState.Staged, plan.State);
        Assert.Null(plan.Apply);
        Assert.Equal("old-launcher", await File.ReadAllTextAsync(harness.LauncherPath));
        Assert.Equal("new-launcher", await File.ReadAllTextAsync(
            Path.Combine(harness.Manager.GetPayloadDirectory(plan.TransactionId), harness.LauncherName)));
        string pendingJson = await File.ReadAllTextAsync(result.PendingPlanPath);
        Assert.Contains("\"state\": \"staged\"", pendingJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"state\": \"Staged\"", pendingJson, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd", pendingJson,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyConfirmAndCompletionUseMoveJournalAndRemoveTransaction()
    {
        using var harness = new Harness(_root);
        _ = await harness.StageAsync();

        SelfUpdatePlan applied = await harness.Manager.ApplyPendingAsync(harness.Target);

        Assert.Equal(SelfUpdatePlanState.AwaitingConfirmation, applied.State);
        Assert.NotNull(applied.Apply);
        Assert.Equal("new-launcher", await File.ReadAllTextAsync(harness.LauncherPath));
        Assert.Equal("support-new-launcher", await File.ReadAllTextAsync(harness.SupportPath));
        await harness.Manager.ConfirmAsync(
            applied.TransactionId,
            harness.Target,
            harness.LauncherPath);
        Assert.True(harness.Manager.IsConfirmed(applied.TransactionId));

        await harness.Manager.CompleteConfirmedAsync(applied.TransactionId, harness.Target);

        Assert.False(File.Exists(harness.Manager.PendingPlanPath));
        Assert.False(Directory.Exists(
            harness.Manager.GetTransactionDirectory(applied.TransactionId)));
        Assert.Equal("new-launcher", await File.ReadAllTextAsync(harness.LauncherPath));
    }

    [Fact]
    public async Task AwaitingConfirmationRollbackRestoresEveryOldFileAndCanRetry()
    {
        using var harness = new Harness(_root);
        _ = await harness.StageAsync();
        SelfUpdatePlan applied = await harness.Manager.ApplyPendingAsync(harness.Target);

        SelfUpdatePlan rolledBack = await harness.Manager
            .RollbackAwaitingConfirmationAsync(harness.Target);

        Assert.Equal(SelfUpdatePlanState.RolledBack, rolledBack.State);
        Assert.All(rolledBack.Apply!, entry =>
        {
            if (entry.HadOriginal)
            {
                Assert.Matches("^[0-9a-f]{64}$", entry.PriorSha256!);
                Assert.NotNull(entry.PriorSize);
                Assert.NotNull(entry.PriorUnixMode);
            }
        });
        Assert.Equal("old-launcher", await File.ReadAllTextAsync(harness.LauncherPath));
        Assert.Equal("old-support", await File.ReadAllTextAsync(harness.SupportPath));
        SelfUpdatePlan retried = await harness.Manager.ApplyPendingAsync(harness.Target);
        Assert.Equal(SelfUpdatePlanState.AwaitingConfirmation, retried.State);
        Assert.Equal("new-launcher", await File.ReadAllTextAsync(harness.LauncherPath));
    }

    [Fact]
    public async Task PriorOwnershipRemovesObsoleteFilesAndRollbackRestoresThem()
    {
        using var harness = new Harness(_root);
        byte[] firstArchive = UpdateTestData.CreateZip(
        [
            (harness.LauncherName, "launcher-v2"u8.ToArray(), 0x81ED),
            ("support.dat", "support-v2"u8.ToArray(), 0x81A4),
            ("obsolete.dll", "obsolete-v2"u8.ToArray(), 0x81A4),
        ]);
        _ = await harness.StageAsync("2.0.0", firstArchive);
        SelfUpdatePlan first = await harness.Manager.ApplyPendingAsync(harness.Target);
        await harness.Manager.ConfirmAsync(
            first.TransactionId,
            harness.Target,
            harness.LauncherPath);
        await harness.Manager.CompleteConfirmedAsync(first.TransactionId, harness.Target);
        string obsoletePath = Path.Combine(harness.Target, "obsolete.dll");
        Assert.Equal("obsolete-v2", await File.ReadAllTextAsync(obsoletePath));

        byte[] secondArchive = UpdateTestData.CreateZip(
        [
            (harness.LauncherName, "launcher-v3"u8.ToArray(), 0x81ED),
            ("support.dat", "support-v3"u8.ToArray(), 0x81A4),
        ]);
        _ = await harness.StageAsync("3.0.0", secondArchive);
        SelfUpdatePlan applied = await harness.Manager.ApplyPendingAsync(harness.Target);

        Assert.False(File.Exists(obsoletePath));
        Assert.Contains(
            applied.Apply!,
            entry => entry.Path == "obsolete.dll"
                && entry.Operation == SelfUpdateApplyOperation.Remove
                && entry.HadOriginal);

        SelfUpdatePlan rolledBack = await harness.Manager
            .RollbackAwaitingConfirmationAsync(harness.Target);

        Assert.Equal(SelfUpdatePlanState.RolledBack, rolledBack.State);
        Assert.Equal("launcher-v2", await File.ReadAllTextAsync(harness.LauncherPath));
        Assert.Equal("support-v2", await File.ReadAllTextAsync(harness.SupportPath));
        Assert.Equal("obsolete-v2", await File.ReadAllTextAsync(obsoletePath));

        SelfUpdatePlan retried = await harness.Manager.ApplyPendingAsync(harness.Target);
        await harness.Manager.ConfirmAsync(
            retried.TransactionId,
            harness.Target,
            harness.LauncherPath);
        await harness.Manager.CompleteConfirmedAsync(retried.TransactionId, harness.Target);

        Assert.False(File.Exists(obsoletePath));
        string ownership = await File.ReadAllTextAsync(Path.Combine(
            harness.Target,
            LauncherSelfUpdateManager.InstallRecordFileName));
        Assert.DoesNotContain("obsolete.dll", ownership, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyFailpointAfterCanonicalAtomicReplaceLeavesVerifiedRollbackReceipt()
    {
        using var harness = new Harness(_root);
        _ = await harness.StageAsync();
        LauncherSelfUpdateManager faulting = harness.CreateManagerWithObserver(observation =>
        {
            if (observation.Boundary == SelfUpdateApplyBoundary.AfterTargetMutation
                && string.Equals(
                    observation.Path,
                    harness.LauncherName,
                    StringComparison.Ordinal))
            {
                Assert.True(File.Exists(harness.LauncherPath));
                throw new InvalidOperationException("failpoint");
            }
        });

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => faulting.ApplyPendingAsync(harness.Target));
        SelfUpdatePlan recovered = Assert.IsType<SelfUpdatePlan>(
            await harness.Manager.LoadPendingAsync());

        Assert.Equal("failpoint", failure.Message);
        Assert.Equal(SelfUpdatePlanState.RolledBack, recovered.State);
        await harness.Manager.VerifyRestoredPriorAsync(harness.Target);
        Assert.Equal("old-launcher", await File.ReadAllTextAsync(harness.LauncherPath));
        Assert.Equal("old-support", await File.ReadAllTextAsync(harness.SupportPath));
        Assert.Equal("new-launcher", await File.ReadAllTextAsync(Path.Combine(
            harness.Manager.GetPayloadDirectory(recovered.TransactionId),
            harness.LauncherName)));
    }

    [Fact]
    public async Task ConditionalPriorIntegrityFieldsAreStrictAndFailClosed()
    {
        using var harness = new Harness(_root);
        _ = await harness.StageAsync();
        SelfUpdatePlan applied = await harness.Manager.ApplyPendingAsync(harness.Target);
        SelfUpdateApplyEntry canonical = Assert.Single(
            applied.Apply!,
            entry => entry.Path == harness.LauncherName);
        Assert.True(canonical.HadOriginal);
        Assert.Matches("^[0-9a-f]{64}$", canonical.PriorSha256!);
        Assert.NotNull(canonical.PriorSize);
        Assert.NotNull(canonical.PriorUnixMode);
        Assert.Matches("^[0-9a-f]{64}$", canonical.ReplacementSha256!);

        JsonObject document = Assert.IsType<JsonObject>(JsonNode.Parse(
            await File.ReadAllTextAsync(harness.Manager.PendingPlanPath)));
        JsonArray apply = Assert.IsType<JsonArray>(document["apply"]);
        JsonObject canonicalNode = Assert.IsType<JsonObject>(apply.Single(node =>
            string.Equals(
                node?["path"]?.GetValue<string>(),
                harness.LauncherName,
                StringComparison.Ordinal)));
        canonicalNode["priorSha256"] = null;
        await File.WriteAllTextAsync(
            harness.Manager.PendingPlanPath,
            document.ToJsonString());

        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            harness.Manager.LoadPendingAsync());
        Assert.Equal("new-launcher", await File.ReadAllTextAsync(harness.LauncherPath));
        Assert.True(Directory.Exists(
            harness.Manager.GetTargetTransactionDirectory(applied)));
    }

    [Fact]
    public async Task CorruptPayloadWrongTargetAndUnknownPlanFieldFailClosed()
    {
        using var harness = new Harness(_root);
        _ = await harness.StageAsync();
        SelfUpdatePlan staged = Assert.IsType<SelfUpdatePlan>(
            await harness.Manager.LoadPendingAsync());
        await File.WriteAllTextAsync(
            Path.Combine(
                harness.Manager.GetPayloadDirectory(staged.TransactionId),
                harness.LauncherName),
            "bad-payload!");

        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            harness.Manager.ApplyPendingAsync(harness.Target));
        Assert.Equal("old-launcher", await File.ReadAllTextAsync(harness.LauncherPath));
        string wrongTarget = Path.Combine(_root, "other-target");
        Directory.CreateDirectory(wrongTarget);
        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            harness.Manager.ApplyPendingAsync(wrongTarget));

        string json = await File.ReadAllTextAsync(harness.Manager.PendingPlanPath);
        await File.WriteAllTextAsync(
            harness.Manager.PendingPlanPath,
            json.TrimEnd().TrimEnd('}') + ",\"unknown\":true}");
        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            harness.Manager.LoadPendingAsync());
    }

    [Fact]
    public async Task BootstrapConfirmationAndOrdinaryStartupDoNotUseShellParsing()
    {
        using var harness = new Harness(_root);
        string[] publicArguments =
        [
            "--config-dir", Path.Combine(_root, "config with spaces"),
            "--data-dir", Path.Combine(_root, "data & literal"),
            "--cache-dir", Path.Combine(_root, "cache"),
            "--update-manifest-uri", "http://127.0.0.1:43119/manifest.json",
        ];
        SelfUpdateStartupResult ordinary = await LauncherSelfUpdateBootstrap.HandleAsync(
            publicArguments,
            harness.Manager,
            harness.Target,
            harness.LauncherPath);
        Assert.False(ordinary.ShouldExit);
        Assert.Equal(publicArguments, ordinary.RemainingArguments);

        SelfUpdateStartupResult deferred = await LauncherSelfUpdateBootstrap.HandleAsync(
            ["--acdream-self-update-deferred-v1", .. publicArguments],
            harness.Manager,
            harness.Target,
            harness.LauncherPath);
        Assert.True(deferred.ShouldExit);
        Assert.Equal(64, deferred.ExitCode);
        Assert.Empty(deferred.RemainingArguments);

        _ = await harness.StageAsync();
        SelfUpdatePlan applied = await harness.Manager.ApplyPendingAsync(harness.Target);
        SelfUpdateStartupResult confirmation;
        using (UpdateSessionBarrier.ExclusiveLease helperLease =
               harness.Manager.Barrier.AcquireExclusive())
        {
            confirmation = await LauncherSelfUpdateBootstrap.HandleAsync(
                [
                    LauncherSelfUpdateBootstrap.ConfirmArgument,
                    applied.TransactionId,
                    .. publicArguments,
                ],
                harness.Manager,
                harness.Target,
                harness.LauncherPath);
        }

        Assert.False(confirmation.ShouldExit);
        Assert.Equal(publicArguments, confirmation.RemainingArguments);
        Assert.True(File.Exists(harness.Manager.PendingPlanPath));
        Assert.True(harness.Manager.IsConfirmed(applied.TransactionId));
        await harness.Manager.CompleteConfirmedAsync(applied.TransactionId, harness.Target);
        Assert.False(File.Exists(harness.Manager.PendingPlanPath));
    }

    [Fact]
    public async Task ContendedOrdinaryStartupAllowsOnlyNoPlanOrValidatedStagedPlan()
    {
        using var harness = new Harness(_root);
        using (UpdateSessionBarrier.SessionLease session =
               harness.Manager.Barrier.AcquireSession())
        {
            SelfUpdateStartupResult empty = await LauncherSelfUpdateBootstrap.HandleAsync(
                ["ordinary"],
                harness.Manager,
                harness.Target,
                harness.LauncherPath);
            Assert.False(empty.ShouldExit);
        }

        _ = await harness.StageAsync();
        using (UpdateSessionBarrier.SessionLease session =
               harness.Manager.Barrier.AcquireSession())
        {
            SelfUpdateStartupResult staged = await LauncherSelfUpdateBootstrap.HandleAsync(
                ["ordinary"],
                harness.Manager,
                harness.Target,
                harness.LauncherPath);
            Assert.False(staged.ShouldExit);
        }

        SelfUpdatePlan awaiting = await harness.Manager.ApplyPendingAsync(harness.Target);
        using (UpdateSessionBarrier.SessionLease session =
               harness.Manager.Barrier.AcquireSession())
        {
            await Assert.ThrowsAsync<LauncherUpdateException>(() =>
                LauncherSelfUpdateBootstrap.HandleAsync(
                    ["ordinary"],
                    harness.Manager,
                    harness.Target,
                    harness.LauncherPath));
        }

        SelfUpdatePlan rolledBack = await harness.Manager
            .RollbackAwaitingConfirmationAsync(harness.Target);
        using (UpdateSessionBarrier.SessionLease session =
               harness.Manager.Barrier.AcquireSession())
        {
            await Assert.ThrowsAsync<LauncherUpdateException>(() =>
                LauncherSelfUpdateBootstrap.HandleAsync(
                    ["ordinary"],
                    harness.Manager,
                    harness.Target,
                    harness.LauncherPath));
        }

        await SetPlanStateAsync(harness.Manager.PendingPlanPath, "applying");
        using (UpdateSessionBarrier.SessionLease session =
               harness.Manager.Barrier.AcquireSession())
        {
            await Assert.ThrowsAsync<LauncherUpdateException>(() =>
                LauncherSelfUpdateBootstrap.HandleAsync(
                    ["ordinary"],
                    harness.Manager,
                    harness.Target,
                    harness.LauncherPath));
        }

        Assert.Equal(SelfUpdatePlanState.AwaitingConfirmation, awaiting.State);
        Assert.Equal(SelfUpdatePlanState.RolledBack, rolledBack.State);
    }

    [Fact]
    public async Task OrdinaryStartupRecoversApplyingAndFinalizesVerifiedRollback()
    {
        using var harness = new Harness(_root);
        _ = await harness.StageAsync();
        _ = await harness.Manager.ApplyPendingAsync(harness.Target);
        await SetPlanStateAsync(harness.Manager.PendingPlanPath, "applying");

        SelfUpdateStartupResult result = await LauncherSelfUpdateBootstrap.HandleAsync(
            ["ordinary"],
            harness.Manager,
            harness.Target,
            harness.LauncherPath);

        Assert.False(result.ShouldExit);
        Assert.Equal(["ordinary"], result.RemainingArguments);
        Assert.Null(await harness.Manager.LoadPendingAsync());
        Assert.Equal("old-launcher", await File.ReadAllTextAsync(harness.LauncherPath));
        Assert.Equal("old-support", await File.ReadAllTextAsync(harness.SupportPath));
    }

    [Fact]
    public async Task InternalPrefixSpoofsCannotCrossPlanStateOrExecutableTrust()
    {
        using var harness = new Harness(_root);
        _ = await harness.StageAsync();
        SelfUpdatePlan staged = Assert.IsType<SelfUpdatePlan>(
            await harness.Manager.LoadPendingAsync());

        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            LauncherSelfUpdateBootstrap.HandleAsync(
                [
                    LauncherSelfUpdateBootstrap.HelperArgument,
                    int.MaxValue.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    harness.Target,
                    staged.TransactionId,
                ],
                harness.Manager,
                harness.Target,
                harness.LauncherPath));
        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            LauncherSelfUpdateBootstrap.HandleAsync(
                [LauncherSelfUpdateBootstrap.ConfirmArgument, staged.TransactionId],
                harness.Manager,
                harness.Target,
                harness.LauncherPath));

        SelfUpdatePlan awaiting = await harness.Manager.ApplyPendingAsync(harness.Target);
        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            LauncherSelfUpdateBootstrap.HandleAsync(
                [LauncherSelfUpdateBootstrap.ConfirmArgument, awaiting.TransactionId],
                harness.Manager,
                harness.Target,
                Path.Combine(harness.Target, "spoof-launcher")));
        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            LauncherSelfUpdateBootstrap.HandleAsync(
                [
                    LauncherSelfUpdateBootstrap.HelperArgument,
                    int.MaxValue.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    harness.Target,
                    awaiting.TransactionId,
                ],
                harness.Manager,
                harness.Target,
                harness.LauncherPath));

        SelfUpdatePlan rolledBack = await harness.Manager
            .RollbackAwaitingConfirmationAsync(harness.Target);
        foreach (string prefix in new[]
                 {
                     LauncherSelfUpdateBootstrap.HelperArgument,
                     LauncherSelfUpdateBootstrap.ConfirmArgument,
                 })
        {
            string[] arguments = prefix == LauncherSelfUpdateBootstrap.HelperArgument
                ? [prefix, int.MaxValue.ToString(), harness.Target, rolledBack.TransactionId]
                : [prefix, rolledBack.TransactionId];
            await Assert.ThrowsAsync<LauncherUpdateException>(() =>
                LauncherSelfUpdateBootstrap.HandleAsync(
                    arguments,
                    harness.Manager,
                    harness.Target,
                    harness.LauncherPath));
        }

        SelfUpdateStartupResult deferred = await LauncherSelfUpdateBootstrap.HandleAsync(
            ["--acdream-self-update-deferred-v1"],
            harness.Manager,
            harness.Target,
            harness.LauncherPath);
        Assert.True(deferred.ShouldExit);
        Assert.Equal(64, deferred.ExitCode);

        await File.WriteAllTextAsync(harness.Manager.PendingPlanPath, "{ambiguous");
        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            LauncherSelfUpdateBootstrap.HandleAsync(
                [LauncherSelfUpdateBootstrap.ConfirmArgument, rolledBack.TransactionId],
                harness.Manager,
                harness.Target,
                harness.LauncherPath));
        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            LauncherSelfUpdateBootstrap.HandleAsync(
                [
                    LauncherSelfUpdateBootstrap.HelperArgument,
                    int.MaxValue.ToString(),
                    harness.Target,
                    rolledBack.TransactionId,
                ],
                harness.Manager,
                harness.Target,
                harness.LauncherPath));
    }

    private static async Task SetPlanStateAsync(string path, string state)
    {
        JsonObject plan = Assert.IsType<JsonObject>(JsonNode.Parse(
            await File.ReadAllTextAsync(path)));
        plan["state"] = state;
        await File.WriteAllTextAsync(path, plan.ToJsonString());
    }

    private sealed class Harness : IDisposable
    {
        private readonly LocalHttpFixture _server = new();
        private readonly HttpClient _http = new();
        private readonly byte[] _archive;
        private readonly ReleaseArtifact _artifact;
        private readonly string _root;

        public Harness(string root)
        {
            _root = root;
            Target = Path.Combine(root, "published launcher");
            Directory.CreateDirectory(Target);
            Rid = LauncherRuntimeIdentity.DetectRid();
            LauncherName = "acdream-launcher"
                + (Rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty);
            LauncherPath = Path.Combine(Target, LauncherName);
            SupportPath = Path.Combine(Target, "support.dat");
            File.WriteAllText(LauncherPath, "old-launcher");
            File.WriteAllText(SupportPath, "old-support");
            _archive = UpdateTestData.LauncherZip(Rid, "new-launcher");
            _server.Add("launcher.zip", _archive);
            _artifact = new ReleaseArtifact(
                _server.UriFor("launcher.zip"),
                UpdateTestData.Sha256(_archive),
                _archive.LongLength);
            Manager = new LauncherSelfUpdateManager(UpdateTestData.Paths(root), _http);
        }

        public string Target { get; }

        public string Rid { get; }

        public string LauncherName { get; }

        public string LauncherPath { get; }

        public string SupportPath { get; }

        public LauncherSelfUpdateManager Manager { get; }

        public Task<SelfUpdateStageResult> StageAsync() => Manager.StageAsync(
            LauncherVersion.Parse("2.0.0"),
            Rid,
            _artifact,
            Target,
            progress: null,
            CancellationToken.None);

        public Task<SelfUpdateStageResult> StageAsync(string version, byte[] archive)
        {
            _server.Add("launcher.zip", archive);
            return Manager.StageAsync(
                LauncherVersion.Parse(version),
                Rid,
                new ReleaseArtifact(
                    _server.UriFor("launcher.zip"),
                    UpdateTestData.Sha256(archive),
                    archive.LongLength),
                Target,
                progress: null,
                CancellationToken.None);
        }

        public LauncherSelfUpdateManager CreateManagerWithObserver(
            Action<SelfUpdateApplyObservation> observer) =>
            new(UpdateTestData.Paths(_root), _http, null, observer);

        public void Dispose()
        {
            _http.Dispose();
            _server.Dispose();
        }
    }
}
