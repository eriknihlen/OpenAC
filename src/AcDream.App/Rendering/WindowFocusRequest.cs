using Silk.NET.GLFW;
using Silk.NET.Windowing;

namespace AcDream.App.Rendering;

/// <summary>
/// Brings a window to the front with the keyboard. A desktop may refuse:
/// Wayland gives focus only to a request it handed a token for, and some X11
/// window managers prevent focus stealing. The window then stays where it
/// opened, as it would without the request, and the refusal is logged.
/// </summary>
internal static class WindowFocusRequest
{
    public static void BringToFront(IView window, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            window.Focus();
        }
        catch (GlfwException refused)
        {
            log($"window: the desktop kept the keyboard elsewhere ({refused.Message})");
        }
    }
}
