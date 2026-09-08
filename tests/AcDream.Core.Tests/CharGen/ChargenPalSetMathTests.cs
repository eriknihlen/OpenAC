using AcDream.Core.CharGen;

namespace AcDream.Core.Tests.CharGen;

public class ChargenPalSetMathTests
{
    [Theory]
    [InlineData(5, 0.0, 0)]
    [InlineData(5, 1.0, 4)]
    [InlineData(5, 0.5, 2)]
    [InlineData(1, 0.0, 0)]
    [InlineData(1, 1.0, 0)]
    public void GetPaletteIndex_matches_the_cited_acclient_formula(int count, double shade, int expected)
    {
        Assert.Equal(expected, ChargenPalSetMath.GetPaletteIndex(count, shade));
    }

    [Theory]
    [InlineData(0, 0.5)]
    [InlineData(-1, 0.5)]
    public void GetPaletteIndex_returns_negative_one_for_non_positive_count(int count, double shade)
    {
        Assert.Equal(-1, ChargenPalSetMath.GetPaletteIndex(count, shade));
    }

    [Theory]
    [InlineData(5, -0.0001)]
    [InlineData(5, 1.0001)]
    [InlineData(5, ChargenAppearanceSelection.UnsetShade)]
    public void GetPaletteIndex_returns_negative_one_for_out_of_range_shade(int count, double shade)
    {
        Assert.Equal(-1, ChargenPalSetMath.GetPaletteIndex(count, shade));
    }

    [Fact]
    public void GetPaletteIndex_never_exceeds_count_minus_one_near_the_upper_bound()
    {
        for (int count = 1; count <= 64; count++)
            Assert.Equal(count - 1, ChargenPalSetMath.GetPaletteIndex(count, 1.0));
    }

    [Fact]
    public void GetPaletteIndex_is_monotonic_non_decreasing_in_shade()
    {
        const int count = 13;
        int previous = -1;
        for (double shade = 0.0; shade <= 1.0; shade += 0.01)
        {
            int index = ChargenPalSetMath.GetPaletteIndex(count, shade);
            Assert.True(index >= previous, $"index regressed at shade={shade}");
            previous = index;
        }
    }
}
