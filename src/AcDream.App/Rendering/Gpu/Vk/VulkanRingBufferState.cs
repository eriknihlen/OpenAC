namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed class VulkanRingBufferState
{
    private ulong _cursor;

    internal VulkanRingBufferState(ulong capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfZero(capacityBytes);
        CapacityBytes = capacityBytes;
    }

    internal ulong CapacityBytes { get; }

    internal ulong AllocatedBytes => _cursor;

    internal ulong PeakAllocatedBytes { get; private set; }

    internal ulong Allocate(int byteCount, ulong alignmentBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        ulong aligned = VulkanMemoryBlockFreeList.AlignUp(_cursor, alignmentBytes);
        ulong end = aligned + (ulong)byteCount;
        if (end > CapacityBytes)
        {
            throw new InvalidOperationException(
                $"Ring allocation of {byteCount} bytes at aligned offset {aligned} needs {end} bytes; " +
                $"this flight slot's ring is {CapacityBytes} bytes. Increase the per-slot ring " +
                "capacity (VulkanGpuDevice's ringCapacityBytesPerSlot).");
        }

        _cursor = end;
        PeakAllocatedBytes = Math.Max(PeakAllocatedBytes, end);
        return aligned;
    }

    internal void Reset() => _cursor = 0;
}

internal sealed class VulkanStagingRingState
{
    private readonly List<PendingSegment> _pending = [];
    private ulong _head;
    private ulong _tail;
    private ulong _live;

    private readonly record struct PendingSegment(long Serial, ulong SizeBytes);

    internal VulkanStagingRingState(ulong capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfZero(capacityBytes);
        CapacityBytes = capacityBytes;
    }

    internal ulong CapacityBytes { get; }

    /// <summary>Bytes reserved by frames that have not yet retired.</summary>
    internal ulong LiveBytes => _live;

    internal int PendingSegmentCount => _pending.Count;

    internal bool TryAllocate(int byteCount, ulong alignmentBytes, long serial, out ulong offsetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        offsetBytes = 0;
        if (byteCount == 0)
            return true;

        var size = (ulong)byteCount;
        if (size > CapacityBytes)
            return false;

        ulong aligned = VulkanMemoryBlockFreeList.AlignUp(_head, alignmentBytes);
        ulong consumed;
        if (aligned + size > CapacityBytes)
        {
            ulong wasted = CapacityBytes - _head;
            aligned = 0;
            consumed = wasted + size;
        }
        else
        {
            consumed = (aligned - _head) + size;
        }

        if (_live + consumed > CapacityBytes)
            return false;

        _head = (aligned + size) % CapacityBytes;
        _live += consumed;
        offsetBytes = aligned;

        if (_pending.Count > 0 && _pending[^1].Serial == serial)
        {
            PendingSegment last = _pending[^1];
            _pending[^1] = last with { SizeBytes = last.SizeBytes + consumed };
        }
        else
        {
            _pending.Add(new PendingSegment(serial, consumed));
        }

        return true;
    }

    internal void Release(long completedSerial)
    {
        int released = 0;
        while (released < _pending.Count && _pending[released].Serial <= completedSerial)
        {
            _tail = (_tail + _pending[released].SizeBytes) % CapacityBytes;
            _live -= Math.Min(_live, _pending[released].SizeBytes);
            released++;
        }

        if (released > 0)
            _pending.RemoveRange(0, released);
    }

    /// <summary>Drops all bookkeeping. Only legal when the device is idle.</summary>
    internal void Reset()
    {
        _pending.Clear();
        _head = 0;
        _tail = 0;
        _live = 0;
    }
}
