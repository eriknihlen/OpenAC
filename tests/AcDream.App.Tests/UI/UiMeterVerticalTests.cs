using AcDream.App.UI;
using Xunit;

namespace AcDream.App.Tests.UI;

public class UiMeterVerticalTests
{
    [Fact]
    public void Vertical_fill_from_bottom_occupies_lower_fraction()
    {
        // 11x58 bar, 50% → fill rect is the bottom 29px.
        var (x, y, w, h) = UiMeter.ComputeVFillRect(0.5f, 11f, 58f, fromBottom: true);
        Assert.Equal(0f, x);
        Assert.Equal(29f, y, 3);    // h - h*p = 58 - 29
        Assert.Equal(11f, w);
        Assert.Equal(29f, h, 3);
    }

    [Fact]
    public void Vertical_fill_from_top_starts_at_zero()
    {
        var (_, y, _, h) = UiMeter.ComputeVFillRect(0.25f, 11f, 58f, fromBottom: false);
        Assert.Equal(0f, y);
        Assert.Equal(14.5f, h, 3);
    }

    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(2f, 58f)]
    public void Vertical_fill_clamps_fraction(float pct, float expectedH)
    {
        var (_, _, _, h) = UiMeter.ComputeVFillRect(pct, 11f, 58f, fromBottom: true);
        Assert.Equal(expectedH, h, 3);
    }
}
