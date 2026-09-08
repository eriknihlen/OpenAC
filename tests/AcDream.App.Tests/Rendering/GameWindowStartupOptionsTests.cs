using AcDream.App.Rendering;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace AcDream.App.Tests.Rendering;

public sealed class GameWindowStartupOptionsTests
{
    [Fact]
    public void OrdinaryStartupPreservesCurrentDecorated1280By720Window()
    {
        WindowOptions defaults = WindowOptions.DefaultVulkan;

        WindowOptions options = GameWindow.CreateStartupWindowOptions(
            exactAutomationFramebuffer: false,
            persistedResolution: "3840x2160",
            useVSync: true);

        Assert.Equal(new Vector2D<int>(1280, 720), options.Size);
        Assert.Equal(defaults.WindowBorder, options.WindowBorder);
        Assert.Equal(defaults.IsVisible, options.IsVisible);
        Assert.True(options.VSync);
    }

    [Theory]
    [InlineData("2560x1440", 2560, 1440)]
    [InlineData("3840x2160", 3840, 2160)]
    public void ExactAutomationStartupUsesRequestedBorderlessClientExtent(
        string resolution,
        int width,
        int height)
    {
        WindowOptions options = GameWindow.CreateStartupWindowOptions(
            exactAutomationFramebuffer: true,
            persistedResolution: resolution,
            useVSync: false);

        Assert.Equal(new Vector2D<int>(width, height), options.Size);
        Assert.Equal(WindowBorder.Hidden, options.WindowBorder);
        Assert.False(options.IsVisible);
        Assert.False(options.VSync);
    }

    [Fact]
    public void ExactAutomationStartupRejectsInvalidResolution()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            GameWindow.CreateStartupWindowOptions(
                exactAutomationFramebuffer: true,
                persistedResolution: "invalid",
                useVSync: false));

        Assert.Equal(
            "Exact automation framebuffer requires a valid persisted resolution.",
            error.Message);
    }
}
