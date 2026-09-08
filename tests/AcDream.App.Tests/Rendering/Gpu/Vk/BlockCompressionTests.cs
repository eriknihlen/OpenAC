using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class BlockCompressionTests
{
    private static byte[] SolidRgba(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            pixels[(i * 4) + 0] = r;
            pixels[(i * 4) + 1] = g;
            pixels[(i * 4) + 2] = b;
            pixels[(i * 4) + 3] = a;
        }

        return pixels;
    }

    // ── sizes ────────────────────────────────────────────────────────────────

    [Fact]
    public void BlockSizesMatchTheDxtFormats()
    {
        Assert.Equal(8, BlockCompressionCodec.BlockSizeBytes(GpuTextureFormat.Bc1Unorm));
        Assert.Equal(16, BlockCompressionCodec.BlockSizeBytes(GpuTextureFormat.Bc2Unorm));
        Assert.Equal(16, BlockCompressionCodec.BlockSizeBytes(GpuTextureFormat.Bc3Unorm));
    }

    [Fact]
    public void LevelSizeRoundsUpToWholeBlocks()
    {
        // A 5x5 BC1 level still needs 2x2 blocks: the edge block is partly
        // outside the image and is still stored in full.
        Assert.Equal(4 * 8, BlockCompressionCodec.LevelSizeBytes(GpuTextureFormat.Bc1Unorm, 5, 5));
        // And a level smaller than one block is still one block.
        Assert.Equal(8, BlockCompressionCodec.LevelSizeBytes(GpuTextureFormat.Bc1Unorm, 1, 1));
    }

    [Fact]
    public void FullMipChainReachesOneByOne()
    {
        Assert.Equal(9, VulkanTextureFormatMapping.FullMipLevelCount(256, 256));
        Assert.Equal(1, VulkanTextureFormatMapping.FullMipLevelCount(1, 1));
        Assert.Equal(9, VulkanTextureFormatMapping.FullMipLevelCount(256, 4));
    }

    [Fact]
    public void LevelExtentFloorsAtOneTexel()
    {
        Assert.Equal((64, 16), VulkanTextureFormatMapping.LevelExtent(256, 64, 2));
        Assert.Equal((1, 1), VulkanTextureFormatMapping.LevelExtent(4, 4, 9));
    }


    [Fact]
    public void SolidColourSurvivesTheRoundTripExactlyInEveryFormat()
    {
        GpuTextureFormat[] formats =
        [
            GpuTextureFormat.Bc1Unorm,
            GpuTextureFormat.Bc2Unorm,
            GpuTextureFormat.Bc3Unorm,
        ];

        // 8-bit values that are exactly representable in RGB565 (multiples of
        // the quantisation step), so "exact" is a fair thing to demand.
        byte[] source = SolidRgba(8, 8, r: 0x00, g: 0x00, b: 0xFF);

        foreach (GpuTextureFormat format in formats)
        {
            byte[] encoded = BlockCompressionCodec.EncodeLevel(format, source, 8, 8);
            byte[] decoded = BlockCompressionCodec.DecodeLevel(format, encoded, 8, 8);
            Assert.Equal(source, decoded);
        }
    }

    [Fact]
    public void Bc3PreservesASolidAlphaExactly()
    {
        byte[] source = SolidRgba(4, 4, 0xFF, 0xFF, 0xFF, a: 0x42);

        byte[] encoded = BlockCompressionCodec.EncodeLevel(GpuTextureFormat.Bc3Unorm, source, 4, 4);
        byte[] decoded = BlockCompressionCodec.DecodeLevel(GpuTextureFormat.Bc3Unorm, encoded, 4, 4);

        for (int texel = 0; texel < 16; texel++)
            Assert.Equal(0x42, decoded[(texel * 4) + 3]);
    }

    [Fact]
    public void Bc1KeepsCutOutTexelsFullyTransparent()
    {
        byte[] source = SolidRgba(4, 4, 0xFF, 0xFF, 0xFF);
        for (int texel = 0; texel < 16; texel += 2)
            source[(texel * 4) + 3] = 0;

        byte[] encoded = BlockCompressionCodec.EncodeLevel(GpuTextureFormat.Bc1Unorm, source, 4, 4);
        byte[] decoded = BlockCompressionCodec.DecodeLevel(GpuTextureFormat.Bc1Unorm, encoded, 4, 4);

        for (int texel = 0; texel < 16; texel++)
        {
            byte alpha = decoded[(texel * 4) + 3];
            if (texel % 2 == 0)
                Assert.Equal(0, alpha);
            else
                Assert.Equal(255, alpha);
        }
    }

    [Fact]
    public void Bc1UsesThreeColourModeOnlyWhenTheBlockHasCutOutTexels()
    {
        byte[] opaque = SolidRgba(4, 4, 0x20, 0x40, 0x60);
        byte[] withHoles = SolidRgba(4, 4, 0x20, 0x40, 0x60);
        withHoles[3] = 0;

        byte[] opaqueBlock = BlockCompressionCodec.EncodeLevel(GpuTextureFormat.Bc1Unorm, opaque, 4, 4);
        byte[] holedBlock = BlockCompressionCodec.EncodeLevel(GpuTextureFormat.Bc1Unorm, withHoles, 4, 4);

        ushort opaqueC0 = (ushort)(opaqueBlock[0] | (opaqueBlock[1] << 8));
        ushort opaqueC1 = (ushort)(opaqueBlock[2] | (opaqueBlock[3] << 8));
        ushort holedC0 = (ushort)(holedBlock[0] | (holedBlock[1] << 8));
        ushort holedC1 = (ushort)(holedBlock[2] | (holedBlock[3] << 8));

        Assert.True(opaqueC0 > opaqueC1, "an opaque block must use BC1's four-colour mode");
        Assert.True(holedC0 <= holedC1, "a block with a cut-out texel must use BC1's three-colour mode");
    }

    [Fact]
    public void GradientRoundTripStaysCloseRatherThanCollapsing()
    {
        var source = new byte[4 * 4 * 4];
        for (int texel = 0; texel < 16; texel++)
        {
            source[(texel * 4) + 0] = (byte)((texel % 4) * 80);
            source[(texel * 4) + 3] = 255;
        }

        byte[] encoded = BlockCompressionCodec.EncodeLevel(GpuTextureFormat.Bc1Unorm, source, 4, 4);
        byte[] decoded = BlockCompressionCodec.DecodeLevel(GpuTextureFormat.Bc1Unorm, encoded, 4, 4);

        var distinct = new HashSet<byte>();
        for (int texel = 0; texel < 16; texel++)
        {
            distinct.Add(decoded[texel * 4]);
            Assert.InRange(Math.Abs(decoded[texel * 4] - source[texel * 4]), 0, 12);
        }

        Assert.Equal(4, distinct.Count);
    }

    // ── determinism ──────────────────────────────────────────────────────────

    [Fact]
    public void EncodingTheSameBytesTwiceProducesTheSameBytes()
    {
        var random = new Random(20260728);
        var source = new byte[16 * 16 * 4];
        random.NextBytes(source);

        byte[] first = BlockCompressionCodec.EncodeLevel(GpuTextureFormat.Bc3Unorm, source, 16, 16);
        byte[] second = BlockCompressionCodec.EncodeLevel(GpuTextureFormat.Bc3Unorm, source, 16, 16);

        Assert.Equal(first, second);
    }

    [Fact]
    public void TheWholeMipChainIsReproducible()
    {
        var random = new Random(1189998819991197253L.GetHashCode());
        var source = new byte[32 * 32 * 4];
        random.NextBytes(source);

        IReadOnlyList<BlockCompressionMipChain.Level> first =
            BlockCompressionMipChain.BuildFromRgba(GpuTextureFormat.Bc1Unorm, source, 32, 32, 6);
        IReadOnlyList<BlockCompressionMipChain.Level> second =
            BlockCompressionMipChain.BuildFromRgba(GpuTextureFormat.Bc1Unorm, source, 32, 32, 6);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
            Assert.Equal(first[i].Data, second[i].Data);
    }


    [Fact]
    public void ChainHalvesEachLevelAndStopsAtOneByOne()
    {
        byte[] source = SolidRgba(16, 16, 0x00, 0xFF, 0x00);

        IReadOnlyList<BlockCompressionMipChain.Level> levels =
            BlockCompressionMipChain.BuildFromRgba(GpuTextureFormat.Bc1Unorm, source, 16, 16, 5);

        Assert.Equal([1, 2, 3, 4], levels.Select(level => level.MipLevel));
        Assert.Equal([8, 4, 2, 1], levels.Select(level => level.Width));
        Assert.Equal([8, 4, 2, 1], levels.Select(level => level.Height));
        // Even a 1x1 level occupies one whole block.
        Assert.Equal(8, levels[^1].Data.Length);
    }

    [Fact]
    public void ChainOfAUniformImageIsThatColourAtEveryLevel()
    {
        byte[] source = SolidRgba(16, 16, 0x00, 0x00, 0xFF);

        IReadOnlyList<BlockCompressionMipChain.Level> levels =
            BlockCompressionMipChain.BuildFromRgba(GpuTextureFormat.Bc1Unorm, source, 16, 16, 5);

        foreach (BlockCompressionMipChain.Level level in levels)
        {
            byte[] decoded = BlockCompressionCodec.DecodeLevel(
                GpuTextureFormat.Bc1Unorm,
                level.Data,
                level.Width,
                level.Height);
            for (int texel = 0; texel < level.Width * level.Height; texel++)
            {
                Assert.Equal(0x00, decoded[texel * 4]);
                Assert.Equal(0x00, decoded[(texel * 4) + 1]);
                Assert.Equal(0xFF, decoded[(texel * 4) + 2]);
            }
        }
    }

    [Fact]
    public void ChainStartsFromACompressedLevelZeroWithoutReEncodingIt()
    {
        byte[] source = SolidRgba(8, 8, 0xFF, 0x00, 0x00);
        byte[] level0 = BlockCompressionCodec.EncodeLevel(GpuTextureFormat.Bc1Unorm, source, 8, 8);

        IReadOnlyList<BlockCompressionMipChain.Level> levels =
            BlockCompressionMipChain.BuildCompressed(GpuTextureFormat.Bc1Unorm, level0, 8, 8, 4);

        Assert.Equal([1, 2, 3], levels.Select(level => level.MipLevel));
    }

    [Fact]
    public void ChainOfASingleLevelImageIsEmpty()
    {
        byte[] source = SolidRgba(4, 4, 1, 2, 3);
        Assert.Empty(BlockCompressionMipChain.BuildFromRgba(GpuTextureFormat.Bc1Unorm, source, 4, 4, 1));
    }

    // ── the filter ───────────────────────────────────────────────────────────

    [Fact]
    public void DownsampleAveragesTwoByTwoBlocksWithRoundHalfUp()
    {
        // Two texels of 0 and two of 1 average to 0.5, which rounds to 1.
        byte[] source =
        [
            0, 0, 0, 0,
            1, 1, 1, 1,
            1, 1, 1, 1,
            0, 0, 0, 0,
        ];

        (byte[] output, int width, int height) = BlockCompressionMipChain.Downsample(source, 2, 2);

        Assert.Equal(1, width);
        Assert.Equal(1, height);
        Assert.Equal<byte[]>([1, 1, 1, 1], output);
    }

    [Fact]
    public void DownsampleClampsAtOddExtentsRatherThanReadingOutOfBounds()
    {
        byte[] source = SolidRgba(3, 3, 10, 20, 30);

        (byte[] output, int width, int height) = BlockCompressionMipChain.Downsample(source, 3, 3);

        Assert.Equal(1, width);
        Assert.Equal(1, height);
        Assert.Equal<byte[]>([10, 20, 30, 255], output);
    }

    [Fact]
    public void DownsampleAveragesAlphaWithoutPremultiplying()
    {
        byte[] source =
        [
            255, 255, 255, 255,
            255, 255, 255, 0,
            255, 255, 255, 255,
            255, 255, 255, 0,
        ];

        (byte[] output, _, _) = BlockCompressionMipChain.Downsample(source, 2, 2);

        Assert.Equal(255, output[0]);
        Assert.Equal(255, output[1]);
        Assert.Equal(255, output[2]);
        Assert.Equal(128, output[3]);
    }

    // ── the table's slot policy ──────────────────────────────────────────────

    [Fact]
    public void TextureSlotsAreHandedOutLowestFirst()
    {
        var slots = new VulkanTextureSlotAllocator(16);

        Assert.Equal(0u, slots.Allocate());
        Assert.Equal(1u, slots.Allocate());
        Assert.Equal(2u, slots.Allocate());
        Assert.Equal(3u, slots.HighWater);
    }

    [Fact]
    public void AReleasedSlotIsReusedAndTheHighWaterDoesNotGrow()
    {
        var slots = new VulkanTextureSlotAllocator(16);
        slots.Allocate();
        uint second = slots.Allocate();
        slots.Allocate();

        slots.Release(second);

        Assert.Equal(second, slots.Allocate());
        Assert.Equal(3u, slots.HighWater);
    }

    [Fact]
    public void ReleasingTheSameSlotTwiceIsRejected()
    {
        var slots = new VulkanTextureSlotAllocator(16);
        uint slot = slots.Allocate();
        slots.Release(slot);

        // Two live textures sharing one table index is a silent
        // wrong-texture bug, so this has to be loud.
        Assert.Throws<InvalidOperationException>(() => slots.Release(slot));
    }

    [Fact]
    public void RunningOutOfSlotsNamesTheCapacityToRaise()
    {
        var slots = new VulkanTextureSlotAllocator(2);
        slots.Allocate();
        slots.Allocate();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => slots.Allocate());

        Assert.Contains("TextureTableCapacity", error.Message, StringComparison.Ordinal);
    }
}
