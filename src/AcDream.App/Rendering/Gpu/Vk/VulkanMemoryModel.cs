using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal readonly record struct VulkanMemoryRange(
    int BlockIndex,
    ulong OffsetBytes,
    ulong SizeBytes,
    bool IsDedicated);

internal sealed class VulkanMemoryBlockFreeList
{
    private readonly List<Range> _free = [];

    private struct Range(ulong offset, ulong size)
    {
        public ulong Offset = offset;
        public ulong Size = size;
        public readonly ulong End => Offset + Size;
    }

    internal VulkanMemoryBlockFreeList(ulong capacityBytes)
    {
        if (capacityBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), "A memory block cannot be empty.");
        CapacityBytes = capacityBytes;
        _free.Add(new Range(0, capacityBytes));
    }

    internal ulong CapacityBytes { get; }

    internal ulong UsedBytes { get; private set; }

    internal ulong FreeBytes => CapacityBytes - UsedBytes;

    internal ulong LargestFreeBytes
    {
        get
        {
            ulong largest = 0;
            foreach (Range range in _free)
                largest = Math.Max(largest, range.Size);
            return largest;
        }
    }

    internal int FreeRangeCount => _free.Count;

    internal bool TryAllocate(ulong sizeBytes, ulong alignmentBytes, out ulong offsetBytes)
    {
        offsetBytes = 0;
        if (sizeBytes == 0)
            return false;

        for (int i = 0; i < _free.Count; i++)
        {
            Range range = _free[i];
            ulong aligned = AlignUp(range.Offset, alignmentBytes);
            ulong padding = aligned - range.Offset;
            if (padding > range.Size || range.Size - padding < sizeBytes)
                continue;

            // The allocation owns [range.Offset, aligned + size) so releasing it
            // returns the alignment padding too.
            ulong consumed = padding + sizeBytes;
            if (consumed == range.Size)
            {
                _free.RemoveAt(i);
            }
            else
            {
                range.Offset += consumed;
                range.Size -= consumed;
                _free[i] = range;
            }

            UsedBytes += consumed;
            offsetBytes = aligned;
            AllocatedPaddingByOffset[aligned] = padding;
            return true;
        }

        return false;
    }

    private Dictionary<ulong, ulong> AllocatedPaddingByOffset { get; } = [];

    internal void Free(ulong offsetBytes, ulong sizeBytes)
    {
        if (sizeBytes == 0)
            return;

        ulong padding = 0;
        if (AllocatedPaddingByOffset.Remove(offsetBytes, out ulong recorded))
            padding = recorded;

        ulong start = offsetBytes - padding;
        ulong end = offsetBytes + sizeBytes;
        ulong length = end - start;
        if (length > UsedBytes)
        {
            throw new InvalidOperationException(
                $"Releasing [{start}, {end}) would free more than the {UsedBytes} bytes this block " +
                "has handed out — the same range was probably released twice.");
        }

        UsedBytes -= length;

        int insertAt = _free.FindIndex(range => range.Offset > start);
        if (insertAt < 0)
            insertAt = _free.Count;
        _free.Insert(insertAt, new Range(start, length));
        Coalesce(insertAt);
    }

    private void Coalesce(int index)
    {
        if (index > 0 && _free[index - 1].End == _free[index].Offset)
        {
            Range merged = _free[index - 1];
            merged.Size += _free[index].Size;
            _free[index - 1] = merged;
            _free.RemoveAt(index);
            index--;
        }

        if (index + 1 < _free.Count && _free[index].End == _free[index + 1].Offset)
        {
            Range merged = _free[index];
            merged.Size += _free[index + 1].Size;
            _free[index] = merged;
            _free.RemoveAt(index + 1);
        }
    }

    internal static ulong AlignUp(ulong value, ulong alignmentBytes) =>
        alignmentBytes <= 1 ? value : (value + alignmentBytes - 1) / alignmentBytes * alignmentBytes;
}

internal sealed class VulkanMemoryTypePool
{
    /// <summary>Plan §4.2's block size: 128 MiB device-local blocks per memory type.</summary>
    internal const ulong DefaultBlockSizeBytes = 128UL * 1024 * 1024;

    /// <summary>Plan §4.2: allocations at or above this size take a block of their own.</summary>
    internal const ulong DefaultDedicatedThresholdBytes = 32UL * 1024 * 1024;

    private readonly List<VulkanMemoryBlockFreeList?> _blocks = [];
    private readonly HashSet<int> _dedicatedBlocks = [];

    internal VulkanMemoryTypePool(
        uint memoryTypeIndex,
        ulong blockSizeBytes = DefaultBlockSizeBytes,
        ulong dedicatedThresholdBytes = DefaultDedicatedThresholdBytes)
    {
        ArgumentOutOfRangeException.ThrowIfZero(blockSizeBytes);
        ArgumentOutOfRangeException.ThrowIfZero(dedicatedThresholdBytes);
        MemoryTypeIndex = memoryTypeIndex;
        BlockSizeBytes = blockSizeBytes;
        DedicatedThresholdBytes = dedicatedThresholdBytes;
    }

    internal uint MemoryTypeIndex { get; }

    internal ulong BlockSizeBytes { get; }

    internal ulong DedicatedThresholdBytes { get; }

    internal int BlockCount => _blocks.Count;

    /// <summary>Blocks that still exist, i.e. have been added and not retired.</summary>
    internal int LiveBlockCount => _blocks.Count(block => block is not null);

    internal bool IsDedicatedSize(ulong sizeBytes) => sizeBytes >= DedicatedThresholdBytes;

