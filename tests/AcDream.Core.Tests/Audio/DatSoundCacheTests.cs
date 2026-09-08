using System.Collections.Concurrent;
using AcDream.Core.Audio;
using AcDream.Core.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Lib.IO;
using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace AcDream.Core.Tests.Audio;

public sealed class DatSoundCacheTests
{
    [Fact]
    public void GetWave_DecodesAndReturnsPcmWave()
    {
        var dats = new FakeDatObjectSource();
        dats.AddWave(1, MakePcmWave(1, dataBytes: 100));
        var cache = new DatSoundCache(dats, maxWaveBytes: 10_000);

        WaveData? wave = cache.GetWave(1);

        Assert.NotNull(wave);
        Assert.Equal(100, wave!.PcmBytes.Length);
        Assert.Equal(1, cache.CachedWaveCount);
        Assert.Equal(100, cache.ResidentWaveBytes);
    }

    [Fact]
    public void GetWave_MissingWave_ReturnsNullAndMemoizesNegativeWithoutRepeatedLookup()
    {
        var dats = new FakeDatObjectSource(); // no wave registered for id 7
        var cache = new DatSoundCache(dats, maxWaveBytes: 10_000);

        Assert.Null(cache.GetWave(7));
        Assert.Null(cache.GetWave(7));
        Assert.Null(cache.GetWave(7));

        Assert.Equal(1, dats.GetCallCount(7));
        Assert.Equal(0, cache.CachedWaveCount);
        Assert.Equal(0, cache.ResidentWaveBytes);
    }

    [Fact]
    public void GetWave_UnsupportedFormat_ReturnsNullAndDoesNotConsumeByteBudget()
    {
        var dats = new FakeDatObjectSource();
        dats.AddWave(2, MakeMp3Wave(2)); // WaveDecoder.Decode returns null for MP3
        var cache = new DatSoundCache(dats, maxWaveBytes: 10_000);

        Assert.Null(cache.GetWave(2));
        Assert.Equal(0, cache.CachedWaveCount);
        Assert.Equal(0, cache.ResidentWaveBytes);

        Assert.Null(cache.GetWave(2));
        Assert.Equal(1, dats.GetCallCount(2));
    }

    [Fact]
    public void GetWave_CacheHit_ReturnsSameInstanceWithoutRedecoding()
    {
        var dats = new FakeDatObjectSource();
        dats.AddWave(1, MakePcmWave(1, dataBytes: 100));
        var cache = new DatSoundCache(dats, maxWaveBytes: 10_000);

        WaveData? first = cache.GetWave(1);
        WaveData? second = cache.GetWave(1);

        Assert.Same(first, second);
        Assert.Equal(1, dats.GetCallCount(1));
    }

    [Fact]
    public void GetWave_EvictsLeastRecentlyUsedWhenOverBudget()
    {
        var dats = new FakeDatObjectSource();
        dats.AddWave(1, MakePcmWave(1, dataBytes: 150)); // A
        dats.AddWave(2, MakePcmWave(2, dataBytes: 150)); // B
        dats.AddWave(3, MakePcmWave(3, dataBytes: 150)); // C
        var cache = new DatSoundCache(dats, maxWaveBytes: 300);

        WaveData? a1 = cache.GetWave(1);
        WaveData? b1 = cache.GetWave(2);
        Assert.Equal(2, cache.CachedWaveCount);
        Assert.Equal(300, cache.ResidentWaveBytes); // exactly at budget, no eviction yet

        // Touch A so B becomes the least-recently-used entry.
        Assert.Same(a1, cache.GetWave(1));

        // Admitting C (150) would push residency to 450 > 300 — B must go.
        WaveData? c1 = cache.GetWave(3);
        Assert.Equal(2, cache.CachedWaveCount);
        Assert.Equal(300, cache.ResidentWaveBytes);

        // A and C are still resident (no re-decode); B was evicted (re-decodes on next use).
        Assert.Same(a1, cache.GetWave(1));
        Assert.Equal(1, dats.GetCallCount(1));
        Assert.Same(c1, cache.GetWave(3));
        Assert.Equal(1, dats.GetCallCount(3));

        WaveData? b2 = cache.GetWave(2);
        Assert.NotNull(b2);
        Assert.NotSame(b1, b2); // evicted entries decode fresh on next request
        Assert.Equal(2, dats.GetCallCount(2));

        DatSoundCacheDiagnostics diagnostics = cache.Diagnostics;
        Assert.Equal(2, diagnostics.CachedWaveCount);
        Assert.Equal(300, diagnostics.ResidentWaveBytes);
        Assert.Equal(300, diagnostics.BudgetBytes);
        Assert.True(diagnostics.Hits >= 3);
        Assert.True(diagnostics.Misses >= 4);
        Assert.True(diagnostics.Evictions >= 2);
    }

    [Fact]
    public void GetWave_OversizeSingleWave_IsServedButNotRetained()
    {
        var dats = new FakeDatObjectSource();
        dats.AddWave(9, MakePcmWave(9, dataBytes: 500));
        var cache = new DatSoundCache(dats, maxWaveBytes: 100); // budget smaller than the wave itself

        WaveData? first = cache.GetWave(9);
        Assert.NotNull(first);
        Assert.Equal(0, cache.CachedWaveCount);
        Assert.Equal(0, cache.ResidentWaveBytes);

        WaveData? second = cache.GetWave(9);
        Assert.NotNull(second);
        Assert.NotSame(first, second); // not retained, so this re-decoded
        Assert.Equal(2, dats.GetCallCount(9));
    }

