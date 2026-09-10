using System;
using System.IO;
using AcDream.App.Rendering.Gpu;
using StbTrueTypeSharp;

namespace AcDream.App.Rendering;

public sealed unsafe class BitmapFont : IDisposable
{
    public readonly struct Glyph
    {
        public readonly float UvMinX;
        public readonly float UvMinY;
        public readonly float UvMaxX;
        public readonly float UvMaxY;
        public readonly float OffsetX;
        public readonly float OffsetY;
        public readonly float Width;     // pixels
        public readonly float Height;
        public readonly float Advance;

        public Glyph(float umn, float vmn, float umx, float vmx,
                     float ox, float oy, float w, float h, float adv)
        {
            UvMinX = umn; UvMinY = vmn; UvMaxX = umx; UvMaxY = vmx;
            OffsetX = ox; OffsetY = oy; Width = w; Height = h; Advance = adv;
        }
    }

    private readonly Glyph[] _glyphs;
    private readonly int _firstChar;
    private readonly int _numChars;
    private readonly IGpuTexture _texture;

    public uint TextureId { get; }
    public float PixelHeight { get; }
    public float LineHeight { get; }
    public float Ascent { get; }
    public int AtlasWidth { get; }
    public int AtlasHeight { get; }

    internal BitmapFont(IGpuDevice device, byte[] ttfBytes, float pixelHeight,
        int atlasSize = 512, int firstChar = 32, int numChars = 96)
    {
        ArgumentNullException.ThrowIfNull(device);
        PixelHeight = pixelHeight;
        AtlasWidth = atlasSize;
        AtlasHeight = atlasSize;
        _firstChar = firstChar;
        _numChars = numChars;

        // Bake the glyph bitmap via stbtt_BakeFontBitmap.
        var bakedChars = new StbTrueType.stbtt_bakedchar[numChars];
        var pixels = new byte[AtlasWidth * AtlasHeight];
        bool ok = StbTrueType.stbtt_BakeFontBitmap(
            ttfBytes, 0, pixelHeight,
            pixels, AtlasWidth, AtlasHeight,
            firstChar, numChars, bakedChars);
        if (!ok)
            throw new InvalidOperationException(
                $"stbtt_BakeFontBitmap failed: atlas {atlasSize}x{atlasSize} " +
                $"too small for pixelHeight={pixelHeight}");

        // Extract vertical metrics for line spacing.
        using var info = StbTrueType.CreateFont(ttfBytes, 0)
            ?? throw new InvalidOperationException("stbtt_InitFont failed");
        float scale = StbTrueType.stbtt_ScaleForPixelHeight(info, pixelHeight);
        int ascent, descent, lineGap;
        StbTrueType.stbtt_GetFontVMetrics(info, &ascent, &descent, &lineGap);
        Ascent = ascent * scale;
        LineHeight = (ascent - descent + lineGap) * scale;

        _glyphs = new Glyph[numChars];
        for (int i = 0; i < numChars; i++)
        {
            var bc = bakedChars[i];
            float w = bc.x1 - bc.x0;
            float h = bc.y1 - bc.y0;
            _glyphs[i] = new Glyph(
                umn: bc.x0 / (float)AtlasWidth,
                vmn: bc.y0 / (float)AtlasHeight,
                umx: bc.x1 / (float)AtlasWidth,
                vmx: bc.y1 / (float)AtlasHeight,
                ox: bc.xoff,
                oy: bc.yoff,
                w: w, h: h,
                adv: bc.xadvance);
        }

        IGpuTexture texture = device.CreateTexture(new GpuTextureDescription(
            "bitmap-font-atlas",
            GpuTextureKind.Texture2D,
            GpuTextureFormat.R8Unorm,
            Width: AtlasWidth,
            Height: AtlasHeight,
            LayerCount: 1,
            MipLevelCount: 1));
        GpuTextureSlot slot;
        try
        {
            fixed (byte* ptr = pixels)
                texture.Upload(0, 0, new ReadOnlySpan<byte>(ptr, AtlasWidth * AtlasHeight));

            IGpuSampler sampler = device.CreateSampler(GpuSamplerDescription.WorldClamp);
            slot = device.RegisterTexture(texture, sampler);
        }
        catch
        {
            texture.Dispose();
            throw;
        }

        _texture = texture;
        TextureId = UiTextureTableHandle.FromSlot(slot);
    }

    public bool TryGetGlyph(char c, out Glyph g)
    {
        int idx = c - _firstChar;
        if ((uint)idx >= (uint)_numChars)
        {
            g = default;
            return false;
        }
        g = _glyphs[idx];
        return true;
    }

    /// <summary>Measure the pixel width of a single-line string in this font.</summary>
    public float MeasureWidth(string s)
    {
        float w = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (TryGetGlyph(s[i], out var g))
                w += g.Advance;
        }
        return w;
    }

    public void Dispose()
    {
        _texture.Dispose();
    }

    public static byte[]? TryLoadSystemMonospaceFont()
    {
        string[] candidates =
        {
            @"C:\Windows\Fonts\consola.ttf",
            @"C:\Windows\Fonts\cour.ttf",
            @"C:\Windows\Fonts\arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
            "/usr/share/fonts/TTF/DejaVuSansMono.ttf",
            // The renderer consumes a single face at offset zero. Font
            // collections need an explicit face offset and are not candidates.
            "/System/Library/Fonts/SFNSMono.ttf",
            "/System/Library/Fonts/Monaco.ttf",
        };
        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                byte[] bytes = File.ReadAllBytes(path);
                using StbTrueType.stbtt_fontinfo? font =
                    StbTrueType.CreateFont(bytes, 0);
                if (font is not null)
                    return bytes;
            }
            catch
            {
            }
        }
        return null;
    }
}