    internal ulong BlockCapacityFor(ulong sizeBytes) =>
        IsDedicatedSize(sizeBytes) ? sizeBytes : BlockSizeBytes;

    internal bool TryAllocate(ulong sizeBytes, ulong alignmentBytes, out VulkanMemoryRange range)
    {
        range = default;
        if (sizeBytes == 0)
            return false;

        // A dedicated-size request never shares: hunting for a hole big enough
        // in a shared block would find one only in a nearly empty block, and
        // taking it would strand the rest.
        if (IsDedicatedSize(sizeBytes))
            return false;

        for (int i = 0; i < _blocks.Count; i++)
        {
            if (_blocks[i] is not { } block || _dedicatedBlocks.Contains(i))
                continue;
            if (block.TryAllocate(sizeBytes, alignmentBytes, out ulong offset))
            {
                range = new VulkanMemoryRange(i, offset, sizeBytes, IsDedicated: false);
                return true;
            }
        }

        return false;
    }

    internal int AddBlock(ulong capacityBytes, bool dedicated)
    {
        _blocks.Add(new VulkanMemoryBlockFreeList(capacityBytes));
        int index = _blocks.Count - 1;
        if (dedicated)
            _dedicatedBlocks.Add(index);
        return index;
    }

    internal VulkanMemoryRange AllocateWholeBlock(int blockIndex, ulong sizeBytes)
    {
        VulkanMemoryBlockFreeList block = BlockAt(blockIndex);
        if (!block.TryAllocate(sizeBytes, 1, out ulong offset) || offset != 0)
        {
            throw new InvalidOperationException(
                $"Block {blockIndex} was created for a dedicated {sizeBytes}-byte allocation " +
                "but could not satisfy it at offset 0.");
        }

        return new VulkanMemoryRange(blockIndex, 0, sizeBytes, IsDedicated: true);
    }

    internal bool Free(in VulkanMemoryRange range)
    {
        VulkanMemoryBlockFreeList block = BlockAt(range.BlockIndex);
        block.Free(range.OffsetBytes, range.SizeBytes);
        if (block.UsedBytes != 0 || !_dedicatedBlocks.Contains(range.BlockIndex))
            return false;

        _blocks[range.BlockIndex] = null;
        _dedicatedBlocks.Remove(range.BlockIndex);
        return true;
    }

    internal ulong UsedBytes
    {
        get
        {
            ulong total = 0;
            foreach (VulkanMemoryBlockFreeList? block in _blocks)
                total += block?.UsedBytes ?? 0;
            return total;
        }
    }

    internal ulong CapacityBytes
    {
        get
        {
            ulong total = 0;
            foreach (VulkanMemoryBlockFreeList? block in _blocks)
                total += block?.CapacityBytes ?? 0;
            return total;
        }
    }

    private VulkanMemoryBlockFreeList BlockAt(int index) =>
        index >= 0 && index < _blocks.Count && _blocks[index] is { } block
            ? block
            : throw new ArgumentOutOfRangeException(
                nameof(index),
                $"Memory block {index} does not exist in the pool for memory type {MemoryTypeIndex}.");
}

internal static class VulkanMemoryTypeSelection
{
    internal static IReadOnlyList<MemoryPropertyFlags> PreferenceOrder(GpuMemoryResidency residency) =>
        residency switch
        {
            GpuMemoryResidency.DeviceLocal =>
            [
                MemoryPropertyFlags.DeviceLocalBit,
                0,
            ],
            GpuMemoryResidency.HostWritable =>
            [
                // ReBAR: device-local AND host-visible. The whole point of §4.3.
                MemoryPropertyFlags.DeviceLocalBit
                    | MemoryPropertyFlags.HostVisibleBit
                    | MemoryPropertyFlags.HostCoherentBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                MemoryPropertyFlags.HostVisibleBit,
            ],
            GpuMemoryResidency.HostReadable =>
            [
                MemoryPropertyFlags.HostVisibleBit
                    | MemoryPropertyFlags.HostCoherentBit
                    | MemoryPropertyFlags.HostCachedBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                MemoryPropertyFlags.HostVisibleBit,
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(residency), residency, "Unknown residency class."),
        };

    internal static bool RequiresMapping(GpuMemoryResidency residency) =>
        residency is GpuMemoryResidency.HostWritable or GpuMemoryResidency.HostReadable;

    internal static IReadOnlyList<uint> ChooseAll(
        IReadOnlyList<MemoryPropertyFlags> memoryTypeProperties,
        uint allowedTypeBits,
        GpuMemoryResidency residency)
    {
        ArgumentNullException.ThrowIfNull(memoryTypeProperties);

        List<uint> candidates = [];
        foreach (MemoryPropertyFlags required in PreferenceOrder(residency))
        {
            for (int i = 0; i < memoryTypeProperties.Count && i < 32; i++)
            {
                if ((allowedTypeBits & (1u << i)) == 0)
                    continue;
                if ((memoryTypeProperties[i] & required) != required)
                    continue;
                if (!candidates.Contains((uint)i))
                    candidates.Add((uint)i);
            }
        }

        return candidates;
    }

    internal static uint? Choose(
        IReadOnlyList<MemoryPropertyFlags> memoryTypeProperties,
        uint allowedTypeBits,
        GpuMemoryResidency residency)
    {
        IReadOnlyList<uint> candidates = ChooseAll(memoryTypeProperties, allowedTypeBits, residency);
        return candidates.Count == 0 ? null : candidates[0];
    }
}
