using System.Text.Json;
using System.Text.Json.Serialization;
using AcDream.Launcher.Core;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Updates;

public enum SelfUpdatePlanState
{
    Staged,
    Applying,
    AwaitingConfirmation,
    RolledBack,
}

public enum SelfUpdateApplyOperation
{
    Install,
    Remove,
}

public sealed record SelfUpdateApplyEntry(
    string Path,
    SelfUpdateApplyOperation Operation,
    bool HadOriginal,
    string? PriorSha256,
    long? PriorSize,
    int? PriorUnixMode,
    string? ReplacementSha256,
    long? ReplacementSize,
    int? ReplacementUnixMode);

public sealed record SelfUpdatePlan(
    int SchemaVersion,
    string TransactionId,
    SelfUpdatePlanState State,
    string Version,
    string Rid,
    string TargetDirectory,
    LauncherInstallationKind InstallationKind,
    string ArchiveSha256,
    long ArchiveSize,
    IReadOnlyList<InstalledFileRecord> Files,
    IReadOnlyList<SelfUpdateApplyEntry>? Apply)
{
    public const int CurrentSchemaVersion = 4;
}

public sealed record LauncherBinaryInstallRecord(
    int SchemaVersion,
    string Version,
    string Rid,
    IReadOnlyList<InstalledFileRecord> Files)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record SelfUpdateStageResult(
    LauncherVersion Version,
    string PendingPlanPath,
    string Status);

internal enum SelfUpdateApplyBoundary
{
    AfterTargetMutation,
}

internal sealed record SelfUpdateApplyObservation(
    SelfUpdateApplyBoundary Boundary,
    string Path,
    SelfUpdateApplyOperation Operation);

