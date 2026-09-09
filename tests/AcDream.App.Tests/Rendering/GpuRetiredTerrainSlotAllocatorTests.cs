using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class GpuRetiredTerrainSlotAllocatorTests
{
    [Fact]
    public void RemovedTerrainSlotIsNotReusedBeforeGpuRetirement()
    {
        var retirement = new DeferredRetirementQueue();
        var allocator = new GpuRetiredTerrainSlotAllocator(1, retirement);
        int first = allocator.Allocate(out bool firstNeedsGrow);
        Assert.False(firstNeedsGrow);

        allocator.FreeAfterGpuUse(first);
        int whilePending = allocator.Allocate(out bool pendingNeedsGrow);

        Assert.NotEqual(first, whilePending);
        Assert.True(pendingNeedsGrow);

        retirement.RunAll();
        int reused = allocator.Allocate(out _);
        Assert.Equal(first, reused);
    }

    [Fact]
    public void UnsubmittedTerrainSlotIsReusableImmediately()
    {
        var retirement = new DeferredRetirementQueue();
        var allocator = new GpuRetiredTerrainSlotAllocator(1, retirement);
        int first = allocator.Allocate(out bool firstNeedsGrow);
        Assert.False(firstNeedsGrow);

        allocator.ReleaseUnsubmitted(first);
        int reused = allocator.Allocate(out bool reusedNeedsGrow);

        Assert.Equal(first, reused);
        Assert.False(reusedNeedsGrow);
    }

    [Fact]
    public void PublicationFailure_RetainsSlotUntilRetryIsAccepted()
    {
        var retirement = new DeferredRetirementQueue { FailNextPublication = true };
        var allocator = new GpuRetiredTerrainSlotAllocator(1, retirement);
        int slot = allocator.Allocate(out _);

        Assert.Throws<InvalidOperationException>(() => allocator.FreeAfterGpuUse(slot));
        Assert.Equal(1, allocator.PendingReleaseCount);
        Assert.Equal(0, retirement.Count);
        int whileUnpublished = allocator.Allocate(out bool needsGrow);
        Assert.NotEqual(slot, whileUnpublished);
        Assert.True(needsGrow);

        allocator.FreeAfterGpuUse(slot);

        Assert.Equal(1, allocator.PendingReleaseCount);
        Assert.Equal(1, retirement.Count);
        retirement.RunAll();
        Assert.Equal(0, allocator.PendingReleaseCount);
        int reused = allocator.Allocate(out _);
        Assert.Equal(slot, reused);
    }

    [Fact]
    public void DuplicateFreeBeforeRetirement_IsIdempotent()
    {
        var retirement = new DeferredRetirementQueue();
        var allocator = new GpuRetiredTerrainSlotAllocator(1, retirement);
        int slot = allocator.Allocate(out _);

        allocator.FreeAfterGpuUse(slot);
        allocator.FreeAfterGpuUse(slot);

        Assert.Equal(1, allocator.PendingReleaseCount);
        Assert.Equal(1, retirement.Count);
        retirement.RunAll();
        Assert.Equal(0, allocator.PendingReleaseCount);
        Assert.Equal(slot, allocator.Allocate(out _));
    }

    [Fact]
    public void RetryPendingPublications_AdvancesLogicallyRemovedSlot()
    {
        var retirement = new DeferredRetirementQueue { FailNextPublication = true };
        var allocator = new GpuRetiredTerrainSlotAllocator(1, retirement);
        int slot = allocator.Allocate(out _);
        Assert.Throws<InvalidOperationException>(() => allocator.FreeAfterGpuUse(slot));

        allocator.RetryPendingPublications();

        Assert.Equal(1, allocator.PendingReleaseCount);
        Assert.Equal(1, retirement.Count);
        retirement.RunAll();
        Assert.Equal(0, allocator.PendingReleaseCount);
        Assert.Equal(slot, allocator.Allocate(out _));
    }

    [Fact]
    public void ReplacementPublicationFailure_RetiresOnlyReplacedSlotAfterRetry()
    {
        var retirement = new DeferredRetirementQueue { FailNextPublication = true };
        var allocator = new GpuRetiredTerrainSlotAllocator(2, retirement);
        int replaced = allocator.Allocate(out _);
        int replacement = allocator.Allocate(out _);

        Assert.Throws<InvalidOperationException>(() => allocator.FreeAfterGpuUse(replaced));
        Assert.Equal(2, allocator.LoadedCount);
        Assert.Equal(1, allocator.PendingReleaseCount);

        allocator.RetryPendingPublications();
        retirement.RunAll();

        Assert.Equal(1, allocator.LoadedCount);
        Assert.Equal(0, allocator.PendingReleaseCount);
        int reused = allocator.Allocate(out _);
        Assert.Equal(replaced, reused);
        Assert.NotEqual(replacement, reused);
    }

    [Fact]
    public void PendingSubmittedSlot_CannotBeReleasedAsUnsubmitted()
    {
        var retirement = new DeferredRetirementQueue();
        var allocator = new GpuRetiredTerrainSlotAllocator(1, retirement);
        int slot = allocator.Allocate(out _);
        allocator.FreeAfterGpuUse(slot);

        Assert.Throws<InvalidOperationException>(() => allocator.ReleaseUnsubmitted(slot));
        Assert.Equal(1, allocator.PendingReleaseCount);
    }

    private sealed class DeferredRetirementQueue : IGpuResourceRetirementQueue
    {
        private readonly List<Action> _releases = [];

        public int Count => _releases.Count;
        public bool FailNextPublication { get; set; }

        public void Retire(Action release)
        {
            if (FailNextPublication)
            {
                FailNextPublication = false;
                throw new InvalidOperationException("Injected publication failure.");
            }

            _releases.Add(release);
        }

        public void RunAll()
        {
            foreach (Action release in _releases)
                release();
            _releases.Clear();
        }
    }
}
