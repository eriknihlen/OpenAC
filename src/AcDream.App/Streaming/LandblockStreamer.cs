using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AcDream.Core.World;

namespace AcDream.App.Streaming;

public sealed class LandblockStreamer : IDisposable, ILandblockCompletionSource
{
    public const int DefaultDrainBatchSize = 4;

    public static int DefaultWorkerCount =>
        Math.Max(1, Math.Min(Environment.ProcessorCount - 2, 8));

    private readonly Func<LandblockBuildRequest, LandblockBuild?> _loadLandblock;
    private readonly bool _supportsRequestOrigin;
    private readonly Func<uint, LoadedLandblock?, AcDream.Core.Terrain.LandblockMeshData?> _buildMeshOrNull;
    private readonly Channel<LandblockStreamJob>[] _lanes;
    private readonly Channel<LandblockStreamResult> _outbox;
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _inboxGate = new();
    private Thread[]? _workers;
    private int _activeWorkers;
    private Exception? _workerFailure;
    private int _completionBacklog;
    private int _disposed;
    private readonly object _disposeGate = new();
    private bool _disposeCompleted;

    private LandblockStreamer(
        Func<LandblockBuildRequest, LandblockBuild?> loadLandblock,
        Func<uint, LoadedLandblock?, AcDream.Core.Terrain.LandblockMeshData?>? buildMeshOrNull,
        bool supportsRequestOrigin,
        int? workerCount)
    {
        if (workerCount is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workerCount),
                workerCount,
                "The landblock build pool needs at least one worker.");
        }
        _loadLandblock = loadLandblock;
        _supportsRequestOrigin = supportsRequestOrigin;
        _buildMeshOrNull = buildMeshOrNull ?? ((_, _) => null);
        int lanes = workerCount ?? DefaultWorkerCount;
        _lanes = new Channel<LandblockStreamJob>[lanes];
        for (int i = 0; i < lanes; i++)
        {
            _lanes[i] = Channel.CreateUnbounded<LandblockStreamJob>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        }
        _outbox = Channel.CreateUnbounded<LandblockStreamResult>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    }

    public static LandblockStreamer CreateForRequests(
        Func<LandblockBuildRequest, LandblockBuild?> loadLandblock,
        Func<uint, LoadedLandblock?, AcDream.Core.Terrain.LandblockMeshData?>? buildMeshOrNull = null,
        int? workerCount = null) =>
        new(loadLandblock, buildMeshOrNull, supportsRequestOrigin: true, workerCount);

    public LandblockStreamer(
        Func<uint, LandblockStreamJobKind, LandblockBuild?> loadLandblock,
        Func<uint, LoadedLandblock?, AcDream.Core.Terrain.LandblockMeshData?>? buildMeshOrNull = null,
        int? workerCount = null)
        : this(
            request => loadLandblock(request.LandblockId, request.Kind) is { } build
                ? build
                : null,
            buildMeshOrNull,
            supportsRequestOrigin: false,
            workerCount)
    {
    }

    public LandblockStreamer(
        Func<uint, LandblockStreamJobKind, LoadedLandblock?> loadLandblock,
        Func<uint, LoadedLandblock?, AcDream.Core.Terrain.LandblockMeshData?>? buildMeshOrNull = null,
        int? workerCount = null)
        : this(
            request => loadLandblock(request.LandblockId, request.Kind) is { } landblock
                ? new LandblockBuild(landblock, Origin: request.Origin)
                : null,
            buildMeshOrNull,
            supportsRequestOrigin: false,
            workerCount)
    {
    }

    public LandblockStreamer(
        Func<uint, LoadedLandblock?> loadLandblock,
        Func<uint, LoadedLandblock?, AcDream.Core.Terrain.LandblockMeshData?>? buildMeshOrNull = null,
        int? workerCount = null)
        : this(
            request => loadLandblock(request.LandblockId) is { } landblock
                ? new LandblockBuild(landblock, Origin: request.Origin)
                : null,
            buildMeshOrNull,
            supportsRequestOrigin: false,
            workerCount)
    {
    }

    internal int WorkerCount => _lanes.Length;

    internal int LaneFor(uint landblockId)
    {
        if (_lanes.Length == 1)
            return 0;
        uint mixed = (landblockId >> 16) * 2654435761u;
        return (int)(mixed % (uint)_lanes.Length);
    }

    public void Start()
    {
        lock (_disposeGate)
        {
            if (System.Threading.Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(LandblockStreamer));
            if (_workers is not null)
                return;

            var workers = new Thread[_lanes.Length];
            System.Threading.Volatile.Write(ref _activeWorkers, workers.Length);
            for (int i = 0; i < workers.Length; i++)
            {
                int lane = i;
                workers[i] = new Thread(() => WorkerLoop(lane))
                {
                    IsBackground = true,
                    Name = workers.Length == 1
                        ? "acdream.streaming.worker"
                        : $"acdream.streaming.worker.{lane}",
                };
            }
            foreach (Thread worker in workers)
                worker.Start();
            _workers = workers;
        }
    }

    public void EnqueueLoad(
        uint landblockId,
        LandblockStreamJobKind kind = LandblockStreamJobKind.LoadNear,
        ulong generation = 0)
    {
        if (_supportsRequestOrigin)
        {
            throw new InvalidOperationException(
                "Request-aware landblock loaders require an explicit captured origin.");
        }
        EnqueueLoad(new LandblockBuildRequest(
            landblockId,
            kind,
            generation,
            default));
    }

    public void EnqueueLoad(LandblockBuildRequest request)
    {
        if (System.Threading.Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(LandblockStreamer));
        if (_supportsRequestOrigin && !request.Origin.IsSpecified)
        {
            throw new InvalidOperationException(
                "Request-aware landblock loaders require a specified captured origin.");
        }
        if (!_supportsRequestOrigin && request.Origin.IsSpecified)
        {
            throw new InvalidOperationException(
                "This compatibility landblock loader cannot consume a non-default build origin. " +
                "Use LandblockStreamer.CreateForRequests.");
        }
        AcDream.Core.Physics.PhysicsDiagnostics.LogTeleport(
            "ENQ",
            request.LandblockId,
            $"kind={request.Kind} origin=({request.Origin.CenterX:X2},{request.Origin.CenterY:X2})");
        WriteJob(new LandblockStreamJob.Load(
            request.LandblockId,
            request.Kind,
            request.Generation,
            request.Origin));
    }

    public void EnqueueUnload(uint landblockId, ulong generation = 0)
    {
        WriteJob(new LandblockStreamJob.Unload(landblockId, generation));
    }

    public void ClearPendingLoads()
    {
        WriteJob(new LandblockStreamJob.ClearLoads());
    }

    private void WriteJob(LandblockStreamJob job)
    {
        lock (_inboxGate)
        {
            if (System.Threading.Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(LandblockStreamer));
            if (_workerFailure is { } failure)
                throw new InvalidOperationException("A landblock streaming worker has terminated.", failure);
            if (job is LandblockStreamJob.ClearLoads)
            {
                foreach (Channel<LandblockStreamJob> lane in _lanes)
                {
                    if (!lane.Writer.TryWrite(job))
                        throw new InvalidOperationException("The landblock streaming inbox is no longer accepting work.");
                }
                return;
            }
            if (!_lanes[LaneFor(job.LandblockId)].Writer.TryWrite(job))
                throw new InvalidOperationException("The landblock streaming inbox is no longer accepting work.");
        }
    }

    public IReadOnlyList<LandblockStreamResult> DrainCompletions(int maxBatchSize = DefaultDrainBatchSize)
    {
        var batch = new List<LandblockStreamResult>(maxBatchSize);
        while (batch.Count < maxBatchSize && TryRead(out var result))
        {
            if (result is null)
                throw new InvalidOperationException(
                    "The completion channel returned a null result.");
            batch.Add(result);
        }
        return batch;
    }

    public int BacklogCount => Math.Max(
        0,
        System.Threading.Volatile.Read(ref _completionBacklog));

    public bool TryPeek(out LandblockStreamResult? result) =>
        _outbox.Reader.TryPeek(out result);

    public bool TryRead(out LandblockStreamResult? result)
    {
        if (!_outbox.Reader.TryRead(out result))
            return false;
        System.Threading.Interlocked.Decrement(ref _completionBacklog);
        return true;
    }

    private void PublishResult(LandblockStreamResult result)
    {
        if (_outbox.Writer.TryWrite(result))
            System.Threading.Interlocked.Increment(ref _completionBacklog);
    }

    private void WorkerLoop(int laneIndex)
    {
        ChannelReader<LandblockStreamJob> inbox = _lanes[laneIndex].Reader;
        var highPriority = new Queue<LandblockStreamJob>();
        var lowPriority = new Queue<LandblockStreamJob>();

        try
        {
            while (!_cancel.Token.IsCancellationRequested)
            {
                if (highPriority.Count == 0 &&
                    lowPriority.Count == 0 &&
                    !inbox.WaitToReadAsync(_cancel.Token).AsTask().GetAwaiter().GetResult())
                {
                    break;
                }

                while (inbox.TryRead(out var job))
                {
                    if (job is LandblockStreamJob.ClearLoads)
                    {
                        DropLoadJobs(highPriority);
                        DropLoadJobs(lowPriority);
                        continue;
                    }
                    EnqueuePrioritized(job, highPriority, lowPriority);
                }

                if (highPriority.Count == 0 && lowPriority.Count == 0)
                    continue;

                if (_cancel.Token.IsCancellationRequested) return;
                var next = highPriority.Count > 0
                    ? highPriority.Dequeue()
                    : lowPriority.Dequeue();
                HandleJob(next);
            }
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
        catch (Exception ex)
        {
            bool cascade;
            lock (_inboxGate)
            {
                cascade = _workerFailure is not null && ex is ChannelClosedException;
                _workerFailure ??= ex;
                foreach (Channel<LandblockStreamJob> lane in _lanes)
                    lane.Writer.TryComplete(ex);
            }
            if (!cascade)
            {
                PublishResult(new LandblockStreamResult.WorkerCrashed(
                    _lanes.Length == 1
                        ? ex.ToString()
                        : $"worker {laneIndex}: {ex}"));
                // Stop the sibling workers. Safe against Dispose: its
                // CTS disposal only happens after every worker (this one
                // included) has been joined.
                _cancel.Cancel();
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeWorkers) == 0)
                _outbox.Writer.TryComplete();
        }
    }

    private static void EnqueuePrioritized(
        LandblockStreamJob job,
        Queue<LandblockStreamJob> highPriority,
        Queue<LandblockStreamJob> lowPriority)
    {
        if (job is LandblockStreamJob.Load
            {
                Kind: LandblockStreamJobKind.LoadNear or LandblockStreamJobKind.PromoteToNear
            } high)
        {
            RemoveLowPriorityJobsForLandblock(
                lowPriority,
                high.LandblockId,
                removeLoadFar: true,
                removeUnload: true);
            highPriority.Enqueue(job);
            return;
        }

        lowPriority.Enqueue(job);
    }

    private static void DropLoadJobs(Queue<LandblockStreamJob> queue)
    {
        int count = queue.Count;
        for (int i = 0; i < count; i++)
        {
            var job = queue.Dequeue();
            if (job is not LandblockStreamJob.Load)
                queue.Enqueue(job);
        }
    }

    private static void RemoveLowPriorityJobsForLandblock(
        Queue<LandblockStreamJob> queue,
        uint landblockId,
        bool removeLoadFar,
        bool removeUnload)
    {
        int count = queue.Count;
        for (int i = 0; i < count; i++)
        {
            var job = queue.Dequeue();
            bool remove = job.LandblockId == landblockId && job switch
            {
                LandblockStreamJob.Load { Kind: LandblockStreamJobKind.LoadFar } => removeLoadFar,
                LandblockStreamJob.Unload => removeUnload,
                _ => false
            };
            if (!remove)
                queue.Enqueue(job);
        }
    }

    private void HandleJob(LandblockStreamJob job)
    {
        switch (job)
        {
            case LandblockStreamJob.Load load:
                try
                {
                    var build = _loadLandblock(load.Request);
                    if (build is null)
                    {
                        PublishResult(new LandblockStreamResult.Failed(
                            load.LandblockId, "LandblockLoader.Load returned null", load.Generation));
                        break;
                    }
                    if (build.Origin != load.Origin)
                    {
                        PublishResult(new LandblockStreamResult.Failed(
                            load.LandblockId,
                            $"Landblock build origin {build.Origin} did not match request origin {load.Origin}",
                            load.Generation));
                        break;
                    }
                    var lb = build.Landblock;
                    if (load.Kind == LandblockStreamJobKind.PromoteToNear)
                    {
                        var promotedMesh = _buildMeshOrNull(load.LandblockId, lb);
                        if (promotedMesh is null)
                        {
                            PublishResult(new LandblockStreamResult.Failed(
                                load.LandblockId, "buildMeshOrNull returned null", load.Generation));
                            break;
                        }
                        PublishResult(new LandblockStreamResult.Promoted(
                            load.LandblockId, build, promotedMesh, load.Generation));
                        break;
                    }
                    var mesh = _buildMeshOrNull(load.LandblockId, lb);
                    if (mesh is null)
                    {
                        PublishResult(new LandblockStreamResult.Failed(
                            load.LandblockId, "buildMeshOrNull returned null", load.Generation));
                        break;
                    }
                    var tier = load.Kind == LandblockStreamJobKind.LoadFar
                        ? LandblockStreamTier.Far : LandblockStreamTier.Near;
                    if (tier == LandblockStreamTier.Far)
                    {
                        // Belt-and-suspenders: factory should have skipped
                        // entity hydration for LoadFar. If it didn't, fail
                        // loud in Debug builds and strip in Release.
                        bool hasNearPayload =
                            lb.Entities.Count > 0 ||
                            build.EnvCells is not null ||
                            lb.PhysicsDats is { } physicsDats &&
                            (physicsDats.Info is not null ||
                             physicsDats.EnvCells.Count > 0 ||
                             physicsDats.Environments.Count > 0 ||
                             physicsDats.Setups.Count > 0 ||
                             physicsDats.GfxObjs.Count > 0);
                        System.Diagnostics.Debug.Assert(
                            !hasNearPayload,
                            $"Far-tier factory returned Near payload for LB 0x{load.LandblockId:X8}");
                        lb = new LoadedLandblock(
                            lb.LandblockId,
                            lb.Heightmap,
                            System.Array.Empty<AcDream.Core.World.WorldEntity>(),
                            PhysicsDatBundle.Empty);
                        build = new LandblockBuild(
                            lb,
                            Origin: build.Origin,
                            TerrainBounds: build.TerrainBounds);
                    }
                    PublishResult(new LandblockStreamResult.Loaded(
                        load.LandblockId, tier, build, mesh, load.Generation));
                }
                catch (Exception ex)
                {
                    PublishResult(new LandblockStreamResult.Failed(
                        load.LandblockId, ex.ToString(), load.Generation));
                }
                break;

            case LandblockStreamJob.Unload unload:
                PublishResult(new LandblockStreamResult.Unloaded(
                    unload.LandblockId,
                    unload.Generation));
                break;
        }
    }

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_disposeCompleted)
                return;

            System.Threading.Interlocked.Exchange(ref _disposed, 1);
            _cancel.Cancel();
            lock (_inboxGate)
            {
                foreach (Channel<LandblockStreamJob> lane in _lanes)
                    lane.Writer.TryComplete();
            }
            // The owner releases the memory-mapped DAT immediately after this
            // object. Join every actual worker without a grace-period timeout
            // so no native read can survive into that teardown.
            if (_workers is { } workers)
            {
                foreach (Thread worker in workers)
                    worker.Join();
            }
            _cancel.Dispose();
            _disposeCompleted = true;
        }
    }
}
