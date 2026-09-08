using System.Text.Json.Serialization;

namespace AcDream.Launcher.Core.Profiles;

public sealed class AccountProfile
{
    [JsonRequired]
    public string Account { get; set; } = string.Empty;

    [JsonRequired]
    public string Password { get; set; } = string.Empty;

    public List<CharacterProfile> Characters { get; set; } = [];
}
