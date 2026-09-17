using AcDream.App.Plugins;
using Silk.NET.Windowing;

namespace AcDream.App.Tests.Plugins;

/// <summary>
/// A minimal stand-in for the native window, used only to exercise
/// WindowPluginHostWindow. Deliberately implements the narrow
/// IPluginHostWindowTarget seam rather than Silk.NET's much larger
/// IWindow, exactly why that seam exists.
/// </summary>
internal sealed class FakeHostWindowTarget : IPluginHostWindowTarget
{
    private WindowState _state = WindowState.Normal;

    /// <summary>
    /// When set and it returns false, models a write that does not stick
    /// at all (a platform that refuses the state change, or no window
    /// focus) -- the setter runs, but the backing state does not follow.
    /// </summary>
    public Func<WindowState, bool>? OnSetWindowState { get; set; }

    /// <summary>
    /// When set, overrides what state actually lands after a write that
    /// OnSetWindowState (if present) accepted -- models Silk.NET's own
    /// GLFW-backed WindowState getter, which reports Fullscreen/Maximized
    /// ahead of ever reporting Normal: un-minimizing a window that was
    /// fullscreen or maximized before it was minimized reads back as
    /// Fullscreen/Maximized again, never as Normal.
    /// </summary>
    public Func<WindowState, WindowState>? OverrideResultingState { get; set; }

    public bool ThrowOnSetWindowState { get; set; }
    public bool ThrowOnClose { get; set; }

    public int SetWindowStateCount { get; private set; }
    public int CloseCount { get; private set; }

    public WindowState WindowState
    {
        get => _state;
        set
        {
            SetWindowStateCount++;
            if (ThrowOnSetWindowState)
                throw new InvalidOperationException("fake window state write failed");
            if (OnSetWindowState is not null && !OnSetWindowState(value))
                return;
            _state = OverrideResultingState is null
                ? value
                : OverrideResultingState(value);
        }
    }

    public void Close()
    {
        if (ThrowOnClose)
            throw new InvalidOperationException("fake close failed");
        CloseCount++;
    }
}
