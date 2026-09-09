using System.Linq;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using Xunit;

namespace AcDream.App.Tests.UI;

public sealed class UiMarkupListScrollbarTests
{
    private static (TextRenderer renderer, UiRenderContext ctx) MakeContext(float w, float h)
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(w, h));
        var ctx = new UiRenderContext(renderer, new Vector2(w, h));
        return (renderer, ctx);
    }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private const int FloatsPerQuad = 48; // 6 vertices/quad x 8 floats/vertex (AppendQuad).

    private static int QuadCount(
        System.Collections.Generic.IReadOnlyList<(uint Texture, System.Collections.Generic.IReadOnlyList<float> Verts)> segs,
        uint texture)
        => segs.Where(s => s.Texture == texture).Sum(s => s.Verts.Count) / FloatsPerQuad;

    private static (uint tex, int w, int h) Resolve(uint id) => (id, 16, 16);


    [Fact]
    public void SingleColumn_Overflowing_DrawsRetailScrollbarChromeAtRightEdge()
    {
        var list = new UiMarkupList
        {
            Width = 100f, Height = 40f, RowHeight = 18f, // 2 visible rows
            SpriteResolve = Resolve,
            SelectedIndexSource = () => -1,
            ItemsSource = () => Enumerable.Range(0, 10).Select(i => $"row{i}").ToArray(),
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        list.DrawSelfAndChildren(ctx);
        var segs = renderer.DebugSpriteSegmentVerts;

        Assert.Equal(1, QuadCount(segs, RetailScrollbarChrome.Track));
        Assert.Equal(1, QuadCount(segs, RetailScrollbarChrome.UpNormal));
        Assert.Equal(1, QuadCount(segs, RetailScrollbarChrome.DownNormal));

        // Track drawn at the list's own right edge, reserving 16px, full height.
        var trackSeg = Assert.Single(segs, s => s.Texture == RetailScrollbarChrome.Track);
        Assert.Equal(84f, trackSeg.Verts[0], 2); // x = Width(100) - 16
        Assert.Equal(0f, trackSeg.Verts[1], 2);
        Assert.Equal(100f, trackSeg.Verts[8], 2); // x + w = Width
        Assert.Equal(40f, trackSeg.Verts[9], 2); // y + h = Height
    }

    [Fact]
    public void SingleColumn_ContentFits_DrawsNoScrollbarChromeAtAll()
    {
        var list = new UiMarkupList
        {
            Width = 100f, Height = 40f, RowHeight = 18f, // 2 visible rows
            SpriteResolve = Resolve,
            SelectedIndexSource = () => -1,
            ItemsSource = () => new[] { "only-one-row" },
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        list.DrawSelfAndChildren(ctx);
        var segs = renderer.DebugSpriteSegmentVerts;

        Assert.Equal(0, QuadCount(segs, RetailScrollbarChrome.Track));
        Assert.Equal(0, QuadCount(segs, RetailScrollbarChrome.UpNormal));
        Assert.Equal(0, QuadCount(segs, RetailScrollbarChrome.DownNormal));
        Assert.Equal(0, QuadCount(segs, RetailScrollbarChrome.ThumbMidNormal));
    }

    [Fact]
    public void SingleColumn_NoSpriteResolveWired_DrawsNoScrollbar_NoCrash()
    {
        var list = new UiMarkupList
        {
            Width = 100f, Height = 40f, RowHeight = 18f,
            SelectedIndexSource = () => -1,
            ItemsSource = () => Enumerable.Range(0, 10).Select(i => $"row{i}").ToArray(),
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        list.DrawSelfAndChildren(ctx);
        var segs = renderer.DebugSpriteSegmentVerts;

        Assert.Equal(0, QuadCount(segs, RetailScrollbarChrome.Track));
        Assert.Equal(0, QuadCount(segs, RetailScrollbarChrome.UpNormal));
        Assert.Equal(0, QuadCount(segs, RetailScrollbarChrome.DownNormal));
        Assert.Equal(0, QuadCount(segs, RetailScrollbarChrome.ThumbMidNormal));
        Assert.DoesNotContain(segs, s => s.Texture != 0u);
    }

    [Fact]
    public void SingleColumn_UpArrowClick_ScrollsUpByOneRow()
    {
        var list = new UiMarkupList
        {
            Width = 100f, Height = 40f, RowHeight = 18f, // 2 visible rows of 10
            SpriteResolve = Resolve,
            SelectedIndexSource = () => -1,
            ItemsSource = () => Enumerable.Range(0, 10).Select(i => $"row{i}").ToArray(),
        };
        var (_, ctx) = MakeContext(200f, 200f);
        list.DrawSelfAndChildren(ctx);

        for (int i = 0; i < 3; i++)
            list.OnEvent(new UiEvent { Type = UiEventType.Scroll, Data0 = -1 });
        list.DrawSelfAndChildren(ctx);

        int? selected = null;
        list.SelectionChanged = row => selected = row;

        // Up-arrow button occupies the scrollbar's own top 16px, x in
        // [84,100).
        Assert.True(list.OnEvent(new UiEvent
        {
            Type = UiEventType.MouseDown, Data1 = 90, Data2 = 5,
        }));
        list.DrawSelfAndChildren(ctx);

        // Row 0 of the (now one-row-higher) view is absolute row 2.
        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 10, Data2 = 2 });
        Assert.Equal(2, selected);
    }

    [Fact]
    public void SingleColumn_ThumbDrag_MovesTopRowAndIsReadableByASubsequentClick()
    {
        var list = new UiMarkupList
        {
            Width = 100f, Height = 40f, RowHeight = 18f, // 2 of 10 rows visible
            SpriteResolve = Resolve,
            SelectedIndexSource = () => -1,
            ItemsSource = () => Enumerable.Range(0, 10).Select(i => $"row{i}").ToArray(),
        };
        var (_, ctx) = MakeContext(200f, 200f);
        list.DrawSelfAndChildren(ctx);

        // Track spans y in [16,24) (Height 40 - 16 up - 16 down = 8px track,
        // thumb ratio 2/10=0.2 but floored to the 8px MinThumb). Press
        // squarely inside the thumb (drawn at the very top initially) then
        // drag to the bottom of the track to scroll to the end.
        Assert.True(list.OnEvent(new UiEvent
        {
            Type = UiEventType.MouseDown, Data1 = 90, Data2 = 18,
        }));
        list.OnEvent(new UiEvent { Type = UiEventType.MouseMove, Data1 = 90, Data2 = 40 });
        list.OnEvent(new UiEvent { Type = UiEventType.MouseUp, Data1 = 90, Data2 = 40 });
        list.DrawSelfAndChildren(ctx);

        int? selected = null;
        list.SelectionChanged = row => selected = row;
        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 10, Data2 = 2 });
        Assert.Equal(8, selected);
    }


    [Fact]
    public void Columns_Overflowing_ReservesSixteenPixels_LastColumnShrinksAccordingly()
    {
        var list = new UiMarkupList
        {
            Width = 100f, Height = 40f, RowHeight = 18f, // 2 visible rows
            SpriteResolve = Resolve,
            SelectedIndexSource = () => -1,
            Columns = new[]
            {
                UiMarkupListColumn.Text(20f, () => Enumerable.Range(0, 10).Select(i => $"a{i}").ToArray(), null),
                UiMarkupListColumn.Icon(
                    0f, () => Enumerable.Range(0, 10).Select(i => (uint)(i + 1)).ToArray(),
                    id => (id, 16, 16), _ => { }),
            },
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        list.DrawSelfAndChildren(ctx);
        var segs = renderer.DebugSpriteSegmentVerts;

        Assert.Equal(1, QuadCount(segs, RetailScrollbarChrome.Track));

        var iconQuad = Assert.Single(segs, s => s.Texture == 1u);
        Assert.Equal(44f, iconQuad.Verts[0], 2);
        Assert.True(iconQuad.Verts[8] <= 84f + 0.01f,
            $"expected the icon column's cell to shrink for the reserved scrollbar, got right edge {iconQuad.Verts[8]}");
    }

    [Fact]
    public void Columns_ContentFits_NoReservation_LastColumnKeepsFullRemainder()
    {
        var list = new UiMarkupList
        {
            Width = 100f, Height = 40f, RowHeight = 18f, // 2 visible rows, 1 row of data
            SpriteResolve = Resolve,
            SelectedIndexSource = () => -1,
            Columns = new[]
            {
                UiMarkupListColumn.Text(20f, () => new[] { "a" }, null),
                UiMarkupListColumn.Icon(0f, () => new uint[] { 1u }, id => (id, 16, 16), _ => { }),
            },
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        list.DrawSelfAndChildren(ctx);
        var segs = renderer.DebugSpriteSegmentVerts;

        Assert.Equal(0, QuadCount(segs, RetailScrollbarChrome.Track));
        var iconQuad = Assert.Single(segs, s => s.Texture == 1u);
        Assert.Equal(52f, iconQuad.Verts[0], 2);
    }

    [Fact]
    public void Columns_Overflowing_UpArrowClick_ScrollsUpByOneRow()
    {
        var list = new UiMarkupList
        {
            Width = 100f, Height = 40f, RowHeight = 18f, // 2 of 10 rows visible
            SpriteResolve = Resolve,
            SelectedIndexSource = () => -1,
            Columns = new[]
            {
                UiMarkupListColumn.Text(100f, () => Enumerable.Range(0, 10).Select(i => $"a{i}").ToArray(), null),
            },
        };
        var (_, ctx) = MakeContext(200f, 200f);
        list.DrawSelfAndChildren(ctx);

        for (int i = 0; i < 3; i++)
            list.OnEvent(new UiEvent { Type = UiEventType.Scroll, Data0 = -1 });
        list.DrawSelfAndChildren(ctx);

        int? selected = null;
        list.SelectionChanged = row => selected = row;

        Assert.True(list.OnEvent(new UiEvent
        {
            Type = UiEventType.MouseDown, Data1 = 90, Data2 = 5,
        }));
        list.DrawSelfAndChildren(ctx);

        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 10, Data2 = 2 });
        Assert.Equal(2, selected);
    }
}
