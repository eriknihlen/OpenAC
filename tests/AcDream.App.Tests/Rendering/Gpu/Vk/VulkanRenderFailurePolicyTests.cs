using AcDream.App.Rendering.Gpu.Vk;
using Silk.NET.Vulkan;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanRenderFailurePolicyTests
{
    [Fact]
    public void ManagedOutOfMemoryAndNestedFatalFailuresAreTerminal()
    {
        Assert.True(VulkanRenderFailurePolicy.IsFatal(new OutOfMemoryException("managed")));
        Assert.True(VulkanRenderFailurePolicy.IsFatal(new InvalidOperationException(
            "wrapper",
            new VulkanCallException("nested", Result.ErrorDeviceLost))));
        Assert.True(VulkanRenderFailurePolicy.IsFatal(new AggregateException(
            new InvalidOperationException("ordinary"),
            new VulkanCallException("nested", Result.ErrorOutOfDeviceMemory))));
    }

    [Theory]
    [InlineData(Result.ErrorDeviceLost)]
    [InlineData(Result.ErrorOutOfHostMemory)]
    [InlineData(Result.ErrorOutOfDeviceMemory)]
    [InlineData(Result.ErrorSurfaceLostKhr)]
    public void TerminalVulkanResultsCannotFallBackToAnotherGraph(Result result) =>
        Assert.True(VulkanRenderFailurePolicy.IsFatal(
            new VulkanCallException("test", result)));

    [Fact]
    public void OrdinaryPackAndNonTerminalVulkanFailuresMayBeQuarantined()
    {
        Assert.False(VulkanRenderFailurePolicy.IsFatal(
            new InvalidOperationException("pack bug")));
        Assert.False(VulkanRenderFailurePolicy.IsFatal(
            new VulkanCallException("pack pipeline", Result.ErrorFormatNotSupported)));
    }
}
