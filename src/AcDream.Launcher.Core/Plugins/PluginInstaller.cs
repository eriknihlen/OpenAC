using System.Text;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Plugins;

public sealed record PluginInstallResult(string Id, string Version, bool WasUpdate);

/// <summary>Install, update, remove and recovery for launcher-managed plugins, following the plan's
/// Install/update, Remove and Recovery pipelines (L-300, L-306, L-307, L-308, L-309, L-310). Every
/// write under <see cref="ApplicationPathSet.PluginsDirectory"/> happens under
/// <see cref="UpdateSessionBarrier.TryAcquireExclusive"/>. Install never writes a character's plugin
/// list; that write goes through <see cref="AcDream.Launcher.Core.Orchestration.ILauncherOrchestrator.UpdateCharacterSettings"/>
/// instead (L-300).</summary>
public sealed class PluginInstaller
{
    /// <summary>The refusal shown when a running session (or another update) holds the barrier.</summary>
    public const string SessionLeaseRefusal =
        "Close all OpenAC sessions to install or update plugins.";

    /// <summary>The install-time caps from the plan's shared contract (Release contract, "Caps").
    /// The one place they're set, so the zip download cap and the extraction limits it feeds can't
    /// drift apart.</summary>
    private static class ContractLimits
    {
        public const long MaximumZipBytes = 64L * 1024 * 1024;

        public static readonly SafeZipExtractionLimits Extraction = new(
            MaximumEntries: 2_000,
            MaximumEntryBytes: 64L * 1024 * 1024,
            MaximumTotalBytes: 256L * 1024 * 1024,
            MaximumCompressionRatio: 200,
            MaximumRelativePathLength: 512);
    }

    private readonly ApplicationPathSet _paths;
    private readonly PluginReleaseClient _releaseClient;
    private readonly VerifiedArtifactDownloader _downloader;
    private readonly SafeZipExtractor _extractor;
    private readonly InstalledPluginRecordStore _recordStore;
    private readonly PluginInventory _inventory;
    private readonly UpdateSessionBarrier _barrier;

    public PluginInstaller(
        ApplicationPathSet paths,
        HttpClient httpClient,
        InstalledPluginRecordStore recordStore,
        PluginInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(httpClient);
        _paths = paths;
        _releaseClient = new PluginReleaseClient(httpClient);
        _downloader = new VerifiedArtifactDownloader(httpClient);
        _extractor = new SafeZipExtractor(ContractLimits.Extraction, ignoreDeclaredModes: true);
        _recordStore = recordStore ?? throw new ArgumentNullException(nameof(recordStore));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _barrier = new UpdateSessionBarrier(paths.DataDirectory);
    }

