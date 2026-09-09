using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed unsafe class VulkanUploadQueue : IDisposable
{
    /// <summary>Plan §4.3: a 48 MiB persistently mapped staging ring.</summary>
    internal const ulong DefaultStagingCapacityBytes = 48UL * 1024 * 1024;

    private readonly Silk.NET.Vulkan.Vk _vk;
    private readonly Device _device;
    private readonly VulkanDeviceMemoryAllocator _allocator;
    private readonly VulkanFrameFlightController _flights;
    private readonly VulkanStagingRingState _ringState;
    private readonly VulkanDebugNames _debugNames;

    private readonly Buffer _stagingBuffer;
    private readonly VulkanAllocation _stagingAllocation;

    private readonly List<BufferCopy2> _bufferCopies = [];
    private readonly List<ImageCopy2> _imageCopies = [];
    private readonly List<TemporaryStaging> _temporaries = [];

    private bool _disposed;

    internal enum BufferCopyKind
    {
        HostStaging,
        DeviceMigration,
    }

    private readonly record struct BufferCopy2(
        Buffer Source,
        Buffer Destination,
        ulong SourceOffset,
        ulong DestinationOffset,
        ulong SizeBytes,
        BufferCopyKind Kind);

    private readonly record struct ImageCopy2(
        Buffer Source,
        ulong SourceOffset,
        Image Destination,
        uint MipLevel,
        uint Layer,
        uint Width,
        uint Height);

    private readonly record struct TemporaryStaging(Buffer Buffer, VulkanAllocation Allocation);

    internal VulkanUploadQueue(
        Silk.NET.Vulkan.Vk vk,
        Device device,
        VulkanDeviceMemoryAllocator allocator,
        VulkanFrameFlightController flights,
        VulkanDebugNames debugNames,
        ulong stagingCapacityBytes = DefaultStagingCapacityBytes)
    {
        _vk = vk ?? throw new ArgumentNullException(nameof(vk));
        _device = device;
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _flights = flights ?? throw new ArgumentNullException(nameof(flights));
        _debugNames = debugNames ?? throw new ArgumentNullException(nameof(debugNames));
        _ringState = new VulkanStagingRingState(stagingCapacityBytes);

        (_stagingBuffer, _stagingAllocation) = CreateHostBuffer(
            stagingCapacityBytes,
            BufferUsageFlags.TransferSrcBit,
            "vk-staging-ring");
    }

    private readonly Dictionary<Image, ImageLayout> _imageEntryLayouts = [];

    private readonly List<MipBlitRequest> _mipBlits = [];

    private readonly record struct MipBlitRequest(
        Image Image,
        int Width,
        int Height,
        int MipLevelCount,
        int LayerCount);

    internal int PendingBufferCopyCount => _bufferCopies.Count;

    internal int PendingImageCopyCount => _imageCopies.Count;

    internal ulong StagingLiveBytes => _ringState.LiveBytes;

    internal void StageBufferWrite(
        Buffer destination,
        ulong destinationOffsetBytes,
        ReadOnlySpan<byte> data,
        string ownerName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (data.IsEmpty)
            return;

        (Buffer source, ulong sourceOffset) = Stage(data, ownerName);
        _bufferCopies.Add(new BufferCopy2(
            source,
            destination,
            sourceOffset,
            destinationOffsetBytes,
            (ulong)data.Length,
            BufferCopyKind.HostStaging));
    }

    internal void StageImageWrite(
        Image destination,
        int mipLevel,
        int layer,
        int width,
        int height,
        ImageLayout entryLayout,
        ReadOnlySpan<byte> data,
        string ownerName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (data.IsEmpty)
            return;

        (Buffer source, ulong sourceOffset) = Stage(data, ownerName, alignmentBytes: 16);
        _imageCopies.Add(new ImageCopy2(
            source,
            sourceOffset,
            destination,
            (uint)mipLevel,
            (uint)layer,
            (uint)width,
            (uint)height));
        RecordEntryLayout(destination, entryLayout);
    }

    internal void EnqueueMipBlit(
        Image image,
        int width,
        int height,
        int mipLevelCount,
        int layerCount,
        ImageLayout entryLayout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (mipLevelCount <= 1)
            return;
        _mipBlits.Add(new MipBlitRequest(image, width, height, mipLevelCount, layerCount));
        RecordEntryLayout(image, entryLayout);
    }

    private void RecordEntryLayout(Image image, ImageLayout entryLayout)
    {
        // First writer of the batch wins: a later writer that found the image
        // already in TRANSFER_DST is describing this batch's own effect, not the
        // layout the batch started from.
        _imageEntryLayouts.TryAdd(image, entryLayout);
    }

    internal void EnqueueBufferCopy(
        Buffer source,
        Buffer destination,
        ulong sourceOffsetBytes,
        ulong destinationOffsetBytes,
        ulong byteCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (byteCount == 0)
            return;
        _bufferCopies.Add(new BufferCopy2(
            source,
            destination,
            sourceOffsetBytes,
            destinationOffsetBytes,
            byteCount,
            BufferCopyKind.DeviceMigration));
    }

    internal bool Record(CommandBuffer commands)
    {
        if (_bufferCopies.Count == 0 && _imageCopies.Count == 0 && _mipBlits.Count == 0)
            return false;

        if (_imageEntryLayouts.Count > 0)
            TransitionImagesToTransfer(commands);

        foreach (BufferCopy2 copy in _bufferCopies)
        {
            if (RequiresDeviceMigrationReadBarrier(copy.Kind))
            {
                BufferMemoryBarrier2 barrier = CreateDeviceMigrationReadBarrier(
                    copy.Source,
                    copy.SourceOffset,
                    copy.SizeBytes);
                var dependency = new DependencyInfo
                {
                    SType = StructureType.DependencyInfo,
                    BufferMemoryBarrierCount = 1,
                    PBufferMemoryBarriers = &barrier,
                };
                _vk.CmdPipelineBarrier2(commands, &dependency);
            }

            var region = new BufferCopy
            {
                SrcOffset = copy.SourceOffset,
                DstOffset = copy.DestinationOffset,
                Size = copy.SizeBytes,
            };
            _vk.CmdCopyBuffer(commands, copy.Source, copy.Destination, 1, &region);
        }

        foreach (ImageCopy2 copy in _imageCopies)
        {
            var region = new BufferImageCopy
            {
                BufferOffset = copy.SourceOffset,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = copy.MipLevel,
                    BaseArrayLayer = copy.Layer,
                    LayerCount = 1,
                },
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = new Extent3D(copy.Width, copy.Height, 1),
            };
            _vk.CmdCopyBufferToImage(
                commands,
                copy.Source,
                copy.Destination,
                ImageLayout.TransferDstOptimal,
                1,
                &region);
        }

        foreach (MipBlitRequest blit in _mipBlits)
            RecordMipBlit(commands, blit);

        if (_imageEntryLayouts.Count > 0)
            TransitionImagesToShaderRead(commands);

        if (_bufferCopies.Count > 0)
        {
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AllTransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.VertexInputBit
                    | PipelineStageFlags2.VertexShaderBit
                    | PipelineStageFlags2.FragmentShaderBit
                    | PipelineStageFlags2.DrawIndirectBit,
                DstAccessMask = AccessFlags2.VertexAttributeReadBit
                    | AccessFlags2.IndexReadBit
                    | AccessFlags2.ShaderReadBit
                    | AccessFlags2.UniformReadBit
                    | AccessFlags2.IndirectCommandReadBit,
            };
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &barrier,
            };
            _vk.CmdPipelineBarrier2(commands, &dependency);
        }

        _bufferCopies.Clear();
        _imageCopies.Clear();
        _mipBlits.Clear();
        _imageEntryLayouts.Clear();
        return true;
    }

    internal static BufferMemoryBarrier2 CreateDeviceMigrationReadBarrier(
        Buffer source,
        ulong sourceOffsetBytes,
        ulong byteCount)
    {
        if (byteCount == 0)
            throw new ArgumentOutOfRangeException(nameof(byteCount));

        return new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllTransferBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.AllTransferBit,
            DstAccessMask = AccessFlags2.TransferReadBit,
            SrcQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
            Buffer = source,
            Offset = sourceOffsetBytes,
            Size = byteCount,
        };
    }

    internal static bool RequiresDeviceMigrationReadBarrier(BufferCopyKind kind) =>
        kind == BufferCopyKind.DeviceMigration;

    private static ImageSubresourceRange WholeColorImage => new()
    {
        AspectMask = ImageAspectFlags.ColorBit,
        BaseMipLevel = 0,
        LevelCount = Silk.NET.Vulkan.Vk.RemainingMipLevels,
        BaseArrayLayer = 0,
        LayerCount = Silk.NET.Vulkan.Vk.RemainingArrayLayers,
    };

    private void TransitionImagesToTransfer(CommandBuffer commands)
    {
        var barriers = new ImageMemoryBarrier2[_imageEntryLayouts.Count];
        int index = 0;
        foreach ((Image image, ImageLayout entryLayout) in _imageEntryLayouts)
        {
            barriers[index++] = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AllCommandsBit,
                SrcAccessMask = AccessFlags2.None,
                DstStageMask = PipelineStageFlags2.AllTransferBit,
                DstAccessMask = AccessFlags2.TransferWriteBit | AccessFlags2.TransferReadBit,
                OldLayout = entryLayout,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = WholeColorImage,
            };
        }

        SubmitBarriers(commands, barriers);
    }

    private void TransitionImagesToShaderRead(CommandBuffer commands)
    {
        var barriers = new ImageMemoryBarrier2[_imageEntryLayouts.Count];
        int index = 0;
        foreach (Image image in _imageEntryLayouts.Keys)
        {
            barriers[index++] = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AllTransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.VertexShaderBit,
                DstAccessMask = AccessFlags2.ShaderReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = WholeColorImage,
            };
        }

        SubmitBarriers(commands, barriers);
    }

    private void SubmitBarriers(CommandBuffer commands, ImageMemoryBarrier2[] barriers)
    {
        if (barriers.Length == 0)
            return;
        fixed (ImageMemoryBarrier2* first = barriers)
        {
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = (uint)barriers.Length,
                PImageMemoryBarriers = first,
            };
            _vk.CmdPipelineBarrier2(commands, &dependency);
        }
    }

    private void RecordMipBlit(CommandBuffer commands, in MipBlitRequest request)
    {
        int width = request.Width;
        int height = request.Height;

        for (uint level = 1; level < request.MipLevelCount; level++)
        {
            int nextWidth = Math.Max(1, width / 2);
            int nextHeight = Math.Max(1, height / 2);

            TransitionMipLevel(
                commands,
                request.Image,
                level - 1,
                ImageLayout.TransferDstOptimal,
                ImageLayout.TransferSrcOptimal,
                AccessFlags2.TransferWriteBit,
                AccessFlags2.TransferReadBit);

            var blit = new ImageBlit2
            {
                SType = StructureType.ImageBlit2,
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = level - 1,
                    BaseArrayLayer = 0,
                    LayerCount = (uint)request.LayerCount,
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = level,
                    BaseArrayLayer = 0,
                    LayerCount = (uint)request.LayerCount,
                },
            };
            blit.SrcOffsets.Element0 = new Offset3D(0, 0, 0);
            blit.SrcOffsets.Element1 = new Offset3D(width, height, 1);
            blit.DstOffsets.Element0 = new Offset3D(0, 0, 0);
            blit.DstOffsets.Element1 = new Offset3D(nextWidth, nextHeight, 1);

            var info = new BlitImageInfo2
            {
                SType = StructureType.BlitImageInfo2,
                SrcImage = request.Image,
                SrcImageLayout = ImageLayout.TransferSrcOptimal,
                DstImage = request.Image,
                DstImageLayout = ImageLayout.TransferDstOptimal,
                RegionCount = 1,
                PRegions = &blit,
                Filter = Filter.Linear,
            };
            _vk.CmdBlitImage2(commands, &info);

            TransitionMipLevel(
                commands,
                request.Image,
                level - 1,
                ImageLayout.TransferSrcOptimal,
                ImageLayout.TransferDstOptimal,
                AccessFlags2.TransferReadBit,
                AccessFlags2.TransferWriteBit);

            width = nextWidth;
            height = nextHeight;
        }
    }

    private void TransitionMipLevel(
        CommandBuffer commands,
        Image image,
        uint level,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        AccessFlags2 sourceAccess,
        AccessFlags2 destinationAccess)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllTransferBit,
            SrcAccessMask = sourceAccess,
            DstStageMask = PipelineStageFlags2.AllTransferBit,
            DstAccessMask = destinationAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = level,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = Silk.NET.Vulkan.Vk.RemainingArrayLayers,
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

    internal void ReleaseCompleted(long completedSerial) => _ringState.Release(completedSerial);

    private (Buffer Buffer, ulong Offset) Stage(
        ReadOnlySpan<byte> data,
        string ownerName,
        ulong alignmentBytes = 4)
    {
        long serial = _flights.OpenSerial != 0 ? _flights.OpenSerial : _flights.SubmittedSerial + 1;
        if (_ringState.TryAllocate(data.Length, alignmentBytes, serial, out ulong offset))
        {
            data.CopyTo(_stagingAllocation.AsSpan().Slice((int)offset, data.Length));
            return (_stagingBuffer, offset);
        }

        (Buffer temporary, VulkanAllocation allocation) = CreateHostBuffer(
            (ulong)data.Length,
            BufferUsageFlags.TransferSrcBit,
            $"vk-staging-temp-{ownerName}");
        data.CopyTo(allocation.AsSpan());
        _temporaries.Add(new TemporaryStaging(temporary, allocation));

        Buffer captured = temporary;
        VulkanAllocation capturedAllocation = allocation;
        _flights.Retire(() =>
        {
            _vk.DestroyBuffer(_device, captured, null);
            _allocator.Free(capturedAllocation);
            _temporaries.RemoveAll(entry => entry.Buffer.Handle == captured.Handle);
        });
        return (temporary, 0);
    }

    private (Buffer Buffer, VulkanAllocation Allocation) CreateHostBuffer(
        ulong sizeBytes,
        BufferUsageFlags usage,
        string name)
    {
        var create = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = sizeBytes,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        VulkanInterop.Check(
            _vk.CreateBuffer(_device, &create, null, out Buffer buffer),
            $"vkCreateBuffer ({name})");

        _vk.GetBufferMemoryRequirements(_device, buffer, out MemoryRequirements requirements);
        VulkanAllocation allocation = _allocator.Allocate(
            requirements,
            GpuMemoryResidency.HostWritable,
            name);
        VulkanInterop.Check(
            _vk.BindBufferMemory(_device, buffer, allocation.Memory, allocation.OffsetBytes),
            $"vkBindBufferMemory ({name})");
        _debugNames.NameBuffer(buffer, name);
        return (buffer, allocation);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (TemporaryStaging temporary in _temporaries)
        {
            _vk.DestroyBuffer(_device, temporary.Buffer, null);
            _allocator.Free(temporary.Allocation);
        }
        _temporaries.Clear();

        _vk.DestroyBuffer(_device, _stagingBuffer, null);
        _allocator.Free(_stagingAllocation);
        _ringState.Reset();
        _bufferCopies.Clear();
        _imageCopies.Clear();
        
    }
}
