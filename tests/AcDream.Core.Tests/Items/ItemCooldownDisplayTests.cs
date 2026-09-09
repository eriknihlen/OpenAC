using AcDream.Core.Items;
namespace AcDream.Core.Tests.Items;

public sealed class ItemCooldownDisplayTests
{
    [Theory]
    [InlineData(10.0, 10.0, 10)]
    [InlineData(10.0, 9.0, 10)]
    [InlineData(10.0, 8.999, 9)]
    [InlineData(10.0, 5.0, 6)]
    [InlineData(10.0, 0.001, 1)]
    [InlineData(10.0, 0.0, 0)]
    [InlineData(0.0, 5.0, 0)]
    public void GetOverlayStep_matches_retail_truncation(
        double duration,
        double remaining,
        int expected)
        => Assert.Equal(
            expected,
            ItemCooldownDisplay.GetOverlayStep(duration, remaining));

}
