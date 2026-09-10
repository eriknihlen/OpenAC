using Silk.NET.GLFW;
using Silk.NET.Core.Loader;
using Silk.NET.Windowing.Glfw;

namespace AcDream.App.Platform;

internal enum GraphicalDisplayProtocol
{
    Unknown,
    Windows,
    X11,
    Wayland,
    Cocoa,
    Automatic,
}

/// <summary>
/// Immutable process-start decision for Silk's GLFW 3.4 backend. Linux users
/// may force <c>x11</c> or <c>wayland</c> with
/// <c>ACDREAM_DISPLAY_PROTOCOL</c>; otherwise GLFW selects from the available
/// native backends. The decision is applied once before any window exists.
/// </summary>
internal sealed record GraphicalWindowBackendSelection(
    GraphicalDisplayProtocol RequestedProtocol,
    string Reason)
{
    internal const string EnvironmentVariable =
        "ACDREAM_DISPLAY_PROTOCOL";

    internal static GraphicalWindowBackendSelection Resolve(
        GraphicalHostOperatingSystem operatingSystem,
        Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (operatingSystem == GraphicalHostOperatingSystem.Windows)
        {
            return new(
                GraphicalDisplayProtocol.Windows,
                "Windows graphical host");
        }

        if (operatingSystem == GraphicalHostOperatingSystem.MacOS)
        {
            // GLFW exposes exactly one native backend on macOS, so the
            // X11/Wayland selection below has nothing to choose between.
            return new(
                GraphicalDisplayProtocol.Cocoa,
                "macOS graphical host");
        }

        string? configured = environment(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim().ToLowerInvariant() switch
            {
                "auto" => new(
                    GraphicalDisplayProtocol.Automatic,
                    $"{EnvironmentVariable}=auto"),
                "x11" => new(
                    GraphicalDisplayProtocol.X11,
                    $"{EnvironmentVariable}=x11"),
                "wayland" => new(
                    GraphicalDisplayProtocol.Wayland,
                    $"{EnvironmentVariable}=wayland"),
                _ => throw new InvalidOperationException(
                    $"{EnvironmentVariable} must be auto, x11, or wayland; " +
                    $"received '{configured}'."),
            };
        }

        string? sessionType = environment("XDG_SESSION_TYPE");
        string? waylandDisplay = environment("WAYLAND_DISPLAY");
        if (string.Equals(
                sessionType,
                "wayland",
                StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(waylandDisplay))
        {
            return new(
                GraphicalDisplayProtocol.Wayland,
                "XDG_SESSION_TYPE=wayland and WAYLAND_DISPLAY is set");
        }

        string? xDisplay = environment("DISPLAY");
        if (!string.IsNullOrWhiteSpace(xDisplay))
        {
            return new(
                GraphicalDisplayProtocol.X11,
                "DISPLAY is set");
        }

        if (!string.IsNullOrWhiteSpace(waylandDisplay))
        {
            return new(
                GraphicalDisplayProtocol.Wayland,
                "WAYLAND_DISPLAY is set");
        }

        return new(
            GraphicalDisplayProtocol.Automatic,
            "no X11 or Wayland environment was detected");
    }
}

internal static class GraphicalWindowBackendConfigurator
{
    private const int GlfwPlatformInitHint = 0x00050003;
    private const int GlfwAnyPlatform = 0x00060000;
    private const int GlfwWin32Platform = 0x00060001;
    private const int GlfwCocoaPlatform = 0x00060002;
    private const int GlfwWaylandPlatform = 0x00060003;
    private const int GlfwX11Platform = 0x00060004;

    private static readonly object Gate = new();
    private static GraphicalDisplayProtocol? _configuredProtocol;
    private static Glfw? _glfw;

    internal static void Configure(
        GraphicalHostPlatformServices platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        GraphicalDisplayProtocol requested =
            platform.WindowBackend.RequestedProtocol;

        lock (Gate)
        {
            if (_configuredProtocol is { } existing)
            {
                if (existing != requested)
                {
                    throw new InvalidOperationException(
                        "The GLFW platform is already configured as " +
                        $"{existing}; it cannot change to {requested} in " +
                        "the same process.");
                }
                return;
            }

            PreferPublishedNativeLibraries();
            GlfwWindowing.Use();
            // Silk's windowing backend initializes this exact singleton.
            // A separate Glfw.GetApi() instance would receive the hint but
            // would not own the window backend's process-global GLFW state.
            Glfw glfw = GlfwProvider.UninitializedGLFW.Value;
            glfw.InitHint(
                (InitHint)GlfwPlatformInitHint,
                requested switch
                {
                    GraphicalDisplayProtocol.Windows =>
                        GlfwWin32Platform,
                    GraphicalDisplayProtocol.X11 =>
                        GlfwX11Platform,
                    GraphicalDisplayProtocol.Wayland =>
                        GlfwWaylandPlatform,
                    GraphicalDisplayProtocol.Cocoa =>
                        GlfwCocoaPlatform,
                    GraphicalDisplayProtocol.Automatic =>
                        GlfwAnyPlatform,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(requested)),
                });
            if (platform.OperatingSystem == GraphicalHostOperatingSystem.Windows)
                Win32GlfwActiveWindowGuard.Install();
            _glfw = glfw;
            _configuredProtocol = requested;
        }
    }

    internal static bool TryGetConfiguredApi(out Glfw? glfw)
    {
        lock (Gate)
        {
            glfw = _glfw;
            return glfw is not null;
        }
    }

    private static void PreferPublishedNativeLibraries()
    {
        if (PathResolver.Default is not DefaultPathResolver resolver)
        {
            throw new InvalidOperationException(
                "Silk.NET's default native path resolver is unavailable.");
        }

        List<Func<string, IEnumerable<string>>> resolvers =
            resolver.Resolvers;
        resolvers.Remove(DefaultPathResolver.BaseDirectoryResolver);
        resolvers.Insert(
            0,
            DefaultPathResolver.BaseDirectoryResolver);
    }
}

