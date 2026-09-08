using System.Text.Json.Serialization;

namespace AcDream.Launcher.Core.Profiles;

public sealed class LauncherProfileDocument
{
    [JsonRequired]
    public int Version { get; set; } = LauncherProfileStore.CurrentVersion;

    public List<ServerProfile> Servers { get; set; } = [];
}
