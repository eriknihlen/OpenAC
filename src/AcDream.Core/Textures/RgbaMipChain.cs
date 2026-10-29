namespace AcDream.Core.Textures;

/// <summary>
/// The smaller copies of an RGBA image a GPU samples when the image is drawn
/// much smaller than it is, so it shrinks smoothly instead of breaking up.
/// Built here rather than by the GPU because the interface blends with plain
/// (not premultiplied) alpha: each level averages colour weighted by alpha,
/// so the colour of clear pixels never bleeds into the edge of opaque ones.
/// </summary>
public static class RgbaMipChain
{
    /// <summary>
    /// Every level from the image itself down to 1x1, each half the size of
    /// the one above (rounded down, at least 1), the way the GPU sizes them.
    /// The first level is <paramref name="rgba"/> itself, not a copy.
    /// </summary>
    public static IReadOnlyList<DecodedTexture> Build(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (rgba.Length < width * height * 4)
            throw new ArgumentException($"a {width}x{height} image needs {width * height * 4} bytes", nameof(rgba));

        var levels = new List<DecodedTexture> { new(rgba, width, height) };
        while (width > 1 || height > 1)
        {
            int nextWidth = Math.Max(1, width / 2), nextHeight = Math.Max(1, height / 2);
            rgba = Halve(rgba, width, height, nextWidth, nextHeight);
            (width, height) = (nextWidth, nextHeight);
            levels.Add(new DecodedTexture(rgba, width, height));
        }
        return levels;
    }

    private static byte[] Halve(byte[] source, int width, int height, int nextWidth, int nextHeight)
    {
        var next = new byte[nextWidth * nextHeight * 4];
        for (int y = 0; y < nextHeight; y++)
        {
            // The last row (and column) of the level below also takes in the
            // odd row the halving would otherwise drop.
            int top = y * 2, bottom = y == nextHeight - 1 ? height - 1 : Math.Min(y * 2 + 1, height - 1);
            for (int x = 0; x < nextWidth; x++)
            {
                int left = x * 2, right = x == nextWidth - 1 ? width - 1 : Math.Min(x * 2 + 1, width - 1);
                long alpha = 0, red = 0, green = 0, blue = 0, plainRed = 0, plainGreen = 0, plainBlue = 0;
                int count = 0;
                for (int sy = top; sy <= bottom; sy++)
                {
                    for (int sx = left; sx <= right; sx++)
                    {
                        int i = (sy * width + sx) * 4;
                        int a = source[i + 3];
                        alpha += a;
                        red += source[i] * a;
                        green += source[i + 1] * a;
                        blue += source[i + 2] * a;
                        plainRed += source[i];
                        plainGreen += source[i + 1];
                        plainBlue += source[i + 2];
                        count++;
                    }
                }

                int o = (y * nextWidth + x) * 4;
                if (alpha > 0)
                {
                    next[o] = Average(red, alpha);
                    next[o + 1] = Average(green, alpha);
                    next[o + 2] = Average(blue, alpha);
                }
                else
                {
                    next[o] = Average(plainRed, count);
                    next[o + 1] = Average(plainGreen, count);
                    next[o + 2] = Average(plainBlue, count);
                }
                next[o + 3] = Average(alpha, count);
            }
        }
        return next;
    }

    private static byte Average(long sum, long count) =>
        (byte)Math.Round((double)sum / count, MidpointRounding.AwayFromZero);
}
