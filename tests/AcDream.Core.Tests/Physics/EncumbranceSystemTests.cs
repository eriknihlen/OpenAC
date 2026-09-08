using AcDream.Core.Items;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class EncumbranceSystemTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-5, 0, 0)]
    [InlineData(100, 0, 15000)]      // 150 * 100
    [InlineData(100, 3, 24000)]
    [InlineData(100, 100, 30000)]
    public void EncumbranceCapacity_MatchesBurdenMath(int strength, int aug, int expected)
    {
        Assert.Equal(expected, EncumbranceSystem.EncumbranceCapacity(strength, aug));
        Assert.Equal(
            BurdenMath.EncumbranceCapacity(strength, aug),
            EncumbranceSystem.EncumbranceCapacity(strength, aug));
    }

    [Theory]
    [InlineData(0, 0, 0f)]
    [InlineData(1000, 500, 0.5f)]
    [InlineData(1000, 1000, 1.0f)]
    [InlineData(1000, 2000, 2.0f)]
    public void Load_MatchesBurdenMath(int capacity, int burden, float expected)
    {
        Assert.Equal(expected, EncumbranceSystem.Load(capacity, burden), precision: 4);
        Assert.Equal(
            BurdenMath.LoadRatio(capacity, burden),
            EncumbranceSystem.Load(capacity, burden),
            precision: 5);
    }

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(0.99f, 1f)]
    [InlineData(1.0f, 1f)]
    [InlineData(1.25f, 0.75f)]
    [InlineData(1.5f, 0.5f)]
    [InlineData(1.75f, 0.25f)]
    [InlineData(2.0f, 0f)]
    [InlineData(3.0f, 0f)]
    public void LoadMod_KneesAt100And200Percent(float load, float expected)
    {
        Assert.Equal(expected, EncumbranceSystem.LoadMod(load), precision: 4);
        Assert.Equal(
            BurdenMath.LoadModifier(load),
            EncumbranceSystem.LoadMod(load),
            precision: 5);
    }
}
