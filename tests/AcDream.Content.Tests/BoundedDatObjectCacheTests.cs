using DatReaderWriter.DBObjs;
using DatReaderWriter.Lib.IO;
using System.Collections.Concurrent;

namespace AcDream.Content.Tests;

public sealed class BoundedDatObjectCacheTests
{
    [Fact]
    public void GetOrAdd_EvictsLeastRecentlyUsedEntryAtEntryLimit()
    {
        var cache = CreateCache(entryLimit: 2, byteLimit: 100);
        var first = Surface(1);
        var second = Surface(2);
        var third = Surface(3);

        cache.GetOrAdd(1, first);
        cache.GetOrAdd(2, second);
        Assert.True(cache.TryGet<Surface>(1, out _)); // first is now hottest
        cache.GetOrAdd(3, third);

        Assert.True(cache.TryGet<Surface>(1, out var retainedFirst));
        Assert.Same(first, retainedFirst);
        Assert.False(cache.TryGet<Surface>(2, out _));
        Assert.True(cache.TryGet<Surface>(3, out var retainedThird));
        Assert.Same(third, retainedThird);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void GetOrAdd_EnforcesEstimatedByteBudgetAndDoesNotRetainOversizeEntry()
    {
        var cache = new BoundedDatObjectCache(
            entryLimit: 10,
            estimatedByteLimit: 10,
            estimateRetainedBytes: value => value.Id);

        cache.GetOrAdd(6, Surface(6));
        cache.GetOrAdd(5, Surface(5));

        Assert.False(cache.TryGet<Surface>(6, out _));
        Assert.True(cache.TryGet<Surface>(5, out _));
        Assert.Equal(5, cache.EstimatedBytes);

        var oversize = Surface(11);
        Assert.Same(oversize, cache.GetOrAdd(11, oversize));
        Assert.False(cache.TryGet<Surface>(11, out _));
        Assert.Equal(1, cache.Count);
        Assert.Equal(5, cache.EstimatedBytes);
    }

    [Fact]
    public void ProductionEstimator_ChargesKnownRenderSurfacePayload()
    {
        const int byteLimit = 512 * 1024;
        var cache = new BoundedDatObjectCache(
            entryLimit: 10,
            estimatedByteLimit: byteLimit);
        var oversizeTexture = new RenderSurface
        {
            Id = 12,
            SourceData = new byte[byteLimit + 1],
        };

        Assert.Same(oversizeTexture, cache.GetOrAdd(12, oversizeTexture));
        Assert.False(cache.TryGet<RenderSurface>(12, out _));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.EstimatedBytes);
    }

    [Fact]
    public void GetOrAdd_DuplicateKeyKeepsOneCanonicalObject()
    {
        var cache = CreateCache(entryLimit: 4, byteLimit: 100);
        var canonical = Surface(7);
        var duplicate = Surface(7);

        Assert.Same(canonical, cache.GetOrAdd(7, canonical));
        Assert.Same(canonical, cache.GetOrAdd(7, duplicate));
        Assert.Equal(1, cache.Count);
        Assert.Equal(1, cache.EstimatedBytes);
    }

    [Fact]
    public void GetOrAdd_ConcurrentDuplicateKeyPublishesOneCanonicalObject()
    {
        var cache = CreateCache(entryLimit: 4, byteLimit: 100);
        var returned = new ConcurrentBag<Surface>();

        Parallel.For(0, 1_000, i =>
        {
            returned.Add(cache.GetOrAdd(42, Surface((uint)(1_000 + i))));
        });

        Surface[] results = returned.ToArray();
        Assert.NotEmpty(results);
        Surface canonical = results[0];
        Assert.All(results, value => Assert.Same(canonical, value));
        Assert.True(cache.TryGet<Surface>(42, out var retained));
        Assert.Same(canonical, retained);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void SameFileIdWithDifferentRequestedTypes_DoesNotAlias()
    {
        var cache = CreateCache(entryLimit: 4, byteLimit: 100);
        var surface = Surface(99);
        var palette = new Palette { Id = 99 };

        cache.GetOrAdd<Surface>(99, surface);
        cache.GetOrAdd<Palette>(99, palette);

        Assert.True(cache.TryGet<Surface>(99, out var retainedSurface));
        Assert.True(cache.TryGet<Palette>(99, out var retainedPalette));
        Assert.Same(surface, retainedSurface);
        Assert.Same(palette, retainedPalette);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Stats_TracksHitsMissesAndEvictions()
    {
        var cache = CreateCache(entryLimit: 1, byteLimit: 100);

        Assert.False(cache.TryGet<Surface>(1, out _)); // miss
        cache.GetOrAdd(1, Surface(1));
        Assert.True(cache.TryGet<Surface>(1, out _)); // hit
        cache.GetOrAdd(2, Surface(2)); // entryLimit=1 evicts id 1

        CacheStats stats = cache.Stats;
        Assert.Equal(1, stats.Hits);
        Assert.Equal(1, stats.Misses);
        Assert.Equal(1, stats.Evictions);
    }

    private static BoundedDatObjectCache CreateCache(int entryLimit, long byteLimit) =>
        new(entryLimit, byteLimit, _ => 1);

    private static Surface Surface(uint id) => new() { Id = id };
}
