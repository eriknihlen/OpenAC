using System.Text.Json;
using System.Text.Json.Serialization;
using AcDream.Launcher.Core.Profiles;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Launching;

public sealed record ComposedSessionConfig(
    string SessionId,
    string ConfigFilePath,
    string StatusFilePath,
    string StderrLogPath,
    SessionConfigDocument Document);

public interface ILauncherSessionConfigService
{
    ComposedSessionConfig ComposeAndWrite(
        ServerProfile server,
        AccountProfile account,
        CharacterProfile character,
        LauncherInstallRecord install,
        ApplicationPathSet paths,
        string sessionId,
        int? loginCommandDelayMs = null);

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
        int? loginCommandDelayMs = null) =>
        SessionConfigComposer.ComposeAndWrite(
            server,
            account,
            character,
            install,
            paths,
            sessionId,
            loginCommandDelayMs);

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
        int? loginCommandDelayMs = null)
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
            Plugins = ComposePluginAllowList(character.Plugins),
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
            document);
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
            document);
    }

    public static ComposedSessionConfig ComposeAndWrite(
        ServerProfile server,
        AccountProfile account,
        CharacterProfile character,
        LauncherInstallRecord install,
        ApplicationPathSet paths,
        string sessionId,
        int? loginCommandDelayMs = null)
    {
        ComposedSessionConfig composed = Compose(
            server,
            account,
            character,
            install,
            paths,
            sessionId,
            loginCommandDelayMs);

        return Write(composed);
    }

    public static ComposedSessionConfig ComposeProbeAndWrite(
        ServerProfile server,
        AccountProfile account,
        LauncherInstallRecord install,
        ApplicationPathSet paths,
        string sessionId) =>
        Write(ComposeProbe(server, account, install, paths, sessionId));

    private static List<string>? ComposePluginAllowList(IReadOnlyList<string> configured)
    {
        if (configured.Count == 0)
            return null;                                   // default: load all

        if (configured.Count == 1
            && string.Equals(configured[0], "none", StringComparison.OrdinalIgnoreCase))
        {
            return [];                                     // explicit: load none
        }

        return [.. configured];
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
