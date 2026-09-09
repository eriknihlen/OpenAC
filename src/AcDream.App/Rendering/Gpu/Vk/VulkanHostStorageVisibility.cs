using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace AcDream.App.Rendering.Gpu.Vk;

internal static class VulkanHostStorageVisibility
{
    internal static BufferMemoryBarrier2 Create(Buffer buffer, ulong sizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sizeBytes);
        return new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.HostBit,
            SrcAccessMask = AccessFlags2.HostWriteBit,
            DstStageMask = PipelineStageFlags2.VertexShaderBit,
            DstAccessMask = AccessFlags2.ShaderReadBit,
            SrcQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
            Buffer = buffer,
            Offset = 0,
            Size = sizeBytes,
        };
    }
}
