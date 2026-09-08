using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed class VulkanSwapchainOutOfDateException(string message) : InvalidOperationException(message);

internal interface IVulkanBackbuffer
{
    Format ImageFormat { get; }

    uint Width { get; }

    uint Height { get; }

    bool TryAcquire(Semaphore acquired, out uint imageIndex);

    Image ImageAt(uint imageIndex);

    ImageView ViewAt(uint imageIndex);

    /// <summary>The semaphore a submit rendering into <paramref name="imageIndex"/> must signal.</summary>
    Semaphore RenderCompleteAt(uint imageIndex);

    bool Present(uint imageIndex);
}

internal sealed unsafe partial class VulkanGpuDevice : IGpuDevice, IGpuPipelineFormatVariantHost
{
    internal const int DefaultRingCapacityBytesPerSlot = 16 * 1024 * 1024;

    internal const PipelineStageFlags2 AcquiredImageWaitStage =
        PipelineStageFlags2.ColorAttachmentOutputBit;

    private readonly Silk.NET.Vulkan.Vk _vk;
    private readonly PhysicalDevice _physicalDevice;
    private readonly Device _device;
    private readonly Queue _graphicsQueue;
    private readonly Queue _presentQueue;
    private readonly uint _graphicsFamily;
    private readonly IVulkanBackbuffer? _backbuffer;

    private readonly VulkanDeviceMemoryAllocator _allocator;
    private readonly VulkanUploadQueue _uploads;
    private readonly VulkanFrameFlightController _flights;
    private readonly VulkanDebugNames _debugNames;
    private readonly Semaphore _timeline;

    private readonly CommandPool[] _commandPools;
    private readonly CommandBuffer[] _commandBuffers;
    private readonly Semaphore[] _imageAcquired;

    private readonly VulkanRingBufferState[] _ringStates;
    private readonly VulkanGpuBuffer[] _ringBuffers;

    private readonly List<Action> _queuedActions = [];

    private VulkanGpuFrame? _openFrame;
    private uint? _acquiredImageIndex;
    private bool _disposed;

    internal VulkanGpuDevice(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice physicalDevice,
        Device device,
        Queue graphicsQueue,
        Queue presentQueue,
        uint graphicsFamily,
        VulkanDeviceFeatureSupport features,
        VulkanDeviceLimitSupport limits,
        VulkanFormatSupport formats,
        string deviceName,
        string driverInfo,
        string apiVersion,
        VulkanDebugNames debugNames,
        IVulkanBackbuffer? backbuffer = null,
        string? shaderSpirvDirectory = null,
        string? pipelineCacheDirectory = null,
        int ringCapacityBytesPerSlot = DefaultRingCapacityBytesPerSlot,
        int framesInFlight = VulkanFrameFlightController.DefaultFramesInFlight,
        bool retainBackbufferCapture = false)
    {
        _retainBackbufferCapture = retainBackbufferCapture;
        _vk = vk ?? throw new ArgumentNullException(nameof(vk));
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(formats);
        _physicalDevice = physicalDevice;
        _device = device;
        _graphicsQueue = graphicsQueue;
        _presentQueue = presentQueue;
        _graphicsFamily = graphicsFamily;
        _backbuffer = backbuffer;
        _debugNames = debugNames ?? throw new ArgumentNullException(nameof(debugNames));
        DepthStencilFormat = formats.DepthStencilFormat;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ringCapacityBytesPerSlot);

