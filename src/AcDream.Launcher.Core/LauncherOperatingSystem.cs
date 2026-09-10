using System.Runtime.Versioning;

namespace AcDream.Launcher.Core;

internal static class LauncherOperatingSystem
{
    [UnsupportedOSPlatformGuard("windows")]
    public static bool IsUnix =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
}
