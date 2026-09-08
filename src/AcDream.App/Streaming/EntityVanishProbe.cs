using System;

namespace AcDream.App.Streaming;

internal static class EntityVanishProbe
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_ENT") == "1";

    /// <summary>Player server guid, set once at world entry so the draw-side
    /// <c>[dyn]</c> line can single out the avatar without plumbing the guid
    /// through the render stack.</summary>
    public static uint PlayerGuid;

    public static void Log(string msg)
    {
        if (Enabled) Console.WriteLine(msg);
    }

    private static string _lastPlayerDyn = "";

    public static void LogPlayerDynOnChange(string status)
    {
        if (!Enabled) return;
        if (status == _lastPlayerDyn) return;
        _lastPlayerDyn = status;
        Console.WriteLine("[dyn] player " + status);
    }
}
