namespace AcDream.Launcher.Core.Launching;

public sealed class SessionConfigDocument
{
    public int Version { get; init; } = 1;

    public SessionProcessSettings Process { get; init; } = new();

    public List<SessionDescriptor> Sessions { get; init; } = [];
}

public sealed class SessionProcessSettings
{
    public SessionPathOverrides? Paths { get; init; }

    public SessionContentDescriptor Content { get; init; } = new();
}

/// <summary>All three members are optional overrides; a host resolves
/// its own default <c>ApplicationPathSet</c> when a member is
/// omitted.</summary>
public sealed class SessionPathOverrides
{
    public string? ConfigDirectory { get; init; }

    public string? DataDirectory { get; init; }

    public string? CacheDirectory { get; init; }
}


public sealed class SessionContentDescriptor
{
    public string DatDirectory { get; init; } = string.Empty;

    public string PreparedAssetPath { get; init; } = string.Empty;

    public string? PreparedAssetOverlayPath { get; init; }

    public uint? PreparedAssetBaseRecipeVersion { get; init; }

    public uint? PreparedAssetEffectiveRecipeVersion { get; init; }
}

public sealed class SessionDescriptor
{
    public string Id { get; init; } = string.Empty;

    public string? Mode { get; init; }

    public SessionEndpointDescriptor Endpoint { get; init; } = new();

    public string Account { get; init; } = string.Empty;

    public SessionCharacterSelector? Character { get; init; }

    /// <summary>Present only for a <c>headless</c> launch (the <c>idle</c>
    /// bot policy). Omitted for <c>gui</c>/<c>guiSelect</c>.</summary>
    public SessionPolicyDescriptor? Policy { get; init; }

    public SessionCredentialDescriptor Credential { get; init; } = new();

    public List<string>? Plugins { get; init; }

    public List<string>? LoginCommands { get; init; }

    public int? LoginCommandDelayMs { get; init; }

    public string StatusFile { get; init; } = string.Empty;
}

public sealed class SessionEndpointDescriptor
{
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; }
}

public sealed class SessionCharacterSelector
{
    public int? Index { get; init; }

    public uint? Id { get; init; }

    public string? Name { get; init; }
}

public sealed class SessionPolicyDescriptor
{
    public string Id { get; init; } = "idle";
}

public sealed class SessionCredentialDescriptor
{
    public string Provider { get; init; } = "standardInput";

    public string Reference { get; init; } = "session";
}
