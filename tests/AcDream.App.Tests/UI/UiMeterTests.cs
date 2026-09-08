using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public class UiMeterTests
{
    [Fact]
    public void ComputeFillRect_HalfFillIsHalfWidth()
    {
        var (x, y, w, h) = UiMeter.ComputeFillRect(0.5f, 200f, 12f);
        Assert.Equal(0f, x); Assert.Equal(0f, y);
        Assert.Equal(100f, w); Assert.Equal(12f, h);
    }

    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(2f, 200f)]
    [InlineData(0f, 0f)]
    [InlineData(1f, 200f)]
    public void ComputeFillRect_ClampsFraction(float pct, float expectedW)
    {
        var (_, _, w, _) = UiMeter.ComputeFillRect(pct, 200f, 12f);
        Assert.Equal(expectedW, w);
    }
}
