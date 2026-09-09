using AcDream.Core.Textures;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Textures;

public class SurfaceDecoderTests
{
    [Fact]
    public void ApplyAuthoredTranslucency_ScalesAlphaOnly_InPlace()
    {
        var texture = new DecodedTexture(
            Rgba8: [10, 20, 30, 200, 40, 50, 60, 100],
            Width: 2,
            Height: 1);

        var result = SurfaceDecoder.ApplyAuthoredTranslucency(texture, 0.5f);

        Assert.Same(texture, result);
        Assert.Equal(new byte[] { 10, 20, 30, 100, 40, 50, 60, 50 }, result.Rgba8);
    }

    [Fact]
    public void ApplyAuthoredTranslucency_FullTranslucency_ZeroesAlpha()
    {
        var texture = new DecodedTexture(Rgba8: [255, 255, 255, 255], Width: 1, Height: 1);

        var result = SurfaceDecoder.ApplyAuthoredTranslucency(texture, 1f);

        Assert.Equal(0, result.Rgba8[3]);
        Assert.Equal(255, result.Rgba8[0]);
    }

    [Fact]
    public void ApplyAuthoredTranslucency_ZeroOrNegative_IsANoOp()
    {
        var texture = new DecodedTexture(Rgba8: [1, 2, 3, 4], Width: 1, Height: 1);

        Assert.Same(texture, SurfaceDecoder.ApplyAuthoredTranslucency(texture, 0f));
        Assert.Equal(4, texture.Rgba8[3]);
        Assert.Same(texture, SurfaceDecoder.ApplyAuthoredTranslucency(texture, -0.25f));
        Assert.Equal(4, texture.Rgba8[3]);
    }

    [Fact]
    public void Decode_A8R8G8B8_ConvertsToRgba8()
    {
        // Source format is B, G, R, A in memory (little-endian ARGB).
        // One 2x2 image: red, green, blue, white pixels.
        var src = new byte[]
        {
            0x00, 0x00, 0xFF, 0xFF,  // red   (B=0, G=0, R=255, A=255)
            0x00, 0xFF, 0x00, 0xFF,  // green
            0xFF, 0x00, 0x00, 0xFF,  // blue
            0xFF, 0xFF, 0xFF, 0xFF,  // white
        };
        var rs = new RenderSurface
        {
            Width = 2,
            Height = 2,
            Format = PixelFormat.PFID_A8R8G8B8,
            SourceData = src,
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs);

        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(16, decoded.Rgba8.Length);  // 2*2*4
        // red pixel, in RGBA: 255, 0, 0, 255
        Assert.Equal(0xFF, decoded.Rgba8[0]);
        Assert.Equal(0x00, decoded.Rgba8[1]);
        Assert.Equal(0x00, decoded.Rgba8[2]);
        Assert.Equal(0xFF, decoded.Rgba8[3]);
    }

    [Fact]
    public void Decode_UnsupportedFormat_ReturnsMagenta()
    {
        var rs = new RenderSurface
        {
            Width = 4,
            Height = 4,
            Format = PixelFormat.PFID_INDEX16,  // not implemented path
            SourceData = new byte[32],
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs);

        Assert.Same(DecodedTexture.Magenta, decoded);
    }

    [Fact]
    public void Decode_A8_NonAdditive_ProducesWhitePlusAlpha()
    {
        var src = new byte[] { 0x00, 0x40, 0x80, 0xFF };  // 2x2 image
        var rs = new RenderSurface
        {
            Width = 2,
            Height = 2,
            Format = PixelFormat.PFID_A8,
            SourceData = src,
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs);

        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(16, decoded.Rgba8.Length);
        // Each input byte expands to (255, 255, 255, val) — white with varying alpha
        Assert.Equal(new byte[]
        {
            255, 255, 255, 0x00,
            255, 255, 255, 0x40,
            255, 255, 255, 0x80,
            255, 255, 255, 0xFF,
        }, decoded.Rgba8);
    }

