#pragma warning disable CS1591
namespace AcDream.Plugin.Abstractions;

/// <summary>Identifies one tile in a tiled map image.</summary>
public readonly record struct PluginMapTileKey
{
    public PluginMapTileKey(int zoom, int x, int y)
    {
        if (zoom < 0) throw new ArgumentOutOfRangeException(nameof(zoom));
        if (x < 0 || y < 0) throw new ArgumentOutOfRangeException(nameof(x));
        Zoom = zoom; X = x; Y = y;
    }
    public int Zoom { get; }
    public int X { get; }
    public int Y { get; }
}

/// <summary>Decoded immutable map tile returned by a host resource provider.</summary>
public sealed record PluginMapTile(PluginMapTileKey Key, int PixelWidth, int PixelHeight, ReadOnlyMemory<byte> Pixels, string PixelFormat = "RGBA8");

/// <summary>Metadata and asynchronous tile access for a map resource.</summary>
public interface IPluginTiledMapResource : IAsyncDisposable
{
    string Id { get; }
    int TileSize { get; }
    int MinZoom { get; }
    int MaxZoom { get; }
    PluginMapViewport WorldBounds { get; }
    ValueTask<PluginMapTile?> LoadTileAsync(PluginMapTileKey key, CancellationToken cancellationToken = default);
}

/// <summary>Loads tiled maps from declared plugin resources.</summary>
public interface IPluginMapResourceCatalog
{
    ValueTask<IPluginTiledMapResource?> OpenMapAsync(string resourceId, CancellationToken cancellationToken = default);
}

/// <summary>Inert map resource provider for headless hosts.</summary>
public sealed class NoOpPluginMapResourceCatalog : IPluginMapResourceCatalog
{
    public static NoOpPluginMapResourceCatalog Instance { get; } = new();
    private NoOpPluginMapResourceCatalog() { }
    public ValueTask<IPluginTiledMapResource?> OpenMapAsync(string resourceId, CancellationToken cancellationToken = default) => ValueTask.FromResult<IPluginTiledMapResource?>(null);
}
