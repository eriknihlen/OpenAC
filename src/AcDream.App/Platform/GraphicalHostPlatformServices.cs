using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AcDream.App.Rendering;
using AcDream.Platform;

namespace AcDream.App.Platform;

internal enum GraphicalHostOperatingSystem
{
    Windows,
    Linux,
    MacOS,
}

internal static class RuntimePlatformGuard
{
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("macos")]
    internal static bool IsUnixRuntime =>
        System.OperatingSystem.IsLinux()
        || System.OperatingSystem.IsMacOS();
}

internal sealed record GraphicalNativeDependency(
    string Feature,
    string PublishedFileName);

internal sealed record GraphicalHostPlatformServices(
    GraphicalHostOperatingSystem OperatingSystem,
    Architecture ProcessArchitecture,
    string RuntimeIdentifier,
    GraphicalWindowBackendSelection WindowBackend,
    ApplicationPathSet Paths,
    IFramePacingWaiterFactory FramePacingWaiters,
    IReadOnlyList<GraphicalNativeDependency> NativeDependencies)
{
    internal static GraphicalHostPlatformServices Resolve()
    {
        GraphicalHostOperatingSystem operatingSystem =
            DetectOperatingSystem();
        Architecture architecture = RuntimeInformation.ProcessArchitecture;
        string runtimeIdentifier = ResolveRuntimeIdentifier(
            operatingSystem,
            architecture);

        return new GraphicalHostPlatformServices(
            operatingSystem,
            architecture,
            runtimeIdentifier,
            GraphicalWindowBackendSelection.Resolve(
                operatingSystem,
                Environment.GetEnvironmentVariable),
            ApplicationPathSet.Resolve(),
            new PlatformFramePacingWaiterFactory(operatingSystem),
            ResolveNativeDependencies(operatingSystem));
    }

    internal void ConfigureWindowBackend() =>
        GraphicalWindowBackendConfigurator.Configure(this);

    internal static GraphicalHostOperatingSystem DetectOperatingSystem()
    {
        if (System.OperatingSystem.IsWindows())
            return GraphicalHostOperatingSystem.Windows;
        if (System.OperatingSystem.IsLinux())
            return GraphicalHostOperatingSystem.Linux;
        if (System.OperatingSystem.IsMacOS())
            return GraphicalHostOperatingSystem.MacOS;

        throw new PlatformNotSupportedException(
            "acdream graphical hosting supports Windows, Linux, and macOS.");
    }

    private static string ResolveRuntimeIdentifier(
        GraphicalHostOperatingSystem operatingSystem,
        Architecture architecture)
    {
        string architectureName = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => throw new PlatformNotSupportedException(
                $"Unsupported graphical process architecture {architecture}."),
        };

        if (operatingSystem == GraphicalHostOperatingSystem.Linux
            && architecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                "The first acdream Linux graphical package supports linux-x64 only.");
        }

        string operatingSystemName = operatingSystem switch
        {
            GraphicalHostOperatingSystem.Windows => "win",
            GraphicalHostOperatingSystem.Linux => "linux",
            GraphicalHostOperatingSystem.MacOS => "osx",
            _ => throw new ArgumentOutOfRangeException(
                nameof(operatingSystem)),
        };
        return $"{operatingSystemName}-{architectureName}";
    }

    private static IReadOnlyList<GraphicalNativeDependency>
        ResolveNativeDependencies(
            GraphicalHostOperatingSystem operatingSystem) =>
        operatingSystem switch
        {
            GraphicalHostOperatingSystem.Windows =>
            [
                new("window/input", "glfw3.dll"),
                new("audio", "soft_oal.dll"),
            ],
            GraphicalHostOperatingSystem.Linux =>
            [
                new("window/input", "libglfw.so.3"),
                new("audio", "libopenal.so"),
            ],
            GraphicalHostOperatingSystem.MacOS =>
            [
                new("window/input", "libglfw.3.dylib"),
                new("audio", "libopenal.dylib"),
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(operatingSystem)),
        };
}
