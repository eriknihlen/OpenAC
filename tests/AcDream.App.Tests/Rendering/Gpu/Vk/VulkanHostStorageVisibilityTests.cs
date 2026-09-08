using AcDream.App.Rendering.Gpu.Vk;
using Silk.NET.Vulkan;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanHostStorageVisibilityTests
{
    [Fact]
    public void RetainedTransformBarrier_PublishesHostWritesToVertexShaderReads()
    {
        BufferMemoryBarrier2 barrier = VulkanHostStorageVisibility.Create(
            default,
            4u * 1024u * 1024u);

        Assert.Equal(StructureType.BufferMemoryBarrier2, barrier.SType);
        Assert.Equal(PipelineStageFlags2.HostBit, barrier.SrcStageMask);
        Assert.Equal(AccessFlags2.HostWriteBit, barrier.SrcAccessMask);
        Assert.Equal(PipelineStageFlags2.VertexShaderBit, barrier.DstStageMask);
        Assert.Equal(AccessFlags2.ShaderReadBit, barrier.DstAccessMask);
        Assert.Equal(Silk.NET.Vulkan.Vk.QueueFamilyIgnored, barrier.SrcQueueFamilyIndex);
        Assert.Equal(Silk.NET.Vulkan.Vk.QueueFamilyIgnored, barrier.DstQueueFamilyIndex);
        Assert.Equal(0ul, barrier.Offset);
        Assert.Equal(4ul * 1024ul * 1024ul, barrier.Size);
    }

    [Fact]
    public void RetainedTransformBarrier_RejectsAnEmptyBinding()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VulkanHostStorageVisibility.Create(default, 0));
    }
}
