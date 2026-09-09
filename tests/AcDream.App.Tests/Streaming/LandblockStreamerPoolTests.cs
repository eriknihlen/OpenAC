using System.Collections.Concurrent;
using AcDream.App.Streaming;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Streaming;

[Collection(AcDream.App.Tests.ThreadSchedulingCollection.Name)]
public sealed class LandblockStreamerPoolTests
{
    private const int SpinTimeoutMs = 10_000;
    private const int SpinStepMs = 10;

    private static AcDream.Core.Terrain.LandblockMeshData StubMesh() =>
        new(
            Array.Empty<AcDream.Core.Terrain.TerrainVertex>(),
            Array.Empty<uint>());

    private static LoadedLandblock StubLandblock(uint id) =>
        new(id, new LandBlock(), Array.Empty<WorldEntity>());

    private static uint IdInLane(LandblockStreamer streamer, int lane, HashSet<uint> taken)
    {
        for (uint x = 0; x < 256; x++)
        {
            for (uint y = 0; y < 256; y++)
            {
                uint id = (x << 24) | (y << 16) | 0xFFFFu;
                if (streamer.LaneFor(id) == lane && taken.Add(id))
                    return id;
            }
        }
        throw new InvalidOperationException($"No landblock id maps to lane {lane}.");
    }

    private static async Task<List<LandblockStreamResult>> DrainCountAsync(
        LandblockStreamer streamer,
        int count,
        int timeoutMs = SpinTimeoutMs)
    {
        var results = new List<LandblockStreamResult>(count);
        for (int i = 0; i < timeoutMs / SpinStepMs && results.Count < count; i++)
        {
            results.AddRange(streamer.DrainCompletions(count - results.Count));
            if (results.Count < count)
                await Task.Delay(SpinStepMs);
        }

        Assert.Equal(count, results.Count);
        return results;
    }

    [Fact]
    public void WorkerCount_DefaultsBoundedFloorsAtOneAndRejectsZero()
    {
        Assert.InRange(LandblockStreamer.DefaultWorkerCount, 1, 8);

        using var defaulted = new LandblockStreamer(loadLandblock: _ => null);
        Assert.Equal(LandblockStreamer.DefaultWorkerCount, defaulted.WorkerCount);

        using var explicitThree = new LandblockStreamer(
            loadLandblock: _ => null,
            buildMeshOrNull: null,
            workerCount: 3);
        Assert.Equal(3, explicitThree.WorkerCount);

        Assert.Throws<ArgumentOutOfRangeException>(() => new LandblockStreamer(
            loadLandblock: _ => null,
            buildMeshOrNull: null,
            workerCount: 0));
    }

    [Fact]
    public void LaneAssignment_SpreadsRealWindowIdsAcrossLanes()
    {
        using var streamer = new LandblockStreamer(
            loadLandblock: _ => null,
            buildMeshOrNull: null,
            workerCount: 8);

        var lanes = new HashSet<int>();
        for (uint x = 0xA0; x < 0xA0 + 25; x++)
            for (uint y = 0xB0; y < 0xB0 + 25; y++)
                lanes.Add(streamer.LaneFor((x << 24) | (y << 16) | 0xFFFFu));

        Assert.True(
            lanes.Count > 1,
            $"25x25 window ids collapsed into {lanes.Count} lane(s).");
    }

    [Fact]
    public async Task PerLandblockJobs_ExecuteInEnqueueOrder_UnderPoolContention()
    {
        const int idCount = 12;
        const int jobsPerId = 8; // alternating LoadFar / Unload
        int loaderCalls = 0;

        using var streamer = new LandblockStreamer(
            loadLandblock: (uint id, LandblockStreamJobKind _) =>
            {
                // Shake worker scheduling so a per-landblock ordering bug
                // would actually interleave.
                if (Interlocked.Increment(ref loaderCalls) % 3 == 0)
                    Thread.Sleep(1);
                return StubLandblock(id);
            },
            buildMeshOrNull: (_, _) => StubMesh(),
            workerCount: 4);
        streamer.Start();

        var ids = new uint[idCount];
        for (uint i = 0; i < idCount; i++)
            ids[i] = ((0x30u + i) << 24) | ((0x40u + i) << 16) | 0xFFFFu;

        // Round-robin across ids while the pool is already running, so jobs
        // for the same id repeatedly queue behind and race with other lanes.
        ulong generation = 0;
        for (int job = 0; job < jobsPerId; job++)
        {
            foreach (uint id in ids)
            {
                generation++;
                if (job % 2 == 0)
                    streamer.EnqueueLoad(id, LandblockStreamJobKind.LoadFar, generation);
                else
                    streamer.EnqueueUnload(id, generation);
            }
        }

        List<LandblockStreamResult> results =
            await DrainCountAsync(streamer, idCount * jobsPerId);

        foreach (uint id in ids)
        {
            var perId = results.Where(result => result.LandblockId == id).ToList();
            Assert.Equal(jobsPerId, perId.Count);
            for (int i = 0; i < perId.Count; i++)
            {
                if (i % 2 == 0)
                    Assert.IsType<LandblockStreamResult.Loaded>(perId[i]);
                else
                    Assert.IsType<LandblockStreamResult.Unloaded>(perId[i]);
                if (i > 0)
                {
                    Assert.True(
                        perId[i].Generation > perId[i - 1].Generation,
                        $"LB 0x{id:X8}: result {i} (gen {perId[i].Generation}) arrived " +
                        $"after gen {perId[i - 1].Generation} out of enqueue order.");
                }
            }
        }
    }

