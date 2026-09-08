using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

/// <summary>
/// A live suballocation: the device memory it sits in, where, and — for
/// host-visible memory — a pointer to its first byte inside the block's
/// persistent mapping.
/// </summary>
internal readonly unsafe struct VulkanAllocation(
    DeviceMemory memory,
    ulong offsetBytes,
    ulong sizeBytes,
    uint memoryTypeIndex,
    MemoryPropertyFlags memoryProperties,
    VulkanMemoryRange range,
    void* mapped)
{
    internal DeviceMemory Memory { get; } = memory;
    internal ulong OffsetBytes { get; } = offsetBytes;
    internal ulong SizeBytes { get; } = sizeBytes;
    internal uint MemoryTypeIndex { get; } = memoryTypeIndex;
    internal MemoryPropertyFlags MemoryProperties { get; } = memoryProperties;
    internal VulkanMemoryRange Range { get; } = range;

    /// <summary>First mapped byte of this allocation, or null on device-local memory.</summary>
    internal void* Mapped { get; } = mapped;

    internal bool IsMapped => Mapped is not null;

    internal Span<byte> AsSpan() => IsMapped
        ? new Span<byte>(Mapped, checked((int)SizeBytes))
        : throw new InvalidOperationException(
            "This allocation lives in device-local memory and has no CPU mapping.");
}

internal interface IVulkanDeviceMemoryBackend
{
    Result AllocateMemory(uint typeIndex, ulong sizeBytes, out DeviceMemory memory);

    Result MapMemory(DeviceMemory memory, ulong sizeBytes, out nint pointer);

    void UnmapMemory(DeviceMemory memory);

    void FreeMemory(DeviceMemory memory);
}

/// <summary>The real backend: the same four Vulkan entry points, unchanged.</summary>
internal sealed unsafe class VkDeviceMemoryBackend(Silk.NET.Vulkan.Vk vk, Device device)
    : IVulkanDeviceMemoryBackend
{
    private readonly Silk.NET.Vulkan.Vk _vk = vk ?? throw new ArgumentNullException(nameof(vk));
    private readonly Device _device = device;

    public Result AllocateMemory(uint typeIndex, ulong sizeBytes, out DeviceMemory memory)
    {
        var allocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = sizeBytes,
            MemoryTypeIndex = typeIndex,
        };
        return _vk.AllocateMemory(_device, &allocate, null, out memory);
    }

    public Result MapMemory(DeviceMemory memory, ulong sizeBytes, out nint pointer)
    {
        void* value = null;
        Result result = _vk.MapMemory(_device, memory, 0, sizeBytes, 0, &value);
        pointer = (nint)value;
        return result;
    }

    public void UnmapMemory(DeviceMemory memory) => _vk.UnmapMemory(_device, memory);

    public void FreeMemory(DeviceMemory memory) => _vk.FreeMemory(_device, memory, null);
}

internal sealed unsafe class VulkanDeviceMemoryAllocator : IDisposable
{
    private readonly IVulkanDeviceMemoryBackend _backend;
    private readonly MemoryPropertyFlags[] _memoryTypeProperties;
    private readonly ulong _blockSizeBytes;
    private readonly ulong _dedicatedThresholdBytes;
    private readonly object _sync = new();

    private readonly Dictionary<uint, VulkanMemoryTypePool> _pools = [];
    private readonly Dictionary<(uint TypeIndex, int BlockIndex), BlockMemory> _blockMemory = [];

    private readonly HashSet<uint> _exhaustedTypes = [];

    private bool _disposed;

    private readonly record struct BlockMemory(DeviceMemory Memory, nint Mapped, ulong CapacityBytes);

    internal VulkanDeviceMemoryAllocator(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice physicalDevice,
        Device device,
        ulong blockSizeBytes = VulkanMemoryTypePool.DefaultBlockSizeBytes,
        ulong dedicatedThresholdBytes = VulkanMemoryTypePool.DefaultDedicatedThresholdBytes)
        : this(
            ReadMemoryTypeProperties(vk ?? throw new ArgumentNullException(nameof(vk)), physicalDevice),
            new VkDeviceMemoryBackend(vk, device),
            blockSizeBytes,
            dedicatedThresholdBytes)
    {
    }

