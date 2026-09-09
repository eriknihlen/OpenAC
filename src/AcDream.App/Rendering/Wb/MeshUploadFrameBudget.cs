namespace AcDream.App.Rendering.Wb;

internal readonly record struct MeshUploadCost(
    long SourceBytes,
    long ArrayAllocationBytes,
    long MipmapBytes,
    int NewArrayCount,
    long BufferUploadBytes = 0,
    long BufferAllocationBytes = 0,
    long BufferCopyBytes = 0,
    int NewBufferCount = 0,
    ulong AdmissionKey = 0);

internal readonly record struct MeshUploadBudgetLimits(
    int MaximumObjects,
    long MaximumSourceBytes,
    long MaximumArrayAllocationBytes,
    long MaximumMipmapBytes,
    int MaximumNewArrays,
    long MaximumBufferUploadBytes = long.MaxValue,
    long MaximumBufferAllocationBytes = long.MaxValue,
    long MaximumBufferCopyBytes = long.MaxValue,
    int MaximumNewBuffers = int.MaxValue,
    long MaximumSingleSourceBytes = long.MaxValue,
    long MaximumSingleArrayAllocationBytes = long.MaxValue,
    long MaximumSingleMipmapBytes = long.MaxValue,
    int MaximumSingleNewArrays = int.MaxValue,
    long MaximumSingleBufferUploadBytes = long.MaxValue);

internal sealed class MeshUploadFrameBudget
{
    private readonly MeshUploadBudgetLimits _limits;

