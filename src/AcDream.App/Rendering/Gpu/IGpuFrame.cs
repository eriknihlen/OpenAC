using System.Runtime.InteropServices;

namespace AcDream.App.Rendering.Gpu;

internal readonly ref struct GpuRingAllocation
{
    public GpuRingAllocation(IGpuBuffer buffer, uint offsetBytes, Span<byte> data)
    {
        Buffer = buffer;
        OffsetBytes = offsetBytes;
        Data = data;
    }

    /// <summary>The ring buffer to bind. Backends may hand out many allocations from one buffer.</summary>
    public IGpuBuffer Buffer { get; }

    public uint OffsetBytes { get; }

    /// <summary>CPU-writable memory for this allocation. Valid until the owning frame retires.</summary>
    public Span<byte> Data { get; }

    public bool IsEmpty => Data.IsEmpty;

    public Span<T> AsSpan<T>() where T : unmanaged => MemoryMarshal.Cast<byte, T>(Data);
}

internal interface IGpuFrame : IDisposable
{
    /// <summary>Frames-in-flight slot index this frame occupies.</summary>
    int SlotIndex { get; }

    /// <summary>Monotonic frame serial. Matches the retirement-ledger key used for resource release.</summary>
    long Serial { get; }

    GpuRingAllocation AllocateRing(int byteCount, GpuRingUsage usage);

    void PublishHostStorageWrites(IGpuBuffer buffer);

    /// <summary>
    /// Opens a rendering pass. The returned encoder must be disposed before the
    /// next pass begins; nesting is not supported and no acdream pass needs it.
    /// </summary>
    IGpuPassEncoder BeginPass(GpuPassDescription description);

    void End();
}
