using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class BoundedUnownedResourceCacheTests
{
    [Fact]
    public void ReacquireRemovesResourceFromBudgetAndEvictionOrder()
    {
        var cache = new BoundedUnownedResourceCache<string>(budgetBytes: 10);
        cache.MarkUnowned("first", 6);
        cache.MarkUnowned("second", 6);

        Assert.True(cache.MarkOwned("first"));
        Assert.Equal(1, cache.Count);
        Assert.Equal(6, cache.ResidentBytes);
        Assert.False(cache.TryTakeOldestOverBudget(out _));
    }

    [Fact]
    public void TakesOnlyOldestResourceWhileOverBudget()
    {
        var cache = new BoundedUnownedResourceCache<string>(budgetBytes: 10);
        cache.MarkUnowned("first", 6);
        cache.MarkUnowned("second", 6);
        cache.MarkUnowned("third", 6);

        Assert.True(cache.TryTakeOldestOverBudget(out string first));
        Assert.Equal("first", first);
        Assert.Equal(12, cache.ResidentBytes);

        Assert.True(cache.TryTakeOldestOverBudget(out string second));
        Assert.Equal("second", second);
        Assert.Equal(6, cache.ResidentBytes);

        Assert.False(cache.TryTakeOldestOverBudget(out _));
    }

    [Fact]
    public void RemarkingUnownedRefreshesLruPositionWithoutDoubleCounting()
    {
        var cache = new BoundedUnownedResourceCache<string>(budgetBytes: 5);
        cache.MarkUnowned("first", 4);
        cache.MarkUnowned("second", 4);
        cache.MarkUnowned("first", 4);

        Assert.Equal(2, cache.Count);
        Assert.Equal(8, cache.ResidentBytes);
        Assert.True(cache.TryTakeOldestOverBudget(out string key));
        Assert.Equal("second", key);
    }

    [Fact]
    public void SizeMutationForSameKeyIsRejected()
    {
        var cache = new BoundedUnownedResourceCache<string>(budgetBytes: 100);
        cache.MarkUnowned("key", 4);

        Assert.Throws<InvalidOperationException>(() => cache.MarkUnowned("key", 8));
    }

    [Fact]
    public void PhysicalBudgetCanTakeOldestEvenBelowLogicalBudget()
    {
        var cache = new BoundedUnownedResourceCache<string>(budgetBytes: 100);
        cache.MarkUnowned("old", 4);
        cache.MarkUnowned("new", 4);

        Assert.False(cache.TryTakeOldestOverBudget(out _));
        Assert.True(cache.TryTakeOldest(out string key));
        Assert.Equal("old", key);
        Assert.Equal(4, cache.ResidentBytes);
    }

    [Fact]
    public void ExactByteAndCountLimitsRemainRetained()
    {
        var cache = new BoundedUnownedResourceCache<int>(budgetBytes: 64, maximumCount: 2);
        cache.MarkUnowned(1, 32);
        cache.MarkUnowned(2, 32);

        Assert.False(cache.TryTakeOldestOverBudget(out _));
        Assert.Equal(2, cache.Count);
        Assert.Equal(64, cache.ResidentBytes);
    }

    [Fact]
    public void CountOverflowTakesOnlyOneOldestResource()
    {
        var cache = new BoundedUnownedResourceCache<string>(budgetBytes: 1_000, maximumCount: 2);
        cache.MarkUnowned("oldest", 1);
        cache.MarkUnowned("middle", 1);
        cache.MarkUnowned("newest", 1);

        Assert.True(cache.TryTakeOldestOverBudget(out string victim));
        Assert.Equal("oldest", victim);
        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryTakeOldestOverBudget(out _));
    }
}
