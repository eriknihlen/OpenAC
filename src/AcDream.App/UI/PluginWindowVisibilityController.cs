namespace AcDream.App.UI;

/// <summary>
/// Whether a plugin's window is on screen: the player's own show or close
/// request, and the plugin's bound visibility when its markup binds one. The
/// window is shown only while both say so. A hide while the plugin's binding
/// is false is the binding's doing, not the player's, so it leaves the
/// player's request alone.
/// </summary>
internal sealed class PluginWindowVisibilityController(
    Func<bool>? availability,
    bool startVisible) : IRetainedPanelController
{
    private bool _requestedVisible = startVisible;

    internal bool ShouldBeVisible() =>
        _requestedVisible && (availability?.Invoke() ?? true);

    public void OnShown() => _requestedVisible = true;

    public void OnHidden()
    {
        if (availability?.Invoke() ?? true)
            _requestedVisible = false;
    }

    public void Dispose()
    {
    }
}
