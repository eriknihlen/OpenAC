using System.Globalization;
using System;
using AcDream.Core.World;

namespace AcDream.App.Streaming;

internal static class StreamingDiagnostics
{
    internal const int DefaultTunnelFreezeFrame = 72;

    public static int? RevealRadiusOverride { get; } =
        ParseRadius(
            Environment.GetEnvironmentVariable("ACDREAM_PROBE_REVEAL_RADIUS"));

    public static StreamingRevealWindow ApplyRevealRadiusOverride(
        StreamingRevealWindow window) =>
        ApplyRevealRadiusOverride(window, RevealRadiusOverride);

    public static StreamingRevealWindow ApplyRevealRadiusOverride(
        StreamingRevealWindow window,
        int? overrideRadius)
    {
        if (overrideRadius is not { } radius)
            return window;

        int far = Math.Max(0, radius);
        return new StreamingRevealWindow(
            Math.Clamp(window.NearRadius, 0, far),
            far);
    }

    public static bool ProbeRevealTiming { get; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_REVEAL_TIMING") == "1";

    public static int? TunnelFreezeFrame { get; } = ParseTunnelFreezeFrame(
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_TUNNEL_FREEZE"));

    internal static int? ParseRadius(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        && value >= 1
            ? value
            : null;

    internal static int? ParseTunnelFreezeFrame(string? raw)
    {
        if (string.Equals(raw, "1", StringComparison.Ordinal)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultTunnelFreezeFrame;
        }

        return int.TryParse(
                raw,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int frame)
            && frame >= 2
            && frame <= TeleportAnimSequencer.TunnelEndFrame
                ? frame
                : null;
    }
}
