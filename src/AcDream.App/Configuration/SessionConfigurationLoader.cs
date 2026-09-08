using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcDream.App.Configuration;

internal static class SessionConfigurationLoader
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false),
        },
    };

    internal static (SessionConfiguration Configuration, SessionDescriptor Session) Load(
        string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        using FileStream stream = File.OpenRead(fullPath);
        SessionConfiguration? configuration =
            JsonSerializer.Deserialize<SessionConfiguration>(stream, Options);

        if (configuration is null)
        {
            throw new SessionConfigurationException(
                "The configuration document is empty.");
        }

        if (configuration.Version != CurrentVersion)
        {
            throw new SessionConfigurationException(
                $"Unsupported configuration version {configuration.Version}; "
                + $"expected {CurrentVersion}.");
        }

        if (configuration.Sessions is null
            || configuration.Sessions.Count != 1)
        {
            throw new SessionConfigurationException(
                "The graphical host requires exactly one configured session.");
        }

        SessionDescriptor session = configuration.Sessions[0]
            ?? throw new SessionConfigurationException(
                "The configured session cannot be null.");

        ValidateContent(configuration.Process?.Content);
        ValidateSession(session);

        return (configuration, session);
    }

    private static void ValidateContent(SessionContentDescriptor? content)
    {
        if (content is null)
            return;
        if (string.IsNullOrWhiteSpace(content.DatDirectory)
            || string.IsNullOrWhiteSpace(content.PreparedAssetPath))
        {
            throw new SessionConfigurationException(
                "process.content requires non-empty datDirectory and preparedAssetPath.");
        }

        bool hasOverlay = !string.IsNullOrWhiteSpace(
            content.PreparedAssetOverlayPath);
        bool hasBaseRecipe = content.PreparedAssetBaseRecipeVersion is > 0;
        bool hasEffectiveRecipe =
            content.PreparedAssetEffectiveRecipeVersion is > 0;
        if (hasOverlay != hasBaseRecipe || hasOverlay != hasEffectiveRecipe)
        {
            throw new SessionConfigurationException(
                "process.content overlay path, base recipe, and effective recipe "
                + "must be supplied together.");
        }
    }

    private static void ValidateSession(SessionDescriptor session)
    {
        if (string.IsNullOrWhiteSpace(session.Id))
        {
            throw new SessionConfigurationException(
                "The session requires a non-empty id.");
        }

        if (session.Endpoint is null
            || string.IsNullOrWhiteSpace(session.Endpoint.Host)
            || session.Endpoint.Port is < 1 or > 65535)
        {
            throw new SessionConfigurationException(
                $"Session '{session.Id}' requires a host and a port from 1 through 65535.");
        }

        if (string.IsNullOrWhiteSpace(session.Account))
        {
            throw new SessionConfigurationException(
                $"Session '{session.Id}' requires a non-empty account.");
        }

        if (session.Character is { } selector)
        {
            int selectorCount =
                (selector.Index.HasValue ? 1 : 0)
                + (selector.Id.HasValue ? 1 : 0)
                + (!string.IsNullOrWhiteSpace(selector.Name) ? 1 : 0);
            if (selectorCount != 1
                || selector.Index is < 0
                || selector.Id == 0u)
            {
                throw new SessionConfigurationException(
                    $"Session '{session.Id}' character selector must specify "
                    + "exactly one valid index, id, or name.");
            }
        }

        if (session.Credential is null
            || string.IsNullOrWhiteSpace(session.Credential.Reference))
        {
            throw new SessionConfigurationException(
                $"Session '{session.Id}' requires a credential reference.");
        }

        if (session.Plugins is { } plugins)
        {
            foreach (string? plugin in plugins)
            {
                if (string.IsNullOrWhiteSpace(plugin))
                {
                    throw new SessionConfigurationException(
                        $"Session '{session.Id}' plugins entries must be non-empty strings.");
                }
            }
        }

        if (session.LoginCommandDelayMs < 0)
        {
            throw new SessionConfigurationException(
                $"Session '{session.Id}' loginCommandDelayMs must be non-negative.");
        }

        if (session.StatusFile is not null
            && string.IsNullOrWhiteSpace(session.StatusFile))
        {
            throw new SessionConfigurationException(
                $"Session '{session.Id}' statusFile must be a non-empty path when present.");
        }

        ValidateMode(session);
    }

    private static void ValidateMode(SessionDescriptor session)
    {
        if (session.Mode is null)
            return;

        if (string.Equals(session.Mode, "probe", StringComparison.Ordinal))
        {
            throw new SessionConfigurationException(
                $"Session '{session.Id}' has mode 'probe'; probe sessions "
                + "are headless-only and cannot run on the graphical host.");
        }

        throw new SessionConfigurationException(
            $"Session '{session.Id}' has unsupported mode '{session.Mode}'.");
    }
}
