namespace AcDream.Plugin.Abstractions;

/// <summary>
/// One spell effect the client believes is currently running on a target,
/// with the time left before it lapses.
/// </summary>
/// <param name="TargetObjectId">The object the effect is running on.</param>
/// <param name="SpellId">The spell that produced the effect.</param>
/// <param name="Family">
/// The spell's family group. Spells in one family replace each other rather
/// than stacking, so this is what a plugin compares to decide whether a
/// re-cast would overwrite an effect already in place.
/// </param>
/// <param name="Quality">
/// The spell's difficulty rating as the client's spell table reports it.
/// </param>
/// <param name="IsUntargeted">
/// True when the spell takes no target of its own (a self or area effect).
/// </param>
/// <param name="SecondsRemaining">
/// Seconds left before the effect lapses, never below zero.
/// </param>
public readonly record struct PluginTrackedEnchantment(
    uint TargetObjectId,
    uint SpellId,
    uint Family,
    int Quality,
    bool IsUntargeted,
    double SecondsRemaining);

/// <summary>
/// Tracks how long the spell effects the client has seen cast will last, so
/// a plugin can decide when to re-cast. This is the client's own bookkeeping
/// from casts it observed, not an authoritative list from the server.
/// </summary>
public interface IEnchantmentAutomation
{
    /// <summary>
    /// Lists the still-running effects tracked for one target, ordered by
    /// spell family and then spell id. Lapsed entries are discarded first.
    /// Returns an empty list for object id zero, for a target with nothing
    /// tracked, or on a host that does not provide this surface.
    /// </summary>
    IReadOnlyList<PluginTrackedEnchantment> Capture(uint targetObjectId) =>
        Array.Empty<PluginTrackedEnchantment>();

    /// <summary>
    /// Records a cast the client did not observe itself, so its effect shows
    /// up in <see cref="Capture"/> for the duration given. Keeps whichever
    /// expiry is later if the same spell is already tracked on that target.
    /// Returns false for a zero target or spell id, a duration that is not a
    /// finite positive number, a spell the client's spell table does not
    /// know, or a host that does not provide this surface.
    /// </summary>
    bool ReportCast(
        uint targetObjectId,
        uint spellId,
        double durationSeconds) => false;
}