    [Fact]
    public void Decode_A8_Additive_ReplicatesByteToAllChannels()
    {
        // isAdditive=true = WB FillA8Additive semantics: R=G=B=A=val.
        // Used for terrain blending alpha masks (TerrainAtlas always passes isAdditive:true).
        var src = new byte[] { 0x00, 0x40, 0x80, 0xFF };  // 2x2 image
        var rs = new RenderSurface
        {
            Width = 2,
            Height = 2,
            Format = PixelFormat.PFID_A8,
            SourceData = src,
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs, palette: null, isClipMap: false, isAdditive: true);

        Assert.Equal(16, decoded.Rgba8.Length);
        Assert.Equal(new byte[]
        {
            0x00, 0x00, 0x00, 0x00,
            0x40, 0x40, 0x40, 0x40,
            0x80, 0x80, 0x80, 0x80,
            0xFF, 0xFF, 0xFF, 0xFF,
        }, decoded.Rgba8);
    }

    [Fact]
    public void Decode_CustomLscapeAlpha_TreatedIdenticallyToA8()
    {
        var src = new byte[] { 0x10, 0x20, 0x30, 0x40 };  // 2x2
        var rs = new RenderSurface
        {
            Width = 2,
            Height = 2,
            Format = PixelFormat.PFID_CUSTOM_LSCAPE_ALPHA,
            SourceData = src,
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs);

        Assert.Equal(16, decoded.Rgba8.Length);
        Assert.Equal(new byte[]
        {
            255, 255, 255, 0x10,
            255, 255, 255, 0x20,
            255, 255, 255, 0x30,
            255, 255, 255, 0x40,
        }, decoded.Rgba8);
    }

    [Fact]
    public void Decode_A8_WithShortSourceData_ReturnsMagenta()
    {
        var rs = new RenderSurface
        {
            Width = 4,
            Height = 4,
            Format = PixelFormat.PFID_A8,
            SourceData = new byte[8],  // expects 16
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs);

        Assert.Same(DecodedTexture.Magenta, decoded);
    }

    [Fact]
    public void Decode_NullSourceData_ReturnsMagenta()
    {
        var rs = new RenderSurface
        {
            Width = 4,
            Height = 4,
            Format = PixelFormat.PFID_A8R8G8B8,
            SourceData = null!,
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs);

        Assert.Same(DecodedTexture.Magenta, decoded);
    }

    [Fact]
    public void Decode_TruncatedA8R8G8B8_ReturnsMagenta()
    {
        // Buffer too small for width*height*4.
        var rs = new RenderSurface
        {
            Width = 2,
            Height = 2,
            Format = PixelFormat.PFID_A8R8G8B8,
            SourceData = new byte[8],  // should be 16
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs);

        Assert.Same(DecodedTexture.Magenta, decoded);
    }

