using System.Text.Json.Serialization;

namespace AcDream.Launcher.Core.Profiles;

public sealed class LauncherProfileDocument
{
    [JsonRequired]
    public int Version { get; set; } = LauncherProfileStore.CurrentVersion;

    public List<ServerProfile> Servers { get; set; } = [];

    /// <summary>Offers a beta-only plugin in Discover and Add from URL. Omitted when false, its
    /// default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ShowBetaPlugins { get; set; }
}
