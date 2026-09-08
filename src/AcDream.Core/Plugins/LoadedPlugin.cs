using System.Runtime.Loader;
using AcDream.Plugin.Abstractions;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.Core.Plugins;

public sealed record LoadedPlugin(
    PluginManifest Manifest,
    IAcDreamPlugin? Plugin,
    AssemblyLoadContext? LoadContext,
    Exception? Error,
    IRenderPackPlugin? RenderPackPlugin = null)
{
    public bool Success =>
        (Plugin is not null || RenderPackPlugin is not null)
        && LoadContext is not null
        && Error is null;
}