    public MeshUploadFrameBudget(MeshUploadBudgetLimits limits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumObjects, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumSourceBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumArrayAllocationBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumMipmapBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumNewArrays, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumBufferUploadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumBufferAllocationBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumBufferCopyBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumNewBuffers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumSingleSourceBytes, limits.MaximumSourceBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumSingleArrayAllocationBytes, limits.MaximumArrayAllocationBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumSingleMipmapBytes, limits.MaximumMipmapBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumSingleNewArrays, limits.MaximumNewArrays);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumSingleBufferUploadBytes, limits.MaximumBufferUploadBytes);
        _limits = limits;
    }

    public int ObjectCount { get; private set; }
    public long SourceBytes { get; private set; }
    public long ArrayAllocationBytes { get; private set; }
    public long MipmapBytes { get; private set; }
    public int NewArrayCount { get; private set; }
    public long BufferUploadBytes { get; private set; }
    public long BufferAllocationBytes { get; private set; }
    public long BufferCopyBytes { get; private set; }
    public int NewBufferCount { get; private set; }
    public void Reset()
    {
        ObjectCount = 0;
        SourceBytes = 0;
        ArrayAllocationBytes = 0;
        MipmapBytes = 0;
        NewArrayCount = 0;
        BufferUploadBytes = 0;
        BufferAllocationBytes = 0;
        BufferCopyBytes = 0;
        NewBufferCount = 0;
    }

    public bool TryAdmit(MeshUploadCost cost)
    {
        Validate(cost);
        if (cost.BufferAllocationBytes != 0
            || cost.BufferCopyBytes != 0
            || cost.NewBufferCount != 0)
        {
            throw new InvalidOperationException(
                "Global-buffer allocation/copy work must be progressed as real maintenance before upload admission.");
        }

        bool oversizedHead = ObjectCount == 0 && ExceedsSingleFrame(cost);
        if (oversizedHead)
        {
            if (cost.SourceBytes > _limits.MaximumSingleSourceBytes
                || cost.ArrayAllocationBytes > _limits.MaximumSingleArrayAllocationBytes
                || cost.MipmapBytes > _limits.MaximumSingleMipmapBytes
                || cost.NewArrayCount > _limits.MaximumSingleNewArrays
                || cost.BufferUploadBytes > _limits.MaximumSingleBufferUploadBytes)
            {
                throw new NotSupportedException(
                    $"Mesh upload generation {cost.AdmissionKey} exceeds the supported single-operation ceiling.");
            }

            Admit(cost);
            return true;
        }

        if (ObjectCount >= _limits.MaximumObjects
                || WouldExceed(SourceBytes, cost.SourceBytes, _limits.MaximumSourceBytes)
                || WouldExceed(ArrayAllocationBytes, cost.ArrayAllocationBytes, _limits.MaximumArrayAllocationBytes)
                || WouldExceed(MipmapBytes, cost.MipmapBytes, _limits.MaximumMipmapBytes)
                || cost.NewArrayCount > _limits.MaximumNewArrays - NewArrayCount
                || WouldExceed(BufferUploadBytes, cost.BufferUploadBytes, _limits.MaximumBufferUploadBytes)
                || WouldExceed(BufferAllocationBytes, cost.BufferAllocationBytes, _limits.MaximumBufferAllocationBytes)
                || WouldExceed(BufferCopyBytes, cost.BufferCopyBytes, _limits.MaximumBufferCopyBytes)
                || cost.NewBufferCount > _limits.MaximumNewBuffers - NewBufferCount)
        {
            return false;
        }

        Admit(cost);
        return true;
    }

    /// <summary>Records work that actually executed this frame.</summary>
    public void RecordBufferMaintenance(long allocationBytes, long copyBytes, int newBufferCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(allocationBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(copyBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(newBufferCount);
        if (WouldExceed(BufferAllocationBytes, allocationBytes, _limits.MaximumBufferAllocationBytes)
            || WouldExceed(BufferCopyBytes, copyBytes, _limits.MaximumBufferCopyBytes)
            || newBufferCount > _limits.MaximumNewBuffers - NewBufferCount)
        {
            throw new InvalidOperationException(
                "Executed global-buffer maintenance exceeded its real per-frame bound.");
        }
        BufferAllocationBytes = checked(BufferAllocationBytes + allocationBytes);
        BufferCopyBytes = checked(BufferCopyBytes + copyBytes);
        NewBufferCount = checked(NewBufferCount + newBufferCount);
    }

    private bool ExceedsSingleFrame(MeshUploadCost cost) =>
        cost.SourceBytes > _limits.MaximumSourceBytes
        || cost.ArrayAllocationBytes > _limits.MaximumArrayAllocationBytes
        || cost.MipmapBytes > _limits.MaximumMipmapBytes
        || cost.NewArrayCount > _limits.MaximumNewArrays
        || cost.BufferUploadBytes > _limits.MaximumBufferUploadBytes
        || cost.BufferAllocationBytes > _limits.MaximumBufferAllocationBytes
        || cost.BufferCopyBytes > _limits.MaximumBufferCopyBytes
        || cost.NewBufferCount > _limits.MaximumNewBuffers;

    private void Admit(MeshUploadCost cost)
    {
        ObjectCount++;
        SourceBytes = checked(SourceBytes + cost.SourceBytes);
        ArrayAllocationBytes = checked(ArrayAllocationBytes + cost.ArrayAllocationBytes);
        MipmapBytes = checked(MipmapBytes + cost.MipmapBytes);
        NewArrayCount = checked(NewArrayCount + cost.NewArrayCount);
        BufferUploadBytes = checked(BufferUploadBytes + cost.BufferUploadBytes);
        BufferAllocationBytes = checked(BufferAllocationBytes + cost.BufferAllocationBytes);
        BufferCopyBytes = checked(BufferCopyBytes + cost.BufferCopyBytes);
        NewBufferCount = checked(NewBufferCount + cost.NewBufferCount);
    }

    private static bool WouldExceed(long current, long added, long maximum) =>
        added > maximum - Math.Min(current, maximum);

    private static void Validate(MeshUploadCost cost)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cost.SourceBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(cost.ArrayAllocationBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(cost.MipmapBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(cost.NewArrayCount);
        ArgumentOutOfRangeException.ThrowIfNegative(cost.BufferUploadBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(cost.BufferAllocationBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(cost.BufferCopyBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(cost.NewBufferCount);
    }
}
