namespace AcDream.Plugin.Abstractions;

/// <summary>One character on the account's character list.</summary>
/// <param name="ObjectId">The character's id, used to pick it for login.</param>
/// <param name="Name">The character's name.</param>
/// <param name="ActiveIndex">Its slot on the account's list.</param>
/// <param name="IsPendingDelete">
/// Whether the character is scheduled for deletion and so cannot be played.
/// </param>
public readonly record struct PluginLoginCharacter(
    uint ObjectId,
    string Name,
    int ActiveIndex,
    bool IsPendingDelete);

/// <summary>
/// The account's character list and the client's own logout. A host that
/// has not bound this surface answers every call as unavailable.
/// </summary>
public interface ILoginAutomation
{
    /// <summary>
    /// Whether this surface is bound to a live session. False on a host
    /// that does not offer it.
    /// </summary>
    bool IsAvailable => false;

    /// <summary>
    /// The character the client will log in next, or 0 when none has been
    /// chosen.
    /// </summary>
    uint NextLoginObjectId => 0u;

    /// <summary>
    /// The account's characters, in list order. Empty before the server has
    /// sent the list, and on a host that does not offer this surface.
    /// </summary>
    IReadOnlyList<PluginLoginCharacter> CaptureRoster() =>
        Array.Empty<PluginLoginCharacter>();

    /// <summary>
    /// Chooses which character the client logs in next. Returns false when
    /// the id is not on the account's list or there is no session to tell.
    /// </summary>
    bool SetNextLogin(uint characterObjectId) => false;

    /// <summary>
    /// Forgets the chosen next character. Returns false when none was
    /// chosen or there is no session to tell.
    /// </summary>
    bool ClearNextLogin() => false;

    /// <summary>
    /// Whether a logout would be accepted right now. False when there is no
    /// in-world session, and while a teleport, portal entry or an earlier
    /// logout is still in flight.
    /// </summary>
    bool CanRequestLogout => false;

    /// <summary>
    /// Asks the client to log the character out. Returns true when the
    /// request was sent -- not that it has completed -- and false in every
    /// case <see cref="CanRequestLogout"/> is false.
    /// </summary>
    bool RequestLogout() => false;

    /// <summary>
    /// The client's own graceful logout: the same route the UI's logout
    /// control uses. Forwards to <see cref="RequestLogout"/>; returns false
    /// when there is no in-world session to log out of.
    /// </summary>
    bool Logout() => RequestLogout();
}
