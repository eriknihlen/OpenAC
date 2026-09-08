using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Tests.Rendering.Gpu;

namespace AcDream.App.Tests.Rendering;

public sealed class PortalDepthMaskRetailStateTests
{
    [Fact]
    public void SealAndPunchUseOneRetailAlwaysWritePipelineAndOneDrawEach()
    {
        using var device = new RecordingGpuDevice();
        var frames = new GpuDeviceFrameLifetime(device);
        var scope = new VulkanWorldPassScope(sampleCount: 1);
        using var renderer = new PortalDepthMaskRenderer(device, frames, scope);

        RecordingGpuPipeline pipeline = Assert.Single(device.CreatedPipelines);
        Assert.Equal("portal-depth-write", pipeline.Description.Name);
        Assert.Equal(
            new GpuDepthState(Test: true, Write: true, GpuCompareOp.Always),
            pipeline.Description.Depth);
        Assert.False(pipeline.Description.StencilTest);
        Assert.False(pipeline.Description.ColorWrite);
        Assert.Equal(GpuCullMode.None, pipeline.Description.Cull);

        frames.BeginFrame();
        renderer.BeginFrame(frameSlot: 0);
        using (IGpuPassEncoder pass = frames.CurrentFrame!.BeginPass(
                   GpuPassDescription.BackbufferClear(
                       "portal-depth-retail-state",
                       Vector4.Zero,
                       sampleCount: 1)))
        using (scope.Publish(pass))
        {
            Vector3[] triangle =
            [
                new(-1f, -1f, 1f),
                new(1f, -1f, 1f),
                new(0f, 1f, 1f),
            ];
            renderer.DrawDepthFan(
                triangle,
                Matrix4x4.Identity,
                ReadOnlySpan<Vector4>.Empty,
                forceFarZ: true);
            renderer.DrawDepthFan(
                triangle,
                Matrix4x4.Identity,
                ReadOnlySpan<Vector4>.Empty,
                forceFarZ: false);
        }
        frames.EndFrame();

        Assert.Equal(
            2,
            device.Calls.OfType<GpuRecordedPipelineBind>()
                .Count(call => call.PipelineName == "portal-depth-write"));
        Assert.Equal(2, device.Calls.OfType<GpuRecordedDraw>().Count());
        Assert.Collection(
            device.Calls.OfType<GpuRecordedPushConstants>(),
            punch => Assert.Equal(1, punch.Constants.RenderPass),
            seal => Assert.Equal(0, seal.Constants.RenderPass));
        Assert.Empty(device.Calls.OfType<GpuRecordedStencil>());
    }
}
