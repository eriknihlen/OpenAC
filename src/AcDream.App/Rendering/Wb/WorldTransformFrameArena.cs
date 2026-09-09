using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;

namespace AcDream.App.Rendering.Wb;

internal readonly record struct WorldTransformFrameSlice(
    long FrameSerial,
    IGpuBuffer Buffer,
    uint BaseOffsetBytes,
    uint BindingSizeBytes,
    uint FirstInstance,
    uint InstanceCount)
{
    internal bool IsValidFor(IGpuFrame frame) =>
        FrameSerial == frame.Serial
        && Buffer is not null
        && BindingSizeBytes >= checked(
            (FirstInstance + InstanceCount) * WorldTransformCapacityPolicy.MatrixBytes)
        && BindingSizeBytes % WorldTransformCapacityPolicy.MatrixBytes == 0u
        && BaseOffsetBytes % WorldTransformCapacityPolicy.MatrixBytes == 0u
        && checked((long)BaseOffsetBytes + BindingSizeBytes) <= Buffer.SizeBytes;
}

internal static class WorldTransformCapacityPolicy
{
    internal const uint MatrixBytes = 64u;
    internal const uint ConnectedDenseBootstrapInstances = 68_395u;
    internal const uint AllocationQuantumBytes = 64u * 1024u;
    internal const uint InitialBindingSizeBytes =
        ((ConnectedDenseBootstrapInstances * MatrixBytes
            + AllocationQuantumBytes - 1u) / AllocationQuantumBytes)
        * AllocationQuantumBytes;
    internal const uint VulkanGuaranteedMaxStorageBufferRangeBytes =
        128u * 1024u * 1024u;

    internal static uint ResolveBindingSizeBytes(
        uint requiredInstances,
        uint maxStorageBufferRangeBytes)
    {
        uint maximum = maxStorageBufferRangeBytes
            - (maxStorageBufferRangeBytes % MatrixBytes);
        ulong requiredBytes = (ulong)requiredInstances * MatrixBytes;
        if (maximum < MatrixBytes || requiredBytes > maximum)
        {
            throw new NotSupportedException(
                $"The enhanced frame needs {requiredInstances:N0} world matrices "
                + $"({requiredBytes:N0} bytes), but this adapter exposes only "
                + $"{maximum:N0} bytes through one storage-buffer binding. "
                + "The pack will fail safe rather than split the authoritative pose buffer.");
        }

        ulong targetBytes = Math.Max(
            requiredBytes,
            (ulong)ConnectedDenseBootstrapInstances * MatrixBytes);
        ulong growthBytes = checked(
            ((targetBytes + AllocationQuantumBytes - 1u)
                / AllocationQuantumBytes)
            * AllocationQuantumBytes);

        return (uint)Math.Min(growthBytes, maximum);
    }

    internal static void ValidateBindingSizeBytes(
        uint bindingSizeBytes,
        uint requiredInstances,
        uint maxStorageBufferRangeBytes)
    {
        ulong requiredBytes = (ulong)requiredInstances * MatrixBytes;
        if (bindingSizeBytes < requiredBytes
            || bindingSizeBytes % MatrixBytes != 0u
            || bindingSizeBytes > maxStorageBufferRangeBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bindingSizeBytes),
                bindingSizeBytes,
                $"A shared world-transform binding must be matrix-aligned, contain "
                + $"all {requiredInstances:N0} matrices, and not exceed the adapter's "
                + $"{maxStorageBufferRangeBytes:N0}-byte storage range.");
        }
    }
}

/// <summary>
/// Frame-local address allocator over the pack-on N.5 transform block. The
/// backing buffer may be a frame-ring allocation or the active pack's retained
/// flight-slot arena. It publishes the prepared shadow prefix once and then
/// only appends already-built ordinary world matrices. It deliberately has no
/// scene, animation, or transform-derivation dependency.
/// </summary>
internal sealed class WorldTransformFrameArena
{
    private WorldTransformFrameSlice _allocation;
    private uint _usedBytes;

    internal bool IsActive => _allocation.Buffer is not null;

    internal uint UsedInstances => _usedBytes / 64u;

    internal WorldTransformFrameSlice Begin(
        IGpuFrame frame,
        ReadOnlySpan<Matrix4x4> transforms) => Begin(
            frame,
            transforms,
            WorldTransformCapacityPolicy.ResolveBindingSizeBytes(
                checked((uint)transforms.Length),
                WorldTransformCapacityPolicy.VulkanGuaranteedMaxStorageBufferRangeBytes));

