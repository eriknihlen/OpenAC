using AcDream.App.Rendering.Wb;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class ContiguousRangeAllocatorTests
{
    [Fact]
    public void ReleasedRangesAreReusedWithoutGrowing()
    {
        var allocator = new ContiguousRangeAllocator(100);

        Assert.True(allocator.TryAllocate(30, out MeshBufferRange first));
        Assert.True(allocator.TryAllocate(20, out MeshBufferRange second));
        allocator.Release(first);

        Assert.True(allocator.TryAllocate(25, out MeshBufferRange reused));
        Assert.Equal(0, reused.Offset);
        Assert.Equal(25, reused.Length);
        Assert.Equal(45, allocator.Used);
        Assert.Equal(100, allocator.Capacity);
        Assert.Equal(50, allocator.LargestFreeRange);
        Assert.Equal(30, second.Offset);
    }

    [Fact]
    public void AdjacentReleasedRangesCoalesceInEitherOrder()
    {
        var allocator = new ContiguousRangeAllocator(100);
        Assert.True(allocator.TryAllocate(20, out MeshBufferRange first));
        Assert.True(allocator.TryAllocate(30, out MeshBufferRange second));
        Assert.True(allocator.TryAllocate(10, out MeshBufferRange third));

        allocator.Release(second);
        allocator.Release(first);
        allocator.Release(third);

        Assert.Equal(0, allocator.Used);
        Assert.Equal(100, allocator.LargestFreeRange);
        Assert.True(allocator.TryAllocate(100, out MeshBufferRange whole));
        Assert.Equal(new MeshBufferRange(0, 100), whole);
    }

    [Fact]
    public void GrowthJoinsTheTrailingFreeRange()
    {
        var allocator = new ContiguousRangeAllocator(64);
        Assert.True(allocator.TryAllocate(48, out _));

        allocator.Grow(128);

        Assert.Equal(80, allocator.LargestFreeRange);
        Assert.True(allocator.TryAllocate(72, out MeshBufferRange allocation));
        Assert.Equal(48, allocation.Offset);
        Assert.Equal(120, allocator.HighWaterMark);
    }

    [Fact]
    public void DoubleReleaseIsRejectedBeforeAccountingChanges()
    {
        var allocator = new ContiguousRangeAllocator(32);
        Assert.True(allocator.TryAllocate(8, out MeshBufferRange allocation));
        allocator.Release(allocation);

        Assert.Throws<InvalidOperationException>(() => allocator.Release(allocation));
        Assert.Equal(0, allocator.Used);
        Assert.Equal(32, allocator.LargestFreeRange);
    }

    [Fact]
    public void BestFitPreservesTheLargerHole()
    {
        var allocator = new ContiguousRangeAllocator(100);
        Assert.True(allocator.TryAllocate(20, out MeshBufferRange small));
        Assert.True(allocator.TryAllocate(10, out _));
        Assert.True(allocator.TryAllocate(40, out MeshBufferRange large));
        Assert.True(allocator.TryAllocate(30, out _));
        allocator.Release(small);
        allocator.Release(large);

        Assert.True(allocator.TryAllocate(18, out MeshBufferRange selected));
        Assert.Equal(small.Offset, selected.Offset);
        Assert.Equal(40, allocator.LargestFreeRange);
    }

    [Fact]
    public void ReleasingTailLowersLiveHighWaterAndAllowsTrim()
    {
        var allocator = new ContiguousRangeAllocator(100);
        Assert.True(allocator.TryAllocate(20, out _));
        Assert.True(allocator.TryAllocate(30, out MeshBufferRange tail));
        Assert.Equal(50, allocator.HighWaterMark);

        allocator.Release(tail);

        Assert.Equal(20, allocator.HighWaterMark);
        Assert.Equal(80, allocator.TrailingFreeLength);
        allocator.Shrink(40);
        Assert.Equal(40, allocator.Capacity);
        Assert.Equal(20, allocator.HighWaterMark);
        Assert.Equal(20, allocator.TrailingFreeLength);
    }

    [Fact]
    public void InteriorReleaseDoesNotHideAHighLiveAllocation()
    {
        var allocator = new ContiguousRangeAllocator(100);
        Assert.True(allocator.TryAllocate(20, out MeshBufferRange first));
        Assert.True(allocator.TryAllocate(30, out _));

        allocator.Release(first);

        Assert.Equal(50, allocator.HighWaterMark);
        Assert.Throws<InvalidOperationException>(() => allocator.Shrink(40));
    }
}