    [Fact]
    public void DecodeSolidColor_Opaque_PreservesAlpha()
    {
        var color = new ColorARGB { Alpha = 0xFF, Red = 0x11, Green = 0x22, Blue = 0x33 };

        var decoded = SurfaceDecoder.DecodeSolidColor(color, translucency: 0f);

        Assert.Equal(1, decoded.Width);
        Assert.Equal(1, decoded.Height);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0xFF }, decoded.Rgba8);
    }

    [Fact]
    public void DecodeSolidColor_FullyTranslucent_AlphaGoesToZero()
    {
        var color = new ColorARGB { Alpha = 0xFF, Red = 0xC8, Green = 0xC8, Blue = 0xC8 };

        var decoded = SurfaceDecoder.DecodeSolidColor(color, translucency: 1f);

        Assert.Equal(0, decoded.Rgba8[3]);  // alpha must be zero
    }

    [Fact]
    public void DecodeIndex16_ClipMap_ZerosAlphaForLowIndices()
    {
        var rs = new RenderSurface
        {
            Width = 4,
            Height = 1,
            Format = PixelFormat.PFID_INDEX16,
            SourceData = new byte[]
            {
                0x00, 0x00,  // index 0
                0x01, 0x00,  // index 1
                0x07, 0x00,  // index 7
                0x08, 0x00,  // index 8
            },
        };
        var palette = new Palette();
        for (int i = 0; i < 16; i++)
            palette.Colors.Add(new ColorARGB { Alpha = 0xFF, Red = 0xAA, Green = 0xBB, Blue = 0xCC });

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs, palette, isClipMap: true);

        // Pixels 0, 1, 2 (indices 0, 1, 7) should be fully transparent.
        Assert.Equal(0, decoded.Rgba8[3]);   // pixel 0 alpha
        Assert.Equal(0, decoded.Rgba8[7]);   // pixel 1 alpha
        Assert.Equal(0, decoded.Rgba8[11]);  // pixel 2 alpha
        // Pixel 3 (index 8) should have the palette alpha.
        Assert.Equal(0xFF, decoded.Rgba8[15]);
        Assert.Equal(0xAA, decoded.Rgba8[12]);
    }

    // ---- PFID_P8 tests -------------------------------------------------------

    [Fact]
    public void Decode_P8_LooksUpPaletteForEachByte()
    {
        // 2x1 surface: pixel 0 → palette index 0 (red), pixel 1 → palette index 1 (blue).
        var palette = new Palette();
        palette.Colors.Add(new ColorARGB { Alpha = 0xFF, Red = 0xFF, Green = 0x00, Blue = 0x00 }); // index 0 = red
        palette.Colors.Add(new ColorARGB { Alpha = 0xFF, Red = 0x00, Green = 0x00, Blue = 0xFF }); // index 1 = blue

        var rs = new RenderSurface
        {
            Width = 2,
            Height = 1,
            Format = PixelFormat.PFID_P8,
            SourceData = new byte[] { 0x00, 0x01 },  // indices
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs, palette);

        Assert.Equal(8, decoded.Rgba8.Length);
        // Pixel 0: red
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00, 0xFF }, decoded.Rgba8[0..4]);
        // Pixel 1: blue
        Assert.Equal(new byte[] { 0x00, 0x00, 0xFF, 0xFF }, decoded.Rgba8[4..8]);
    }

    [Fact]
    public void Decode_P8_ClipMap_ZerosAlphaForLowIndices()
    {
        // 4x1 surface with indices 0, 3, 7, 8.
        // isClipMap=true → indices 0..7 should be fully transparent; index 8 opaque.
        var palette = new Palette();
        for (int i = 0; i < 16; i++)
            palette.Colors.Add(new ColorARGB { Alpha = 0xFF, Red = 0xCC, Green = 0xDD, Blue = 0xEE });

        var rs = new RenderSurface
        {
            Width = 4,
            Height = 1,
            Format = PixelFormat.PFID_P8,
            SourceData = new byte[] { 0x00, 0x03, 0x07, 0x08 },
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs, palette, isClipMap: true);

        // Indices 0, 3, 7 should be transparent.
        Assert.Equal(0, decoded.Rgba8[3]);   // pixel 0 alpha
        Assert.Equal(0, decoded.Rgba8[7]);   // pixel 1 alpha
        Assert.Equal(0, decoded.Rgba8[11]);  // pixel 2 alpha
        Assert.Equal(0xFF, decoded.Rgba8[15]);
        Assert.Equal(0xCC, decoded.Rgba8[12]);
    }

    [Fact]
    public void Decode_P8_WithoutPalette_ReturnsMagenta()
    {
        // P8 without palette passed → falls through to magenta.
        var rs = new RenderSurface
        {
            Width = 2,
            Height = 1,
            Format = PixelFormat.PFID_P8,
            SourceData = new byte[] { 0x00, 0x01 },
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs);

        Assert.Same(DecodedTexture.Magenta, decoded);
    }

    [Fact]
    public void Decode_P8_TruncatedData_ReturnsMagenta()
    {
        var palette = new Palette();
        palette.Colors.Add(new ColorARGB { Alpha = 0xFF, Red = 0xAA, Green = 0xBB, Blue = 0xCC });

        var rs = new RenderSurface
        {
            Width = 4,
            Height = 1,
            Format = PixelFormat.PFID_P8,
            SourceData = new byte[] { 0x00, 0x00 },  // expects 4 bytes
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs, palette);

        Assert.Same(DecodedTexture.Magenta, decoded);
    }

    // ---- PFID_R8G8B8 tests ---------------------------------------------------

    [Fact]
    public void Decode_R8G8B8_ConvertsToRgba8WithOpaqueAlpha()
    {
        // PFID_R8G8B8 is stored on disk as B,G,R (little-endian 24-bit BGR).
        // 2x1 surface: first pixel = red (B=0,G=0,R=255), second = green (B=0,G=255,R=0).
        var rs = new RenderSurface
        {
            Width = 2,
            Height = 1,
            Format = PixelFormat.PFID_R8G8B8,
            SourceData = new byte[]
            {
                0x00, 0x00, 0xFF,  // B=0, G=0, R=255 → red
                0x00, 0xFF, 0x00,  // B=0, G=255, R=0 → green
            },
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs);

        Assert.Equal(8, decoded.Rgba8.Length);
        // Red pixel → R=255, G=0, B=0, A=255
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00, 0xFF }, decoded.Rgba8[0..4]);
        // Green pixel → R=0, G=255, B=0, A=255
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x00, 0xFF }, decoded.Rgba8[4..8]);
    }

    [Fact]
    public void Decode_R8G8B8_TruncatedData_ReturnsMagenta()
    {
        var rs = new RenderSurface
        {
            Width = 2,
            Height = 1,
            Format = PixelFormat.PFID_R8G8B8,
            SourceData = new byte[] { 0x00, 0x00 },  // expects 6 bytes
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs);

        Assert.Same(DecodedTexture.Magenta, decoded);
    }

    // ---- PFID_X8R8G8B8 tests -------------------------------------------------

    [Fact]
    public void Decode_X8R8G8B8_ConvertsToRgba8DiscardingXByte()
    {
        // PFID_X8R8G8B8 is stored on disk as B,G,R,X (DirectX little-endian 32-bit).
        // The X byte is unused padding — NOT alpha. Output alpha must be 255.
        // 2x1: first pixel = blue (B=255,G=0,R=0,X=0xDE), second = white (B=255,G=255,R=255,X=0xAD).
        var rs = new RenderSurface
        {
            Width = 2,
            Height = 1,
            Format = PixelFormat.PFID_X8R8G8B8,
            SourceData = new byte[]
            {
                0xFF, 0x00, 0x00, 0xDE,  // B=255, G=0, R=0, X=0xDE → blue, alpha forced 255
                0xFF, 0xFF, 0xFF, 0xAD,  // B=255, G=255, R=255, X=0xAD → white, alpha forced 255
            },
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs);

        Assert.Equal(8, decoded.Rgba8.Length);
        // Blue pixel → R=0, G=0, B=255, A=255 (X byte discarded)
        Assert.Equal(new byte[] { 0x00, 0x00, 0xFF, 0xFF }, decoded.Rgba8[0..4]);
        // White pixel → R=255, G=255, B=255, A=255 (X byte discarded)
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, decoded.Rgba8[4..8]);
    }

    [Fact]
    public void Decode_X8R8G8B8_TruncatedData_ReturnsMagenta()
    {
        var rs = new RenderSurface
        {
            Width = 2,
            Height = 1,
            Format = PixelFormat.PFID_X8R8G8B8,
            SourceData = new byte[] { 0xFF, 0x00, 0x00, 0xDE },  // expects 8 bytes (2 pixels)
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs);

        Assert.Same(DecodedTexture.Magenta, decoded);
    }


    private static readonly byte[] TinyJpeg8x8 =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01,
        0x00, 0x01, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x84, 0x00, 0x03, 0x02, 0x02, 0x03, 0x02, 0x02, 0x03,
        0x03, 0x03, 0x03, 0x04, 0x03, 0x03, 0x04, 0x05, 0x08, 0x05, 0x05, 0x04, 0x04, 0x05, 0x0A, 0x07,
        0x07, 0x06, 0x08, 0x0C, 0x0A, 0x0C, 0x0C, 0x0B, 0x0A, 0x0B, 0x0B, 0x0D, 0x0E, 0x12, 0x10, 0x0D,
        0x0E, 0x11, 0x0E, 0x0B, 0x0B, 0x10, 0x16, 0x10, 0x11, 0x13, 0x14, 0x15, 0x15, 0x15, 0x0C, 0x0F,
        0x17, 0x18, 0x16, 0x14, 0x18, 0x12, 0x14, 0x15, 0x14, 0x01, 0x03, 0x04, 0x04, 0x05, 0x04, 0x05,
        0x09, 0x05, 0x05, 0x09, 0x14, 0x0D, 0x0B, 0x0D, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14,
        0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14,
        0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14,
        0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00,
        0x08, 0x00, 0x08, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, 0xFF, 0xC4, 0x01,
        0xA2, 0x00, 0x00, 0x01, 0x05, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x10, 0x00,
        0x02, 0x01, 0x03, 0x03, 0x02, 0x04, 0x03, 0x05, 0x05, 0x04, 0x04, 0x00, 0x00, 0x01, 0x7D, 0x01,
        0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22,
        0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0, 0x24,
        0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28, 0x29,
        0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A,
        0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A,
        0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A,
        0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8,
        0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6,
        0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2, 0xE3,
        0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9,
        0xFA, 0x01, 0x00, 0x03, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x11, 0x00,
        0x02, 0x01, 0x02, 0x04, 0x04, 0x03, 0x04, 0x07, 0x05, 0x04, 0x04, 0x00, 0x01, 0x02, 0x77, 0x00,
        0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13,
        0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0, 0x15,
        0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26, 0x27,
        0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88,
        0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6,
        0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4,
        0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE2,
        0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9,
        0xFA, 0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00, 0xF9,
        0x37, 0x59, 0xD6, 0x7F, 0xB5, 0xFC, 0x9F, 0xDC, 0xF9, 0x5E, 0x5E, 0x7F, 0x8B, 0x76, 0x73, 0x8F,
        0x6F, 0x6A, 0xCD, 0xA2, 0x8A, 0xFE, 0xE5, 0xCB, 0x32, 0xCC, 0x26, 0x4F, 0x84, 0x86, 0x07, 0x03,
        0x0E, 0x4A, 0x50, 0xBD, 0x95, 0xDB, 0xB5, 0xDB, 0x6F, 0x56, 0xDB, 0xDD, 0xB7, 0xAB, 0x3E, 0x3F,
        0x31, 0xCC, 0x71, 0x59, 0xB6, 0x2A, 0x78, 0xDC, 0x6C, 0xF9, 0xAA, 0x4A, 0xD7, 0x76, 0x4A, 0xF6,
        0x49, 0x2D, 0x12, 0x4B, 0x64, 0xBA, 0x1F, 0xFF, 0xD9,
    ];

    [Fact]
    public void Decode_CustomRawJpeg_DecodesRealPixels()
    {
        var rs = new RenderSurface
        {
            Width = 0,
            Height = 0,
            Format = PixelFormat.PFID_CUSTOM_RAW_JPEG,
            SourceData = TinyJpeg8x8,
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs);

        Assert.NotSame(DecodedTexture.Magenta, decoded);
        Assert.Equal(8, decoded.Width);
        Assert.Equal(8, decoded.Height);
        Assert.Equal(8 * 8 * 4, decoded.Rgba8.Length);

        // Top-left quadrant was authored ~RGB(200,30,40); bottom-right ~RGB(20,40,220).
        // JPEG is lossy, so assert within a generous tolerance rather than exact bytes.
        int topLeft = (1 * decoded.Width + 1) * 4;
        Assert.InRange(decoded.Rgba8[topLeft + 0], 170, 230);   // R
        Assert.InRange(decoded.Rgba8[topLeft + 2], 10, 70);     // B
        Assert.Equal(0xFF, decoded.Rgba8[topLeft + 3]);

        int bottomRight = (6 * decoded.Width + 6) * 4;
        Assert.InRange(decoded.Rgba8[bottomRight + 0], 0, 60);     // R
        Assert.InRange(decoded.Rgba8[bottomRight + 2], 190, 255);  // B
        Assert.Equal(0xFF, decoded.Rgba8[bottomRight + 3]);
    }

    [Fact]
    public void Decode_CustomRawJpeg_CorruptData_ReturnsMagenta()
    {
        var rs = new RenderSurface
        {
            Width = 0,
            Height = 0,
            Format = PixelFormat.PFID_CUSTOM_RAW_JPEG,
            SourceData = [0x01, 0x02, 0x03, 0x04],  // not a JPEG stream at all
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs);

        Assert.Same(DecodedTexture.Magenta, decoded);
    }

    [Fact]
    public void Decode_CustomRawJpeg_NullSourceData_ReturnsMagenta()
    {
        var rs = new RenderSurface
        {
            Width = 0,
            Height = 0,
            Format = PixelFormat.PFID_CUSTOM_RAW_JPEG,
            SourceData = null!,
        };

        var decoded = SurfaceDecoder.DecodeRenderSurface(rs);

        Assert.Same(DecodedTexture.Magenta, decoded);
    }
}
