using AcDream.Core.Items;
using Xunit;

namespace AcDream.Core.Tests.Items;

public class BurdenMathTests
{
    [Theory]
    [InlineData(100, 0, 15000)]   // base: str*150
    [InlineData(100, 3, 24000)]
    [InlineData(100, 10, 30000)]
    [InlineData(0, 5, 0)]         // str<=0 -> 0
    public void EncumbranceCapacity_matches_retail(int str, int aug, int expected)
        => Assert.Equal(expected, BurdenMath.EncumbranceCapacity(str, aug));

    [Theory]
    [InlineData(15000, 7500, 0.5f)]
    [InlineData(15000, 0, 0f)]
    [InlineData(0, 100, 0f)]      // cap<=0 guard
    public void LoadRatio_is_burden_over_capacity(int cap, int burden, float expected)
        => Assert.Equal(expected, BurdenMath.LoadRatio(cap, burden), 4);

    [Theory]
    [InlineData(0.5f, 1f, 0)]
    [InlineData(1f, 1f, 0)]
    [InlineData(1.25f, 0.75f, 30)]
    [InlineData(1.5f, 0.5f, 50)]
    [InlineData(2f, 0f, 100)]
    [InlineData(3f, 0f, 100)]
    public void LoadModifier_and_character_info_penalty_match_retail(
        float load, float modifier, int penalty)
    {
        Assert.Equal(modifier, BurdenMath.LoadModifier(load), 4);
        Assert.Equal(penalty, BurdenMath.LoadPenaltyPercent(load));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 0.16667f)]
    [InlineData(1.0f, 0.33333f)]
    [InlineData(3.0f, 1.0f)]      // full at 300%
    [InlineData(4.0f, 1.0f)]
    public void LoadToFill_is_third_clamped(float load, float expected)
        => Assert.Equal(expected, BurdenMath.LoadToFill(load), 4);

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 50)]
    [InlineData(1.0f, 100)]
    [InlineData(3.0f, 300)]       // 300% = full
    [InlineData(4.0f, 300)]
    public void LoadToPercent_saturates_at_300(float load, int expected)
        => Assert.Equal(expected, BurdenMath.LoadToPercent(load));
}