internal static unsafe class GlfwNativePlatformProbe
{
    private const int GlfwWin32Platform = 0x00060001;
    private const int GlfwCocoaPlatform = 0x00060002;
    private const int GlfwWaylandPlatform = 0x00060003;
    private const int GlfwX11Platform = 0x00060004;

    internal static GraphicalDisplayProtocol GetActiveProtocol(
        GraphicalHostOperatingSystem operatingSystem)
    {
        if (operatingSystem == GraphicalHostOperatingSystem.Windows)
            return GraphicalDisplayProtocol.Windows;
        if (operatingSystem == GraphicalHostOperatingSystem.MacOS)
            return GraphicalDisplayProtocol.Cocoa;

        if (!GraphicalWindowBackendConfigurator.TryGetConfiguredApi(
                out Glfw? glfw)
            || glfw is null
            || !glfw.Context.TryGetProcAddress(
                "glfwGetPlatform",
                out nint export))
        {
            return GraphicalDisplayProtocol.Unknown;
        }

        int platform =
            ((delegate* unmanaged[Cdecl]<int>)export)();
        return platform switch
        {
            GlfwX11Platform => GraphicalDisplayProtocol.X11,
            GlfwWaylandPlatform => GraphicalDisplayProtocol.Wayland,
            GlfwWin32Platform => GraphicalDisplayProtocol.Windows,
            GlfwCocoaPlatform => GraphicalDisplayProtocol.Cocoa,
            _ => GraphicalDisplayProtocol.Unknown,
        };
    }

    internal static string GetVersion(
        GraphicalHostOperatingSystem operatingSystem)
    {
        if (!GraphicalWindowBackendConfigurator.TryGetConfiguredApi(
                out Glfw? glfw)
            || glfw is null)
        {
            return "unknown";
        }

        return glfw.GetVersionString() ?? "unknown";
    }
}
