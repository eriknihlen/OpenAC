using System.Text.Json.Serialization;

namespace AcDream.Launcher.Core.Profiles;

public sealed class AccountProfile
{
    [JsonRequired]
    public string Account { get; set; } = string.Empty;

    [JsonRequired]
    public string Password { get; set; } = string.Empty;

    /// <summary>Profile tags such as "Main" or "Bots"; the main window filters accounts by them.</summary>
    public List<string> Profiles { get; set; } = [];

    /// <summary>The plugins every character on this account launches with, unless the character
    /// has its own list.</summary>
    public List<string> Plugins { get; set; } = [];

    /// <summary>Commands run in order after any character on this account logs in.</summary>
    public List<string> LoginCommands { get; set; } = [];

    public List<CharacterProfile> Characters { get; set; } = [];

    /// <summary>The character this account's row is set to launch, or null for the character screen.
    /// Left out of the file while unset.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? SelectedCharacter { get; set; }

    /// <summary>The launch mode this account's row is set to. Left out of the file while unset.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public LaunchMode? SelectedLaunchMode { get; set; }

    /// <summary>The plugins a character on this account launches with: its own list when it has
    /// one, the account's otherwise.</summary>
    public IReadOnlyList<string> PluginsFor(CharacterProfile? character) =>
        character?.Plugins ?? Plugins;
}
