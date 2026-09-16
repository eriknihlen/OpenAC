using System.Text.Json;
using System.Text.Json.Serialization;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Launching;

public sealed record ComposedSessionConfig(
    string SessionId,
    string ConfigFilePath,
    string StatusFilePath,
    string StderrLogPath,
    SessionConfigDocument Document,
    IReadOnlyList<string> PluginStatusLines);

public interface ILauncherSessionConfigService
{
    ComposedSessionConfig ComposeAndWrite(
        ServerProfile server,
        AccountProfile account,
        CharacterProfile character,
        LauncherInstallRecord install,
        ApplicationPathSet paths,
        string sessionId,
        int? loginCommandDelayMs = null,
        PluginCatalog? catalog = null);

    ComposedSessionConfig ComposeProbeAndWrite(
        ServerProfile server,
        AccountProfile account,
        LauncherInstallRecord install,
        ApplicationPathSet paths,
        string sessionId);
}

public sealed class LauncherSessionConfigService : ILauncherSessionConfigService
{
    public ComposedSessionConfig ComposeAndWrite(
        ServerProfile server,
        AccountProfile account,
        CharacterProfile character,
        LauncherInstallRecord install,
        ApplicationPathSet paths,
        string sessionId,
        int? loginCommandDelayMs = null,
        PluginCatalog? catalog = null) =>
        SessionConfigComposer.ComposeAndWrite(
            server,
            account,
            character,
            install,
            paths,
            sessionId,
            loginCommandDelayMs,
            catalog);

    public ComposedSessionConfig ComposeProbeAndWrite(
        ServerProfile server,
        AccountProfile account,
        LauncherInstallRecord install,
        ApplicationPathSet paths,
        string sessionId) =>
        SessionConfigComposer.ComposeProbeAndWrite(
            server,
            account,
            install,
            paths,
            sessionId);
}

