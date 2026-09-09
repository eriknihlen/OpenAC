using System.Text.Json.Serialization;

namespace AcDream.Launcher.Core.Profiles;

public sealed class LauncherProfileDocument
{
    [JsonRequired]
    public int Version { get; set; } = LauncherProfileStore.CurrentVersion;

    public List<ServerProfile> Servers { get; set; } = [];

    // Null identifies documents created before the shared user list was introduced.
    public List<LauncherUser>? Users { get; set; }
}

public sealed record LauncherUser(string Account, string Password);
