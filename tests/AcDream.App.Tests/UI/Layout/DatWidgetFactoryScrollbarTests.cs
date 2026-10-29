using System.Linq;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using Xunit;

namespace AcDream.App.Tests.UI.Layout;

public sealed class DatWidgetFactoryScrollbarTests
{
    // ── #31: the vendor item strip's horizontal bar composes its thumb from
    //        three authored slices; none of them reached the element.

    [Fact]
    public void HorizontalBar_CompositedThumbResolvesItsThreeSlices()
    {
        ImportedLayout layout = FixtureLoader.LoadVendor();
        UiScrollbar bar = Assert.IsType<UiScrollbar>(layout.FindElement(VendorUiController.ItemScrollbarId));

        Assert.True(bar.Horizontal);
        Assert.Equal(0x06004C80u, bar.ThumbTopSprite);
        Assert.Equal(0x06004C83u, bar.ThumbSprite);
        Assert.Equal(0x06004C86u, bar.ThumbBotSprite);
        Assert.Equal(0x06004C81u, bar.ThumbTopRolloverSprite);
        Assert.Equal(0x06004C85u, bar.ThumbPressedSprite);
    }

    [Fact]
    public void HorizontalBar_ReadsItsAuthoredProportionalityAndMinimum()
    {
        ImportedLayout layout = FixtureLoader.LoadVendor();
        UiScrollbar bar = Assert.IsType<UiScrollbar>(layout.FindElement(VendorUiController.ItemScrollbarId));

        Assert.True(bar.Proportional);
        Assert.Equal(16f, bar.MinThumbExtent);
        Assert.Equal(16f, bar.ThumbExtent);
    }

    // ── #33: a bar authored as not proportional keeps its thumb's authored
    //        height instead of sizing it to the visible fraction.

    [Fact]
    public void VerticalBar_NotProportional_KeepsTheAuthoredThumbExtent()
    {
        ElementInfo info = ScrollbarFixtures.FixedThumbBar(proportional: false, minThumb: 37);

        UiScrollbar bar = Assert.IsType<UiScrollbar>(
            DatWidgetFactory.Create(info, _ => (1u, 4, 4), null, null, null));

        Assert.False(bar.Horizontal);
        Assert.False(bar.Proportional);
        Assert.Equal(37f, bar.MinThumbExtent);
        Assert.Equal(39f, bar.ThumbExtent);
        Assert.Equal(17f, bar.DecrementButtonExtent);
        Assert.Equal(17f, bar.IncrementButtonExtent);

        var model = new UiScrollable { ContentHeight = 400, ViewHeight = 300 };
        model.ScrollToEnd();
        var (start, extent) = bar.ModelThumbRect(model, trackStart: 17f, trackLen: 273f);
        Assert.Equal(39f, extent);
        Assert.Equal(17f + 273f - 39f, start);
    }

    [Fact]
    public void VerticalBar_Proportional_SizesTheThumbButNeverBelowTheAuthoredMinimum()
    {
        ElementInfo info = ScrollbarFixtures.FixedThumbBar(proportional: true, minThumb: 37);

        UiScrollbar bar = Assert.IsType<UiScrollbar>(
            DatWidgetFactory.Create(info, _ => (1u, 4, 4), null, null, null));

        var model = new UiScrollable { ContentHeight = 10_000, ViewHeight = 100 };
        var (_, extent) = bar.ModelThumbRect(model, trackStart: 17f, trackLen: 273f);
        Assert.Equal(37f, extent);
    }

    // A character-creation attribute slider fills in a different colour while
    // its attribute is locked; the bar's state picks the fill art.

    [Fact]
    public void HorizontalBar_FillFollowsTheBarsStateWhenTheFillAuthorsArtPerState()
    {
        ElementInfo info = ScrollbarFixtures.LockableScalarBar();

        UiScrollbar bar = Assert.IsType<UiScrollbar>(
            DatWidgetFactory.Create(info, _ => (1u, 4, 4), null, null, null));

        Assert.Equal(ScrollbarFixtures.UnlockedFill, bar.ThumbFillSprite);
        Assert.True(bar.TrySetThumbFillState(ScrollbarFixtures.LockedState));
        Assert.Equal(ScrollbarFixtures.LockedFill, bar.ThumbFillSprite);
        Assert.True(bar.TrySetThumbFillState(ScrollbarFixtures.UnlockedState));
        Assert.Equal(ScrollbarFixtures.UnlockedFill, bar.ThumbFillSprite);
        Assert.False(bar.TrySetThumbFillState(0x10000099u));
        Assert.Equal(ScrollbarFixtures.UnlockedFill, bar.ThumbFillSprite);
    }

    // A horizontal bar's child 4 is not a track: it is a fill from the bar's
    // start to the thumb's centre (the thumb's left edge truncated, plus half
    // its width rounded down), so a locked attribute colours its slider up
    // to the jewel and no further. See the research note on the scrollbar
    // fill up to the thumb.

