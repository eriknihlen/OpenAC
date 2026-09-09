using AcDream.Core.Terrain;

namespace AcDream.App.Rendering;

internal sealed class GpuRetiredTerrainSlotAllocator
{
    private readonly TerrainSlotAllocator _allocator;
    private readonly GpuRetirementLedger _retirementLedger;
    private readonly Dictionary<int, RetryableGpuResourceRelease> _pendingReleases = [];

    public GpuRetiredTerrainSlotAllocator(
        int initialCapacity,
        IGpuResourceRetirementQueue retirement)
    {
        _allocator = new TerrainSlotAllocator(initialCapacity);
        _retirementLedger = new GpuRetirementLedger(
            retirement ?? throw new ArgumentNullException(nameof(retirement)));
    }

    public int Capacity => _allocator.Capacity;
    public int LoadedCount => _allocator.LoadedCount;
    internal int PendingReleaseCount => _pendingReleases.Count;

    public int Allocate(out bool needsGrow) => _allocator.Allocate(out needsGrow);

    public void GrowTo(int newCapacity) => _allocator.GrowTo(newCapacity);

    public void ReleaseUnsubmitted(int slot)
    {
        if (_pendingReleases.ContainsKey(slot))
        {
            throw new InvalidOperationException(
                "A GPU-submitted terrain slot cannot be released as unsubmitted.");
        }

        _allocator.Free(slot);
    }

    public void FreeAfterGpuUse(int slot)
    {
        if (_pendingReleases.TryGetValue(slot, out RetryableGpuResourceRelease? pending))
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

        var release = new RetryableGpuResourceRelease(
            () => _allocator.Free(slot),
            () =>
            {
                if (!_pendingReleases.Remove(slot))
                {
                    throw new InvalidOperationException(
                        "Terrain-slot retirement lost its ownership record.");
                }
            });
        _pendingReleases.Add(slot, release);

        try
        {
            _retirementLedger.Retire(release);
        }
        catch when (release.IsComplete)
        {
        }
    }

    public void RetryPendingPublications() =>
        _retirementLedger.RetryPendingPublications();
}
