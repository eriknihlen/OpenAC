using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.UI;

public sealed class UiRenderContextDrawStringDatOutlineTests
{
    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private const uint ForegroundTex = 1u;
    private const uint BackgroundTex = 2u;

    private static (TextRenderer renderer, UiRenderContext ctx) Build()
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        var ctx = new UiRenderContext(renderer, new Vector2(800f, 600f));
        return (renderer, ctx);
    }

    private static FontCharDesc Glyph(
        char c, ushort offsetX, ushort offsetY, byte width, byte height,
        sbyte before = 0, sbyte after = 0, sbyte vBefore = 0)
        => new()
        {
            Unicode = c,
            Width = width,
            Height = height,
            OffsetX = offsetX,
            OffsetY = offsetY,
            HorizontalOffsetBefore = before,
            HorizontalOffsetAfter = after,
            VerticalOffsetBefore = vBefore,
        };

    private readonly record struct Quad(
        float X, float Y, float W, float H,
        float U0, float V0, float U1, float V1,
        float R, float G, float B, float A);

    private static List<Quad> DecodeQuads(IReadOnlyList<float> verts)
    {
        var quads = new List<Quad>();
        const int floatsPerVertex = 8;
        const int floatsPerQuad = floatsPerVertex * 6;
        for (int i = 0; i + floatsPerQuad <= verts.Count; i += floatsPerQuad)
        {
            float x0 = verts[i + 0], y0 = verts[i + 1], u0 = verts[i + 2], v0 = verts[i + 3];
            float r = verts[i + 4], g = verts[i + 5], b = verts[i + 6], a = verts[i + 7];
            // Second vertex (index 1) is (x+w, y+h, u1, v1) per AppendQuad's V0..V5 order.
            float x1 = verts[i + 8 + 0], y1 = verts[i + 8 + 1], u1 = verts[i + 8 + 2], v1 = verts[i + 8 + 3];
            quads.Add(new Quad(x0, y0, x1 - x0, y1 - y0, u0, v0, u1, v1, r, g, b, a));
        }
        return quads;
    }

    // ── Two-pass ordering ────────────────────────────────────────────────────

    [Fact]
    public void Outline_TwoGlyphString_EmitsOneWholeStringOutlineSegmentThenOneWholeStringFillSegment()
    {
        (TextRenderer renderer, UiRenderContext ctx) = Build();
        var font = new UiDatFont(
            fgTex: ForegroundTex, fgW: 64, fgH: 64,
            bgTex: BackgroundTex, bgW: 64, bgH: 64,
            lineHeight: 16f, baselineOffset: 12f,
            glyphs: new Dictionary<char, FontCharDesc>
            {
                ['A'] = Glyph('A', offsetX: 4, offsetY: 4, width: 8, height: 8),
                ['B'] = Glyph('B', offsetX: 4, offsetY: 4, width: 8, height: 8, before: 1),
            },
            borderX: 4, borderY: 4);

        ctx.DrawStringDat(font, "AB", 0, 0, new Vector4(1f, 1f, 1f, 1f), outline: true);

        var segs = renderer.DebugSpriteSegments;
        Assert.Equal(2, segs.Count);
        Assert.Equal(BackgroundTex, segs[0].Texture);
        Assert.Equal(12, segs[0].VertexCount);   // 2 glyphs × 6 verts, ONE segment
        Assert.Equal(ForegroundTex, segs[1].Texture);
        Assert.Equal(12, segs[1].VertexCount);
    }

    [Fact]
    public void NoOutline_OnlyOneFillPassRuns()
    {
        (TextRenderer renderer, UiRenderContext ctx) = Build();
        var font = new UiDatFont(
            fgTex: ForegroundTex, fgW: 64, fgH: 64,
            bgTex: BackgroundTex, bgW: 64, bgH: 64,
            lineHeight: 16f, baselineOffset: 12f,
            glyphs: new Dictionary<char, FontCharDesc>
            {
                ['A'] = Glyph('A', offsetX: 4, offsetY: 4, width: 8, height: 8),
            },
            borderX: 4, borderY: 4);

        ctx.DrawStringDat(font, "A", 0, 0, new Vector4(1f, 1f, 1f, 1f), outline: false);

        var seg = Assert.Single(renderer.DebugSpriteSegments);
        Assert.Equal(ForegroundTex, seg.Texture);
    }

    // ── Tint ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Outline_UsesOutlineColor_NotFillColor()
    {
        (TextRenderer renderer, UiRenderContext ctx) = Build();
        var font = new UiDatFont(
            fgTex: ForegroundTex, fgW: 64, fgH: 64,
            bgTex: BackgroundTex, bgW: 64, bgH: 64,
            lineHeight: 16f, baselineOffset: 12f,
            glyphs: new Dictionary<char, FontCharDesc>
            {
                ['A'] = Glyph('A', offsetX: 4, offsetY: 4, width: 8, height: 8),
            },
            borderX: 4, borderY: 4);

        var fill = new Vector4(1f, 1f, 0.247f, 1f);      // SpewBox-style gold/yellow fill
        var outlineColor = new Vector4(0.2f, 0.4f, 0.6f, 1f); // an arbitrary non-black outline

        ctx.DrawStringDat(font, "A", 0, 0, fill, outline: true, outlineColor: outlineColor);

        var segVerts = renderer.DebugSpriteSegmentVerts;
        Assert.Equal(2, segVerts.Count);

        Quad outlineQuad = Assert.Single(DecodeQuads(segVerts[0].Verts));
        Assert.Equal(outlineColor.X, outlineQuad.R, 5);
        Assert.Equal(outlineColor.Y, outlineQuad.G, 5);
        Assert.Equal(outlineColor.Z, outlineQuad.B, 5);

        Quad fillQuad = Assert.Single(DecodeQuads(segVerts[1].Verts));
        Assert.Equal(fill.X, fillQuad.R, 5);
        Assert.Equal(fill.Y, fillQuad.G, 5);
        Assert.Equal(fill.Z, fillQuad.B, 5);
    }

    [Fact]
    public void Outline_DefaultsToBlack_WhenNoOutlineColorSupplied()
    {
        (TextRenderer renderer, UiRenderContext ctx) = Build();
        var font = new UiDatFont(
            fgTex: ForegroundTex, fgW: 64, fgH: 64,
            bgTex: BackgroundTex, bgW: 64, bgH: 64,
            lineHeight: 16f, baselineOffset: 12f,
            glyphs: new Dictionary<char, FontCharDesc>
            {
                ['A'] = Glyph('A', offsetX: 4, offsetY: 4, width: 8, height: 8),
            },
            borderX: 4, borderY: 4);

        ctx.DrawStringDat(font, "A", 0, 0, new Vector4(1f, 1f, 0.247f, 1f), outline: true);

        var segVerts = renderer.DebugSpriteSegmentVerts;
        Quad outlineQuad = Assert.Single(DecodeQuads(segVerts[0].Verts));
        Assert.Equal(0f, outlineQuad.R);
        Assert.Equal(0f, outlineQuad.G);
        Assert.Equal(0f, outlineQuad.B);
    }

    // ── Background-plane inflation (Fix 1+2) ────────────────────────────────

    [Fact]
    public void Outline_BackgroundPass_InflatesDestAndSourceRectByBorderPixels()
    {
        (TextRenderer renderer, UiRenderContext ctx) = Build();
        const int borderX = 4, borderY = 3;
        const int atlasW = 100, atlasH = 100;
        var font = new UiDatFont(
            fgTex: ForegroundTex, fgW: atlasW, fgH: atlasH,
            bgTex: BackgroundTex, bgW: atlasW, bgH: atlasH,
            lineHeight: 16f, baselineOffset: 12f,
            glyphs: new Dictionary<char, FontCharDesc>
            {
                // OffsetX/Y >= border, matching every measured real font (§1.2:
                // "the FG glyph always sits at exactly (hB, vB) inside the
                // inflated window").
                ['A'] = Glyph('A', offsetX: 10, offsetY: 8, width: 9, height: 8),
            },
            borderX: borderX, borderY: borderY);

        ctx.DrawStringDat(font, "A", 20f, 5f, new Vector4(1f, 1f, 1f, 1f), outline: true);

        var segVerts = renderer.DebugSpriteSegmentVerts;
        Quad bg = Assert.Single(DecodeQuads(segVerts[0].Verts));
        Quad fg = Assert.Single(DecodeQuads(segVerts[1].Verts));

        // Foreground (fill) quad is UN-inflated — the glyph's own box.
        Assert.Equal(9f, fg.W, 3);
        Assert.Equal(8f, fg.H, 3);
        Assert.Equal(10f / atlasW, fg.U0, 5);
        Assert.Equal(8f / atlasH, fg.V0, 5);
        Assert.Equal(19f / atlasW, fg.U1, 5);   // (offsetX + width) / atlasW
        Assert.Equal(16f / atlasH, fg.V1, 5);   // (offsetY + height) / atlasH

        Assert.Equal(fg.X - borderX, bg.X, 3);
        Assert.Equal(fg.Y - borderY, bg.Y, 3);
        Assert.Equal(fg.W + 2 * borderX, bg.W, 3);
        Assert.Equal(fg.H + 2 * borderY, bg.H, 3);
        Assert.Equal((10 - borderX) / (float)atlasW, bg.U0, 5);
        Assert.Equal((8 - borderY) / (float)atlasH, bg.V0, 5);
        Assert.Equal((10 - borderX + 9 + 2 * borderX) / (float)atlasW, bg.U1, 5);
        Assert.Equal((8 - borderY + 8 + 2 * borderY) / (float)atlasH, bg.V1, 5);
    }

    [Fact]
    public void Outline_ZeroBorderFont_BackgroundPassMatchesForegroundRectExactly()
    {
        (TextRenderer renderer, UiRenderContext ctx) = Build();
        var font = new UiDatFont(
            fgTex: ForegroundTex, fgW: 64, fgH: 64,
            bgTex: BackgroundTex, bgW: 64, bgH: 64,
            lineHeight: 16f, baselineOffset: 12f,
            glyphs: new Dictionary<char, FontCharDesc>
            {
                ['A'] = Glyph('A', offsetX: 4, offsetY: 4, width: 8, height: 8),
            },
            borderX: 0, borderY: 0);

        ctx.DrawStringDat(font, "A", 0, 0, new Vector4(1f, 1f, 1f, 1f), outline: true);

        var segVerts = renderer.DebugSpriteSegmentVerts;
        Quad bg = Assert.Single(DecodeQuads(segVerts[0].Verts));
        Quad fg = Assert.Single(DecodeQuads(segVerts[1].Verts));

        Assert.Equal(fg.X, bg.X, 3);
        Assert.Equal(fg.Y, bg.Y, 3);
        Assert.Equal(fg.W, bg.W, 3);
        Assert.Equal(fg.H, bg.H, 3);
        Assert.Equal(fg.U0, bg.U0, 5);
        Assert.Equal(fg.U1, bg.U1, 5);
    }

    // ── 8-neighbour fallback (no background atlas) ──────────────────────────

    [Fact]
    public void Outline_FontWithNoBackgroundAtlas_FallsBackToEightNeighbourForegroundBlits()
    {
        (TextRenderer renderer, UiRenderContext ctx) = Build();
        var font = new UiDatFont(
            fgTex: ForegroundTex, fgW: 64, fgH: 64,
            bgTex: 0, bgW: 0, bgH: 0,
            lineHeight: 16f, baselineOffset: 12f,
            glyphs: new Dictionary<char, FontCharDesc>
            {
                ['A'] = Glyph('A', offsetX: 4, offsetY: 4, width: 8, height: 8),
            },
            borderX: 0, borderY: 0);

        ctx.DrawStringDat(font, "A", 20f, 10f, new Vector4(1f, 1f, 1f, 1f), outline: true);

        var seg = Assert.Single(renderer.DebugSpriteSegments);
        Assert.Equal(ForegroundTex, seg.Texture);
        Assert.Equal(9, seg.VertexCount / 6);   // 8 neighbour outline blits + 1 fill blit

        var quads = DecodeQuads(renderer.DebugSpriteSegmentVerts[0].Verts);
        Assert.Equal(9, quads.Count);

        var seen = new HashSet<(int dx, int dy)>();
        foreach (Quad q in quads)
        {
            int dx = (int)MathF.Round(q.X - 20f);
            int dy = (int)MathF.Round(q.Y - 10f);
            Assert.InRange(dx, -1, 1);
            Assert.InRange(dy, -1, 1);
            Assert.True(seen.Add((dx, dy)), $"duplicate blit at offset ({dx},{dy})");
        }
        Assert.Equal(9, seen.Count);   // all 9 positions in the 3x3 block, each exactly once
    }
}
