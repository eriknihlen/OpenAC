using System.Collections.Generic;
using AcDream.App.Streaming;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using Xunit;

namespace AcDream.Core.Tests.Streaming;

public class StreamingControllerTests
{
    private sealed class FakeStreamer
    {
        public List<uint> Loads { get; } = new();
        public List<(uint Id, LandblockStreamJobKind Kind)> LoadJobs { get; } = new();
        public List<uint> Unloads { get; } = new();
        public Queue<LandblockStreamResult> Pending { get; } = new();

        public void EnqueueLoad(uint id, LandblockStreamJobKind kind)
        {
            Loads.Add(id);
            LoadJobs.Add((id, kind));
        }
        public void EnqueueUnload(uint id) => Unloads.Add(id);
        public IReadOnlyList<LandblockStreamResult> DrainCompletions(int max)
        {
            var batch = new List<LandblockStreamResult>();
            while (batch.Count < max && Pending.Count > 0)
                batch.Add(Pending.Dequeue());
            return batch;
        }
    }

    [Fact]
    public void FirstTick_EnqueuesWholeVisibleWindow()
    {
        var state = new GpuWorldState();
        var fake = new FakeStreamer();
        var controller = new StreamingController(
            enqueueLoad: fake.EnqueueLoad,
            enqueueUnload: fake.EnqueueUnload,
            drainCompletions: fake.DrainCompletions,
            applyTerrain: (_, _) => { },
            state: state,
            nearRadius: 2,
            farRadius: 2);

        controller.Tick(observerCx: 50, observerCy: 50);

        // 5×5 window = 25 loads enqueued (nearRadius==farRadius so all go to ToLoadNear), 0 unloads.
        Assert.Equal(25, fake.Loads.Count);
        Assert.Empty(fake.Unloads);
    }

    [Fact]
    public void SecondTick_SamePosition_EnqueuesNothing()
    {
        var state = new GpuWorldState();
        var fake = new FakeStreamer();
        var controller = new StreamingController(
            fake.EnqueueLoad, fake.EnqueueUnload, fake.DrainCompletions,
            (_, _) => { }, state, nearRadius: 2, farRadius: 2);

        controller.Tick(50, 50);
        fake.Loads.Clear();

        controller.Tick(50, 50);

        Assert.Empty(fake.Loads);
        Assert.Empty(fake.Unloads);
    }

    [Fact]
    public void FirstTick_EnqueuesRevealNeighborhoodBeforeOtherNearWork()
    {
        var state = new GpuWorldState();
        var fake = new FakeStreamer();
        var controller = new StreamingController(
            enqueueLoad: fake.EnqueueLoad,
            enqueueUnload: fake.EnqueueUnload,
            drainCompletions: fake.DrainCompletions,
            applyTerrain: (_, _) => { },
            state: state,
            nearRadius: 2,
            farRadius: 2);
        uint destination =
            StreamingRegion.EncodeLandblockIdForTest(50, 50);
        controller.BeginDestinationReservation(
            revealGeneration: 1,
            destinationCell: destination,
            requiredRenderRadius: 1);

        controller.Tick(observerCx: 50, observerCy: 50);

        Assert.Equal(25, fake.LoadJobs.Count);
        Assert.All(
            fake.LoadJobs.Take(9),
            job => Assert.InRange(
                Math.Max(
                    Math.Abs((int)((job.Id >> 24) & 0xFFu) - 50),
                    Math.Abs((int)((job.Id >> 16) & 0xFFu) - 50)),
                0,
                1));
        Assert.All(
            fake.LoadJobs.Skip(9),
            job => Assert.Equal(
                2,
                Math.Max(
                    Math.Abs((int)((job.Id >> 24) & 0xFFu) - 50),
                    Math.Abs((int)((job.Id >> 16) & 0xFFu) - 50))));
    }

