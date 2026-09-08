using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;

namespace AcDream.App.Tests.Rendering;

public sealed class PrivateViewportFlightTargetsTests
{
    [Fact]
    public void FlightSlotsOwnDistinctTargetsAndPublishOnlyCompletedScenes()
    {
        using var device = new RecordingGpuDevice();
        using var targets =
            new PrivateEntityViewportRenderer.PrivateViewportFlightTargets(
                device,
                "paperdoll");

        var first = Assert.IsType<
            PrivateEntityViewportRenderer.PrivateViewportFlightTargets.TargetSlot>(
                targets.Ensure(0, 120, 180));
        var second = Assert.IsType<
            PrivateEntityViewportRenderer.PrivateViewportFlightTargets.TargetSlot>(
                targets.Ensure(1, 120, 180));

        Assert.NotSame(first.Target, second.Target);
        Assert.NotEqual(first.TextureSlot, second.TextureSlot);
        Assert.Equal(2, targets.AllocatedSlotCount);
        Assert.Equal(0u, targets.CompletedHandle(0));
        Assert.Equal(0u, targets.CompletedHandle(1));

        first.HasRenderedScene = true;

        Assert.NotEqual(0u, targets.CompletedHandle(0));
        Assert.Equal(0u, targets.CompletedHandle(1));
        Assert.Same(first, targets.Ensure(0, 120, 180));

        targets.InvalidateCompletedScenes();

        Assert.Equal(0u, targets.CompletedHandle(0));
        Assert.Equal(2, targets.AllocatedSlotCount);
    }

    [Fact]
    public void ResizeRetiresEveryFlightTargetBeforeCreatingTheNewExtent()
    {
        using var device = new RecordingGpuDevice();
        using var targets =
            new PrivateEntityViewportRenderer.PrivateViewportFlightTargets(
                device,
                "paperdoll");
        var first = targets.Ensure(0, 120, 180)!;
        var second = targets.Ensure(1, 120, 180)!;
        var firstTarget = Assert.IsType<RecordingGpuRenderTarget>(first.Target);
        var secondTarget = Assert.IsType<RecordingGpuRenderTarget>(second.Target);

        var resized = targets.Ensure(1, 160, 220)!;

        Assert.True(firstTarget.IsDisposed);
        Assert.True(secondTarget.IsDisposed);
        Assert.Equal(1, targets.AllocatedSlotCount);
        Assert.Equal(160, resized.Target.Description.Width);
        Assert.Equal(220, resized.Target.Description.Height);
        Assert.Equal(3, device.CreatedRenderTargets.Count);
    }
}
