namespace AcDream.Plugin.Abstractions;

/// <summary>Whether a host-window operation actually happened.</summary>
public enum HostWindowStatus
{
    /// <summary>
    /// The host has no window to act on (a no-window/headless host), the
    /// requested change did not take, or the outcome could not be confirmed
    /// (see the platform caveats on <see cref="IHostWindow.Minimize"/>).
    /// Treat this as "unknown", not "definitely unchanged" -- a plugin
    /// should not spin retrying on this signal alone.
    /// </summary>
    Unavailable = 0,

    /// <summary>The operation reached the host and took effect.</summary>
    Done,
}

/// <summary>
/// One outcome from an <see cref="IHostWindow"/> call. <see cref="Notice"/>
/// carries a one-line reason when the host has one worth surfacing (a
/// logger message, a diagnostic panel) -- callers should branch on
/// <see cref="Status"/>/<see cref="Succeeded"/> only, never on the text of
/// Notice, which is not a stable identifier.
/// </summary>
public readonly record struct HostWindowResult(
    HostWindowStatus Status,
    string? Notice = null)
{
    /// <summary>Whether the operation took effect.</summary>
    public bool Succeeded => Status == HostWindowStatus.Done;
}

/// <summary>
/// The client's own OS window -- minimize, restore, and close, the same
/// three controls the window's title bar already offers. A host with no
/// window (a headless bot process) answers every query as unavailable
/// rather than throwing, so a plugin written against a graphical host still
/// loads there.
/// </summary>
public interface IHostWindow
{
    /// <summary>
    /// Whether the window is currently minimized/iconified. Always
    /// <c>false</c> on a host with no window, and also always
    /// <c>false</c> on a platform that cannot report iconification back
    /// at all (see the platform caveats below).
    /// </summary>
    bool IsMinimized => false;

    /// <summary>
    /// Minimizes the OS window (GLFW iconify; Windows, Linux, macOS).
    /// Reports <see cref="HostWindowStatus.Done"/> only once the window
    /// actually reports the minimized state back; a host with no window,
    /// or one that refused the change, reports <see
    /// cref="HostWindowStatus.Unavailable"/>.
    ///
    /// That confirmation is not equally trustworthy on every platform: it
    /// is synchronous on Windows, arrives asynchronously on X11 (a call
    /// immediately after Minimize can briefly still read the old state),
    /// is animated on macOS (there is a brief window before the OS
    /// finishes iconifying), and the Wayland compositor protocol has no
    /// way to report iconification back to the client at all -- on
    /// Wayland this call always reports Unavailable even when the window
    /// did minimize. Treat Unavailable here as "not confirmed", not as
    /// "definitely still shown"; do not retry it in a loop.
    /// </summary>
    HostWindowResult Minimize() => new(HostWindowStatus.Unavailable);

    /// <summary>
    /// Un-minimizes the OS window if it is currently minimized; a no-op
    /// success if it was already shown, whatever its prior maximized or
    /// fullscreen state. Same confirmation caveats as <see
    /// cref="Minimize"/>.
    /// </summary>
    HostWindowResult Restore() => new(HostWindowStatus.Unavailable);

    /// <summary>
    /// Requests that the client close, following the exact route its own
    /// close button uses -- graceful logout, then teardown, then process
    /// exit. This never terminates the process directly: a host with no
    /// window ends the plugin's own session through its normal terminal
    /// path (the same one a SIGINT or a policy-driven stop already uses)
    /// instead.
    /// </summary>
    HostWindowResult RequestClose() => new(HostWindowStatus.Unavailable);
}

/// <summary>Shared inert window for hosts with nothing to act on.</summary>
public sealed class NoOpHostWindow : IHostWindow
{
    /// <summary>The shared instance; this type holds no state.</summary>
    public static NoOpHostWindow Instance { get; } = new();

    private NoOpHostWindow()
    {
    }
}
