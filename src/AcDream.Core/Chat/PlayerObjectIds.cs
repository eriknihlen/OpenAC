namespace AcDream.Core.Chat;

/// <summary>
/// The range of object ids the server gives player characters. Chat uses it
/// to tell another player from a creature or an item that speaks: only a
/// player is someone to answer or to click to send a tell.
/// </summary>
public static class PlayerObjectIds
{
    /// <summary>The lowest id a player character can have.</summary>
    public const uint First = 0x50000001u;

    /// <summary>The highest id a player character can have.</summary>
    public const uint Last = 0x6FFFFFFFu;

    /// <summary>Whether <paramref name="objectId"/> is a player character's id.</summary>
    public static bool IsPlayer(uint objectId) =>
        objectId is >= First and <= Last;
}
