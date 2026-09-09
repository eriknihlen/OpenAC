using System.Threading.Tasks;
using AcDream.App.Streaming;
using AcDream.App.Rendering.Wb;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using Xunit;

namespace AcDream.Core.Tests.Streaming;

public class LandblockStreamerTests
{
    private const int SpinTimeoutMs = 2000;
    private const int SpinStepMs = 10;
    private const int SpinMaxIterations = SpinTimeoutMs / SpinStepMs;

    [Fact]
    public async Task Load_FollowedByDrain_ReturnsLoadedRecord()
    {
        var stubLandblock = new LoadedLandblock(
            0xA9B4FFFEu,
            new LandBlock(),
            System.Array.Empty<WorldEntity>());
        var stubMesh = new AcDream.Core.Terrain.LandblockMeshData(
            System.Array.Empty<AcDream.Core.Terrain.TerrainVertex>(),
            System.Array.Empty<uint>());

        using var streamer = new LandblockStreamer(
            loadLandblock: id => id == 0xA9B4FFFEu ? stubLandblock : null,
            buildMeshOrNull: (_, _) => stubMesh);

        streamer.Start();
        streamer.EnqueueLoad(0xA9B4FFFEu, generation: 42);

        LandblockStreamResult? result = null;
        for (int i = 0; i < SpinMaxIterations && result is null; i++)
        {
            var drained = streamer.DrainCompletions(maxBatchSize: LandblockStreamer.DefaultDrainBatchSize);
            if (drained.Count > 0) result = drained[0];
            else await Task.Delay(SpinStepMs);
        }

        Assert.NotNull(result);
        var loaded = Assert.IsType<LandblockStreamResult.Loaded>(result);
        Assert.Equal(0xA9B4FFFEu, loaded.LandblockId);
        Assert.Equal(42ul, loaded.Generation);
        Assert.Same(stubLandblock, loaded.Landblock);
    }

    [Fact]
    public async Task LoadNear_OvertakesQueuedFarLoads()
    {
        var callOrder = new System.Collections.Generic.List<(uint Id, LandblockStreamJobKind Kind)>();
        var stubMesh = new AcDream.Core.Terrain.LandblockMeshData(
            System.Array.Empty<AcDream.Core.Terrain.TerrainVertex>(),
            System.Array.Empty<uint>());

        using var streamer = new LandblockStreamer(
            loadLandblock: (id, kind) =>
            {
                callOrder.Add((id, kind));
                return new LoadedLandblock(id, new LandBlock(), System.Array.Empty<WorldEntity>());
            },
            buildMeshOrNull: (_, _) => stubMesh,
            workerCount: 1);

        streamer.EnqueueLoad(0xAAAAFFFFu, LandblockStreamJobKind.LoadFar);
        streamer.EnqueueLoad(0xBBBBFFFFu, LandblockStreamJobKind.LoadFar);
        streamer.EnqueueLoad(0xCCCCFFFFu, LandblockStreamJobKind.LoadFar);
        streamer.EnqueueLoad(0xDDDDFFFFu, LandblockStreamJobKind.LoadNear);
        streamer.Start();

        var result = await DrainFirstAsync(streamer);

        var loaded = Assert.IsType<LandblockStreamResult.Loaded>(result);
        Assert.Equal(0xDDDDFFFFu, loaded.LandblockId);
        Assert.Equal((0xDDDDFFFFu, LandblockStreamJobKind.LoadNear), callOrder[0]);
    }

    [Fact]
    public async Task PromoteToNear_ProducesPromotedWithMeshData()
    {
        int meshBuildCalls = 0;
        var entity = new WorldEntity
        {
            Id = 7,
            SourceGfxObjOrSetupId = 0,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = System.Array.Empty<MeshRef>()
        };

        using var streamer = new LandblockStreamer(
            loadLandblock: (id, kind) => new LoadedLandblock(id, new LandBlock(), new[] { entity }),
            buildMeshOrNull: (_, _) =>
            {
                meshBuildCalls++;
                return new AcDream.Core.Terrain.LandblockMeshData(
                    System.Array.Empty<AcDream.Core.Terrain.TerrainVertex>(),
                    System.Array.Empty<uint>());
            });

        streamer.EnqueueLoad(0xA9B4FFFFu, LandblockStreamJobKind.PromoteToNear);
        streamer.Start();

        var result = await DrainFirstAsync(streamer);

        var promoted = Assert.IsType<LandblockStreamResult.Promoted>(result);
        Assert.Equal(0xA9B4FFFFu, promoted.LandblockId);
        Assert.Same(entity, promoted.Entities[0]);
        Assert.NotNull(promoted.MeshData);
        Assert.Equal(1, meshBuildCalls);
    }