    [Fact]
    public async Task ClearPendingLoads_DropsQueuedLoadsAcrossAllLanes()
    {
        const int workerCount = 4;
        using var release = new ManualResetEventSlim();
        using var entered = new CountdownEvent(workerCount);
        var loadedIds = new ConcurrentBag<uint>();
        var blockerIds = new HashSet<uint>();

        using var streamer = new LandblockStreamer(
            loadLandblock: (uint id, LandblockStreamJobKind _) =>
            {
                loadedIds.Add(id);
                bool isBlocker;
                lock (blockerIds)
                    isBlocker = blockerIds.Contains(id);
                if (isBlocker)
                {
                    entered.Signal();
                    release.Wait();
                }
                return StubLandblock(id);
            },
            buildMeshOrNull: (_, _) => StubMesh(),
            workerCount: workerCount);

        var taken = new HashSet<uint>();
        var victimIds = new List<uint>();
        var unloadIds = new List<uint>();
        var survivorIds = new List<uint>();
        lock (blockerIds)
        {
            for (int lane = 0; lane < workerCount; lane++)
            {
                blockerIds.Add(IdInLane(streamer, lane, taken));
                victimIds.Add(IdInLane(streamer, lane, taken));
                victimIds.Add(IdInLane(streamer, lane, taken));
                unloadIds.Add(IdInLane(streamer, lane, taken));
                survivorIds.Add(IdInLane(streamer, lane, taken));
            }
        }

        streamer.Start();
        lock (blockerIds)
        {
            foreach (uint id in blockerIds)
                streamer.EnqueueLoad(id, LandblockStreamJobKind.LoadFar);
        }
        // Every worker is now inside its blocker build; everything below
        // queues behind them, one lane each.
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        foreach (uint id in victimIds)
            streamer.EnqueueLoad(id, LandblockStreamJobKind.LoadFar);
        foreach (uint id in unloadIds)
            streamer.EnqueueUnload(id);

        // Must supersede every load queued above on EVERY lane, while the
        // queued unloads survive.
        streamer.ClearPendingLoads();

        foreach (uint id in survivorIds)
            streamer.EnqueueLoad(id, LandblockStreamJobKind.LoadFar);

        release.Set();

        List<LandblockStreamResult> results =
            await DrainCountAsync(streamer, workerCount * 3);

        var loadedResultIds = results
            .OfType<LandblockStreamResult.Loaded>()
            .Select(result => result.LandblockId)
            .ToHashSet();
        var unloadedResultIds = results
            .OfType<LandblockStreamResult.Unloaded>()
            .Select(result => result.LandblockId)
            .ToHashSet();

        lock (blockerIds)
        {
            foreach (uint id in blockerIds)
                Assert.Contains(id, loadedResultIds);
        }
        foreach (uint id in survivorIds)
            Assert.Contains(id, loadedResultIds);
        foreach (uint id in unloadIds)
            Assert.Contains(id, unloadedResultIds);
        foreach (uint id in victimIds)
        {
            Assert.DoesNotContain(id, loadedResultIds);
            Assert.DoesNotContain(id, loadedIds);
        }
    }

