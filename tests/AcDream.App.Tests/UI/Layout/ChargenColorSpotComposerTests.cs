using AcDream.App.UI.Layout;
using AcDream.Core.CharGen;

namespace AcDream.App.Tests.UI.Layout;

public sealed class ChargenColorSpotComposerTests
{
    private static byte[] Pixels(params (byte r, byte g, byte b, byte a)[] pixels)
    {
        var buffer = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            buffer[i * 4] = pixels[i].r;
            buffer[i * 4 + 1] = pixels[i].g;
            buffer[i * 4 + 2] = pixels[i].b;
            buffer[i * 4 + 3] = pixels[i].a;
        }
        return buffer;
    }

    [Fact]
    public void ReplaceExactBlackWithColor_RecolorsOnlyOpaqueExactBlackPixels_LeavesEverythingElseUntouched()
    {
        var rgb = new ChargenSwatchRgb(200, 40, 90);
        byte[] source = Pixels(
            (0, 0, 0, 255),
            (218, 167, 85, 255),
            (2, 1, 0, 255),
            (0, 0, 0, 0));

        byte[] baked = ChargenColorSpotComposer.ReplaceExactBlackWithColor(source, rgb);

        Assert.Equal(rgb.R, baked[0]);
        Assert.Equal(rgb.G, baked[1]);
        Assert.Equal(rgb.B, baked[2]);
        Assert.Equal(255, baked[3]);

        Assert.Equal(218, baked[4]);
        Assert.Equal(167, baked[5]);
        Assert.Equal(85, baked[6]);
        Assert.Equal(255, baked[7]);

        Assert.Equal(2, baked[8]);
        Assert.Equal(1, baked[9]);
        Assert.Equal(0, baked[10]);
        Assert.Equal(255, baked[11]);

        Assert.Equal(0, baked[12]);
        Assert.Equal(0, baked[13]);
        Assert.Equal(0, baked[14]);
        Assert.Equal(0, baked[15]); // still transparent — not force-opaqued
    }

    [Fact]
    public void ReplaceExactBlackWithColor_DoesNotMutateTheSourceBuffer()
    {
        byte[] source = Pixels((0, 0, 0, 255));
        byte[] sourceCopy = (byte[])source.Clone();

        _ = ChargenColorSpotComposer.ReplaceExactBlackWithColor(source, new ChargenSwatchRgb(10, 20, 30));

        Assert.Equal(sourceCopy, source);
    }

    [Fact]
    public void ReplaceExactBlackWithColor_EveryOpaqueBlackPixelRecolored_SemiTransparentBlackLeftAlone()
    {
        byte[] source = Pixels((0, 0, 0, 255), (0, 0, 0, 255), (0, 0, 0, 128));
        var rgb = new ChargenSwatchRgb(9, 8, 7);

        byte[] baked = ChargenColorSpotComposer.ReplaceExactBlackWithColor(source, rgb);

        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(rgb.R, baked[i * 4]);
            Assert.Equal(rgb.G, baked[i * 4 + 1]);
            Assert.Equal(rgb.B, baked[i * 4 + 2]);
            Assert.Equal(255, baked[i * 4 + 3]);
        }
        Assert.Equal(0, baked[8]);
        Assert.Equal(0, baked[9]);
        Assert.Equal(0, baked[10]);
        Assert.Equal(128, baked[11]); // untouched, including its own alpha
    }
}
