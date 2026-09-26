using System.Text;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Plugins;

public sealed record PluginInstallResult(string Id, string Version, bool WasUpdate);

/// <summary>Install, update, remove and recovery for launcher-managed plugins, following the plan's
/// Install/update, Remove and Recovery pipelines. Install, update and a channel change run while
/// clients are playing: they hold the install's session lock shared, as a running client does, so a
/// client update or a move of the install cannot start underneath them, and they take the plugin
/// write lock so two launchers never write plugins at once. The new files are written into the
/// plugin's folder one at a time and the running clients load them. Remove and recovery still need
/// every session closed and take <see cref="UpdateSessionBarrier.TryAcquireExclusive"/>. Install
/// never writes a character's plugin list; that write goes through
/// <see cref="AcDream.Launcher.Core.Orchestration.ILauncherOrchestrator.UpdateCharacterSettings"/>
/// instead.</summary>
public sealed class PluginInstaller
{
    /// <summary>The refusal shown when removing a plugin while a running session (or an update)
    /// holds the barrier.</summary>
    public const string SessionLeaseRefusal =
        "Close all OpenAC sessions to remove plugins.";

    /// <summary>The refusal shown when an install, update or channel change finds a client update,
    /// a move of the install folder or a plugin removal already running.</summary>
    public const string UpdateInProgressRefusal =
        "A client update or another launcher task is running. Try again when it has finished.";

    /// <summary>The refusal shown when another launcher is already installing or updating a plugin.</summary>
    public const string PluginWriteBusyRefusal =
        "Another plugin install or update is running. Try again when it has finished.";

    /// <summary>The lock file, beside the session lock, that one plugin write at a time holds.</summary>
    public const string PluginWriteLockFileName = ".plugin-write.lock";

    /// <summary>The refusal shown when a manifest declares a capability vocabulary newer than this
    /// launcher knows. Says the launcher needs updating rather than that the manifest is invalid,
    /// since the player can act on the former.</summary>
    public const string CapabilityVocabularyRefusal =
        "This plugin needs a newer launcher than the one installed. Update the launcher, then try again.";

    /// <summary>The refusal shown when the caller supplies the capabilities the player reviewed and
    /// the release manifest declares a different set: the release changed between the install
    /// dialog opening and the install running.</summary>
    public const string CapabilitiesChangedRefusal =
        "This plugin changed since you reviewed it. Check it again before installing.";

    /// <summary>The refusal shown when a package ships its own top-level <c>files</c> folder.</summary>
    public const string PackagedFilesRefusal =
        "This plugin's package contains a top-level 'files' folder. That name is kept for "
        + "the plugin's own saved files, so the launcher will not install it.";

    /// <summary>The folder inside a plugin's folder that holds the player's files for it.</summary>
    private const string FilesFolderName = ApplicationPathSet.PluginFilesFolderName;

    /// <summary>The install-time caps from the plan's shared contract (Release contract, "Caps").
    /// The one place they're set, so the zip download cap and the extraction limits it feeds can't
    /// drift apart; <see cref="DirectInstallCheck"/> reuses the same extraction limits.</summary>
    internal static class ContractLimits
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

    /// <summary>
    /// Runs right after an update moved the old version's files aside and
    /// before the new ones go in; a test throws here to stand in for a write
    /// failing at that moment.
    /// </summary>
    internal Action? AfterOldCodeSetAside { get; set; }

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

