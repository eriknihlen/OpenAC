using AcDream.App.Rendering;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed unsafe class VulkanSwapchain : IDisposable
{
    private readonly Silk.NET.Vulkan.Vk _vk;
    private readonly KhrSurface _surfaceApi;
    private readonly KhrSwapchain _swapchainApi;
    private readonly PhysicalDevice _physicalDevice;
    private readonly Device _device;
    private readonly SurfaceKHR _surface;
    private readonly VulkanQueueFamilyChoice _families;

    private SwapchainKHR _swapchain;
    private Image[] _images = [];
    private ImageView[] _views = [];
    private Semaphore[] _renderComplete = [];
    private bool _disposed;

    internal VulkanSwapchain(
        Silk.NET.Vulkan.Vk vk,
        KhrSurface surfaceApi,
        KhrSwapchain swapchainApi,
        PhysicalDevice physicalDevice,
        Device device,
        SurfaceKHR surface,
        VulkanQueueFamilyChoice families)
    {
        _vk = vk ?? throw new ArgumentNullException(nameof(vk));
        _surfaceApi = surfaceApi ?? throw new ArgumentNullException(nameof(surfaceApi));
        _swapchainApi = swapchainApi ?? throw new ArgumentNullException(nameof(swapchainApi));
        _physicalDevice = physicalDevice;
        _device = device;
        _surface = surface;
        _families = families ?? throw new ArgumentNullException(nameof(families));
    }

    internal VulkanSwapchainConfiguration? Configuration { get; private set; }

    internal bool IsCreated => _swapchain.Handle != 0;

    internal int ImageCount => _images.Length;

    internal ImageView ViewAt(uint index) => _views[index];

    internal Image ImageAt(uint index) => _images[index];

    internal Semaphore RenderCompleteAt(uint index) => _renderComplete[index];

    internal (SurfaceCapabilitiesKHR Capabilities,
        IReadOnlyList<SurfaceFormatKHR> Formats,
        IReadOnlyList<PresentModeKHR> PresentModes) QuerySurface()
    {
        VulkanInterop.Check(
            _surfaceApi.GetPhysicalDeviceSurfaceCapabilities(
                _physicalDevice,
                _surface,
                out SurfaceCapabilitiesKHR capabilities),
            "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

        uint formatCount = 0;
        VulkanInterop.Check(
            _surfaceApi.GetPhysicalDeviceSurfaceFormats(
                _physicalDevice,
                _surface,
                ref formatCount,
                null),
            "vkGetPhysicalDeviceSurfaceFormatsKHR (count)");
        var formats = new SurfaceFormatKHR[formatCount];
        if (formatCount != 0)
        {
            fixed (SurfaceFormatKHR* first = formats)
            {
                VulkanInterop.Check(
                    _surfaceApi.GetPhysicalDeviceSurfaceFormats(
                        _physicalDevice,
                        _surface,
                        ref formatCount,
                        first),
                    "vkGetPhysicalDeviceSurfaceFormatsKHR");
            }
        }

        uint modeCount = 0;
        VulkanInterop.Check(
            _surfaceApi.GetPhysicalDeviceSurfacePresentModes(
                _physicalDevice,
                _surface,
                ref modeCount,
                null),
            "vkGetPhysicalDeviceSurfacePresentModesKHR (count)");
        var modes = new PresentModeKHR[modeCount];
        if (modeCount != 0)
        {
            fixed (PresentModeKHR* first = modes)
            {
                VulkanInterop.Check(
                    _surfaceApi.GetPhysicalDeviceSurfacePresentModes(
                        _physicalDevice,
                        _surface,
                        ref modeCount,
                        first),
                    "vkGetPhysicalDeviceSurfacePresentModesKHR");
            }
        }

        return (capabilities, formats, modes);
    }

    internal bool Recreate(FramePacingPolicy pacing, uint framebufferWidth, uint framebufferHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        (SurfaceCapabilitiesKHR capabilities,
            IReadOnlyList<SurfaceFormatKHR> formats,
            IReadOnlyList<PresentModeKHR> modes) = QuerySurface();

        VulkanSwapchainConfiguration configuration =
            VulkanSwapchainConfigurationFactory.Create(
                capabilities,
                formats,
                modes,
                pacing,
                framebufferWidth,
                framebufferHeight);
        if (!configuration.IsPresentable)
            return false;

        SwapchainKHR old = _swapchain;
        uint* families = stackalloc uint[2]
        {
            _families.GraphicsFamily,
            _families.PresentFamily,
        };
        var create = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = configuration.ImageCount,
            ImageFormat = configuration.ImageFormat,
            ImageColorSpace = configuration.ColorSpace,
            ImageExtent = new Extent2D(configuration.Width, configuration.Height),
            ImageArrayLayers = 1,
            ImageUsage = configuration.Usage,
            ImageSharingMode = _families.IsUnified ? SharingMode.Exclusive : SharingMode.Concurrent,
            QueueFamilyIndexCount = _families.IsUnified ? 0u : 2u,
            PQueueFamilyIndices = _families.IsUnified ? null : families,
            PreTransform = configuration.PreTransform,
            CompositeAlpha = configuration.CompositeAlpha,
            PresentMode = configuration.PresentMode,
            Clipped = true,
            OldSwapchain = old,
        };

        VulkanInterop.Check(
            _swapchainApi.CreateSwapchain(_device, &create, null, out SwapchainKHR created),
            "vkCreateSwapchainKHR");

        DestroyImageResources();
        if (old.Handle != 0)
            _swapchainApi.DestroySwapchain(_device, old, null);

        _swapchain = created;
        Configuration = configuration;
        AcquireImages(configuration);
        return true;
    }

    private void AcquireImages(VulkanSwapchainConfiguration configuration)
    {
        uint count = 0;
        VulkanInterop.Check(
            _swapchainApi.GetSwapchainImages(_device, _swapchain, ref count, null),
            "vkGetSwapchainImagesKHR (count)");
        _images = new Image[count];
        fixed (Image* first = _images)
        {
            VulkanInterop.Check(
                _swapchainApi.GetSwapchainImages(_device, _swapchain, ref count, first),
                "vkGetSwapchainImagesKHR");
        }

        _views = new ImageView[count];
        _renderComplete = new Semaphore[count];
        for (uint i = 0; i < count; i++)
        {
            var viewCreate = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _images[i],
                ViewType = ImageViewType.Type2D,
                Format = configuration.ImageFormat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            VulkanInterop.Check(
                _vk.CreateImageView(_device, &viewCreate, null, out ImageView view),
                "vkCreateImageView (swapchain image)");
            _views[i] = view;

            var semaphoreCreate = new SemaphoreCreateInfo
            {
                SType = StructureType.SemaphoreCreateInfo,
            };
            VulkanInterop.Check(
                _vk.CreateSemaphore(_device, &semaphoreCreate, null, out Semaphore semaphore),
                "vkCreateSemaphore (render complete)");
            _renderComplete[i] = semaphore;
        }
    }

    internal VulkanSwapchainAction TryAcquire(
        Semaphore acquired,
        ulong timeoutNanoseconds,
        out uint imageIndex)
    {
        imageIndex = 0;
        if (!IsCreated)
            return VulkanSwapchainAction.RecreateNow;

        Result result = _swapchainApi.AcquireNextImage(
            _device,
            _swapchain,
            timeoutNanoseconds,
            acquired,
            default,
            ref imageIndex);
        VulkanSwapchainAction action = VulkanSwapchainRecreationPolicy.OnAcquire(result);
        if (action == VulkanSwapchainAction.Fail)
            throw new VulkanCallException("vkAcquireNextImageKHR", result);
        return action;
    }

    internal VulkanSwapchainAction Present(Queue presentQueue, uint imageIndex)
    {
        SwapchainKHR swapchain = _swapchain;
        Semaphore wait = _renderComplete[imageIndex];
        uint index = imageIndex;
        var present = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &wait,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &index,
        };
        Result result = _swapchainApi.QueuePresent(presentQueue, &present);
        VulkanSwapchainAction action = VulkanSwapchainRecreationPolicy.OnPresent(result);
        if (action == VulkanSwapchainAction.Fail)
            throw new VulkanCallException("vkQueuePresentKHR", result);
        return action;
    }

    internal byte[] CaptureImage(
        Queue graphicsQueue,
        uint graphicsFamily,
        uint imageIndex)
    {
        if (Configuration is not { } configuration)
            throw new InvalidOperationException("The swapchain has not been created.");

        uint width = configuration.Width;
        uint height = configuration.Height;
        uint byteCount = width * height * 4;

        VulkanInterop.Check(_vk.DeviceWaitIdle(_device), "vkDeviceWaitIdle (screenshot)");

        Silk.NET.Vulkan.Buffer buffer = default;
        DeviceMemory memory = default;
        CommandPool pool = default;
        try
        {
            var bufferCreate = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = byteCount,
                Usage = BufferUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
            };
            VulkanInterop.Check(
                _vk.CreateBuffer(_device, &bufferCreate, null, out buffer),
                "vkCreateBuffer (screenshot)");
            _vk.GetBufferMemoryRequirements(_device, buffer, out MemoryRequirements requirements);
            uint? typeIndex = VulkanActiveDeviceProbe.FindMemoryType(
                _vk,
                _physicalDevice,
                requirements.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            if (typeIndex is not { } index)
            {
                throw new NotSupportedException(
                    "No host-visible Vulkan memory type is available for screenshot readback.");
            }

            var allocate = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = index,
            };
            VulkanInterop.Check(
                _vk.AllocateMemory(_device, &allocate, null, out memory),
                "vkAllocateMemory (screenshot)");
            VulkanInterop.Check(
                _vk.BindBufferMemory(_device, buffer, memory, 0),
                "vkBindBufferMemory (screenshot)");

            var poolCreate = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = graphicsFamily,
                Flags = CommandPoolCreateFlags.TransientBit,
            };
            VulkanInterop.Check(
                _vk.CreateCommandPool(_device, &poolCreate, null, out pool),
                "vkCreateCommandPool (screenshot)");
            var allocateCommands = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            VulkanInterop.Check(
                _vk.AllocateCommandBuffers(_device, &allocateCommands, out CommandBuffer commands),
                "vkAllocateCommandBuffers (screenshot)");

            RecordCapture(commands, _images[imageIndex], buffer, width, height);

            var commandSubmit = new CommandBufferSubmitInfo
            {
                SType = StructureType.CommandBufferSubmitInfo,
                CommandBuffer = commands,
            };
            var submit = new SubmitInfo2
            {
                SType = StructureType.SubmitInfo2,
                CommandBufferInfoCount = 1,
                PCommandBufferInfos = &commandSubmit,
            };
            VulkanInterop.Check(
                _vk.QueueSubmit2(graphicsQueue, 1, &submit, default),
                "vkQueueSubmit2 (screenshot)");
            VulkanInterop.Check(
                _vk.QueueWaitIdle(graphicsQueue),
                "vkQueueWaitIdle (screenshot)");

            void* mapped = null;
            VulkanInterop.Check(
                _vk.MapMemory(_device, memory, 0, byteCount, 0, &mapped),
                "vkMapMemory (screenshot)");
            try
            {
                return VulkanBackbufferSwizzle.ToGlOriginRgba(
                    new ReadOnlySpan<byte>(mapped, (int)byteCount),
                    (int)width,
                    (int)height,
                    (int)width * 4);
            }
            finally
            {
                _vk.UnmapMemory(_device, memory);
            }
        }
        finally
        {
            if (pool.Handle != 0)
                _vk.DestroyCommandPool(_device, pool, null);
            if (buffer.Handle != 0)
                _vk.DestroyBuffer(_device, buffer, null);
            if (memory.Handle != 0)
                _vk.FreeMemory(_device, memory, null);
        }
    }

    private void RecordCapture(
        CommandBuffer commands,
        Image image,
        Silk.NET.Vulkan.Buffer destination,
        uint width,
        uint height)
    {
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        VulkanInterop.Check(
            _vk.BeginCommandBuffer(commands, &begin),
            "vkBeginCommandBuffer (screenshot)");

        var subresource = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };
        TransitionImage(
            commands,
            image,
            subresource,
            ImageLayout.PresentSrcKhr,
            ImageLayout.TransferSrcOptimal,
            PipelineStageFlags2.AllCommandsBit,
            AccessFlags2.None,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferReadBit);

        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(width, height, 1),
        };
        _vk.CmdCopyImageToBuffer(
            commands,
            image,
            ImageLayout.TransferSrcOptimal,
            destination,
            1,
            &region);

        TransitionImage(
            commands,
            image,
            subresource,
            ImageLayout.TransferSrcOptimal,
            ImageLayout.PresentSrcKhr,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferReadBit,
            PipelineStageFlags2.AllCommandsBit,
            AccessFlags2.None);

        VulkanInterop.Check(
            _vk.EndCommandBuffer(commands),
            "vkEndCommandBuffer (screenshot)");
    }

    /// <summary>One batched <c>vkCmdPipelineBarrier2</c> image transition.</summary>
    internal void TransitionImage(
        CommandBuffer commands,
        Image image,
        ImageSubresourceRange subresource,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        PipelineStageFlags2 sourceStage,
        AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStage,
        AccessFlags2 destinationAccess)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = sourceStage,
            SrcAccessMask = sourceAccess,
            DstStageMask = destinationStage,
            DstAccessMask = destinationAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = subresource,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _vk.CmdPipelineBarrier2(commands, &dependency);
    }

    private void DestroyImageResources()
    {
        foreach (Semaphore semaphore in _renderComplete)
        {
            if (semaphore.Handle != 0)
                _vk.DestroySemaphore(_device, semaphore, null);
        }

        foreach (ImageView view in _views)
        {
            if (view.Handle != 0)
                _vk.DestroyImageView(_device, view, null);
        }

        _renderComplete = [];
        _views = [];
        _images = [];
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        DestroyImageResources();
        if (_swapchain.Handle != 0)
        {
            _swapchainApi.DestroySwapchain(_device, _swapchain, null);
            _swapchain = default;
        }

        Configuration = null;
    }
}