    [Fact]
    public async Task Load_CarriesTheExactCompletedCellTransactionToTheConsumer()
    {
        var landblock = new LoadedLandblock(
            0x8C04FFFFu,
            new LandBlock(),
            System.Array.Empty<WorldEntity>());
        var cellBuild = new EnvCellLandblockBuild(
            landblock.LandblockId,
            System.Array.Empty<AcDream.App.Rendering.LoadedCell>(),
            System.Array.Empty<EnvCellShellPlacement>());
        var build = new LandblockBuild(landblock, cellBuild);
        var mesh = new AcDream.Core.Terrain.LandblockMeshData(
            System.Array.Empty<AcDream.Core.Terrain.TerrainVertex>(),
            System.Array.Empty<uint>());

        using var streamer = new LandblockStreamer(
            loadLandblock: (_, _) => build,
            buildMeshOrNull: (_, _) => mesh);
        streamer.Start();
        streamer.EnqueueLoad(landblock.LandblockId, LandblockStreamJobKind.LoadNear);

        var loaded = Assert.IsType<LandblockStreamResult.Loaded>(
            await DrainFirstAsync(streamer));

        Assert.Same(build, loaded.Build);
        Assert.Same(cellBuild, loaded.Build.EnvCells);
    }

    [Fact]
    public async Task PromoteToNear_OvertakesAndSupersedesQueuedFarLoadForSameLandblock()
    {
        var callOrder = new System.Collections.Generic.List<(uint Id, LandblockStreamJobKind Kind)>();
        var stubMesh = new AcDream.Core.Terrain.LandblockMeshData(
            System.Array.Empty<AcDream.Core.Terrain.TerrainVertex>(),
            System.Array.Empty<uint>());

        using var streamer = new LandblockStreamer(
            loadLandblock: (id, kind) =>
            {
                callOrder.Add((id, kind));
                return new LoadedLandblock(id, new LandBlock(), System.Array.Empty<WorldEntity>());
            },
            buildMeshOrNull: (_, _) => stubMesh);

        streamer.EnqueueLoad(0xA9B4FFFFu, LandblockStreamJobKind.LoadFar);
        streamer.EnqueueLoad(0xA9B4FFFFu, LandblockStreamJobKind.PromoteToNear);
        streamer.Start();

        var result = await DrainFirstAsync(streamer);

        var promoted = Assert.IsType<LandblockStreamResult.Promoted>(result);
        Assert.Equal(0xA9B4FFFFu, promoted.LandblockId);
        Assert.Equal((0xA9B4FFFFu, LandblockStreamJobKind.PromoteToNear), callOrder[0]);
    }

    [Fact]
    public async Task Load_WhenLoaderReturnsNull_ReportsFailed()
    {
        using var streamer = new LandblockStreamer(
            loadLandblock: _ => null);

        streamer.Start();
        streamer.EnqueueLoad(0x12340000u);

        LandblockStreamResult? result = null;
        for (int i = 0; i < SpinMaxIterations && result is null; i++)
        {
            var drained = streamer.DrainCompletions(LandblockStreamer.DefaultDrainBatchSize);
            if (drained.Count > 0) result = drained[0];
            else await Task.Delay(SpinStepMs);
        }

        Assert.NotNull(result);
        Assert.IsType<LandblockStreamResult.Failed>(result);
    }

