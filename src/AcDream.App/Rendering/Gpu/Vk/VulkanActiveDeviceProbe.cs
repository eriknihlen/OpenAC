using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace AcDream.App.Rendering.Gpu.Vk;

internal static unsafe class VulkanActiveDeviceProbe
{
    /// <summary>Edge of the offscreen target. Small enough to be free, large enough to be a real image.</summary>
    internal const uint ProbeExtent = 64;

    internal static ReadOnlySpan<byte> ExpectedClearRgba => [0x33, 0x77, 0xBB, 0xEE];

    internal static VulkanFunctionProbeResult Run(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice physicalDevice,
        Device device,
        Queue graphicsQueue,
        uint graphicsFamily)
    {
        ArgumentNullException.ThrowIfNull(vk);
        var failures = new List<string>();

        bool descriptorLayout = false;
        bool pushConstantLayout = false;
        bool dynamicRendering = false;
        bool timelineWait = false;
        bool hostQueryReset = false;
        bool readback = false;

        DescriptorSetLayout storageLayout = default;
        DescriptorSetLayout uniformLayout = default;
        DescriptorSetLayout tableLayout = default;
        PipelineLayout pipelineLayout = default;

        try
        {
            descriptorLayout = Attempt(
                "descriptor-indexing set layouts",
                () =>
                {
                    storageLayout = VulkanPipelineLayouts.CreateStorageSetLayout(vk, device);
                    uniformLayout = VulkanPipelineLayouts.CreateUniformSetLayout(vk, device);
                    tableLayout = VulkanPipelineLayouts.CreateTextureTableSetLayout(vk, device);
                },
                failures);

            if (descriptorLayout)
            {
                pushConstantLayout = Attempt(
                    "three-set pipeline layout with the 96-byte push-constant block",
                    () => pipelineLayout = VulkanPipelineLayouts.CreatePipelineLayout(
                        vk,
                        device,
                        storageLayout,
                        uniformLayout,
                        tableLayout),
                    failures);
            }

            hostQueryReset = Attempt(
                "host timestamp query-pool reset",
                () => ProbeHostQueryReset(vk, device),
                failures);

            byte[]? pixels = null;
            dynamicRendering = Attempt(
                "dynamic-rendering clear, synchronization2 barriers and timeline submit",
                () => pixels = RenderAndReadBack(vk, physicalDevice, device, graphicsQueue, graphicsFamily),
                failures);
            timelineWait = dynamicRendering;

            if (dynamicRendering && pixels is not null)
            {
                readback = Attempt(
                    "offscreen readback pixel comparison",
                    () => VerifyClearColour(pixels),
                    failures);
            }
        }
        finally
        {
            if (pipelineLayout.Handle != 0)
                vk.DestroyPipelineLayout(device, pipelineLayout, null);
            if (tableLayout.Handle != 0)
                vk.DestroyDescriptorSetLayout(device, tableLayout, null);
            if (uniformLayout.Handle != 0)
                vk.DestroyDescriptorSetLayout(device, uniformLayout, null);
            if (storageLayout.Handle != 0)
                vk.DestroyDescriptorSetLayout(device, storageLayout, null);
        }

        return new VulkanFunctionProbeResult(
            DeviceCreation: true,
            DescriptorIndexingLayout: descriptorLayout,
            PushConstantLayout: pushConstantLayout,
            DynamicRenderingClear: dynamicRendering,
            TimelineSemaphoreWait: timelineWait,
            HostQueryReset: hostQueryReset,
            OffscreenReadback: readback,
            Failures: failures);
    }

