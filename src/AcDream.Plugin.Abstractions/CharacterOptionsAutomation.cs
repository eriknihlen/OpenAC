namespace AcDream.Plugin.Abstractions;

/// <summary>What became of a request to change one character option.</summary>
public enum PluginCharacterOptionStatus
{
    /// <summary>
    /// Nothing was changed: the character is not in the world, or there is
    /// no session to tell.
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// The option now has the value asked for, and the change goes to the
    /// server the way a change on the character options page does.
    /// </summary>
    Accepted,

    /// <summary>No character option has that name.</summary>
    UnknownOption,
}

/// <summary>
/// The outcome of one character option change. Branch on
/// <see cref="Status"/>; <see cref="Notice"/> is a one-line reason for a
/// person and is not a stable identifier.
/// </summary>
/// <param name="Status">What happened.</param>
/// <param name="Notice">Why, when there is something worth saying.</param>
public readonly record struct PluginCharacterOptionResult(
    PluginCharacterOptionStatus Status,
    string? Notice = null)
{
    /// <summary>Whether the option now has the value asked for.</summary>
    public bool Accepted => Status == PluginCharacterOptionStatus.Accepted;
}

/// <summary>
/// The character's own options: the on/off switches of the client's
/// character options page (accepting gifts, auto-accepting fellowship
/// requests, main pack preferred, showing the cloak, ...), which the server
/// keeps with the character. An option is named the way that page's
/// settings are named, such as <c>AllowGive</c> or
/// <c>FellowshipAutoAcceptRequests</c>, compared without regard to case;
/// <see cref="Names"/> lists them. A host that has not bound this surface
/// answers every call as unavailable.
/// </summary>
public interface ICharacterOptionsAutomation
{
    /// <summary>
    /// Whether the character is in the world, so options can be read and
    /// changed.
    /// </summary>
    bool IsAvailable => false;

    /// <summary>
    /// Every option name this client knows, in the options' own order. Empty
    /// on a host that does not offer this surface.
    /// </summary>
    IReadOnlyList<string> Names => Array.Empty<string>();

    /// <summary>
    /// Reads one option's current value. Returns false when no option has
    /// that name or the character is not in the world.
    /// </summary>
    bool TryGet(string name, out bool value)
    {
        value = false;
        return false;
    }

    /// <summary>
    /// Turns one option on or off through the same route the character
    /// options page uses: the client's copy changes at once, and the server
    /// is told either straight away or with the client's next save of the
    /// whole option set, depending on the option. Setting an option to the
    /// value it already has is accepted and sends nothing.
    /// </summary>
    PluginCharacterOptionResult Set(string name, bool value) =>
        new(PluginCharacterOptionStatus.Unavailable);
}