        Capabilities = new GpuCapabilityRecord
        {
            Backend = GpuBackendKind.Vulkan,
            DeviceName = deviceName,
            DriverInfo = driverInfo,
            ApiVersion = apiVersion,
            MaxTextureTableSlots = Math.Min(
                limits.MaxDescriptorSetUpdateAfterBindSampledImages,
                limits.MaxPerStageDescriptorUpdateAfterBindSampledImages),
            MaxStorageBufferBindings = GpuBindingModel.StorageBindingCount,
            MaxPushConstantBytes = limits.MaxPushConstantsSize,
            MinStorageBufferOffsetAlignment = Math.Max(limits.MinStorageBufferOffsetAlignment, 1),
            MaxStorageBufferRangeBytes = limits.MaxStorageBufferRange,
            MinUniformBufferOffsetAlignment = Math.Max(limits.MinUniformBufferOffsetAlignment, 1),
            MaxClipDistances = limits.MaxClipDistances,
            MaxSampleCount = limits.MaxColorSampleCount,
            MaxImageDimension2D = limits.MaxImageDimension2D,
            MaxImageArrayLayers = limits.MaxImageArrayLayers,
            DeviceLocalMemoryBytes = limits.DeviceLocalHeapBytes,
            SupportsMultiDrawIndirect = features.MultiDrawIndirect,
            SupportsDrawParameters = features.ShaderDrawParameters,
            SupportsTextureCompressionBc =
                features.TextureCompressionBc && formats.Bc1Sampled && formats.Bc2Sampled && formats.Bc3Sampled,
            SupportsTimestampQueries = limits.TimestampComputeAndGraphics,
            SupportsMultiview = features.Multiview,
            SupportsPersistentlyMappedRings = true,
            SupportsRgba16FloatRenderTargets =
                formats.Rgba16FloatColorAttachment
                && formats.Rgba16FloatSampled
                && formats.Rgba16FloatLinearFilter
                && formats.MaxRgba16FloatSampleCount > 0,
            MaxRgba16FloatSampleCount =
                formats.Rgba16FloatColorAttachment
                && formats.Rgba16FloatSampled
                && formats.Rgba16FloatLinearFilter
                    ? Math.Min(formats.MaxRgba16FloatSampleCount, limits.MaxColorSampleCount)
                    : 0u,
            SupportsSampledDepth = formats.DepthStencilSampled,
        };

