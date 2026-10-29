using AcDream.Core.Textures;

namespace AcDream.Core.Tests.Textures;

public sealed class RgbaMipChainTests
{
    private static byte[] Solid(int width, int height, byte r, byte g, byte b, byte a)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4)
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, a);
        return rgba;
    }

    [Theory]
    [InlineData(1, 1, new[] { 1, 1 })]
    [InlineData(8, 8, new[] { 8, 8, 4, 4, 2, 2, 1, 1 })]
    [InlineData(5, 2, new[] { 5, 2, 2, 1, 1, 1 })]
    [InlineData(1, 4, new[] { 1, 4, 1, 2, 1, 1 })]
    public void LevelsHalveDownToOnePixelTheWayTheGpuSizesThem(int width, int height, int[] extents)
    {
        IReadOnlyList<DecodedTexture> levels = RgbaMipChain.Build(Solid(width, height, 1, 2, 3, 4), width, height);

        int[] actual = levels.SelectMany(level => new[] { level.Width, level.Height }).ToArray();
        Assert.Equal(extents, actual);
        Assert.All(levels, level => Assert.Equal(level.Width * level.Height * 4, level.Rgba8.Length));
    }

    [Fact]
    public void TheFirstLevelIsTheImageItself()
    {
        byte[] rgba = Solid(4, 4, 10, 20, 30, 40);

        Assert.Same(rgba, RgbaMipChain.Build(rgba, 4, 4)[0].Rgba8);
    }

    [Fact]
    public void ASolidImageStaysTheSameColourAtEveryLevel()
    {
        IReadOnlyList<DecodedTexture> levels = RgbaMipChain.Build(Solid(16, 8, 200, 100, 50, 255), 16, 8);

        Assert.All(levels, level => Assert.Equal(Solid(level.Width, level.Height, 200, 100, 50, 255), level.Rgba8));
    }

    [Fact]
    public void ClearPixelsDoNotDarkenTheEdgeOfOpaqueOnes()
    {
        // White opaque on the left, clear black on the right: a plain average
        // would turn the edge grey; weighting colour by alpha keeps it white.
        byte[] rgba = [255, 255, 255, 255, 0, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 0];

        DecodedTexture next = RgbaMipChain.Build(rgba, 2, 2)[1];

        Assert.Equal(new byte[] { 255, 255, 255, 128 }, next.Rgba8);
    }

    [Fact]
    public void AFullyClearBlockStaysClear()
    {
        DecodedTexture next = RgbaMipChain.Build(Solid(2, 2, 90, 90, 90, 0), 2, 2)[1];

        Assert.Equal(0, next.Rgba8[3]);
    }

    [Fact]
    public void AnOddEdgeIsFoldedIntoTheLevelBelow()
    {
        // 3x1: red, red, blue. The single 1x1 pixel below covers all three
        // source pixels, so blue shows rather than being dropped.
        byte[] rgba = [255, 0, 0, 255, 255, 0, 0, 255, 0, 0, 255, 255];

        DecodedTexture next = RgbaMipChain.Build(rgba, 3, 1)[1];

        Assert.Equal(1, next.Width);
        Assert.Equal(new byte[] { 170, 0, 85, 255 }, next.Rgba8);
    }
}
