using System.Runtime.InteropServices;

namespace AcDream.Launcher.Core.Updates;

public static class LauncherRuntimeIdentity
{
    public static string DetectRid()
    {
        string os = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsLinux()
                ? "linux"
                : throw new PlatformNotSupportedException(
                    "The launcher updater supports Windows and Linux only.");
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                $"The launcher updater does not support {RuntimeInformation.ProcessArchitecture}."),
        };
        return $"{os}-{architecture}";
    }

    internal static bool IsValidRid(string? rid) =>
        !string.IsNullOrEmpty(rid)
        && rid.Length <= 64
        && rid[0] is >= 'a' and <= 'z'
        && rid.All(character =>
            character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-');
}
