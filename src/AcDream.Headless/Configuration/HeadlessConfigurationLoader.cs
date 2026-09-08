using System.Text.Json;
using System.Text.Json.Serialization;
using AcDream.Core.Net.Messages;

namespace AcDream.Headless.Configuration;

internal static class HeadlessConfigurationLoader
{
    private const int CurrentVersion = 1;

    private static readonly HashSet<CharacterOptionId> AllowedCharacterOptions =
    [
        // Tier 1 (22).
        CharacterOptionId.IgnoreAllegianceRequests,
        CharacterOptionId.IgnoreFellowshipRequests,
        CharacterOptionId.IgnoreTradeRequests,
        CharacterOptionId.AllowGive,
        CharacterOptionId.FellowshipShareXP,
        CharacterOptionId.AcceptLootPermits,
        CharacterOptionId.FellowshipShareLoot,
        CharacterOptionId.FellowshipAutoAcceptRequests,
        CharacterOptionId.DisplayAllegianceLogonNotifications,
        CharacterOptionId.UseChargeAttack,
        CharacterOptionId.UseCraftSuccessDialog,
        CharacterOptionId.AutoRepeatAttack,
        CharacterOptionId.LeadMissileTargets,
        CharacterOptionId.UseFastMissiles,
        CharacterOptionId.ConfirmVolatileRareUse,
        CharacterOptionId.AppearOffline,
        CharacterOptionId.ListenToAllegianceChat,
        CharacterOptionId.ListenToGeneralChat,
        CharacterOptionId.ListenToTradeChat,
        CharacterOptionId.ListenToLFGChat,
        CharacterOptionId.ListenToRoleplayChat,
        CharacterOptionId.ListenToSocietyChat,
        // Tier 2 (4).
        CharacterOptionId.MainPackPreferred,
        CharacterOptionId.ToggleRun,
        CharacterOptionId.AutoTarget,
        CharacterOptionId.SalvageMultiple,
    ];

    private static readonly HashSet<string> AllowedCharacterOptionNames =
        new(
            AllowedCharacterOptions.Select(static id => id.ToString()),
            StringComparer.Ordinal);

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

    internal static HeadlessConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        using FileStream stream = File.OpenRead(fullPath);
        using JsonDocument document = JsonDocument.Parse(
            stream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        HeadlessConfiguration? configuration =
            document.RootElement.Deserialize<HeadlessConfiguration>(Options);

        if (configuration is null)
        {
            throw new HeadlessConfigurationException(
                "The configuration document is empty.");
        }

        if (configuration.Version != CurrentVersion)
        {
            throw new HeadlessConfigurationException(
                $"Unsupported configuration version {configuration.Version}; "
                + $"expected {CurrentVersion}.");
        }

        if (configuration.Sessions is null)
        {
            throw new HeadlessConfigurationException(
                "sessions must be an array.");
        }

        ValidateContent(configuration.Process?.Content);

        JsonElement sessionsElement =
            document.RootElement.GetProperty("sessions");
        var sessionIds = new HashSet<string>(StringComparer.Ordinal);
        var credentialReferences = new HashSet<string>(
            StringComparer.Ordinal);
        int sessionIndex = 0;
        foreach (HeadlessSessionDescriptor? session in configuration.Sessions)
        {
            if (session is null
                || string.IsNullOrWhiteSpace(session.Id))
            {
                throw new HeadlessConfigurationException(
                    "Every session requires a non-empty id.");
            }

            if (!sessionIds.Add(session.Id))
            {
                throw new HeadlessConfigurationException(
                    $"Duplicate session id '{session.Id}'.");
            }

            ValidateSession(session, sessionsElement[sessionIndex]);
            string credentialKey =
                $"{session.Credential.Provider}:{session.Credential.Reference}";
            if (!credentialReferences.Add(credentialKey))
            {
                throw new HeadlessConfigurationException(
                    $"Credential reference for session '{session.Id}' is already in use.");
            }
            sessionIndex++;
        }

        return configuration;
    }

    private static void ValidateContent(HeadlessContentDescriptor? content)
    {
        if (content is null)
            return;
        if (string.IsNullOrWhiteSpace(content.DatDirectory)
            || string.IsNullOrWhiteSpace(content.PreparedAssetPath))
        {
            throw new HeadlessConfigurationException(
                "process.content requires non-empty datDirectory and preparedAssetPath.");
        }

        bool hasOverlay = !string.IsNullOrWhiteSpace(
            content.PreparedAssetOverlayPath);
        bool hasBaseRecipe = content.PreparedAssetBaseRecipeVersion is > 0;
        bool hasEffectiveRecipe =
            content.PreparedAssetEffectiveRecipeVersion is > 0;
        if (hasOverlay != hasBaseRecipe || hasOverlay != hasEffectiveRecipe)
        {
            throw new HeadlessConfigurationException(
                "process.content overlay path, base recipe, and effective recipe "
                + "must be supplied together.");
        }
    }