    [Fact]
    public void ReconfigureRadii_SmallerWindowUnloadsOnlyOutsideResidents()
    {
        var state = new GpuWorldState();
        var fake = new FakeStreamer();
        int pendingClears = 0;
        var controller = new StreamingController(
            fake.EnqueueLoad,
            fake.EnqueueUnload,
            fake.DrainCompletions,
            (_, _) => { },
            state,
            nearRadius: 2,
            farRadius: 2,
            clearPendingLoads: () => pendingClears++);
        controller.Tick(50, 50);
        for (int dx = -2; dx <= 2; dx++)
        for (int dy = -2; dy <= 2; dy++)
            AddPublished(state, 50 + dx, 50 + dy, LandblockStreamTier.Near);
        fake.Loads.Clear();
        fake.LoadJobs.Clear();

        controller.ReconfigureRadii(0, 0);

        Assert.Equal(1, pendingClears);
        Assert.Empty(fake.Loads);
        Assert.Equal(24, fake.Unloads.Count);
        Assert.DoesNotContain(0x3232FFFFu, fake.Unloads);
    }

    [Fact]
    public void ReconfigureRadii_LargerWindowLoadsOnlyMissingResidents()
    {
        var state = new GpuWorldState();
        var fake = new FakeStreamer();
        var controller = new StreamingController(
            fake.EnqueueLoad,
            fake.EnqueueUnload,
            fake.DrainCompletions,
            (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0);
        controller.Tick(50, 50);
        AddPublished(state, 50, 50, LandblockStreamTier.Near);
        fake.Loads.Clear();
        fake.LoadJobs.Clear();

        controller.ReconfigureRadii(1, 1);

        Assert.Equal(8, fake.LoadJobs.Count);
        Assert.All(
            fake.LoadJobs,
            static job => Assert.Equal(LandblockStreamJobKind.LoadNear, job.Kind));
        Assert.DoesNotContain(fake.LoadJobs, static job => job.Id == 0x3232FFFFu);
        Assert.Empty(fake.Unloads);
    }

    [Fact]
    public void ReconfigureRadii_UnchangedWindowDoesNotClearOrRebootstrap()
    {
        var state = new GpuWorldState();
        var fake = new FakeStreamer();
        int pendingClears = 0;
        var controller = new StreamingController(
            fake.EnqueueLoad,
            fake.EnqueueUnload,
            fake.DrainCompletions,
            (_, _) => { },
            state,
            nearRadius: 1,
            farRadius: 2,
            clearPendingLoads: () => pendingClears++);
        controller.Tick(50, 50);
        fake.Loads.Clear();
        fake.LoadJobs.Clear();

        controller.ReconfigureRadii(1, 2);

        Assert.Equal(0, pendingClears);
        Assert.Empty(fake.Loads);
        Assert.Empty(fake.Unloads);
    }

    [Fact]
    public void ReconfigureRadii_ClearFailureRetainsOldWindowAndResumesWithoutDuplicateLoads()
    {
        var state = new GpuWorldState();
        var fake = new FakeStreamer();
        int clearAttempts = 0;
        var controller = new StreamingController(
            fake.EnqueueLoad,
            fake.EnqueueUnload,
            fake.DrainCompletions,
            (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0,
            clearPendingLoads: () =>
            {
                clearAttempts++;
                if (clearAttempts == 1)
                    throw new InvalidOperationException("clear not admitted");
            });
        controller.Tick(50, 50);
        AddPublished(state, 50, 50, LandblockStreamTier.Near);
        fake.LoadJobs.Clear();

        Assert.Throws<AggregateException>(() => controller.ReconfigureRadii(1, 1));
        Assert.Equal(0, controller.NearRadius);
        Assert.Equal(0, controller.FarRadius);
        Assert.Empty(fake.LoadJobs);

        controller.ReconfigureRadii(1, 1);

        Assert.Equal(2, clearAttempts);
        Assert.Equal(8, fake.LoadJobs.Count);
        Assert.Equal(8, fake.LoadJobs.Select(static job => job.Id).Distinct().Count());
        Assert.Equal(1, controller.NearRadius);
        Assert.Equal(1, controller.FarRadius);
    }

    [Fact]
    public void ReconfigureRadii_QueueFailureRetriesOnlyUncommittedAdmission()
    {
        var state = new GpuWorldState();
        var fake = new FakeStreamer();
        int loadAttempts = 0;
        bool failNext = false;
        void EnqueueLoad(uint id, LandblockStreamJobKind kind)
        {
            loadAttempts++;
            if (failNext)
            {
                failNext = false;
                throw new InvalidOperationException("load not admitted");
            }
            fake.EnqueueLoad(id, kind);
        }
        var controller = new StreamingController(
            EnqueueLoad,
            fake.EnqueueUnload,
            fake.DrainCompletions,
            (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0);
        controller.Tick(50, 50);
        AddPublished(state, 50, 50, LandblockStreamTier.Near);
        fake.LoadJobs.Clear();
        loadAttempts = 0;
        failNext = true;

        Assert.Throws<AggregateException>(() => controller.ReconfigureRadii(1, 1));
        Assert.Equal(8, loadAttempts);
        Assert.Equal(7, fake.LoadJobs.Count);
        Assert.Equal(0, controller.NearRadius);

        controller.ReconfigureRadii(1, 1);

        Assert.Equal(9, loadAttempts);
        Assert.Equal(8, fake.LoadJobs.Count);
        Assert.Equal(8, fake.LoadJobs.Select(static job => job.Id).Distinct().Count());
        Assert.Equal(1, controller.NearRadius);
    }

    [Fact]
    public void ReconfigureRadii_CommittedQueueFailureIsReportedButNeverReplayed()
    {
        var state = new GpuWorldState();
        var fake = new FakeStreamer();
        bool throwCommitted = false;
        int loadAttempts = 0;
        void EnqueueLoad(uint id, LandblockStreamJobKind kind)
        {
            loadAttempts++;
            fake.EnqueueLoad(id, kind);
            if (throwCommitted)
            {
                throwCommitted = false;
                throw new StreamingMutationException(
                    "post-admission diagnostic failed",
                    mutationCommitted: true);
            }
        }
        var controller = new StreamingController(
            EnqueueLoad,
            fake.EnqueueUnload,
            fake.DrainCompletions,
            (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0);
        controller.Tick(50, 50);
        AddPublished(state, 50, 50, LandblockStreamTier.Near);
        fake.LoadJobs.Clear();
        loadAttempts = 0;
        throwCommitted = true;

        Assert.Throws<AggregateException>(() => controller.ReconfigureRadii(1, 1));

        Assert.Equal(8, loadAttempts);
        Assert.Equal(8, fake.LoadJobs.Count);
        Assert.Equal(1, controller.NearRadius);
        controller.ReconfigureRadii(1, 1);
        Assert.Equal(8, loadAttempts);
    }

    [Fact]
    public void ReconfigureRadii_ClearCallbackReentryDefersWithoutRecursiveClear()
    {
        var state = new GpuWorldState();
        var fake = new FakeStreamer();
        StreamingController? controller = null;
        int clearCalls = 0;
        controller = new StreamingController(
            fake.EnqueueLoad,
            fake.EnqueueUnload,
            fake.DrainCompletions,
            (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0,
            clearPendingLoads: () =>
            {
                clearCalls++;
                controller!.ReconfigureRadii(1, 1);
            });
        controller.Tick(50, 50);
        AddPublished(state, 50, 50, LandblockStreamTier.Near);
        fake.LoadJobs.Clear();

        controller.ReconfigureRadii(1, 1);

        Assert.Equal(1, clearCalls);
        Assert.Equal(8, fake.LoadJobs.Count);
        Assert.Equal(1, controller.NearRadius);
    }

    [Fact]
    public void ReconfigureRadii_QueueCallbackReentryDefersWithoutDuplicateAdmission()
    {
        var state = new GpuWorldState();
        var fake = new FakeStreamer();
        StreamingController? controller = null;
        bool reenter = false;
        int loadCalls = 0;
        void EnqueueLoad(uint id, LandblockStreamJobKind kind)
        {
            loadCalls++;
            fake.EnqueueLoad(id, kind);
            if (reenter)
            {
                reenter = false;
                controller!.ReconfigureRadii(1, 1);
            }
        }
        controller = new StreamingController(
            EnqueueLoad,
            fake.EnqueueUnload,
            fake.DrainCompletions,
            (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0);
        controller.Tick(50, 50);
        AddPublished(state, 50, 50, LandblockStreamTier.Near);
        fake.LoadJobs.Clear();
        loadCalls = 0;
        reenter = true;

        controller.ReconfigureRadii(1, 1);

        Assert.Equal(8, loadCalls);
        Assert.Equal(8, fake.LoadJobs.Count);
        Assert.Equal(8, fake.LoadJobs.Select(static job => job.Id).Distinct().Count());
        Assert.Equal(1, controller.NearRadius);
    }

    [Fact]
    public void DrainingLoadedResult_AddsToState()
    {
        var state = new GpuWorldState();
        var fake = new FakeStreamer();
        var applied = new List<LoadedLandblock>();
        var controller = new StreamingController(
            fake.EnqueueLoad, fake.EnqueueUnload, fake.DrainCompletions,
            (build, _) => applied.Add(build.Landblock), state, nearRadius: 2, farRadius: 2);

        // Note: LoadedLandblock's actual fields are LandblockId, Heightmap,
        // Entities (positional record). Adjust if the first positional arg
        // name differs.
        const uint landblockId = 0x3232FFFFu;
        var lb = new LoadedLandblock(landblockId, new LandBlock(), System.Array.Empty<WorldEntity>());
        var stubMesh = new AcDream.Core.Terrain.LandblockMeshData(
            System.Array.Empty<AcDream.Core.Terrain.TerrainVertex>(),
            System.Array.Empty<uint>());
        fake.Pending.Enqueue(new LandblockStreamResult.Loaded(landblockId, LandblockStreamTier.Near, lb, stubMesh));

        for (int frame = 0; frame < 32 && !state.IsLoaded(landblockId); frame++)
            controller.Tick(50, 50);

        Assert.Single(applied);
        Assert.True(state.IsLoaded(landblockId));
    }

    [Fact]
    public void DrainingUnloadedResult_RemovesUnownedLandblockFromState()
    {
        var state = new GpuWorldState();
        var fake = new FakeStreamer();
        var controller = new StreamingController(
            fake.EnqueueLoad, fake.EnqueueUnload, fake.DrainCompletions,
            (_, _) => { }, state, nearRadius: 2, farRadius: 2);

        const uint landblockId = 0x2020FFFFu;
        var lb = new LoadedLandblock(landblockId, new LandBlock(), System.Array.Empty<WorldEntity>());
        state.AddLandblock(lb);
        fake.Pending.Enqueue(new LandblockStreamResult.Unloaded(landblockId));

        controller.Tick(50, 50);

        Assert.False(state.IsLoaded(landblockId));
    }

    [Fact]
    public void DrainingUnloadedResult_CommitsStateBeforePresentationTeardown()
    {
        var state = new GpuWorldState();
        var fake = new FakeStreamer();
        const uint landblockId = 0x2020FFFFu;
        var lb = new LoadedLandblock(
            landblockId,
            new LandBlock(),
            new[]
            {
                new WorldEntity
                {
                    Id = 1,
                    SourceGfxObjOrSetupId = 0x01000000u,
                    Position = System.Numerics.Vector3.Zero,
                    Rotation = System.Numerics.Quaternion.Identity,
                    MeshRefs = Array.Empty<MeshRef>(),
                },
            });
        state.AddLandblock(lb);
        bool callbackSawDetachedState = false;
        var controller = new StreamingController(
            fake.EnqueueLoad,
            fake.EnqueueUnload,
            fake.DrainCompletions,
            (_, _) => { },
            state,
            nearRadius: 2,
            farRadius: 2,
            removeTerrain: id =>
            {
                callbackSawDetachedState = id == landblockId
                    && !state.TryGetLandblock(id, out _);
            });
        fake.Pending.Enqueue(new LandblockStreamResult.Unloaded(landblockId));

        controller.Tick(50, 50);

        Assert.True(callbackSawDetachedState);
        Assert.False(state.IsLoaded(landblockId));
    }

    private static void AddPublished(
        GpuWorldState state,
        int x,
        int y,
        LandblockStreamTier tier)
    {
        uint id = ((uint)x << 24) | ((uint)y << 16) | 0xFFFFu;
        state.AddLandblock(
            new LoadedLandblock(id, new LandBlock(), Array.Empty<WorldEntity>()),
            tier: tier);
    }
}
