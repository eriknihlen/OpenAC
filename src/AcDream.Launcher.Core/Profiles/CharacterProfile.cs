using System.Text.Json.Serialization;

namespace AcDream.Launcher.Core.Profiles;

public sealed class CharacterProfile
{
    [JsonRequired]
    public string Name { get; set; } = string.Empty;

    public string? Id { get; set; }

    public LaunchMode LaunchMode { get; set; } = LaunchMode.GuiSelect;

    public List<string> Plugins { get; set; } = [];

    public List<string> LoginCommands { get; set; } = [];
}
