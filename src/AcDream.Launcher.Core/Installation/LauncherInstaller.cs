using AcDream.Launcher.Core.Integrity;
using AcDream.Launcher.Core.Launching;
using AcDream.Platform;
using System.Globalization;

namespace AcDream.Launcher.Core.Installation;

public enum LauncherInstallPhase
{
    Idle,
    ValidatingDatFiles,
    PreparingOutput,
    BakingMeshes,
    BakingCollision,
    VerifyingPackage,
    SavingRecord,
    Completed,
    Cancelled,
    Failed,
}

public sealed record LauncherInstallProgress(
    LauncherInstallPhase Phase,
    string Status,
    long Completed = 0,
    long Total = 0,
    int Failures = 0,
    double EtaSeconds = 0)
{
    public double Fraction => Total > 0
        ? Math.Clamp((double)Completed / Total, 0, 1)
        : 0;
}

public sealed record LauncherInstallResult(LauncherInstallRecord Record);

public sealed class LauncherInstallException : Exception
{
    public LauncherInstallException(string message)
        : base(message)
    {
    }

    public LauncherInstallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface ILauncherInstaller
{
    IReadOnlyList<DatDirectoryValidation> DetectDatDirectories();

    DatDirectoryValidation ValidateDatDirectory(string? directory);

    /// <param name="forceFullVerification">Hash the installed package even
    /// when a previous full hash of the same bytes is remembered. Ordinary
    /// startup passes false so the launcher window is not held behind a
    /// multi-second hash of a very large file; an explicit "verify my files"
    /// request passes true.</param>
    Task<InstallRecordVerification> LoadExistingAsync(
        CancellationToken cancellationToken = default,
        bool forceFullVerification = false);

    Task<InstallRecordVerification> LoadExistingWithProgressAsync(
        CancellationToken cancellationToken = default,
        bool forceFullVerification = false,
        IProgress<string>? progress = null) =>
        LoadExistingAsync(cancellationToken, forceFullVerification);

    Task<LauncherInstallResult> InstallAsync(
        string datDirectory,
        int threads,
        IProgress<LauncherInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<LauncherInstallResult> ApplyContentUpdateAsync(
        string datDirectory,
        int threads,
        ContentMigrationPlan migration,
        IProgress<LauncherInstallProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        migration.Kind == ContentWorkKind.FullRebuild
            ? InstallAsync(
                datDirectory,
                threads,
                progress,
                cancellationToken)
            : Task.FromException<LauncherInstallResult>(
                new NotSupportedException(
                    "This installer does not support filtered content overlays."));

    void ConfirmClientCompatibility()
    {
    }
}

public sealed class LauncherInstaller : ILauncherInstaller
{
    public const long FullRebuildRequiredFreeBytes = 2L * 1024 * 1024 * 1024;

    private readonly string _bakeExecutablePath;
    private readonly DatDirectoryLocator _datDirectories;
    private readonly LauncherInstallRecordStore _recordStore;
    private readonly LauncherContentStateStore _contentStateStore;
    private readonly IBakeProcessRunner _processRunner;
    private readonly Func<string, CancellationToken, Task<string>> _computeSha256;
    private readonly Func<string, long> _availableFreeSpace;
    private readonly SemaphoreSlim _installGate = new(1, 1);

    private LauncherInstallRecord? _verifiedRecord;

    internal Action? TransactionLeaseContentionObservedForTest { get; set; }
    internal Action? PublicationLeaseContentionObservedForTest { get; set; }

    public LauncherInstaller(
        ApplicationPathSet paths,
        string bakeExecutablePath,
        DatDirectoryLocator? datDirectories = null,
        LauncherInstallRecordStore? recordStore = null,
        LauncherContentStateStore? contentStateStore = null,
        IBakeProcessRunner? processRunner = null,
        Func<string, CancellationToken, Task<string>>? computeSha256 = null,
        Func<string, long>? availableFreeSpace = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(bakeExecutablePath);
        _bakeExecutablePath = Path.GetFullPath(bakeExecutablePath);
        _datDirectories = datDirectories ?? new DatDirectoryLocator();
        _computeSha256 = computeSha256
            ?? ((path, cancellationToken) =>
                FileIntegrity.ComputeSha256HexAsync(path, cancellationToken));
        _recordStore = recordStore
            ?? new LauncherInstallRecordStore(
                paths,
                _datDirectories,
                _computeSha256);
        _contentStateStore = contentStateStore
            ?? new LauncherContentStateStore(paths, _computeSha256);
        _processRunner = processRunner ?? new SystemBakeProcessRunner();
        _availableFreeSpace = availableFreeSpace ?? GetAvailableFreeSpace;
    }

    public IReadOnlyList<DatDirectoryValidation> DetectDatDirectories() =>
        _datDirectories.Detect();

    public DatDirectoryValidation ValidateDatDirectory(string? directory) =>
        _datDirectories.Validate(directory);

    public void ConfirmClientCompatibility() =>
        _contentStateStore.ClearClientCompatibilityPending();

    public async Task<InstallRecordVerification> LoadExistingAsync(
        CancellationToken cancellationToken = default,
        bool forceFullVerification = false) =>
        await LoadExistingWithProgressAsync(
                cancellationToken,
                forceFullVerification)
            .ConfigureAwait(false);

    public async Task<InstallRecordVerification> LoadExistingWithProgressAsync(
        CancellationToken cancellationToken = default,
        bool forceFullVerification = false,
        IProgress<string>? progress = null)
    {
        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using InstallerTransactionLease lease =
                await InstallerTransactionLease.AcquireAsync(
                        _recordStore.DataDirectory,
                        cancellationToken,
                        TransactionLeaseContentionObservedForTest)
                    .ConfigureAwait(false);
            InstallRecordVerification verification =
                await RecoverExistingUnderPublicationGuardAsync(
                        cancellationToken,
                        forceFullVerification,
                        progress)
                .ConfigureAwait(false);
            verification = await ResolveContentStateAsync(
                    verification,
                    forceFullVerification,
                    cancellationToken)
                .ConfigureAwait(false);
            if (verification.IsVerified
                && verification.Record is not null
                && _contentStateStore.IsClientCompatibilityPending)
            {
                verification = verification with
                {
                    Record = verification.Record with
                    {
                        RequiresClientCompatibilityConfirmation = true,
                    },
                    Status = "World data is verified; matching client confirmation is pending.",
                };
            }

            _verifiedRecord = verification.Record;
            return verification;
        }
        finally
        {
            _installGate.Release();
        }
    }

    public async Task<LauncherInstallResult> InstallAsync(
        string datDirectory,
        int threads,
        IProgress<LauncherInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (threads <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(threads),
                "Bake thread count must be positive.");
        }

        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using InstallerTransactionLease lease =
                await InstallerTransactionLease.AcquireAsync(
                        _recordStore.DataDirectory,
                        cancellationToken,
                        TransactionLeaseContentionObservedForTest)
                    .ConfigureAwait(false);
            return await InstallCoreAsync(
                    datDirectory,
                    threads,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _installGate.Release();
        }
    }

    private async Task<LauncherInstallResult> InstallCoreAsync(
        string datDirectory,
        int threads,
        IProgress<LauncherInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(
            progress,
            LauncherInstallPhase.ValidatingDatFiles,
            "Validating the four retail DAT files...");
        DatDirectoryValidation validation = _datDirectories.Validate(datDirectory);
        if (!validation.IsValid)
        {
            string message = validation.Message
                + FormatMissing(validation.MissingFileNames);
            Report(progress, LauncherInstallPhase.Failed, message);
            throw new LauncherInstallException(message);
        }

        if (!File.Exists(_bakeExecutablePath))
        {
            string message =
                $"The co-deployed bake tool is missing at '{_bakeExecutablePath}'.";
            Report(progress, LauncherInstallPhase.Failed, message);
            throw new LauncherInstallException(message);
        }

        string outputPath = _recordStore.PreparedAssetPath;
        string bakeOutputPath = GetFullRebuildCandidatePath(outputPath);
        string backupPath = LauncherInstallRecordStore.GetBackupPath(outputPath);
        InstallRecordVerification existing =
            await RecoverExistingUnderPublicationGuardAsync(
                    cancellationToken,
                    forceFullVerification: true)
            .ConfigureAwait(false);
        _verifiedRecord = existing.Record;
        LauncherContentState? priorContentState = null;
        if (existing.Record is not null)
        {
            (priorContentState, _) = await _contentStateStore.LoadAsync(
                    existing.Record,
                    forceFullVerification: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException(
                "The prepared package path has no parent directory."));

        long availableBytes;
        try
        {
            availableBytes = _availableFreeSpace(outputPath);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException)
        {
            string message =
                $"Could not check free space for the world-data rebuild: {exception.Message}";
            Report(progress, LauncherInstallPhase.Failed, message);
            throw new LauncherInstallException(message, exception);
        }

        if (availableBytes < FullRebuildRequiredFreeBytes)
        {
            string message =
                $"The optimized world-data rebuild needs at least "
                + $"{FormatGiB(FullRebuildRequiredFreeBytes)} GiB free "
                + $"beside the active package; only "
                + $"{FormatGiB(availableBytes)} GiB is available.";
            Report(progress, LauncherInstallPhase.Failed, message);
            throw new LauncherInstallException(message);
        }

        Report(
            progress,
            LauncherInstallPhase.PreparingOutput,
            "Preparing a one-time optimized world-data rebuild beside the active package. "
                + "This can take several minutes; progress will update below...");
        LauncherInstallRecordStore.TryDelete(bakeOutputPath);
        LauncherInstallRecordStore.TryDelete(backupPath);
        bool previousPreserved = false;
        bool canonicalReplaced = false;

        var parser = new BakeProgressJsonlParser();
        var protocol = new BakeProgressProtocol();
        string? publicationNonce = null;

        void Observe(BakeProgressEvent progressEvent)
        {
            bool accepted = protocol.Observe(progressEvent);
            switch (progressEvent)
            {
                case BakeWorkProgressEvent value when accepted:
                    LauncherInstallPhase phase = value.Phase switch
                    {
                        "mesh" => LauncherInstallPhase.BakingMeshes,
                        "collision" => LauncherInstallPhase.BakingCollision,
                        _ => LauncherInstallPhase.BakingMeshes,
                    };
                    Report(
                        progress,
                        phase,
                        $"Baking {value.Phase} assets: "
                            + $"{value.Completed:N0}/{value.Total:N0}; "
                            + $"failures: {value.Failures:N0}",
                        value.Completed,
                        value.Total,
                        value.Failures,
                        value.EtaSeconds);
                    break;
                case BakeErrorEvent value when accepted:
                    Report(
                        progress,
                        LauncherInstallPhase.Failed,
                        $"Bake tool error: {value.Message}");
                    break;
                case MalformedBakeProgressEvent value:
                    Report(
                        progress,
                        LauncherInstallPhase.Failed,
                        $"Malformed bake progress: {value.Reason}");
                    break;
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            publicationNonce = BakePublicationGuardPaths.CreateNonce();
            await using (
                BakePublicationGuardContract.PublicationLease publication =
                    await BakePublicationGuardContract.AcquireAsync(
                            bakeOutputPath,
                            cancellationToken)
                        .ConfigureAwait(false))
            {
                BakePublicationGuardContract.Authorize(
                    bakeOutputPath,
                    publicationNonce,
                    publication);
            }

            var request = new BakeProcessRequest(
                _bakeExecutablePath,
                validation.Directory,
                bakeOutputPath,
                threads,
                publicationNonce);
            BakeProcessResult processResult = await _processRunner.RunAsync(
                    request,
                    chunk =>
                    {
                        foreach (BakeProgressEvent progressEvent in parser.Append(chunk))
                        {
                            Observe(progressEvent);
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (BakeProgressEvent progressEvent in parser.Complete())
            {
                Observe(progressEvent);
            }
            protocol.CompleteInput();

            cancellationToken.ThrowIfCancellationRequested();
            if (protocol.Violation is not null)
            {
                throw new LauncherInstallException(protocol.Violation);
            }

            if (processResult.ExitCode != 0)
            {
                throw new LauncherInstallException(
                    BuildChildFailure(
                        processResult.ExitCode,
                        protocol.Error?.Message,
                        processResult.StandardError));
            }

            if (protocol.Error is not null)
            {
                throw new LauncherInstallException(
                    $"The bake tool reported an error: {protocol.Error.Message}");
            }

            BakeStartedEvent? started = protocol.Started;
            BakeCompletedEvent? completed = protocol.Completed;
            if (started is null || completed is null)
            {
                throw new LauncherInstallException(
                    "The bake protocol did not finish with a v1 completed event.");
            }

            if (started.BakeToolVersion != completed.BakeToolVersion
                || completed.BakeToolVersion
                    != LauncherInstallRecordStore.CurrentBakeToolVersion)
            {
                throw new LauncherInstallException(
                    $"The bake tool reported version {completed.BakeToolVersion}; "
                    + $"version {LauncherInstallRecordStore.CurrentBakeToolVersion} "
                    + "is required.");
            }

            if (completed.Failures != 0)
            {
                throw new LauncherInstallException(
                    $"The bake completed with {completed.Failures:N0} failed assets.");
            }

            if (!File.Exists(bakeOutputPath))
            {
                throw new LauncherInstallException(
                    "The bake tool reported success but did not publish acdream.pak.");
            }

            long size = new FileInfo(bakeOutputPath).Length;
            if (size <= 0 || size != completed.OutputBytes)
            {
                throw new LauncherInstallException(
                    "The published package size does not match the bake completion record.");
            }

            Report(
                progress,
                LauncherInstallPhase.VerifyingPackage,
                "Computing the prepared package SHA-256...");
            string sha256 = await _computeSha256(bakeOutputPath, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var record = new LauncherInstallRecord(
                validation.Directory,
                outputPath,
                sha256,
                size,
                completed.BakeToolVersion);
            Report(
                progress,
                LauncherInstallPhase.SavingRecord,
                "Activating the verified package...");
            previousPreserved = PreservePreviousPackage(outputPath, backupPath);
            File.Move(bakeOutputPath, outputPath, overwrite: true);
            canonicalReplaced = true;
            await _recordStore.SaveAtomicallyUnderLeaseAsync(
                    record,
                    cancellationToken)
                .ConfigureAwait(false);
            _recordStore.RememberVerifiedPackage(record);

            _contentStateStore.Delete();
            if (priorContentState?.Overlay is not null)
            {
                LauncherInstallRecordStore.TryDelete(
                    _contentStateStore.GetOverlayPath(
                        priorContentState.Overlay));
            }

            _verifiedRecord = record;
            await FinalizeSuccessfulPublicationAsync(
                    bakeOutputPath,
                    backupPath,
                    publicationNonce)
                .ConfigureAwait(false);
            Report(
                progress,
                LauncherInstallPhase.Completed,
                "Client content installed and verified.",
                completed: 1,
                total: 1);
            return new LauncherInstallResult(record);
        }
        catch (OperationCanceledException)
        {
            await FinalizeFailedPublicationAsync(
                    bakeOutputPath,
                    outputPath,
                    backupPath,
                    previousPreserved,
                    canonicalReplaced,
                    publicationNonce)
                .ConfigureAwait(false);
            Report(
                progress,
                LauncherInstallPhase.Cancelled,
                "Installation cancelled; no new install record was published.");
            throw;
        }
        catch (Exception ex)
        {
            await FinalizeFailedPublicationAsync(
                    bakeOutputPath,
                    outputPath,
                    backupPath,
                    previousPreserved,
                    canonicalReplaced,
                    publicationNonce)
                .ConfigureAwait(false);
            Report(
                progress,
                LauncherInstallPhase.Failed,
                $"Installation failed: {ex.Message}");
            if (ex is LauncherInstallException)
            {
                throw;
            }

            throw new LauncherInstallException("Installation failed.", ex);
        }
    }

    private async Task<InstallRecordVerification>
        RecoverExistingUnderPublicationGuardAsync(
            CancellationToken cancellationToken,
            bool forceFullVerification = false,
            IProgress<string>? progress = null)
    {
        string outputPath = _recordStore.PreparedAssetPath;
        string candidatePath = GetFullRebuildCandidatePath(outputPath);
        await using (
            BakePublicationGuardContract.PublicationLease candidatePublication =
                await BakePublicationGuardContract.AcquireAsync(
                        candidatePath,
                        cancellationToken,
                        PublicationLeaseContentionObservedForTest)
                    .ConfigureAwait(false))
        {
            BakePublicationGuardContract.Invalidate(
                candidatePath,
                candidatePublication);
            LauncherInstallRecordStore.TryDelete(candidatePath);
            BakeOutputStagingContract.DeleteOwnedStagingFiles(candidatePath);
        }

        string overlayCandidatePath = _contentStateStore.OverlayCandidatePath;
        await using (
            BakePublicationGuardContract.PublicationLease overlayPublication =
                await BakePublicationGuardContract.AcquireAsync(
                        overlayCandidatePath,
                        cancellationToken)
                    .ConfigureAwait(false))
        {
            BakePublicationGuardContract.Invalidate(
                overlayCandidatePath,
                overlayPublication);
            LauncherInstallRecordStore.TryDelete(overlayCandidatePath);
            BakeOutputStagingContract.DeleteOwnedStagingFiles(
                overlayCandidatePath);
        }

        await using BakePublicationGuardContract.PublicationLease publication =
            await BakePublicationGuardContract.AcquireAsync(
                    outputPath,
                    cancellationToken,
                    PublicationLeaseContentionObservedForTest)
                .ConfigureAwait(false);
        BakePublicationGuardContract.Invalidate(outputPath, publication);
        BakeOutputStagingContract.DeleteOwnedStagingFiles(outputPath);
        return await _recordStore.LoadAndVerifyUnderLeaseAsync(
                cancellationToken,
                forceFullVerification,
                progress)
            .ConfigureAwait(false);
    }

    private static string FormatGiB(long bytes) =>
        (bytes / (1024d * 1024d * 1024d)).ToString("N1", CultureInfo.InvariantCulture);

    public async Task<LauncherInstallResult> ApplyContentUpdateAsync(
        string datDirectory,
        int threads,
        ContentMigrationPlan migration,
        IProgress<LauncherInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migration);
        if (migration.Kind is not ContentWorkKind.FullRebuild
            and not ContentWorkKind.Overlay)
        {
            throw new LauncherInstallException(
                $"Content work kind {migration.Kind} cannot build an update.");
        }

        if (threads <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(threads),
                "Bake thread count must be positive.");
        }

        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool compatibilityMarkerPublished = false;
        try
        {
            await using InstallerTransactionLease lease =
                await InstallerTransactionLease.AcquireAsync(
                        _recordStore.DataDirectory,
                        cancellationToken,
                        TransactionLeaseContentionObservedForTest)
                    .ConfigureAwait(false);
            _contentStateStore.MarkClientCompatibilityPending();
            compatibilityMarkerPublished = true;
            LauncherInstallResult result = migration.Kind == ContentWorkKind.FullRebuild
                ? await InstallCoreAsync(
                        datDirectory,
                        threads,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await InstallOverlayCoreAsync(
                        datDirectory,
                        threads,
                        migration,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            LauncherInstallRecord gatedRecord = result.Record with
            {
                RequiresClientCompatibilityConfirmation = true,
            };
            _verifiedRecord = gatedRecord;
            return new LauncherInstallResult(gatedRecord);
        }
        catch
        {
            if (compatibilityMarkerPublished)
            {
                _contentStateStore.ClearClientCompatibilityPending();
            }

            throw;
        }
        finally
        {
            _installGate.Release();
        }
    }

    private async Task<LauncherInstallResult> InstallOverlayCoreAsync(
        string datDirectory,
        int threads,
        ContentMigrationPlan migration,
        IProgress<LauncherInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (migration.TargetRecipeVersion
            != LauncherInstallRecordStore.CurrentBakeToolVersion)
        {
            throw new LauncherInstallException(
                $"Overlay target recipe {migration.TargetRecipeVersion} does not "
                + $"match launcher recipe "
                + $"{LauncherInstallRecordStore.CurrentBakeToolVersion}.");
        }

        if (migration.EffectiveDatIds.Count == 0
            && migration.EffectiveLandblocks.Count == 0)
        {
            throw new LauncherInstallException(
                "An overlay migration must name at least one DAT id or landblock.");
        }

        DatDirectoryValidation validation = _datDirectories.Validate(datDirectory);
        if (!validation.IsValid)
        {
            throw new LauncherInstallException(
                validation.Message + FormatMissing(validation.MissingFileNames));
        }

        if (!File.Exists(_bakeExecutablePath))
        {
            throw new LauncherInstallException(
                $"The co-deployed bake tool is missing at '{_bakeExecutablePath}'.");
        }

        InstallRecordVerification baseVerification =
            await RecoverExistingUnderPublicationGuardAsync(cancellationToken)
                .ConfigureAwait(false);
        LauncherInstallRecord baseRecord = baseVerification.Record
            ?? throw new LauncherInstallException(
                "A verified base pak is required before building an overlay.");
        if (baseRecord.BakeToolVersion != migration.FromRecipeVersion)
        {
            throw new LauncherInstallException(
                $"The overlay plan starts at recipe {migration.FromRecipeVersion}, "
                + $"but the installed base is recipe {baseRecord.BakeToolVersion}.");
        }

        (LauncherContentState? priorState, string? priorStateError) =
            await _contentStateStore.LoadAsync(
                    baseRecord,
                    forceFullVerification: false,
                    cancellationToken)
                .ConfigureAwait(false);
        if (priorStateError is not null)
        {
            throw new LauncherInstallException(priorStateError);
        }

        string candidatePath = _contentStateStore.OverlayCandidatePath;
        LauncherInstallRecordStore.TryDelete(candidatePath);
        string? publicationNonce = null;
        string? publishedOverlayPath = null;
        bool statePublished = false;
        var parser = new BakeProgressJsonlParser();
        var protocol = new BakeProgressProtocol();

        void Observe(BakeProgressEvent progressEvent)
        {
            bool accepted = protocol.Observe(progressEvent);
            switch (progressEvent)
            {
                case BakeWorkProgressEvent value when accepted:
                    LauncherInstallPhase phase = value.Phase == "collision"
                        ? LauncherInstallPhase.BakingCollision
                        : LauncherInstallPhase.BakingMeshes;
                    Report(
                        progress,
                        phase,
                        $"Building world-data overlay: {value.Completed:N0}/"
                        + $"{value.Total:N0}; failures: {value.Failures:N0}",
                        value.Completed,
                        value.Total,
                        value.Failures,
                        value.EtaSeconds);
                    break;
                case BakeErrorEvent value when accepted:
                    Report(progress, LauncherInstallPhase.Failed, value.Message);
                    break;
                case MalformedBakeProgressEvent value:
                    Report(progress, LauncherInstallPhase.Failed, value.Reason);
                    break;
            }
        }

        try
        {
            Report(
                progress,
                LauncherInstallPhase.PreparingOutput,
                "Preparing a small filtered overlay beside active content...");
            publicationNonce = BakePublicationGuardPaths.CreateNonce();
            await using (
                BakePublicationGuardContract.PublicationLease publication =
                    await BakePublicationGuardContract.AcquireAsync(
                            candidatePath,
                            cancellationToken)
                        .ConfigureAwait(false))
            {
                BakePublicationGuardContract.Authorize(
                    candidatePath,
                    publicationNonce,
                    publication);
            }

            var request = new BakeProcessRequest(
                _bakeExecutablePath,
                validation.Directory,
                candidatePath,
                threads,
                publicationNonce,
                migration.EffectiveDatIds,
                migration.EffectiveLandblocks);
            BakeProcessResult processResult = await _processRunner.RunAsync(
                    request,
                    chunk =>
                    {
                        foreach (BakeProgressEvent value in parser.Append(chunk))
                        {
                            Observe(value);
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (BakeProgressEvent value in parser.Complete())
            {
                Observe(value);
            }

            protocol.CompleteInput();
            cancellationToken.ThrowIfCancellationRequested();
            if (protocol.Violation is not null)
            {
                throw new LauncherInstallException(protocol.Violation);
            }

            if (processResult.ExitCode != 0)
            {
                throw new LauncherInstallException(
                    BuildChildFailure(
                        processResult.ExitCode,
                        protocol.Error?.Message,
                        processResult.StandardError));
            }

            if (protocol.Error is not null)
            {
                throw new LauncherInstallException(protocol.Error.Message);
            }

            BakeStartedEvent started = protocol.Started
                ?? throw new LauncherInstallException(
                    "The bake protocol did not report a started event.");
            BakeCompletedEvent completed = protocol.Completed
                ?? throw new LauncherInstallException(
                    "The bake protocol did not report a completed event.");
            if (started.BakeToolVersion != migration.TargetRecipeVersion
                || completed.BakeToolVersion != migration.TargetRecipeVersion
                || completed.Failures != 0)
            {
                throw new LauncherInstallException(
                    "The filtered bake did not complete with the requested recipe.");
            }

            if (!File.Exists(candidatePath))
            {
                throw new LauncherInstallException(
                    "The filtered bake did not publish an overlay candidate.");
            }

            long size = new FileInfo(candidatePath).Length;
            if (size <= 0 || size != completed.OutputBytes)
            {
                throw new LauncherInstallException(
                    "The overlay candidate size does not match bake completion.");
            }

            Report(
                progress,
                LauncherInstallPhase.VerifyingPackage,
                "Verifying the small world-data overlay...");
            string sha256 = await _computeSha256(candidatePath, cancellationToken)
                .ConfigureAwait(false);
            string fileName = $"acdream-update-{migration.TargetRecipeVersion}-"
                + $"{sha256[..12].ToLowerInvariant()}.pak";
            var overlay = new LauncherContentOverlay(
                fileName,
                sha256,
                size,
                migration.TargetRecipeVersion);
            string? candidateError = _contentStateStore.ValidateCandidate(
                baseRecord,
                candidatePath,
                overlay);
            if (candidateError is not null)
            {
                throw new LauncherInstallException(candidateError);
            }

            publishedOverlayPath = _contentStateStore.GetOverlayPath(overlay);
            File.Move(candidatePath, publishedOverlayPath, overwrite: true);
            await FinalizeSuccessfulPublicationAsync(
                    candidatePath,
                    backupPath: candidatePath + ".unused",
                    publicationNonce)
                .ConfigureAwait(false);
            var state = new LauncherContentState(
                LauncherContentState.CurrentSchemaVersion,
                baseRecord.PreparedAssetSha256,
                migration.TargetRecipeVersion,
                overlay);
            Report(
                progress,
                LauncherInstallPhase.SavingRecord,
                "Activating the verified world-data overlay...");
            await _contentStateStore.SaveAtomicallyAsync(
                    baseRecord,
                    state,
                    cancellationToken)
                .ConfigureAwait(false);
            statePublished = true;

            if (priorState?.Overlay is not null)
            {
                string priorPath = _contentStateStore.GetOverlayPath(
                    priorState.Overlay);
                if (!PathsEqual(priorPath, publishedOverlayPath))
                {
                    LauncherInstallRecordStore.TryDelete(priorPath);
                }
            }

            var resolvedRecord = baseRecord with
            {
                PreparedAssetOverlayPath = publishedOverlayPath,
                EffectiveBakeToolVersion = migration.TargetRecipeVersion,
            };
            _verifiedRecord = resolvedRecord;
            Report(
                progress,
                LauncherInstallPhase.Completed,
                "World data overlay installed and verified.",
                1,
                1);
            return new LauncherInstallResult(resolvedRecord);
        }
        catch (OperationCanceledException)
        {
            await FinalizeOverlayFailureAsync(
                    candidatePath,
                    publishedOverlayPath,
                    statePublished,
                    publicationNonce)
                .ConfigureAwait(false);
            Report(
                progress,
                LauncherInstallPhase.Cancelled,
                "World data update cancelled; active content was preserved.");
            throw;
        }
        catch (Exception ex)
        {
            await FinalizeOverlayFailureAsync(
                    candidatePath,
                    publishedOverlayPath,
                    statePublished,
                    publicationNonce)
                .ConfigureAwait(false);
            Report(
                progress,
                LauncherInstallPhase.Failed,
                $"World data update failed: {ex.Message}");
            if (ex is LauncherInstallException)
            {
                throw;
            }

            throw new LauncherInstallException("World data update failed.", ex);
        }
    }

    private async Task<InstallRecordVerification> ResolveContentStateAsync(
        InstallRecordVerification baseVerification,
        bool forceFullVerification,
        CancellationToken cancellationToken)
    {
        LauncherInstallRecord? record = baseVerification.Record;
        if (record is null)
        {
            return baseVerification;
        }

        if (baseVerification.IsVerified
            && record.BakeToolVersion
                == LauncherInstallRecordStore.CurrentBakeToolVersion)
        {
            return baseVerification;
        }

        (LauncherContentState? state, string? error) =
            await _contentStateStore.LoadAsync(
                    record,
                    forceFullVerification,
                    cancellationToken)
                .ConfigureAwait(false);
        if (error is not null)
        {
            if (!forceFullVerification
                && baseVerification.RequiresContentUpdate)
            {
                return baseVerification with
                {
                    Status = baseVerification.Status
                        + " The previous overlay was ignored because it is invalid.",
                };
            }

            return new InstallRecordVerification(
                InstallRecordVerificationState.Invalid,
                null,
                error);
        }

        if (state?.Overlay is not null)
        {
            if (state.EffectiveRecipeVersion
                > LauncherInstallRecordStore.CurrentBakeToolVersion)
            {
                return new InstallRecordVerification(
                    InstallRecordVerificationState.Invalid,
                    null,
                    $"Prepared content recipe {state.EffectiveRecipeVersion} "
                    + $"does not match required recipe "
                    + $"{LauncherInstallRecordStore.CurrentBakeToolVersion}.");
            }

            if (state.EffectiveRecipeVersion
                < LauncherInstallRecordStore.CurrentBakeToolVersion)
            {
                ContentMigrationPlan migration;
                try
                {
                    migration = ContentMigrationCatalog.Resolve(
                        record.BakeToolVersion,
                        LauncherInstallRecordStore.CurrentBakeToolVersion);
                }
                catch (InvalidOperationException ex)
                {
                    return new InstallRecordVerification(
                        InstallRecordVerificationState.Invalid,
                        null,
                        ex.Message);
                }

                return new InstallRecordVerification(
                    InstallRecordVerificationState.ContentUpdateRequired,
                    record,
                    $"World data update required: {migration.Reason}.",
                    migration);
            }

            return new InstallRecordVerification(
                InstallRecordVerificationState.Verified,
                record with
                {
                    PreparedAssetOverlayPath =
                        _contentStateStore.GetOverlayPath(state.Overlay),
                    EffectiveBakeToolVersion = state.EffectiveRecipeVersion,
                },
                "Base and overlay client content verified.");
        }

        return baseVerification;
    }

    private static async Task FinalizeSuccessfulPublicationAsync(
        string outputPath,
        string backupPath,
        string publicationNonce)
    {
        await using BakePublicationGuardContract.PublicationLease publication =
            await BakePublicationGuardContract.AcquireAsync(
                    outputPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
        BakePublicationGuardContract.Invalidate(
            outputPath,
            publication,
            publicationNonce);
        LauncherInstallRecordStore.TryDelete(backupPath);
        BakeOutputStagingContract.DeleteOwnedStagingFiles(outputPath);
    }

    private static async Task FinalizeFailedPublicationAsync(
        string publicationPath,
        string outputPath,
        string backupPath,
        bool previousPreserved,
        bool canonicalReplaced,
        string? publicationNonce)
    {
        await using BakePublicationGuardContract.PublicationLease publication =
            await BakePublicationGuardContract.AcquireAsync(
                    publicationPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
        BakePublicationGuardContract.Invalidate(
            publicationPath,
            publication,
            publicationNonce);
        if (canonicalReplaced)
        {
            LauncherInstallRecordStore.TryDelete(outputPath);
        }

        if (previousPreserved && File.Exists(backupPath))
        {
            File.Move(backupPath, outputPath, overwrite: true);
        }

        LauncherInstallRecordStore.TryDelete(publicationPath);
        LauncherInstallRecordStore.TryDelete(backupPath);
        BakeOutputStagingContract.DeleteOwnedStagingFiles(publicationPath);
    }

    private static async Task FinalizeOverlayFailureAsync(
        string candidatePath,
        string? publishedOverlayPath,
        bool statePublished,
        string? publicationNonce)
    {
        await using BakePublicationGuardContract.PublicationLease publication =
            await BakePublicationGuardContract.AcquireAsync(
                    candidatePath,
                    CancellationToken.None)
                .ConfigureAwait(false);
        BakePublicationGuardContract.Invalidate(
            candidatePath,
            publication,
            publicationNonce);
        LauncherInstallRecordStore.TryDelete(candidatePath);
        BakeOutputStagingContract.DeleteOwnedStagingFiles(candidatePath);
        if (!statePublished && publishedOverlayPath is not null)
        {
            LauncherInstallRecordStore.TryDelete(publishedOverlayPath);
        }
    }

    private bool PreservePreviousPackage(string outputPath, string backupPath)
    {
        LauncherInstallRecord? previous = _verifiedRecord;
        if (previous is null
            || !PathsEqual(previous.PreparedAssetPath, outputPath)
            || !File.Exists(outputPath))
        {
            return false;
        }

        File.Move(outputPath, backupPath, overwrite: true);
        return true;
    }

    internal static string GetFullRebuildCandidatePath(string outputPath) =>
        outputPath + ".candidate";

    private static long GetAvailableFreeSpace(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException(
                $"Path '{fullPath}' has no filesystem root.",
                nameof(path));
        return new DriveInfo(root).AvailableFreeSpace;
    }

    private static string BuildChildFailure(
        int exitCode,
        string? jsonError,
        string standardError)
    {
        string detail = !string.IsNullOrWhiteSpace(jsonError)
            ? jsonError
            : standardError.Trim();
        return detail.Length == 0
            ? $"The bake tool exited with code {exitCode}."
            : $"The bake tool exited with code {exitCode}: {detail}";
    }

    private static void Report(
        IProgress<LauncherInstallProgress>? progress,
        LauncherInstallPhase phase,
        string status,
        long completed = 0,
        long total = 0,
        int failures = 0,
        double etaSeconds = 0) =>
        progress?.Report(new LauncherInstallProgress(
            phase,
            status,
            completed,
            total,
            failures,
            etaSeconds));

    private static string FormatMissing(IReadOnlyList<string> missing) =>
        missing.Count == 0
            ? string.Empty
            : " Missing: " + string.Join(", ", missing) + ".";

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
