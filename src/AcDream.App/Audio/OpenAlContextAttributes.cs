namespace AcDream.App.Audio;

/// <summary>
/// The attribute list we hand the audio backend when its playback context is
/// created, and the words we use to report what came back.
/// </summary>
/// <remarks>
/// The one thing we ask for is that the backend's output limiter stays off. A
/// limiter reacts to a loud moment by pulling the whole mix down and letting it
/// back up afterwards, so one burst of sounds makes everything else audibly
/// duck and swell. The game's own mixer never did that — it simply clips — so
/// the limiter is turned off wherever the device lets us and every sound keeps
/// the level it was mixed at.
/// </remarks>
internal static class OpenAlContextAttributes
{
    /// <summary>
    /// The device extension that exposes the output limiter as a context
    /// attribute. Without it the limiter cannot be asked about or turned off.
    /// </summary>
    internal const string OutputLimiterExtension = "ALC_SOFT_output_limiter";

    /// <summary>
    /// The attribute key selecting the output limiter. The binding we use does
    /// not name it, so it is spelled out here.
    /// </summary>
    internal const int OutputLimiter = 0x1999;

    /// <summary>The attribute value meaning "off".</summary>
    internal const int Off = 0;

    /// <summary>An attribute list ends with a zero key.</summary>
    internal const int EndOfList = 0;

    /// <summary>
    /// The value we put in the buffer before asking the device, so that a device
    /// which writes nothing can be told apart from one that answers "off".
    /// </summary>
    internal const int Unanswered = int.MinValue;

    /// <summary>
    /// What a limiter read actually told us. The value counts only when the
    /// device wrote one and raised no error; anything else is unknown, which is
    /// what the startup line then says. Reporting a silent non-answer as "off"
    /// would be a lie in the one line whose job is to report the limiter.
    /// </summary>
    internal static int? ReadLimiterState(int value, bool errored) =>
        errored || value == Unanswered ? null : value;

    /// <summary>
    /// The attribute list for a new context: limiter off when the device can be
    /// asked, and nothing at all when it cannot.
    /// </summary>
    internal static int[]? Build(bool outputLimiterControllable) =>
        outputLimiterControllable
            ? [OutputLimiter, Off, EndOfList]
            : null;

    /// <summary>
    /// The single startup line reporting what the limiter was doing and what it
    /// is doing now.
    /// </summary>
    internal static string Describe(bool outputLimiterControllable, int? before, int? after) =>
        outputLimiterControllable
            ? $"[audio] output limiter: was {State(before)}, now {State(after)}"
            : "[audio] output limiter: this device does not let us turn it off";

    private static string State(int? value) => value switch
    {
        null => "unknown",
        Off => "off",
        _ => "on",
    };
}