    /// <summary>Install a new plugin or update an already-managed one from the same repo. The caller
    /// resolves <paramref name="repo"/> itself, from the catalog or a typed
    /// <c>github.com/owner/name</c> URL.</summary>
    public async Task<PluginInstallResult> InstallOrUpdateAsync(
        string repo,
        PluginCatalog? catalog,
        ClientVersionResolution? clientResolution,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        PluginReleaseFetchResult manifestFetch = await _releaseClient.FetchDocumentAsync(
                GitHubReleaseLocator.LatestAsset(repo, "plugin.json"),
                cancellationToken)
            .ConfigureAwait(false);
        RequireSuccess(manifestFetch);
        PluginReleaseDocument manifestDocument = manifestFetch.Document!;

        LauncherPluginManifest manifest;
        try
        {
            manifest = LauncherPluginManifest.Parse(
                Encoding.UTF8.GetString(manifestDocument.Content));
            manifest.ValidateForInstall();
        }
        catch (LauncherPluginManifestException ex)
        {
            throw new LauncherUpdateException($"The plugin manifest is invalid: {ex.Message}", ex);
        }

        if (manifestDocument.Tag is not { } tag || !manifest.MatchesTag(tag))
        {
            throw new LauncherUpdateException(
                $"The release tag does not match plugin version {manifest.Version}.");
        }

        LauncherVersion pluginVersion = LauncherVersion.Parse(manifest.Version);
        string? blockReason = PluginInventory.FindBlockReason(catalog, manifest.Id, pluginVersion);
        if (blockReason is not null)
        {
            throw new LauncherUpdateException($"'{manifest.Id}' is blocked: {blockReason}");
        }

        string? incompatibility = PluginInventory.EvaluateVersionCompatibility(
            manifest,
            clientResolution?.Version);
        // "Client not installed" is informational, not a refusal (L-303): nothing can run yet
        // to be incompatible with, so blocking here would only stop the very first install.
        if (incompatibility is not null
            && !string.Equals(
                incompatibility,
                LauncherPluginCompatibility.ClientNotInstalled,
                StringComparison.Ordinal))
        {
            throw new LauncherUpdateException($"'{manifest.Id}' {incompatibility}.");
        }

        InstalledPluginRecord? existingRecord = _recordStore.Find(manifest.Id);
        bool isUpdate = existingRecord is not null
            && string.Equals(existingRecord.Repo, repo, StringComparison.Ordinal);

        if (existingRecord is not null && !isUpdate)
        {
            throw new LauncherUpdateException(
                $"'{manifest.Id}' is already installed from '{existingRecord.Repo}'.");
        }

        if (!isUpdate)
        {
            InstalledPluginInfo? otherSource = _inventory.Find(
                manifest.Id,
                clientResolution,
                catalog);
            if (otherSource is not null)
            {
                throw new LauncherUpdateException(
                    $"'{manifest.Id}' is already present as a "
                    + $"{DescribeSource(otherSource.Source)} plugin.");
            }
        }

        string targetDirectory = Path.Combine(_paths.PluginsDirectory, manifest.Id);
        if (existingRecord is null && Directory.Exists(targetDirectory))
        {
            // The full path stays out of the player-facing message; the inner exception keeps it
            // for diagnostics.
            throw new LauncherUpdateException(
                $"A folder named {manifest.Id} is already in your plugins folder, and the "
                + "launcher didn't install it. Move or delete that folder, then try again.",
                new LauncherUpdateException(
                    $"'{targetDirectory}' already exists and is not a launcher-managed plugin."));
        }

        if (isUpdate)
        {
            LauncherVersion currentVersion = existingRecord!.Version is { } current
                ? LauncherVersion.Parse(current)
                : throw new LauncherUpdateException(
                    $"'{manifest.Id}' has no confirmed installed version to update. "
                    + "Restart the launcher to recover it first.");
            if (pluginVersion <= currentVersion)
            {
                throw new LauncherUpdateException(
                    $"'{manifest.Id}' {pluginVersion} is not newer than the installed "
                    + $"{currentVersion}.");
            }
        }

        string zipName = $"{manifest.Id}-{manifest.Version}.zip";
        PluginReleaseFetchResult shaFetch = await _releaseClient.FetchDocumentAsync(
                GitHubReleaseLocator.TaggedAsset(repo, tag, zipName + ".sha256"),
                cancellationToken)
            .ConfigureAwait(false);
        RequireSuccess(shaFetch);
        PluginSha256File shaFile = PluginSha256File.Parse(
            Encoding.UTF8.GetString(shaFetch.Document!.Content));
        shaFile.RequireMatches(zipName);

        string zipPath = Path.Combine(
            _paths.CacheDirectory,
            "plugin-downloads",
            $"{manifest.Id}-{Guid.NewGuid():N}.zip");
        string stagingDirectory = Path.Combine(
            _paths.PluginsDirectory,
            ".staging",
            $"{manifest.Id}-{Guid.NewGuid():N}");
        try
        {
            _ = await _downloader.DownloadAsync(
                    GitHubReleaseLocator.TaggedAsset(repo, tag, zipName),
                    shaFile.Sha256,
                    ContractLimits.MaximumZipBytes,
                    zipPath,
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<ExtractedFileRecord> extracted = await _extractor.ExtractAsync(
                    zipPath,
                    stagingDirectory,
                    executableNames: null,
                    cancellationToken)
                .ConfigureAwait(false);
            PluginContentPolicy.Validate(extracted, manifest.EntryDll);

            byte[] zipManifestBytes = await File.ReadAllBytesAsync(
                    Path.Combine(stagingDirectory, "plugin.json"),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!zipManifestBytes.AsSpan().SequenceEqual(manifestDocument.Content))
            {
                throw new LauncherUpdateException(
                    "The plugin archive's plugin.json does not match the published release.");
            }

            if (!_barrier.TryAcquireExclusive(out UpdateSessionBarrier.ExclusiveLease? lease))
            {
                throw new LauncherUpdateException(SessionLeaseRefusal);
            }

            using (lease)
            {
                SwapIntoPlace(
                    manifest.Id,
                    repo,
                    catalog,
                    manifest.Version,
                    tag,
                    shaFile.Sha256,
                    existingRecord,
                    stagingDirectory,
                    targetDirectory);
            }

            return new PluginInstallResult(manifest.Id, manifest.Version, isUpdate);
        }
        finally
        {
            VerifiedArtifactDownloader.TryDelete(zipPath);
            SafeZipExtractor.TryDeleteDirectory(stagingDirectory);
            TryDeleteIfEmpty(Path.Combine(_paths.PluginsDirectory, ".staging"));
        }
    }

    /// <summary>Removes a launcher-managed plugin. <paramref name="deleteStorage"/> also deletes the
    /// plugin's own subtree under <c>ConfigDirectory/plugins/&lt;id&gt;</c>
    /// (<c>ScopedPluginHost</c>'s scope), never the shared root and never Vtank's own profile
    /// directory.</summary>
    public void Remove(string id, bool deleteStorage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _ = _recordStore.Find(id)
            ?? throw new LauncherUpdateException($"'{id}' is not a launcher-managed plugin.");

        if (!_barrier.TryAcquireExclusive(out UpdateSessionBarrier.ExclusiveLease? lease))
        {
            throw new LauncherUpdateException(SessionLeaseRefusal);
        }

        using (lease)
        {
            string targetDirectory = Path.Combine(_paths.PluginsDirectory, id);
            if (Directory.Exists(targetDirectory))
            {
                string trashDirectory = CreateTrashPath(id);
                Directory.Move(targetDirectory, trashDirectory);
                SafeZipExtractor.TryDeleteDirectory(trashDirectory);
            }

            TryDeleteIfEmpty(Path.Combine(_paths.PluginsDirectory, ".trash"));

            _recordStore.Records.RemoveAll(record =>
                string.Equals(record.Id, id, StringComparison.OrdinalIgnoreCase));
            _recordStore.Save();

            if (deleteStorage)
            {
                SafeZipExtractor.TryDeleteDirectory(
                    Path.Combine(_paths.ConfigDirectory, "plugins", id));
            }
        }
    }

    /// <summary>The launch-time Recovery pipeline: reclaims staging/downloads, restores or discards
    /// <c>.trash</c>, and reconciles every <c>pending</c> record. Skips this launch (does nothing) if
    /// the exclusive lease is already held.</summary>
    public void Recover()
    {
        if (!_barrier.TryAcquireExclusive(out UpdateSessionBarrier.ExclusiveLease? lease))
        {
            return;
        }

        using (lease)
        {
            string stagingRoot = Path.Combine(_paths.PluginsDirectory, ".staging");
            if (Directory.Exists(stagingRoot))
            {
                foreach (string directory in Directory.EnumerateDirectories(stagingRoot))
                {
                    SafeZipExtractor.TryDeleteDirectory(directory);
                }

                TryDeleteIfEmpty(stagingRoot);
            }

            string downloadsRoot = Path.Combine(_paths.CacheDirectory, "plugin-downloads");
            if (Directory.Exists(downloadsRoot))
            {
                foreach (string file in Directory.EnumerateFiles(downloadsRoot))
                {
                    VerifiedArtifactDownloader.TryDelete(file);
                }
            }

            string trashRoot = Path.Combine(_paths.PluginsDirectory, ".trash");
            if (Directory.Exists(trashRoot))
            {
                foreach (string trashDirectory in Directory.EnumerateDirectories(trashRoot))
                {
                    string id = IdFromTrashPath(trashDirectory);
                    string original = Path.Combine(_paths.PluginsDirectory, id);
                    if (!Directory.Exists(original))
                    {
                        Directory.Move(trashDirectory, original);
                    }
                    else
                    {
                        SafeZipExtractor.TryDeleteDirectory(trashDirectory);
                    }
                }

                TryDeleteIfEmpty(trashRoot);
            }

            bool changed = ReconcilePendingRecords();
            changed |= DropRecordsWithMissingFolders();
            if (changed)
            {
                _recordStore.Save();
            }
        }
    }

    private bool ReconcilePendingRecords()
    {
        bool changed = false;
        foreach (InstalledPluginRecord record in _recordStore.Records.ToArray())
        {
            if (record.Pending is not { } pending)
            {
                continue;
            }

            string manifestPath = Path.Combine(_paths.PluginsDirectory, record.Id, "plugin.json");
            string? actualVersion = TryReadManifestVersion(manifestPath);
            if (actualVersion is not null
                && string.Equals(actualVersion, pending.Version, StringComparison.Ordinal))
            {
                Upsert(record with
                {
                    Version = pending.Version,
                    Tag = pending.Tag,
                    ZipSha256 = pending.ZipSha256,
                    Pending = null,
                });
            }
            else if (record.Version is not null)
            {
                Upsert(record with { Pending = null });
            }
            else
            {
                _recordStore.Records.Remove(record);
            }

            changed = true;
        }

        return changed;
    }

    private bool DropRecordsWithMissingFolders()
    {
        bool changed = false;
        foreach (InstalledPluginRecord record in _recordStore.Records.ToArray())
        {
            if (record.Pending is null
                && !Directory.Exists(Path.Combine(_paths.PluginsDirectory, record.Id)))
            {
                _recordStore.Records.Remove(record);
                changed = true;
            }
        }

        return changed;
    }

    private void SwapIntoPlace(
        string id,
        string repo,
        PluginCatalog? catalog,
        string newVersion,
        string newTag,
        string newZipSha256,
        InstalledPluginRecord? existingRecord,
        string stagingDirectory,
        string targetDirectory)
    {
        var pending = new PendingPluginInstall(newVersion, newTag, newZipSha256);
        InstalledPluginRecord pendingRecord = existingRecord is null
            ? new InstalledPluginRecord(
                id,
                repo,
                DetermineSource(catalog, id, repo),
                Version: null,
                Tag: null,
                ZipSha256: null,
                InstalledAt: DateTimeOffset.UtcNow,
                Pending: pending)
            : existingRecord with { Pending = pending };

        Upsert(pendingRecord);
        _recordStore.Save();

        Directory.CreateDirectory(_paths.PluginsDirectory);
        bool hadExistingFolder = Directory.Exists(targetDirectory);
        string trashDirectory = CreateTrashPath(id);
        if (hadExistingFolder)
        {
            Directory.Move(targetDirectory, trashDirectory);
        }

        try
        {
            Directory.Move(stagingDirectory, targetDirectory);
        }
        catch
        {
            if (hadExistingFolder)
            {
                Directory.Move(trashDirectory, targetDirectory);
            }

            throw;
        }

        Upsert(pendingRecord with
        {
            Version = newVersion,
            Tag = newTag,
            ZipSha256 = newZipSha256,
            Pending = null,
        });
        _recordStore.Save();

        if (hadExistingFolder)
        {
            SafeZipExtractor.TryDeleteDirectory(trashDirectory);
        }

        TryDeleteIfEmpty(Path.Combine(_paths.PluginsDirectory, ".trash"));
    }

    private void Upsert(InstalledPluginRecord record)
    {
        int index = _recordStore.Records.FindIndex(
            candidate => string.Equals(candidate.Id, record.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _recordStore.Records[index] = record;
        }
        else
        {
            _recordStore.Records.Add(record);
        }
    }

    private string CreateTrashPath(string id)
    {
        string trashRoot = Path.Combine(_paths.PluginsDirectory, ".trash");
        Directory.CreateDirectory(trashRoot);
        return Path.Combine(trashRoot, $"{id}-{Guid.NewGuid():N}");
    }

    /// <summary>Reclaims <c>.staging</c>/<c>.trash</c> once their last entry is gone, never a
    /// directory something else still has files in.</summary>
    private static void TryDeleteIfEmpty(string directory)
    {
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            SafeZipExtractor.TryDeleteDirectory(directory);
        }
    }

    private static string IdFromTrashPath(string trashDirectory)
    {
        string name = Path.GetFileName(trashDirectory);
        int lastDash = name.LastIndexOf('-');
        return lastDash > 0 ? name[..lastDash] : name;
    }

    private static PluginInstallSource DetermineSource(PluginCatalog? catalog, string id, string repo) =>
        catalog is not null
        && catalog.Plugins.Any(entry =>
            string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Repo, repo, StringComparison.Ordinal))
            ? PluginInstallSource.Listed
            : PluginInstallSource.Unlisted;

    private static string? TryReadManifestVersion(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            return LauncherPluginManifest.Parse(File.ReadAllText(manifestPath)).Version;
        }
        catch (LauncherPluginManifestException)
        {
            return null;
        }
    }

    private static void RequireSuccess(PluginReleaseFetchResult result)
    {
        switch (result.Status)
        {
            case PluginReleaseFetchStatus.RateLimited:
                throw new LauncherUpdateException("GitHub is rate limiting; try later.");
            case PluginReleaseFetchStatus.Unavailable:
                throw new LauncherUpdateException("The plugin release is unavailable.");
        }
    }

    private static string DescribeSource(InstalledPluginSource source) => source switch
    {
        InstalledPluginSource.Managed => "launcher-installed",
        InstalledPluginSource.Manual => "manually installed",
        InstalledPluginSource.Bundled => "client-bundled",
        _ => source.ToString(),
    };
}
