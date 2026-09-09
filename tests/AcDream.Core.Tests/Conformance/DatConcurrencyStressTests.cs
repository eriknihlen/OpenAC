using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using Xunit;

namespace AcDream.Core.Tests.Conformance;

[Trait("Category", "Conformance")]
[Trait("Lane", "InstalledDat")]
public class DatConcurrencyStressTests
{
    private const int HammerThreads = 8;
    private const int LoopsPerThread = 25;

    private sealed record FileRef(DatFileSource Source, uint Id);

    private enum DatFileSource { Cell, Portal, HighRes }

    private sealed record Golden(bool Ok, int Length, ulong Fnv);

    [Fact]
    public void ConcurrentRawReads_MatchSingleThreadedGolden()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var refs = BuildIdSet(dats);
        Assert.True(refs.Count > 500, $"id set unexpectedly small ({refs.Count}) — fixture assumptions broke");

        // Golden pass: single-threaded raw reads.
        var golden = new Dictionary<FileRef, Golden>(refs.Count);
        foreach (var r in refs)
            golden[r] = ReadRaw(dats, r);

        // Hammer: every thread re-reads the FULL set in its own shuffled order.
        var anomalies = HammerAndCollect(refs, r =>
        {
            var got = ReadRaw(dats, r);
            return golden[r] == got
                ? null
                : $"{r.Source} 0x{r.Id:X8}: golden=({golden[r].Ok},{golden[r].Length},{golden[r].Fnv:X16}) got=({got.Ok},{got.Length},{got.Fnv:X16})";
        });

