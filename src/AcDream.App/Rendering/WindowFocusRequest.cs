using Silk.NET.GLFW;
using Silk.NET.Windowing;

namespace AcDream.App.Rendering;

/// <summary>
/// Asks for a window opened onto a playing session to come to the front with
/// the keyboard. GLFW already asks once when it shows a new window; this asks
/// again once the window has joined the session. The desktop decides either
/// way: Windows lets a process take the foreground only while it is allowed
/// to, Wayland gives focus only to a request it handed a token for, and some
/// X11 window managers prevent focus stealing. A refused request leaves the
/// window where it opened and is not reported (Windows refuses without an
/// error); only an error GLFW itself raises is logged, so it cannot end the
/// window's start.
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
        catch (GlfwException error)
        {
            log($"window: asking for the keyboard failed ({error.Message})");
        }
    }
}
