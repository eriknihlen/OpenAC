namespace AcDream.Content;

public readonly record struct CacheStats(long Hits, long Misses, long Evictions)
{
    public static CacheStats operator +(CacheStats a, CacheStats b) =>
        new(a.Hits + b.Hits, a.Misses + b.Misses, a.Evictions + b.Evictions);
}
