using AcDream.App.Settings;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Rendering.Packs;

internal sealed class RenderPackSelectionBinding : IDisposable
{
    private readonly RuntimeSettingsController _settings;
    private readonly RenderPackController _controller;
    private readonly Action<string> _log;
    private long _fallbackPersistedGeneration = -1;
    private bool _suppressDisplayEdge;
    private bool _disposed;

    internal RenderPackSelectionBinding(
        RuntimeSettingsController settings,
        RenderPackController controller,
        Action<string>? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _log = log ?? (_ => { });
        _settings.DisplayChanged += OnDisplayChanged;
        _controller.Request(_settings.Display.RenderPack);
    }

    internal RenderPackActivationSnapshot ApplyAtFrameBoundary(
        RenderPackActivationExtent extent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RenderPackActivationSnapshot snapshot = _controller.ApplyAtFrameBoundary(extent);
        if (snapshot.State != RenderPackActivationState.FailedToRetail
            || snapshot.ActivationGeneration == _fallbackPersistedGeneration
            || _settings.Display.RenderPack.IsRetail)
            return snapshot;

        _fallbackPersistedGeneration = snapshot.ActivationGeneration;
        _suppressDisplayEdge = true;
        try
        {
            _settings.SaveDisplay(_settings.Display with
            {
                RenderPack = RenderPackSelectionSettings.Retail,
            });
        }
        finally
        {
            _suppressDisplayEdge = false;
        }

        if (_settings.Display.RenderPack.IsRetail)
        {
            _log(
                $"[render-pack] selection failed; persisted acdream default (retail-faithful): "
                + snapshot.Reason);
        }
        else
        {
            _log(
                $"[render-pack] selection failed and retail fallback could not be persisted: "
                + snapshot.Reason);
        }
        return snapshot;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _settings.DisplayChanged -= OnDisplayChanged;
    }

    private void OnDisplayChanged(DisplaySettings display)
    {
        if (!_disposed && !_suppressDisplayEdge)
            _controller.Request(display.RenderPack, explicitUserChoice: true);
    }
}
