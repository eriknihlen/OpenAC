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
}