    private static bool Attempt(string name, Action action, List<string> failures)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception error)
        {
            failures.Add($"{name}: {error.GetType().Name}: {error.Message}");
            return false;
        }
    }

    private static void ProbeHostQueryReset(Silk.NET.Vulkan.Vk vk, Device device)
    {
        var create = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = 2,
        };
        VulkanInterop.Check(
            vk.CreateQueryPool(device, &create, null, out QueryPool pool),
            "vkCreateQueryPool");
        try
        {
            vk.ResetQueryPool(device, pool, 0, 2);
        }
        finally
        {
            vk.DestroyQueryPool(device, pool, null);
        }
    }

    private static byte[] RenderAndReadBack(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice physicalDevice,
        Device device,
        Queue queue,
        uint graphicsFamily)
    {
        Image image = default;
        DeviceMemory imageMemory = default;
        ImageView view = default;
        Silk.NET.Vulkan.Buffer readback = default;
        DeviceMemory readbackMemory = default;
        CommandPool commandPool = default;
        Semaphore timeline = default;

        try
        {
            (image, imageMemory) = CreateColorTarget(vk, physicalDevice, device);
            view = CreateImageView(vk, device, image);
            uint byteCount = ProbeExtent * ProbeExtent * 4;
            (readback, readbackMemory) = CreateReadbackBuffer(vk, physicalDevice, device, byteCount);

            var poolCreate = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = graphicsFamily,
                Flags = CommandPoolCreateFlags.TransientBit,
            };
            VulkanInterop.Check(
                vk.CreateCommandPool(device, &poolCreate, null, out commandPool),
                "vkCreateCommandPool");

            var allocate = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            VulkanInterop.Check(
                vk.AllocateCommandBuffers(device, &allocate, out CommandBuffer commands),
                "vkAllocateCommandBuffers");

            RecordClearAndCopy(vk, commands, image, view, readback);

            var semaphoreType = new SemaphoreTypeCreateInfo
            {
                SType = StructureType.SemaphoreTypeCreateInfo,
                SemaphoreType = SemaphoreType.Timeline,
                InitialValue = 0,
            };
            var semaphoreCreate = new SemaphoreCreateInfo
            {
                SType = StructureType.SemaphoreCreateInfo,
                PNext = &semaphoreType,
            };
            VulkanInterop.Check(
                vk.CreateSemaphore(device, &semaphoreCreate, null, out timeline),
                "vkCreateSemaphore (timeline)");

            var commandSubmit = new CommandBufferSubmitInfo
            {
                SType = StructureType.CommandBufferSubmitInfo,
                CommandBuffer = commands,
            };
            var signal = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = timeline,
                Value = 1,
                StageMask = PipelineStageFlags2.AllCommandsBit,
            };
            var submit = new SubmitInfo2
            {
                SType = StructureType.SubmitInfo2,
                CommandBufferInfoCount = 1,
                PCommandBufferInfos = &commandSubmit,
                SignalSemaphoreInfoCount = 1,
                PSignalSemaphoreInfos = &signal,
            };
            VulkanInterop.Check(
                vk.QueueSubmit2(queue, 1, &submit, default),
                "vkQueueSubmit2");

            ulong waitValue = 1;
            Semaphore waitSemaphore = timeline;
            var wait = new SemaphoreWaitInfo
            {
                SType = StructureType.SemaphoreWaitInfo,
                SemaphoreCount = 1,
                PSemaphores = &waitSemaphore,
                PValues = &waitValue,
            };
            VulkanInterop.Check(
                vk.WaitSemaphores(device, &wait, 5_000_000_000ul),
                "vkWaitSemaphores (timeline)");

            void* mapped = null;
            VulkanInterop.Check(
                vk.MapMemory(device, readbackMemory, 0, byteCount, 0, &mapped),
                "vkMapMemory (readback)");
            try
            {
                var pixels = new byte[byteCount];
                new ReadOnlySpan<byte>(mapped, (int)byteCount).CopyTo(pixels);
                return pixels;
            }
            finally
            {
                vk.UnmapMemory(device, readbackMemory);
            }
        }
        finally
        {
            if (timeline.Handle != 0)
                vk.DestroySemaphore(device, timeline, null);
            if (commandPool.Handle != 0)
                vk.DestroyCommandPool(device, commandPool, null);
            if (readback.Handle != 0)
                vk.DestroyBuffer(device, readback, null);
            if (readbackMemory.Handle != 0)
                vk.FreeMemory(device, readbackMemory, null);
            if (view.Handle != 0)
                vk.DestroyImageView(device, view, null);
            if (image.Handle != 0)
                vk.DestroyImage(device, image, null);
            if (imageMemory.Handle != 0)
                vk.FreeMemory(device, imageMemory, null);
        }
    }

    private static void RecordClearAndCopy(
        Silk.NET.Vulkan.Vk vk,
        CommandBuffer commands,
        Image image,
        ImageView view,
        Silk.NET.Vulkan.Buffer readback)
    {
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        VulkanInterop.Check(vk.BeginCommandBuffer(commands, &begin), "vkBeginCommandBuffer");

        var subresource = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };

        Barrier(
            vk,
            commands,
            image,
            subresource,
            ImageLayout.Undefined,
            ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.TopOfPipeBit,
            AccessFlags2.None,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit);

        var clear = new ClearValue
        {
            Color = new ClearColorValue
            {
                Float32_0 = ExpectedClearRgba[0] / 255f,
                Float32_1 = ExpectedClearRgba[1] / 255f,
                Float32_2 = ExpectedClearRgba[2] / 255f,
                Float32_3 = ExpectedClearRgba[3] / 255f,
            },
        };
        var attachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = view,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = clear,
        };
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(ProbeExtent, ProbeExtent)),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &attachment,
        };
        vk.CmdBeginRendering(commands, &rendering);
        vk.CmdEndRendering(commands);

        Barrier(
            vk,
            commands,
            image,
            subresource,
            ImageLayout.ColorAttachmentOptimal,
            ImageLayout.TransferSrcOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit,
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
            ImageExtent = new Extent3D(ProbeExtent, ProbeExtent, 1),
        };
        vk.CmdCopyImageToBuffer(
            commands,
            image,
            ImageLayout.TransferSrcOptimal,
            readback,
            1,
            &region);

        VulkanInterop.Check(vk.EndCommandBuffer(commands), "vkEndCommandBuffer");
    }

    private static void Barrier(
        Silk.NET.Vulkan.Vk vk,
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
        vk.CmdPipelineBarrier2(commands, &dependency);
    }

    private static (Image Image, DeviceMemory Memory) CreateColorTarget(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice physicalDevice,
        Device device)
    {
        var create = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            Extent = new Extent3D(ProbeExtent, ProbeExtent, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        VulkanInterop.Check(
            vk.CreateImage(device, &create, null, out Image image),
            "vkCreateImage (probe colour target)");

        vk.GetImageMemoryRequirements(device, image, out MemoryRequirements requirements);
        DeviceMemory memory = Allocate(
            vk,
            physicalDevice,
            device,
            requirements,
            MemoryPropertyFlags.DeviceLocalBit);
        VulkanInterop.Check(
            vk.BindImageMemory(device, image, memory, 0),
            "vkBindImageMemory (probe colour target)");
        return (image, memory);
    }

    private static ImageView CreateImageView(
        Silk.NET.Vulkan.Vk vk,
        Device device,
        Image image)
    {
        var create = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
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
            vk.CreateImageView(device, &create, null, out ImageView view),
            "vkCreateImageView (probe colour target)");
        return view;
    }

    private static (Silk.NET.Vulkan.Buffer Buffer, DeviceMemory Memory) CreateReadbackBuffer(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice physicalDevice,
        Device device,
        uint byteCount)
    {
        var create = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = byteCount,
            Usage = BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
        };
        VulkanInterop.Check(
            vk.CreateBuffer(device, &create, null, out Silk.NET.Vulkan.Buffer buffer),
            "vkCreateBuffer (probe readback)");

        vk.GetBufferMemoryRequirements(device, buffer, out MemoryRequirements requirements);
        DeviceMemory memory = Allocate(
            vk,
            physicalDevice,
            device,
            requirements,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        VulkanInterop.Check(
            vk.BindBufferMemory(device, buffer, memory, 0),
            "vkBindBufferMemory (probe readback)");
        return (buffer, memory);
    }

    private static DeviceMemory Allocate(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice physicalDevice,
        Device device,
        MemoryRequirements requirements,
        MemoryPropertyFlags properties)
    {
        uint? typeIndex = FindMemoryType(
            vk,
            physicalDevice,
            requirements.MemoryTypeBits,
            properties);
        if (typeIndex is not { } index)
        {
            throw new NotSupportedException(
                $"No Vulkan memory type satisfies {properties} for the capability probe.");
        }

        var allocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = index,
        };
        VulkanInterop.Check(
            vk.AllocateMemory(device, &allocate, null, out DeviceMemory memory),
            "vkAllocateMemory (probe)");
        return memory;
    }

    internal static uint? FindMemoryType(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice physicalDevice,
        uint typeBits,
        MemoryPropertyFlags properties)
    {
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out PhysicalDeviceMemoryProperties memory);
        for (uint i = 0; i < memory.MemoryTypeCount && i < 32; i++)
        {
            bool allowed = (typeBits & (1u << (int)i)) != 0;
            if (allowed && memory.MemoryTypes[(int)i].PropertyFlags.HasFlag(properties))
                return i;
        }

        return null;
    }

    internal static void VerifyClearColour(ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != ProbeExtent * ProbeExtent * 4)
        {
            throw new InvalidOperationException(
                $"the readback returned {pixels.Length} bytes; expected " +
                $"{ProbeExtent * ProbeExtent * 4}.");
        }

        for (int i = 0; i < pixels.Length; i += 4)
        {
            for (int channel = 0; channel < 4; channel++)
            {
                byte actual = pixels[i + channel];
                byte expected = ExpectedClearRgba[channel];
                if (Math.Abs(actual - expected) > 1)
                {
                    throw new InvalidOperationException(
                        $"pixel {i / 4} channel {channel} read 0x{actual:X2}, expected " +
                        $"0x{expected:X2}.");
                }
            }
        }
    }
}
