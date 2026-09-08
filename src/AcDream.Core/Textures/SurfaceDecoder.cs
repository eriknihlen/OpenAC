using System.Collections.Concurrent;
using AcDream.Core.Rendering.Wb;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using StbImageSharp;

namespace AcDream.Core.Textures;

public static class SurfaceDecoder
{
    private static readonly BcDecoder BcDecoder = new();

    private static readonly ConcurrentDictionary<uint, byte> LoggedMagentaIds = new();

    private static DecodedTexture LogMagentaOnce(RenderSurface rs, string reason)
    {
        if (LoggedMagentaIds.TryAdd(rs.Id, 0))
        {
            Console.WriteLine(
                $"[UI] SurfaceDecoder: RenderSurface 0x{rs.Id:X8} decoded to the 1x1 "
                + $"magenta placeholder ({reason}; format={rs.Format} "
                + $"{rs.Width}x{rs.Height}).");
        }
        return DecodedTexture.Magenta;
    }

    public static DecodedTexture DecodeRenderSurface(RenderSurface rs)
        => DecodeRenderSurface(rs, palette: null, isClipMap: false, isAdditive: false);

    public static DecodedTexture DecodeRenderSurface(RenderSurface rs, Palette? palette, bool isClipMap = false, bool isAdditive = false)
    {
        if (rs.SourceData is null)
            return LogMagentaOnce(rs, "null SourceData");

        if (rs.Format == PixelFormat.PFID_CUSTOM_RAW_JPEG)
        {
            try
            {
                return DecodeCustomRawJpeg(rs);
            }
            catch (Exception ex)
            {
                return LogMagentaOnce(rs, $"JPEG decode failed: {ex.Message}");
            }
        }

        if (rs.Width <= 0 || rs.Height <= 0)
            return LogMagentaOnce(rs, "non-positive Width/Height");

        try
        {
            return rs.Format switch
            {
                PixelFormat.PFID_R8G8B8 => DecodeR8G8B8(rs),
                PixelFormat.PFID_A8R8G8B8 => DecodeA8R8G8B8(rs),
                PixelFormat.PFID_X8R8G8B8 => DecodeX8R8G8B8(rs),
                PixelFormat.PFID_DXT1 => DecodeBc(rs, CompressionFormat.Bc1, isClipMap),
                PixelFormat.PFID_DXT3 => DecodeBc(rs, CompressionFormat.Bc2, isClipMap),
                PixelFormat.PFID_DXT5 => DecodeBc(rs, CompressionFormat.Bc3, isClipMap),
                PixelFormat.PFID_A8 or PixelFormat.PFID_CUSTOM_LSCAPE_ALPHA => DecodeA8(rs, isAdditive),
                PixelFormat.PFID_P8 when palette is not null => DecodeP8(rs, palette, isClipMap),
                PixelFormat.PFID_P8 => LogMagentaOnce(rs, "PFID_P8 with no palette"),
                PixelFormat.PFID_INDEX16 when palette is not null => DecodeIndex16(rs, palette, isClipMap),
                PixelFormat.PFID_INDEX16 => LogMagentaOnce(rs, "PFID_INDEX16 with no palette"),
                PixelFormat.PFID_R5G6B5 => DecodeR5G6B5(rs),
                PixelFormat.PFID_A4R4G4B4 => DecodeA4R4G4B4(rs),
                _ => LogMagentaOnce(rs, $"unsupported PixelFormat {rs.Format}"),
            };
        }
        catch (Exception ex)
        {
            return LogMagentaOnce(rs, $"decode threw: {ex.Message}");
        }
    }

    private static DecodedTexture DecodeCustomRawJpeg(RenderSurface rs)
    {
        ImageResult image = ImageResult.FromMemory(rs.SourceData!, ColorComponents.RedGreenBlueAlpha);
        if (image.Width <= 0 || image.Height <= 0)
            throw new InvalidDataException(
                $"JPEG surface 0x{rs.Id:X8} decoded to {image.Width}x{image.Height}.");
        return new DecodedTexture(image.Data, image.Width, image.Height);
    }

    private static DecodedTexture DecodeIndex16(RenderSurface rs, Palette palette, bool isClipMap)
    {
        int expectedBytes = rs.Width * rs.Height * 2;
        if (rs.SourceData.Length < expectedBytes || palette.Colors.Count == 0)
            return DecodedTexture.Magenta;

        var rgba = new byte[rs.Width * rs.Height * 4];
        TextureHelpers.FillIndex16(rs.SourceData, palette, rgba.AsSpan(), rs.Width, rs.Height, isClipMap);
        return new DecodedTexture(rgba, rs.Width, rs.Height);
    }

    public static DecodedTexture DecodeSolidColor(DatReaderWriter.Types.ColorARGB color, float translucency)
    {
        if (color is null) return DecodedTexture.Magenta;
        float opacity = Math.Clamp(1f - translucency, 0f, 1f);
        byte alpha = (byte)Math.Clamp(color.Alpha * opacity, 0f, 255f);
        return new DecodedTexture(
            Rgba8: [color.Red, color.Green, color.Blue, alpha],
            Width: 1,
            Height: 1);
    }

    public static DecodedTexture ApplyAuthoredTranslucency(DecodedTexture texture, float translucency)
    {
        if (translucency <= 0f) return texture;
        float alphaScale = Math.Clamp(1f - translucency, 0f, 1f);
        byte[] rgba = texture.Rgba8;
        for (int i = 3; i < rgba.Length; i += 4)
            rgba[i] = (byte)(rgba[i] * alphaScale);
        return texture;
    }