public sealed class LauncherSelfUpdateManager
{
    public const string InstallRecordFileName = "launcher.install.json";
    private const string TargetTransactionPrefix = ".acdream-self-update-";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        Converters =
        {
            new JsonStringEnumConverter<SelfUpdatePlanState>(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false),
            new JsonStringEnumConverter<SelfUpdateApplyOperation>(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false),
        },
    };

    private readonly VerifiedArtifactDownloader _downloader;
    private readonly SafeZipExtractor _extractor;
    private readonly Action<SelfUpdateApplyObservation>? _applyObserver;

    private sealed record JournalFileMetadata(string Sha256, long Size, int UnixMode);

    private sealed record RollbackAction(
        SelfUpdateApplyEntry Entry,
        string TargetPath,
        string BackupPath,
        string DiscardPath);

    public LauncherSelfUpdateManager(
        ApplicationPathSet paths,
        HttpClient httpClient,
        SafeZipExtractor? extractor = null)
        : this(paths, httpClient, extractor, applyObserver: null)
    {
    }

    internal LauncherSelfUpdateManager(
        ApplicationPathSet paths,
        HttpClient httpClient,
        SafeZipExtractor? extractor,
        Action<SelfUpdateApplyObservation>? applyObserver)
    {
        ArgumentNullException.ThrowIfNull(paths);
        RootDirectory = Path.Combine(
            Path.GetFullPath(paths.DataDirectory),
            "launcher-update");
        TransactionsDirectory = Path.Combine(RootDirectory, "transactions");
        PendingPlanPath = Path.Combine(RootDirectory, "pending.json");
        Barrier = new UpdateSessionBarrier(paths.DataDirectory);
        _downloader = new VerifiedArtifactDownloader(
            httpClient ?? throw new ArgumentNullException(nameof(httpClient)));
        _extractor = extractor ?? new SafeZipExtractor();
        _applyObserver = applyObserver;
    }

    public string RootDirectory { get; }

    public string TransactionsDirectory { get; }

    public string PendingPlanPath { get; }

    public UpdateSessionBarrier Barrier { get; }

    public Task<SelfUpdateStageResult> StageAsync(
        ReleaseManifest manifest,
        string rid,
        string targetDirectory,
        IProgress<ArtifactDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return StageAsync(
            manifest.Version,
            rid,
            manifest.RequireLauncher(rid),
            LauncherInstallationLayout.Flat(targetDirectory, rid),
            progress,
            cancellationToken);
    }

    public Task<SelfUpdateStageResult> StageAsync(
        ReleaseManifest manifest,
        string rid,
        LauncherInstallationLayout layout,
        IProgress<ArtifactDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(layout);
        return StageAsync(
            manifest.Version,
            rid,
            manifest.RequireLauncher(rid),
            layout,
            progress,
            cancellationToken);
    }

    internal Task<SelfUpdateStageResult> StageAsync(
        LauncherVersion version,
        string rid,
        ReleaseArtifact artifact,
        string targetDirectory,
        IProgress<ArtifactDownloadProgress>? progress,
        CancellationToken cancellationToken) =>
        StageAsync(
            version,
            rid,
            artifact,
            LauncherInstallationLayout.Flat(targetDirectory, rid),
            progress,
            cancellationToken);

    internal async Task<SelfUpdateStageResult> StageAsync(
        LauncherVersion version,
        string rid,
        ReleaseArtifact artifact,
        LauncherInstallationLayout layout,
        IProgress<ArtifactDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(artifact);
        if (!LauncherRuntimeIdentity.IsValidRid(rid))
        {
            throw new ArgumentException("RID is invalid.", nameof(rid));
        }

        ValidateLayout(layout, rid);
        using UpdateSessionBarrier.ExclusiveLease lease = Barrier.AcquireExclusive();
        string target = NormalizeTargetDirectory(layout.InstalledRoot);
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(TransactionsDirectory);
        SelfUpdatePlan? existing = await LoadPendingAsync(cancellationToken)
            .ConfigureAwait(false);
        _ = CleanupOwnedResidueUnderLease(existing, target, lease);
        if (existing is not null)
        {
            throw new LauncherUpdateException(
                $"Launcher self-update {existing.Version} is already {existing.State}. "
                + "Restart the launcher to finish it before staging another.");
        }

        string transactionId = Guid.NewGuid().ToString("N");
        string transactionDirectory = GetTransactionDirectory(transactionId);
        string payloadDirectory = GetPayloadDirectory(transactionId);
        string archivePath = Path.Combine(transactionDirectory, "launcher.zip");
        Directory.CreateDirectory(transactionDirectory);
        try
        {
            _ = await _downloader.DownloadAsync(
                    artifact,
                    archivePath,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<ExtractedFileRecord> extracted = await _extractor.ExtractAsync(
                    archivePath,
                    payloadDirectory,
                    PayloadExecutableNames.ForPayload(rid, launcherPayload: true),
                    cancellationToken)
                .ConfigureAwait(false);
            if (layout.Kind == LauncherInstallationKind.MacBundle)
            {
                ValidateMacBundleExecutable(extracted, layout);
            }
            else
            {
                ClientVersionStore.ValidateRequiredExecutables(
                    extracted,
                    rid,
                    launcherPayload: true);
            }
            ValidatePayloadLayout(extracted, layout);
            if (extracted.Any(file => string.Equals(
                    file.Path,
                    InstallRecordFileName,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new LauncherUpdateException(
                    $"The launcher ZIP may not provide '{InstallRecordFileName}'.");
            }

            var plan = new SelfUpdatePlan(
                SelfUpdatePlan.CurrentSchemaVersion,
                transactionId,
                SelfUpdatePlanState.Staged,
                version.Value,
                rid,
                target,
                layout.Kind,
                artifact.Sha256.ToLowerInvariant(),
                artifact.Size,
                extracted.Select(file => new InstalledFileRecord(
                        file.Path,
                        file.Sha256,
                        file.Size,
                        file.UnixMode))
                    .OrderBy(file => file.Path, StringComparer.Ordinal)
                    .ToArray(),
                null);
            ValidatePlan(plan, target);
            await WritePlanAsync(plan, cancellationToken).ConfigureAwait(false);
            VerifiedArtifactDownloader.TryDelete(archivePath);
            return new SelfUpdateStageResult(
                version,
                PendingPlanPath,
                $"Launcher {version} is staged and will be applied on next start.");
        }
        catch
        {
            if (!File.Exists(PendingPlanPath))
            {
                SafeZipExtractor.TryDeleteDirectory(transactionDirectory);
            }

            throw;
        }
    }

    public async Task<SelfUpdatePlan?> LoadPendingAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(PendingPlanPath))
        {
            return null;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(PendingPlanPath, cancellationToken)
                .ConfigureAwait(false);
            SelfUpdatePlan? plan = ClientVersionStore.ParseStrict<SelfUpdatePlan>(
                bytes,
                SerializerOptions);
            if (plan is null)
            {
                throw new LauncherUpdateException("The self-update plan is empty.");
            }

            plan = MigratePlan(plan);
            ValidatePlan(plan, plan.TargetDirectory);
            return plan;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LauncherUpdateException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or FormatException
                                   or NotSupportedException)
        {
            throw new LauncherUpdateException(
                $"The pending launcher self-update is invalid: {ex.Message}",
                ex);
        }
    }

    public async Task<SelfUpdatePlan> ApplyPendingAsync(
        string expectedTargetDirectory,
        CancellationToken cancellationToken = default)
    {
        string expectedTarget = NormalizeTargetDirectory(expectedTargetDirectory);
        SelfUpdatePlan plan = await LoadPendingAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new LauncherUpdateException("There is no staged launcher self-update.");
        ValidatePlan(plan, expectedTarget);

        if (plan.InstallationKind == LauncherInstallationKind.MacBundle)
        {
            return await ApplyBundlePendingAsync(plan, cancellationToken).ConfigureAwait(false);
        }

        if (plan.State == SelfUpdatePlanState.AwaitingConfirmation)
        {
            return plan;
        }

        if (plan.State == SelfUpdatePlanState.Applying)
        {
            plan = await RollbackApplyingAsync(plan, cancellationToken)
                .ConfigureAwait(false);
        }

        if (plan.State == SelfUpdatePlanState.RolledBack)
        {
            await VerifyRestoredPriorAsync(plan, expectedTarget, cancellationToken)
                .ConfigureAwait(false);
            plan = plan with
            {
                State = SelfUpdatePlanState.Staged,
                Apply = null,
            };
            await WritePlanAsync(plan, cancellationToken).ConfigureAwait(false);
        }

        await VerifyPayloadAsync(plan, cancellationToken).ConfigureAwait(false);
        LauncherBinaryInstallRecord? previous = await ReadAndVerifyInstallRecordAsync(
                expectedTarget,
                plan.Rid,
                cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<SelfUpdateApplyEntry> apply = await BuildApplyJournalAsync(
                plan,
                previous,
                expectedTarget,
                cancellationToken)
            .ConfigureAwait(false);
        apply = await PrepareTargetTransactionAsync(plan, apply, cancellationToken)
            .ConfigureAwait(false);
        plan = plan with
        {
            State = SelfUpdatePlanState.Applying,
            Apply = apply,
        };
        await WritePlanAsync(plan, cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (SelfUpdateApplyEntry entry in plan.Apply)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ApplyEntryAsync(plan, entry, cancellationToken)
                    .ConfigureAwait(false);
                _applyObserver?.Invoke(new SelfUpdateApplyObservation(
                    SelfUpdateApplyBoundary.AfterTargetMutation,
                    entry.Path,
                    entry.Operation));
            }

            plan = plan with { State = SelfUpdatePlanState.AwaitingConfirmation };
            await WritePlanAsync(plan, cancellationToken).ConfigureAwait(false);
            return plan;
        }
        catch
        {
            await RollbackApplyingAsync(plan, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SelfUpdatePlan> RecoverApplyingAsync(
        string expectedTargetDirectory,
        CancellationToken cancellationToken = default)
    {
        string expectedTarget = NormalizeTargetPath(expectedTargetDirectory);
        SelfUpdatePlan plan = await LoadPendingAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new LauncherUpdateException("There is no pending self-update.");
        ValidatePlan(plan, expectedTarget);
        return plan.State == SelfUpdatePlanState.Applying
            ? plan.InstallationKind == LauncherInstallationKind.MacBundle
                ? await RollbackBundleApplyingAsync(plan, cancellationToken).ConfigureAwait(false)
                : await RollbackApplyingAsync(plan, cancellationToken).ConfigureAwait(false)
            : plan;
    }

    internal async Task VerifyRestoredPriorAsync(
        string expectedTargetDirectory,
        CancellationToken cancellationToken = default)
    {
        string expectedTarget = NormalizeTargetDirectory(expectedTargetDirectory);
        SelfUpdatePlan plan = await LoadPendingAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new LauncherUpdateException("There is no rolled-back self-update.");
        ValidatePlan(plan, expectedTarget);
        if (plan.State != SelfUpdatePlanState.RolledBack)
        {
            throw new LauncherUpdateException(
                "The pending self-update has no verified rollback receipt.");
        }

        await VerifyRestoredPriorAsync(plan, expectedTarget, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ConfirmAsync(
        string transactionId,
        string expectedTargetDirectory,
        string currentExecutablePath,
        CancellationToken cancellationToken = default)
    {
        SelfUpdatePlan plan = await LoadPendingAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new LauncherUpdateException("There is no self-update to confirm.");
        string expectedTarget = NormalizeTargetDirectory(expectedTargetDirectory);
        ValidatePlan(plan, expectedTarget);
        if (!string.Equals(plan.TransactionId, transactionId, StringComparison.Ordinal)
            || plan.State != SelfUpdatePlanState.AwaitingConfirmation)
        {
            throw new LauncherUpdateException(
                "The running launcher does not match the pending confirmation plan.");
        }

        string expectedExecutable = LayoutFor(plan).LauncherPath;
        if (!PathsEqual(expectedExecutable, currentExecutablePath))
        {
            throw new LauncherUpdateException(
                "Only the newly installed launcher executable may confirm self-update.");
        }

        await VerifyAppliedTargetsAsync(plan, expectedTarget, cancellationToken)
            .ConfigureAwait(false);
        if (plan.InstallationKind == LauncherInstallationKind.Flat)
        {
            await VerifyInstalledOwnershipMatchesPlanAsync(
                    plan,
                    expectedTarget,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        string confirmationPath = GetConfirmationPath(transactionId);
        await AtomicJsonFile.WriteBytesAsync(
                confirmationPath,
                "confirmed"u8.ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public bool IsConfirmed(string transactionId) =>
        File.Exists(GetConfirmationPath(transactionId));

    public async Task CompleteConfirmedAsync(
        string transactionId,
        string expectedTargetDirectory,
        CancellationToken cancellationToken = default)
    {
        SelfUpdatePlan plan = await LoadPendingAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new LauncherUpdateException("There is no self-update to complete.");
        string expectedTarget = NormalizeTargetDirectory(expectedTargetDirectory);
        ValidatePlan(plan, expectedTarget);
        if (!string.Equals(plan.TransactionId, transactionId, StringComparison.Ordinal)
            || plan.State != SelfUpdatePlanState.AwaitingConfirmation
            || !IsConfirmed(transactionId))
        {
            throw new LauncherUpdateException("The self-update is not confirmed.");
        }

        File.Delete(PendingPlanPath);
        SafeZipExtractor.TryDeleteDirectory(GetTargetTransactionDirectory(plan));
        SafeZipExtractor.TryDeleteDirectory(GetTransactionDirectory(transactionId));
    }

    internal async Task CompleteRolledBackAsync(
        string transactionId,
        string expectedTargetDirectory,
        UpdateSessionBarrier.ExclusiveLease lease,
        CancellationToken cancellationToken = default)
    {
        Barrier.RequireOwned(lease);
        string expectedTarget = NormalizeTargetDirectory(expectedTargetDirectory);
        SelfUpdatePlan plan = await LoadPendingAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new LauncherUpdateException("There is no rolled-back self-update.");
        ValidatePlan(plan, expectedTarget);
        if (!string.Equals(plan.TransactionId, transactionId, StringComparison.Ordinal)
            || plan.State != SelfUpdatePlanState.RolledBack)
        {
            throw new LauncherUpdateException(
                "The self-update does not have the expected rollback receipt.");
        }

        await VerifyRestoredPriorAsync(plan, expectedTarget, cancellationToken)
            .ConfigureAwait(false);
        File.Delete(PendingPlanPath);
        SafeZipExtractor.TryDeleteDirectory(GetTargetTransactionDirectory(plan));
        SafeZipExtractor.TryDeleteDirectory(GetTransactionDirectory(transactionId));
    }

    public async Task<SelfUpdatePlan> RollbackAwaitingConfirmationAsync(
        string expectedTargetDirectory,
        CancellationToken cancellationToken = default)
    {
        string expectedTarget = NormalizeTargetDirectory(expectedTargetDirectory);
        SelfUpdatePlan plan = await LoadPendingAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new LauncherUpdateException("There is no self-update to roll back.");
        ValidatePlan(plan, expectedTarget);
        if (plan.State != SelfUpdatePlanState.AwaitingConfirmation)
        {
            throw new LauncherUpdateException(
                "The pending self-update is not awaiting confirmation.");
        }

        plan = plan with { State = SelfUpdatePlanState.Applying };
        await WritePlanAsync(plan, cancellationToken).ConfigureAwait(false);
        return plan.InstallationKind == LauncherInstallationKind.MacBundle
            ? await RollbackBundleApplyingAsync(plan, cancellationToken).ConfigureAwait(false)
            : await RollbackApplyingAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    public string GetTransactionDirectory(string transactionId)
    {
        RequireTransactionId(transactionId);
        return Path.Combine(TransactionsDirectory, transactionId);
    }

    public string GetPayloadDirectory(string transactionId) =>
        Path.Combine(GetTransactionDirectory(transactionId), "payload");

    public string GetConfirmationPath(string transactionId) =>
        Path.Combine(GetTransactionDirectory(transactionId), "confirmed");

    internal string GetTargetTransactionDirectory(SelfUpdatePlan plan) =>
        LayoutFor(plan).GetSiblingTransactionDirectory(plan.TransactionId);

    internal string GetStagedLauncherPath(SelfUpdatePlan plan) =>
        ClientVersionStore.ResolveContained(
            GetPayloadDirectory(plan.TransactionId),
            LayoutFor(plan).PayloadLauncherPath);

    internal bool CleanupOwnedResidueUnderLease(
        SelfUpdatePlan? pending,
        string targetDirectory,
        UpdateSessionBarrier.ExclusiveLease lease)
    {
        Barrier.RequireOwned(lease);
        string target = NormalizeTargetDirectory(targetDirectory);
        CleanupDataResidue(pending?.TransactionId);
        string? keepTarget = pending is
        {
            State: SelfUpdatePlanState.Applying or SelfUpdatePlanState.AwaitingConfirmation,
        }
            ? pending.TransactionId
            : null;
        string transactionContainer = pending?.InstallationKind == LauncherInstallationKind.MacBundle
            || string.Equals(Path.GetFileName(target), "OpenAC.app", StringComparison.Ordinal)
            ? Path.GetDirectoryName(target)
                ?? throw new LauncherUpdateException("The app bundle has no containing directory.")
            : target;
        CleanupTargetResidue(transactionContainer, keepTarget);
        return !HasReclaimableResidue(
            pending?.TransactionId,
            transactionContainer,
            keepTarget);
    }

    private static string GetLauncherFileName(string rid) =>
        PayloadExecutableNames.Launcher + PayloadExecutableNames.SuffixForRid(rid);

    private static LauncherInstallationLayout LayoutFor(SelfUpdatePlan plan) =>
        plan.InstallationKind == LauncherInstallationKind.MacBundle
            ? LauncherInstallationLayout.MacBundle(plan.TargetDirectory, plan.Rid)
            : LauncherInstallationLayout.Flat(plan.TargetDirectory, plan.Rid);

    private static void ValidateLayout(LauncherInstallationLayout layout, string rid)
    {
        if (layout.Kind == LauncherInstallationKind.MacBundle)
        {
            if (!rid.StartsWith("osx-", StringComparison.Ordinal)
                || layout != LauncherInstallationLayout.MacBundle(layout.InstalledRoot, rid))
            {
                throw new LauncherUpdateException("The macOS launcher installation layout is invalid.");
            }
        }
        else if (layout.Kind != LauncherInstallationKind.Flat)
        {
            throw new LauncherUpdateException("The launcher installation layout is invalid.");
        }
    }

    private static void ValidatePayloadLayout(
        IReadOnlyList<ExtractedFileRecord> files,
        LauncherInstallationLayout layout)
    {
        if (layout.Kind != LauncherInstallationKind.MacBundle)
        {
            return;
        }

        string prefix = layout.PayloadRoot + "/";
        string infoPlist = layout.PayloadRoot + "/Contents/Info.plist";
        if (files.Any(file => !file.Path.StartsWith(prefix, StringComparison.Ordinal))
            || !files.Any(file => string.Equals(
                file.Path,
                layout.PayloadLauncherPath,
                StringComparison.Ordinal))
            || !files.Any(file => string.Equals(
                file.Path,
                infoPlist,
                StringComparison.Ordinal)))
        {
            throw new LauncherUpdateException(
                "The macOS launcher ZIP must contain exactly the OpenAC.app bundle root and launcher executable.");
        }
    }

    private static void ValidateMacBundleExecutable(
        IReadOnlyList<ExtractedFileRecord> files,
        LauncherInstallationLayout layout)
    {
        ExtractedFileRecord? launcher = files.SingleOrDefault(file =>
            string.Equals(file.Path, layout.PayloadLauncherPath, StringComparison.Ordinal));
        if (launcher is null
            || (launcher.UnixMode & (int)UnixFileMode.UserExecute) == 0)
        {
            throw new LauncherUpdateException(
                "The macOS launcher ZIP lacks an executable app-bundle launcher.");
        }
    }

    private async Task<IReadOnlyList<SelfUpdateApplyEntry>> BuildApplyJournalAsync(
        SelfUpdatePlan plan,
        LauncherBinaryInstallRecord? previous,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        var operations = new Dictionary<string, SelfUpdateApplyOperation>(
            StringComparer.OrdinalIgnoreCase);
        foreach (InstalledFileRecord file in plan.Files)
        {
            operations.Add(file.Path, SelfUpdateApplyOperation.Install);
        }

        operations.Add(InstallRecordFileName, SelfUpdateApplyOperation.Install);
        if (previous is not null)
        {
            foreach (InstalledFileRecord file in previous.Files)
            {
                if (!operations.ContainsKey(file.Path))
                {
                    operations.Add(file.Path, SelfUpdateApplyOperation.Remove);
                }
            }
        }

        var result = new List<SelfUpdateApplyEntry>(operations.Count);
        foreach ((string path, SelfUpdateApplyOperation operation) in operations
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            string targetPath = ClientVersionStore.ResolveContained(targetDirectory, path);
            EnsureSafeParent(targetDirectory, targetPath);
            JournalFileMetadata? prior = await CaptureOptionalFileMetadataAsync(
                    targetPath,
                    $"Self-update target '{path}'",
                    cancellationToken)
                .ConfigureAwait(false);
            bool hadOriginal = prior is not null;
            if (operation == SelfUpdateApplyOperation.Remove && !hadOriginal)
            {
                throw new LauncherUpdateException(
                    $"Owned obsolete launcher file '{path}' is missing.");
            }

            if (string.Equals(
                    path,
                    GetLauncherFileName(plan.Rid),
                    StringComparison.OrdinalIgnoreCase)
                && !hadOriginal)
            {
                throw new LauncherUpdateException(
                    "The canonical launcher executable is missing before self-update.");
            }

            result.Add(new SelfUpdateApplyEntry(
                path,
                operation,
                hadOriginal,
                prior?.Sha256,
                prior?.Size,
                prior?.UnixMode,
                ReplacementSha256: null,
                ReplacementSize: null,
                ReplacementUnixMode: null));
        }

        return result;
    }

    private async Task<IReadOnlyList<SelfUpdateApplyEntry>> PrepareTargetTransactionAsync(
        SelfUpdatePlan plan,
        IReadOnlyList<SelfUpdateApplyEntry> apply,
        CancellationToken cancellationToken)
    {
        string swap = GetTargetTransactionDirectory(plan);
        if (Directory.Exists(swap))
        {
            ClientVersionStore.RejectReparseTree(swap);
            SafeZipExtractor.TryDeleteDirectory(swap);
        }

        if (Directory.Exists(swap) || File.Exists(swap))
        {
            throw new LauncherUpdateException(
                "The target-local self-update transaction could not be reclaimed.");
        }

        string incoming = Path.Combine(swap, "incoming");
        Directory.CreateDirectory(incoming);
        string payload = GetPayloadDirectory(plan.TransactionId);
        foreach (InstalledFileRecord file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = ClientVersionStore.ResolveContained(payload, file.Path);
            string destination = ClientVersionStore.ResolveContained(incoming, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await CopyFileDurablyAsync(source, destination, cancellationToken)
                .ConfigureAwait(false);
            if (LauncherOperatingSystem.IsUnix && file.UnixMode != 0)
            {
                File.SetUnixFileMode(destination, (UnixFileMode)file.UnixMode);
            }

            await VerifyFileAsync(
                    incoming,
                    file,
                    "Target-local incoming launcher",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var ownership = new LauncherBinaryInstallRecord(
            LauncherBinaryInstallRecord.CurrentSchemaVersion,
            plan.Version,
            plan.Rid,
            plan.Files);
        await AtomicJsonFile.WriteAsync(
                Path.Combine(incoming, InstallRecordFileName),
                ownership,
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false);
        ClientVersionStore.RejectReparseTree(swap);

        string[] actual = Directory.EnumerateFiles(
                incoming,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(incoming, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        string[] expected = apply
            .Where(entry => entry.Operation == SelfUpdateApplyOperation.Install)
            .Select(entry => entry.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new LauncherUpdateException(
                "The target-local self-update incoming tree is incomplete.");
        }

        var completed = new List<SelfUpdateApplyEntry>(apply.Count);
        foreach (SelfUpdateApplyEntry entry in apply)
        {
            if (entry.Operation == SelfUpdateApplyOperation.Remove)
            {
                completed.Add(entry);
                continue;
            }

            JournalFileMetadata replacement = await CaptureRequiredFileMetadataAsync(
                    ClientVersionStore.ResolveContained(incoming, entry.Path),
                    $"Target-local incoming launcher file '{entry.Path}'",
                    cancellationToken)
                .ConfigureAwait(false);
            completed.Add(entry with
            {
                ReplacementSha256 = replacement.Sha256,
                ReplacementSize = replacement.Size,
                ReplacementUnixMode = replacement.UnixMode,
            });
        }

        return completed;
    }

    private async Task ApplyEntryAsync(
        SelfUpdatePlan plan,
        SelfUpdateApplyEntry entry,
        CancellationToken cancellationToken)
    {
        string swap = GetTargetTransactionDirectory(plan);
        string incoming = Path.Combine(swap, "incoming");
        string backup = Path.Combine(swap, "backup");
        string targetPath = ClientVersionStore.ResolveContained(
            plan.TargetDirectory,
            entry.Path);
        string backupPath = ClientVersionStore.ResolveContained(backup, entry.Path);
        string incomingPath = ClientVersionStore.ResolveContained(incoming, entry.Path);
        ClientVersionStore.RejectReparseTree(swap);
        EnsureExistingParentsSafe(plan.TargetDirectory, targetPath);
        EnsureExistingParentsSafe(swap, incomingPath);
        EnsureExistingParentsSafe(swap, backupPath);
        EnsurePathMissing(backupPath, $"Self-update backup '{entry.Path}'");

        if (entry.HadOriginal)
        {
            await VerifyPriorFileAsync(entry, targetPath, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            EnsurePathMissing(targetPath, $"Self-update target '{entry.Path}'");
        }

        if (entry.Operation == SelfUpdateApplyOperation.Remove)
        {
            EnsureSafeParent(swap, backupPath);
            File.Move(targetPath, backupPath);
            return;
        }

        await VerifyReplacementFileAsync(entry, incomingPath, cancellationToken)
            .ConfigureAwait(false);
        EnsureSafeParent(swap, backupPath);

        if (entry.HadOriginal)
        {
            File.Replace(incomingPath, targetPath, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(incomingPath, targetPath);
        }
    }

    private async Task<SelfUpdatePlan> ApplyBundlePendingAsync(
        SelfUpdatePlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.State == SelfUpdatePlanState.AwaitingConfirmation)
        {
            return plan;
        }

        if (plan.State == SelfUpdatePlanState.Applying)
        {
            plan = await RollbackBundleApplyingAsync(plan, cancellationToken).ConfigureAwait(false);
        }

        if (plan.State == SelfUpdatePlanState.RolledBack)
        {
            await VerifyBundleRestoredAsync(plan, cancellationToken).ConfigureAwait(false);
            plan = plan with { State = SelfUpdatePlanState.Staged, Apply = null };
            await WritePlanAsync(plan, cancellationToken).ConfigureAwait(false);
        }

        await VerifyPayloadAsync(plan, cancellationToken).ConfigureAwait(false);
        LauncherInstallationLayout layout = LayoutFor(plan);
        string swap = GetTargetTransactionDirectory(plan);
        if (Directory.Exists(swap))
        {
            ClientVersionStore.RejectReparseTree(swap);
            SafeZipExtractor.TryDeleteDirectory(swap);
        }

        if (Directory.Exists(swap) || File.Exists(swap))
        {
            throw new LauncherUpdateException("The macOS bundle transaction could not be reclaimed.");
        }

        string incomingRoot = Path.Combine(swap, "incoming");
        Directory.CreateDirectory(incomingRoot);
        await CopyBundleIncomingAsync(plan, incomingRoot, cancellationToken).ConfigureAwait(false);
        string incomingBundle = ClientVersionStore.ResolveContained(incomingRoot, layout.PayloadRoot);
        string backupRoot = Path.Combine(swap, "backup");
        string backupBundle = Path.Combine(backupRoot, layout.PayloadRoot);
        ClientVersionStore.RejectReparseTree(swap);
        if (!Directory.Exists(layout.InstalledRoot)
            || (File.GetAttributes(layout.InstalledRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new LauncherUpdateException("The installed macOS app bundle is missing or linked.");
        }

        plan = plan with { State = SelfUpdatePlanState.Applying, Apply = [] };
        await WritePlanAsync(plan, cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(backupRoot);
            Directory.Move(layout.InstalledRoot, backupBundle);
            _applyObserver?.Invoke(new SelfUpdateApplyObservation(
                SelfUpdateApplyBoundary.AfterTargetMutation,
                layout.PayloadRoot,
                SelfUpdateApplyOperation.Install));
            Directory.Move(incomingBundle, layout.InstalledRoot);
            await VerifyAppliedTargetsAsync(plan, layout.InstalledRoot, cancellationToken)
                .ConfigureAwait(false);
            plan = plan with { State = SelfUpdatePlanState.AwaitingConfirmation };
            await WritePlanAsync(plan, cancellationToken).ConfigureAwait(false);
            return plan;
        }
        catch
        {
            await RollbackBundleApplyingAsync(plan, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task CopyBundleIncomingAsync(
        SelfUpdatePlan plan,
        string incomingRoot,
        CancellationToken cancellationToken)
    {
        string payload = GetPayloadDirectory(plan.TransactionId);
        foreach (InstalledFileRecord file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = ClientVersionStore.ResolveContained(payload, file.Path);
            string destination = ClientVersionStore.ResolveContained(incomingRoot, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await CopyFileDurablyAsync(source, destination, cancellationToken).ConfigureAwait(false);
            if (LauncherOperatingSystem.IsUnix && file.UnixMode != 0)
            {
                File.SetUnixFileMode(destination, (UnixFileMode)file.UnixMode);
            }

            await VerifyFileAsync(incomingRoot, file, "Incoming macOS app bundle", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<SelfUpdatePlan> RollbackBundleApplyingAsync(
        SelfUpdatePlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.State != SelfUpdatePlanState.Applying || plan.Apply is null)
        {
            throw new LauncherUpdateException("The macOS bundle rollback journal is missing.");
        }

        LauncherInstallationLayout layout = LayoutFor(plan);
        string swap = GetTargetTransactionDirectory(plan);
        string backup = Path.Combine(swap, "backup", layout.PayloadRoot);
        string discard = Path.Combine(swap, "rollback-discard", layout.PayloadRoot);
        if (!Directory.Exists(swap))
        {
            throw new LauncherUpdateException("The macOS bundle rollback transaction is missing.");
        }

        ClientVersionStore.RejectReparseTree(swap);
        if (Directory.Exists(backup))
        {
            if (Directory.Exists(layout.InstalledRoot))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(discard)!);
                Directory.Move(layout.InstalledRoot, discard);
            }

            Directory.Move(backup, layout.InstalledRoot);
        }
        else if (!Directory.Exists(layout.InstalledRoot))
        {
            throw new LauncherUpdateException("The macOS bundle rollback state is ambiguous.");
        }

        await VerifyBundleRestoredAsync(plan, cancellationToken).ConfigureAwait(false);
        plan = plan with { State = SelfUpdatePlanState.RolledBack };
        await WritePlanAsync(plan, cancellationToken).ConfigureAwait(false);
        SafeZipExtractor.TryDeleteDirectory(swap);
        return plan;
    }

    private static Task VerifyBundleRestoredAsync(
        SelfUpdatePlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LauncherInstallationLayout layout = LayoutFor(plan);
        if (!Directory.Exists(layout.InstalledRoot)
            || !File.Exists(layout.LauncherPath)
            || (File.GetAttributes(layout.InstalledRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new LauncherUpdateException("The prior macOS app bundle was not restored.");
        }

        return Task.CompletedTask;
    }

    private async Task<SelfUpdatePlan> RollbackApplyingAsync(
        SelfUpdatePlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.State != SelfUpdatePlanState.Applying || plan.Apply is null)
        {
            throw new LauncherUpdateException("The self-update rollback journal is missing.");
        }

        string swap = GetTargetTransactionDirectory(plan);
        IReadOnlyList<RollbackAction> actions = await PreflightRollbackAsync(
                plan,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (RollbackAction action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClientVersionStore.RejectReparseTree(swap);
            EnsureExistingParentsSafe(plan.TargetDirectory, action.TargetPath);
            EnsureExistingParentsSafe(swap, action.BackupPath);
            EnsureExistingParentsSafe(swap, action.DiscardPath);
            if (action.Entry.Operation == SelfUpdateApplyOperation.Remove)
            {
                EnsureSafeParent(plan.TargetDirectory, action.TargetPath);
                File.Move(action.BackupPath, action.TargetPath);
                continue;
            }

            EnsureSafeParent(swap, action.DiscardPath);
            if (action.Entry.HadOriginal)
            {
                File.Replace(
                    action.BackupPath,
                    action.TargetPath,
                    action.DiscardPath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(action.TargetPath, action.DiscardPath);
            }
        }

        await VerifyRestoredPriorAsync(plan, plan.TargetDirectory, cancellationToken)
            .ConfigureAwait(false);
        plan = plan with
        {
            State = SelfUpdatePlanState.RolledBack,
        };
        await WritePlanAsync(plan, cancellationToken).ConfigureAwait(false);
        SafeZipExtractor.TryDeleteDirectory(swap);
        return plan;
    }

    private async Task<IReadOnlyList<RollbackAction>> PreflightRollbackAsync(
        SelfUpdatePlan plan,
        CancellationToken cancellationToken)
    {
        string swap = GetTargetTransactionDirectory(plan);
        if (!Directory.Exists(swap))
        {
            throw new LauncherUpdateException(
                "The target-local self-update rollback transaction is missing.");
        }

        ClientVersionStore.RejectReparseTree(swap);
        ValidateRollbackTree(plan, swap);
        string incoming = Path.Combine(swap, "incoming");
        string backup = Path.Combine(swap, "backup");
        string discard = Path.Combine(swap, "rollback-discard");
        var actions = new List<RollbackAction>();
        foreach (SelfUpdateApplyEntry entry in plan.Apply!.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string targetPath = ClientVersionStore.ResolveContained(
                plan.TargetDirectory,
                entry.Path);
            string incomingPath = ClientVersionStore.ResolveContained(incoming, entry.Path);
            string backupPath = ClientVersionStore.ResolveContained(backup, entry.Path);
            string discardPath = ClientVersionStore.ResolveContained(discard, entry.Path);
            EnsureExistingParentsSafe(plan.TargetDirectory, targetPath);
            EnsureExistingParentsSafe(swap, incomingPath);
            EnsureExistingParentsSafe(swap, backupPath);
            EnsureExistingParentsSafe(swap, discardPath);

            JournalFileMetadata? target = await CaptureOptionalFileMetadataAsync(
                    targetPath,
                    $"Rollback target '{entry.Path}'",
                    cancellationToken)
                .ConfigureAwait(false);
            JournalFileMetadata? incomingFile = await CaptureOptionalFileMetadataAsync(
                    incomingPath,
                    $"Rollback incoming file '{entry.Path}'",
                    cancellationToken)
                .ConfigureAwait(false);
            JournalFileMetadata? backupFile = await CaptureOptionalFileMetadataAsync(
                    backupPath,
                    $"Rollback backup file '{entry.Path}'",
                    cancellationToken)
                .ConfigureAwait(false);
            JournalFileMetadata? discardedFile = await CaptureOptionalFileMetadataAsync(
                    discardPath,
                    $"Rollback discard file '{entry.Path}'",
                    cancellationToken)
                .ConfigureAwait(false);

            if (entry.Operation == SelfUpdateApplyOperation.Remove)
            {
                RequireMissing(incomingFile, entry.Path, "incoming");
                RequireMissing(discardedFile, entry.Path, "discard");
                if (backupFile is not null && target is null)
                {
                    RequirePriorMetadata(entry, backupFile, "rollback backup");
                    actions.Add(new RollbackAction(
                        entry,
                        targetPath,
                        backupPath,
                        discardPath));
                    continue;
                }

                if (backupFile is null && target is not null)
                {
                    RequirePriorMetadata(entry, target, "restored rollback target");
                    continue;
                }

                throw AmbiguousRollback(entry.Path);
            }

            if (entry.HadOriginal)
            {
                if (backupFile is not null
                    && target is not null
                    && incomingFile is null
                    && discardedFile is null)
                {
                    RequirePriorMetadata(entry, backupFile, "rollback backup");
                    RequireReplacementMetadata(entry, target, "applied rollback target");
                    actions.Add(new RollbackAction(
                        entry,
                        targetPath,
                        backupPath,
                        discardPath));
                    continue;
                }

                if (backupFile is null && target is not null)
                {
                    RequirePriorMetadata(entry, target, "restored rollback target");
                    if (incomingFile is not null && discardedFile is null)
                    {
                        RequireReplacementMetadata(
                            entry,
                            incomingFile,
                            "unapplied rollback incoming file");
                        continue;
                    }

                    if (incomingFile is null && discardedFile is not null)
                    {
                        RequireReplacementMetadata(
                            entry,
                            discardedFile,
                            "completed rollback discard");
                        continue;
                    }
                }

                throw AmbiguousRollback(entry.Path);
            }

            RequireMissing(backupFile, entry.Path, "backup");
            if (target is not null
                && incomingFile is null
                && discardedFile is null)
            {
                RequireReplacementMetadata(entry, target, "applied rollback target");
                actions.Add(new RollbackAction(
                    entry,
                    targetPath,
                    backupPath,
                    discardPath));
                continue;
            }

            if (target is null && incomingFile is not null && discardedFile is null)
            {
                RequireReplacementMetadata(
                    entry,
                    incomingFile,
                    "unapplied rollback incoming file");
                continue;
            }

            if (target is null && incomingFile is null && discardedFile is not null)
            {
                RequireReplacementMetadata(
                    entry,
                    discardedFile,
                    "completed rollback discard");
                continue;
            }

            throw AmbiguousRollback(entry.Path);
        }

        return actions;
    }

    private static void ValidateRollbackTree(SelfUpdatePlan plan, string swap)
    {
        RequireTransactionContainer(Path.Combine(swap, "incoming"), required: true);
        RequireTransactionContainer(Path.Combine(swap, "backup"), required: false);
        RequireTransactionContainer(
            Path.Combine(swap, "rollback-discard"),
            required: false);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "incoming",
        };
        foreach (SelfUpdateApplyEntry entry in plan.Apply!)
        {
            if (entry.Operation == SelfUpdateApplyOperation.Install)
            {
                AddAllowedTreePath(allowed, "incoming", entry.Path);
                AddAllowedTreePath(allowed, "rollback-discard", entry.Path);
            }

            if (entry.HadOriginal)
            {
                AddAllowedTreePath(allowed, "backup", entry.Path);
            }
        }

        foreach (string path in Directory.EnumerateFileSystemEntries(
                     swap,
                     "*",
                     SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(swap, path).Replace('\\', '/');
            if (!allowed.Contains(relative))
            {
                throw new LauncherUpdateException(
                    $"The rollback transaction contains unrecorded path '{relative}'.");
            }
        }
    }

    private static void RequireTransactionContainer(string path, bool required)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0
                || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new LauncherUpdateException(
                    $"Rollback container '{Path.GetFileName(path)}' is not a safe directory.");
            }
        }
        catch (FileNotFoundException) when (!required)
        {
        }
        catch (DirectoryNotFoundException) when (!required)
        {
        }
        catch (FileNotFoundException)
        {
            throw new LauncherUpdateException(
                $"Required rollback container '{Path.GetFileName(path)}' is missing.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new LauncherUpdateException(
                $"Required rollback container '{Path.GetFileName(path)}' is missing.");
        }
    }

    private static void AddAllowedTreePath(
        HashSet<string> allowed,
        string container,
        string relativePath)
    {
        allowed.Add(container);
        string current = container;
        foreach (string segment in relativePath.Split('/'))
        {
            current += "/" + segment;
            allowed.Add(current);
        }
    }

    private static LauncherUpdateException AmbiguousRollback(string path) => new(
        $"Rollback state for '{path}' is corrupt or ambiguous; transaction evidence was preserved.");

    private static void RequireMissing(
        JournalFileMetadata? metadata,
        string path,
        string location)
    {
        if (metadata is not null)
        {
            throw new LauncherUpdateException(
                $"Rollback {location} for '{path}' is unexpected; transaction evidence was preserved.");
        }
    }

    private static async Task VerifyRestoredPriorAsync(
        SelfUpdatePlan plan,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        if (plan.InstallationKind == LauncherInstallationKind.MacBundle)
        {
            await VerifyBundleRestoredAsync(plan, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (plan.Apply is null)
        {
            throw new LauncherUpdateException("The rollback receipt is missing its apply journal.");
        }

        foreach (SelfUpdateApplyEntry entry in plan.Apply)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string targetPath = ClientVersionStore.ResolveContained(targetDirectory, entry.Path);
            EnsureExistingParentsSafe(targetDirectory, targetPath);
            if (entry.HadOriginal)
            {
                await VerifyPriorFileAsync(entry, targetPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                EnsurePathMissing(targetPath, $"Restored rollback target '{entry.Path}'");
            }
        }
    }

    private static async Task VerifyPriorFileAsync(
        SelfUpdateApplyEntry entry,
        string path,
        CancellationToken cancellationToken)
    {
        JournalFileMetadata actual = await CaptureRequiredFileMetadataAsync(
                path,
                $"Prior launcher file '{entry.Path}'",
                cancellationToken)
            .ConfigureAwait(false);
        RequirePriorMetadata(entry, actual, "prior launcher file");
    }

    private static async Task VerifyReplacementFileAsync(
        SelfUpdateApplyEntry entry,
        string path,
        CancellationToken cancellationToken)
    {
        JournalFileMetadata actual = await CaptureRequiredFileMetadataAsync(
                path,
                $"Replacement launcher file '{entry.Path}'",
                cancellationToken)
            .ConfigureAwait(false);
        RequireReplacementMetadata(entry, actual, "replacement launcher file");
    }

    private static void RequirePriorMetadata(
        SelfUpdateApplyEntry entry,
        JournalFileMetadata actual,
        string description) =>
        RequireMetadata(
            entry.Path,
            description,
            actual,
            entry.PriorSha256,
            entry.PriorSize,
            entry.PriorUnixMode);

    private static void RequireReplacementMetadata(
        SelfUpdateApplyEntry entry,
        JournalFileMetadata actual,
        string description) =>
        RequireMetadata(
            entry.Path,
            description,
            actual,
            entry.ReplacementSha256,
            entry.ReplacementSize,
            entry.ReplacementUnixMode);

    private static void RequireMetadata(
        string path,
        string description,
        JournalFileMetadata actual,
        string? expectedSha256,
        long? expectedSize,
        int? expectedUnixMode)
    {
        if (!string.Equals(actual.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase)
            || actual.Size != expectedSize
            || actual.UnixMode != expectedUnixMode)
        {
            throw new LauncherUpdateException(
                $"The {description} '{path}' failed its rollback integrity check; "
                + "transaction evidence was preserved.");
        }
    }

    private static async Task<JournalFileMetadata> CaptureRequiredFileMetadataAsync(
        string path,
        string description,
        CancellationToken cancellationToken) =>
        await CaptureOptionalFileMetadataAsync(path, description, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new LauncherUpdateException($"{description} is missing.");

    private static async Task<JournalFileMetadata?> CaptureOptionalFileMetadataAsync(
        string path,
        string description,
        CancellationToken cancellationToken)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new LauncherUpdateException($"{description} is a directory or reparse point.");
        }

        var before = new FileInfo(path);
        long size = before.Length;
        int unixMode = LauncherOperatingSystem.IsUnix
            ? (int)File.GetUnixFileMode(path) & 0x1FF
            : 0;
        string sha256 = await Integrity.FileIntegrity.ComputeSha256HexAsync(
                path,
                cancellationToken)
            .ConfigureAwait(false);
        var after = new FileInfo(path);
        after.Refresh();
        if (!after.Exists
            || (after.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            || after.Length != size
            || (LauncherOperatingSystem.IsUnix
                && ((int)File.GetUnixFileMode(path) & 0x1FF) != unixMode))
        {
            throw new LauncherUpdateException($"{description} changed while it was measured.");
        }

        return new JournalFileMetadata(sha256, size, unixMode);
    }

    private static void EnsurePathMissing(string path, string description)
    {
        try
        {
            _ = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        throw new LauncherUpdateException($"{description} already exists.");
    }

    private async Task VerifyPayloadAsync(
        SelfUpdatePlan plan,
        CancellationToken cancellationToken)
    {
        string payload = GetPayloadDirectory(plan.TransactionId);
        if (!Directory.Exists(payload))
        {
            throw new LauncherUpdateException("The staged launcher payload is missing.");
        }

        ClientVersionStore.RejectReparseTree(payload);
        string[] actual = Directory.EnumerateFiles(
                payload,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(payload, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        string[] expected = plan.Files
            .Select(file => file.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new LauncherUpdateException(
                "The staged launcher contains missing or unrecorded files.");
        }

        foreach (InstalledFileRecord file in plan.Files)
        {
            await VerifyFileAsync(payload, file, "Staged launcher", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task VerifyAppliedTargetsAsync(
        SelfUpdatePlan plan,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        LauncherInstallationLayout layout = LayoutFor(plan);
        foreach (InstalledFileRecord file in plan.Files)
        {
            if (plan.InstallationKind == LauncherInstallationKind.MacBundle)
            {
                string prefix = layout.PayloadRoot + "/";
                InstalledFileRecord installed = file with { Path = file.Path[prefix.Length..] };
                await VerifyFileAsync(
                        targetDirectory,
                        installed,
                        "Applied launcher",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await VerifyFileAsync(
                        targetDirectory,
                        file,
                        "Applied launcher",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (plan.Apply is not null)
        {
            foreach (SelfUpdateApplyEntry obsolete in plan.Apply.Where(entry =>
                         entry.Operation == SelfUpdateApplyOperation.Remove))
            {
                string path = ClientVersionStore.ResolveContained(
                    targetDirectory,
                    obsolete.Path);
                if (File.Exists(path) || Directory.Exists(path))
                {
                    throw new LauncherUpdateException(
                        $"Obsolete launcher file '{obsolete.Path}' remains after apply.");
                }
            }
        }
    }

    private static async Task VerifyFileAsync(
        string root,
        InstalledFileRecord file,
        string description,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = ClientVersionStore.ResolveContained(root, file.Path);
        EnsureSafeParent(root, path);
        var info = new FileInfo(path);
        if (!info.Exists
            || (info.Attributes & FileAttributes.ReparsePoint) != 0
            || info.Length != file.Size)
        {
            throw new LauncherUpdateException(
                $"{description} file '{file.Path}' is missing, linked, or corrupt.");
        }

        string sha256 = await Integrity.FileIntegrity.ComputeSha256HexAsync(
                path,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherUpdateException(
                $"{description} file '{file.Path}' SHA-256 is corrupt.");
        }

        if (LauncherOperatingSystem.IsUnix
            && ((int)File.GetUnixFileMode(path) & 0x1FF) != file.UnixMode)
        {
            throw new LauncherUpdateException(
                $"{description} file '{file.Path}' mode is corrupt.");
        }
    }

    private static async Task<LauncherBinaryInstallRecord?> ReadAndVerifyInstallRecordAsync(
        string targetDirectory,
        string rid,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(targetDirectory, InstallRecordFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new LauncherUpdateException("The launcher ownership record is linked.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken)
            .ConfigureAwait(false);
        LauncherBinaryInstallRecord? record = ClientVersionStore.ParseStrict<LauncherBinaryInstallRecord>(
            bytes,
            SerializerOptions);
        ValidateInstallRecord(record, rid);
        foreach (InstalledFileRecord file in record!.Files)
        {
            await VerifyFileAsync(
                    targetDirectory,
                    file,
                    "Owned launcher",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return record;
    }

    private static async Task VerifyInstalledOwnershipMatchesPlanAsync(
        SelfUpdatePlan plan,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        LauncherBinaryInstallRecord? record = await ReadAndVerifyInstallRecordAsync(
                targetDirectory,
                plan.Rid,
                cancellationToken)
            .ConfigureAwait(false);
        if (record is null
            || !string.Equals(record.Version, plan.Version, StringComparison.Ordinal)
            || !record.Files.SequenceEqual(plan.Files))
        {
            throw new LauncherUpdateException(
                "The installed launcher ownership record does not match the pending plan.");
        }
    }

    private async Task WritePlanAsync(
        SelfUpdatePlan plan,
        CancellationToken cancellationToken)
    {
        ValidatePlan(plan, plan.TargetDirectory);
        await AtomicJsonFile.WriteAsync(
                PendingPlanPath,
                plan,
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static SelfUpdatePlan MigratePlan(SelfUpdatePlan plan) =>
        plan.SchemaVersion == 3
            ? plan with
            {
                SchemaVersion = SelfUpdatePlan.CurrentSchemaVersion,
                InstallationKind = LauncherInstallationKind.Flat,
            }
            : plan;

    private void ValidatePlan(SelfUpdatePlan plan, string expectedTargetDirectory)
    {
        if (plan.SchemaVersion != SelfUpdatePlan.CurrentSchemaVersion)
        {
            throw new LauncherUpdateException(
                $"Self-update schema version {plan.SchemaVersion} is not supported.");
        }

        RequireTransactionId(plan.TransactionId);
        if (!LauncherVersion.TryParse(plan.Version, out _)
            || !LauncherRuntimeIdentity.IsValidRid(plan.Rid)
            || !ReleaseManifestClient.IsSha256(plan.ArchiveSha256)
            || plan.ArchiveSize <= 0
            || plan.ArchiveSize > ReleaseManifestClient.MaximumArtifactBytes)
        {
            throw new LauncherUpdateException("The self-update plan metadata is invalid.");
        }

        bool bundleRecovery = plan.InstallationKind == LauncherInstallationKind.MacBundle
            && plan.State == SelfUpdatePlanState.Applying;
        string target = bundleRecovery
            ? NormalizeTargetPath(plan.TargetDirectory)
            : NormalizeTargetDirectory(plan.TargetDirectory);
        if (!PathsEqual(target, expectedTargetDirectory))
        {
            throw new LauncherUpdateException(
                "The self-update target does not match the running launcher directory.");
        }

        ValidateFileRecords(plan.Files, "self-update file list");
        if (!Enum.IsDefined(plan.InstallationKind))
        {
            throw new LauncherUpdateException("The self-update installation layout is invalid.");
        }

        LauncherInstallationLayout layout = LayoutFor(plan);
        if (plan.InstallationKind == LauncherInstallationKind.MacBundle)
        {
            string prefix = layout.PayloadRoot + "/";
            string infoPlist = layout.PayloadRoot + "/Contents/Info.plist";
            if (!plan.Rid.StartsWith("osx-", StringComparison.Ordinal)
                || plan.Files.Any(file => !file.Path.StartsWith(prefix, StringComparison.Ordinal))
                || !plan.Files.Any(file => string.Equals(
                    file.Path,
                    layout.PayloadLauncherPath,
                    StringComparison.Ordinal))
                || !plan.Files.Any(file => string.Equals(
                    file.Path,
                    infoPlist,
                    StringComparison.Ordinal))
                || (plan.State == SelfUpdatePlanState.Staged) != (plan.Apply is null)
                || (plan.Apply is not null && plan.Apply.Count != 0))
            {
                throw new LauncherUpdateException("The macOS bundle self-update plan is invalid.");
            }

            string bundleTransaction = GetTargetTransactionDirectory(plan);
            if (!IsContained(layout.ContainerDirectory, bundleTransaction))
            {
                throw new LauncherUpdateException("A macOS bundle transaction path escaped.");
            }

            return;
        }

        var newPaths = new HashSet<string>(
            plan.Files.Select(file => file.Path),
            StringComparer.OrdinalIgnoreCase);
        if (newPaths.Contains(InstallRecordFileName)
            || !newPaths.Contains(GetLauncherFileName(plan.Rid)))
        {
            throw new LauncherUpdateException(
                "The self-update file list has a reserved path or lacks the launcher executable.");
        }

        if (plan.State == SelfUpdatePlanState.Staged && plan.Apply is not null
            || plan.State != SelfUpdatePlanState.Staged && plan.Apply is null)
        {
            throw new LauncherUpdateException(
                "The self-update apply journal does not match its state.");
        }

        if (plan.Apply is not null)
        {
            var applyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? prior = null;
            foreach (SelfUpdateApplyEntry entry in plan.Apply)
            {
                if (!ClientVersionStore.IsNormalizedRelative(entry.Path)
                    || !applyPaths.Add(entry.Path)
                    || !Enum.IsDefined(entry.Operation)
                    || (entry.Operation == SelfUpdateApplyOperation.Remove
                        && !entry.HadOriginal)
                    || entry.HadOriginal != (
                        ReleaseManifestClient.IsSha256(entry.PriorSha256)
                        && entry.PriorSize is >= 0
                        && entry.PriorUnixMode is >= 0 and <= 0x1FF)
                    || entry.HadOriginal == (
                        entry.PriorSha256 is null
                        && entry.PriorSize is null
                        && entry.PriorUnixMode is null)
                    || (entry.Operation == SelfUpdateApplyOperation.Install) != (
                        ReleaseManifestClient.IsSha256(entry.ReplacementSha256)
                        && entry.ReplacementSize is >= 0
                        && entry.ReplacementUnixMode is >= 0 and <= 0x1FF)
                    || (entry.Operation == SelfUpdateApplyOperation.Install) == (
                        entry.ReplacementSha256 is null
                        && entry.ReplacementSize is null
                        && entry.ReplacementUnixMode is null)
                    || (prior is not null
                        && string.Compare(prior, entry.Path, StringComparison.Ordinal) >= 0))
                {
                    throw new LauncherUpdateException(
                        "The self-update apply journal is invalid, duplicated, or unsorted.");
                }

                prior = entry.Path;
            }

            foreach (string required in newPaths.Append(InstallRecordFileName))
            {
                SelfUpdateApplyEntry? entry = plan.Apply.FirstOrDefault(candidate =>
                    string.Equals(candidate.Path, required, StringComparison.OrdinalIgnoreCase));
                if (entry?.Operation != SelfUpdateApplyOperation.Install)
                {
                    throw new LauncherUpdateException(
                        "The self-update apply journal does not install every new owned file.");
                }
            }

            if (plan.Apply.Any(entry =>
                    entry.Operation == SelfUpdateApplyOperation.Remove
                    && (newPaths.Contains(entry.Path)
                        || string.Equals(
                            entry.Path,
                            InstallRecordFileName,
                            StringComparison.OrdinalIgnoreCase))))
            {
                throw new LauncherUpdateException(
                    "The self-update journal removes a new or reserved file.");
            }
        }

        string transactionDirectory = GetTransactionDirectory(plan.TransactionId);
        if (!IsContained(TransactionsDirectory, transactionDirectory)
            || !IsContained(layout.ContainerDirectory, GetTargetTransactionDirectory(plan)))
        {
            throw new LauncherUpdateException("A self-update transaction path escaped.");
        }
    }

    private static void ValidateInstallRecord(LauncherBinaryInstallRecord? record, string rid)
    {
        if (record is null
            || record.SchemaVersion != LauncherBinaryInstallRecord.CurrentSchemaVersion
            || !LauncherVersion.TryParse(record.Version, out _)
            || !string.Equals(record.Rid, rid, StringComparison.Ordinal))
        {
            throw new LauncherUpdateException("The launcher ownership record is invalid.");
        }

        ValidateFileRecords(record.Files, "launcher ownership file list");
        if (record.Files.Any(file => string.Equals(
                file.Path,
                InstallRecordFileName,
                StringComparison.OrdinalIgnoreCase))
            || !record.Files.Any(file => string.Equals(
                file.Path,
                GetLauncherFileName(rid),
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new LauncherUpdateException(
                "The launcher ownership record contains its reserved metadata path "
                + "or lacks the canonical launcher executable.");
        }
    }

    private static void ValidateFileRecords(
        IReadOnlyList<InstalledFileRecord>? files,
        string description)
    {
        if (files is null || files.Count == 0)
        {
            throw new LauncherUpdateException($"The {description} is empty.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? prior = null;
        foreach (InstalledFileRecord file in files)
        {
            if (!ClientVersionStore.IsNormalizedRelative(file.Path)
                || !paths.Add(file.Path)
                || !ReleaseManifestClient.IsSha256(file.Sha256)
                || file.Size < 0
                || file.UnixMode is < 0 or > 0x1FF
                || (prior is not null
                    && string.Compare(prior, file.Path, StringComparison.Ordinal) >= 0))
            {
                throw new LauncherUpdateException(
                    $"The {description} is invalid, duplicated, or unsorted.");
            }

            prior = file.Path;
        }
    }

    private static async Task CopyFileDurablyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, 64 * 1024, cancellationToken)
            .ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static string NormalizeTargetDirectory(string targetDirectory)
    {
        string target = NormalizeTargetPath(targetDirectory);
        if (!Directory.Exists(target)
            || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
        {
            throw new LauncherUpdateException(
                "The self-update target directory is missing or is a reparse point.");
        }

        return target;
    }

    private static string NormalizeTargetPath(string targetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        if (!Path.IsPathFullyQualified(targetDirectory))
        {
            throw new LauncherUpdateException(
                "The self-update target directory must be absolute.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
    }

    private static void EnsureSafeParent(string root, string filePath)
    {
        string? parent = Path.GetDirectoryName(filePath);
        if (parent is null)
        {
            throw new LauncherUpdateException("A self-update target has no parent.");
        }

        EnsureExistingParentsSafe(root, filePath);
        Directory.CreateDirectory(parent);
        EnsureExistingParentsSafe(root, filePath);
    }

    private static void EnsureExistingParentsSafe(string root, string filePath)
    {
        string? parent = Path.GetDirectoryName(filePath);
        if (parent is null)
        {
            throw new LauncherUpdateException("A self-update target has no parent.");
        }

        for (var directory = new DirectoryInfo(parent);
             directory is not null && IsContained(root, directory.FullName);
             directory = directory.Parent)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(directory.FullName);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            if ((attributes & FileAttributes.Directory) == 0
                || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new LauncherUpdateException(
                    $"Self-update target parent '{directory.FullName}' is not a safe directory.");
            }

            if (PathsEqual(directory.FullName, root))
            {
                break;
            }
        }
    }

    private static bool IsContained(string root, string path)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullPath = Path.GetFullPath(path);
        return PathsEqual(fullRoot, fullPath)
            || fullPath.StartsWith(
                fullRoot + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right) =>
        LauncherPathIdentity.Equals(left, right);

    private static void RequireTransactionId(string transactionId)
    {
        if (transactionId.Length != 32
            || !Guid.TryParseExact(transactionId, "N", out Guid parsed)
            || !string.Equals(parsed.ToString("N"), transactionId, StringComparison.Ordinal))
        {
            throw new LauncherUpdateException("The self-update transaction id is invalid.");
        }
    }

    private void CleanupDataResidue(string? keepTransactionId)
    {
        if (Directory.Exists(TransactionsDirectory))
        {
            foreach (string directory in Directory.EnumerateDirectories(
                         TransactionsDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(directory);
                if (IsCanonicalTransactionId(name)
                    && !string.Equals(name, keepTransactionId, StringComparison.Ordinal))
                {
                    SafeZipExtractor.TryDeleteDirectory(directory);
                }
            }
        }

        if (!Directory.Exists(RootDirectory))
        {
            return;
        }

        foreach (string temporary in Directory.EnumerateFiles(
                     RootDirectory,
                     ".pending.json.*.tmp",
                     SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(temporary);
            const string prefix = ".pending.json.";
            const string suffix = ".tmp";
            if (name.Length == prefix.Length + 32 + suffix.Length
                && name.StartsWith(prefix, StringComparison.Ordinal)
                && name.EndsWith(suffix, StringComparison.Ordinal)
                && IsCanonicalTransactionId(name.Substring(prefix.Length, 32)))
            {
                VerifiedArtifactDownloader.TryDelete(temporary);
            }
        }
    }

    private static void CleanupTargetResidue(
        string targetDirectory,
        string? keepTransactionId)
    {
        foreach (string directory in Directory.EnumerateDirectories(
                     targetDirectory,
                     TargetTransactionPrefix + "*",
                     SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(directory);
            string transaction = name[TargetTransactionPrefix.Length..];
            if (name.Length == TargetTransactionPrefix.Length + 32
                && IsCanonicalTransactionId(transaction)
                && !string.Equals(transaction, keepTransactionId, StringComparison.Ordinal))
            {
                SafeZipExtractor.TryDeleteDirectory(directory);
            }
        }
    }

    private bool HasReclaimableResidue(
        string? keepDataTransactionId,
        string targetDirectory,
        string? keepTargetTransactionId)
    {
        bool data = Directory.Exists(TransactionsDirectory)
            && Directory.EnumerateDirectories(
                    TransactionsDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Any(name => name is not null
                    && IsCanonicalTransactionId(name)
                    && !string.Equals(
                        name,
                        keepDataTransactionId,
                        StringComparison.Ordinal));
        bool target = Directory.EnumerateDirectories(
                targetDirectory,
                TargetTransactionPrefix + "*",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Any(name => name is not null
                && name.Length == TargetTransactionPrefix.Length + 32
                && IsCanonicalTransactionId(name[TargetTransactionPrefix.Length..])
                && !string.Equals(
                    name[TargetTransactionPrefix.Length..],
                    keepTargetTransactionId,
                    StringComparison.Ordinal));
        return data || target;
    }

    private static bool IsCanonicalTransactionId(string value) =>
        value.Length == 32
        && Guid.TryParseExact(value, "N", out Guid parsed)
        && string.Equals(parsed.ToString("N"), value, StringComparison.Ordinal);
}
