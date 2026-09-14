using System.Text.Json.Serialization;

namespace AcDream.Launcher.Core.Profiles;

public sealed class AccountProfile
{
    [JsonRequired]
    public string Account { get; set; } = string.Empty;

    [JsonRequired]
    public string Password { get; set; } = string.Empty;

    public List<CharacterProfile> Characters { get; set; } = [];

    /// <summary>
    /// The main window's row for this account on this server, as last
    /// left: ticked for the batch launch, the character picked (null for
    /// character select) and headless or graphical. Written as soon as
    /// the row changes, so the launcher opens the way it was closed.
    /// </summary>
    public bool Selected { get; set; }

    public string? SelectedCharacter { get; set; }

    public bool Headless { get; set; }
}
