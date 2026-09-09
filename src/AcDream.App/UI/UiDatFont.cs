using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.UI;

public sealed class UiDatFont
{
    public const uint DefaultFontId = 0x40000000u;

    /// <summary>Foreground (glyph pixels) GL texture handle + atlas pixel size.</summary>
    public uint ForegroundTexture { get; }
    public int ForegroundWidth { get; }
    public int ForegroundHeight { get; }

    /// <summary>Background (outline/shadow) GL texture handle + atlas pixel size.
    /// 0 when the font has no background atlas (then the outline pass is skipped).</summary>
    public uint BackgroundTexture { get; }
    public int BackgroundWidth { get; }
    public int BackgroundHeight { get; }

    public float LineHeight { get; }

    public float BaselineOffset { get; }

    public int BorderX { get; }
    public int BorderY { get; }

    private readonly Dictionary<char, FontCharDesc> _glyphs;

    internal UiDatFont(
        uint fgTex, int fgW, int fgH,
        uint bgTex, int bgW, int bgH,
        float lineHeight, float baselineOffset,
        Dictionary<char, FontCharDesc> glyphs,
        int borderX = 0, int borderY = 0)
    {
        ForegroundTexture = fgTex; ForegroundWidth = fgW; ForegroundHeight = fgH;
        BackgroundTexture = bgTex; BackgroundWidth = bgW; BackgroundHeight = bgH;
        LineHeight = lineHeight;
        BaselineOffset = baselineOffset;
        _glyphs = glyphs;
        BorderX = borderX;
        BorderY = borderY;
    }

    public bool HasBackground => BackgroundTexture != 0;

    public bool TryGetGlyph(char c, out FontCharDesc glyph) => _glyphs.TryGetValue(c, out glyph!);

    public static UiDatFont? Load(IDatReaderWriter dats, TextureCache cache, uint fontId = DefaultFontId)
    {
        ArgumentNullException.ThrowIfNull(dats);
        ArgumentNullException.ThrowIfNull(cache);

        if (!dats.TryGet<Font>(fontId, out var font) || font is null)
            return null;

        // Foreground atlas is required; without it there are no glyph pixels.
        if (font.ForegroundSurfaceDataId == 0)
            return null;

        uint fgTex = cache.GetOrUploadRenderSurface(font.ForegroundSurfaceDataId, out int fgW, out int fgH, nearest: true);

        uint bgTex = 0; int bgW = 0, bgH = 0;
        if (font.BackgroundSurfaceDataId != 0)
            bgTex = cache.GetOrUploadRenderSurface(font.BackgroundSurfaceDataId, out bgW, out bgH, nearest: true);

        var glyphs = new Dictionary<char, FontCharDesc>(font.CharDescs.Count);
        foreach (var cd in font.CharDescs)
            glyphs[(char)cd.Unicode] = cd;

        return new UiDatFont(
            fgTex, fgW, fgH,
            bgTex, bgW, bgH,
            lineHeight: font.MaxCharHeight,
            baselineOffset: font.BaselineOffset,
            glyphs,
            borderX: (int)font.NumHorizontalBorderPixels,
            borderY: (int)font.NumVerticalBorderPixels);
    }

    public float MeasureWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0f;

        float width = 0f;
        for (int index = 0; index < text.Length; index++)
        {
            if (_glyphs.TryGetValue(text[index], out FontCharDesc? glyph))
                width += GlyphAdvance(glyph);
        }

        return width;
    }

    public static float MeasureWidth(string? text, Func<char, FontCharDesc?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (string.IsNullOrEmpty(text)) return 0f;
        float w = 0f;
        for (int i = 0; i < text.Length; i++)
            if (lookup(text[i]) is { } g)
                w += GlyphAdvance(g);
        return w;
    }

    public static float GlyphAdvance(FontCharDesc g)
        => g.HorizontalOffsetBefore + g.Width + g.HorizontalOffsetAfter;
}
