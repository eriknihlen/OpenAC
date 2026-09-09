using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.Core.Lighting;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed unsafe partial class MeshModernSharedIndexOffscreenTests
{
    private const int Extent = 64;
    private const uint Prefix = 2;
    private static readonly object VulkanLock = new();

    [Trait("Lane", "Vulkan")]
    [Fact]
    public void CommittedProductionOrdinaryShader_RendersLocalSidecarsAtNonzeroTransformPrefix()
    {
        lock (VulkanLock)
        {
            string shaderDirectory = Path.Combine(
                RepositoryRoot(), "src", "AcDream.App", "Rendering", "Shaders", "spv");
            Assert.Equal(
                Path.GetFullPath(Path.Combine(
                    RepositoryRoot(), "src", "AcDream.App", "Rendering", "Shaders", "spv")),
                Path.GetFullPath(shaderDirectory));
            Assert.True(File.Exists(Path.Combine(shaderDirectory, "mesh_modern.vert.spv")));

            using var host = HeadlessVulkanHost.Create(shaderDirectory);
            byte[] pixels = Render(host.Device, host.Vk, host.PhysicalDevice, host.LogicalDevice, host.Queue, host.QueueFamily);

            int dark = CountPixels(pixels, 51);
            int bright = CountPixels(pixels, 179);
            Assert.True(dark > 64, $"Expected a dark local-sidecar instance, found {dark} matching pixels.");
            Assert.True(bright > 64, $"Expected a bright local-sidecar instance, found {bright} matching pixels.");
        }
    }

    [Fact]
    public void ReadbackDependency_PublishesTheExactCopiedRangeBeforeEndAndSubmit()
    {
        const ulong byteCount = 16_384;
        var readback = new Buffer(0x470u);

        BufferMemoryBarrier2 barrier = CreateHostReadBarrier(readback, byteCount);
        Assert.Equal(StructureType.BufferMemoryBarrier2, barrier.SType);
        Assert.Equal(PipelineStageFlags2.CopyBit, barrier.SrcStageMask);
        Assert.Equal(AccessFlags2.TransferWriteBit, barrier.SrcAccessMask);
        Assert.Equal(PipelineStageFlags2.HostBit, barrier.DstStageMask);
        Assert.Equal(AccessFlags2.HostReadBit, barrier.DstAccessMask);
        Assert.Equal(Silk.NET.Vulkan.Vk.QueueFamilyIgnored, barrier.SrcQueueFamilyIndex);
        Assert.Equal(Silk.NET.Vulkan.Vk.QueueFamilyIgnored, barrier.DstQueueFamilyIndex);
        Assert.Equal(readback.Handle, barrier.Buffer.Handle);
        Assert.Equal(0ul, barrier.Offset);
        Assert.Equal(byteCount, barrier.Size);

        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "tests", "AcDream.App.Tests", "Rendering", "Gpu", "Vk",
            "MeshModernSharedIndexOffscreenTests.cs"));
        int readbackStart = source.LastIndexOf("private static byte[] ReadBack(", StringComparison.Ordinal);
        int readbackEnd = source.LastIndexOf("private static int CountPixels(", StringComparison.Ordinal);
        Assert.True(readbackStart >= 0 && readbackEnd > readbackStart);
        string livePath = source[readbackStart..readbackEnd];

        int copy = livePath.IndexOf("vk.CmdCopyImageToBuffer(commands, image, ImageLayout.TransferSrcOptimal, readback, 1, &copy);", StringComparison.Ordinal);
        int descriptor = livePath.IndexOf("BufferMemoryBarrier2 hostReadBarrier = CreateHostReadBarrier(readback, byteCount);", StringComparison.Ordinal);
        int dependency = livePath.IndexOf("PBufferMemoryBarriers = &hostReadBarrier", StringComparison.Ordinal);
        int publish = livePath.IndexOf("vk.CmdPipelineBarrier2(commands, &hostDependency);", StringComparison.Ordinal);
        int end = livePath.IndexOf("vk.EndCommandBuffer(commands)", StringComparison.Ordinal);
        int submit = livePath.IndexOf("vk.QueueSubmit2(queue, 1, &submit, default)", StringComparison.Ordinal);
        Assert.True(
            copy >= 0 && copy < descriptor && descriptor < dependency && dependency < publish
                && publish < end && end < submit,
            $"Expected copy -> descriptor -> barrier -> end -> submit, got {copy}, {descriptor}, {dependency}, {publish}, {end}, {submit}.");
    }

    private static byte[] Render(
        VulkanGpuDevice device,
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice physicalDevice,
        Device logicalDevice,
        Queue queue,
        uint queueFamily)
    {
        using IGpuBuffer vertices = device.CreateBuffer(new GpuBufferDescription(
            "s5-470-vertices",
            4 * Marshal.SizeOf<Vertex>(),
            GpuBufferUsage.Vertex | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));
        using IGpuBuffer indices = device.CreateBuffer(new GpuBufferDescription(
            "s5-470-indices",
            6 * sizeof(ushort),
            GpuBufferUsage.Index | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));
        Vertex[] vertexData =
        [
            new(new Vector3(-0.45f, -0.45f, 0f), Vector3.UnitZ, Vector2.Zero),
            new(new Vector3( 0.45f, -0.45f, 0f), Vector3.UnitZ, Vector2.UnitX),
            new(new Vector3( 0.45f,  0.45f, 0f), Vector3.UnitZ, Vector2.One),
            new(new Vector3(-0.45f,  0.45f, 0f), Vector3.UnitZ, Vector2.UnitY),
        ];
        vertices.Upload(0, MemoryMarshal.AsBytes<Vertex>(vertexData));
        indices.Upload(0, MemoryMarshal.AsBytes<ushort>([0, 1, 2, 2, 3, 0]));

        using IGpuRenderTarget target = device.CreateRenderTarget(new GpuRenderTargetDescription(
            "s5-470-offscreen",
            Extent,
            Extent,
            GpuTextureFormat.Rgba8UnormRenderTarget,
            DepthFormat: null,
            SampleCount: 1));
        using IGpuPipeline pipeline = device.CreatePipeline(new GpuPipelineDescription
        {
            Name = "s5-470-mesh-modern",
            Shaders = new GpuShaderSet("mesh_modern"),
            VertexLayout = GpuVertexLayout.WorldMesh,
            Topology = GpuPrimitiveTopology.TriangleList,
            Blend = GpuBlendMode.None,
            Depth = GpuDepthState.Disabled,
            Cull = GpuCullMode.None,
            SampleCount = 1,
        });
        Assert.False(pipeline.Description.Shaders.HasEmbeddedSpirv);
        Assert.Equal("mesh_modern", pipeline.Description.Shaders.Name);

        using (IGpuFrame frame = device.BeginFrame())
        {
            using IGpuPassEncoder encoder = frame.BeginPass(new GpuPassDescription
            {
                Name = "s5-470-shared-index-witness",
                Color = new GpuColorAttachment(
                    target,
                    GpuLoadOp.Clear,
                    GpuStoreOp.Store,
                    new Vector4(0f, 0f, 0f, 1f)),
                Depth = null,
                SampleCount = 1,
            });
            encoder.BindPipeline(pipeline);

            GpuPushConstants constants = GpuPushConstants.Default;
            constants.LightDebug = 3;
            constants.TextureIndexB = Prefix;
            encoder.SetPushConstants(constants);

            Matrix4x4[] transforms =
            [
                Matrix4x4.CreateTranslation(20f, 20f, 0f),
                Matrix4x4.CreateTranslation(-20f, -20f, 0f),
                Matrix4x4.CreateTranslation(-0.5f, 0f, 0f),
                Matrix4x4.CreateTranslation( 0.5f, 0f, 0f),
            ];
            BindStorage(frame, encoder, GpuBindingModel.StorageInstances, transforms);
            BindStorage(frame, encoder, GpuBindingModel.StorageBatches,
                [new BatchData(device.DefaultTextureSlot.Index, 1f, 0u, 1u)]);
            BindStorage(frame, encoder, GpuBindingModel.StorageClipSlots, [17u, 29u]);
            BindStorage(frame, encoder, GpuBindingModel.StorageGlobalLights, [GlobalLight.Zero]);
            BindStorage(frame, encoder, GpuBindingModel.StorageInstanceLightSets,
                [-1, -1, -1, -1, -1, -1, -1, -1, 0, -1, -1, -1, -1, -1, -1, -1]);
            BindStorage(frame, encoder, GpuBindingModel.StorageInstanceIndoor, [0u, 1u]);
            BindStorage(frame, encoder, GpuBindingModel.StorageInstanceAlpha, [0.25f, 0.75f]);
            BindStorage(frame, encoder, GpuBindingModel.StorageInstanceSelectionLighting,
                [new Vector2(0.2f, 0f), new Vector2(0.7f, 0f)]);
            BindStorage(frame, encoder, GpuBindingModel.StorageInstanceDetailCategory, [3u, 9u]);

            SceneLightingUbo lighting = default;
            BindUniform(frame, encoder, GpuBindingModel.UniformSceneLighting, lighting);
            encoder.BindVertexBuffer(0, vertices, 0);
            encoder.BindIndexBuffer(indices, 0, GpuIndexType.UInt16);
            encoder.DrawIndexed(6, 2, 0, 0, Prefix);
        }

        device.WaitIdle();
        VulkanGpuRenderTarget vkTarget = Assert.IsType<VulkanGpuRenderTarget>(target);
        return ReadBack(
            vk,
            physicalDevice,
            logicalDevice,
            queue,
            queueFamily,
            vkTarget.ColorResult.Image);
    }

    private static void BindStorage<T>(
        IGpuFrame frame,
        IGpuPassEncoder encoder,
        uint binding,
        T[] values)
        where T : unmanaged
    {
        GpuRingAllocation allocation = frame.AllocateRing(
            checked(values.Length * Marshal.SizeOf<T>()),
            GpuRingUsage.Storage);
        values.AsSpan().CopyTo(allocation.AsSpan<T>());
        encoder.BindStorageBuffer(binding, allocation.Buffer, allocation.OffsetBytes, (uint)allocation.Data.Length);
    }

    private static void BindUniform<T>(
        IGpuFrame frame,
        IGpuPassEncoder encoder,
        uint binding,
        T value)
        where T : unmanaged
    {
        GpuRingAllocation allocation = frame.AllocateRing(Marshal.SizeOf<T>(), GpuRingUsage.Uniform);
        allocation.AsSpan<T>()[0] = value;
        encoder.BindUniformBuffer(binding, allocation.Buffer, allocation.OffsetBytes, (uint)allocation.Data.Length);
    }

    private static byte[] ReadBack(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice physicalDevice,
        Device device,
        Queue queue,
        uint queueFamily,
        Image image)
    {
        uint byteCount = Extent * Extent * 4u;
        Buffer readback = default;
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
            VulkanInterop.Check(vk.CreateBuffer(device, &bufferCreate, null, out readback), "vkCreateBuffer (S5-470 readback)");
            vk.GetBufferMemoryRequirements(device, readback, out MemoryRequirements requirements);
            uint memoryType = VulkanActiveDeviceProbe.FindMemoryType(
                vk,
                physicalDevice,
                requirements.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
                ?? throw new NotSupportedException("S5-470 requires coherent host-visible readback memory.");
            var memoryAllocate = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = memoryType,
            };
            VulkanInterop.Check(vk.AllocateMemory(device, &memoryAllocate, null, out memory), "vkAllocateMemory (S5-470 readback)");
            VulkanInterop.Check(vk.BindBufferMemory(device, readback, memory, 0), "vkBindBufferMemory (S5-470 readback)");

            var poolCreate = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = queueFamily,
                Flags = CommandPoolCreateFlags.TransientBit,
            };
            VulkanInterop.Check(vk.CreateCommandPool(device, &poolCreate, null, out pool), "vkCreateCommandPool (S5-470 readback)");
            var commandAllocate = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            VulkanInterop.Check(vk.AllocateCommandBuffers(device, &commandAllocate, out CommandBuffer commands), "vkAllocateCommandBuffers (S5-470 readback)");
            var begin = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            VulkanInterop.Check(vk.BeginCommandBuffer(commands, &begin), "vkBeginCommandBuffer (S5-470 readback)");

            var barrier = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.FragmentShaderBit,
                SrcAccessMask = AccessFlags2.ShaderReadBit,
                DstStageMask = PipelineStageFlags2.CopyBit,
                DstAccessMask = AccessFlags2.TransferReadBit,
                OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = new ImageSubresourceRange(
                    ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = 1,
                PImageMemoryBarriers = &barrier,
            };
            vk.CmdPipelineBarrier2(commands, &dependency);
            var copy = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(Extent, Extent, 1),
            };
            vk.CmdCopyImageToBuffer(commands, image, ImageLayout.TransferSrcOptimal, readback, 1, &copy);
            BufferMemoryBarrier2 hostReadBarrier = CreateHostReadBarrier(readback, byteCount);
            var hostDependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &hostReadBarrier,
            };
            vk.CmdPipelineBarrier2(commands, &hostDependency);
            VulkanInterop.Check(vk.EndCommandBuffer(commands), "vkEndCommandBuffer (S5-470 readback)");

            var commandInfo = new CommandBufferSubmitInfo
            {
                SType = StructureType.CommandBufferSubmitInfo,
                CommandBuffer = commands,
            };
            var submit = new SubmitInfo2
            {
                SType = StructureType.SubmitInfo2,
                CommandBufferInfoCount = 1,
                PCommandBufferInfos = &commandInfo,
            };
            VulkanInterop.Check(vk.QueueSubmit2(queue, 1, &submit, default), "vkQueueSubmit2 (S5-470 readback)");
            VulkanInterop.Check(vk.QueueWaitIdle(queue), "vkQueueWaitIdle (S5-470 readback)");

            void* mapped = null;
            VulkanInterop.Check(vk.MapMemory(device, memory, 0, byteCount, 0, &mapped), "vkMapMemory (S5-470 readback)");
            try
            {
                var pixels = new byte[byteCount];
                new ReadOnlySpan<byte>(mapped, pixels.Length).CopyTo(pixels);
                return pixels;
            }
            finally
            {
                vk.UnmapMemory(device, memory);
            }
        }
        finally
        {
            if (pool.Handle != 0)
                vk.DestroyCommandPool(device, pool, null);
            if (readback.Handle != 0)
                vk.DestroyBuffer(device, readback, null);
            if (memory.Handle != 0)
                vk.FreeMemory(device, memory, null);
        }
    }

    private static BufferMemoryBarrier2 CreateHostReadBarrier(Buffer readback, ulong byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfZero(byteCount);
        return new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit,
            DstAccessMask = AccessFlags2.HostReadBit,
            SrcQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
            Buffer = readback,
            Offset = 0,
            Size = byteCount,
        };
    }

    private static int CountPixels(ReadOnlySpan<byte> pixels, byte expected)
    {
        int count = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if (Math.Abs(pixels[offset + 0] - expected) <= 2
                && Math.Abs(pixels[offset + 1] - expected) <= 2
                && Math.Abs(pixels[offset + 2] - expected) <= 2
                && pixels[offset + 3] >= 253)
            {
                count++;
            }
        }
        return count;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct Vertex(Vector3 Position, Vector3 Normal, Vector2 TexCoord);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct BatchData(
        uint TextureIndex,
        float SurfaceOpacity,
        uint TextureLayer,
        uint Flags);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct GlobalLight(
        Vector4 PositionAndKind,
        Vector4 DirectionAndRange,
        Vector4 ColorAndIntensity,
        Vector4 ConeAngleEtc)
    {
        internal static GlobalLight Zero { get; } = default;
    }

    private sealed class HeadlessVulkanHost : IDisposable
    {
        private bool _disposed;

        private HeadlessVulkanHost(
            Silk.NET.Vulkan.Vk vk,
            Instance instance,
            PhysicalDevice physicalDevice,
            Device logicalDevice,
            Queue queue,
            uint queueFamily,
            VulkanGpuDevice device)
        {
            Vk = vk;
            Instance = instance;
            PhysicalDevice = physicalDevice;
            LogicalDevice = logicalDevice;
            Queue = queue;
            QueueFamily = queueFamily;
            Device = device;
        }

        internal Silk.NET.Vulkan.Vk Vk { get; }
        internal Instance Instance { get; }
        internal PhysicalDevice PhysicalDevice { get; }
        internal Device LogicalDevice { get; }
        internal Queue Queue { get; }
        internal uint QueueFamily { get; }
        internal VulkanGpuDevice Device { get; }

        internal static HeadlessVulkanHost Create(string shaderDirectory)
        {
            Silk.NET.Vulkan.Vk vk = Silk.NET.Vulkan.Vk.GetApi();
            Instance instance = default;
            Device logicalDevice = default;
            VulkanGpuDevice? gpuDevice = null;
            try
            {
                instance = VulkanInstanceFactory.Create(vk, [], enableOptionalExtensions: false).Instance;
                IReadOnlyList<VulkanPhysicalDeviceCandidate> candidates =
                    VulkanPhysicalDeviceInspector.Enumerate(vk, instance, out PhysicalDevice[] handles);
                VulkanPhysicalDeviceChoice selected = VulkanPhysicalDeviceSelection.Choose(candidates, null)
                    ?? throw new NotSupportedException("S5-470 offscreen proof found no Vulkan physical device.");
                PhysicalDevice physicalDevice = handles[selected.Device.Index];
                VulkanDeviceFeatureSupport features = VulkanPhysicalDeviceInspector.ReadFeatures(vk, physicalDevice);
                uint queueFamily = VulkanQueueFamilySelection.ChooseGraphicsOnly(
                    VulkanPhysicalDeviceInspector.ReadQueueFamilies(vk, physicalDevice, surfaceApi: null, default))
                    ?? throw new NotSupportedException("S5-470 offscreen proof found no graphics queue.");
                VulkanLogicalDeviceFactory.Created created = VulkanLogicalDeviceFactory.Create(
                    vk,
                    physicalDevice,
                    new VulkanQueueFamilyChoice(queueFamily, queueFamily),
                    requireSwapchain: false,
                    features);
                logicalDevice = created.Device;
                VulkanDeviceLimitSupport limits = VulkanPhysicalDeviceInspector.ReadLimits(vk, physicalDevice);
                VulkanFormatSupport formats = VulkanPhysicalDeviceInspector.ReadFormats(
                    vk, physicalDevice, surfaceOffersUnorm: true);
                gpuDevice = new VulkanGpuDevice(
                    vk,
                    physicalDevice,
                    logicalDevice,
                    created.GraphicsQueue,
                    created.GraphicsQueue,
                    queueFamily,
                    features,
                    limits,
                    formats,
                    selected.Device.DeviceName,
                    VulkanPhysicalDeviceInspector.DescribeDriver(selected.Device),
                    VulkanApiVersion.Describe(selected.Device.ApiVersion),
                    VulkanDebugNames.Disabled,
                    backbuffer: null,
                    shaderSpirvDirectory: shaderDirectory,
                    pipelineCacheDirectory: null,
                    ringCapacityBytesPerSlot: 2 * 1024 * 1024,
                    framesInFlight: 1);
                return new HeadlessVulkanHost(
                    vk,
                    instance,
                    physicalDevice,
                    logicalDevice,
                    created.GraphicsQueue,
                    queueFamily,
                    gpuDevice);
            }
            catch
            {
                gpuDevice?.Dispose();
                if (logicalDevice.Handle != 0)
                    vk.DestroyDevice(logicalDevice, null);
                if (instance.Handle != 0)
                    vk.DestroyInstance(instance, null);
                vk.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Device.Dispose();
            if (LogicalDevice.Handle != 0)
                Vk.DestroyDevice(LogicalDevice, null);
            if (Instance.Handle != 0)
                Vk.DestroyInstance(Instance, null);
            Vk.Dispose();
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
