using AcDream.Core.Terrain;
using Xunit;

namespace AcDream.Core.Tests.Terrain;

public class TerrainSlotAllocatorTests
{
    [Fact]
    public void Allocate_FromFreshAllocator_ReturnsZero()
    {
        var alloc = new TerrainSlotAllocator(initialCapacity: 8);
        Assert.Equal(0, alloc.Allocate(out _));
    }

    [Fact]
    public void Allocate_TwoTimes_ReturnsZeroThenOne()
    {
        var alloc = new TerrainSlotAllocator(initialCapacity: 8);
        Assert.Equal(0, alloc.Allocate(out _));
        Assert.Equal(1, alloc.Allocate(out _));
    }

    [Fact]
    public void FreeThenAllocate_ReusesFreedSlot()
    {
        var alloc = new TerrainSlotAllocator(initialCapacity: 8);
        var s0 = alloc.Allocate(out _);
        var s1 = alloc.Allocate(out _);
        alloc.Free(s0);
        Assert.Equal(s0, alloc.Allocate(out _));
    }

    [Fact]
    public void FreeOrderedFreshAllocs_ReturnsInFifoOrder()
    {
        var alloc = new TerrainSlotAllocator(initialCapacity: 8);
        var s0 = alloc.Allocate(out _);
        var s1 = alloc.Allocate(out _);
        var s2 = alloc.Allocate(out _);
        alloc.Free(s0);
        alloc.Free(s2);
        Assert.Equal(s0, alloc.Allocate(out _));
        Assert.Equal(s2, alloc.Allocate(out _));
    }

    [Fact]
    public void Allocate_BeyondInitialCapacity_SignalsNeedsGrow()
    {
        var alloc = new TerrainSlotAllocator(initialCapacity: 2);
        alloc.Allocate(out var grow0);
        alloc.Allocate(out var grow1);
        alloc.Allocate(out var grow2);
        Assert.False(grow0);
        Assert.False(grow1);
        Assert.True(grow2);
    }

    [Fact]
    public void GrowTo_DoublesCapacityCorrectly()
    {
        var alloc = new TerrainSlotAllocator(initialCapacity: 4);
        alloc.GrowTo(8);
        Assert.Equal(8, alloc.Capacity);
        alloc.GrowTo(64);
        Assert.Equal(64, alloc.Capacity);
    }

    [Fact]
    public void LoadedCount_TracksAllocAndFree()
    {
        var alloc = new TerrainSlotAllocator(initialCapacity: 8);
        Assert.Equal(0, alloc.LoadedCount);
        var s0 = alloc.Allocate(out _);
        var s1 = alloc.Allocate(out _);
        Assert.Equal(2, alloc.LoadedCount);
        alloc.Free(s0);
        Assert.Equal(1, alloc.LoadedCount);
    }

    [Fact]
    public void Free_TwiceForSameSlot_Throws()
    {
        var alloc = new TerrainSlotAllocator(initialCapacity: 8);
        var s0 = alloc.Allocate(out _);
        alloc.Free(s0);
        Assert.Throws<InvalidOperationException>(() => alloc.Free(s0));
    }
}
