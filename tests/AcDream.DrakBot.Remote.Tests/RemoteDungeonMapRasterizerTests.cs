using System.Numerics;

namespace AcDream.DrakBot.Remote.Tests;

public sealed class RemoteDungeonMapRasterizerTests
{
    private const uint Landblock = 0x61450000u;

    /// <summary>A level square of floor, its corners counter-clockwise from the south-west.</summary>
    private static RemoteDungeonPolygon Floor(float x, float y, float size, float z, RemoteDungeonSurface kind = RemoteDungeonSurface.Floor) =>
        new(kind, [new Vector2(x, y), new Vector2(x + size, y), new Vector2(x + size, y + size), new Vector2(x, y + size)], z, z);

    /// <summary>A wall standing on the line from (x0, y0) to (x1, y1), from the floor up.</summary>
    private static RemoteDungeonPolygon Wall(float x0, float y0, float x1, float y1, float z, float height = 3f) =>
        new(RemoteDungeonSurface.Wall, [new Vector2(x0, y0), new Vector2(x1, y1), new Vector2(x1, y1), new Vector2(x0, y0)], z, z + height);

    /// <summary>The phone's transform: where a world point lands on a layer.</summary>
    private static (int X, int Y) Pixel(RemoteDungeonMapLayer layer, double wx, double wy) =>
        ((int)Math.Floor(wx / RemoteDungeonMapRasterizer.MetresPerPixel - layer.XMin),
         (int)Math.Floor((layer.Height - 1) - (wy / RemoteDungeonMapRasterizer.MetresPerPixel - layer.YMin)));

    private static (byte R, byte G, byte B, byte A) At(RemoteDungeonMapLayer layer, int x, int y)
    {
        byte[] pixels = RemotePng.Decode(layer.Png, out int width, out int height);
        Assert.Equal(layer.Width, width);
        Assert.Equal(layer.Height, height);
        int offset = (y * width + x) * 4;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }

    [Fact]
    public void TwoStoreysDrawAsTwoLayersInOneFrame_LandingsGoWithTheNearest()
    {
        // A 10 m room at ground, another 6 m down and 20 m east, and a 1 m landing
        // halfway down between them: two storeys, the landing filed with the lower.
        var geometry = new RemoteDungeonGeometry(Landblock,
        [
            Floor(-5720f, -11400f, 10f, 0f),
            Floor(-5700f, -11400f, 10f, -6f),
            Floor(-5709f, -11395f, 1f, -3.4f),
        ]);
        RemoteDungeonMaps? maps = RemoteDungeonMapRasterizer.Render(geometry, DateTime.UnixEpoch);
        Assert.NotNull(maps);
        Assert.Equal(Landblock, maps.LandblockId);
        Assert.Equal([-6d, 0d], maps.Layers.Select(l => l.Z));
        Assert.Equal([0, 1], maps.Layers.Select(l => l.Layer));

        RemoteDungeonMapLayer lower = maps.Layers[0], upper = maps.Layers[1];
        // One frame: half a metre a pixel with two pixels of air, both layers alike.
        Assert.Equal((int)Math.Floor(-5720d / 0.5) - RemoteDungeonMapRasterizer.MarginPixels, lower.XMin);
        Assert.Equal((int)Math.Floor(-11400d / 0.5) - RemoteDungeonMapRasterizer.MarginPixels, lower.YMin);
        Assert.Equal(30 / 0.5 + 2 * RemoteDungeonMapRasterizer.MarginPixels + 1, lower.Width);
        Assert.Equal(10 / 0.5 + 2 * RemoteDungeonMapRasterizer.MarginPixels + 1, lower.Height);
        Assert.Equal((lower.Width, lower.Height, lower.XMin, lower.YMin), (upper.Width, upper.Height, upper.XMin, upper.YMin));

        // The ground room is painted on the upper layer only, the lower room on the lower only.
        (int X, int Y) ground = Pixel(lower, -5715d, -11395d);
        (int X, int Y) sunk = Pixel(lower, -5695d, -11395d);
        (int X, int Y) landing = Pixel(lower, -5708.5d, -11394.5d);
        Assert.NotEqual(0, At(upper, ground.X, ground.Y).A);
        Assert.Equal(0, At(lower, ground.X, ground.Y).A);
        Assert.NotEqual(0, At(lower, sunk.X, sunk.Y).A);
        Assert.Equal(0, At(upper, sunk.X, sunk.Y).A);
        Assert.NotEqual(0, At(lower, landing.X, landing.Y).A);
        Assert.Equal(0, At(upper, landing.X, landing.Y).A);
        // North is up: the room's north edge is nearer row zero than its south edge.
        Assert.True(Pixel(lower, -5715d, -11391d).Y < Pixel(lower, -5715d, -11399d).Y);
        Assert.NotEqual(lower.ETag, upper.ETag);
    }