        Assert.True(anomalies.IsEmpty,
            $"{anomalies.Count} concurrent raw-read anomalies. First: {string.Join(" | ", anomalies.Take(10))}");
    }

    [Fact]
    public void ConcurrentTypedReads_MatchSingleThreadedGolden()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); // dats absent (CI) — skip

        using var dats = new DatCollection(new DatCollectionOptions
        {
            DatDirectory = datDir,
            AccessType = DatAccessType.Read,
            FileCachingStrategy = FileCachingStrategy.Never,
        });
        var refs = BuildIdSet(dats);

        var golden = new Dictionary<FileRef, ulong>(refs.Count);
        foreach (var r in refs)
            golden[r] = ReadTypedFingerprint(dats, r);

        var anomalies = HammerAndCollect(refs, r =>
        {
            var got = ReadTypedFingerprint(dats, r);
            return golden[r] == got
                ? null
                : $"{r.Source} 0x{r.Id:X8}: golden=0x{golden[r]:X16} got=0x{got:X16}";
        });

        Assert.True(anomalies.IsEmpty,
            $"{anomalies.Count} concurrent typed-read anomalies. First: {string.Join(" | ", anomalies.Take(10))}");
    }

    // ---- hammer scaffolding -------------------------------------------------

    private static ConcurrentBag<string> HammerAndCollect(
        IReadOnlyList<FileRef> refs, Func<FileRef, string?> probe)
    {
        var anomalies = new ConcurrentBag<string>();
        var threads = new List<Thread>();
        using var start = new ManualResetEventSlim(false);

        for (int t = 0; t < HammerThreads; t++)
        {
            int seed = 7919 * (t + 1); // deterministic per-thread shuffle
            var thread = new Thread(() =>
            {
                var order = refs.ToArray();
                var rng = new Random(seed);
                start.Wait();
                for (int loop = 0; loop < LoopsPerThread; loop++)
                {
                    // Fisher–Yates so threads disagree about visit order — maximizes
                    // simultaneous different-file + same-file overlap.
                    for (int i = order.Length - 1; i > 0; i--)
                    {
                        int j = rng.Next(i + 1);
                        (order[i], order[j]) = (order[j], order[i]);
                    }
                    foreach (var r in order)
                    {
                        if (anomalies.Count > 50) return; // enough evidence
                        var a = probe(r);
                        if (a is not null) anomalies.Add(a);
                    }
                }
            })
            { IsBackground = true, Name = $"dat-hammer-{t}" };
            thread.Start();
            threads.Add(thread);
        }

        start.Set(); // release all threads at once
        foreach (var th in threads)
            Assert.True(th.Join(TimeSpan.FromMinutes(4)), "hammer thread did not finish in time");
        return anomalies;
    }

    private static List<FileRef> BuildIdSet(DatCollection dats)
    {
        var refs = new List<FileRef>();
        var portalIds = new HashSet<uint>();
        var highResIds = new HashSet<uint>();

        var cellIds = dats.Cell.Tree.GetFilesInRange(0xA8000000u, 0xABFFFFFFu)
            .Select(f => f.Id)
            .Take(2500)
            .ToList();
        refs.AddRange(cellIds.Select(id => new FileRef(DatFileSource.Cell, id)));

        int chained = 0;
        foreach (var envCellId in cellIds.Where(id => (id & 0xFFFFu) is >= 0x0100 and < 0xFF00))
        {
            if (chained++ >= 400) break;
            if (!dats.Cell.TryGet<EnvCell>(envCellId, out var envCell))
                continue;

            portalIds.Add(0x0D000000u | envCell.EnvironmentId);
            foreach (var rawSurface in envCell.Surfaces)
            {
                uint surfaceId = 0x08000000u | rawSurface;
                if (!portalIds.Add(surfaceId))
                    continue;
                if (!dats.Portal.TryGet<Surface>(surfaceId, out var surface)
                    || (uint)surface.OrigTextureId == 0)
                    continue;
                uint surfaceTextureId = (uint)surface.OrigTextureId;
                if (!portalIds.Add(surfaceTextureId))
                    continue;
                if (dats.Portal.TryGet<SurfaceTexture>(surfaceTextureId, out var st)
                    && st.Textures.Count > 0)
                {
                    uint renderSurfaceId = (uint)st.Textures[0];
                    portalIds.Add(renderSurfaceId);
                    highResIds.Add(renderSurfaceId); // texture path probes highres too
                }
            }
        }

        refs.AddRange(portalIds.Select(id => new FileRef(DatFileSource.Portal, id)));
        refs.AddRange(highResIds.Select(id => new FileRef(DatFileSource.HighRes, id)));
        return refs;
    }

    private static Golden ReadRaw(DatCollection dats, FileRef r)
    {
        var db = Db(dats, r.Source);
        if (!db.TryGetFileBytes(r.Id, out byte[]? bytes) || bytes is null)
            return new Golden(false, 0, 0);
        return new Golden(true, bytes.Length, Fnv(bytes));
    }

    private static ulong ReadTypedFingerprint(DatCollection dats, FileRef r)
    {
        var db = Db(dats, r.Source);
        if (r.Source == DatFileSource.Cell)
        {
            if ((r.Id & 0xFFFFu) == 0xFFFFu)
                return db.TryGet<LandBlock>(r.Id, out var lbk)
                    ? Mix(1, (ulong)lbk.Height.Length) : 0;
            if ((r.Id & 0xFFFFu) == 0xFFFEu)
                return db.TryGet<LandBlockInfo>(r.Id, out var lbi)
                    ? Mix(2, lbi.NumCells) : 0;
            return db.TryGet<EnvCell>(r.Id, out var cell)
                ? Mix(3, (ulong)cell.CellPortals.Count << 32
                         | (uint)cell.Surfaces.Count << 16
                         | cell.EnvironmentId) : 0;
        }

        return (r.Id >> 24) switch
        {
            0x0D => db.TryGet<DatReaderWriter.DBObjs.Environment>(r.Id, out var env)
                ? Mix(4, (ulong)env.Cells.Count) : 0,
            0x08 => db.TryGet<Surface>(r.Id, out var s)
                ? Mix(5, (ulong)s.Type << 32 | (uint)s.OrigTextureId) : 0,
            0x05 => db.TryGet<SurfaceTexture>(r.Id, out var st)
                ? Mix(6, (ulong)st.Textures.Count << 32
                         | (st.Textures.Count > 0 ? (uint)st.Textures[0] : 0u)) : 0,
            0x06 => db.TryGet<RenderSurface>(r.Id, out var rs)
                ? Mix(7, (ulong)rs.Width << 48 | (ulong)rs.Height << 32
                         | (uint)rs.SourceData.Length) : 0,
            _ => 0xFEEDu,
        };
    }

    private static DatDatabase Db(DatCollection dats, DatFileSource source) => source switch
    {
        DatFileSource.Cell => dats.Cell,
        DatFileSource.Portal => dats.Portal,
        _ => dats.HighRes,
    };

    private static ulong Fnv(byte[] bytes)
    {
        ulong h = 14695981039346656037UL;
        foreach (var b in bytes)
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h;
    }

    private static ulong Mix(ulong tag, ulong value) =>
        (tag << 56) ^ value ^ 0xA5A5_5A5A_0000_0000UL;
}
