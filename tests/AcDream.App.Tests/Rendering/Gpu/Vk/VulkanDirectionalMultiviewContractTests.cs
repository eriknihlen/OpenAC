using AcDream.App.Rendering.Gpu.Vk;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanDirectionalMultiviewContractTests
{
    [Fact]
    public void LowMaskCoversBothAttachmentLayersInOneBarrierRange()
    {
        VulkanDirectionalMultiviewRange range =
            VulkanDirectionalMultiviewContract.Resolve(0b11, 2);

        Assert.Equal(0u, range.BaseLayer);
        Assert.Equal(2u, range.LayerCount);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0b01u)]
    [InlineData(0b10u)]
    [InlineData(0b111u)]
    public void PartialOrExtraMaskFailsBeforeRecording(uint viewMask)
    {
        Assert.Throws<NotSupportedException>(() =>
            VulkanDirectionalMultiviewContract.Resolve(viewMask, 2));
    }
}
