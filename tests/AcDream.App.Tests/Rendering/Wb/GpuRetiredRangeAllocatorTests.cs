using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class GpuRetiredRangeAllocatorTests
{
    [Fact]
    public void ReleasedRangeCannotBeOverwrittenBeforeGpuRetirement()
    {
        var retirement = new DeferredRetirementQueue();
        var allocator = new GpuRetiredRangeAllocator(capacity: 8, retirement);
        Assert.True(allocator.TryAllocate(8, out MeshBufferRange first));

        allocator.ReleaseAfterGpuUse(first);

        Assert.Equal(1, allocator.PendingReleaseCount);
        Assert.Equal(8, allocator.PendingReleaseLength);
        Assert.False(allocator.TryAllocate(8, out _));
        retirement.RunAll();
        Assert.Equal(0, allocator.PendingReleaseCount);
        Assert.Equal(0, allocator.PendingReleaseLength);
        Assert.True(allocator.TryAllocate(8, out MeshBufferRange reused));
        Assert.Equal(first, reused);
    }

    [Fact]
    public void FailedUploadCanReleaseUnsubmittedRangeImmediately()
    {
        var retirement = new DeferredRetirementQueue();
        var allocator = new GpuRetiredRangeAllocator(capacity: 8, retirement);
        Assert.True(allocator.TryAllocate(8, out MeshBufferRange first));

        allocator.ReleaseUnsubmitted(first);

        Assert.True(allocator.TryAllocate(8, out MeshBufferRange reused));
        Assert.Equal(first, reused);
        Assert.Equal(0, retirement.Count);
    }

    [Fact]
    public void FailedRetirement_KeepsPendingOwnershipAccounting()
    {
        var retirement = new DeferredRetirementQueue();
        var allocator = new GpuRetiredRangeAllocator(capacity: 8, retirement);
        Assert.True(allocator.TryAllocate(8, out MeshBufferRange range));

        allocator.ReleaseAfterGpuUse(range);
        retirement.RunAll();
        allocator.ReleaseAfterGpuUse(range);

        Assert.Throws<InvalidOperationException>(retirement.RunAll);
        Assert.Equal(1, allocator.PendingReleaseCount);
        Assert.Equal(8, allocator.PendingReleaseLength);
    }

    [Fact]
    public void PublicationFailure_RetryDoesNotDuplicatePendingAccounting()
    {
        var retirement = new DeferredRetirementQueue { FailNextPublication = true };
        var allocator = new GpuRetiredRangeAllocator(capacity: 8, retirement);
        Assert.True(allocator.TryAllocate(8, out MeshBufferRange range));

        Assert.Throws<InvalidOperationException>(() => allocator.ReleaseAfterGpuUse(range));
        Assert.Equal(1, allocator.PendingReleaseCount);
        Assert.Equal(8, allocator.PendingReleaseLength);
        Assert.Equal(0, retirement.Count);

        allocator.ReleaseAfterGpuUse(range);

        Assert.Equal(1, allocator.PendingReleaseCount);
        Assert.Equal(8, allocator.PendingReleaseLength);
        Assert.Equal(1, retirement.Count);
        retirement.RunAll();
        Assert.Equal(0, allocator.PendingReleaseCount);
        Assert.Equal(0, allocator.PendingReleaseLength);
        Assert.True(allocator.TryAllocate(8, out MeshBufferRange reused));
        Assert.Equal(range, reused);
    }

    [Fact]
    public void DuplicateReleaseBeforeRetirement_DoesNotPublishTwice()
    {
        var retirement = new DeferredRetirementQueue();
        var allocator = new GpuRetiredRangeAllocator(capacity: 8, retirement);
        Assert.True(allocator.TryAllocate(8, out MeshBufferRange range));

        allocator.ReleaseAfterGpuUse(range);
        allocator.ReleaseAfterGpuUse(range);

        Assert.Equal(1, allocator.PendingReleaseCount);
        Assert.Equal(8, allocator.PendingReleaseLength);
        Assert.Equal(1, retirement.Count);
        retirement.RunAll();
        Assert.Equal(0, allocator.PendingReleaseCount);
        Assert.Equal(0, allocator.PendingReleaseLength);
    }

    [Fact]
    public void PendingSubmittedRange_CannotBeReleasedAsUnsubmitted()
    {
        var retirement = new DeferredRetirementQueue();
        var allocator = new GpuRetiredRangeAllocator(capacity: 8, retirement);
        Assert.True(allocator.TryAllocate(8, out MeshBufferRange range));
        allocator.ReleaseAfterGpuUse(range);

        Assert.Throws<InvalidOperationException>(() => allocator.ReleaseUnsubmitted(range));
        Assert.Equal(1, allocator.PendingReleaseCount);
        Assert.Equal(8, allocator.PendingReleaseLength);
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
