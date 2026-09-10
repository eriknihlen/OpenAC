using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.Core.Orchestration;

public readonly record struct LauncherCapability(bool IsAvailable, string? Reason)
{
    public static LauncherCapability Available { get; } = new(true, null);

    public static LauncherCapability Unavailable(string reason) =>
        new(false, reason);
}

public sealed record LauncherPlatformCapabilities(
    bool IsWindows,
    bool IsLinux,
    bool CanRunHeadless,
    bool CanLaunchGraphicalClient,
    string PlatformName,
    string? GraphicalLaunchDisabledReason)
{
    public bool IsMacOS { get; init; }

    public const string UnsupportedPlatformGraphicalLaunchDisabledReason =
        "Graphical client launches are supported on Windows, Linux, and macOS.";

    public static LauncherPlatformCapabilities Detect()
        => ForOperatingSystem(
            OperatingSystem.IsWindows(),
            OperatingSystem.IsLinux(),
            OperatingSystem.IsMacOS());

    internal static LauncherPlatformCapabilities ForOperatingSystem(
        bool isWindows,
        bool isLinux,
        bool isMacOS)
    {
        if (isWindows)
        {
            return new LauncherPlatformCapabilities(
                IsWindows: true,
                IsLinux: false,
                CanRunHeadless: true,
                CanLaunchGraphicalClient: true,
                PlatformName: "Windows",
                GraphicalLaunchDisabledReason: null);
        }

        if (isLinux)
        {
            return new LauncherPlatformCapabilities(
                IsWindows: false,
                IsLinux: true,
                CanRunHeadless: true,
                CanLaunchGraphicalClient: true,
                PlatformName: "Linux",
                GraphicalLaunchDisabledReason: null);
        }

        if (isMacOS)
        {
            return new LauncherPlatformCapabilities(
                IsWindows: false,
                IsLinux: false,
                CanRunHeadless: true,
                CanLaunchGraphicalClient: true,
                PlatformName: "macOS",
                GraphicalLaunchDisabledReason: null)
            {
                IsMacOS = true,
            };
        }

        return new LauncherPlatformCapabilities(
            IsWindows: false,
            IsLinux: false,
            CanRunHeadless: false,
            CanLaunchGraphicalClient: false,
            PlatformName: "Unsupported",
            GraphicalLaunchDisabledReason: UnsupportedPlatformGraphicalLaunchDisabledReason);
    }

    public LauncherCapability ForLaunchMode(LaunchMode mode)
    {
        if (mode == LaunchMode.Headless)
        {
            return CanRunHeadless
                ? LauncherCapability.Available
                : LauncherCapability.Unavailable(
                    "Headless launches are supported only on Windows, Linux, and macOS.");
        }

        return CanLaunchGraphicalClient
            ? LauncherCapability.Available
            : LauncherCapability.Unavailable(
                GraphicalLaunchDisabledReason
                    ?? "The graphical client is unavailable on this platform.");
    }
}
