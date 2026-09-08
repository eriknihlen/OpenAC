using AcDream.App.Rendering.Packs;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class RenderPackResidentBudgetTests
{
    private const long LowAt1080p = 64L * 1024 * 1024;
    private const long Unlimited = long.MaxValue;

    [Theory]
    [InlineData(1920, 1080, LowAt1080p)]
    [InlineData(1280, 720, LowAt1080p)]
    [InlineData(1600, 900, LowAt1080p)]
    public void AtOrBelowTheReferenceResolutionTheDeclaredCeilingApplies(int width, int height, long expected)
    {
        Assert.Equal(expected, RenderPackResidentBudget.Effective(LowAt1080p, width, height, Unlimited));
    }

    [Fact]
    public void At1440pTheCeilingScalesByPixelCount()
    {
        long effective = RenderPackResidentBudget.Effective(LowAt1080p, 2560, 1440, Unlimited);
        Assert.Equal((long)Math.Ceiling(LowAt1080p * 16.0 / 9.0), effective);
        Assert.True(effective >= 67_368_164L);
    }

    [Fact]
    public void At4KTheCeilingIsFourTimesTheDeclaredFigure()
    {
        Assert.Equal(4L * LowAt1080p, RenderPackResidentBudget.Effective(LowAt1080p, 3840, 2160, Unlimited));
    }

    [Fact]
    public void TheHardwareCapStillWins()
    {
        Assert.Equal(
            100_000_000L,
            RenderPackResidentBudget.Effective(LowAt1080p, 3840, 2160, hardwareCapBytes: 100_000_000L));
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    public void AZeroExtentIsRejected(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RenderPackResidentBudget.Effective(LowAt1080p, width, height, Unlimited));
    }
}
