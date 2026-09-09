using AcDream.App.Rendering.Gpu.Vk;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanDrawBindingStateTests
{
    [Fact]
    public void FirstDrawBinds_IdenticalDrawReuses_ChangedInputsRebind()
    {
        var state = new VulkanDrawBindingState();

        Assert.True(state.RequiresBind(pipelineLayout: 10, packGeneration: 3));
        state.MarkBound(pipelineLayout: 10, packGeneration: 3);
        Assert.False(state.RequiresBind(pipelineLayout: 10, packGeneration: 3));

        state.MarkDirty();
        Assert.True(state.RequiresBind(pipelineLayout: 10, packGeneration: 3));
        state.MarkBound(pipelineLayout: 10, packGeneration: 3);
        Assert.True(state.RequiresBind(pipelineLayout: 11, packGeneration: 3));
        Assert.True(state.RequiresBind(pipelineLayout: 10, packGeneration: 4));
    }

    [Fact]
    public void PackAndRetailLayoutsCannotReuseEachOthersBinding()
    {
        var state = new VulkanDrawBindingState();

        state.MarkBound(pipelineLayout: 20, packGeneration: 7);

        Assert.True(state.RequiresBind(pipelineLayout: 21, packGeneration: 0));
        state.MarkBound(pipelineLayout: 21, packGeneration: 0);
        Assert.True(state.RequiresBind(pipelineLayout: 20, packGeneration: 7));
    }

    [Fact]
    public void WarmChecksAllocateZero()
    {
        var state = new VulkanDrawBindingState();
        state.MarkBound(pipelineLayout: 30, packGeneration: 8);

        ZeroAllocationProbe.AssertAllocatesNothing(
            "VulkanDrawBindingState.RequiresBind",
            () => _ = state.RequiresBind(
                pipelineLayout: 30,
                packGeneration: 8),
            batchSize: 10_000);
    }
}
