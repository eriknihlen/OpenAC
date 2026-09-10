using System.Runtime.InteropServices;

namespace AcDream.Launcher.Core.Updates;

public static class LauncherRuntimeIdentity
{
    public static string DetectRid()
        => DetectRid(
            OperatingSystem.IsWindows(),
            OperatingSystem.IsLinux(),
            OperatingSystem.IsMacOS(),
            RuntimeInformation.ProcessArchitecture);

    internal static string DetectRid(
        bool isWindows,
        bool isLinux,
        bool isMacOS,
        Architecture architecture)
    {
        string os = isWindows
            ? "win"
            : isLinux
                ? "linux"
                : isMacOS
                    ? "osx"
                : throw new PlatformNotSupportedException(
                    "The launcher updater supports Windows, Linux, and macOS only.");
        string architectureName = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                $"The launcher updater does not support {architecture}."),
        };
        return $"{os}-{architectureName}";
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
