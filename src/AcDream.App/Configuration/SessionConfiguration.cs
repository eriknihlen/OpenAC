using System.Text.Json.Serialization;

namespace AcDream.App.Configuration;

internal sealed class SessionConfiguration
{
    [JsonRequired]
    public int Version { get; init; }

    public SessionProcessSettings? Process { get; init; }

    [JsonRequired]
    public List<SessionDescriptor?> Sessions { get; init; } = [];
}

internal sealed class SessionProcessSettings
{
    public SessionContentDescriptor? Content { get; init; }

    public SessionProcessPathOverrides? Paths { get; init; }
}

internal sealed class SessionProcessPathOverrides
{
    public string? ConfigDirectory { get; init; }
    public string? DataDirectory { get; init; }
    public string? CacheDirectory { get; init; }
}

internal sealed class SessionContentDescriptor
{
    [JsonRequired]
    public string DatDirectory { get; init; } = string.Empty;

    [JsonRequired]
    public string PreparedAssetPath { get; init; } = string.Empty;

    public string? PreparedAssetOverlayPath { get; init; }

    public uint? PreparedAssetBaseRecipeVersion { get; init; }

    public uint? PreparedAssetEffectiveRecipeVersion { get; init; }
}

internal sealed record SessionDescriptor
{
    [JsonRequired]
    public string Id { get; init; } = string.Empty;

    [JsonRequired]
    public SessionEndpointDescriptor Endpoint { get; init; } = new();

    [JsonRequired]
    public string Account { get; init; } = string.Empty;

    public SessionCharacterSelectorDescriptor? Character { get; init; }

    public SessionPolicyDescriptor? Policy { get; init; }

    public string? Mode { get; init; }

    [JsonRequired]
    public SessionCredentialDescriptor Credential { get; init; } = new();

    /// <summary>Accepted-but-ignored by App; Headless's own loader owns the
    /// allow-list semantics for this field (OP7 D8).</summary>
    public Dictionary<string, bool>? CharacterOptions { get; init; }

    /// <summary>LA1/LA5: plugin ids to load. Absent = load all; explicit
    /// empty = load none.</summary>
    public List<string>? Plugins { get; init; }

    public List<string>? LoginCommands { get; init; }

    public int LoginCommandDelayMs { get; init; } = 500;

    public string? StatusFile { get; init; }
}

internal sealed class SessionEndpointDescriptor
{
    [JsonRequired]
    public string Host { get; init; } = string.Empty;

    [JsonRequired]
    public int Port { get; init; }
}

internal sealed class SessionCharacterSelectorDescriptor
{
    public int? Index { get; init; }
    public uint? Id { get; init; }
    public string? Name { get; init; }
}

/// <summary>Loose by design: App never inspects the policy's shape beyond
/// "does this document parse" — <c>Id</c>/<c>Role</c> stay untyped strings so
/// this DTO never has to track Headless's own policy-id/role vocabulary.</summary>
internal sealed class SessionPolicyDescriptor
{
    public string? Id { get; init; }
    public string? Role { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<SessionCredentialProviderKind>))]
internal enum SessionCredentialProviderKind
{
    Environment,
    StandardInput,
    File,
}

internal sealed class SessionCredentialDescriptor
{
    [JsonRequired]
    public SessionCredentialProviderKind Provider { get; init; }

    [JsonRequired]
    public string Reference { get; init; } = string.Empty;
}
