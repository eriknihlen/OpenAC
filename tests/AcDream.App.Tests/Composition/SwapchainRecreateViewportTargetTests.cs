using AcDream.App.Composition;
using AcDream.App.Input;
using AcDream.App.Rendering;

namespace AcDream.App.Tests.Composition;

public sealed class SwapchainRecreateViewportTargetTests
{
    [Fact]
    public void ResizeViewport_ArmsRecreation_OncePerEvent()
    {
        int armed = 0;
        IFramebufferViewportTarget target =
            new VulkanHostInputCameraCompositionFactory.SwapchainRecreateViewportTarget(
                () => armed++);

        target.ResizeViewport(1024, 768);
        Assert.Equal(1, armed);

        target.ResizeViewport(800, 600);
        Assert.Equal(2, armed);
    }

    [Fact]
    public void ResizeViewport_IgnoresTheEventSize_ContextReadsLiveFramebuffer()
    {
        int armed = 0;
        IFramebufferViewportTarget target =
            new VulkanHostInputCameraCompositionFactory.SwapchainRecreateViewportTarget(
                () => armed++);

        target.ResizeViewport(int.MaxValue, 1);
        Assert.Equal(1, armed);
    }

    [Fact]
    public void Constructor_RejectsNullRecreateHook()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new VulkanHostInputCameraCompositionFactory.SwapchainRecreateViewportTarget(null!));
    }

    [Fact]
    public void ResizeController_DeliversResizeEventsToTheBoundTarget()
    {
        int armed = 0;
        var controller = new FramebufferResizeController(new ViewportAspectState());
        controller.BindViewport(
            new VulkanHostInputCameraCompositionFactory.SwapchainRecreateViewportTarget(
                () => armed++));

        controller.Resize(1280, 720);
        Assert.Equal(1, armed);

        controller.Resize(0, 0);       // minimised — gated, no arm
        Assert.Equal(1, armed);

        controller.Resize(1024, 768);
        Assert.Equal(2, armed);
    }
}
