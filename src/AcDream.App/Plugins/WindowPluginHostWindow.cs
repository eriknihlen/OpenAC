using AcDream.Plugin.Abstractions;
using Silk.NET.Windowing;

namespace AcDream.App.Plugins;

/// <summary>
/// Minimize/restore/close for the OS window that hosts the client, reached
/// through the same window the close button and the clipboard already use
/// (via the narrow <see cref="IPluginHostWindowTarget"/> seam, not the full
/// Silk.NET window surface). A plugin call can arrive on any thread; every
/// write is marshalled onto the window's own thread through
/// <see cref="MainThreadDispatchQueue"/> -- GLFW window-state and close
/// calls are documented main-thread-only, the same hazard the clipboard
/// write already guards against.
///
/// The success check is per operation, not a single "did WindowState end
/// up == desired" test: on Silk.NET's GLFW backend, a window own
/// WindowState getter reports Fullscreen or Maximized ahead of ever
/// reporting Normal, so un-minimizing a window that was fullscreen or
/// maximized before it was minimized never reads back as Normal. Forcing
/// WindowState to Normal on every Restore would also fight the display
/// mode switcher own tracking of fullscreen/maximized state. Restore
/// therefore means "make sure it is not minimized", not "make it Normal":
/// it writes Normal only when the window is currently minimized, and
/// succeeds whenever the window ends up anything other than minimized --
/// including a Restore call on a window that was never minimized to begin
/// with, which is a no-op success rather than an unnecessary write.
/// Silk's live state check tests iconified before maximized/fullscreen,
/// and reading WindowState resyncs its own cache from the native window,
/// which is why writing Normal here can never actually drop a fullscreen
/// window to windowed -- the very next read reports Fullscreen again.
/// </summary>
internal sealed class WindowPluginHostWindow(
    Func<IPluginHostWindowTarget?> window,
    Func<bool> isMinimized,
    Func<MainThreadDispatchQueue> dispatch)
    : IHostWindow
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(1);

    private const string NoWindowNotice = "the host has no window";
    private const string DidNotLandNotice = "the write did not take";
    private const string NotConfirmedNotice =
        "the call could not be confirmed on the window own thread";

    private readonly Func<IPluginHostWindowTarget?> _window =
        window ?? throw new ArgumentNullException(nameof(window));
    private readonly Func<bool> _isMinimized =
        isMinimized ?? throw new ArgumentNullException(nameof(isMinimized));
    private readonly Func<MainThreadDispatchQueue> _dispatch =
        dispatch ?? throw new ArgumentNullException(nameof(dispatch));

    /// <summary>
    /// Reads the cached minimized flag rather than the window own
    /// WindowState -- that read is itself a main-thread-only GLFW call, so
    /// the host keeps a copy current via the window own StateChanged event
    /// instead of touching Silk.NET from whatever thread a plugin calls
    /// this from.
    ///
    /// The read-back this flag (and Minimize own success check) relies
    /// on is not equally trustworthy on every platform: it is synchronous
    /// on Win32, arrives asynchronously over WM_STATE on X11 (so a call
    /// immediately after Minimize can briefly still read the old value),
    /// is animated on macOS/Cocoa (there is a brief window where the OS
    /// has not finished iconifying yet), and Wayland compositor protocol
    /// has no way to report iconification back to the client at all -- on
    /// Wayland, IsMinimized never becomes true and Minimize() always
    /// reports Unavailable even when the window did minimize. A plugin
    /// should treat an Unavailable result from Minimize as "unknown", not
    /// as "definitely still shown" -- do not retry in a loop on that
    /// signal alone.
    /// </summary>
    public bool IsMinimized => _isMinimized();

    public HostWindowResult Minimize()
    {
        IPluginHostWindowTarget? target = Resolve();
        if (target is null)
            return new HostWindowResult(HostWindowStatus.Unavailable, NoWindowNotice);

        bool succeeded = false;
        void Apply()
        {
            try
            {
                if (target.WindowState != WindowState.Minimized)
                    target.WindowState = WindowState.Minimized;
                succeeded = target.WindowState == WindowState.Minimized;
                // Silent by design: the write can fail to land (no window
                // focus, a platform refusal, or on Wayland a compositor that
                // never reports iconification back at all) -- read the state
                // back rather than trust the setter, matching
                // WindowPluginClipboard.
            }
            catch
            {
                succeeded = false;
            }
        }

        bool reached = _dispatch().InvokeAndWait(Apply, CallTimeout);
        return reached && succeeded
            ? new HostWindowResult(HostWindowStatus.Done)
            : new HostWindowResult(
                HostWindowStatus.Unavailable,
                reached ? DidNotLandNotice : NotConfirmedNotice);
    }

    public HostWindowResult Restore()
    {
        IPluginHostWindowTarget? target = Resolve();
        if (target is null)
            return new HostWindowResult(HostWindowStatus.Unavailable, NoWindowNotice);

        bool succeeded = false;
        void Apply()
        {
            try
            {
                // Only un-minimize -- never force Normal on a window that
                // is already Maximized/Fullscreen/Normal. Forcing it would
                // also desynchronize the display mode switcher own
                // ExtendedOptionsCache tracking of fullscreen/maximized
                // state.
                if (target.WindowState == WindowState.Minimized)
                    target.WindowState = WindowState.Normal;
                // "Restored" means "not minimized", not "== Normal": on
                // Silk.NET GLFW backend, a window that was
                // fullscreen/maximized before it was minimized reads back as
                // Fullscreen/Maximized again once un-minimized, never as
                // Normal.
                succeeded = target.WindowState != WindowState.Minimized;
            }
            catch
            {
                succeeded = false;
            }
        }

        bool reached = _dispatch().InvokeAndWait(Apply, CallTimeout);
        return reached && succeeded
            ? new HostWindowResult(HostWindowStatus.Done)
            : new HostWindowResult(
                HostWindowStatus.Unavailable,
                reached ? DidNotLandNotice : NotConfirmedNotice);
    }

    public HostWindowResult RequestClose()
    {
        IPluginHostWindowTarget? target = Resolve();
        if (target is null)
            return new HostWindowResult(HostWindowStatus.Unavailable, NoWindowNotice);

        // Close() is the exact route the window own close button and
        // OS-close-request handling both use: it raises the Closing
        // callback, which runs the graceful logout and teardown before the
        // process exits. This never calls a direct process-exit API.
        bool succeeded = false;
        void SafeClose()
        {
            try
            {
                target.Close();
                succeeded = true;
            }
            catch
            {
                // Swallowed on purpose: InvokeAndWait reports whether the
                // queued action ran, not whether it threw, so a throwing
                // Close() must not escape into the plugin thread that
                // called RequestClose -- succeeded stays false instead.
                succeeded = false;
            }
        }

        bool reached;
        try
        {
            reached = _dispatch().InvokeAndWait(SafeClose, CallTimeout);
        }
        catch
        {
            reached = false;
        }

        return reached && succeeded
            ? new HostWindowResult(HostWindowStatus.Done)
            : new HostWindowResult(
                HostWindowStatus.Unavailable,
                reached ? DidNotLandNotice : NotConfirmedNotice);
    }

    private IPluginHostWindowTarget? Resolve()
    {
        try
        {
            return _window();
        }
        catch
        {
            return null;
        }
    }
}
