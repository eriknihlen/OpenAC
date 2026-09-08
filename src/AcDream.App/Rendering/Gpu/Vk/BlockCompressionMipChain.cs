namespace AcDream.App.Rendering.Gpu.Vk;

internal static class BlockCompressionMipChain
{
    internal readonly record struct Level(int MipLevel, int Width, int Height, byte[] Data);

    internal static IReadOnlyList<Level> BuildCompressed(
        GpuTextureFormat format,
        ReadOnlySpan<byte> level0,
        int width,
        int height,
        int mipLevelCount)
    {
        if (!BlockCompressionCodec.IsBlockCompressed(format))
            throw new ArgumentOutOfRangeException(nameof(format), format, "This chain builder is for BC formats.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mipLevelCount);
        if (mipLevelCount == 1)
            return [];

        byte[] rgba = BlockCompressionCodec.DecodeLevel(format, level0, width, height);
        return BuildFromRgba(format, rgba, width, height, mipLevelCount);
    }

    internal static IReadOnlyList<Level> BuildFromRgba(
        GpuTextureFormat format,
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int mipLevelCount)
    {
        var levels = new List<Level>(Math.Max(0, mipLevelCount - 1));
        byte[] current = rgba.ToArray();
        int currentWidth = width;
        int currentHeight = height;

        for (int level = 1; level < mipLevelCount; level++)
        {
            (byte[] next, int nextWidth, int nextHeight) = Downsample(current, currentWidth, currentHeight);
            byte[] encoded = BlockCompressionCodec.IsBlockCompressed(format)
                ? BlockCompressionCodec.EncodeLevel(format, next, nextWidth, nextHeight)
                : next;
            levels.Add(new Level(level, nextWidth, nextHeight, encoded));
            current = next;
            currentWidth = nextWidth;
            currentHeight = nextHeight;
        }

        return levels;
    }

    internal static (byte[] Rgba, int Width, int Height) Downsample(
        ReadOnlySpan<byte> rgba,
        int width,
        int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int nextWidth = Math.Max(1, width / 2);
        int nextHeight = Math.Max(1, height / 2);
        var output = new byte[nextWidth * nextHeight * 4];

        for (int y = 0; y < nextHeight; y++)
        {
            int y0 = Math.Min((y * 2) + 0, height - 1);
            int y1 = Math.Min((y * 2) + 1, height - 1);
            for (int x = 0; x < nextWidth; x++)
            {
                int x0 = Math.Min((x * 2) + 0, width - 1);
                int x1 = Math.Min((x * 2) + 1, width - 1);

                int a = ((y0 * width) + x0) * 4;
                int b = ((y0 * width) + x1) * 4;
                int c = ((y1 * width) + x0) * 4;
                int d = ((y1 * width) + x1) * 4;
                int destination = ((y * nextWidth) + x) * 4;

                for (int channel = 0; channel < 4; channel++)
                {
                    int sum = rgba[a + channel] + rgba[b + channel] + rgba[c + channel] + rgba[d + channel];
                    output[destination + channel] = (byte)((sum + 2) / 4);
                }
            }
        }

        return (output, nextWidth, nextHeight);
    }
}
