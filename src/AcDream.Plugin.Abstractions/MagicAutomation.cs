namespace AcDream.Plugin.Abstractions;

/// <summary>
/// The outcome the server reported for the most recent spell the local
/// player finished casting. It is a single latched slot, not a queue: each
/// new completion overwrites the previous one.
/// </summary>
/// <param name="Revision">
/// Counts up by one every time a new cast completes, so a plugin can tell
/// a fresh completion from one it has already seen. Zero means no cast has
/// completed yet this session.
/// </param>
/// <param name="SpellId">The spell that finished casting.</param>
/// <param name="TargetObjectId">
/// The object the spell was cast at, or zero for an untargeted spell.
/// </param>
/// <param name="WeenieError">
/// The server's error code for the cast; zero means the server accepted it.
/// </param>
public readonly record struct PluginCastCompletion(
    long Revision,
    uint SpellId,
    uint TargetObjectId,
    uint WeenieError)
{
    /// <summary>
    /// True when a cast has actually completed and the server reported no
    /// error for it.
    /// </summary>
    public bool IsSuccess => Revision != 0 && WeenieError == 0u;
}
