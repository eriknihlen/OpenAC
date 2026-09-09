using System.Reflection;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu.Vk;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed unsafe class VulkanSwapchainFailureTests
{
    [Theory]
    [InlineData(Result.ErrorDeviceLost)]
    [InlineData(Result.ErrorSurfaceLostKhr)]
    [InlineData((Result)(-123456789))]
    public void AcquireFatalResult_ThrowsOriginalNativeFailure(Result result)
    {
        using var fixture = new Fixture();
        fixture.Native.AcquireResult = result;

        VulkanCallException error = Assert.Throws<VulkanCallException>(() =>
            fixture.Swapchain.TryAcquire(Acquired, Timeout, out _));

        Assert.Equal("vkAcquireNextImageKHR", error.Operation);
        Assert.Equal(result, error.Result);
        AssertAcquireCall(fixture.Native);
        Assert.Empty(fixture.Native.Presents);
    }

    [Theory]
    [InlineData(Result.ErrorDeviceLost)]
    [InlineData(Result.ErrorSurfaceLostKhr)]
    [InlineData((Result)(-123456789))]
    public void PresentFatalResult_ThrowsOriginalNativeFailure(Result result)
    {
        using var fixture = new Fixture();
        fixture.Native.PresentResult = result;

        VulkanCallException error = Assert.Throws<VulkanCallException>(() =>
            fixture.Swapchain.Present(PresentQueue, NativeImageIndex));

        Assert.Equal("vkQueuePresentKHR", error.Operation);
        Assert.Equal(result, error.Result);
        AssertPresentCall(fixture.Native);
        Assert.Empty(fixture.Native.Acquisitions);
    }

    [Theory]
    [InlineData(Result.Success, (int)VulkanSwapchainAction.Continue)]
    [InlineData(Result.SuboptimalKhr, (int)VulkanSwapchainAction.RecreateAtFrameBoundary)]
    [InlineData(Result.Timeout, (int)VulkanSwapchainAction.Idle)]
    [InlineData(Result.NotReady, (int)VulkanSwapchainAction.Idle)]
    [InlineData(Result.ErrorOutOfDateKhr, (int)VulkanSwapchainAction.RecreateNow)]
    public void AcquireNonfatalResult_PreservesActionAndUsableImageIndex(Result result, int action)
    {
        using var fixture = new Fixture();
        fixture.Native.AcquireResult = result;

        Assert.Equal((VulkanSwapchainAction)action,
            fixture.Swapchain.TryAcquire(Acquired, Timeout, out uint imageIndex));

        AssertAcquireCall(fixture.Native);
        if (result is Result.Success or Result.SuboptimalKhr)
            Assert.Equal(NativeImageIndex, imageIndex);
    }

    [Theory]
    [InlineData(Result.Success, (int)VulkanSwapchainAction.Continue)]
    [InlineData(Result.SuboptimalKhr, (int)VulkanSwapchainAction.RecreateAtFrameBoundary)]
    [InlineData(Result.ErrorOutOfDateKhr, (int)VulkanSwapchainAction.RecreateNow)]
    public void PresentNonfatalResult_PreservesAction(Result result, int action)
    {
        using var fixture = new Fixture();
        fixture.Native.PresentResult = result;

        Assert.Equal((VulkanSwapchainAction)action,
            fixture.Swapchain.Present(PresentQueue, NativeImageIndex));

        AssertPresentCall(fixture.Native);
    }

    [Fact]
    public void UncreatedSwapchain_ReturnsRecreateWithoutNativeAcquire()
    {
        using var fixture = new Fixture(created: false);
        fixture.Native.AcquireResult = Result.ErrorDeviceLost;

        Assert.False(fixture.Swapchain.IsCreated);
        Assert.Equal(VulkanSwapchainAction.RecreateNow,
            fixture.Swapchain.TryAcquire(Acquired, Timeout, out _));
        Assert.Empty(fixture.Native.Acquisitions);
        Assert.Empty(fixture.Native.Presents);
    }

    private static readonly Device Device = new((nint)0x4771);
    private static readonly SwapchainKHR Handle = new(0x4772ul);
    private static readonly VkSemaphore Acquired = new(0x4773ul);
    private static readonly Queue PresentQueue = new((nint)0x4774);
    private static readonly VkSemaphore RenderComplete = new(0x4775ul);
    private const ulong Timeout = 1_234_567ul;
    private const uint NativeImageIndex = 2u;

    private static void AssertAcquireCall(RecordingNativeContext native)
    {
        AcquireCall call = Assert.Single(native.Acquisitions);
        Assert.Equal(Device, call.Device);
        Assert.Equal(Handle, call.Swapchain);
        Assert.Equal(Timeout, call.Timeout);
        Assert.Equal(Acquired, call.Semaphore);
        Assert.Equal(default, call.Fence);
    }

    private static void AssertPresentCall(RecordingNativeContext native)
    {
        PresentCall call = Assert.Single(native.Presents);
        Assert.Equal(PresentQueue, call.Queue);
        Assert.Equal(StructureType.PresentInfoKhr, call.Type);
        Assert.Equal(1u, call.WaitCount);
        Assert.Equal(RenderComplete, call.Wait);
        Assert.Equal(1u, call.SwapchainCount);
        Assert.Equal(Handle, call.Swapchain);
        Assert.Equal(NativeImageIndex, call.ImageIndex);
    }

    private sealed class Fixture : IDisposable
    {
        internal RecordingNativeContext Native { get; } = new();
        private readonly Silk.NET.Vulkan.Vk _vk;
        private readonly KhrSurface _surface;
        private readonly KhrSwapchain _swapchainApi;
        internal VulkanSwapchain Swapchain { get; }

        internal Fixture(bool created = true)
        {
            _vk = new Silk.NET.Vulkan.Vk(Native);
            _surface = new KhrSurface(Native);
            _swapchainApi = new KhrSwapchain(Native);
            Swapchain = new VulkanSwapchain(_vk, _surface, _swapchainApi,
                new PhysicalDevice((nint)0x4776), Device, new SurfaceKHR(0x4777ul),
                new VulkanQueueFamilyChoice(0, 0));
            SetField(Swapchain, "_swapchain", created ? Handle : default(SwapchainKHR));
            SetField(Swapchain, "_renderComplete", new VkSemaphore[]
            {
                new(0x4778ul), new(0x4779ul), RenderComplete,
            });
        }

        public void Dispose()
        {
            _swapchainApi.Dispose();
            _surface.Dispose();
            _vk.Dispose();
            Native.Dispose();
        }

        private static void SetField(object target, string name, object value) =>
            (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Missing {name}."))
            .SetValue(target, value);
    }

    private readonly record struct AcquireCall(
        Device Device, SwapchainKHR Swapchain, ulong Timeout, VkSemaphore Semaphore, Fence Fence);
    private readonly record struct PresentCall(
        Queue Queue, StructureType Type, uint WaitCount, VkSemaphore Wait,
        uint SwapchainCount, SwapchainKHR Swapchain, uint ImageIndex);

    private sealed class RecordingNativeContext : INativeContext
    {
        private static RecordingNativeContext? s_active;
        internal Result AcquireResult { get; set; } = Result.Success;
        internal Result PresentResult { get; set; } = Result.Success;
        internal List<AcquireCall> Acquisitions { get; } = [];
        internal List<PresentCall> Presents { get; } = [];

        internal RecordingNativeContext()
        {
            Assert.Null(s_active);
            s_active = this;
        }

        public nint GetProcAddress(string proc, int? slot = null) => proc switch
        {
            "vkAcquireNextImageKHR" =>
                (nint)(delegate* unmanaged<Device, SwapchainKHR, ulong, VkSemaphore, Fence, uint*, Result>)
                    &AcquireNextImage,
            "vkQueuePresentKHR" =>
                (nint)(delegate* unmanaged<Queue, PresentInfoKHR*, Result>)&QueuePresent,
            _ => (nint)(delegate* unmanaged<void>)&NoOp,
        };

        public bool TryGetProcAddress(string proc, out nint addr, int? slot = null)
        {
            addr = GetProcAddress(proc, slot);
            return true;
        }

        public void Dispose()
        {
            if (ReferenceEquals(s_active, this))
                s_active = null;
        }

        [UnmanagedCallersOnly]
        private static Result AcquireNextImage(
            Device device, SwapchainKHR swapchain, ulong timeout,
            VkSemaphore semaphore, Fence fence, uint* imageIndex)
        {
            RecordingNativeContext active = s_active!;
            active.Acquisitions.Add(new AcquireCall(device, swapchain, timeout, semaphore, fence));
            if (active.AcquireResult is Result.Success or Result.SuboptimalKhr)
                *imageIndex = NativeImageIndex;
            return active.AcquireResult;
        }

        [UnmanagedCallersOnly]
        private static Result QueuePresent(Queue queue, PresentInfoKHR* present)
        {
            RecordingNativeContext active = s_active!;
            active.Presents.Add(new PresentCall(queue, present->SType,
                present->WaitSemaphoreCount, present->PWaitSemaphores[0],
                present->SwapchainCount, present->PSwapchains[0], present->PImageIndices[0]));
            return active.PresentResult;
        }

        [UnmanagedCallersOnly]
        private static void NoOp()
        {
        }
    }
}
