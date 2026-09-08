using AcDream.Core.Rendering.Wb;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Textures;

public class TextureDecodeConformanceTests
{
    // ---- helpers ---------------------------------------------------------------

    private static Palette MakePalette(params ColorARGB[] colors)
    {
        var pal = new Palette();
        foreach (var c in colors)
            pal.Colors.Add(c);
        return pal;
    }

    private static ColorARGB Rgba(byte r, byte g, byte b, byte a = 0xFF)
        => new ColorARGB { Red = r, Green = g, Blue = b, Alpha = a };

    private static byte[] OurDecodeIndex16(byte[] src, Palette palette, int width, int height, bool isClipMap = false)
    {
        var rgba = new byte[width * height * 4];
        int paletteMax = palette.Colors.Count - 1;
        for (int i = 0; i < width * height; i++)
        {
            int s = i * 2;
            ushort idx = (ushort)(src[s] | (src[s + 1] << 8));
            if (idx > paletteMax) idx = 0;
            var c = palette.Colors[idx];
            int d = i * 4;
            if (isClipMap && idx < 8)
            {
                rgba[d + 0] = 0;
                rgba[d + 1] = 0;
                rgba[d + 2] = 0;
                rgba[d + 3] = 0;
            }
            else
            {
                rgba[d + 0] = c.Red;
                rgba[d + 1] = c.Green;
                rgba[d + 2] = c.Blue;
                rgba[d + 3] = c.Alpha;
            }
        }
        return rgba;
    }

    private static byte[] OurDecodeP8(byte[] src, Palette palette, int width, int height, bool isClipMap = false)
    {
        var rgba = new byte[width * height * 4];
        int paletteMax = palette.Colors.Count - 1;
        for (int i = 0; i < width * height; i++)
        {
            int idx = src[i];
            if (idx > paletteMax) idx = 0;
            var c = palette.Colors[idx];
            int d = i * 4;
            if (isClipMap && idx < 8)
            {
                rgba[d + 0] = 0;
                rgba[d + 1] = 0;
                rgba[d + 2] = 0;
                rgba[d + 3] = 0;
            }
            else
            {
                rgba[d + 0] = c.Red;
                rgba[d + 1] = c.Green;
                rgba[d + 2] = c.Blue;
                rgba[d + 3] = c.Alpha;
            }
        }
        return rgba;
    }

