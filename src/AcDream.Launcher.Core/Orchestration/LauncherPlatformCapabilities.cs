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
    public const string LinuxGraphicalLaunchDisabledReason =
        "GUI launches require the Linux graphical client (Modern Runtime Slice L), "
        + "which is parked at L1 and will resume later. The launcher, character "
        + "probe, and headless sessions remain available on Linux.";

    public static LauncherPlatformCapabilities Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            return new LauncherPlatformCapabilities(
                IsWindows: true,
                IsLinux: false,
                CanRunHeadless: true,
                CanLaunchGraphicalClient: true,
                PlatformName: "Windows",
                GraphicalLaunchDisabledReason: null);
        }

        if (OperatingSystem.IsLinux())
        {
            return new LauncherPlatformCapabilities(
                IsWindows: false,
                IsLinux: true,
                CanRunHeadless: true,
                CanLaunchGraphicalClient: false,
                PlatformName: "Linux",
                GraphicalLaunchDisabledReason: LinuxGraphicalLaunchDisabledReason);
        }

        return new LauncherPlatformCapabilities(
            IsWindows: false,
            IsLinux: false,
            CanRunHeadless: false,
            CanLaunchGraphicalClient: false,
            PlatformName: "Unsupported",
            GraphicalLaunchDisabledReason:
                "Graphical client launches are supported on Windows. Linux support "
                + "requires Modern Runtime Slice L.");
    }

    public LauncherCapability ForLaunchMode(LaunchMode mode)
    {
        if (mode == LaunchMode.Headless)
        {
            return CanRunHeadless
                ? LauncherCapability.Available
                : LauncherCapability.Unavailable(
                    "Headless launches are supported only on Windows and Linux.");
        }

        return CanLaunchGraphicalClient
            ? LauncherCapability.Available
            : LauncherCapability.Unavailable(
                GraphicalLaunchDisabledReason
                    ?? "The graphical client is unavailable on this platform.");
    }
}