    /// <summary>Test seam: the same allocator over a stand-in driver.</summary>
    internal VulkanDeviceMemoryAllocator(
        IVulkanDeviceMemoryBackend backend,
        IReadOnlyList<MemoryPropertyFlags> memoryTypeProperties,
        ulong blockSizeBytes = VulkanMemoryTypePool.DefaultBlockSizeBytes,
        ulong dedicatedThresholdBytes = VulkanMemoryTypePool.DefaultDedicatedThresholdBytes)
        : this(
            [.. memoryTypeProperties ?? throw new ArgumentNullException(nameof(memoryTypeProperties))],
            backend ?? throw new ArgumentNullException(nameof(backend)),
            blockSizeBytes,
            dedicatedThresholdBytes)
    {
    }

    private VulkanDeviceMemoryAllocator(
        MemoryPropertyFlags[] memoryTypeProperties,
        IVulkanDeviceMemoryBackend backend,
        ulong blockSizeBytes,
        ulong dedicatedThresholdBytes)
    {
        _memoryTypeProperties = memoryTypeProperties;
        _backend = backend;
        _blockSizeBytes = blockSizeBytes;
        _dedicatedThresholdBytes = dedicatedThresholdBytes;
    }

    private static MemoryPropertyFlags[] ReadMemoryTypeProperties(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice physicalDevice)
    {
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out PhysicalDeviceMemoryProperties properties);
        var flags = new MemoryPropertyFlags[properties.MemoryTypeCount];
        for (uint i = 0; i < properties.MemoryTypeCount && i < 32; i++)
            flags[i] = properties.MemoryTypes[(int)i].PropertyFlags;
        return flags;
    }

    /// <summary>Live <c>vkAllocateMemory</c> objects. Kept two orders of magnitude below the device limit by design.</summary>
    internal int DeviceMemoryObjectCount => _blockMemory.Count;

    /// <summary>Blocks the pools still hold, across every memory type.</summary>
    internal int LiveBlockCount
    {
        get
        {
            lock (_sync)
            {
                int total = 0;
                foreach (VulkanMemoryTypePool pool in _pools.Values)
                    total += pool.LiveBlockCount;
                return total;
            }
        }
    }

    internal ulong AllocatedBytes { get; private set; }

    internal ulong CommittedBytes { get; private set; }

    internal IReadOnlyList<MemoryPropertyFlags> MemoryTypeProperties => _memoryTypeProperties;

    internal VulkanAllocation Allocate(
        in MemoryRequirements requirements,
        GpuMemoryResidency residency,
        string ownerName)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            IReadOnlyList<uint> candidates = VulkanMemoryTypeSelection.ChooseAll(
                _memoryTypeProperties,
                requirements.MemoryTypeBits,
                residency);
            if (candidates.Count == 0)
            {
                throw new NotSupportedException(
                    $"No Vulkan memory type satisfies {residency} for '{ownerName}'. " +
                    $"Allowed type bits 0x{requirements.MemoryTypeBits:X8}; the device exposes " +
                    $"{_memoryTypeProperties.Length} memory types.");
            }

            ulong size = requirements.Size;
            ulong alignment = Math.Max(requirements.Alignment, 1);
            Result lastResult = Result.ErrorOutOfDeviceMemory;

            foreach (uint typeIndex in candidates)
            {
                if (_exhaustedTypes.Contains(typeIndex))
                    continue;

                if (!_pools.TryGetValue(typeIndex, out VulkanMemoryTypePool? pool))
                {
                    pool = new VulkanMemoryTypePool(typeIndex, _blockSizeBytes, _dedicatedThresholdBytes);
                    _pools.Add(typeIndex, pool);
                }

                if (!pool.TryAllocate(size, alignment, out VulkanMemoryRange range))
                {
                    bool dedicated = pool.IsDedicatedSize(size);
                    ulong capacity = Math.Max(pool.BlockCapacityFor(size), size);

                    Result created = TryCreateBlockMemory(
                        typeIndex,
                        capacity,
                        ownerName,
                        out BlockMemory memory,
                        out string operation);
                    if (created != Result.Success)
                    {
                        lastResult = created;
                        if (created is Result.ErrorOutOfDeviceMemory or Result.ErrorOutOfHostMemory)
                        {
                            _exhaustedTypes.Add(typeIndex);
                            continue;
                        }

                        throw new VulkanCallException(operation, created);
                    }

                    int blockIndex = pool.AddBlock(capacity, dedicated);
                    _blockMemory[(typeIndex, blockIndex)] = memory;
                    CommittedBytes += capacity;

                    range = dedicated
                        ? pool.AllocateWholeBlock(blockIndex, size)
                        : pool.TryAllocate(size, alignment, out VulkanMemoryRange placed)
                            ? placed
                            : throw new InvalidOperationException(
                                $"A freshly created {capacity}-byte block could not satisfy a {size}-byte " +
                                $"allocation at alignment {alignment} for '{ownerName}'.");
                }

                BlockMemory block = _blockMemory[(typeIndex, range.BlockIndex)];
                AllocatedBytes += range.SizeBytes;
                void* mapped = block.Mapped == 0
                    ? null
                    : (void*)(block.Mapped + (nint)range.OffsetBytes);
                return new VulkanAllocation(
                    block.Memory,
                    range.OffsetBytes,
                    range.SizeBytes,
                    typeIndex,
                    _memoryTypeProperties[(int)typeIndex],
                    range,
                    mapped);
            }

            throw new VulkanCallException(
                $"vkAllocateMemory ({size} bytes for '{ownerName}', {residency}) — every candidate " +
                $"memory type [{string.Join(", ", candidates)}] refused it " +
                $"(exhausted: [{string.Join(", ", _exhaustedTypes.Order())}])",
                lastResult);
        }
    }

    /// <summary>Returns an allocation's bytes to its pool, freeing the block when a dedicated one empties.</summary>
    internal void Free(in VulkanAllocation allocation)
    {
        lock (_sync)
        {
            if (_disposed || allocation.SizeBytes == 0)
                return;
            if (!_pools.TryGetValue(allocation.MemoryTypeIndex, out VulkanMemoryTypePool? pool))
                return;

            AllocatedBytes -= Math.Min(AllocatedBytes, allocation.Range.SizeBytes);
            if (!pool.Free(allocation.Range))
                return;

            var key = (allocation.MemoryTypeIndex, allocation.Range.BlockIndex);
            if (!_blockMemory.Remove(key, out BlockMemory block))
                return;

            if (block.Mapped != 0)
                _backend.UnmapMemory(block.Memory);
            _backend.FreeMemory(block.Memory);
            CommittedBytes -= Math.Min(CommittedBytes, block.CapacityBytes);
        }
    }

    private Result TryCreateBlockMemory(
        uint typeIndex,
        ulong capacityBytes,
        string ownerName,
        out BlockMemory block,
        out string operation)
    {
        block = default;
        operation = $"vkAllocateMemory ({capacityBytes} bytes on memory type {typeIndex} for '{ownerName}')";

        Result result = _backend.AllocateMemory(typeIndex, capacityBytes, out DeviceMemory memory);
        if (result != Result.Success)
            return result;

        nint mapped = 0;
        if (_memoryTypeProperties[(int)typeIndex].HasFlag(MemoryPropertyFlags.HostVisibleBit))
        {
            Result mapResult = _backend.MapMemory(memory, capacityBytes, out nint pointer);
            if (mapResult != Result.Success)
            {
                _backend.FreeMemory(memory);
                operation = $"vkMapMemory (block on memory type {typeIndex} for '{ownerName}')";
                return mapResult;
            }

            mapped = pointer;
        }

        block = new BlockMemory(memory, mapped, capacityBytes);
        return Result.Success;
    }

    /// <summary>Human-readable accounting for the diagnostics report and for teardown assertions.</summary>
    internal string Describe()
    {
        lock (_sync)
        {
            string description = $"{DeviceMemoryObjectCount} device-memory object(s), "
                + $"{CommittedBytes / (1024 * 1024)} MiB committed, "
                + $"{AllocatedBytes / (1024 * 1024)} MiB allocated";
            if (_exhaustedTypes.Count > 0)
                description += $"; exhausted memory types: {string.Join(", ", _exhaustedTypes.Order())}";
            return description;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (BlockMemory block in _blockMemory.Values)
            {
                if (block.Mapped != 0)
                    _backend.UnmapMemory(block.Memory);
                _backend.FreeMemory(block.Memory);
            }

            _blockMemory.Clear();
            _pools.Clear();
            _exhaustedTypes.Clear();
            AllocatedBytes = 0;
            CommittedBytes = 0;
        }
    }
}