    [Theory]
    [InlineData(0f, 18f)]
    [InlineData(0.5f, 145f)]
    [InlineData(1f, 272f)]
    public void HorizontalBar_Child4FillsOnlyUpToTheThumbsCentre(float position, float width)
    {
        UiScrollbar bar = CreateDrawable(ScrollbarFixtures.LockableScalarBar());
        bar.SetScalarPosition(position);

        Assert.Equal(new[] { (0f, width) }, DrawnSpans(bar, ScrollbarFixtures.UnlockedFill));
    }

    [Fact]
    public void HorizontalBar_LockedFillStillEndsAtTheThumbsCentre()
    {
        UiScrollbar bar = CreateDrawable(ScrollbarFixtures.LockableScalarBar());
        bar.SetScalarPosition(0.5f);
        bar.TrySetThumbFillState(ScrollbarFixtures.LockedState);

        Assert.Equal(new[] { (0f, 145f) }, DrawnSpans(bar, ScrollbarFixtures.LockedFill));
        Assert.Empty(DrawnSpans(bar, ScrollbarFixtures.UnlockedFill));
    }

    // The combat power bar has art of its own (the full-width track) and a
    // child 4 authored 0 x 0: the child still fills up to the thumb's centre.
    // 707 wide, thumb 12: at half, 347 + 6 pixels.

    [Fact]
    public void HorizontalBar_WithItsOwnTrack_StillFillsItsChild4ToTheThumb()
    {
        const uint track = 0x060074CAu;
        const uint fill = 0x06001919u;
        const uint thumbArt = 0x06001923u;
        var info = new ElementInfo { Id = 0x1000004Fu, Type = 11u, Width = 707f, Height = 14f };
        info.States[UiStateInfo.DirectStateId] = new UiStateInfo
        {
            Id = UiStateInfo.DirectStateId, Image = new UiImageMedia(track, 1),
        };
        var thumb = new ElementInfo { Id = 1u, Type = 1u, Width = 12f, Height = 14f };
        thumb.States[UiStateInfo.DirectStateId] = new UiStateInfo
        {
            Id = UiStateInfo.DirectStateId, Image = new UiImageMedia(thumbArt, 1),
        };
        var child4 = new ElementInfo { Id = 4u, Type = 3u };
        child4.States[UiStateInfo.DirectStateId] = new UiStateInfo
        {
            Id = UiStateInfo.DirectStateId, Image = new UiImageMedia(fill, 1),
        };
        info.Children.Add(thumb);
        info.Children.Add(child4);
        UiScrollbar bar = CreateDrawable(info);
        bar.SetScalarPosition(0.5f);

        Assert.Equal(new[] { (0f, 707f) }, DrawnSpans(bar, track));
        Assert.Equal(new[] { (0f, 353f) }, DrawnSpans(bar, fill));
    }

    private static readonly Dictionary<uint, (int w, int h)> ArtSizes = new()
    {
        [ScrollbarFixtures.UnlockedFill] = (7, 39),
        [ScrollbarFixtures.LockedFill] = (7, 39),
        [0x06004D34u] = (37, 39),
        [0x060074CAu] = (60, 13),
        [0x06001919u] = (60, 13),
        [0x06001923u] = (12, 14),
    };

    private static UiScrollbar CreateDrawable(ElementInfo info)
    {
        UiScrollbar bar = Assert.IsType<UiScrollbar>(
            DatWidgetFactory.Create(info, _ => (1u, 4, 4), null, null, null));
        bar.SpriteResolve = id => ArtSizes.TryGetValue(id, out var size)
            ? (id, size.w, size.h)
            : (0u, 0, 0);
        return bar;
    }

    /// <summary>The left edge and width of every quad drawn with <paramref name="texture"/>.</summary>
    private static (float x, float width)[] DrawnSpans(UiScrollbar bar, uint texture)
    {
        var renderer = new TextRenderer(new RecordingGpuDevice(), new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(1600f, 1200f));
        var ctx = new UiRenderContext(renderer, new Vector2(1600f, 1200f));
        bar.DrawSelfAndChildren(ctx);
        const int floatsPerVertex = 8, verticesPerQuad = 6;
        var spans = new List<(float, float)>();
        foreach (var (_, verts) in renderer.DebugSpriteSegmentVerts.Where(s => s.Texture == texture))
        {
            for (int q = 0; q + floatsPerVertex * verticesPerQuad <= verts.Count; q += floatsPerVertex * verticesPerQuad)
            {
                float min = float.MaxValue, max = float.MinValue;
                for (int v = 0; v < verticesPerQuad; v++)
                {
                    float x = verts[q + v * floatsPerVertex];
                    min = MathF.Min(min, x);
                    max = MathF.Max(max, x);
                }
                spans.Add((min, max - min));
            }
        }
        return spans.ToArray();
    }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }
}