    internal WorldTransformFrameSlice Begin(
        IGpuFrame frame,
        ReadOnlySpan<Matrix4x4> transforms,
        uint bindingSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ResetIfStale(frame.Serial);
        if (IsActive)
        {
            throw new InvalidOperationException(
                "The directional-shadow transform frame was already published.");
        }

        uint byteCount = checked((uint)(transforms.Length * WorldTransformCapacityPolicy.MatrixBytes));
        if (bindingSizeBytes < byteCount
            || bindingSizeBytes % WorldTransformCapacityPolicy.MatrixBytes != 0u)
            throw new ArgumentOutOfRangeException(nameof(bindingSizeBytes));

        GpuRingAllocation allocation = frame.AllocateRing(
            checked((int)bindingSizeBytes),
            GpuRingUsage.Storage);
        if (!transforms.IsEmpty)
            MemoryMarshal.AsBytes(transforms).CopyTo(allocation.Data);

        _allocation = new WorldTransformFrameSlice(
            frame.Serial,
            allocation.Buffer,
            allocation.OffsetBytes,
            bindingSizeBytes,
            FirstInstance: 0,
            checked((uint)transforms.Length));
        _usedBytes = byteCount;
        return _allocation;
    }

    internal WorldTransformFrameSlice BeginRetained(
        IGpuFrame frame,
        in WorldTransformFrameSlice shadowPrefix)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ResetIfStale(frame.Serial);
        if (IsActive)
        {
            throw new InvalidOperationException(
                "The directional-shadow transform frame was already published.");
        }
        if (!shadowPrefix.IsValidFor(frame)
            || shadowPrefix.FirstInstance != 0
            || shadowPrefix.Buffer.Residency != GpuMemoryResidency.HostWritable
            || !shadowPrefix.Buffer.HostWritesAreCoherent
            || !shadowPrefix.Buffer.Usage.HasFlag(GpuBufferUsage.Storage))
        {
            throw new ArgumentException(
                "The retained shadow prefix must be this frame's host-writable "
                + "matrix-aligned storage arena at base instance zero.",
                nameof(shadowPrefix));
        }

        uint byteCount = checked(
            shadowPrefix.InstanceCount * WorldTransformCapacityPolicy.MatrixBytes);
        if (byteCount > shadowPrefix.BindingSizeBytes)
        {
            throw new ArgumentException(
                "The retained shadow prefix exceeds its storage binding.",
                nameof(shadowPrefix));
        }

        _allocation = shadowPrefix;
        _usedBytes = byteCount;
        return _allocation;
    }

    internal WorldTransformFrameSlice Append(
        IGpuFrame frame,
        ReadOnlySpan<Matrix4x4> transforms)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ResetIfStale(frame.Serial);
        if (!IsActive)
        {
            throw new InvalidOperationException(
                "The shared world-transform frame has not been published.");
        }

        uint byteCount = checked(
            (uint)(transforms.Length * WorldTransformCapacityPolicy.MatrixBytes));
        uint start = _usedBytes;
        uint end = checked(start + byteCount);
        if (end > _allocation.BindingSizeBytes)
        {
            throw new InvalidOperationException(
                $"The enhanced frame needs {end / WorldTransformCapacityPolicy.MatrixBytes:N0} world matrices; "
                + $"this frame's shared transform binding contains "
                + $"{_allocation.BindingSizeBytes / WorldTransformCapacityPolicy.MatrixBytes:N0}. "
                + "The pack will fail safe rather than bind a second pose buffer.");
        }

        if (!transforms.IsEmpty)
        {
            _allocation.Buffer.Upload(
                checked((long)_allocation.BaseOffsetBytes + start),
                MemoryMarshal.AsBytes(transforms));
        }
        _usedBytes = end;
        return _allocation with
        {
            FirstInstance = start / WorldTransformCapacityPolicy.MatrixBytes,
            InstanceCount = checked((uint)transforms.Length),
        };
    }

    internal bool IsActiveFor(long frameSerial) =>
        IsActive && _allocation.FrameSerial == frameSerial;

    internal void Cancel(IGpuFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (IsActiveFor(frame.Serial))
            Reset();
    }

    internal void ResetIfStale(long frameSerial)
    {
        if (IsActive && _allocation.FrameSerial != frameSerial)
            Reset();
    }

    internal void Reset()
    {
        _allocation = default;
        _usedBytes = 0;
    }
}
