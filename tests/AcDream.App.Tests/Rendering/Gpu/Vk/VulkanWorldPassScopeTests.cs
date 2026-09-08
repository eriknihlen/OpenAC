using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Tests.Rendering.Gpu;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanWorldPassScopeTests
{
    [Fact]
    public void OrdinaryPublicationStartsWithEmptyFrameSections()
    {
        using var device = new RecordingGpuDevice();
        using IGpuBuffer buffer = Buffer(device);
        var scope = new VulkanWorldPassScope(sampleCount: 1);
        scope.Sections.SceneLighting = new GpuBufferSection(buffer, 256, 576);

        using (scope.Publish(Encoder(device)))
            Assert.False(scope.Sections.SceneLighting.IsValid);

        Assert.False(scope.Sections.SceneLighting.IsValid);
    }

    [Fact]
    public void PreparedPublicationPreservesCurrentFrameSectionsUntilDispose()
    {
        using var device = new RecordingGpuDevice();
        using IGpuBuffer buffer = Buffer(device);
        var scope = new VulkanWorldPassScope(sampleCount: 1);
        var lighting = new GpuBufferSection(buffer, 256, 576);
        scope.Sections.SceneLighting = lighting;

        using (scope.PublishPrepared(Encoder(device)))
            Assert.Equal(lighting, scope.Sections.SceneLighting);

        Assert.False(scope.Sections.SceneLighting.IsValid);
    }

    private static IGpuBuffer Buffer(RecordingGpuDevice device) =>
        device.CreateBuffer(new GpuBufferDescription(
            "prepared-lighting",
            1024,
            GpuBufferUsage.Uniform,
            GpuMemoryResidency.HostWritable));

    private static IGpuPassEncoder Encoder(RecordingGpuDevice device) =>
        new RecordingGpuPassEncoder(
            device,
            new GpuPassDescription
            {
                Name = "world",
                Color = new GpuColorAttachment(
                    Target: null,
                    GpuLoadOp.Clear,
                    GpuStoreOp.Store,
                    default),
                Depth = null,
                SampleCount = 1,
            });
}
