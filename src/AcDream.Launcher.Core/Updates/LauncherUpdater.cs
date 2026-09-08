namespace AcDream.Launcher.Core.Updates;

public enum LauncherUpdatePhase
{
    Idle,
    Checking,
    DownloadingClient,
    ExtractingClient,
    ActivatingClient,
    DownloadingLauncher,
    StagingLauncher,
    RollingBack,
    Completed,
    Cancelled,
    Failed,
}

public sealed record LauncherUpdateProgress(
    LauncherUpdatePhase Phase,
    string Status,
    long Completed = 0,
    long Total = 0)
{
    public double Percent => Total <= 0
        ? 0
        : Math.Clamp(Completed * 100d / Total, 0, 100);
}

public sealed record LauncherUpdateCheckResult(
    ReleaseManifest Manifest,
    string Rid,
    LauncherVersion LauncherVersion,
    LauncherVersion? InstalledClientVersion,
    bool IsClientUpdateAvailable,
    bool IsLauncherUpdateAvailable,
    bool IsLauncherMinimumSatisfied,
    string Status);

public interface ILauncherUpdater
{
    ClientVersionResolution CurrentClient { get; }

    Task<ClientVersionResolution> InitializeAsync(
        CancellationToken cancellationToken = default);

    Task<LauncherUpdateCheckResult> CheckAsync(
        CancellationToken cancellationToken = default);