    [Fact]
    [Trait("Status", "KnownFailure")]
    public void GetWave_ConcurrentSameId_PublishesOneCanonicalWaveAndDecodesOnce()
    {
        var dats = new FakeDatObjectSource();
        dats.AddWave(1, MakePcmWave(1, dataBytes: 1000));
        var cache = new DatSoundCache(dats, maxWaveBytes: 10_000);

        var results = new ConcurrentBag<WaveData?>();
        Parallel.For(0, 200, _ => results.Add(cache.GetWave(1)));

        WaveData?[] all = results.ToArray();
        Assert.All(all, Assert.NotNull);
        WaveData canonical = all[0]!;
        Assert.All(all, value => Assert.Same(canonical, value));
        Assert.Equal(1, dats.GetCallCount(1));
    }

    [Fact]
    public async Task GetWave_StaleMissCannotDecodeAgainAfterWinnerPublishes()
    {
        using var winnerDecodeEntered = new ManualResetEventSlim();
        using var releaseWinnerDecode = new ManualResetEventSlim();
        using var staleMissParked = new ManualResetEventSlim();
        using var releaseStaleMiss = new ManualResetEventSlim();

        var dats = new FakeDatObjectSource
        {
            BeforeWaveGet = _ =>
            {
                winnerDecodeEntered.Set();
                if (!releaseWinnerDecode.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("winner decode was never released");
            },
        };
        dats.AddWave(1, MakePcmWave(1, dataBytes: 1000));

        int initialMissOrdinal = 0;
        var cache = new DatSoundCache(
            dats,
            maxWaveBytes: 10_000,
            afterInitialWaveMiss: _ =>
            {
                if (Interlocked.Increment(ref initialMissOrdinal) != 2)
                    return;

                staleMissParked.Set();
                if (!releaseStaleMiss.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("stale miss was never released");
            });

        Task<WaveData?> winner = Task.Run(() => cache.GetWave(1));
        Assert.True(winnerDecodeEntered.Wait(TimeSpan.FromSeconds(5)));

        Task<WaveData?> stale = Task.Run(() => cache.GetWave(1));
        Assert.True(staleMissParked.Wait(TimeSpan.FromSeconds(5)));

        releaseWinnerDecode.Set();
        WaveData? canonical = await winner;
        releaseStaleMiss.Set();
        WaveData? recovered = await stale;

        Assert.NotNull(canonical);
        Assert.Same(canonical, recovered);
        Assert.Equal(1, dats.GetCallCount(1));
    }

    [Fact]
    public void GetWave_ConcurrentMissingId_NeverThrowsAndAllReturnNull()
    {
        var dats = new FakeDatObjectSource(); // id 5 never registered
        var cache = new DatSoundCache(dats, maxWaveBytes: 10_000);

        var results = new ConcurrentBag<WaveData?>();
        Parallel.For(0, 200, _ => results.Add(cache.GetWave(5)));

        Assert.All(results, Assert.Null);
        Assert.Equal(0, cache.CachedWaveCount);
    }

    // ── Test fixtures ────────────────────────────────────────────────────

    private static byte[] MakePcmHeader(ushort channels = 1, uint rate = 22050, ushort bits = 16)
    {
        byte[] h = new byte[18];
        h[0] = 0x01; h[1] = 0x00;                    // fmtTag PCM
        h[2] = (byte)channels; h[3] = (byte)(channels >> 8);
        h[4] = (byte)rate;     h[5] = (byte)(rate >> 8);
        h[6] = (byte)(rate >> 16); h[7] = (byte)(rate >> 24);
        uint avg = rate * channels * (uint)(bits / 8);
        h[8]  = (byte)avg;       h[9]  = (byte)(avg >> 8);
        h[10] = (byte)(avg >> 16); h[11] = (byte)(avg >> 24);
        ushort blockAlign = (ushort)(channels * (bits / 8));
        h[12] = (byte)blockAlign; h[13] = (byte)(blockAlign >> 8);
        h[14] = (byte)bits;       h[15] = (byte)(bits >> 8);
        return h;
    }

    private static Wave MakePcmWave(uint id, int dataBytes) => new()
    {
        Id = id,
        Header = MakePcmHeader(),
        Data = new byte[dataBytes],
    };

    private static Wave MakeMp3Wave(uint id) => new()
    {
        Id = id,
        Header = new byte[18] { 0x55, 0x00, 0x02, 0x00, 0x44, 0xAC, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        Data = new byte[1024],
    };

    private sealed class FakeDatObjectSource : IDatObjectSource
    {
        private readonly Dictionary<uint, Wave> _waves = new();
        private readonly Dictionary<uint, int> _callCounts = new();
        private readonly object _gate = new();

        public void AddWave(uint id, Wave wave) => _waves[id] = wave;

        public Action<uint>? BeforeWaveGet { get; init; }

        public int GetCallCount(uint id)
        {
            lock (_gate)
                return _callCounts.TryGetValue(id, out int count) ? count : 0;
        }

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : IDBObj
        {
            if (typeof(T) == typeof(Wave))
            {
                BeforeWaveGet?.Invoke(fileId);
                lock (_gate)
                {
                    _callCounts.TryGetValue(fileId, out int count);
                    _callCounts[fileId] = count + 1;
                }

                return _waves.TryGetValue(fileId, out var wave) ? (T)(object)wave : default;
            }
            return default;
        }

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value) where T : IDBObj
        {
            T? result = Get<T>(fileId);
            if (result is null)
            {
                value = default!;
                return false;
            }
            value = result;
            return true;
        }
    }
}
