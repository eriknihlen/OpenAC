using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Rendering;

internal readonly record struct DirectionalShadowTransformPublishStats(
    bool TopologyUploaded,
    int DynamicMatricesUpdated,
    int DynamicRangesUpdated,
    long BytesWritten,
    int CurrentChangedMatrices = 0,
    int PendingReplayMatrices = 0,
    bool UsedFullDynamicFallback = false,
    bool DenseDirectUpload = false,
    bool DenseFlightReplay = false);

internal sealed class DirectionalShadowTransformBufferSet : IDisposable
{
    private readonly IGpuDevice _device;
    private SlotState[] _slots = [];
    private ulong _denseTopologyBuildSequence;
    private ulong _denseRevision = 1;
    private bool _disposed;

    internal DirectionalShadowTransformBufferSet(IGpuDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        if (!device.Capabilities.SupportsPersistentlyMappedRings)
        {
            throw new NotSupportedException(
                "Directional-shadow retained transforms require persistently mapped host-writable buffers.");
        }
    }

    internal long RetainedGpuBytes
    {
        get
        {
            long total = 0;
            for (int i = 0; i < _slots.Length; i++)
                total = checked(total + (_slots[i].Buffer?.SizeBytes ?? 0L));
            return total;
        }
    }

    internal int BufferCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Buffer is not null)
                    count++;
            }
            return count;
        }
    }

    internal long RetainedScratchBytes
    {
        get
        {
            long bytes = checked((long)_slots.Length
                * System.Runtime.CompilerServices.Unsafe.SizeOf<SlotState>());
            for (int index = 0; index < _slots.Length; index++)
                bytes = checked(bytes + (_slots[index].Pending?.RetainedBytes ?? 0L));
            return bytes;
        }
    }

    internal DirectionalShadowTransformPublishStats LastStats { get; private set; }

    internal WorldTransformFrameSlice Publish(
        IGpuFrame frame,
        ulong topologyBuildSequence,
        ReadOnlySpan<Matrix4x4> transforms,
        ReadOnlySpan<int> dynamicTransformSlots)
    {
        return Publish(
            frame,
            topologyBuildSequence,
            transforms,
            dynamicTransformSlots,
            dynamicTransformSlots,
            denseRefresh: false);
    }

    internal WorldTransformFrameSlice Publish(
        IGpuFrame frame,
        ulong topologyBuildSequence,
        ReadOnlySpan<Matrix4x4> transforms,
        ReadOnlySpan<int> dynamicTransformSlots,
        ReadOnlySpan<int> allDynamicTransformSlots)
    {
        return Publish(
            frame,
            topologyBuildSequence,
            transforms,
            dynamicTransformSlots,
            allDynamicTransformSlots,
            denseRefresh: false);
    }

    internal WorldTransformFrameSlice Publish(
        IGpuFrame frame,
        ulong topologyBuildSequence,
        ReadOnlySpan<Matrix4x4> transforms,
        ReadOnlySpan<int> dynamicTransformSlots,
        ReadOnlySpan<int> allDynamicTransformSlots,
        bool denseRefresh,
        uint bindingSizeBytes = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (topologyBuildSequence == 0)
            throw new ArgumentOutOfRangeException(nameof(topologyBuildSequence));
        uint requiredInstances = checked((uint)transforms.Length);
        if (bindingSizeBytes == 0)
        {
            bindingSizeBytes = WorldTransformCapacityPolicy.ResolveBindingSizeBytes(
                requiredInstances,
                _device.Capabilities.MaxStorageBufferRangeBytes);
        }
        WorldTransformCapacityPolicy.ValidateBindingSizeBytes(
            bindingSizeBytes,
            requiredInstances,
            _device.Capabilities.MaxStorageBufferRangeBytes);
        if (!denseRefresh)
            ValidateDynamicSlots(dynamicTransformSlots, transforms.Length);
        ResetDenseRevisionForTopology(topologyBuildSequence);
        if (denseRefresh)
        {
            ValidateDynamicSlots(allDynamicTransformSlots, transforms.Length);
            if (_denseRevision == ulong.MaxValue)
            {
                throw new InvalidOperationException(
                    "Directional-shadow dense transform revision was exhausted.");
            }
            _denseRevision++;
            for (int index = 0; index < _slots.Length; index++)
                _slots[index].Pending?.Clear();
        }
        EnsureSlotCapacity(frame.SlotIndex);
        ref SlotState slot = ref _slots[frame.SlotIndex];
        bool matchingSlot = slot.Buffer is not null
            && slot.TopologyBuildSequence == topologyBuildSequence
            && slot.TransformCount == transforms.Length
            && slot.Buffer.SizeBytes >= bindingSizeBytes;
        bool denseFlightReplay = matchingSlot
            && slot.ConsumedDenseRevision != _denseRevision;
        int pendingReplayMatrices = matchingSlot
                ? slot.Pending?.Count ?? 0
                : 0;
        if (!denseRefresh)
        {
            MarkPendingChanges(
                topologyBuildSequence,
                transforms.Length,
                dynamicTransformSlots);
        }
        int contentBytes = checked(transforms.Length * 64);
        int allocationBytes = checked((int)bindingSizeBytes);

        if (slot.Buffer is null
            || slot.TopologyBuildSequence != topologyBuildSequence
            || slot.TransformCount != transforms.Length
            || slot.Buffer.SizeBytes < allocationBytes)
        {
            IGpuBuffer? candidate = null;
            try
            {
                candidate = _device.CreateBuffer(new GpuBufferDescription(
                    $"directional-shadow-transforms-slot-{frame.SlotIndex}-build-{topologyBuildSequence}",
                    allocationBytes,
                    GpuBufferUsage.Storage | GpuBufferUsage.TransferDestination,
                    GpuMemoryResidency.HostWritable));
                if (!candidate.HostWritesAreCoherent)
                {
                    throw new NotSupportedException(
                        "Directional-shadow retained transforms require coherent "
                        + "host-writable Vulkan memory. The pack will fail safe "
                        + "on this adapter rather than expose unflushed pose data.");
                }
                if (!transforms.IsEmpty)
                {
                    candidate.Upload(0, MemoryMarshal.AsBytes(transforms));
                    frame.PublishHostStorageWrites(candidate);
                }
            }
            catch
            {
                candidate?.Dispose();
                throw;
            }

            IGpuBuffer? previous = slot.Buffer;
            PendingTransformSet pending = slot.Pending
                ?? new PendingTransformSet(transforms.Length);
            pending.EnsureCapacity(transforms.Length);
            pending.Clear();
            slot = new SlotState(
                candidate,
                topologyBuildSequence,
                transforms.Length,
                pending,
                _denseRevision);
            previous?.Dispose();
            LastStats = new DirectionalShadowTransformPublishStats(
                TopologyUploaded: true,
                DynamicMatricesUpdated: 0,
                DynamicRangesUpdated: 0,
                BytesWritten: contentBytes,
                CurrentChangedMatrices: dynamicTransformSlots.Length,
                PendingReplayMatrices: 0,
                DenseDirectUpload: denseRefresh);
        }
        else
        {
            PendingTransformSet pending = slot.Pending
                ?? throw new InvalidOperationException(
                    "A retained directional-shadow flight slot has no pending-change owner.");
            bool directDenseUpload = denseRefresh || denseFlightReplay;
            ReadOnlySpan<int> slotsToUpload = directDenseUpload
                ? allDynamicTransformSlots
                : pending.GetSorted();
            if (directDenseUpload && !denseRefresh)
                ValidateDynamicSlots(allDynamicTransformSlots, transforms.Length);
            int ranges = UploadDynamicRanges(
                slot.Buffer,
                transforms,
                slotsToUpload,
                out long bytesWritten);
            if (ranges != 0)
                frame.PublishHostStorageWrites(slot.Buffer);
            LastStats = new DirectionalShadowTransformPublishStats(
                TopologyUploaded: false,
                DynamicMatricesUpdated: slotsToUpload.Length,
                DynamicRangesUpdated: ranges,
                BytesWritten: bytesWritten,
                CurrentChangedMatrices: dynamicTransformSlots.Length,
                PendingReplayMatrices: directDenseUpload ? 0 : pendingReplayMatrices,
                DenseDirectUpload: denseRefresh,
                DenseFlightReplay: denseFlightReplay && !denseRefresh);
            pending.Clear();
            slot = slot with { ConsumedDenseRevision = _denseRevision };
        }

        IGpuBuffer buffer = slot.Buffer
            ?? throw new InvalidOperationException(
                "The retained directional-shadow transform buffer was not published.");
        return new WorldTransformFrameSlice(
            frame.Serial,
            buffer,
            BaseOffsetBytes: 0,
            checked((uint)buffer.SizeBytes),
            FirstInstance: 0,
            checked((uint)transforms.Length));
    }

    private void ResetDenseRevisionForTopology(ulong topologyBuildSequence)
    {
        if (_denseTopologyBuildSequence == topologyBuildSequence)
            return;
        _denseTopologyBuildSequence = topologyBuildSequence;
        _denseRevision = 1;
        for (int index = 0; index < _slots.Length; index++)
            _slots[index].Pending?.Clear();
    }

    private void MarkPendingChanges(
        ulong topologyBuildSequence,
        int transformCount,
        ReadOnlySpan<int> dynamicTransformSlots)
    {
        if (dynamicTransformSlots.IsEmpty)
            return;
        for (int index = 0; index < _slots.Length; index++)
        {
            ref SlotState candidate = ref _slots[index];
            if (candidate.Buffer is null
                || candidate.TopologyBuildSequence != topologyBuildSequence
                || candidate.TransformCount != transformCount)
            {
                continue;
            }
            PendingTransformSet pending = candidate.Pending
                ??= new PendingTransformSet(transformCount);
            pending.EnsureCapacity(transformCount);
            pending.Mark(dynamicTransformSlots);
        }
    }

    private static int UploadDynamicRanges(
        IGpuBuffer buffer,
        ReadOnlySpan<Matrix4x4> transforms,
        ReadOnlySpan<int> slots,
        out long bytesWritten)
    {
        bytesWritten = 0;
        int ranges = 0;
        int cursor = 0;
        while (cursor < slots.Length)
        {
            int start = slots[cursor];
            int end = start + 1;
            cursor++;
            while (cursor < slots.Length && slots[cursor] == end)
            {
                end++;
                cursor++;
            }

            ReadOnlySpan<Matrix4x4> values = transforms.Slice(start, end - start);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(values);
            buffer.Upload(checked((long)start * 64L), bytes);
            bytesWritten = checked(bytesWritten + bytes.Length);
            ranges++;
        }
        return ranges;
    }

    private static void ValidateDynamicSlots(
        ReadOnlySpan<int> slots,
        int transformCount)
    {
        int previous = -1;
        for (int i = 0; i < slots.Length; i++)
        {
            int current = slots[i];
            if ((uint)current >= (uint)transformCount)
            {
                throw new InvalidOperationException(
                    $"Dynamic shadow transform slot {current} is outside the "
                    + $"{transformCount}-matrix retained product.");
            }
            if (current <= previous)
            {
                throw new InvalidOperationException(
                    "Dynamic shadow transform slots must be strictly increasing.");
            }
            previous = current;
        }
    }

    private void EnsureSlotCapacity(int slotIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotIndex);
        if (_slots.Length > slotIndex)
            return;
        int capacity = _slots.Length == 0 ? 2 : _slots.Length;
        while (capacity <= slotIndex)
            capacity = checked(capacity * 2);
        Array.Resize(ref _slots, capacity);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i].Buffer?.Dispose();
            _slots[i] = default;
        }
        LastStats = default;
        _denseTopologyBuildSequence = 0;
        _denseRevision = 0;
    }

    private record struct SlotState(
        IGpuBuffer? Buffer,
        ulong TopologyBuildSequence,
        int TransformCount,
        PendingTransformSet? Pending,
        ulong ConsumedDenseRevision);

    private sealed class PendingTransformSet
    {
        private int[] _slots;
        private bool[] _marked;

        internal PendingTransformSet(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(capacity);
            _slots = new int[capacity];
            _marked = new bool[capacity];
        }

        internal int Count { get; private set; }

        internal long RetainedBytes => checked(
            (long)_slots.Length * sizeof(int) + _marked.Length);

        internal void EnsureCapacity(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(capacity);
            if (_slots.Length >= capacity)
                return;
            Array.Resize(ref _slots, capacity);
            Array.Resize(ref _marked, capacity);
        }

        internal void Mark(ReadOnlySpan<int> slots)
        {
            for (int index = 0; index < slots.Length; index++)
            {
                int slot = slots[index];
                if (_marked[slot])
                    continue;
                _marked[slot] = true;
                _slots[Count++] = slot;
            }
        }

        internal ReadOnlySpan<int> GetSorted()
        {
            Array.Sort(_slots, 0, Count);
            return _slots.AsSpan(0, Count);
        }

        internal void Clear()
        {
            for (int index = 0; index < Count; index++)
                _marked[_slots[index]] = false;
            Count = 0;
        }
    }
}
