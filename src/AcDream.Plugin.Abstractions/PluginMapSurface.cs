#pragma warning disable CS1591
namespace AcDream.Plugin.Abstractions;

/// <summary>Thread-safe stateful map surface useful to hosts and test fixtures.</summary>
public sealed class PluginMapSurface : IPluginMapSurface
{
    private readonly object _gate = new();
    private PluginMapImage? _background;
    private IReadOnlyList<PluginMapMarker> _markers = Array.Empty<PluginMapMarker>();
    private IReadOnlyList<PluginMapPoint> _route = Array.Empty<PluginMapPoint>();
    private bool _disposed;

    public PluginMapSurface(PluginMapViewport viewport) => Viewport = viewport;
    public PluginMapViewport Viewport { get; set; }
    public PluginMapImage? Background { get { lock (_gate) return _background; } }
    public IReadOnlyList<PluginMapMarker> Markers { get { lock (_gate) return _markers; } }
    public IReadOnlyList<PluginMapPoint> Route { get { lock (_gate) return _route; } }
    public event Action<PluginMapInput>? Input;

    public void SetBackground(PluginMapImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        lock (_gate) { ThrowIfDisposed(); _background = image; }
    }
    public void SetMarkers(IReadOnlyList<PluginMapMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        lock (_gate) { ThrowIfDisposed(); _markers = markers.ToArray(); }
    }
    public void SetRoute(IReadOnlyList<PluginMapPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        lock (_gate) { ThrowIfDisposed(); _route = points.ToArray(); }
    }
    /// <summary>Delivers input to subscribers, if the surface is still active.</summary>
    public void RaiseInput(PluginMapInput input)
    {
        Action<PluginMapInput>? handler;
        lock (_gate) { ThrowIfDisposed(); handler = Input; }
        handler?.Invoke(input);
    }
    public void Dispose()
    {
        lock (_gate) { _disposed = true; Input = null; _background = null; _markers = Array.Empty<PluginMapMarker>(); _route = Array.Empty<PluginMapPoint>(); }
    }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(PluginMapSurface)); }
}
