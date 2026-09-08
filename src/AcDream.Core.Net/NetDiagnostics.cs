using System.Globalization;
namespace AcDream.Core.Net;

/// <summary>
/// Direction mask for the N5 <c>LossyTransportDecorator</c>
/// (<c>ACDREAM_NET_DROP_DIR</c>): drop outbound datagrams, inbound
/// datagrams, or both.
/// </summary>
public enum NetDropDirection
{
    Out,
    In,
    Both,
}

public static class NetDiagnostics
{
    public static bool ProbeNet { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_NET") == "1";

    public static int NetDropPercent { get; set; } =
        ParseDropPercent(
            Environment.GetEnvironmentVariable("ACDREAM_NET_DROP_PCT"));

    /// <summary>
    /// <c>ACDREAM_NET_DROP_SEED</c> (int, default 1) — the decorator's PRNG
    /// seed. Same seed ⇒ identical per-direction drop pattern.
    /// </summary>
    public static int NetDropSeed { get; set; } =
        int.TryParse(
            Environment.GetEnvironmentVariable("ACDREAM_NET_DROP_SEED"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int seed)
            ? seed
            : 1;

    /// <summary>
    /// <c>ACDREAM_NET_DROP_DIR</c> (<c>out</c> | <c>in</c> | <c>both</c>,
    /// default <c>both</c>) — which directions the decorator drops.
    /// </summary>
    public static NetDropDirection NetDropDir { get; set; } =
        ParseDropDirection(
            Environment.GetEnvironmentVariable("ACDREAM_NET_DROP_DIR"));

    internal static int ParseDropPercent(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent)
        && percent is >= 0 and <= 100
            ? percent
            : 0;

    internal static NetDropDirection ParseDropDirection(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "out" => NetDropDirection.Out,
            "in" => NetDropDirection.In,
            _ => NetDropDirection.Both,
        };

    public static bool ProbeReveal { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_REVEAL") == "1";
}
