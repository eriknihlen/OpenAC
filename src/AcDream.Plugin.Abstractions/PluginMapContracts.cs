#pragma warning disable CS1591
namespace AcDream.Plugin.Abstractions;

/// <summary>A point in the canonical OpenAC map coordinate system.</summary>
/// <remarks>EastWest and NorthSouth are navigation units (240 metres), with north and east positive.</remarks>
public readonly record struct PluginMapPoint(double EastWest, double NorthSouth, double Elevation = 0);

/// <summary>A rectangular map viewport in canonical world units.</summary>
public readonly record struct PluginMapViewport
{
    public PluginMapViewport(PluginMapPoint center, double width, double height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Map dimensions must be positive.");
        Center = center;
        Width = width;
        Height = height;
    }
    public PluginMapPoint Center { get; }
    public double Width { get; }
    public double Height { get; }
}

/// <summary>Pixel coordinates in a map image, using a top-left origin.</summary>
public readonly record struct PluginMapPixel(double X, double Y);

/// <summary>Converts canonical world coordinates to and from a map image.</summary>
public interface IPluginMapCoordinateConverter
{
    PluginMapPixel WorldToPixel(PluginMapPoint world, double imageWidth, double imageHeight);
    PluginMapPoint PixelToWorld(PluginMapPixel pixel, double imageWidth, double imageHeight);
    PluginMapViewport WorldBounds { get; }
}

/// <summary>A linear converter for a map image whose bounds are known in world units.</summary>
public sealed class LinearPluginMapCoordinateConverter : IPluginMapCoordinateConverter
{
    public LinearPluginMapCoordinateConverter(PluginMapViewport worldBounds) => WorldBounds = worldBounds;
    public PluginMapViewport WorldBounds { get; }

    public PluginMapPixel WorldToPixel(PluginMapPoint world, double imageWidth, double imageHeight)
    {
        ValidateImage(imageWidth, imageHeight);
        return new(
            (world.EastWest - (WorldBounds.Center.EastWest - WorldBounds.Width / 2)) / WorldBounds.Width * imageWidth,
            (WorldBounds.Center.NorthSouth + WorldBounds.Height / 2 - world.NorthSouth) / WorldBounds.Height * imageHeight);
    }

    public PluginMapPoint PixelToWorld(PluginMapPixel pixel, double imageWidth, double imageHeight)
    {
        ValidateImage(imageWidth, imageHeight);
        return new(
            WorldBounds.Center.EastWest - WorldBounds.Width / 2 + pixel.X / imageWidth * WorldBounds.Width,
            WorldBounds.Center.NorthSouth + WorldBounds.Height / 2 - pixel.Y / imageHeight * WorldBounds.Height);
    }

    private static void ValidateImage(double width, double height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
    }
}

/// <summary>Describes a map image and its canonical coordinate bounds.</summary>
public sealed record PluginMapImage(string Id, int PixelWidth, int PixelHeight, IPluginMapCoordinateConverter Coordinates);

/// <summary>A map marker owned by a plugin.</summary>
public sealed record PluginMapMarker(string Id, PluginMapPoint Position, string? Label = null, string? IconId = null, string? Tooltip = null, bool IsSelected = false);

/// <summary>Input delivered by a map surface.</summary>
public readonly record struct PluginMapInput(PluginMapInputKind Kind, PluginMapPixel Position, double WheelDelta = 0, string? MarkerId = null);
public enum PluginMapInputKind { PointerDown, PointerUp, Click, Wheel, Drag }

/// <summary>Host-owned map surface suitable for declarative map controls.</summary>
public interface IPluginMapSurface : IDisposable
{
    PluginMapViewport Viewport { get; set; }
    PluginMapImage? Background { get; }
    IReadOnlyList<PluginMapMarker> Markers { get; }
    IReadOnlyList<PluginMapPoint> Route { get; }
    event Action<PluginMapInput>? Input;
    void SetBackground(PluginMapImage image);
    void SetMarkers(IReadOnlyList<PluginMapMarker> markers);
    void SetRoute(IReadOnlyList<PluginMapPoint> points);
}

/// <summary>Map controls exposed by a UI host; inert in headless hosts.</summary>
public interface IPluginMapRegistry
{
    IPluginMapSurface AddMap(string mapId, PluginMapViewport initialViewport);
}

public sealed class NoOpPluginMapRegistry : IPluginMapRegistry
{
    public static NoOpPluginMapRegistry Instance { get; } = new();
    private NoOpPluginMapRegistry() { }
    public IPluginMapSurface AddMap(string mapId, PluginMapViewport initialViewport) => new NoOpMap(initialViewport);
    private sealed class NoOpMap(PluginMapViewport viewport) : IPluginMapSurface
    {
        public PluginMapViewport Viewport { get; set; } = viewport;
        public PluginMapImage? Background => null;
        public IReadOnlyList<PluginMapMarker> Markers => Array.Empty<PluginMapMarker>();
        public IReadOnlyList<PluginMapPoint> Route => Array.Empty<PluginMapPoint>();
        public event Action<PluginMapInput>? Input { add { } remove { } }
        public void SetBackground(PluginMapImage image) { }
        public void SetMarkers(IReadOnlyList<PluginMapMarker> markers) { }
        public void SetRoute(IReadOnlyList<PluginMapPoint> points) { }
        public void Dispose() { }
    }
}