    [Fact]
    public async Task NearTierJobs_RunBeforeQueuedFarJobs_WithinEachLane()
    {
        const int workerCount = 3;
        using var release = new ManualResetEventSlim();
        using var entered = new CountdownEvent(workerCount);
        var executionOrder = new ConcurrentQueue<uint>();
        var blockerIds = new HashSet<uint>();

        using var streamer = new LandblockStreamer(
            loadLandblock: (uint id, LandblockStreamJobKind _) =>
            {
                bool isBlocker;
                lock (blockerIds)
                    isBlocker = blockerIds.Contains(id);
                if (isBlocker)
                {
                    entered.Signal();
                    release.Wait();
                }
                else
                {
                    executionOrder.Enqueue(id);
                }
                return StubLandblock(id);
            },
            buildMeshOrNull: (_, _) => StubMesh(),
            workerCount: workerCount);

        var taken = new HashSet<uint>();
        var farIds = new uint[workerCount];
        var nearIds = new uint[workerCount];
        lock (blockerIds)
        {
            for (int lane = 0; lane < workerCount; lane++)
            {
                blockerIds.Add(IdInLane(streamer, lane, taken));
                farIds[lane] = IdInLane(streamer, lane, taken);
                nearIds[lane] = IdInLane(streamer, lane, taken);
            }
        }

        streamer.Start();
        lock (blockerIds)
        {
            foreach (uint id in blockerIds)
                streamer.EnqueueLoad(id, LandblockStreamJobKind.LoadFar);
        }
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        for (int lane = 0; lane < workerCount; lane++)
        {
            streamer.EnqueueLoad(farIds[lane], LandblockStreamJobKind.LoadFar);
            streamer.EnqueueLoad(nearIds[lane], LandblockStreamJobKind.LoadNear);
        }

        release.Set();

        await DrainCountAsync(streamer, workerCount * 3);

        var observed = executionOrder.ToList();
        for (int lane = 0; lane < workerCount; lane++)
        {
            int nearIndex = observed.IndexOf(nearIds[lane]);
            int farIndex = observed.IndexOf(farIds[lane]);
            Assert.True(nearIndex >= 0 && farIndex >= 0);
            Assert.True(
                nearIndex < farIndex,
                $"lane {lane}: near 0x{nearIds[lane]:X8} (index {nearIndex}) ran " +
                $"after far 0x{farIds[lane]:X8} (index {farIndex}).");
        }
    }

    [Fact]
    public async Task SingleWorkerPool_ReproducesSerialGlobalOrdering()
    {
        var callOrder = new List<uint>();
        var loaderThreads = new HashSet<int>();

        using var streamer = new LandblockStreamer(
            loadLandblock: (uint id, LandblockStreamJobKind _) =>
            {
                callOrder.Add(id);
                loaderThreads.Add(System.Environment.CurrentManagedThreadId);
                return StubLandblock(id);
            },
            buildMeshOrNull: (_, _) => StubMesh(),
            workerCount: 1);
        Assert.Equal(1, streamer.WorkerCount);

        streamer.EnqueueLoad(0xAAAAFFFFu, LandblockStreamJobKind.LoadFar);
        streamer.EnqueueLoad(0xBBBBFFFFu, LandblockStreamJobKind.LoadFar);
        streamer.EnqueueLoad(0xCCCCFFFFu, LandblockStreamJobKind.LoadNear);
        streamer.Start();

        List<LandblockStreamResult> results = await DrainCountAsync(streamer, 3);

        Assert.Equal(
            new[] { 0xCCCCFFFFu, 0xAAAAFFFFu, 0xBBBBFFFFu },
            callOrder);
        Assert.Single(loaderThreads);
        var first = Assert.IsType<LandblockStreamResult.Loaded>(results[0]);
        Assert.Equal(0xCCCCFFFFu, first.LandblockId);
    }

    [Fact]
    public void Dispose_JoinsEveryWorkerInThePool()
    {
        const int workerCount = 3;
        using var release = new ManualResetEventSlim();
        using var entered = new CountdownEvent(workerCount);
        var loaderThreads = new ConcurrentDictionary<int, byte>();
        var blockerIds = new HashSet<uint>();

        var streamer = new LandblockStreamer(
            loadLandblock: (uint id, LandblockStreamJobKind _) =>
            {
                loaderThreads.TryAdd(System.Environment.CurrentManagedThreadId, 0);
                entered.Signal();
                release.Wait();
                return StubLandblock(id);
            },
            buildMeshOrNull: (_, _) => StubMesh(),
            workerCount: workerCount);

        try
        {
            var taken = new HashSet<uint>();
            for (int lane = 0; lane < workerCount; lane++)
                blockerIds.Add(IdInLane(streamer, lane, taken));

            streamer.Start();
            foreach (uint id in blockerIds)
                streamer.EnqueueLoad(id, LandblockStreamJobKind.LoadFar);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(workerCount, loaderThreads.Count);

            Exception? disposeError = null;
            var disposeThread = new Thread(() =>
            {
                try
                {
                    streamer.Dispose();
                }
                catch (Exception error)
                {
                    disposeError = error;
                }
            })
            {
                IsBackground = true,
                Name = "LandblockStreamer pool dispose contract",
            };
            disposeThread.Start();
            Assert.True(SpinWait.SpinUntil(
                () => (disposeThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)),
                "dispose never waited for the still-building workers");

            release.Set();
            Assert.True(disposeThread.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(disposeError);

            Assert.Throws<ObjectDisposedException>(
                () => streamer.EnqueueLoad(0x1234FFFFu, LandblockStreamJobKind.LoadFar));
            Assert.Throws<ObjectDisposedException>(
                () => streamer.EnqueueUnload(0x1234FFFFu));
            Assert.Throws<ObjectDisposedException>(streamer.ClearPendingLoads);
        }
        finally
        {
            release.Set();
            streamer.Dispose();
        }
    }
}
