using System.Linq;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.App.Tests.UI;

public sealed class UiMenuPlainStyleTests
{
    private const int FloatsPerQuad = 48; // 6 vertices/quad × 8 floats/vertex (AppendQuad).

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private static (TextRenderer renderer, UiRenderContext ctx) MakeContext(float w, float h)
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(w, h));
        var ctx = new UiRenderContext(renderer, new Vector2(w, h));
        return (renderer, ctx);
    }

    private static int QuadCount(
        System.Collections.Generic.IReadOnlyList<(uint Texture, System.Collections.Generic.IReadOnlyList<float> Verts)> segs,
        uint texture)
        => segs.Where(s => s.Texture == texture).Sum(s => s.Verts.Count) / FloatsPerQuad;

    private const uint FaceTexture = 77u;
    private const uint FontTexture = 1u;

    private static UiDatFont MakeFont()
    {
        var glyphs = new System.Collections.Generic.Dictionary<char, FontCharDesc>
        {
            ['W'] = new FontCharDesc { Unicode = 'W', Width = 8, Height = 8 },
        };
        return new UiDatFont(
            fgTex: FontTexture, fgW: 32, fgH: 32,
            bgTex: 0, bgW: 0, bgH: 0,
            lineHeight: 16f, baselineOffset: 12f,
            glyphs);
    }

    private static UiMenu MakeMenu(bool retailButtonArt) => new()
    {
        Width = 100f, Height = 20f,
        DatFont = MakeFont(),
        SpriteResolve = _ => (FaceTexture, 46, 17),
        RetailButtonArt = retailButtonArt,
        NormalSprite = 0x06004D65u,
        PressedSprite = 0x06004D66u,
        Items = new[] { new UiMenu.MenuItem("Warrior", (object?)"Warrior") },
        ButtonLabelProvider = () => "W",
    };

    [Fact]
    public void Plain_ClosedState_DrawsNoTexturedFaceQuad()
    {
        var menu = MakeMenu(retailButtonArt: false);
        var (renderer, ctx) = MakeContext(200f, 200f);

        menu.DrawSelfAndChildren(ctx);

        Assert.Equal(0, QuadCount(renderer.DebugSpriteSegmentVerts, FaceTexture));
    }

    [Fact]
    public void Plain_ClosedState_DrawsFillOutlineTextAndTriangle()
    {
        var menu = MakeMenu(retailButtonArt: false);
        var (renderer, ctx) = MakeContext(200f, 200f);

        menu.DrawSelfAndChildren(ctx);

        var segs = renderer.DebugSpriteSegmentVerts;
        // Exactly: background fill (1 quad) + 1px outline (4 sides) +
        // the ▾ triangle (4 stacked bands) = 9 untextured quads.
        Assert.Equal(9, QuadCount(segs, 0u));
        Assert.Equal(1, QuadCount(segs, FontTexture));
    }

    [Fact]
    public void Plain_ClosedState_TriangleSitsRightAligned_TextSitsAtListPadding()
    {
        var menu = MakeMenu(retailButtonArt: false);
        var (renderer, ctx) = MakeContext(200f, 200f);

        menu.DrawSelfAndChildren(ctx);

        var glyphSeg = Assert.Single(renderer.DebugSpriteSegmentVerts, s => s.Texture == FontTexture);
        Assert.Equal(UiMenu.PlainPadding, glyphSeg.Verts[0], 3);

        var untexturedSegs = renderer.DebugSpriteSegmentVerts.Where(s => s.Texture == 0u).ToList();
        Assert.Equal(2, untexturedSegs.Count); // [fill+outline], [triangle]
        var triangleSeg = untexturedSegs[^1];
        Assert.Equal(4 * FloatsPerQuad, triangleSeg.Verts.Count);

        for (int q = 0; q < 4; q++)
        {
            float rightEdgeX = triangleSeg.Verts[q * FloatsPerQuad + 8];
            Assert.True(rightEdgeX <= menu.Width - 6f + 0.01f);
            Assert.True(rightEdgeX >= menu.Width - 13f - 0.01f);
        }
    }

    [Fact]
    public void Retail_ClosedState_StillEmitsItsGoldFaceSprite()
    {
        var menu = MakeMenu(retailButtonArt: true);
        var (renderer, ctx) = MakeContext(200f, 200f);

        menu.DrawSelfAndChildren(ctx);

        Assert.True(QuadCount(renderer.DebugSpriteSegmentVerts, FaceTexture) > 0);
    }

    [Fact]
    public void RetailButtonArt_DefaultsTrue_SoNonMarkupCallersAreUnaffected()
    {
        Assert.True(new UiMenu().RetailButtonArt);
    }

    [Fact]
    public void Retail_ClosedState_DrawIsByteForByteUnchanged_RegressionGolden()
    {
        UiMenu menu = new()
        {
            Width = 100f, Height = 20f,
            DatFont = MakeFont(),
            SpriteResolve = _ => (FaceTexture, 46, 17),
            NormalSprite = 0x06004D65u,
            PressedSprite = 0x06004D66u,
            Items = new[] { new UiMenu.MenuItem("Warrior", (object?)"Warrior") },
            ButtonLabelProvider = () => "W",
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        menu.DrawSelfAndChildren(ctx);

        var segs = renderer.DebugSpriteSegmentVerts;
        Assert.Equal(3, QuadCount(segs, FaceTexture));
        Assert.Equal(1, QuadCount(segs, FontTexture));
        Assert.Equal(0, QuadCount(segs, 0u));
    }


    private const float PlainRowHeight = 18f;
    private const float PlainColumnWidth = 90f;

    private static UiMenu MakePopupMenu(
        bool retailButtonArt, int itemCount, int rowsPerColumn, bool scrollable,
        System.Action<int>? countResolveCall = null)
    {
        var items = Enumerable.Range(0, itemCount)
            .Select(i => new UiMenu.MenuItem(i == 0 ? "W" : $"row{i}", (object?)i))
            .ToArray();
        return new UiMenu
        {
            Width = 100f, Height = 20f,
            DatFont = MakeFont(),
            SpriteResolve = id =>
            {
                countResolveCall?.Invoke(1);
                return (id, 8, 8);
            },
            RetailButtonArt = retailButtonArt,
            NormalSprite = 0x06004D65u,
            PressedSprite = 0x06004D66u,
            PopupBgSprite = 0x0600124Cu,
            ItemNormalSprite = 0x0600124Eu,
            ItemHighlightSprite = 0x0600124Du,
            ScrollTrackSprite = 0x06004C5Fu,
            ScrollThumbSprite = 0x06004C63u,
            ScrollThumbTopSprite = 0x06004C60u,
            ScrollThumbBottomSprite = 0x06004C66u,
            ScrollUpSprite = 0x06004C6Cu,
            ScrollDownSprite = 0x06004C69u,
            ColumnWidth = PlainColumnWidth,
            RowHeight = PlainRowHeight,
            RowsPerColumn = rowsPerColumn,
            Scrollable = scrollable,
            OpenUpward = false,   // downward: PopupTop == Height, simplest math for these tests
            Items = items,
            ButtonLabelProvider = () => "W",
        };
    }

    private static bool HasFillQuad(
        System.Collections.Generic.IReadOnlyList<(uint Texture, System.Collections.Generic.IReadOnlyList<float> Verts)> segs,
        float x, float y, float w, float h, Vector4 color, float tol = 0.05f)
    {
        foreach (var seg in segs)
        {
            if (seg.Texture != 0u) continue;
            var v = seg.Verts;
            for (int b = 0; b + FloatsPerQuad <= v.Count; b += FloatsPerQuad)
            {
                float qx = v[b], qy = v[b + 1];
                float qw = v[b + 8] - qx, qh = v[b + 9] - qy;
                float r = v[b + 4], g = v[b + 5], bl = v[b + 6], a = v[b + 7];
                if (MathF.Abs(qx - x) < tol && MathF.Abs(qy - y) < tol
                    && MathF.Abs(qw - w) < tol && MathF.Abs(qh - h) < tol
                    && MathF.Abs(r - color.X) < tol && MathF.Abs(g - color.Y) < tol
                    && MathF.Abs(bl - color.Z) < tol && MathF.Abs(a - color.W) < tol)
                    return true;
            }
        }
        return false;
    }

    private static void OpenAndHover(UiMenu menu, int? hoverRow = null)
    {
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, Data1: 10, Data2: 10)));
        Assert.True(menu.IsOpen);
        if (hoverRow is { } row)
        {
            int ly = 20 + RetailChromeSprites.Border + row * (int)PlainRowHeight + (int)(PlainRowHeight / 2);
            Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseMove, Data1: 10, Data2: ly)));
        }
    }

    [Fact]
    public void Plain_OpenPopup_GridMode_DrawsFlatFillsSelectedAndHover_NoSpriteResolveCalls()
    {
        int resolveCalls = 0;
        var menu = MakePopupMenu(retailButtonArt: false, itemCount: 3, rowsPerColumn: 7, scrollable: false,
            countResolveCall: n => resolveCalls += n);
        menu.Selected = 1;
        OpenAndHover(menu, hoverRow: 2);   // row 2 is hovered (not selected)

        var (renderer, ctx) = MakeContext(200f, 200f);
        menu.DrawOverlays(ctx);
        var segs = renderer.DebugSpriteSegmentVerts;

        Assert.Equal(0, resolveCalls);   // no DAT art resolved at all — not even by id
        Assert.Equal(0, QuadCount(segs, 0x0600124Cu));
        Assert.Equal(0, QuadCount(segs, 0x0600124Du));
        Assert.Equal(0, QuadCount(segs, 0x0600124Eu));

        float outerTop = menu.Height;   // OpenUpward=false
        float outerW = menu.PopupOuterWidth, outerH = menu.PopupOuterHeight;
        float inX = RetailChromeSprites.Border, inY = outerTop + RetailChromeSprites.Border;

        Assert.True(HasFillQuad(segs, 0f, outerTop, outerW, outerH, menu.PlainBackgroundColor),
            "expected the plain popup background fill");
        Assert.True(HasFillQuad(segs, inX, inY + 1 * PlainRowHeight, PlainColumnWidth, PlainRowHeight, menu.PlainSelectedColor),
            "expected row 1 (selected/current) filled with PlainSelectedColor");
        Assert.True(HasFillQuad(segs, inX, inY + 2 * PlainRowHeight, PlainColumnWidth, PlainRowHeight, menu.PlainHoverColor),
            "expected row 2 (hovered) filled with PlainHoverColor");

        // background(1) + outline(4 sides) + selected row(1) + hovered row(1) = 7,
        // nothing else untextured.
        Assert.Equal(7, QuadCount(segs, 0u));
    }

    [Fact]
    public void Plain_OpenPopup_RowText_LeftAlignedAtPlainPadding()
    {
        var menu = MakePopupMenu(retailButtonArt: false, itemCount: 1, rowsPerColumn: 7, scrollable: false);
        OpenAndHover(menu);

        var (renderer, ctx) = MakeContext(200f, 200f);
        menu.DrawOverlays(ctx);

        var glyphSeg = Assert.Single(renderer.DebugSpriteSegmentVerts, s => s.Texture == FontTexture);
        Assert.Equal(RetailChromeSprites.Border + UiMenu.PlainPadding, glyphSeg.Verts[0], 3);
    }


    [Fact]
    public void Plain_OpenPopup_ScrollableOverflow_DrawsRetailScrollbarChrome_RowsStayPlain()
    {
        int resolveCalls = 0;
        var menu = MakePopupMenu(retailButtonArt: false, itemCount: 12, rowsPerColumn: 5, scrollable: true,
            countResolveCall: n => resolveCalls += n);
        menu.Selected = 0;
        OpenAndHover(menu);

        var (renderer, ctx) = MakeContext(200f, 200f);
        menu.DrawOverlays(ctx);
        var segs = renderer.DebugSpriteSegmentVerts;

        Assert.True(menu.PopupScroll.HasOverflow);
        Assert.Equal(6, resolveCalls);
        Assert.Equal(1, QuadCount(segs, menu.ScrollTrackSprite));
        Assert.Equal(1, QuadCount(segs, menu.ScrollUpSprite));
        Assert.Equal(1, QuadCount(segs, menu.ScrollDownSprite));
        Assert.Equal(1, QuadCount(segs, menu.ScrollThumbTopSprite));
        Assert.Equal(1, QuadCount(segs, menu.ScrollThumbSprite));
        Assert.Equal(1, QuadCount(segs, menu.ScrollThumbBottomSprite));

        float outerTop = menu.Height;
        float inX = RetailChromeSprites.Border, inY = outerTop + RetailChromeSprites.Border;

        Assert.True(HasFillQuad(segs, inX, inY, PlainColumnWidth, PlainRowHeight, menu.PlainSelectedColor),
            "expected visible row 0 (selected/current) still filled with PlainSelectedColor");
        Assert.Equal(0, QuadCount(segs, menu.ItemHighlightSprite));
        Assert.Equal(0, QuadCount(segs, menu.ItemNormalSprite));

        Assert.Equal(6, QuadCount(segs, 0u));
    }

    [Fact]
    public void Plain_ScrollablePopup_ContentFits_DrawsNoScrollbarAtAll()
    {
        int resolveCalls = 0;
        var menu = MakePopupMenu(retailButtonArt: false, itemCount: 3, rowsPerColumn: 5, scrollable: true,
            countResolveCall: n => resolveCalls += n);
        OpenAndHover(menu);

        var (renderer, ctx) = MakeContext(200f, 200f);
        menu.DrawOverlays(ctx);
        var segs = renderer.DebugSpriteSegmentVerts;

        Assert.False(menu.PopupScroll.HasOverflow);

        Assert.Equal(3, resolveCalls);
        Assert.Equal(1, QuadCount(segs, menu.ScrollTrackSprite));
        Assert.Equal(1, QuadCount(segs, menu.ScrollUpSprite));
        Assert.Equal(1, QuadCount(segs, menu.ScrollDownSprite));
        Assert.Equal(0, QuadCount(segs, menu.ScrollThumbSprite));

        // popup bg(1)+outline(4) = 5 untextured quads (nothing
        // selected/hovered here either, and the scrollbar draws no fills).
        Assert.Equal(5, QuadCount(segs, 0u));
    }

    [Fact]
    public void Plain_OpenPopup_HitTesting_SelectsHoveredRow_ClosesPopup()
    {
        object? picked = null;
        var menu = MakePopupMenu(retailButtonArt: false, itemCount: 3, rowsPerColumn: 7, scrollable: false);
        menu.OnSelect = p => picked = p;
        OpenAndHover(menu, hoverRow: 2);

        int ly = 20 + RetailChromeSprites.Border + 2 * (int)PlainRowHeight + (int)(PlainRowHeight / 2);
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, Data1: 10, Data2: ly)));

        Assert.Equal(2, picked);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void Retail_OpenPopup_DrawIsByteForByteUnchanged_RegressionGolden()
    {
        var menu = MakePopupMenu(retailButtonArt: true, itemCount: 2, rowsPerColumn: 7, scrollable: false);
        menu.Selected = 1;
        OpenAndHover(menu);

        var (renderer, ctx) = MakeContext(200f, 200f);
        menu.DrawOverlays(ctx);
        var segs = renderer.DebugSpriteSegmentVerts;

        Assert.Equal(1, QuadCount(segs, RetailChromeSprites.CenterFill));   // bevel drawn
        Assert.Equal(1, QuadCount(segs, 0x0600124Cu));   // PopupBgSprite panel fill
        Assert.Equal(1, QuadCount(segs, 0x0600124Du));   // ItemHighlightSprite (row 1, selected)
        Assert.Equal(1, QuadCount(segs, 0x0600124Eu));   // ItemNormalSprite (row 0)
        Assert.Equal(0, QuadCount(segs, 0u));
    }
}
