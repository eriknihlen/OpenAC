using System.Text.Json.Serialization;

namespace AcDream.Headless.Configuration;

internal sealed class HeadlessConfiguration
{
    [JsonRequired]
    public int Version { get; init; }

    public HeadlessProcessSettings Process { get; init; } = new();

    [JsonRequired]
    public List<HeadlessSessionDescriptor?> Sessions { get; init; } = [];
}

internal sealed class HeadlessProcessSettings
{
    public HeadlessPathOverrides Paths { get; init; } = new();

    public HeadlessContentDescriptor? Content { get; init; }
}

internal sealed class HeadlessContentDescriptor
{
    [JsonRequired]
    public string DatDirectory { get; init; } = string.Empty;

    [JsonRequired]
    public string PreparedAssetPath { get; init; } = string.Empty;

    public string? PreparedAssetOverlayPath { get; init; }

    public uint? PreparedAssetBaseRecipeVersion { get; init; }

    public uint? PreparedAssetEffectiveRecipeVersion { get; init; }
}

internal sealed record HeadlessSessionDescriptor
{
    [JsonRequired]
    public string Id { get; init; } = string.Empty;

    [JsonRequired]
    public HeadlessEndpointDescriptor Endpoint { get; init; } = new();

    [JsonRequired]
    public string Account { get; init; } = string.Empty;

    public HeadlessSessionMode? Mode { get; init; }

    public HeadlessCharacterSelector? Character { get; init; }

    public HeadlessBotPolicyDescriptor? Policy { get; init; }

    [JsonRequired]
    public HeadlessCredentialReference Credential { get; init; } = new();

    public Dictionary<string, bool>? CharacterOptions { get; init; }

    public List<string>? Plugins { get; init; }

    public List<string>? LoginCommands { get; init; }

    public int LoginCommandDelayMs { get; init; } = 500;

    public string? StatusFile { get; init; }
}

internal sealed class HeadlessEndpointDescriptor
{
    [JsonRequired]
    public string Host { get; init; } = string.Empty;

    [JsonRequired]
    public int Port { get; init; }
}

internal sealed class HeadlessCharacterSelector
{
    public int? Index { get; init; }
    public uint? Id { get; init; }
    public string? Name { get; init; }
}

internal sealed class HeadlessBotPolicyDescriptor
{
    [JsonRequired]
    public string Id { get; init; } = string.Empty;

    public HeadlessBotPolicyRole? Role { get; init; }
}

internal enum HeadlessSessionMode
{
    Probe,
}

[JsonConverter(typeof(JsonStringEnumConverter<HeadlessBotPolicyRole>))]
internal enum HeadlessBotPolicyRole
{
    Leader,

    /// <summary>Fellowship recruit / allegiance vassal — gets recruited and
    /// swears to the Leader bot.</summary>
    Recruit,
}

[JsonConverter(typeof(JsonStringEnumConverter<HeadlessCredentialProviderKind>))]
internal enum HeadlessCredentialProviderKind
{
    Environment,
    StandardInput,
    File,
}

internal sealed class HeadlessCredentialReference
{
    [JsonRequired]
    public HeadlessCredentialProviderKind Provider { get; init; }

    [JsonRequired]
    public string Reference { get; init; } = string.Empty;
}