    [Fact]
    public async Task Load_WhenBuildMeshReturnsNull_ReportsFailed()
    {
        var stubLandblock = new LoadedLandblock(
            0xABCDFFFEu,
            new LandBlock(),
            System.Array.Empty<WorldEntity>());

        using var streamer = new LandblockStreamer(
            loadLandblock: _ => stubLandblock,
            buildMeshOrNull: (_, _) => null);   // mesh-build returns null

        streamer.Start();
        streamer.EnqueueLoad(0xABCDFFFEu);

        LandblockStreamResult? result = null;
        for (int i = 0; i < SpinMaxIterations && result is null; i++)
        {
            var drained = streamer.DrainCompletions(LandblockStreamer.DefaultDrainBatchSize);
            if (drained.Count > 0) result = drained[0];
            else await Task.Delay(SpinStepMs);
        }

        Assert.NotNull(result);
        var failed = Assert.IsType<LandblockStreamResult.Failed>(result);
        Assert.Equal(0xABCDFFFEu, failed.LandblockId);
        Assert.Contains("mesh", failed.Error, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_WhenLoaderThrows_ReportsFailedWithMessage()
    {
        using var streamer = new LandblockStreamer(
            loadLandblock: _ => throw new System.InvalidOperationException("boom"));

        streamer.Start();
        streamer.EnqueueLoad(0x55550000u);

        LandblockStreamResult? result = null;
        for (int i = 0; i < SpinMaxIterations && result is null; i++)
        {
            var drained = streamer.DrainCompletions(LandblockStreamer.DefaultDrainBatchSize);
            if (drained.Count > 0) result = drained[0];
            else await Task.Delay(SpinStepMs);
        }

        var failed = Assert.IsType<LandblockStreamResult.Failed>(result);
        Assert.Contains("boom", failed.Error);
    }

    [Fact]
    public async Task Unload_ProducesUnloadedResult()
    {
        using var streamer = new LandblockStreamer(loadLandblock: _ => null);

        streamer.Start();
        streamer.EnqueueUnload(0xABCD0000u, generation: 43);

        LandblockStreamResult? result = null;
        for (int i = 0; i < SpinMaxIterations && result is null; i++)
        {
            var drained = streamer.DrainCompletions(LandblockStreamer.DefaultDrainBatchSize);
            if (drained.Count > 0) result = drained[0];
            else await Task.Delay(SpinStepMs);
        }

        var unloaded = Assert.IsType<LandblockStreamResult.Unloaded>(result);
        Assert.Equal(0xABCD0000u, unloaded.LandblockId);
        Assert.Equal(43ul, unloaded.Generation);
    }

    [Fact]
    public async Task CompletionSourcePeekPreservesExactResultAndBacklog()
    {
        using var streamer = new LandblockStreamer(loadLandblock: _ => null);
        streamer.Start();
        streamer.EnqueueUnload(0xABCE0000u, generation: 44);

        for (int i = 0;
            i < SpinMaxIterations && streamer.BacklogCount == 0;
            i++)
        {
            await Task.Delay(SpinStepMs);
        }

        Assert.Equal(1, streamer.BacklogCount);
        Assert.True(streamer.TryPeek(out LandblockStreamResult? firstPeek));
        Assert.NotNull(firstPeek);
        Assert.Equal(1, streamer.BacklogCount);
        Assert.True(streamer.TryPeek(out LandblockStreamResult? secondPeek));
        Assert.Same(firstPeek, secondPeek);
        Assert.Equal(1, streamer.BacklogCount);

        Assert.True(streamer.TryRead(out LandblockStreamResult? consumed));
        Assert.Same(firstPeek, consumed);
        Assert.Equal(0, streamer.BacklogCount);
        Assert.False(streamer.TryRead(out _));
    }

    [Fact]
    public async Task Load_ExecutesLoaderOnWorkerThread()
    {
        int testThreadId = System.Environment.CurrentManagedThreadId;
        int? loaderThreadId = null;
        var stubLandblock = new LoadedLandblock(
            0x77770FFEu,
            new LandBlock(),
            System.Array.Empty<WorldEntity>());
        var stubMesh = new AcDream.Core.Terrain.LandblockMeshData(
            System.Array.Empty<AcDream.Core.Terrain.TerrainVertex>(),
            System.Array.Empty<uint>());

        using var streamer = new LandblockStreamer(
            loadLandblock: id =>
            {
                loaderThreadId = System.Environment.CurrentManagedThreadId;
                return stubLandblock;
            },
            buildMeshOrNull: (_, _) => stubMesh);

        streamer.Start();
        streamer.EnqueueLoad(0x77770FFEu);

        LandblockStreamResult? result = null;
        for (int i = 0; i < SpinMaxIterations && result is null; i++)
        {
            var drained = streamer.DrainCompletions(LandblockStreamer.DefaultDrainBatchSize);
            if (drained.Count > 0) result = drained[0];
            else await Task.Delay(SpinStepMs);
        }

        Assert.NotNull(result);
        Assert.IsType<LandblockStreamResult.Loaded>(result);
        // The loader MUST have run on a different thread than the test thread.
        Assert.NotNull(loaderThreadId);
        Assert.NotEqual(testThreadId, loaderThreadId.Value);
    }

    [Fact]
    public void DisposeAndConcurrentDisposeWaitForInFlightLoad()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var streamer = new LandblockStreamer(loadLandblock: _ =>
        {
            entered.Set();
            release.Wait();
            return null;
        });

        try
        {
            streamer.Start();
            streamer.EnqueueLoad(0x12340000u);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

            Exception? firstError = null;
            var firstDisposeThread = new Thread(() =>
            {
                try
                {
                    streamer.Dispose();
                }
                catch (Exception error)
                {
                    firstError = error;
                }
            })
            {
                IsBackground = true,
                Name = "LandblockStreamer primary dispose contract",
            };
            firstDisposeThread.Start();
            Assert.True(SpinWait.SpinUntil(
                () => (firstDisposeThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(2)),
                "the primary dispose never waited for the in-flight load");

            Exception? secondError = null;
            var secondDisposeThread = new Thread(() =>
            {
                try
                {
                    streamer.Dispose();
                }
                catch (Exception error)
                {
                    secondError = error;
                }
            })
            {
                IsBackground = true,
                Name = "LandblockStreamer concurrent dispose contract",
            };
            secondDisposeThread.Start();
            Assert.True(SpinWait.SpinUntil(
                () => (secondDisposeThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(2)),
                "the concurrent dispose never waited for primary disposal");

            release.Set();
            Assert.True(firstDisposeThread.Join(TimeSpan.FromSeconds(2)));
            Assert.True(secondDisposeThread.Join(TimeSpan.FromSeconds(2)));
            Assert.Null(firstError);
            Assert.Null(secondError);
        }
        finally
        {
            release.Set();
            streamer.Dispose();
        }
    }

    [Fact]
    public void DisposeRejectsEveryEnqueueKind()
    {
        var streamer = new LandblockStreamer(loadLandblock: _ => null);
        streamer.Start();
        streamer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => streamer.EnqueueLoad(0x12340000u));
        Assert.Throws<ObjectDisposedException>(() => streamer.EnqueueUnload(0x12340000u));
        Assert.Throws<ObjectDisposedException>(streamer.ClearPendingLoads);
    }

    [Fact]
    public async Task ConcurrentStartAndDisposeLeaveAClosedStreamer()
    {
        for (int iteration = 0; iteration < 20; iteration++)
        {
            var streamer = new LandblockStreamer(loadLandblock: _ => null);
            Exception? startFailure = null;

            Task start = Task.Run(() =>
            {
                try { streamer.Start(); }
                catch (ObjectDisposedException ex) { startFailure = ex; }
            });
            Task dispose = Task.Run(streamer.Dispose);
            await Task.WhenAll(start, dispose).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(startFailure is null or ObjectDisposedException);
            Assert.Throws<ObjectDisposedException>(() => streamer.EnqueueLoad(0x12340000u));
            streamer.Dispose();
        }
    }

    private static async Task<LandblockStreamResult> DrainFirstAsync(LandblockStreamer streamer)
    {
        for (int i = 0; i < SpinMaxIterations; i++)
        {
            var drained = streamer.DrainCompletions(maxBatchSize: LandblockStreamer.DefaultDrainBatchSize);
            if (drained.Count > 0) return drained[0];
            await Task.Delay(SpinStepMs);
        }

        throw new Xunit.Sdk.XunitException("Timed out waiting for streamer completion.");
    }
}
