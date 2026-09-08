using AcDream.App.Plugins;

namespace AcDream.App.Rendering.Packs;

internal sealed class RenderPackCatalogSource
{
    private readonly BufferedRenderPackRegistry _registry;
    private readonly RenderPackHostCapabilities _capabilities;

    internal RenderPackCatalogSource(
        BufferedRenderPackRegistry registry,
        RenderPackHostCapabilities capabilities)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _capabilities = capabilities
            ?? throw new ArgumentNullException(nameof(capabilities));
    }

    internal long Revision => _registry.Revision;

    internal event Action<long> Changed
    {
        add => _registry.Changed += value;
        remove => _registry.Changed -= value;
    }

    internal RenderPackCatalog Snapshot() => RenderPackCatalog.Build(
        _registry.Snapshot(),
        _capabilities);
}