    private static byte[] OurDecodeA8R8G8B8(byte[] src, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            int s = i * 4;
            rgba[s + 0] = src[s + 2]; // R
            rgba[s + 1] = src[s + 1]; // G
            rgba[s + 2] = src[s + 0]; // B
            rgba[s + 3] = src[s + 3]; // A
        }
        return rgba;
    }

    private static byte[] OurDecodeR8G8B8(byte[] src, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            int s = i * 3;
            int d = i * 4;
            rgba[d + 0] = src[s + 2]; // R
            rgba[d + 1] = src[s + 1]; // G
            rgba[d + 2] = src[s + 0]; // B
            rgba[d + 3] = 0xFF;        // A = opaque
        }
        return rgba;
    }

    private static byte[] OurDecodeA8(byte[] src, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            byte a = src[i];
            int d = i * 4;
            rgba[d + 0] = a;
            rgba[d + 1] = a;
            rgba[d + 2] = a;
            rgba[d + 3] = a;
        }
        return rgba;
    }

    // ---- tests -----------------------------------------------------------------

    /// <summary>
    /// Test 1: INDEX16 normal mode — 2×2 image with two palette entries.
    /// WB's FillIndex16 and our DecodeIndex16 must produce identical RGBA bytes.
    /// </summary>
    [Fact]
    public void FillIndex16_MatchesOurDecodeIndex16()
    {
        var src = new byte[]
        {
            0x00, 0x00,  // pixel(0,0) → palette index 0
            0x01, 0x00,  // pixel(1,0) → palette index 1
            0x01, 0x00,  // pixel(0,1) → palette index 1
            0x00, 0x00,  // pixel(1,1) → palette index 0
        };
        var palette = MakePalette(
            Rgba(0xFF, 0x00, 0x00),  // index 0 = red
            Rgba(0x00, 0x00, 0xFF)   // index 1 = blue
        );

        var expected = OurDecodeIndex16(src, palette, 2, 2);

        var actual = new byte[2 * 2 * 4];
        TextureHelpers.FillIndex16(src, palette, actual, 2, 2);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FillIndex16_ClipMap_MatchesOurClipMapBehavior()
    {
        var src = new byte[]
        {
            0x00, 0x00,  // index 0 → transparent
            0x01, 0x00,  // index 1 → transparent
            0x07, 0x00,  // index 7 → transparent
            0x08, 0x00,  // index 8 → opaque
        };
        // Build a 16-entry palette so indices 0–8 are all valid.
        var palette = new Palette();
        for (int i = 0; i < 16; i++)
            palette.Colors.Add(Rgba(0xAA, 0xBB, 0xCC));

        var expected = OurDecodeIndex16(src, palette, 4, 1, isClipMap: true);

        var actual = new byte[4 * 1 * 4];
        TextureHelpers.FillIndex16(src, palette, actual, 4, 1, isClipMap: true);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Test 3: P8 (8-bit palette index) — 2×2 image.
    /// WB FillP8 and our DecodeP8 must produce identical RGBA output.
    /// </summary>
    [Fact]
    public void FillP8_MatchesOurDecodeP8()
    {
        // 2×2 P8: bytes are direct palette indices
        var src = new byte[] { 0x00, 0x01, 0x01, 0x00 };
        var palette = MakePalette(
            Rgba(0x10, 0x20, 0x30),  // index 0
            Rgba(0x40, 0x50, 0x60)   // index 1
        );

        var expected = OurDecodeP8(src, palette, 2, 2);

        var actual = new byte[2 * 2 * 4];
        TextureHelpers.FillP8(src, palette, actual, 2, 2);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Test 4: A8R8G8B8 (BGRA on-disk → RGBA) — 2×1 image.
    /// WB FillA8R8G8B8 and our DecodeA8R8G8B8 both swap B↔R.
    /// </summary>
    [Fact]
    public void FillA8R8G8B8_MatchesOurDecodeA8R8G8B8()
    {
        // On-disk layout: B, G, R, A per pixel
        var src = new byte[]
        {
            0x00, 0x00, 0xFF, 0xFF,  // pixel 0: B=0, G=0, R=255, A=255 → red
            0xFF, 0x00, 0x00, 0x80,  // pixel 1: B=255, G=0, R=0, A=128 → blue, semi-transparent
        };

        var expected = OurDecodeA8R8G8B8(src, 2, 1);

        var actual = new byte[2 * 1 * 4];
        TextureHelpers.FillA8R8G8B8(src, actual, 2, 1);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Test 5: R8G8B8 (BGR on-disk → RGBA, alpha forced 255) — 2×1 image.
    /// Both implementations output R,G,B,255 for each 3-byte BGR triple.
    /// </summary>
    [Fact]
    public void FillR8G8B8_MatchesOurDecodeR8G8B8()
    {
        // On-disk layout: B, G, R per pixel (24-bit BGR)
        var src = new byte[]
        {
            0x00, 0x00, 0xFF,  // pixel 0: B=0, G=0, R=255 → red
            0x00, 0xFF, 0x00,  // pixel 1: B=0, G=255, R=0 → green
        };

        var expected = OurDecodeR8G8B8(src, 2, 1);

        var actual = new byte[2 * 1 * 4];
        TextureHelpers.FillR8G8B8(src, actual, 2, 1);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FillA8Additive_MatchesOurDecodeA8()
    {
        // 4×1 single-byte-per-pixel alpha values
        var src = new byte[] { 0x00, 0x40, 0x80, 0xFF };

        var expected = OurDecodeA8(src, 4, 1);

        var actual = new byte[4 * 1 * 4];
        TextureHelpers.FillA8Additive(src, actual, 4, 1);

        Assert.Equal(expected, actual);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, actual[0..4]);
        Assert.Equal(new byte[] { 0x40, 0x40, 0x40, 0x40 }, actual[4..8]);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, actual[12..16]);
    }

    /// <summary>
    /// Test 7: A8 non-additive (FillA8) documents WB's behavior that DIFFERS from ours.
    /// WB's FillA8 sets R=G=B=255 and A=input_byte.
    /// Our DecodeA8 sets R=G=B=A=input_byte (the additive mode, used for terrain blending).
    /// This test proves the divergence exists and documents the WB behavior explicitly.
    /// </summary>
    [Fact]
    public void FillA8_NonAdditive_ProducesWhitePlusAlpha()
    {
        var src = new byte[] { 0x00, 0x80, 0xFF };  // 3×1

        var actual = new byte[3 * 1 * 4];
        TextureHelpers.FillA8(src, actual, 3, 1);

        // WB non-additive: R=G=B=255, A=input byte
        Assert.Equal(new byte[] { 255, 255, 255, 0x00 }, actual[0..4]);   // alpha=0
        Assert.Equal(new byte[] { 255, 255, 255, 0x80 }, actual[4..8]);   // alpha=128
        Assert.Equal(new byte[] { 255, 255, 255, 0xFF }, actual[8..12]);  // alpha=255

        var ourDecoded = OurDecodeA8(src, 3, 1);
        Assert.NotEqual(ourDecoded, actual);  // divergence is intentional — both are documented
    }

    /// <summary>
    /// Test 8: R5G6B5 (16-bit packed RGB, no alpha) — WB format we don't implement yet.
    /// Verifies the expected bit-expansion: 5-bit red → 8-bit by left-shifting 3,
    /// 6-bit green → 8-bit by left-shifting 2, 5-bit blue → 8-bit by left-shifting 3.
    /// Alpha is always 255.
    /// </summary>
    [Fact]
    public void FillR5G6B5_ProducesExpectedRgba()
    {
        // Encode a single pixel: R=0x1F (31), G=0x3F (63), B=0x1F (31)
        // Packed as 16-bit little-endian: bits 15-11=R, 10-5=G, 4-0=B
        // val = (0x1F << 11) | (0x3F << 5) | 0x1F = 0xFFFF
        var src = new byte[] { 0xFF, 0xFF };

        var actual = new byte[1 * 1 * 4];
        TextureHelpers.FillR5G6B5(src, actual, 1, 1);

        // R = (0x1F << 3) = 0xF8, G = (0x3F << 2) = 0xFC, B = (0x1F << 3) = 0xF8, A = 255
        Assert.Equal((byte)0xF8, actual[0]);  // R
        Assert.Equal((byte)0xFC, actual[1]);  // G
        Assert.Equal((byte)0xF8, actual[2]);  // B
        Assert.Equal((byte)255,  actual[3]);  // A always opaque

        // Test a second pixel: pure red = R=31, G=0, B=0
        // val = (0x1F << 11) = 0xF800
        var srcRed = new byte[] { 0x00, 0xF8 };  // little-endian 0xF800
        var actualRed = new byte[4];
        TextureHelpers.FillR5G6B5(srcRed, actualRed, 1, 1);
        Assert.Equal((byte)0xF8, actualRed[0]);  // R = 31 << 3 = 0xF8
        Assert.Equal((byte)0x00, actualRed[1]);  // G = 0
        Assert.Equal((byte)0x00, actualRed[2]);  // B = 0
        Assert.Equal((byte)255,  actualRed[3]);  // A
    }

    [Fact]
    public void FillA4R4G4B4_ProducesExpectedRgba()
    {
        // Encode one pixel: A=0xF(255), R=0xA(170), G=0x5(85), B=0x0(0)
        // val = (0xF << 12) | (0xA << 8) | (0x5 << 4) | 0x0 = 0xFA50
        // little-endian bytes: 0x50, 0xFA
        var src = new byte[] { 0x50, 0xFA };  // 1×1

        var actual = new byte[1 * 1 * 4];
        TextureHelpers.FillA4R4G4B4(src, actual, 1, 1);

        // R = 0xA * 17 = 170, G = 0x5 * 17 = 85, B = 0x0 * 17 = 0, A = 0xF * 17 = 255
        Assert.Equal((byte)(0xA * 17), actual[0]);  // R = 170
        Assert.Equal((byte)(0x5 * 17), actual[1]);  // G = 85
        Assert.Equal((byte)(0x0 * 17), actual[2]);  // B = 0
        Assert.Equal((byte)(0xF * 17), actual[3]);  // A = 255

        var srcZero = new byte[] { 0x00, 0x00 };
        var actualZero = new byte[4];
        TextureHelpers.FillA4R4G4B4(srcZero, actualZero, 1, 1);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, actualZero);
    }
}