    /// <summary>
    /// Decode single-byte-per-pixel alpha (PFID_A8 / PFID_CUSTOM_LSCAPE_ALPHA) into RGBA8.
    /// When <paramref name="isAdditive"/> is true: R=G=B=A=val (terrain alpha masks and
    /// additive entity textures — the shader reads .r for the blend weight). When false:
    /// R=G=B=255, A=val (WB FillA8 semantics for non-additive entity textures).
    /// </summary>
    private static DecodedTexture DecodeA8(RenderSurface rs, bool isAdditive)
    {
        int expected = rs.Width * rs.Height;
        if (rs.SourceData.Length < expected)
            return DecodedTexture.Magenta;

        var rgba = new byte[expected * 4];
        if (isAdditive)
            TextureHelpers.FillA8Additive(rs.SourceData, rgba.AsSpan(), rs.Width, rs.Height);
        else
            TextureHelpers.FillA8(rs.SourceData, rgba.AsSpan(), rs.Width, rs.Height);
        return new DecodedTexture(rgba, rs.Width, rs.Height);
    }

    private static DecodedTexture DecodeA8R8G8B8(RenderSurface rs)
    {
        int expected = rs.Width * rs.Height * 4;
        if (rs.SourceData.Length < expected)
            return DecodedTexture.Magenta;

        var rgba = new byte[expected];
        TextureHelpers.FillA8R8G8B8(rs.SourceData, rgba.AsSpan(), rs.Width, rs.Height);
        return new DecodedTexture(rgba, rs.Width, rs.Height);
    }

    private static DecodedTexture DecodeP8(RenderSurface rs, Palette palette, bool isClipMap)
    {
        int expectedBytes = rs.Width * rs.Height;
        if (rs.SourceData.Length < expectedBytes || palette.Colors.Count == 0)
            return DecodedTexture.Magenta;

        var rgba = new byte[rs.Width * rs.Height * 4];
        TextureHelpers.FillP8(rs.SourceData, palette, rgba.AsSpan(), rs.Width, rs.Height, isClipMap);
        return new DecodedTexture(rgba, rs.Width, rs.Height);
    }

    private static DecodedTexture DecodeR8G8B8(RenderSurface rs)
    {
        int expectedBytes = rs.Width * rs.Height * 3;
        if (rs.SourceData.Length < expectedBytes)
            return DecodedTexture.Magenta;

        var rgba = new byte[rs.Width * rs.Height * 4];
        TextureHelpers.FillR8G8B8(rs.SourceData, rgba.AsSpan(), rs.Width, rs.Height);
        return new DecodedTexture(rgba, rs.Width, rs.Height);
    }

    private static DecodedTexture DecodeX8R8G8B8(RenderSurface rs)
    {
        int expectedBytes = rs.Width * rs.Height * 4;
        if (rs.SourceData.Length < expectedBytes)
            return DecodedTexture.Magenta;

        var rgba = new byte[expectedBytes];
        for (int i = 0; i < rs.Width * rs.Height; i++)
        {
            int s = i * 4;
            // On-disk byte order: B, G, R, X (little-endian 32-bit; high byte X is padding)
            rgba[s + 0] = rs.SourceData[s + 2];  // R
            rgba[s + 1] = rs.SourceData[s + 1];  // G
            rgba[s + 2] = rs.SourceData[s + 0];  // B
            rgba[s + 3] = 0xFF;                  // A = opaque (X byte discarded)
        }
        return new DecodedTexture(rgba, rs.Width, rs.Height);
    }

    private static DecodedTexture DecodeR5G6B5(RenderSurface rs)
    {
        int expectedBytes = rs.Width * rs.Height * 2;
        if (rs.SourceData.Length < expectedBytes)
            return DecodedTexture.Magenta;

        var rgba = new byte[rs.Width * rs.Height * 4];
        TextureHelpers.FillR5G6B5(rs.SourceData, rgba.AsSpan(), rs.Width, rs.Height);
        return new DecodedTexture(rgba, rs.Width, rs.Height);
    }

    private static DecodedTexture DecodeA4R4G4B4(RenderSurface rs)
    {
        int expectedBytes = rs.Width * rs.Height * 2;
        if (rs.SourceData.Length < expectedBytes)
            return DecodedTexture.Magenta;

        var rgba = new byte[rs.Width * rs.Height * 4];
        TextureHelpers.FillA4R4G4B4(rs.SourceData, rgba.AsSpan(), rs.Width, rs.Height);
        return new DecodedTexture(rgba, rs.Width, rs.Height);
    }

    private static DecodedTexture DecodeBc(RenderSurface rs, CompressionFormat format, bool isClipMap)
    {
        var pixels = BcDecoder.DecodeRaw(rs.SourceData, rs.Width, rs.Height, format);
        var rgba = new byte[rs.Width * rs.Height * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            int s = i * 4;
            rgba[s + 0] = pixels[i].r;
            rgba[s + 1] = pixels[i].g;
            rgba[s + 2] = pixels[i].b;
            rgba[s + 3] = pixels[i].a;
            if (isClipMap && rgba[s + 0] == 0 && rgba[s + 1] == 0 && rgba[s + 2] == 0)
                rgba[s + 3] = 0;
        }
        return new DecodedTexture(rgba, rs.Width, rs.Height);
    }
}
