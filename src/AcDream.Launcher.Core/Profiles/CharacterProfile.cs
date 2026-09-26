using System.Text.Json.Serialization;

namespace AcDream.Launcher.Core.Profiles;

public sealed class CharacterProfile
{
    [JsonRequired]
    public string Name { get; set; } = string.Empty;

    public string? Id { get; set; }

    public LaunchMode LaunchMode { get; set; } = LaunchMode.GuiSelect;

    /// <summary>This character's own plugin list, used instead of its account's. Null (and left
    /// out of the file) while the character uses the account's list.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Plugins { get; set; }
}