    private static void ValidateSession(
        HeadlessSessionDescriptor session,
        JsonElement sessionElement)
    {
        if (session.Endpoint is null
            || string.IsNullOrWhiteSpace(session.Endpoint.Host)
            || session.Endpoint.Port is < 1 or > 65535)
        {
            throw new HeadlessConfigurationException(
                $"Session '{session.Id}' requires a host and a port from 1 through 65535.");
        }

        if (string.IsNullOrWhiteSpace(session.Account))
        {
            throw new HeadlessConfigurationException(
                $"Session '{session.Id}' requires a non-empty account.");
        }

        ValidateModeShape(session, sessionElement);

        if (session.Credential is null
            || string.IsNullOrWhiteSpace(session.Credential.Reference))
        {
            throw new HeadlessConfigurationException(
                $"Session '{session.Id}' requires a credential reference.");
        }

        ValidateCharacterOptions(session);
        ValidateLaunchContractFields(session);
    }

    private static void ValidateModeShape(
        HeadlessSessionDescriptor session,
        JsonElement sessionElement)
    {
        bool hasMode = sessionElement.TryGetProperty(
            "mode",
            out JsonElement modeElement);
        bool hasCharacter = sessionElement.TryGetProperty(
            "character",
            out JsonElement characterElement);
        bool hasPolicy = sessionElement.TryGetProperty(
            "policy",
            out JsonElement policyElement);

        RejectExplicitNull(session.Id, "mode", hasMode, modeElement);
        RejectExplicitNull(
            session.Id,
            "character",
            hasCharacter,
            characterElement);
        RejectExplicitNull(session.Id, "policy", hasPolicy, policyElement);

        if (session.Mode == HeadlessSessionMode.Probe)
        {
            if (hasCharacter)
            {
                throw new HeadlessConfigurationException(
                    $"Session '{session.Id}' has mode \"probe\" and must omit "
                    + "'character' — a probe never selects a character.");
            }
            if (hasPolicy)
            {
                throw new HeadlessConfigurationException(
                    $"Session '{session.Id}' has mode \"probe\" and must omit "
                    + "'policy' — a probe never drives a bot policy.");
            }
            return;
        }

        if (hasMode)
        {
            throw new HeadlessConfigurationException(
                $"Session '{session.Id}' is normal play and must omit 'mode'.");
        }

        if (session.Character is null)
        {
            throw new HeadlessConfigurationException(
                $"Session '{session.Id}' requires a character selector.");
        }

        int selectorCount =
            (session.Character.Index.HasValue ? 1 : 0)
            + (session.Character.Id.HasValue ? 1 : 0)
            + (!string.IsNullOrWhiteSpace(session.Character.Name) ? 1 : 0);
        if (selectorCount != 1
            || session.Character.Index is < 0
            || session.Character.Id == 0u)
        {
            throw new HeadlessConfigurationException(
                $"Session '{session.Id}' character selector must specify exactly one valid index, id, or name.");
        }

        if (session.Policy is null
            || string.IsNullOrWhiteSpace(session.Policy.Id))
        {
            throw new HeadlessConfigurationException(
                $"Session '{session.Id}' requires a non-empty policy id.");
        }
    }

    private static void RejectExplicitNull(
        string sessionId,
        string propertyName,
        bool isPresent,
        JsonElement value)
    {
        if (isPresent && value.ValueKind == JsonValueKind.Null)
        {
            throw new HeadlessConfigurationException(
                $"Session '{sessionId}' field '{propertyName}' cannot be null; "
                + "supply a value when allowed or omit the field.");
        }
    }

    private static void ValidateLaunchContractFields(HeadlessSessionDescriptor session)
    {
        if (session.Plugins is { } plugins)
        {
            foreach (string? plugin in plugins)
            {
                if (string.IsNullOrWhiteSpace(plugin))
                {
                    throw new HeadlessConfigurationException(
                        $"Session '{session.Id}' plugins entries must be non-empty strings.");
                }
            }
        }

        if (session.LoginCommandDelayMs < 0)
        {
            throw new HeadlessConfigurationException(
                $"Session '{session.Id}' loginCommandDelayMs must be non-negative.");
        }

        if (session.StatusFile is not null
            && string.IsNullOrWhiteSpace(session.StatusFile))
        {
            throw new HeadlessConfigurationException(
                $"Session '{session.Id}' statusFile must be a non-empty path when present.");
        }
    }

    private static void ValidateCharacterOptions(HeadlessSessionDescriptor session)
    {
        if (session.CharacterOptions is not { } declared)
            return;

        foreach (string name in declared.Keys)
        {
            if (!AllowedCharacterOptionNames.Contains(name))
            {
                throw new HeadlessConfigurationException(
                    $"Session '{session.Id}' characterOptions declares "
                    + $"'{name}', which is not a bot-declarable character "
                    + "option name.");
            }
        }

        if (declared.TryGetValue(
                nameof(CharacterOptionId.IgnoreFellowshipRequests), out bool ignoreFellowship)
            && ignoreFellowship
            && declared.TryGetValue(
                nameof(CharacterOptionId.FellowshipAutoAcceptRequests), out bool autoAcceptFellowship)
            && autoAcceptFellowship)
        {
            throw new HeadlessConfigurationException(
                $"Session '{session.Id}' characterOptions declares both "
                + $"'{nameof(CharacterOptionId.IgnoreFellowshipRequests)}' and "
                + $"'{nameof(CharacterOptionId.FellowshipAutoAcceptRequests)}' "
                + "as true; retail's own mutual exclusion makes that "
                + "combination unsatisfiable — turning one on always clears "
                + "the other.");
        }
    }
}
