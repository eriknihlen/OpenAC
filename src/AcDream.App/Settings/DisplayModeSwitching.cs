using System;
using Silk.NET.GLFW;
using Silk.NET.Windowing;

namespace AcDream.App.Settings;

internal interface IDisplayModeSwitcher
{
    /// <summary>True while the window is a native fullscreen window (has a
    /// monitor attached).</summary>
    bool IsFullscreen { get; }

    (int Width, int Height)? CurrentFullscreenMode { get; }

    bool TryEnterFullscreen(int width, int height, out string? error);

    bool TryLeaveFullscreen(int width, int height, out string? error);
}

internal sealed unsafe class GlfwDisplayModeSwitcher : IDisplayModeSwitcher
{
    private readonly IWindow _window;

    private static readonly Lazy<Glfw> Api = new(Glfw.GetApi);

    private static (int X, int Y) _windowedPosition = (60, 60);

    private static bool MacOsHost { get; } =
        AcDream.App.Platform.GraphicalHostPlatformServices.DetectOperatingSystem()
            == AcDream.App.Platform.GraphicalHostOperatingSystem.MacOS;

    public GlfwDisplayModeSwitcher(IWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public bool IsFullscreen
    {
        get
        {
            try
            {
                WindowHandle* handle = Handle();
                if (handle is null) return false;
                return Api.Value.GetWindowMonitor(handle) is not null;
            }
            catch (GlfwException)
            {
                return false;
            }
        }
    }

    public (int Width, int Height)? CurrentFullscreenMode
    {
        get
        {
            try
            {
                WindowHandle* handle = Handle();
                if (handle is null) return null;
                Glfw glfw = Api.Value;
                Silk.NET.GLFW.Monitor* monitor = glfw.GetWindowMonitor(handle);
                if (monitor is null) return null;
                Silk.NET.GLFW.VideoMode* mode = glfw.GetVideoMode(monitor);
                return mode is null ? null : (mode->Width, mode->Height);
            }
            catch (GlfwException)
            {
                return null;
            }
        }
    }

    public bool TryEnterFullscreen(int width, int height, out string? error)
    {
        error = null;
        WindowHandle* handle = Handle();
        if (handle is null)
        {
            error = "no native GLFW window handle";
            return false;
        }

        try
        {
            Glfw glfw = Api.Value;
            Silk.NET.GLFW.Monitor* monitor = ResolveWindowMonitor(glfw, handle);
            if (monitor is null)
            {
                error = "no monitor";
                return false;
            }

            if (!TryFindRefreshRate(glfw, monitor, width, height, out int refresh))
            {
                error = $"mode {width}x{height} is not in the monitor's mode list";
                return false;
            }

            if (glfw.GetWindowMonitor(handle) is null)
            {
                // Remember the windowed placement so leaving fullscreen can
                // restore it (GLFW does not remember it for us).
                glfw.GetWindowPos(handle, out int x, out int y);
                _windowedPosition = (x, y);
            }

            // GLFW_AUTO_ICONIFY restores the desktop video mode on focus loss.
            if (MacOsHost)
            {
                glfw.SetWindowAttrib(
                    handle,
                    WindowAttributeSetter.AutoIconify,
                    false);
            }

            glfw.SetWindowMonitor(handle, monitor, 0, 0, width, height, refresh);

            if (glfw.GetWindowMonitor(handle) is null)
            {
                error = "the mode switch did not take (GLFW reports no monitor attached)";
                return false;
            }

            Console.WriteLine(
                $"display: fullscreen mode switch {width}x{height}@{refresh}");
            return true;
        }
        catch (GlfwException ex)
        {
            // Non-Windows platforms throw; Windows never reaches here.
            error = ex.Message;
            return false;
        }
    }

    public bool TryLeaveFullscreen(int width, int height, out string? error)
    {
        error = null;
        WindowHandle* handle = Handle();
        if (handle is null)
        {
            error = "no native GLFW window handle";
            return false;
        }

        try
        {
            Glfw glfw = Api.Value;
            if (glfw.GetWindowMonitor(handle) is null)
                return true;   // already windowed
            glfw.SetWindowMonitor(
                handle, null,
                _windowedPosition.X, _windowedPosition.Y,
                width, height, 0);

            if (glfw.GetWindowMonitor(handle) is not null)
            {
                error = "the window is still fullscreen (GLFW reports a monitor attached)";
                return false;
            }

            Console.WriteLine(
                $"display: left fullscreen to windowed {width}x{height}");
            return true;
        }
        catch (GlfwException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>The monitor the window belongs to: the attached monitor when
    /// fullscreen, else the window's own monitor per Silk's assignment
    /// (index into the GLFW monitor array — the SAME monitor
    /// <c>DisplayModeCatalog.InstallFromWindow</c> enumerated), falling back
    /// to the primary.</summary>
    private Silk.NET.GLFW.Monitor* ResolveWindowMonitor(Glfw glfw, WindowHandle* handle)
    {
        Silk.NET.GLFW.Monitor* attached = glfw.GetWindowMonitor(handle);
        if (attached is not null) return attached;

        int? index = _window.Monitor?.Index;
        if (index is int i && i >= 0)
        {
            Silk.NET.GLFW.Monitor** monitors = glfw.GetMonitors(out int count);
            if (monitors is not null && i < count)
                return monitors[i];
        }
        return glfw.GetPrimaryMonitor();
    }

    private static bool TryFindRefreshRate(
        Glfw glfw, Silk.NET.GLFW.Monitor* monitor, int width, int height, out int refresh)
    {
        refresh = 0;
        Silk.NET.GLFW.VideoMode* modes = glfw.GetVideoModes(monitor, out int count);
        if (modes is null) return false;
        for (int i = 0; i < count; i++)
        {
            if (modes[i].Width == width && modes[i].Height == height)
                refresh = Math.Max(refresh, modes[i].RefreshRate);
        }
        return refresh > 0;
    }

    private WindowHandle* Handle()
    {
        nint native = _window.Native?.Glfw ?? 0;
        return native == 0 ? null : (WindowHandle*)native;
    }
}
