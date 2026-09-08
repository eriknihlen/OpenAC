using AcDream.App.Settings;
using AcDream.App.Plugins;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Packs;
using AcDream.UI.Abstractions.Panels.Settings;
using Silk.NET.Input;

namespace AcDream.App.Composition;

internal sealed record SettingsDevToolsResult(
    AcDream.UI.Abstractions.Settings.QualitySettings ResolvedQuality)
{
    internal RenderPackCatalogSource? RenderPacks { get; init; }

    internal RenderPackSelectionSettings RenderPackSelection { get; init; } =
        RenderPackSelectionSettings.Retail;
}

internal sealed record SettingsDevToolsDependencies(
    RuntimeSettingsController Settings,
    IRuntimeSettingsStartupTarget StartupTarget)
{
    internal BufferedRenderPackRegistry? RenderPacks { get; init; }

    internal IGpuDevice? GpuDevice { get; init; }
}

internal sealed class SettingsDevToolsCompositionPhase :
    ISettingsDevToolsCompositionPhase<
        GameWindowPlatformResult<GameWindowGraphics, IInputContext>,
        HostInputCameraResult,
        ContentEffectsAudioResult,
        SettingsDevToolsResult>
{
    private readonly SettingsDevToolsDependencies _dependencies;

    public SettingsDevToolsCompositionPhase(SettingsDevToolsDependencies dependencies)
    {
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
    }

    public SettingsDevToolsResult Compose(
        GameWindowPlatformResult<GameWindowGraphics, IInputContext> platform,
        HostInputCameraResult host,
        ContentEffectsAudioResult content)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(content);

        _dependencies.Settings.ApplyStartup(_dependencies.StartupTarget);
        RenderPackCatalogSource? renderPacks = null;
        if (_dependencies.RenderPacks is { } registry
            && _dependencies.GpuDevice is { } gpu)
        {
            renderPacks = new RenderPackCatalogSource(
                registry,
                RenderPackCapabilityResolver.Resolve(gpu.Capabilities));
        }

        return new SettingsDevToolsResult(_dependencies.Settings.ResolvedQuality)
        {
            RenderPacks = renderPacks,
            RenderPackSelection = _dependencies.Settings.Display.RenderPack,
        };
    }
}
