using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed unsafe class VulkanGpuBuffer : IGpuBuffer
{
    private readonly Silk.NET.Vulkan.Vk _vk;
    private readonly Device _device;
    private readonly VulkanDeviceMemoryAllocator _allocator;
    private readonly VulkanUploadQueue _uploads;
    private readonly IGpuResourceRetirementQueue _retirement;
    private readonly VulkanAllocation _allocation;
    private bool _disposed;

    internal VulkanGpuBuffer(
        Silk.NET.Vulkan.Vk vk,
        Device device,
        VulkanDeviceMemoryAllocator allocator,
        VulkanUploadQueue uploads,
        IGpuResourceRetirementQueue retirement,
        VulkanDebugNames debugNames,
        in GpuBufferDescription description)
    {
        _vk = vk ?? throw new ArgumentNullException(nameof(vk));
        _device = device;
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _uploads = uploads ?? throw new ArgumentNullException(nameof(uploads));
        _retirement = retirement ?? throw new ArgumentNullException(nameof(retirement));
        ArgumentException.ThrowIfNullOrWhiteSpace(description.Name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.SizeBytes);

        Name = description.Name;
        SizeBytes = description.SizeBytes;
        Usage = description.Usage;
        Residency = description.Residency;

        var create = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)description.SizeBytes,
            Usage = UsageFlagsOf(description.Usage),
            SharingMode = SharingMode.Exclusive,
        };
        VulkanInterop.Check(
            _vk.CreateBuffer(_device, &create, null, out Buffer handle),
            $"vkCreateBuffer ('{description.Name}')");
        Handle = handle;

        try
        {
            _vk.GetBufferMemoryRequirements(_device, handle, out MemoryRequirements requirements);
            _allocation = _allocator.Allocate(requirements, description.Residency, description.Name);
            VulkanInterop.Check(
                _vk.BindBufferMemory(_device, handle, _allocation.Memory, _allocation.OffsetBytes),
                $"vkBindBufferMemory ('{description.Name}')");
        }
        catch
        {
            _vk.DestroyBuffer(_device, handle, null);
            throw;
        }

        debugNames.NameBuffer(handle, description.Name);
    }

    public string Name { get; }
    public long SizeBytes { get; }
    public GpuBufferUsage Usage { get; }
    public GpuMemoryResidency Residency { get; }
    public bool HostWritesAreCoherent =>
        _allocation.MemoryProperties.HasFlag(MemoryPropertyFlags.HostCoherentBit);

    internal Buffer Handle { get; }

    /// <summary>True when this buffer's memory is persistently mapped and directly writable.</summary>
    internal bool IsMapped => _allocation.IsMapped;

    /// <summary>The buffer's whole mapped range. Only valid on host-visible residency.</summary>
    internal Span<byte> MappedSpan => _allocation.AsSpan()[..checked((int)SizeBytes)];

    public void Upload(long offsetBytes, ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(offsetBytes);
        if (data.IsEmpty)
            return;
        if (offsetBytes + data.Length > SizeBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                $"Writing {data.Length} bytes at offset {offsetBytes} exceeds " +
                $"'{Name}' ({SizeBytes} bytes).");
        }

        if (_allocation.IsMapped)
        {
            data.CopyTo(MappedSpan.Slice((int)offsetBytes, data.Length));
            return;
        }

        _uploads.StageBufferWrite(Handle, (ulong)offsetBytes, data, Name);
    }

    public void CopyTo(
        IGpuBuffer destination,
        long sourceOffsetBytes,
        long destinationOffsetBytes,
        long byteCount)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        if (destination is not VulkanGpuBuffer target)
        {
            throw new ArgumentException(
                "The Vulkan backend can only copy into a Vulkan buffer.",
                nameof(destination));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        if (byteCount == 0)
            return;
        if (sourceOffsetBytes + byteCount > SizeBytes)
            throw new ArgumentOutOfRangeException(nameof(byteCount), $"The copy reads past the end of '{Name}'.");
        if (destinationOffsetBytes + byteCount > target.SizeBytes)
            throw new ArgumentOutOfRangeException(nameof(byteCount), $"The copy writes past the end of '{target.Name}'.");

        _uploads.EnqueueBufferCopy(
            Handle,
            target.Handle,
            (ulong)sourceOffsetBytes,
            (ulong)destinationOffsetBytes,
            (ulong)byteCount);
    }

    public void Read(long offsetBytes, Span<byte> destination)
    {
        ThrowIfDisposed();
        if (Residency != GpuMemoryResidency.HostReadable)
        {
            throw new InvalidOperationException(
                $"'{Name}' has {Residency} residency; only HostReadable buffers can be read back. " +
                "This path is diagnostics-only by design.");
        }

        if (destination.IsEmpty)
            return;
        if (offsetBytes + destination.Length > SizeBytes)
            throw new ArgumentOutOfRangeException(nameof(destination), $"The read runs past the end of '{Name}'.");

        MappedSpan.Slice((int)offsetBytes, destination.Length).CopyTo(destination);
    }

    internal static BufferUsageFlags UsageFlagsOf(GpuBufferUsage usage)
    {
        BufferUsageFlags flags = BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit;
        if (usage.HasFlag(GpuBufferUsage.Vertex))
            flags |= BufferUsageFlags.VertexBufferBit;
        if (usage.HasFlag(GpuBufferUsage.Index))
            flags |= BufferUsageFlags.IndexBufferBit;
        if (usage.HasFlag(GpuBufferUsage.Storage))
            flags |= BufferUsageFlags.StorageBufferBit;
        if (usage.HasFlag(GpuBufferUsage.Uniform))
            flags |= BufferUsageFlags.UniformBufferBit;
        if (usage.HasFlag(GpuBufferUsage.Indirect))
            flags |= BufferUsageFlags.IndirectBufferBit;
        return flags;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Buffer handle = Handle;
        VulkanAllocation allocation = _allocation;
        _retirement.Retire(() =>
        {
            _vk.DestroyBuffer(_device, handle, null);
            _allocator.Free(allocation);
        });
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
