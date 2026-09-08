using AcDream.App.Rendering;

namespace AcDream.App.Rendering.Wb;

internal sealed class GpuRetiredRangeAllocator
{
    private readonly ContiguousRangeAllocator _allocator;
    private readonly GpuRetirementLedger _retirementLedger;
    private readonly Dictionary<MeshBufferRange, RetryableGpuResourceRelease> _pendingReleases = [];
    private int _pendingReleaseCount;
    private int _pendingReleaseLength;

    public GpuRetiredRangeAllocator(int capacity, IGpuResourceRetirementQueue retirement)
    {
        _allocator = new ContiguousRangeAllocator(capacity);
        _retirementLedger = new GpuRetirementLedger(
            retirement ?? throw new ArgumentNullException(nameof(retirement)));
    }

    public int Capacity => _allocator.Capacity;
    public int Used => _allocator.Used;
    public int HighWaterMark => _allocator.HighWaterMark;
    public int LargestFreeRange => _allocator.LargestFreeRange;
    public int TrailingFreeLength => _allocator.TrailingFreeLength;
    public int PendingReleaseCount => _pendingReleaseCount;
    public int PendingReleaseLength => _pendingReleaseLength;

    public bool TryAllocate(int length, out MeshBufferRange allocation) =>
        _allocator.TryAllocate(length, out allocation);

    public void Grow(int newCapacity) => _allocator.Grow(newCapacity);

    public void Shrink(int newCapacity) => _allocator.Shrink(newCapacity);

    public void ReleaseAfterGpuUse(MeshBufferRange allocation)
    {
        if (_pendingReleases.TryGetValue(allocation, out RetryableGpuResourceRelease? pending))
        {
            try
            {
                _retirementLedger.RetryPendingPublication(pending);
            }
            catch when (pending.IsComplete)
            {
            }
            return;
        }

        int nextPendingCount = checked(_pendingReleaseCount + 1);
        int nextPendingLength = checked(_pendingReleaseLength + allocation.Length);
        var release = new RetryableGpuResourceRelease(
            () => _allocator.Release(allocation),
            () => _pendingReleaseCount = checked(_pendingReleaseCount - 1),
            () => _pendingReleaseLength = checked(
                _pendingReleaseLength - allocation.Length),
            () =>
            {
                if (!_pendingReleases.Remove(allocation))
                {
                    throw new InvalidOperationException(
                        "GPU buffer range retirement lost its ownership record.");
                }
            });
        _pendingReleases.Add(allocation, release);
        _pendingReleaseCount = nextPendingCount;
        _pendingReleaseLength = nextPendingLength;

        try
        {
            _retirementLedger.Retire(release);
        }
        catch when (release.IsComplete)
        {
        }
    }

    public void ReleaseUnsubmitted(MeshBufferRange allocation)
    {
        if (_pendingReleases.ContainsKey(allocation))
        {
            throw new InvalidOperationException(
                "A GPU-submitted range cannot be released as unsubmitted.");
        }

        _allocator.Release(allocation);
    }
}
