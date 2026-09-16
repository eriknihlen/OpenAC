using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

/// <summary>Implemented by a host's UI registry when it can also carry the plugin's
/// install folder through to the panel, without adding it to <see cref="IScopedUiRegistry"/>
/// itself (L-317).</summary>
public interface IPluginDirectoryUiRegistry
{
    IDisposable RegisterPanel(
        PluginUiOwner owner,
        string? pluginDirectory,
        PluginPanelDescriptor descriptor,
        string markupPath,
        object binding);

    IDisposable RegisterPanelContent(
        PluginUiOwner owner,
        string? pluginDirectory,
        PluginPanelDescriptor descriptor,
        string markupContent,
        object binding);
}
