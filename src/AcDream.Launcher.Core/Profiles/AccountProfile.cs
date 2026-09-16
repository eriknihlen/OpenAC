using System.Text.Json.Serialization;

namespace AcDream.Launcher.Core.Profiles;

public sealed class AccountProfile
{
    [JsonRequired]
    public string Account { get; set; } = string.Empty;

    [JsonRequired]
    public string Password { get; set; } = string.Empty;

    public List<CharacterProfile> Characters { get; set; } = [];

    /// <summary>The character this account's row is set to launch, or null for the character screen.</summary>
    public string? SelectedCharacter { get; set; }

    /// <summary>The launch mode this account's row is set to.</summary>
    public LaunchMode? SelectedLaunchMode { get; set; }
}