        var timelineType = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0,
        };
        var timelineCreate = new SemaphoreCreateInfo
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = &timelineType,
        };
        VulkanInterop.Check(
            _vk.CreateSemaphore(_device, &timelineCreate, null, out _timeline),
            "vkCreateSemaphore (RHI frame timeline)");

        _flights = new VulkanFrameFlightController(
            new VulkanTimelineApi(_vk, _device, _timeline),
            framesInFlight);
        _allocator = new VulkanDeviceMemoryAllocator(_vk, physicalDevice, _device);
        _uploads = new VulkanUploadQueue(_vk, _device, _allocator, _flights, _debugNames);

        int slots = _flights.SlotCount;
        _commandPools = new CommandPool[slots];
        _commandBuffers = new CommandBuffer[slots];
        _imageAcquired = new Semaphore[slots];
        _ringStates = new VulkanRingBufferState[slots];
        _ringBuffers = new VulkanGpuBuffer[slots];
        for (int slot = 0; slot < slots; slot++)
        {
            var poolCreate = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = graphicsFamily,
            };
            VulkanInterop.Check(
                _vk.CreateCommandPool(_device, &poolCreate, null, out CommandPool pool),
                "vkCreateCommandPool (RHI flight slot)");
            _commandPools[slot] = pool;

            var allocate = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            VulkanInterop.Check(
                _vk.AllocateCommandBuffers(_device, &allocate, out CommandBuffer commands),
                "vkAllocateCommandBuffers (RHI flight slot)");
            _commandBuffers[slot] = commands;

            var semaphoreCreate = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            VulkanInterop.Check(
                _vk.CreateSemaphore(_device, &semaphoreCreate, null, out Semaphore acquired),
                "vkCreateSemaphore (RHI image acquired)");
            _imageAcquired[slot] = acquired;

            _ringStates[slot] = new VulkanRingBufferState((ulong)ringCapacityBytesPerSlot);
            _ringBuffers[slot] = new VulkanGpuBuffer(
                _vk,
                _device,
                _allocator,
                _uploads,
                _flights,
                _debugNames,
                new GpuBufferDescription(
                    $"vk-ring-slot-{slot}",
                    ringCapacityBytesPerSlot,
                    GpuBufferUsage.Storage
                        | GpuBufferUsage.Uniform
                        | GpuBufferUsage.Indirect
                        | GpuBufferUsage.Vertex
                        | GpuBufferUsage.Index,
                    GpuMemoryResidency.HostWritable));

            if (!_ringBuffers[slot].IsMapped)
            {
                throw new NotSupportedException(
                    "The Vulkan per-frame ring must live in host-visible memory; this device " +
                    "offered no host-visible type for a HostWritable buffer.");
            }
        }

        InitialiseResources(shaderSpirvDirectory, pipelineCacheDirectory);
    }

    public GpuBackendKind Backend => GpuBackendKind.Vulkan;

    public GpuCapabilityRecord Capabilities { get; }

    public IGpuResourceRetirementQueue Retirement => _flights;

    internal Format DepthStencilFormat { get; }

    internal Silk.NET.Vulkan.Vk Api => _vk;

    internal Device Handle => _device;

    internal VulkanDeviceMemoryAllocator Allocator => _allocator;

    internal VulkanUploadQueue Uploads => _uploads;

    internal VulkanDebugNames DebugNames => _debugNames;

    internal VulkanFrameFlightController Flights => _flights;

    internal CommandBuffer CurrentCommands => _commandBuffers[_flights.CurrentSlot];

    public IGpuBuffer CreateBuffer(in GpuBufferDescription description)
    {
        ThrowIfDisposed();
        return new VulkanGpuBuffer(_vk, _device, _allocator, _uploads, _flights, _debugNames, description);
    }

    public void QueueDeviceAction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _queuedActions.Add(action);
    }

    public void ProcessDeviceActions()
    {
        Action[] pending = [.. _queuedActions];
        _queuedActions.Clear();
        foreach (Action action in pending)
            action();
    }

    public IGpuFrame BeginFrame() =>
        TryBeginFrame(out IGpuFrame? frame) && frame is not null
            ? frame
            : throw new VulkanSwapchainOutOfDateException(
                "The swapchain is out of date and must be recreated before another frame is recorded.");

    internal bool TryBeginFrame(out IGpuFrame? frame)
    {
        ThrowIfDisposed();
        frame = null;
        if (_openFrame is not null)
            throw new InvalidOperationException("A frame is already open; end it before beginning another.");

        long serial = _flights.BeginFrame();
        _captureValidity.BeginFrame();
        int slot = _flights.CurrentSlot;

        _uploads.ReleaseCompleted(CompletedSerial());
        _ringStates[slot].Reset();
        FrameBindingsAt(slot).BeginFrame();

        _acquiredImageIndex = null;
        if (_backbuffer is not null)
        {
            if (!_backbuffer.TryAcquire(_imageAcquired[slot], out uint imageIndex))
            {
                // Release the serial by submitting nothing: the timeline must
                // still reach this value or the next BeginFrame waits forever.
                SignalTimelineWithoutWork(serial);
                _flights.EndFrame();
                return false;
            }

            _acquiredImageIndex = imageIndex;
        }

        VulkanInterop.Check(
            _vk.ResetCommandPool(_device, _commandPools[slot], 0),
            "vkResetCommandPool");
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        VulkanInterop.Check(
            _vk.BeginCommandBuffer(_commandBuffers[slot], &begin),
            "vkBeginCommandBuffer (frame)");

        _backbufferRenderingReady = false;

        BeginFrameResources(slot);

        var opened = new VulkanGpuFrame(this, slot, serial);
        _openFrame = opened;
        frame = opened;
        return true;
    }

    private long CompletedSerial()
    {
        VulkanInterop.Check(
            _vk.GetSemaphoreCounterValue(_device, _timeline, out ulong value),
            "vkGetSemaphoreCounterValue (upload release)");
        return (long)value;
    }

    internal GpuRingAllocation AllocateRing(int slotIndex, int byteCount, GpuRingUsage usage)
    {
        ThrowIfDisposed();
        ulong alignment = usage switch
        {
            GpuRingUsage.Storage => Capabilities.MinStorageBufferOffsetAlignment,
            GpuRingUsage.Uniform => Capabilities.MinUniformBufferOffsetAlignment,
            GpuRingUsage.Indirect => 4,
            _ => 16,
        };

        ulong offset = _ringStates[slotIndex].Allocate(byteCount, alignment);
        VulkanGpuBuffer buffer = _ringBuffers[slotIndex];
        Span<byte> data = byteCount == 0
            ? Span<byte>.Empty
            : buffer.MappedSpan.Slice((int)offset, byteCount);
        return new GpuRingAllocation(buffer, (uint)offset, data);
    }

    internal void EndFrame(VulkanGpuFrame frame)
    {
        if (!ReferenceEquals(_openFrame, frame))
            return;

        int slot = frame.SlotIndex;
        CommandBuffer commands = _commandBuffers[slot];

        EndFrameResources(slot, commands);

        _uploads.Record(commands);

        if (_acquiredImageIndex is { } imageIndex && _backbuffer is not null)
        {
            Image presentable = _backbuffer.ImageAt(imageIndex);
            ImageLayout current = RecordBackbufferCapture(commands, presentable);
            TransitionBackbufferForPresent(commands, presentable, current);
        }

        VulkanInterop.Check(_vk.EndCommandBuffer(commands), "vkEndCommandBuffer (frame)");

        var commandSubmit = new CommandBufferSubmitInfo
        {
            SType = StructureType.CommandBufferSubmitInfo,
            CommandBuffer = commands,
        };
        SemaphoreSubmitInfo waitSemaphore = CreateAcquiredImageWait(_imageAcquired[slot]);
        SemaphoreSubmitInfo* signals = stackalloc SemaphoreSubmitInfo[2];
        int signalCount = 0;
        if (_acquiredImageIndex is { } presented && _backbuffer is not null)
        {
            signals[signalCount++] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = _backbuffer.RenderCompleteAt(presented),
                StageMask = PipelineStageFlags2.AllCommandsBit,
            };
        }

        signals[signalCount++] = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = _timeline,
            Value = (ulong)frame.Serial,
            StageMask = PipelineStageFlags2.AllCommandsBit,
        };

        var submit = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            WaitSemaphoreInfoCount = _acquiredImageIndex is null ? 0u : 1u,
            PWaitSemaphoreInfos = _acquiredImageIndex is null ? null : &waitSemaphore,
            CommandBufferInfoCount = 1,
            PCommandBufferInfos = &commandSubmit,
            SignalSemaphoreInfoCount = (uint)signalCount,
            PSignalSemaphoreInfos = signals,
        };
        VulkanInterop.Check(
            _vk.QueueSubmit2(_graphicsQueue, 1, &submit, default),
            "vkQueueSubmit2 (frame)");
        _captureValidity.CompleteSubmission();

        _flights.EndFrame();
        _openFrame = null;

        if (_acquiredImageIndex is { } toPresent && _backbuffer is not null)
        {
            PresentSucceeded = _backbuffer.Present(toPresent);
            _acquiredImageIndex = null;
        }
    }

    internal static SemaphoreSubmitInfo CreateAcquiredImageWait(Semaphore semaphore) => new()
    {
        SType = StructureType.SemaphoreSubmitInfo,
        Semaphore = semaphore,
        StageMask = AcquiredImageWaitStage,
    };

    /// <summary>False after a present that reported the swapchain should be rebuilt.</summary>
    internal bool PresentSucceeded { get; private set; } = true;

    private void TransitionBackbufferForPresent(CommandBuffer commands, Image image, ImageLayout currentLayout)
    {
        bool captured = currentLayout == ImageLayout.TransferSrcOptimal;
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = captured
                ? PipelineStageFlags2.CopyBit
                : PipelineStageFlags2.ColorAttachmentOutputBit,
            SrcAccessMask = captured
                ? AccessFlags2.TransferReadBit
                : AccessFlags2.ColorAttachmentWriteBit,
            DstStageMask = PipelineStageFlags2.BottomOfPipeBit,
            DstAccessMask = AccessFlags2.None,
            OldLayout = currentLayout,
            NewLayout = ImageLayout.PresentSrcKhr,
            SrcQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _vk.CmdPipelineBarrier2(commands, &dependency);
    }

    private void SignalTimelineWithoutWork(long serial)
    {
        var signal = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = _timeline,
            Value = (ulong)serial,
            StageMask = PipelineStageFlags2.AllCommandsBit,
        };
        var submit = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            SignalSemaphoreInfoCount = 1,
            PSignalSemaphoreInfos = &signal,
        };
        VulkanInterop.Check(
            _vk.QueueSubmit2(_graphicsQueue, 1, &submit, default),
            "vkQueueSubmit2 (abandoned frame timeline signal)");
    }

    public void WaitIdle()
    {
        if (_disposed)
            return;
        VulkanInterop.Check(_vk.DeviceWaitIdle(_device), "vkDeviceWaitIdle");
        _flights.WaitForSubmittedWork();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _vk.DeviceWaitIdle(_device);
        DisposeResources();
        _flights.DrainAll();

        foreach (VulkanGpuBuffer ring in _ringBuffers)
            ring.Dispose();
        _flights.DrainAll();

        foreach (Semaphore semaphore in _imageAcquired)
        {
            if (semaphore.Handle != 0)
                _vk.DestroySemaphore(_device, semaphore, null);
        }

        foreach (CommandPool pool in _commandPools)
        {
            if (pool.Handle != 0)
                _vk.DestroyCommandPool(_device, pool, null);
        }

        _uploads.Dispose();
        _flights.DrainAll();

        if (_timeline.Handle != 0)
            _vk.DestroySemaphore(_device, _timeline, null);

        _flights.Dispose();
        _allocator.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