    Task<ClientVersionResolution> InstallClientAsync(
        LauncherUpdateCheckResult check,
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SelfUpdateStageResult> StageLauncherAsync(
        LauncherUpdateCheckResult check,
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ClientVersionResolution> RollbackClientAsync(
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class LauncherUpdater : ILauncherUpdater
{
    private readonly IReleaseManifestClient _manifestClient;
    private readonly ClientVersionStore _versions;
    private readonly LauncherSelfUpdateManager _selfUpdates;
    private readonly VerifiedArtifactDownloader _downloader;
    private readonly SafeZipExtractor _extractor;
    private readonly LauncherVersion _launcherVersion;
    private readonly string _rid;
    private readonly string _launcherTargetDirectory;
    private readonly Func<bool> _hasRunningSessions;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public LauncherUpdater(
        IReleaseManifestClient manifestClient,
        HttpClient httpClient,
        ClientVersionStore versions,
        LauncherSelfUpdateManager selfUpdates,
        LauncherVersion launcherVersion,
        string rid,
        string launcherTargetDirectory,
        Func<bool>? hasRunningSessions = null,
        SafeZipExtractor? extractor = null)
    {
        _manifestClient = manifestClient
            ?? throw new ArgumentNullException(nameof(manifestClient));
        _versions = versions ?? throw new ArgumentNullException(nameof(versions));
        _selfUpdates = selfUpdates ?? throw new ArgumentNullException(nameof(selfUpdates));
        _launcherVersion = launcherVersion
            ?? throw new ArgumentNullException(nameof(launcherVersion));
        if (!LauncherRuntimeIdentity.IsValidRid(rid))
        {
            throw new ArgumentException("RID is invalid.", nameof(rid));
        }

        _rid = rid;
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherTargetDirectory);
        _launcherTargetDirectory = Path.GetFullPath(launcherTargetDirectory);
        _hasRunningSessions = hasRunningSessions ?? (() => false);
        _downloader = new VerifiedArtifactDownloader(
            httpClient ?? throw new ArgumentNullException(nameof(httpClient)));
        _extractor = extractor ?? new SafeZipExtractor();
    }

    public ClientVersionResolution CurrentClient => _versions.CachedResolution;

    public Task<ClientVersionResolution> InitializeAsync(
        CancellationToken cancellationToken = default) =>
        _versions.LoadAndRecoverAsync(_rid, cancellationToken);

    public async Task<LauncherUpdateCheckResult> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ReleaseManifest manifest = await _manifestClient.FetchAsync(cancellationToken)
                .ConfigureAwait(false);
            _ = manifest.RequireClient(_rid);
            _ = manifest.RequireLauncher(_rid);
            ClientVersionResolution installed = _versions.CachedResolution;
            LauncherVersion? installedVersion = installed.IsVerified
                ? installed.Version
                : null;
            bool clientAvailable = installedVersion is null
                || manifest.Version > installedVersion;
            bool launcherAvailable = manifest.Version > _launcherVersion;
            bool minimumSatisfied = _launcherVersion >= manifest.MinimumLauncherVersion;
            string status = BuildCheckStatus(
                manifest,
                installedVersion,
                clientAvailable,
                launcherAvailable,
                minimumSatisfied);
            return new LauncherUpdateCheckResult(
                manifest,
                _rid,
                _launcherVersion,
                installedVersion,
                clientAvailable,
                launcherAvailable,
                minimumSatisfied,
                status);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ClientVersionResolution> InstallClientAsync(
        LauncherUpdateCheckResult check,
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        ValidateCheck(check);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RefuseRunningSessions();
            using UpdateSessionBarrier.ExclusiveLease lease =
                _versions.Barrier.AcquireExclusive();
            RefuseRunningSessions();
            ClientVersionResolution current = await _versions
                .LoadAndRecoverUnderLeaseAsync(_rid, cancellationToken)
                .ConfigureAwait(false);
            if (!check.IsLauncherMinimumSatisfied)
            {
                throw new LauncherUpdateException(
                    $"Client {check.Manifest.Version} requires launcher "
                    + $"{check.Manifest.MinimumLauncherVersion} or newer. "
                    + "Stage the launcher update first.");
            }

            if (current.IsVerified
                && current.Version is not null
                && current.Version >= check.Manifest.Version)
            {
                Report(
                    progress,
                    LauncherUpdatePhase.Completed,
                    $"Client {current.Version} is already current.",
                    1,
                    1);
                return current;
            }

            ReleaseArtifact artifact = check.Manifest.RequireClient(_rid);
            Guid transactionId = Guid.NewGuid();
            string staging = _versions.CreateClientStagingDirectory(transactionId);
            string archive = Path.Combine(
                _versions.AppDirectory,
                $".client-download-{transactionId:N}.zip");
            try
            {
                Report(
                    progress,
                    LauncherUpdatePhase.DownloadingClient,
                    $"Downloading client {check.Manifest.Version}...",
                    0,
                    artifact.Size);
                var downloadProgress = new ForwardProgress<ArtifactDownloadProgress>(value =>
                    Report(
                        progress,
                        LauncherUpdatePhase.DownloadingClient,
                        $"Downloading client {check.Manifest.Version}: "
                            + $"{value.BytesReceived:N0}/{value.TotalBytes:N0} bytes",
                        value.BytesReceived,
                        value.TotalBytes));
                _ = await _downloader.DownloadAsync(
                        artifact,
                        archive,
                        downloadProgress,
                        cancellationToken)
                    .ConfigureAwait(false);

                Report(
                    progress,
                    LauncherUpdatePhase.ExtractingClient,
                    "Verifying paths and extracting the client archive...");
                IReadOnlyList<ExtractedFileRecord> files = await _extractor.ExtractAsync(
                        archive,
                        staging,
                        cancellationToken)
                    .ConfigureAwait(false);
                Report(
                    progress,
                    LauncherUpdatePhase.ActivatingClient,
                    $"Atomically activating client {check.Manifest.Version}...");
                ClientVersionResolution result = await _versions
                    .PromoteAndActivateUnderLeaseAsync(
                        staging,
                        check.Manifest.Version,
                        _rid,
                        artifact,
                        files,
                        cancellationToken)
                    .ConfigureAwait(false);
                Report(
                    progress,
                    LauncherUpdatePhase.Completed,
                    $"Client {check.Manifest.Version} installed and activated.",
                    1,
                    1);
                return result;
            }
            catch (OperationCanceledException)
            {
                Report(
                    progress,
                    LauncherUpdatePhase.Cancelled,
                    "Client update cancelled; the active version was not changed.");
                throw;
            }
            catch (Exception ex)
            {
                Report(
                    progress,
                    LauncherUpdatePhase.Failed,
                    $"Client update failed: {ex.Message}");
                throw;
            }
            finally
            {
                VerifiedArtifactDownloader.TryDelete(archive);
                SafeZipExtractor.TryDeleteDirectory(staging);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<SelfUpdateStageResult> StageLauncherAsync(
        LauncherUpdateCheckResult check,
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        ValidateCheck(check);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RefuseRunningSessions();
            if (check.Manifest.Version <= _launcherVersion)
            {
                throw new LauncherUpdateException(
                    $"Launcher {_launcherVersion} is already current.");
            }

            Report(
                progress,
                LauncherUpdatePhase.DownloadingLauncher,
                $"Downloading launcher {check.Manifest.Version}...");
            var downloadProgress = new ForwardProgress<ArtifactDownloadProgress>(value =>
                Report(
                    progress,
                    LauncherUpdatePhase.DownloadingLauncher,
                    $"Downloading launcher {check.Manifest.Version}: "
                        + $"{value.BytesReceived:N0}/{value.TotalBytes:N0} bytes",
                    value.BytesReceived,
                    value.TotalBytes));
            try
            {
                SelfUpdateStageResult result = await _selfUpdates.StageAsync(
                        check.Manifest,
                        _rid,
                        _launcherTargetDirectory,
                        downloadProgress,
                        cancellationToken)
                    .ConfigureAwait(false);
                Report(
                    progress,
                    LauncherUpdatePhase.StagingLauncher,
                    result.Status,
                    1,
                    1);
                return result;
            }
            catch (OperationCanceledException)
            {
                Report(
                    progress,
                    LauncherUpdatePhase.Cancelled,
                    "Launcher update staging cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                Report(
                    progress,
                    LauncherUpdatePhase.Failed,
                    $"Launcher update staging failed: {ex.Message}");
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ClientVersionResolution> RollbackClientAsync(
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RefuseRunningSessions();
            Report(
                progress,
                LauncherUpdatePhase.RollingBack,
                "Verifying and activating the previous client version...");
            ClientVersionResolution result = await _versions.RollbackAsync(
                    _rid,
                    cancellationToken)
                .ConfigureAwait(false);
            Report(
                progress,
                LauncherUpdatePhase.Completed,
                $"Rolled back to client {result.Version}.",
                1,
                1);
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void ValidateCheck(LauncherUpdateCheckResult check)
    {
        if (!string.Equals(check.Rid, _rid, StringComparison.Ordinal)
            || !check.LauncherVersion.Equals(_launcherVersion))
        {
            throw new LauncherUpdateException(
                "The update check belongs to a different launcher runtime.");
        }

        _ = check.Manifest.RequireClient(_rid);
        _ = check.Manifest.RequireLauncher(_rid);
    }

    private void RefuseRunningSessions()
    {
        if (_hasRunningSessions())
        {
            throw new LauncherUpdateException(
                "Stop every launcher session before installing or rolling back an update.");
        }
    }

    private static string BuildCheckStatus(
        ReleaseManifest manifest,
        LauncherVersion? installed,
        bool clientAvailable,
        bool launcherAvailable,
        bool minimumSatisfied)
    {
        if (!minimumSatisfied)
        {
            return $"Release {manifest.Version} requires launcher "
                + $"{manifest.MinimumLauncherVersion} or newer.";
        }

        if (clientAvailable && launcherAvailable)
        {
            return $"Client and launcher {manifest.Version} are available.";
        }

        if (clientAvailable)
        {
            return installed is null
                ? $"Client {manifest.Version} is available for installation."
                : $"Client update {installed} -> {manifest.Version} is available.";
        }

        if (launcherAvailable)
        {
            return $"Launcher {manifest.Version} is available.";
        }

        return "Client and launcher are up to date.";
    }

    private static void Report(
        IProgress<LauncherUpdateProgress>? progress,
        LauncherUpdatePhase phase,
        string status,
        long completed = 0,
        long total = 0) =>
        progress?.Report(new LauncherUpdateProgress(
            phase,
            status,
            completed,
            total));

    private sealed class ForwardProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
