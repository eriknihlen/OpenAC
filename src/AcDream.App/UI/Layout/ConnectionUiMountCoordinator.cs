using AcDream.Content;

namespace AcDream.App.UI.Layout;

internal sealed class ConnectionUiMountCoordinator(
    UiRoot host, RetailUiAssets assets, ConnectionRuntimeBindings bindings) : IDisposable
{
    private ConnectionUiController? _controller;
    private bool _disposed;

    internal bool IsVisible => _controller?.Root.Visible == true;

    internal void Tick()
    {
        if (_disposed) return;
        if (_controller is null)
        {
            lock (assets.DatLock)
            {
                uint layoutId = RetailDataIdResolver.Resolve(assets.Dats,
                    ConnectionUiController.RootEnum, 5u);
                ImportedLayout? layout = layoutId == 0u ? null : LayoutImporter.Import(
                    assets.Dats, layoutId, ConnectionUiController.RootElementId,
                    assets.ResolveSprite, assets.DefaultFont, assets.ResolveFont);
                if (layout is null) return;
                var strings = new DatStringResolver(assets.Dats);
                string? checking = strings.Resolve(0x23000002u,
                    DatStringResolver.ComputeHash("ID_DataPatch_Interrogation"));
                string? complete = strings.Resolve(0x23000002u,
                    DatStringResolver.ComputeHash("ID_DataPatch_PatchingDone"));
                if (checking is null || complete is null) return;
                _controller = ConnectionUiController.Bind(host, layout, bindings,
                    checking, complete);
            }
        }
        _controller?.Tick();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller?.Dispose();
        _controller = null;
    }
}