public static class SessionConfigComposer
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static ComposedSessionConfig Compose(
        ServerProfile server,
        AccountProfile account,
        CharacterProfile character,
        LauncherInstallRecord install,
        ApplicationPathSet paths,
        string sessionId,
        int? loginCommandDelayMs = null,
        PluginCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        EnsureClientCompatibilityConfirmed(install);

        (string configFilePath, string statusFilePath, string stderrLogPath) =
            BuildSessionPaths(paths, sessionId);

        SessionCharacterSelector? selector = character.LaunchMode == LaunchMode.GuiSelect
            ? null
            : BuildSelector(character);

        SessionPolicyDescriptor? policy = character.LaunchMode == LaunchMode.Headless
            ? new SessionPolicyDescriptor()
            : null;

        (List<string> pluginAllowList, IReadOnlyList<string> pluginStatusLines) =
            ComposePluginAllowList(character.Plugins, catalog, paths);

        var descriptor = new SessionDescriptor
        {
            Id = sessionId,
            Endpoint = new SessionEndpointDescriptor
            {
                Host = server.Host,
                Port = server.Port,
            },
            Account = account.Account,
            Character = selector,
            Policy = policy,
            Credential = new SessionCredentialDescriptor(),
            Plugins = pluginAllowList,
            LoginCommands = character.LoginCommands.Count > 0
                ? [.. character.LoginCommands]
                : null,
            LoginCommandDelayMs = loginCommandDelayMs,
            StatusFile = statusFilePath,
        };

        var document = new SessionConfigDocument
        {
            Process = new SessionProcessSettings
            {
                Content = new SessionContentDescriptor
                {
                    DatDirectory = install.DatDirectory,
                    PreparedAssetPath = install.PreparedAssetPath,
                    PreparedAssetOverlayPath = install.PreparedAssetOverlayPath,
                    PreparedAssetBaseRecipeVersion =
                        install.PreparedAssetOverlayPath is null
                            ? null
                            : install.BakeToolVersion,
                    PreparedAssetEffectiveRecipeVersion =
                        install.PreparedAssetOverlayPath is null
                            ? null
                            : install.ResolvedBakeToolVersion,
                },
            },
            Sessions = [descriptor],
        };

        return new ComposedSessionConfig(
            sessionId,
            configFilePath,
            statusFilePath,
            stderrLogPath,
            document,
            pluginStatusLines);
    }

    public static ComposedSessionConfig ComposeProbe(
        ServerProfile server,
        AccountProfile account,
        LauncherInstallRecord install,
        ApplicationPathSet paths,
        string sessionId)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        EnsureClientCompatibilityConfirmed(install);

        (string configFilePath, string statusFilePath, string stderrLogPath) =
            BuildSessionPaths(paths, sessionId);

        var descriptor = new SessionDescriptor
        {
            Id = sessionId,
            Mode = "probe",
            Endpoint = new SessionEndpointDescriptor
            {
                Host = server.Host,
                Port = server.Port,
            },
            Account = account.Account,
            Character = null,
            Policy = null,
            Credential = new SessionCredentialDescriptor(),
            Plugins = [],
            LoginCommands = null,
            LoginCommandDelayMs = null,
            StatusFile = statusFilePath,
        };

        var document = new SessionConfigDocument
        {
            Process = new SessionProcessSettings
            {
                Content = new SessionContentDescriptor
                {
                    DatDirectory = install.DatDirectory,
                    PreparedAssetPath = install.PreparedAssetPath,
                    PreparedAssetOverlayPath = install.PreparedAssetOverlayPath,
                    PreparedAssetBaseRecipeVersion =
                        install.PreparedAssetOverlayPath is null
                            ? null
                            : install.BakeToolVersion,
                    PreparedAssetEffectiveRecipeVersion =
                        install.PreparedAssetOverlayPath is null
                            ? null
                            : install.ResolvedBakeToolVersion,
                },
            },
            Sessions = [descriptor],
        };

        return new ComposedSessionConfig(
            sessionId,
            configFilePath,
            statusFilePath,
            stderrLogPath,
            document,
            PluginStatusLines: []);
    }

    public static ComposedSessionConfig ComposeAndWrite(
        ServerProfile server,
        AccountProfile account,
        CharacterProfile character,
        LauncherInstallRecord install,
        ApplicationPathSet paths,
        string sessionId,
        int? loginCommandDelayMs = null,
        PluginCatalog? catalog = null)
    {
        ComposedSessionConfig composed = Compose(
            server,
            account,
            character,
            install,
            paths,
            sessionId,
            loginCommandDelayMs,
            catalog);

        return Write(composed);
    }

    public static ComposedSessionConfig ComposeProbeAndWrite(
        ServerProfile server,
        AccountProfile account,
        LauncherInstallRecord install,
        ApplicationPathSet paths,
        string sessionId) =>
        Write(ComposeProbe(server, account, install, paths, sessionId));

    /// <summary>Blank means none, and always sends an explicit list, so a downloaded plugin never
    /// loads until a character opts in (L-300, L-302). Also enforces the Direct install checks
    /// (L-318): the client applies none of its own, so this is the only place a refused or
    /// duplicated id is kept out of a session.</summary>
    private static (List<string> Allowed, IReadOnlyList<string> StatusLines) ComposePluginAllowList(
        IReadOnlyList<string> configured,
        PluginCatalog? catalog,
        ApplicationPathSet paths)
    {
        if (configured.Count == 0)
            return ([], []);                                // blank: load none (L-302)

        if (configured.Count == 1
            && string.Equals(configured[0], "none", StringComparison.OrdinalIgnoreCase))
        {
            return ([], []);                                // explicit: load none
        }

        var statusLines = new List<string>();
        var recordStore = InstalledPluginRecordStore.ForApplicationPaths(paths);
        try
        {
            recordStore.Load();
        }
        catch (LauncherUpdateException)
        {
            // A corrupted record store only costs this pass its memory of managed plugins; every
            // folder is still checked as a Direct install rather than blocking the session.
        }

        var inventory = new PluginInventory(paths, recordStore);
        Dictionary<string, InstalledPluginInfo> inventoryById = inventory
            .Build(clientResolution: null, catalog: null)
            .GroupBy(info => info.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var checkedIds = new List<string>(configured.Count);
        foreach (string id in configured)
        {
            if (inventoryById.TryGetValue(id, out InstalledPluginInfo? info))
            {
                if (info.Refusal is not null)
                {
                    statusLines.Add($"Plugin '{id}' failed its install checks and was not loaded.");
                    continue;
                }

                if (info.HasDuplicate)
                {
                    statusLines.Add($"Plugin '{id}' has more than one copy installed and was not loaded.");
                    continue;
                }
            }

            checkedIds.Add(id);
        }

        PluginCatalog? effective = catalog ?? TryLoadCachedCatalog(paths);
        if (effective is null)
            return (checkedIds, statusLines);

        var allowed = new List<string>(checkedIds.Count);
        foreach (string id in checkedIds)
        {
            if (effective.IsBlocked(id, version: null))
            {
                statusLines.Add($"Plugin '{id}' is blocked and was not loaded.");
                continue;
            }

            allowed.Add(id);
        }

        return (allowed, statusLines);
    }

    private static PluginCatalog? TryLoadCachedCatalog(ApplicationPathSet paths)
    {
        string path = Path.Combine(paths.CacheDirectory, "plugins.json");
        if (!File.Exists(path))
            return null;

        try
        {
            return PluginCatalog.Parse(File.ReadAllText(path));
        }
        catch (LauncherUpdateException)
        {
            return null;
        }
    }

    private static void EnsureClientCompatibilityConfirmed(
        LauncherInstallRecord install)
    {
        if (install.RequiresClientCompatibilityConfirmation)
        {
            throw new InvalidOperationException(
                "Prepared content cannot be launched until the matching client "
                + "has been confirmed or installed.");
        }
    }

    private static ComposedSessionConfig Write(ComposedSessionConfig composed)
    {
        string? directory = Path.GetDirectoryName(composed.ConfigFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream stream = File.Create(composed.ConfigFilePath);
        JsonSerializer.Serialize(stream, composed.Document, SerializerOptions);

        return composed;
    }

    public static string Serialize(SessionConfigDocument document) =>
        JsonSerializer.Serialize(document, SerializerOptions);

    private static (string ConfigFilePath, string StatusFilePath, string StderrLogPath)
        BuildSessionPaths(
            ApplicationPathSet paths,
            string sessionId)
    {
        string sessionDirectory = Path.Combine(
            paths.CacheDirectory,
            "launcher",
            "sessions",
            sessionId);

        return (
            Path.Combine(sessionDirectory, "session.json"),
            Path.Combine(sessionDirectory, "status.jsonl"),
            Path.Combine(sessionDirectory, "client.err.log"));
    }

    private static SessionCharacterSelector BuildSelector(CharacterProfile character)
    {
        if (CharacterIdFormat.TryParse(character.Id, out uint id) && id != 0)
        {
            return new SessionCharacterSelector { Id = id };
        }

        if (!string.IsNullOrWhiteSpace(character.Name))
        {
            return new SessionCharacterSelector { Name = character.Name };
        }

        throw new InvalidOperationException(
            "Character has neither a usable id nor a name to select by.");
    }
}
