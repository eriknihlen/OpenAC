using AcDream.Core.Items;
using Xunit;

namespace AcDream.Core.Tests.Items;

public sealed class StackSplitQuantityStateTests
{
    [Fact]
    public void Reset_initializesToFullStack_andClampsZeroMaximumToOne()
    {
        var state = new StackSplitQuantityState();

        state.Reset(17u);
        Assert.Equal(17u, state.Value);
        Assert.Equal(17u, state.Maximum);
        Assert.Equal(1f, state.Ratio);

        state.Reset(0u);
        Assert.Equal(1u, state.Value);
        Assert.Equal(1u, state.Maximum);
    }

    [Theory]
    [InlineData(null, 1u)]
    [InlineData("", 1u)]
    [InlineData("invalid", 1u)]
    [InlineData("0", 1u)]
    [InlineData("7", 7u)]
    [InlineData("999", 10u)]
    public void Text_usesWcstoulStyleParse_thenClamps(string? text, uint expected)
    {
        var state = new StackSplitQuantityState();
        state.Reset(10u);

        state.SetFromText(text);

        Assert.Equal(expected, state.Value);
    }

    [Theory]
    [InlineData(-1f, 1u)]
    [InlineData(0f, 1u)]
    [InlineData(0.099f, 1u)]
    [InlineData(0.1f, 2u)]
    [InlineData(0.5f, 6u)]
    [InlineData(0.999f, 10u)]
    [InlineData(1f, 10u)]
    [InlineData(2f, 10u)]
    public void Slider_matchesRetailThousandStepConversion(float ratio, uint expected)
    {
        var state = new StackSplitQuantityState();
        state.Reset(10u);

        state.SetFromSliderRatio(ratio);

        Assert.Equal(expected, state.Value);
    }

    [Fact]
    public void GetObjectSplitSize_usesSliderOnlyForSelectedObject()
    {
        var state = new StackSplitQuantityState();
        state.Reset(10u);
        state.SetValue(3u);

        Assert.Equal(3u, state.GetObjectSplitSize(0xAu, 0xAu, 10u));
        Assert.Equal(7u, state.GetObjectSplitSize(0xBu, 0xAu, 7u));
        Assert.Equal(3u, state.GetObjectSplitSize(0xCu, 0xCu, 0u));
        Assert.Equal(1u, state.GetObjectSplitSize(0xCu, 0xDu, 0u));
    }
}
