namespace AcDream.App.Rendering.Gpu.Vk;

internal static class BlockCompressionCodec
{
    /// <summary>Edge of a BC block in texels.</summary>
    internal const int BlockExtent = 4;

    internal static int BlockSizeBytes(GpuTextureFormat format) => format switch
    {
        GpuTextureFormat.Bc1Unorm => 8,
        GpuTextureFormat.Bc2Unorm or GpuTextureFormat.Bc3Unorm => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a block-compressed format."),
    };

    internal static bool IsBlockCompressed(GpuTextureFormat format) =>
        format is GpuTextureFormat.Bc1Unorm or GpuTextureFormat.Bc2Unorm or GpuTextureFormat.Bc3Unorm;

    internal static int BlockCount(int extent) => Math.Max(1, (extent + BlockExtent - 1) / BlockExtent);

    internal static int LevelSizeBytes(GpuTextureFormat format, int width, int height) =>
        BlockCount(width) * BlockCount(height) * BlockSizeBytes(format);

    // ── decode ───────────────────────────────────────────────────────────────

    internal static byte[] DecodeLevel(
        GpuTextureFormat format,
        ReadOnlySpan<byte> blocks,
        int width,
        int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int blockSize = BlockSizeBytes(format);
        int blocksX = BlockCount(width);
        int blocksY = BlockCount(height);
        if (blocks.Length < blocksX * blocksY * blockSize)
        {
            throw new ArgumentException(
                $"A {width}x{height} {format} level needs {blocksX * blocksY * blockSize} bytes; " +
                $"{blocks.Length} were supplied.",
                nameof(blocks));
        }

        var rgba = new byte[width * height * 4];
        Span<byte> texels = stackalloc byte[BlockExtent * BlockExtent * 4];
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int offset = ((by * blocksX) + bx) * blockSize;
                DecodeBlock(format, blocks.Slice(offset, blockSize), texels);

                for (int y = 0; y < BlockExtent; y++)
                {
                    int targetY = (by * BlockExtent) + y;
                    if (targetY >= height)
                        break;
                    for (int x = 0; x < BlockExtent; x++)
                    {
                        int targetX = (bx * BlockExtent) + x;
                        if (targetX >= width)
                            break;
                        int source = ((y * BlockExtent) + x) * 4;
                        int destination = ((targetY * width) + targetX) * 4;
                        texels.Slice(source, 4).CopyTo(rgba.AsSpan(destination, 4));
                    }
                }
            }
        }

        return rgba;
    }

    /// <summary>Decodes one block into 16 RGBA8 texels in row-major order.</summary>
    internal static void DecodeBlock(GpuTextureFormat format, ReadOnlySpan<byte> block, Span<byte> rgba)
    {
        if (rgba.Length < BlockExtent * BlockExtent * 4)
            throw new ArgumentException("A decoded block needs 64 bytes.", nameof(rgba));

        int colourOffset = format == GpuTextureFormat.Bc1Unorm ? 0 : 8;
        DecodeColourBlock(
            block.Slice(colourOffset, 8),
            allowTransparentMode: format == GpuTextureFormat.Bc1Unorm,
            rgba);

        switch (format)
        {
            case GpuTextureFormat.Bc2Unorm:
                DecodeExplicitAlpha(block[..8], rgba);
                break;
            case GpuTextureFormat.Bc3Unorm:
                DecodeInterpolatedAlpha(block[..8], rgba);
                break;
        }
    }

    private static void DecodeColourBlock(ReadOnlySpan<byte> block, bool allowTransparentMode, Span<byte> rgba)
    {
        ushort c0 = (ushort)(block[0] | (block[1] << 8));
        ushort c1 = (ushort)(block[2] | (block[3] << 8));
        uint indices = (uint)(block[4] | (block[5] << 8) | (block[6] << 16) | (block[7] << 24));

        Span<byte> palette = stackalloc byte[4 * 4];
        Unpack565(c0, palette[..4]);
        Unpack565(c1, palette.Slice(4, 4));

        bool transparent = allowTransparentMode && c0 <= c1;
        if (transparent)
        {
            for (int channel = 0; channel < 3; channel++)
                palette[8 + channel] = (byte)((palette[channel] + palette[4 + channel]) / 2);
            palette[11] = 255;
            palette[12] = 0;
            palette[13] = 0;
            palette[14] = 0;
            palette[15] = 0;
        }
        else
        {
            for (int channel = 0; channel < 3; channel++)
            {
                palette[8 + channel] = (byte)(((2 * palette[channel]) + palette[4 + channel]) / 3);
                palette[12 + channel] = (byte)((palette[channel] + (2 * palette[4 + channel])) / 3);
            }

            palette[11] = 255;
            palette[15] = 255;
        }

        for (int texel = 0; texel < 16; texel++)
        {
            int index = (int)((indices >> (texel * 2)) & 0x3);
            palette.Slice(index * 4, 4).CopyTo(rgba.Slice(texel * 4, 4));
        }
    }

    private static void DecodeExplicitAlpha(ReadOnlySpan<byte> alphaBlock, Span<byte> rgba)
    {
        for (int texel = 0; texel < 16; texel++)
        {
            int nibble = (alphaBlock[texel / 2] >> ((texel % 2) * 4)) & 0xF;
            // 4-bit alpha replicated into 8 bits, the standard BC2 expansion.
            rgba[(texel * 4) + 3] = (byte)((nibble * 255) / 15);
        }
    }

    private static void DecodeInterpolatedAlpha(ReadOnlySpan<byte> alphaBlock, Span<byte> rgba)
    {
        byte a0 = alphaBlock[0];
        byte a1 = alphaBlock[1];
        Span<byte> palette = stackalloc byte[8];
        palette[0] = a0;
        palette[1] = a1;
        if (a0 > a1)
        {
            for (int i = 1; i <= 6; i++)
                palette[i + 1] = (byte)((((7 - i) * a0) + (i * a1)) / 7);
        }
        else
        {
            for (int i = 1; i <= 4; i++)
                palette[i + 1] = (byte)((((5 - i) * a0) + (i * a1)) / 5);
            palette[6] = 0;
            palette[7] = 255;
        }

        ulong bits = 0;
        for (int i = 0; i < 6; i++)
            bits |= (ulong)alphaBlock[2 + i] << (i * 8);

        for (int texel = 0; texel < 16; texel++)
        {
            int index = (int)((bits >> (texel * 3)) & 0x7);
            rgba[(texel * 4) + 3] = palette[index];
        }
    }

    private static ushort Pack565(int r, int g, int b)
    {
        int r5 = ((Math.Clamp(r, 0, 255) * 31) + 127) / 255;
        int g6 = ((Math.Clamp(g, 0, 255) * 63) + 127) / 255;
        int b5 = ((Math.Clamp(b, 0, 255) * 31) + 127) / 255;
        return (ushort)((r5 << 11) | (g6 << 5) | b5);
    }

    private static void Unpack565(ushort packed, Span<byte> rgba)
    {
        int r = (packed >> 11) & 0x1F;
        int g = (packed >> 5) & 0x3F;
        int b = packed & 0x1F;
        rgba[0] = (byte)((r << 3) | (r >> 2));
        rgba[1] = (byte)((g << 2) | (g >> 4));
        rgba[2] = (byte)((b << 3) | (b >> 2));
        rgba[3] = 255;
    }

    // ── encode ───────────────────────────────────────────────────────────────

    internal static byte[] EncodeLevel(
        GpuTextureFormat format,
        ReadOnlySpan<byte> rgba,
        int width,
        int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (rgba.Length < width * height * 4)
            throw new ArgumentException($"A {width}x{height} RGBA8 level needs {width * height * 4} bytes.", nameof(rgba));

        int blockSize = BlockSizeBytes(format);
        int blocksX = BlockCount(width);
        int blocksY = BlockCount(height);
        var output = new byte[blocksX * blocksY * blockSize];

        Span<byte> texels = stackalloc byte[BlockExtent * BlockExtent * 4];
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                GatherBlock(rgba, width, height, bx, by, texels);
                EncodeBlock(format, texels, output.AsSpan(((by * blocksX) + bx) * blockSize, blockSize));
            }
        }

        return output;
    }

    private static void GatherBlock(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int blockX,
        int blockY,
        Span<byte> texels)
    {
        for (int y = 0; y < BlockExtent; y++)
        {
            int sourceY = Math.Min((blockY * BlockExtent) + y, height - 1);
            for (int x = 0; x < BlockExtent; x++)
            {
                int sourceX = Math.Min((blockX * BlockExtent) + x, width - 1);
                int source = ((sourceY * width) + sourceX) * 4;
                rgba.Slice(source, 4).CopyTo(texels.Slice(((y * BlockExtent) + x) * 4, 4));
            }
        }
    }

    internal static void EncodeBlock(GpuTextureFormat format, ReadOnlySpan<byte> rgba, Span<byte> block)
    {
        block.Clear();
        switch (format)
        {
            case GpuTextureFormat.Bc1Unorm:
                EncodeColourBlock(rgba, allowTransparentMode: true, block[..8]);
                break;
            case GpuTextureFormat.Bc2Unorm:
                EncodeExplicitAlpha(rgba, block[..8]);
                EncodeColourBlock(rgba, allowTransparentMode: false, block.Slice(8, 8));
                break;
            case GpuTextureFormat.Bc3Unorm:
                EncodeInterpolatedAlpha(rgba, block[..8]);
                EncodeColourBlock(rgba, allowTransparentMode: false, block.Slice(8, 8));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Not a block-compressed format.");
        }
    }

    private static void EncodeColourBlock(ReadOnlySpan<byte> rgba, bool allowTransparentMode, Span<byte> block)
    {
        bool needsTransparency = false;
        if (allowTransparentMode)
        {
            for (int texel = 0; texel < 16 && !needsTransparency; texel++)
                needsTransparency = rgba[(texel * 4) + 3] < 128;
        }

        FindColourExtremes(rgba, needsTransparency, out int minR, out int minG, out int minB, out int maxR, out int maxG, out int maxB);
        ushort c0 = Pack565(maxR, maxG, maxB);
        ushort c1 = Pack565(minR, minG, minB);

        if (needsTransparency)
        {
            if (c0 > c1)
                (c0, c1) = (c1, c0);
        }
        else if (c0 <= c1)
        {
            if (c0 == c1)
            {
                if (c1 == 0)
                    c0 = 1;
                else
                    c1 = (ushort)(c1 - 1);
            }
            else
            {
                (c0, c1) = (c1, c0);
            }
        }

        Span<byte> palette = stackalloc byte[4 * 4];
        Unpack565(c0, palette[..4]);
        Unpack565(c1, palette.Slice(4, 4));
        if (needsTransparency)
        {
            for (int channel = 0; channel < 3; channel++)
                palette[8 + channel] = (byte)((palette[channel] + palette[4 + channel]) / 2);
        }
        else
        {
            for (int channel = 0; channel < 3; channel++)
            {
                palette[8 + channel] = (byte)(((2 * palette[channel]) + palette[4 + channel]) / 3);
                palette[12 + channel] = (byte)((palette[channel] + (2 * palette[4 + channel])) / 3);
            }
        }

        uint indices = 0;
        int paletteSize = needsTransparency ? 3 : 4;
        for (int texel = 0; texel < 16; texel++)
        {
            int index;
            if (needsTransparency && rgba[(texel * 4) + 3] < 128)
            {
                index = 3;
            }
            else
            {
                index = NearestPaletteEntry(rgba.Slice(texel * 4, 3), palette, paletteSize);
            }

            indices |= (uint)index << (texel * 2);
        }

        block[0] = (byte)(c0 & 0xFF);
        block[1] = (byte)(c0 >> 8);
        block[2] = (byte)(c1 & 0xFF);
        block[3] = (byte)(c1 >> 8);
        block[4] = (byte)(indices & 0xFF);
        block[5] = (byte)((indices >> 8) & 0xFF);
        block[6] = (byte)((indices >> 16) & 0xFF);
        block[7] = (byte)((indices >> 24) & 0xFF);
    }

    private static void FindColourExtremes(
        ReadOnlySpan<byte> rgba,
        bool ignoreTransparentTexels,
        out int minR, out int minG, out int minB,
        out int maxR, out int maxG, out int maxB)
    {
        minR = minG = minB = 255;
        maxR = maxG = maxB = 0;
        bool any = false;
        for (int texel = 0; texel < 16; texel++)
        {
            if (ignoreTransparentTexels && rgba[(texel * 4) + 3] < 128)
                continue;
            any = true;
            int r = rgba[texel * 4];
            int g = rgba[(texel * 4) + 1];
            int b = rgba[(texel * 4) + 2];
            minR = Math.Min(minR, r);
            minG = Math.Min(minG, g);
            minB = Math.Min(minB, b);
            maxR = Math.Max(maxR, r);
            maxG = Math.Max(maxG, g);
            maxB = Math.Max(maxB, b);
        }

        if (any)
            return;

        minR = minG = minB = 0;
        maxR = maxG = maxB = 0;
    }

    private static int NearestPaletteEntry(ReadOnlySpan<byte> colour, ReadOnlySpan<byte> palette, int paletteSize)
    {
        int best = 0;
        int bestDistance = int.MaxValue;
        for (int entry = 0; entry < paletteSize; entry++)
        {
            int dr = colour[0] - palette[entry * 4];
            int dg = colour[1] - palette[(entry * 4) + 1];
            int db = colour[2] - palette[(entry * 4) + 2];
            int distance = (dr * dr) + (dg * dg) + (db * db);
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            best = entry;
        }

        return best;
    }

    private static void EncodeExplicitAlpha(ReadOnlySpan<byte> rgba, Span<byte> alphaBlock)
    {
        for (int texel = 0; texel < 16; texel++)
        {
            int nibble = (rgba[(texel * 4) + 3] * 15 + 127) / 255;
            int index = texel / 2;
            if (texel % 2 == 0)
                alphaBlock[index] = (byte)((alphaBlock[index] & 0xF0) | nibble);
            else
                alphaBlock[index] = (byte)((alphaBlock[index] & 0x0F) | (nibble << 4));
        }
    }

    private static void EncodeInterpolatedAlpha(ReadOnlySpan<byte> rgba, Span<byte> alphaBlock)
    {
        byte min = 255;
        byte max = 0;
        for (int texel = 0; texel < 16; texel++)
        {
            byte a = rgba[(texel * 4) + 3];
            min = Math.Min(min, a);
            max = Math.Max(max, a);
        }

        byte a0 = max;
        byte a1 = min;
        if (a0 == a1)
        {
            if (a1 > 0)
                a1 = (byte)(a1 - 1);
            else
                a0 = 1;
        }

        Span<byte> palette = stackalloc byte[8];
        palette[0] = a0;
        palette[1] = a1;
        for (int i = 1; i <= 6; i++)
            palette[i + 1] = (byte)((((7 - i) * a0) + (i * a1)) / 7);

        ulong bits = 0;
        for (int texel = 0; texel < 16; texel++)
        {
            byte a = rgba[(texel * 4) + 3];
            int best = 0;
            int bestDistance = int.MaxValue;
            for (int entry = 0; entry < 8; entry++)
            {
                int distance = Math.Abs(a - palette[entry]);
                if (distance >= bestDistance)
                    continue;
                bestDistance = distance;
                best = entry;
            }

            bits |= (ulong)best << (texel * 3);
        }

        alphaBlock[0] = a0;
        alphaBlock[1] = a1;
        for (int i = 0; i < 6; i++)
            alphaBlock[2 + i] = (byte)((bits >> (i * 8)) & 0xFF);
    }
}
