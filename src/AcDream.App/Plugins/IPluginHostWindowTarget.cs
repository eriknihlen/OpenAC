using Silk.NET.Windowing;

namespace AcDream.App.Plugins;

/// <summary>
/// The narrow slice of the native window that plugin host-window
/// operations need -- state and close. <see cref="WindowPluginHostWindow"/>
/// depends on this instead of the much larger Silk.NET
/// <see cref="Silk.NET.Windowing.IWindow"/>, so a test fake only needs to
/// implement two members. A third narrow seam, IWindowedSizeSurface in
/// Settings/RuntimeSettingsTargets.cs, covers window Size/IsMaximized/
/// Restore for the display-settings panel -- unrelated to plugins, do not
/// merge the two.
/// </summary>
internal interface IPluginHostWindowTarget
{
    WindowState WindowState { get; set; }

    void Close();
}

/// <summary>Adapts a real Silk.NET window to <see cref="IPluginHostWindowTarget"/>.</summary>
internal sealed class SilkPluginHostWindowTarget(IWindow window)
    : IPluginHostWindowTarget
{
    private readonly IWindow _window = window
        ?? throw new ArgumentNullException(nameof(window));

    public WindowState WindowState
    {
        get => _window.WindowState;
        set => _window.WindowState = value;
    }

    public void Close() => _window.Close();
}
