using AcDream.Plugin.Abstractions;
using Silk.NET.Input;

namespace AcDream.App.Plugins;

/// <summary>
/// The window's clipboard, reached through the same keyboard device the
/// retained text controls copy with. The keyboard is wired well after the
/// plugin host is built, so it is resolved on each call.
/// </summary>
public sealed class WindowPluginClipboard(
    Func<IKeyboard?> keyboard,
    Func<MainThreadDispatchQueue> dispatch)
    : IPluginClipboard
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(1);

    private readonly Func<IKeyboard?> _keyboard =
        keyboard ?? throw new ArgumentNullException(nameof(keyboard));
    private readonly Func<MainThreadDispatchQueue> _dispatch =
        dispatch ?? throw new ArgumentNullException(nameof(dispatch));

    public bool TrySetText(string text)
    {
        if (text is null)
            return false;
        IKeyboard? device;
        try
        {
            device = _keyboard();
        }
        catch
        {
            return false;
        }
        if (device is null)
            return false;

        bool written = false;
        void Write()
        {
            try
            {
                device.ClipboardText = text;
                // The underlying GLFW clipboard write can fail silently --
                // no exception, no falsy return -- when the platform
                // refuses it (or when it ran off the window's own thread,
                // which the dispatch queue below exists to prevent). Read
                // back what actually landed instead of trusting the
                // setter.
                //
                // This read-back is only a meaningful check on Windows.
                // X11 and Wayland clipboards are ownership-based: as long
                // as this process still owns the selection, GLFW's getter
                // just returns its own last-set string back, regardless of
                // whether anything reached the system clipboard -- so a
                // Linux build cannot detect the same silent-failure class
                // this way. That is an existing platform gap, not one this
                // change introduces or changes.
                written = device.ClipboardText == text;
            }
            catch
            {
                // No window focus, or the platform refused the clipboard.
                written = false;
            }
        }

        // GLFW clipboard calls are main-thread-only; a plugin's own event
        // handlers can run on whatever thread the plugin chose, so this
        // always marshals through the dispatch queue rather than trusting
        // the caller to already be on the right thread.
        return _dispatch().InvokeAndWait(Write, WriteTimeout) && written;
    }
}
