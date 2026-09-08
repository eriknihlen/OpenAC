using AcDream.App.UI;
using Xunit;

namespace AcDream.App.Tests.UI;

public class UiScrollableTests
{
    [Fact]
    public void Clamp_KeepsScrollWithinContent()
    {
        var s = new UiScrollable { ContentHeight = 300, ViewHeight = 100 };
        s.SetScrollY(500);
        Assert.Equal(200, s.ScrollY);
        s.SetScrollY(-50);
        Assert.Equal(0, s.ScrollY);
    }

    [Fact]
    public void FitsView_PinsToZero()
    {
        var s = new UiScrollable { ContentHeight = 80, ViewHeight = 100 };
        s.SetScrollY(40);
        Assert.Equal(0, s.ScrollY);
        Assert.False(s.HasOverflow);
    }

    [Fact]
    public void ThumbRatio_IsViewOverContent_ClampedToOne()
    {
        var s = new UiScrollable { ContentHeight = 400, ViewHeight = 100 };
        Assert.Equal(0.25f, s.ThumbRatio, 3);
        var full = new UiScrollable { ContentHeight = 50, ViewHeight = 100 };
        Assert.Equal(1f, full.ThumbRatio, 3);
    }

    [Fact]
    public void PositionRatio_MapsScrollToZeroOne()
    {
        var s = new UiScrollable { ContentHeight = 300, ViewHeight = 100 };
        s.SetScrollY(100);
        Assert.Equal(0.5f, s.PositionRatio, 3);
        s.SetScrollY(200);
        Assert.Equal(1f, s.PositionRatio, 3);
    }

    [Fact]
    public void SetPositionRatio_IsInverseOfPositionRatio()
    {
        var s = new UiScrollable { ContentHeight = 300, ViewHeight = 100 };
        s.SetPositionRatio(0.5f);
        Assert.Equal(100, s.ScrollY);
    }

    [Fact]
    public void ScrollByLines_AdvancesByLineHeight()
    {
        var s = new UiScrollable { ContentHeight = 1000, ViewHeight = 100, LineHeight = 16 };
        s.ScrollByLines(-2);
        Assert.Equal(0, s.ScrollY);
        s.SetScrollY(50);
        s.ScrollByLines(2);
        Assert.Equal(82, s.ScrollY);
    }

    [Fact]
    public void ScrollByPage_AdvancesByViewHeight()
    {
        var s = new UiScrollable { ContentHeight = 1000, ViewHeight = 100, LineHeight = 16 };
        s.SetScrollY(200);
        s.ScrollByPage(1);
        Assert.Equal(300, s.ScrollY);
    }

    [Fact]
    public void SetExtents_PreservesEndButRetainsUserScrollback()
    {
        var s = new UiScrollable();

        s.SetExtents(contentHeight: 400, viewHeight: 100, preserveEnd: true);
        Assert.Equal(300, s.ScrollY);

        // A scrollbar/wheel interaction moves off the end. Repeating layout with
        // the same extents must not overwrite that user-selected position.
        s.ScrollByLines(-1);
        Assert.Equal(284, s.ScrollY);
        s.SetExtents(contentHeight: 400, viewHeight: 100, preserveEnd: true);
        Assert.Equal(284, s.ScrollY);

        s.ScrollToEnd();
        s.SetExtents(contentHeight: 500, viewHeight: 100, preserveEnd: true);
        Assert.Equal(400, s.ScrollY);
    }
}