    [Fact]
    public void WallsAreDrawnDarkBesideTheirFloorAndNowhereElse()
    {
        var geometry = new RemoteDungeonGeometry(Landblock,
        [
            Floor(0f, 0f, 10f, 0f),
            Wall(10f, 0f, 10f, 10f, 0f),           // the room's east wall
            Wall(30f, 0f, 30f, 10f, 0f),           // a wall with no floor near it: an upper wall of some tall room
        ]);
        RemoteDungeonMaps? maps = RemoteDungeonMapRasterizer.Render(geometry, DateTime.UnixEpoch);
        Assert.NotNull(maps);
        RemoteDungeonMapLayer layer = Assert.Single(maps.Layers);

        (int X, int Y) inside = Pixel(layer, 5d, 5d);
        (byte R, byte G, byte B, byte A) floor = At(layer, inside.X, inside.Y);
        Assert.Equal(255, floor.A);
        (int X, int Y) edge = Pixel(layer, 10d, 5d);
        (byte R, byte G, byte B, byte A) wall = At(layer, edge.X, edge.Y);
        Assert.Equal(255, wall.A);
        Assert.True(wall.R + wall.G + wall.B < floor.R + floor.G + floor.B, "a wall is darker than the floor it stands beside");
        (int X, int Y) far = Pixel(layer, 30d, 5d);
        Assert.Equal(0, At(layer, far.X, far.Y).A);
    }

    [Fact]
    public void ARampIsPaintedItsOwnShade()
    {
        var geometry = new RemoteDungeonGeometry(Landblock,
        [
            Floor(0f, 0f, 10f, 0f),
            Floor(10f, 0f, 4f, 0f, RemoteDungeonSurface.Ramp),
        ]);
        RemoteDungeonMaps? maps = RemoteDungeonMapRasterizer.Render(geometry, DateTime.UnixEpoch);
        Assert.NotNull(maps);
        RemoteDungeonMapLayer layer = Assert.Single(maps.Layers);
        (int X, int Y) flat = Pixel(layer, 5d, 5d);
        (int X, int Y) slope = Pixel(layer, 12d, 2d);   // the ramp is a 4 m square
        (byte R, byte G, byte B, byte A) floor = At(layer, flat.X, flat.Y);
        (byte R, byte G, byte B, byte A) ramp = At(layer, slope.X, slope.Y);
        Assert.Equal(255, ramp.A);
        Assert.NotEqual(floor, ramp);
    }

    [Fact]
    public void NoFloorMeansNoMap_ATinyFloorStillGetsItsStorey()
    {
        Assert.Null(RemoteDungeonMapRasterizer.Render(new RemoteDungeonGeometry(Landblock, [Wall(0f, 0f, 10f, 0f, 0f)]), DateTime.UnixEpoch));
        Assert.Null(RemoteDungeonMapRasterizer.Render(new RemoteDungeonGeometry(Landblock, []), DateTime.UnixEpoch));

        RemoteDungeonMaps? closet = RemoteDungeonMapRasterizer.Render(new RemoteDungeonGeometry(Landblock, [Floor(0f, 0f, 2f, -12f)]), DateTime.UnixEpoch);
        Assert.NotNull(closet);
        Assert.Equal([-12d], closet.Layers.Select(l => l.Z));
    }

    [Fact]
    public void StoreysNeedARoomsWorthOfFloor_AndCloseOnesMerge()
    {
        // Ground with a slab a metre up beside it is one storey (the heavier), a
        // 2 m² ledge at -6 is no storey, and a real room at -12 is.
        RemoteDungeonPolygon[] polygons =
        [
            Floor(0f, 0f, 20f, 0f),
            Floor(20f, 0f, 8f, 1f),
            Floor(40f, 0f, 1.4f, -6f),
            Floor(60f, 0f, 9f, -12f),
        ];
        Assert.Equal([-12d, 0d], RemoteDungeonMapRasterizer.Levels(polygons));
        Assert.Equal(0, RemoteDungeonMapRasterizer.Nearest([-12d, 0d], -7d));
        Assert.Equal(1, RemoteDungeonMapRasterizer.Nearest([-12d, 0d], -5d));
    }

    [Fact]
    public void ThePngRoundTrips()
    {
        var rgba = new byte[3 * 2 * 4];
        for (int index = 0; index < rgba.Length; index++)
            rgba[index] = (byte)(index * 7);
        byte[] png = RemotePng.Encode(3, 2, rgba);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png.Take(4));
        byte[] back = RemotePng.Decode(png, out int width, out int height);
        Assert.Equal((3, 2), (width, height));
        Assert.Equal(rgba, back);
    }
}
