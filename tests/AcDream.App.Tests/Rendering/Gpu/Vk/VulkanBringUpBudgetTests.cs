using AcDream.App.Rendering.Gpu.Vk;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanBringUpBudgetTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveBudgetNeverRetires(int budget)
    {
        Assert.False(
            VulkanBringUpHost.ShouldRetire(
                budget,
                presentedFrames: 10_000,
                capturesScreenshot: true,
                screenshotRequested: true));
    }

    [Fact]
    public void TheLoopRunsUntilTheBudgetIsReached()
    {
        Assert.False(
            VulkanBringUpHost.ShouldRetire(
                frameBudget: 30,
                presentedFrames: 29,
                capturesScreenshot: false,
                screenshotRequested: false));
        Assert.True(
            VulkanBringUpHost.ShouldRetire(
                frameBudget: 30,
                presentedFrames: 30,
                capturesScreenshot: false,
                screenshotRequested: false));
    }

    [Fact]
    public void ARunThatOwesAScreenshotStaysOpenUntilItHasBeenAttempted()
    {
        Assert.False(
            VulkanBringUpHost.ShouldRetire(
                frameBudget: 2,
                presentedFrames: 2,
                capturesScreenshot: true,
                screenshotRequested: false));
        Assert.True(
            VulkanBringUpHost.ShouldRetire(
                frameBudget: 2,
                presentedFrames: 4,
                capturesScreenshot: true,
                screenshotRequested: true));
    }

    /// <summary>
    /// With no artifact directory there is no PNG to wait for, so the budget is
    /// the only term.
    /// </summary>
    [Fact]
    public void ARunWithNoArtifactDirectoryRetiresOnTheBudgetAlone()
    {
        Assert.True(
            VulkanBringUpHost.ShouldRetire(
                frameBudget: 1,
                presentedFrames: 1,
                capturesScreenshot: false,
                screenshotRequested: false));
    }
}
