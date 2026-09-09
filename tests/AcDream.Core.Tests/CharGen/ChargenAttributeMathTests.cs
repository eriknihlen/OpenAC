using AcDream.Core.CharGen;

namespace AcDream.Core.Tests.CharGen;

public sealed class ChargenAttributeMathTests
{
    [Fact]
    public void RemainingCredits_IsBudgetMinusSumOfRawAttributeValues()
    {
        var values = new ChargenAttributeValues(10, 10, 10, 10, 10, 10);

        int remaining = ChargenAttributeMath.RemainingCredits(180u, values);

        Assert.Equal(120, remaining);
    }

    [Fact]
    public void RemainingCredits_ZeroWhenValuesExactlyConsumeBudget()
    {
        var values = new ChargenAttributeValues(40, 40, 40, 20, 20, 20);
        Assert.Equal(180, values.Total);

        Assert.Equal(0, ChargenAttributeMath.RemainingCredits(180u, values));
    }

    [Fact]
    public void IsFullySpent_TrueOnlyWhenRemainingIsExactlyZero()
    {
        var underspent = new ChargenAttributeValues(10, 10, 10, 10, 10, 10);
        var exact = new ChargenAttributeValues(40, 40, 40, 20, 20, 20);

        Assert.False(ChargenAttributeMath.IsFullySpent(180u, underspent));
        Assert.True(ChargenAttributeMath.IsFullySpent(180u, exact));
    }

    [Fact]
    public void IsFullySpent_FalseWhenCreditsRemain_MatchesRetailFinishGate()
    {
        var oneUnderBudget = new ChargenAttributeValues(40, 40, 40, 20, 20, 19);

        Assert.False(ChargenAttributeMath.IsFullySpent(180u, oneUnderBudget));
        Assert.Equal(1, ChargenAttributeMath.RemainingCredits(180u, oneUnderBudget));
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void IsWithinRange_EnforcesRetailFloorAndCeiling(int value, bool expected)
    {
        Assert.Equal(expected, ChargenAttributeMath.IsWithinRange(value));
    }

    [Fact]
    public void AreAllWithinRange_FalseWhenAnySingleAttributeIsOutOfRange()
    {
        var withinRange = new ChargenAttributeValues(10, 100, 50, 50, 50, 50);
        var oneTooLow = withinRange with { Focus = 9 };
        var oneTooHigh = withinRange with { Self = 101 };

        Assert.True(ChargenAttributeMath.AreAllWithinRange(withinRange));
        Assert.False(ChargenAttributeMath.AreAllWithinRange(oneTooLow));
        Assert.False(ChargenAttributeMath.AreAllWithinRange(oneTooHigh));
    }

    [Fact]
    public void AttributeValues_TotalSumsAllSixInWireOrder()
    {
        var values = new ChargenAttributeValues(
            Strength: 1, Endurance: 2, Coordination: 3, Quickness: 4, Focus: 5, Self: 6);

        Assert.Equal(21, values.Total);
    }
}