    /// <summary>Install a new plugin or update an already-managed one from the same repo, at the
    /// exact <paramref name="tag"/> the caller already resolved (the update check, Discover's
    /// details, or Add from URL): what was offered is what installs, so this never re-resolves
    /// latest itself. The caller resolves <paramref name="repo"/> itself, from the catalog or a
    /// typed <c>github.com/owner/name</c> URL. <paramref name="displayedCapabilities"/>, when
    /// supplied, must match the release manifest's own capabilities (name and note,
    /// order-insensitive) or the install is refused: it is the consent the player actually saw,
    /// and the release can change under them between the dialog opening and this call
    /// running. <paramref name="channel"/>, when supplied, is the channel the player chose for this
    /// install (Discover's per-row picker) and wins over the version-inferred default below; omitted,
    /// every existing caller keeps today's inference unchanged.</summary>
    public async Task<PluginInstallResult> InstallOrUpdateAsync(
        string repo,
        string tag,
        PluginCatalog? catalog,
        ClientVersionResolution? clientResolution,
        IReadOnlyList<LauncherPluginCapabilityDeclaration>? displayedCapabilities = null,
        PluginReleaseChannel? channel = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        PluginReleaseFetchResult manifestFetch = await _releaseClient.FetchDocumentAsync(
                GitHubReleaseLocator.TaggedAsset(repo, tag, "plugin.json"),
                cancellationToken)
            .ConfigureAwait(false);
        RequireSuccess(manifestFetch);
        PluginReleaseDocument manifestDocument = manifestFetch.Document!;

        LauncherPluginManifest manifest;
        try
        {
            // Every read of an installed plugin.json drops a byte-order mark, so the published
            // copy is read the same way; otherwise a file the checker accepts fails to install.
            ReadOnlySpan<byte> manifestBytes = manifestDocument.Content;
            if (manifestBytes.StartsWith(Encoding.UTF8.Preamble))
                manifestBytes = manifestBytes[Encoding.UTF8.Preamble.Length..];

            manifest = LauncherPluginManifest.Parse(Encoding.UTF8.GetString(manifestBytes));
            manifest.ValidateForInstall();
        }
        catch (LauncherPluginCapabilityVersionException ex)
        {
            throw new LauncherUpdateException(CapabilityVocabularyRefusal, ex);
        }
        catch (LauncherPluginManifestException ex)
        {
            throw new LauncherUpdateException($"The plugin manifest is invalid: {ex.Message}", ex);
        }

        if (displayedCapabilities is not null
            && !CapabilitiesMatch(displayedCapabilities, manifest.Capabilities))
        {
            throw new LauncherUpdateException(CapabilitiesChangedRefusal);
        }

        if (!manifest.MatchesTag(tag))
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
        // "Client not installed" is informational, not a refusal: nothing can run yet
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
            && string.Equals(existingRecord.Repo, repo, StringComparison.OrdinalIgnoreCase);

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
        RefuseUnmanagedFolder(existingRecord, manifest.Id, targetDirectory);

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
            RefusePackagedFilesFolder(stagingDirectory);

            byte[] zipManifestBytes = await File.ReadAllBytesAsync(
                    Path.Combine(stagingDirectory, "plugin.json"),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!zipManifestBytes.AsSpan().SequenceEqual(manifestDocument.Content))
            {
                throw new LauncherUpdateException(
                    "The plugin archive's plugin.json does not match the published release.");
            }

            string zipIconPath = Path.Combine(stagingDirectory, LauncherPluginIcon.FileName);
            if (File.Exists(zipIconPath))
            {
                byte[] zipIconBytes = await File.ReadAllBytesAsync(zipIconPath, cancellationToken)
                    .ConfigureAwait(false);
                LauncherPluginIcon.Validate(zipIconBytes);

                PluginReleaseFetchResult iconFetch = await _releaseClient.FetchDocumentAsync(
                        GitHubReleaseLocator.TaggedAsset(repo, tag, LauncherPluginIcon.FileName),
                        cancellationToken)
                    .ConfigureAwait(false);
                RequireSuccess(iconFetch, "The release is missing its icon.png asset.");

                if (!zipIconBytes.AsSpan().SequenceEqual(iconFetch.Document!.Content))
                {
                    throw new LauncherUpdateException(
                        "The plugin archive's icon.png does not match the published release.");
                }
            }

            using (AcquirePluginWrite())
            {
                // Asked again now that no other launcher can change the folder: a copy placed by
                // hand while the download ran must not be replaced as if it were the old version.
                RefuseUnmanagedFolder(existingRecord, manifest.Id, targetDirectory);

                ReplaceInPlace(
                    manifest.Id,
                    repo,
                    catalog,
                    manifest.Version,
                    tag,
                    shaFile.Sha256,
                    pluginVersion.IsPreRelease,
                    existingRecord,
                    stagingDirectory,
                    targetDirectory,
                    channel,
                    manifest.EntryDll);
            }

            return new PluginInstallResult(manifest.Id, manifest.Version, isUpdate);
        }
        finally
        {
            VerifiedArtifactDownloader.TryDelete(zipPath);
            // A staging folder holds the player's files only if a swap
            // failed part-way; they go back, or stay for Recover.
            ReclaimStaging(stagingDirectory, targetDirectory);

            TryDeleteIfEmpty(Path.Combine(_paths.PluginsDirectory, ".staging"));
        }
    }

    /// <summary>Sets a launcher-managed plugin's channel, under the same exclusive lease as
    /// every other record write. Returns the updated record so the toggle can reflect it without
    /// waiting for the next Check pass.</summary>
    public InstalledPluginRecord SetChannel(string id, PluginReleaseChannel channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        InstalledPluginRecord record = _recordStore.Find(id)
            ?? throw new LauncherUpdateException($"'{id}' is not a launcher-managed plugin.");

        using (AcquirePluginWrite())
        {
            InstalledPluginRecord updated = record with { Channel = channel };
            Upsert(updated);
            _recordStore.Save();
            return updated;
        }
    }

    /// <summary>Removes a launcher-managed plugin's code. Its private files in
    /// <c>plugins/&lt;id&gt;/files</c> stay unless <paramref name="deleteStorage"/> asks for them
    /// too; never the shared root and never Vtank's own profile directory.</summary>
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
                RemoveCodeKeepingFiles(id, targetDirectory, deleteStorage);
            }

            TryDeleteIfEmpty(Path.Combine(_paths.PluginsDirectory, ".trash"));

            _recordStore.Records.RemoveAll(record =>
                string.Equals(record.Id, id, StringComparison.OrdinalIgnoreCase));
            _recordStore.Save();
        }
    }

    /// <summary>
    /// Moves a plugin folder to the trash in one rename, hands its private
    /// files back to a fresh folder of the same name unless asked to delete
    /// them, then deletes the trash. A crash between the two renames leaves
    /// the files in the trash, where Recover finds and returns them.
    /// </summary>
    private void RemoveCodeKeepingFiles(string trashName, string pluginDirectory, bool deleteFiles)
    {
        string trashDirectory = CreateTrashPath(trashName);
        Directory.Move(pluginDirectory, trashDirectory);
        string trashedFiles = Path.Combine(trashDirectory, FilesFolderName);
        if (!deleteFiles && Directory.Exists(trashedFiles))
        {
            Directory.CreateDirectory(pluginDirectory);
            Directory.Move(trashedFiles, Path.Combine(pluginDirectory, FilesFolderName));
        }

        SafeZipExtractor.TryDeleteDirectory(trashDirectory);
    }

    /// <summary>Removes a Direct install by its folder rather than its manifest id, which
    /// <see cref="LauncherPluginManifest.Parse"/> never validates and so could name anything. Never touches a launcher-managed or bundled plugin.</summary>
    public void RemoveDirect(string directory, bool deleteStorage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!_barrier.TryAcquireExclusive(out UpdateSessionBarrier.ExclusiveLease? lease))
        {
            throw new LauncherUpdateException(SessionLeaseRefusal);
        }

        using (lease)
        {
            InstalledPluginInfo row = _inventory.Build(clientResolution: null, catalog: null)
                .FirstOrDefault(info => LauncherPathIdentity.Equals(info.Directory, directory))
                ?? throw new LauncherUpdateException("That plugin is no longer installed.");

            if (row.Source != InstalledPluginSource.Direct)
            {
                throw new LauncherUpdateException(
                    "Only a directly installed plugin can be removed this way.");
            }

            string? parent = Path.GetDirectoryName(row.Directory);
            if (parent is null || !LauncherPathIdentity.Equals(parent, _paths.PluginsDirectory))
            {
                throw new LauncherUpdateException("That plugin is not under the plugins folder.");
            }

            string folderName = Path.GetFileName(row.Directory);
            if (folderName.StartsWith('.'))
            {
                throw new LauncherUpdateException("That folder is not a plugin install.");
            }

            RemoveCodeKeepingFiles(folderName, row.Directory, deleteStorage);
            TryDeleteIfEmpty(Path.Combine(_paths.PluginsDirectory, ".trash"));

            // A plugin keeps its files under its manifest id, which for a
            // hand-unzipped folder need not match the folder's own name.
            if (deleteStorage
                && LauncherPluginManifest.HasValidInstallId(row.Id)
                && !string.Equals(row.Id, folderName, StringComparison.OrdinalIgnoreCase))
            {
                string idFiles = _paths.PluginFilesDirectory(row.Id);
                SafeZipExtractor.TryDeleteDirectory(idFiles);
                TryDeleteIfEmpty(Path.Combine(_paths.PluginsDirectory, row.Id));
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
            // The trash first: an interrupted update may have moved the old
            // folder there, and it has to be back in place before a staged
            // copy of the player's files can be returned into it.
            RecoverTrash();

            string stagingRoot = Path.Combine(_paths.PluginsDirectory, ".staging");
            if (Directory.Exists(stagingRoot))
            {
                // Before any staging folder goes: an unfinished in-place update keeps the old
                // version's files beside its staging folder until it is settled.
                RecoverInPlaceUpdates(stagingRoot);

                foreach (string directory in Directory.EnumerateDirectories(stagingRoot))
                {
                    ReclaimStaging(
                        directory,
                        Path.Combine(_paths.PluginsDirectory, IdFromTrashPath(directory)));
                }

                // A marker whose staging folder is already gone says nothing.
                foreach (string marker in Directory.EnumerateFiles(stagingRoot, "*.player-files"))
                {
                    if (!Directory.Exists(marker[..^".player-files".Length]))
                        VerifiedArtifactDownloader.TryDelete(marker);
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
                && !HasPluginCode(Path.Combine(_paths.PluginsDirectory, record.Id)))
            {
                _recordStore.Records.Remove(record);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Writes a new version into the plugin's own folder while clients may be running it. The
    /// clients read a plugin's assemblies into memory, so no file is held open; they reload the
    /// plugin a second after its folder goes quiet. The player's <c>files</c> folder is never
    /// moved. The old version's files are set aside first and the new ones moved in, with
    /// <c>plugin.json</c> last, so a folder holding the new <c>plugin.json</c> holds all of the new
    /// version. A journal beside the staging folder names the old files until the swap is done: a
    /// failure puts them back, and so does <see cref="Recover"/> after a crash.
    /// </summary>
    private void ReplaceInPlace(
        string id,
        string repo,
        PluginCatalog? catalog,
        string newVersion,
        string newTag,
        string newZipSha256,
        bool newVersionIsPreRelease,
        InstalledPluginRecord? existingRecord,
        string stagingDirectory,
        string targetDirectory,
        PluginReleaseChannel? explicitChannel,
        string entryDll)
    {
        var pending = new PendingPluginInstall(newVersion, newTag, newZipSha256);
        // The channel follows the version just fetched: a prerelease always lands
        // on beta; a stable release keeps whatever channel an existing record already carries, so a
        // beta player's update to stable never downgrades them off the channel. A caller-supplied
        // channel (Discover's per-row picker) wins over both: picking Beta on a plugin whose newest
        // release is stable must still land on Beta, not be silently discarded.
        PluginReleaseChannel channel = explicitChannel ?? (newVersionIsPreRelease
            ? PluginReleaseChannel.Beta
            : existingRecord?.Channel ?? PluginReleaseChannel.Stable);
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
            {
                Channel = channel,
            }
            : existingRecord with { Pending = pending, Channel = channel };

        Upsert(pendingRecord);
        _recordStore.Save();

        Directory.CreateDirectory(targetDirectory);
        string[] oldCode = CodeFiles(targetDirectory);
        string[] newCode = CodeFiles(stagingDirectory)
            .OrderBy(file => InstallOrder(file, entryDll))
            .ThenBy(static file => file, StringComparer.Ordinal)
            .ToArray();
        string journal = InPlaceJournal(stagingDirectory);
        string setAside = SetAsideDirectory(stagingDirectory);
        File.WriteAllLines(journal, [id, .. oldCode]);

        try
        {
            foreach (string file in oldCode)
                MoveFile(Path.Combine(targetDirectory, file), Path.Combine(setAside, file));
            AfterOldCodeSetAside?.Invoke();
            foreach (string file in newCode)
                MoveFile(Path.Combine(stagingDirectory, file), Path.Combine(targetDirectory, file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PutBack(journal);
            throw new LauncherUpdateException(
                $"'{id}' could not be updated: {ex.Message} The previous version is back in place.",
                ex);
        }
        catch
        {
            PutBack(journal);
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

        FinishInPlace(journal);
    }

    /// <summary>
    /// Every file of a plugin's code, relative to its folder with <c>/</c> separators: everything
    /// but the player's top-level <c>files</c> folder.
    /// </summary>
    private static string[] CodeFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return [];
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(static file => !IsInFilesFolder(file))
            .OrderBy(static file => file, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsInFilesFolder(string relativeFile) =>
        relativeFile.StartsWith(FilesFolderName + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The new version's files go in with its entry assembly second to last and <c>plugin.json</c>
    /// last: a client reloads when either changes, and by then everything they need is there.
    /// </summary>
    private static int InstallOrder(string file, string entryDll) =>
        string.Equals(file, "plugin.json", StringComparison.OrdinalIgnoreCase) ? 2
        : string.Equals(file, entryDll.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) ? 1
        : 0;

    private static void MoveFile(string source, string destination)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("A plugin file has no parent folder."));
        File.Move(source, destination, overwrite: true);
    }

    /// <summary>The journal of an in-place update: the plugin id, then the old version's files.</summary>
    private static string InPlaceJournal(string stagingDirectory) =>
        Path.TrimEndingDirectorySeparator(stagingDirectory) + ".in-place";

    /// <summary>Where an in-place update keeps the old version's files until it is done.</summary>
    private static string SetAsideDirectory(string stagingDirectory) =>
        Path.TrimEndingDirectorySeparator(stagingDirectory) + ".old";

    /// <summary>
    /// Undoes an unfinished in-place update: every old file set aside goes back, and every file
    /// the old version did not have leaves the plugin's folder. It can run again after being
    /// interrupted itself, because it only ever moves what is still set aside. The player's
    /// <c>files</c> folder is never touched.
    /// </summary>
    private void PutBack(string journal)
    {
        string[] lines = File.ReadAllLines(journal);
        string id = lines[0];
        var oldCode = new HashSet<string>(lines.Skip(1), StringComparer.Ordinal);
        string targetDirectory = Path.Combine(_paths.PluginsDirectory, id);
        string stagingDirectory = journal[..^".in-place".Length];
        string setAside = SetAsideDirectory(stagingDirectory);

        foreach (string file in CodeFiles(setAside))
            MoveFile(Path.Combine(setAside, file), Path.Combine(targetDirectory, file));
        foreach (string file in CodeFiles(targetDirectory))
        {
            if (!oldCode.Contains(file))
                File.Delete(Path.Combine(targetDirectory, file));
        }

        RemoveEmptyCodeFolders(targetDirectory);
        SafeZipExtractor.TryDeleteDirectory(setAside);
        VerifiedArtifactDownloader.TryDelete(journal);
    }

    /// <summary>
    /// Ends a finished in-place update: the old version's files and the journal go. A file an
    /// older client still has open stays in the set-aside folder, which a later
    /// <see cref="Recover"/> deletes.
    /// </summary>
    private void FinishInPlace(string journal)
    {
        string id = File.ReadLines(journal).First();
        string stagingDirectory = journal[..^".in-place".Length];
        RemoveEmptyCodeFolders(Path.Combine(_paths.PluginsDirectory, id));
        SafeZipExtractor.TryDeleteDirectory(SetAsideDirectory(stagingDirectory));
        VerifiedArtifactDownloader.TryDelete(journal);
    }

    /// <summary>Removes folders the old version had and the new one left empty.</summary>
    private static void RemoveEmptyCodeFolders(string pluginDirectory)
    {
        if (!Directory.Exists(pluginDirectory))
            return;
        foreach (string directory in Directory
            .EnumerateDirectories(pluginDirectory, "*", SearchOption.AllDirectories)
            .OrderByDescending(static directory => directory.Length))
        {
            string relative = Path.GetRelativePath(pluginDirectory, directory)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (string.Equals(relative, FilesFolderName, StringComparison.OrdinalIgnoreCase)
                || IsInFilesFolder(relative))
            {
                continue;
            }
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }

    /// <summary>
    /// Settles every in-place update a crash left unfinished. One whose new <c>plugin.json</c> had
    /// already gone in is finished; any other is put back to the old version.
    /// </summary>
    private void RecoverInPlaceUpdates(string stagingRoot)
    {
        foreach (string journal in Directory.EnumerateFiles(stagingRoot, "*.in-place"))
        {
            string stagingDirectory = journal[..^".in-place".Length];
            string? id = File.ReadLines(journal).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(id))
            {
                VerifiedArtifactDownloader.TryDelete(journal);
                continue;
            }

            bool newManifestIsIn = !File.Exists(Path.Combine(stagingDirectory, "plugin.json"))
                && File.Exists(Path.Combine(_paths.PluginsDirectory, id, "plugin.json"))
                && Directory.Exists(stagingDirectory);
            if (newManifestIsIn)
                FinishInPlace(journal);
            else
                PutBack(journal);
        }
    }

    /// <summary>
    /// The locks a plugin write holds: the install's session lock shared, as a running client
    /// holds it, so nothing that rewrites the whole install can start meanwhile; and the plugin
    /// write lock alone, so no other launcher writes plugins at the same time.
    /// </summary>
    private IDisposable AcquirePluginWrite()
    {
        if (!_barrier.TryAcquireSession(out UpdateSessionBarrier.SessionLease? session))
        {
            throw new LauncherUpdateException(UpdateInProgressRefusal);
        }

        string lockPath = Path.Combine(
            Path.GetDirectoryName(_barrier.LockPath)
                ?? throw new InvalidOperationException("The session lock path has no parent folder."),
            PluginWriteLockFileName);
        try
        {
            var writeLock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return new PluginWriteLease(session!, writeLock);
        }
        catch (IOException ex)
        {
            session!.Dispose();
            throw new LauncherUpdateException(PluginWriteBusyRefusal, ex);
        }
        catch
        {
            session!.Dispose();
            throw;
        }
    }

    private sealed class PluginWriteLease(
        UpdateSessionBarrier.SessionLease session,
        FileStream writeLock) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            writeLock.Dispose();
            session.Dispose();
        }
    }

    /// <summary>
    /// The file beside a staging folder that says its files/ holds the
    /// player's files rather than anything the package brought. It sits
    /// outside the staging folder, so no package can ship it.
    /// </summary>
    private static string PlayerFilesMarker(string stagingDirectory) =>
        Path.TrimEndingDirectorySeparator(stagingDirectory) + ".player-files";

    /// <summary>
    /// Hands a staging folder's files/ back only when the marker says they
    /// are the player's, then deletes the staging folder and its marker.
    /// Leaves both when the files could not be handed back.
    /// </summary>
    private static void ReclaimStaging(string stagingDirectory, string pluginDirectory)
    {
        string marker = PlayerFilesMarker(stagingDirectory);
        if (File.Exists(marker) && !ReturnStagedFiles(stagingDirectory, pluginDirectory))
            return;

        SafeZipExtractor.TryDeleteDirectory(stagingDirectory);
        VerifiedArtifactDownloader.TryDelete(marker);
    }

    private static void RefuseUnmanagedFolder(
        InstalledPluginRecord? existingRecord,
        string id,
        string targetDirectory)
    {
        // A folder holding nothing but files/ is what removing a plugin's
        // code leaves behind; installing the plugin again picks its files up.
        if (existingRecord is not null || !HasPluginCode(targetDirectory))
            return;

        // The full path stays out of the player-facing message; the inner exception keeps it
        // for diagnostics.
        throw new LauncherUpdateException(
            $"A folder named {id} is already in your plugins folder, and the "
            + "launcher didn't install it. Move or delete that folder, then try again.",
            new LauncherUpdateException(
                $"'{targetDirectory}' already exists and is not a launcher-managed plugin."));
    }

    /// <summary>
    /// Refuses a package that ships its own top-level <c>files</c> folder:
    /// that name inside a plugin's folder belongs to the player's saved
    /// files, and an update carries the old one into the new version.
    /// </summary>
    private static void RefusePackagedFilesFolder(string stagingDirectory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(stagingDirectory))
        {
            if (string.Equals(
                    Path.GetFileName(entry),
                    FilesFolderName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new LauncherUpdateException(PackagedFilesRefusal);
            }
        }
    }

    /// <summary>Whether a plugin folder holds anything besides the player's files.</summary>
    internal static bool HasPluginCode(string pluginDirectory) =>
        Directory.Exists(pluginDirectory)
        && Directory.EnumerateFileSystemEntries(pluginDirectory).Any(entry =>
            !string.Equals(
                Path.GetFileName(entry),
                FilesFolderName,
                StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns files a swap staged beside new code to their plugin folder.
    /// True when nothing of the player's is left in <paramref name="stagingDirectory"/>.
    /// </summary>
    private static bool ReturnStagedFiles(string stagingDirectory, string pluginDirectory)
    {
        string staged = Path.Combine(stagingDirectory, FilesFolderName);
        if (!Directory.Exists(staged))
            return true;

        string target = Path.Combine(pluginDirectory, FilesFolderName);
        if (Directory.Exists(target))
            return false;

        try
        {
            Directory.CreateDirectory(pluginDirectory);
            Directory.Move(staged, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left where it is; the next Recover pass tries again.
            return false;
        }
    }

    /// <summary>
    /// Restores a managed plugin whose update or removal was interrupted
    /// with its folder in the trash, and otherwise returns any of the
    /// player's files the trash still holds before deleting it.
    /// </summary>
    private void RecoverTrash()
    {
        string trashRoot = Path.Combine(_paths.PluginsDirectory, ".trash");
        if (!Directory.Exists(trashRoot))
            return;

        foreach (string trashDirectory in Directory.EnumerateDirectories(trashRoot))
        {
            string id = IdFromTrashPath(trashDirectory);
            string original = Path.Combine(_paths.PluginsDirectory, id);
            // Record-less trash is a Direct removal, never a managed one; resurrecting it
            // would undo a removal that already succeeded.
            if (_recordStore.Find(id) is not null && !HasPluginCode(original))
            {
                string originalFiles = Path.Combine(original, FilesFolderName);
                string trashFiles = Path.Combine(trashDirectory, FilesFolderName);
                if (Directory.Exists(originalFiles))
                {
                    if (Directory.Exists(trashFiles))
                    {
                        // Two copies cannot arise from one swap; leave both
                        // for the player rather than pick one.
                        continue;
                    }

                    Directory.Move(originalFiles, trashFiles);
                }

                if (Directory.Exists(original))
                {
                    Directory.Delete(original);
                }

                Directory.Move(trashDirectory, original);
            }
            else if (ReturnStagedFiles(trashDirectory, original))
            {
                SafeZipExtractor.TryDeleteDirectory(trashDirectory);
            }
        }

        TryDeleteIfEmpty(trashRoot);
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
            && string.Equals(entry.Repo, repo, StringComparison.OrdinalIgnoreCase))
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

    private static void RequireSuccess(
        PluginReleaseFetchResult result,
        string unavailableMessage = "The plugin release is unavailable.")
    {
        switch (result.Status)
        {
            case PluginReleaseFetchStatus.RateLimited:
                throw new LauncherUpdateException("GitHub is rate limiting; try later.");
            case PluginReleaseFetchStatus.Unavailable:
                throw new LauncherUpdateException(unavailableMessage);
        }
    }

    /// <summary>Same declarations regardless of order: an author cannot dodge the comparison by
    /// reordering an otherwise-unchanged list. Public so the launcher can use the same yardstick to
    /// decide whether an update needs fresh consent.</summary>
    public static bool CapabilitiesMatch(
        IReadOnlyList<LauncherPluginCapabilityDeclaration> displayed,
        IReadOnlyList<LauncherPluginCapabilityDeclaration> actual)
    {
        if (displayed.Count != actual.Count)
            return false;

        return Sorted(displayed).SequenceEqual(Sorted(actual));

        static IEnumerable<(LauncherPluginCapability Name, string Note)> Sorted(
            IReadOnlyList<LauncherPluginCapabilityDeclaration> declarations) => declarations
            .Select(declaration => (declaration.Name, declaration.Note))
            .OrderBy(entry => entry.Name)
            .ThenBy(entry => entry.Note, StringComparer.Ordinal);
    }

    private static string DescribeSource(InstalledPluginSource source) => source switch
    {
        InstalledPluginSource.Managed => "launcher-installed",
        InstalledPluginSource.Direct => "directly installed",
        InstalledPluginSource.Bundled => "client-bundled",
        _ => source.ToString(),
    };
}
