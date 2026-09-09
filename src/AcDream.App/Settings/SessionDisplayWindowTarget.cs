using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Settings;

internal sealed class SessionDisplayWindowTarget(
    IRuntimeDisplayWindowTarget window,
    bool fixedAutomationViewport = false,
    bool directCharacterLaunch = false) : IRuntimeDisplayWindowTarget
{
    private readonly IRuntimeDisplayWindowTarget _window = window
        ?? throw new ArgumentNullException(nameof(window));
    private DisplaySettings? _preferred;
    private bool _directLaunchPending = directCharacterLaunch;

    public bool IsGameplay { get; private set; }

    public RuntimeDisplayApplyResult Apply(DisplaySettings display)
    {
        ArgumentNullException.ThrowIfNull(display);
        _preferred = display;
        if (IsGameplay || fixedAutomationViewport || _directLaunchPending)
            return _window.Apply(display);

        _window.Apply(display with { Resolution = "800x600", Fullscreen = false });
        // The temporary window mode must not replace the saved preference.
        return new RuntimeDisplayApplyResult(display.Fullscreen);
    }

    public void SetGameplay(bool active)
    {
        if (IsGameplay == active)
            return;
        IsGameplay = active;
        if (active)
            _directLaunchPending = false;
        if (_preferred is { } display)
            Apply(display);
    }

    public void EndDirectLaunch()
    {
        if (!_directLaunchPending) return;
        _directLaunchPending = false;
        if (_preferred is { } display)
            Apply(display);
    }
}
