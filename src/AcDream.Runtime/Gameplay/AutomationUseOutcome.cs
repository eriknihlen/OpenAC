namespace AcDream.Runtime.Gameplay;

/// <summary>
/// The plugin-facing outcome of a walk-then-use attempt on a world object.
/// </summary>
internal enum AutomationUseOutcome
{
    Started,
    Busy,
    NotUseable,
    NotInWorld,

    /// <summary>
    /// The outbound send itself was rejected by the transport layer (not
    /// a busy gate, not an unusable target) -- distinct from Busy so a
    /// caller does not read it as "try again shortly".
    /// </summary>
    Unavailable,

    /// <summary>
    /// The use meant picking the object up, and no pack the character has
    /// open has room for it. Nothing was sent.
    /// </summary>
    NoRoom,
}
