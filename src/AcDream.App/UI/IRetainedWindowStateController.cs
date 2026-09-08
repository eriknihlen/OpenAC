namespace AcDream.App.UI;

public readonly record struct RetainedWindowState(
    bool Collapsed = false,
    bool Maximized = false,
    float? PersistedTop = null,
    float? PersistedHeight = null,
    bool? RequestedVisible = null);

public interface IRetainedWindowStateController
{
    RetainedWindowState CaptureWindowState();
    void RestoreWindowState(RetainedWindowState state);
}
