using AcDream.Plugin.Abstractions;

namespace AcDream.Plugin.Tests;

public sealed class PluginMapRenderingContractTests
{
    [Fact]
    public void CoordinateConversionRoundTripsAndUsesTopLeftPixels()
    {
        var bounds = new PluginMapViewport(new PluginMapPoint(100, 200), 20, 40);
        var converter = new LinearPluginMapCoordinateConverter(bounds);

        Assert.Equal(new PluginMapPixel(1000, 0), converter.WorldToPixel(new(110, 220), 1000, 800));
        var world = converter.PixelToWorld(new(1000, 0), 1000, 800);
        Assert.Equal(110, world.EastWest, 10);
        Assert.Equal(220, world.NorthSouth, 10);
    }

    [Fact]
    public void InvalidMapDimensionsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginMapViewport(default, 0, 1));
        var converter = new LinearPluginMapCoordinateConverter(new(default, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => converter.WorldToPixel(default, 0, 1));
    }

    [Fact]
    public void MapSurfaceCopiesStateAndRejectsUseAfterDispose()
    {
        using var surface = new PluginMapSurface(new(default, 10, 10));
        var markers = new[] { new PluginMapMarker("a", default) };
        surface.SetMarkers(markers);
        markers[0] = new PluginMapMarker("b", default);
        Assert.Equal("a", surface.Markers[0].Id);
        surface.Dispose();
        Assert.Throws<ObjectDisposedException>(() => surface.SetRoute([]));
    }

    [Fact]
    public async Task HeadlessFacilitiesAreSafeAndInert()
    {
        using var map = NoOpPluginMapRegistry.Instance.AddMap("map", new(default, 1, 1));
        map.SetMarkers([new("marker", default)]);
        map.SetRoute([default]);
        Assert.Null(map.Background);
        Assert.Empty(map.Markers);
        Assert.Empty(map.Route);
        Assert.Null(NoOpPluginRenderRegistry.Instance.LoadTexture("missing"));
        Assert.Null(await NoOpPluginMapResourceCatalog.Instance.OpenMapAsync("missing"));
    }
}
