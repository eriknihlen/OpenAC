using AcDream.Content;
using Xunit;

namespace AcDream.Content.Tests;

public sealed class DecodedTextureCacheTests {
    private static DecodedTextureKey Key(
        uint id,
        bool clip = false,
        bool additive = false) => new(id, clip, additive);

    [Fact]
    public void RetainOrUse_EvictsLeastRecentlyUsedEntryToMeetByteBudget() {
        var cache = new DecodedTextureCache(maximumBytes: 8, maximumEntries: 8);
        var first = new byte[4];
        var second = new byte[4];
        var third = new byte[4];

        Assert.Same(first, cache.RetainOrUse(Key(1), first, out var firstCached));
        Assert.Same(second, cache.RetainOrUse(Key(2), second, out var secondCached));
        Assert.True(cache.TryGet(Key(1), out _)); // ID 2 is now least recently used.

        Assert.Same(third, cache.RetainOrUse(Key(3), third, out var thirdCached));

        Assert.True(firstCached);
        Assert.True(secondCached);
        Assert.True(thirdCached);
        Assert.True(cache.TryGet(Key(1), out _));
        Assert.False(cache.TryGet(Key(2), out _));
        Assert.True(cache.TryGet(Key(3), out _));
        Assert.Equal(8, cache.ResidentBytes);
    }

    [Fact]
    public void RetainOrUse_DoesNotAdmitOversizedEntry() {
        var cache = new DecodedTextureCache(maximumBytes: 3, maximumEntries: 8);
        var pixels = new byte[4];

        Assert.Same(pixels, cache.RetainOrUse(Key(1), pixels, out var isCached));

        Assert.False(isCached);
        Assert.False(cache.TryGet(Key(1), out _));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.ResidentBytes);
    }

    [Fact]
    public void RetainOrUse_ExistingCanonicalEntryWinsConcurrentDecodeRace() {
        var cache = new DecodedTextureCache(maximumBytes: 16, maximumEntries: 8);
        var canonical = new byte[4];
        var duplicate = new byte[4];

        cache.RetainOrUse(Key(1), canonical, out _);
        var result = cache.RetainOrUse(Key(1), duplicate, out var isCached);

        Assert.True(isCached);
        Assert.Same(canonical, result);
        Assert.Equal(1, cache.Count);
        Assert.Equal(4, cache.ResidentBytes);
    }

    [Fact]
    public void RetainOrUse_EnforcesEntryBudget() {
        var cache = new DecodedTextureCache(maximumBytes: 1024, maximumEntries: 2);

        cache.RetainOrUse(Key(1), new byte[1], out _);
        cache.RetainOrUse(Key(2), new byte[1], out _);
        cache.RetainOrUse(Key(3), new byte[1], out _);

        Assert.False(cache.TryGet(Key(1), out _));
        Assert.True(cache.TryGet(Key(2), out _));
        Assert.True(cache.TryGet(Key(3), out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void DecodeAffectingSurfaceFlags_DoNotAliasSameRenderSurface()
    {
        var cache = new DecodedTextureCache(maximumBytes: 1024, maximumEntries: 8);
        var opaque = new byte[] { 1 };
        var clip = new byte[] { 2 };
        var additive = new byte[] { 3 };

        cache.RetainOrUse(Key(1), opaque, out _);
        cache.RetainOrUse(Key(1, clip: true), clip, out _);
        cache.RetainOrUse(Key(1, additive: true), additive, out _);

        Assert.Same(opaque, AssertCacheHit(cache, Key(1)));
        Assert.Same(clip, AssertCacheHit(cache, Key(1, clip: true)));
        Assert.Same(additive, AssertCacheHit(cache, Key(1, additive: true)));
    }

    [Fact]
    public async Task GetOrCreate_ConcurrentMissRunsFactoryOnce()
    {
        var cache = new DecodedTextureCache(maximumBytes: 1024, maximumEntries: 8);
        int calls = 0;
        using var release = new ManualResetEventSlim();
        Task<byte[]>[] readers = Enumerable.Range(0, 8)
            .Select(readerIndex => Task.Run(() => cache.GetOrCreate(
                Key(1),
                () =>
                {
                    Interlocked.Increment(ref calls);
                    release.Wait();
                    return new byte[] { 1, 2, 3, 4 };
                },
                out _)))
            .ToArray();

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, 5_000));
        release.Set();
        byte[][] results = await Task.WhenAll(readers);

        Assert.Equal(1, calls);
        Assert.All(results, result => Assert.Same(results[0], result));
    }

    [Fact]
    public void GetOrCreate_FailedFactoryCanRetry()
    {
        var cache = new DecodedTextureCache(maximumBytes: 1024, maximumEntries: 8);
        int calls = 0;

        Assert.Throws<InvalidOperationException>(() => cache.GetOrCreate(
            Key(1),
            () =>
            {
                calls++;
                throw new InvalidOperationException("decode failed");
            },
            out _));
        byte[] recovered = cache.GetOrCreate(
            Key(1),
            () =>
            {
                calls++;
                return new byte[] { 1 };
            },
            out var retained);

        Assert.Equal(2, calls);
        Assert.True(retained);
        Assert.Equal(new byte[] { 1 }, recovered);
    }

    [Fact]
    public void Stats_TracksHitsMissesAndEvictions()
    {
        var cache = new DecodedTextureCache(maximumBytes: 4, maximumEntries: 1);

        Assert.False(cache.TryGet(Key(1), out _)); // miss
        cache.RetainOrUse(Key(1), new byte[4], out _);
        Assert.True(cache.TryGet(Key(1), out _)); // hit
        cache.RetainOrUse(Key(2), new byte[4], out _); // maximumEntries=1 evicts key 1

        CacheStats stats = cache.Stats;
        Assert.Equal(1, stats.Hits);
        Assert.Equal(1, stats.Misses);
        Assert.Equal(1, stats.Evictions);
    }

    private static byte[] AssertCacheHit(DecodedTextureCache cache, DecodedTextureKey key)
    {
        Assert.True(cache.TryGet(key, out byte[] pixels));
        return pixels;
    }
}
