using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Plugins;

/// <summary>Where an installed plugin's files came from.</summary>
public enum InstalledPluginSource
{
    Managed,
    Direct,
    Bundled,
}

/// <summary>One row of the Installed view, built by reading <c>plugin.json</c> files only
/// (L-303).</summary>
public sealed record InstalledPluginInfo(
    string Id,
    string DisplayName,
    string Version,
    InstalledPluginSource Source,
    string Directory,
    string? Repo,
    string Compatibility,
    bool CompatibilityIsWarning,
    string? Blocked,
    bool Conflict,
    PluginInstallSource? ListedSource,
    string? Refusal = null,
    bool HasDuplicate = false);

/// <summary>Builds the Installed view and the character checklist by scanning
/// <c>DataDirectory/plugins</c> and, when a client is installed, its bundled
/// <c>&lt;client&gt;/plugins</c>, cross-referenced against <see cref="InstalledPluginRecordStore"/>
/// (L-303, L-309, L-318). A Direct row (an unzipped folder with no matching record) is checked
/// against <see cref="DirectInstallCheck"/> every time the inventory is built.</summary>
public sealed class PluginInventory
{
    private readonly ApplicationPathSet _paths;
    private readonly InstalledPluginRecordStore _recordStore;

    public PluginInventory(ApplicationPathSet paths, InstalledPluginRecordStore recordStore)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _recordStore = recordStore ?? throw new ArgumentNullException(nameof(recordStore));
    }

    public IReadOnlyList<InstalledPluginInfo> Build(
        ClientVersionResolution? clientResolution,
        PluginCatalog? catalog)
    {
        LauncherVersion? clientVersion = clientResolution?.Version;
        string? bundledDirectory = clientResolution is { IsVerified: true, Directory: { } directory }
            ? Path.Combine(directory, "plugins")
            : null;

        var results = new List<InstalledPluginInfo>();
        var placedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A record's own folder is placed first and unconditionally, so a stray folder sharing its
        // id can never take the managed row by sorting ahead of it (L-318).
        foreach (InstalledPluginRecord record in _recordStore.Records)
        {
            string ownDirectory = Path.Combine(_paths.PluginsDirectory, record.Id);
            string manifestPath = Path.Combine(ownDirectory, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            LauncherPluginManifest? manifest = TryParseManifest(manifestPath);
            if (manifest is null
                || !string.Equals(manifest.Id, record.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            claimedDirectories.Add(ownDirectory);
            placedIds.Add(manifest.Id);
            results.Add(BuildInfo(
                manifest,
                ownDirectory,
                InstalledPluginSource.Managed,
                record.Repo,
                record.Source,
                catalog,
                clientVersion,
                refusal: null));
        }

        // Every other folder is a Direct install (today's Manual), checked every time it is read.
        foreach ((string ownDirectory, LauncherPluginManifest manifest) in ScanManifests(
                     _paths.PluginsDirectory))
        {
            if (claimedDirectories.Contains(ownDirectory))
            {
                continue;
            }

            placedIds.Add(manifest.Id);
            results.Add(BuildInfo(
                manifest,
                ownDirectory,
                InstalledPluginSource.Direct,
                repo: null,
                listedSource: null,
                catalog,
                clientVersion,
                refusal: DirectInstallCheck.Refusal(ownDirectory, manifest)));
        }

        var bundledIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unrepresentedBundledIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (bundledDirectory is not null)
        {
            foreach ((string ownDirectory, LauncherPluginManifest manifest) in ScanManifests(
                         bundledDirectory))
            {
                bundledIds.Add(manifest.Id);
                if (placedIds.Add(manifest.Id))
                {
                    results.Add(BuildInfo(
                        manifest,
                        ownDirectory,
                        InstalledPluginSource.Bundled,
                        repo: null,
                        listedSource: null,
                        catalog,
                        clientVersion,
                        refusal: null));
                }
                else
                {
                    unrepresentedBundledIds[manifest.Id] =
                        unrepresentedBundledIds.GetValueOrDefault(manifest.Id) + 1;
                }
            }
        }

        var occurrencesById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (InstalledPluginInfo info in results)
        {
            occurrencesById[info.Id] = occurrencesById.GetValueOrDefault(info.Id) + 1;
        }

        foreach ((string id, int extra) in unrepresentedBundledIds)
        {
            occurrencesById[id] = occurrencesById.GetValueOrDefault(id) + extra;
        }

        for (int index = 0; index < results.Count; index++)
        {
            InstalledPluginInfo info = results[index];
            if (info.Source != InstalledPluginSource.Bundled && bundledIds.Contains(info.Id))
            {
                info = info with { Conflict = true };
            }

            // The client loads no copy of a duplicated id (L-318): every Direct copy is refused, and
            // a surviving managed or bundled copy is flagged, because the collision is real either way.
            if (occurrencesById.GetValueOrDefault(info.Id) > 1)
            {
                info = info.Source == InstalledPluginSource.Direct
                    ? info with { Refusal = info.Refusal ?? "Another copy of this plugin is installed." }
                    : info with { HasDuplicate = true };
            }

            results[index] = info;
        }

        return results;
    }

    public InstalledPluginInfo? Find(
        string id,
        ClientVersionResolution? clientResolution,
        PluginCatalog? catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Build(clientResolution, catalog)
            .FirstOrDefault(info => string.Equals(info.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The version-only compatibility verdict (min/max/skip host version) a manifest gets
    /// against the installed client. Host filtering (graphical/headless) is a per-launch concern, not
    /// an install-time one, so it is deliberately not applied here.</summary>
    internal static string? EvaluateVersionCompatibility(
        LauncherPluginManifest manifest,
        LauncherVersion? clientVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        LauncherPluginHostVersion? installed = LauncherPluginHostVersion.FromLauncherVersion(
            clientVersion);
        if (installed is not { } version)
        {
            return LauncherPluginCompatibility.ClientNotInstalled;
        }

        if (manifest.MinHostVersion is { } min && version.CompareTo(min) < 0)
        {
            return $"requires OpenAC {min} or newer (this is {version})";
        }

        if (manifest.MaxHostVersion is { } max && version.CompareTo(max) > 0)
        {
            return $"supports OpenAC up to {max} (this is {version})";
        }

        if (manifest.SkipHostVersions.Contains(version))
        {
            return $"is marked broken on OpenAC {version}";
        }

        return null;
    }

    internal static string? FindBlockReason(
        PluginCatalog? catalog,
        string id,
        LauncherVersion pluginVersion) =>
        catalog?.Blocked.FirstOrDefault(block =>
                string.Equals(block.Id, id, StringComparison.OrdinalIgnoreCase)
                && block.Matches(pluginVersion.Value))
            ?.Reason;

    private static InstalledPluginInfo BuildInfo(
        LauncherPluginManifest manifest,
        string directory,
        InstalledPluginSource source,
        string? repo,
        PluginInstallSource? listedSource,
        PluginCatalog? catalog,
        LauncherVersion? clientVersion,
        string? refusal)
    {
        string? blocked = LauncherVersion.TryParse(manifest.Version, out LauncherVersion? version)
            ? FindBlockReason(catalog, manifest.Id, version)
            : null;
        LauncherPluginCompatibility.CompatibilityDescription compatibility =
            LauncherPluginCompatibility.Describe(manifest, clientVersion);
        return new InstalledPluginInfo(
            manifest.Id,
            manifest.DisplayName,
            manifest.Version,
            source,
            directory,
            repo,
            compatibility.Text,
            compatibility.IsWarning,
            blocked,
            Conflict: false,
            listedSource,
            refusal,
            HasDuplicate: false);
    }

    private static IEnumerable<(string Directory, LauncherPluginManifest Manifest)> ScanManifests(
        string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (string directory in Directory.EnumerateDirectories(root)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (Path.GetFileName(directory).StartsWith('.'))
            {
                continue;
            }

            string manifestPath = Path.Combine(directory, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            LauncherPluginManifest? manifest = TryParseManifest(manifestPath);
            if (manifest is not null)
            {
                yield return (directory, manifest);
            }
        }
    }

    private static LauncherPluginManifest? TryParseManifest(string manifestPath)
    {
        try
        {
            return LauncherPluginManifest.Parse(File.ReadAllText(manifestPath));
        }
        catch (LauncherPluginManifestException)
        {
            return null;
        }
    }
}
