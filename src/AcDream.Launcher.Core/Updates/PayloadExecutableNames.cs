namespace AcDream.Launcher.Core.Updates;

public static class PayloadExecutableNames
{
    public const string GraphicalHost = "AcDream.App";

    public const string HeadlessHost = "acdream-headless";

    /// <summary>The launcher itself (<c>launcher-*.zip</c>).</summary>
    public const string Launcher = "acdream-launcher";

    /// <summary>
    /// The co-deployed bake tool (<c>launcher-*.zip</c>). It is packed beside
    /// the launcher but is not required to ACTIVATE one, so it appears in the
    /// payload set and not in the required set.
    /// </summary>
    public const string BakeTool = "acdream-bake";

    public static string SuffixForRid(string rid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);
        return rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty;
    }

    public static string SuffixForCurrentOs() =>
        OperatingSystem.IsWindows() ? ".exe" : string.Empty;

    public static IReadOnlyList<string> ForPayload(string rid, bool launcherPayload)
    {
        string suffix = SuffixForRid(rid);
        return launcherPayload
            ? [Launcher + suffix, BakeTool + suffix]
            : [GraphicalHost + suffix, HeadlessHost + suffix];
    }

    /// <summary>
    /// The executables whose absence invalidates the payload outright.
    /// </summary>
    public static IReadOnlyList<string> RequiredForPayload(string rid, bool launcherPayload)
    {
        string suffix = SuffixForRid(rid);
        return launcherPayload
            ? [Launcher + suffix]
            : [GraphicalHost + suffix, HeadlessHost + suffix];
    }
}
