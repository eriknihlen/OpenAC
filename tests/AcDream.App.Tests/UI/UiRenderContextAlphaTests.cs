using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.UI;

public sealed class UiRenderContextAlphaTests
{
    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private static (TextRenderer renderer, UiRenderContext ctx) Build()
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        var ctx = new UiRenderContext(renderer, new Vector2(800f, 600f));
        return (renderer, ctx);
    }

    private static UiDatFont BuildFont() => new(
        fgTex: 1, fgW: 64, fgH: 64,
        bgTex: 0, bgW: 0, bgH: 0,
        lineHeight: 16f, baselineOffset: 12f,
        glyphs: new Dictionary<char, FontCharDesc>
        {
            ['A'] = new FontCharDesc
            {
                Unicode = 'A',
                Width = 8,
                Height = 16,
                OffsetX = 0,
                OffsetY = 0,
                HorizontalOffsetBefore = 0,
                HorizontalOffsetAfter = 0,
                VerticalOffsetBefore = 0,
            },
        });

    private static UiDatFont BuildOutlinedFont() => new(
        fgTex: 1, fgW: 64, fgH: 64,
        bgTex: 2, bgW: 64, bgH: 64,
        lineHeight: 16f, baselineOffset: 12f,
        glyphs: new Dictionary<char, FontCharDesc>
        {
            ['A'] = new FontCharDesc
            {
                Unicode = 'A',
                Width = 8,
                Height = 16,
                OffsetX = 0,
                OffsetY = 0,
                HorizontalOffsetBefore = 0,
                HorizontalOffsetAfter = 0,
                VerticalOffsetBefore = 0,
            },
        });

    private static BitmapFont BuildBitmapFontOrSkip(IGpuDevice device)
    {
        byte[]? ttf = BitmapFont.TryLoadSystemMonospaceFont();
        if (ttf is null)
            throw new InvalidOperationException(
                "Lane=SystemFont requires a host system TTF font; see docs/release-gate.md.");
        return new BitmapFont(device, ttf, pixelHeight: 16f);
    }

    // -- DrawSprite: full-opacity identity ---------------------------------

    [Fact]
    public void FullOpacity_DrawSprite_MatchesRequestedAlpha_Identity()
    {
        (TextRenderer renderer, UiRenderContext ctx) = Build();
        Assert.Equal(1f, ctx.AlphaMod);

        ctx.DrawSprite(7u, 0, 0, 10, 10, 0, 0, 1, 1, new Vector4(1f, 1f, 1f, 1f));

        var seg = Assert.Single(renderer.DebugSpriteSegments);
        Assert.Equal(7u, seg.Texture);
        Assert.Equal(1f, seg.Alpha);
    }

    [Fact]
    public void HalfOpacityWindow_MultipliesEverySpriteEmission()
    {
        (TextRenderer renderer, UiRenderContext ctx) = Build();

        ctx.PushAlpha(0.5f);
        Assert.Equal(0.5f, ctx.AlphaMod);
        ctx.DrawSprite(7u, 0, 0, 10, 10, 0, 0, 1, 1, new Vector4(1f, 1f, 1f, 1f));
        ctx.PopAlpha();

        var seg = Assert.Single(renderer.DebugSpriteSegments);
        Assert.Equal(0.5f, seg.Alpha);

        // Pop restores full opacity for whatever draws next.
        Assert.Equal(1f, ctx.AlphaMod);
    }

    [Fact]
    public void NestedPushAlpha_ComposesMultiplicatively()
    {
        (TextRenderer renderer, UiRenderContext ctx) = Build();

        ctx.PushAlpha(0.5f);
        ctx.PushAlpha(0.4f);
        Assert.Equal(0.2f, ctx.AlphaMod, 5);
        ctx.DrawSprite(7u, 0, 0, 10, 10, 0, 0, 1, 1, new Vector4(1f, 1f, 1f, 1f));
        ctx.PopAlpha();
        ctx.PopAlpha();

        var seg = Assert.Single(renderer.DebugSpriteSegments);
        Assert.Equal(0.2f, seg.Alpha, 5);
    }

    [Fact]
    public void NestedPushAlpha_MultipliesAgainstAnAlreadyTintedColor()
    {
        (TextRenderer renderer, UiRenderContext ctx) = Build();

        ctx.PushAlpha(0.5f);
        ctx.DrawSprite(7u, 0, 0, 10, 10, 0, 0, 1, 1, new Vector4(1f, 1f, 1f, 0.8f));
        ctx.PopAlpha();

        var seg = Assert.Single(renderer.DebugSpriteSegments);
        Assert.Equal(0.4f, seg.Alpha, 5);
    }

    [Fact]
    public void PopAlpha_WithoutMatchingPush_IsANoOp()
    {
        (TextRenderer renderer, UiRenderContext ctx) = Build();
        ctx.PopAlpha();
        Assert.Equal(1f, ctx.AlphaMod);
    }

    // -- DrawStringDat: the CH6c fix (text now respects window alpha) -----

    [Fact]
    public void FullOpacity_DrawStringDat_MatchesRequestedAlpha_Identity()
    {
        (TextRenderer renderer, UiRenderContext ctx) = Build();
        UiDatFont font = BuildFont();

        ctx.DrawStringDat(font, "A", 0, 0, new Vector4(1f, 1f, 1f, 1f));

        // bgTex == 0, so only the foreground (fill) pass draws — one segment
        // on the font's foreground texture (id 1).
        var seg = Assert.Single(renderer.DebugSpriteSegments);
        Assert.Equal(1u, seg.Texture);
        Assert.Equal(1f, seg.Alpha);
    }

    [Fact]
    public void HalfOpacityWindow_MultipliesDatFontGlyphAlpha()
    {
        (TextRenderer renderer, UiRenderContext ctx) = Build();
        UiDatFont font = BuildFont();

        ctx.PushAlpha(0.5f);
        ctx.DrawStringDat(font, "A", 0, 0, new Vector4(1f, 1f, 1f, 1f));
        ctx.PopAlpha();

        var seg = Assert.Single(renderer.DebugSpriteSegments);
        Assert.Equal(0.5f, seg.Alpha);
    }

    [Fact]
    public void HalfOpacityWindow_MultipliesDatFontOutlineAndForegroundPassAlpha()
    {
        (TextRenderer renderer, UiRenderContext ctx) = Build();
        UiDatFont font = BuildOutlinedFont();

        ctx.PushAlpha(0.5f);
        ctx.DrawStringDat(font, "A", 0, 0, new Vector4(1f, 1f, 1f, 1f), outline: true);
        ctx.PopAlpha();

        Assert.Equal(2, renderer.DebugSpriteSegments.Count);
        foreach (var seg in renderer.DebugSpriteSegments)
            Assert.Equal(0.5f, seg.Alpha);
    }


    [Fact]
    [Trait("Lane", "SystemFont")]
    public void FullOpacity_DrawString_BitmapFontPath_MatchesRequestedAlpha_Identity()
    {
        var device = new RecordingGpuDevice();
        using BitmapFont font = BuildBitmapFontOrSkip(device);
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        var ctx = new UiRenderContext(renderer, new Vector2(800f, 600f));

        ctx.DrawString("A", 0, 0, new Vector4(1f, 1f, 1f, 1f), font);

        (int vertexCount, float alpha) = renderer.DebugTextBuffer;
        Assert.True(vertexCount > 0);
        Assert.Equal(1f, alpha);
    }

    [Fact]
    [Trait("Lane", "SystemFont")]
    public void HalfOpacityWindow_MultipliesBitmapFontGlyphAlpha()
    {
        var device = new RecordingGpuDevice();
        using BitmapFont font = BuildBitmapFontOrSkip(device);
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        var ctx = new UiRenderContext(renderer, new Vector2(800f, 600f));

        ctx.PushAlpha(0.5f);
        ctx.DrawString("A", 0, 0, new Vector4(1f, 1f, 1f, 1f), font);
        ctx.PopAlpha();

        (int vertexCount, float alpha) = renderer.DebugTextBuffer;
        Assert.True(vertexCount > 0);
        Assert.Equal(0.5f, alpha);
    }
}
