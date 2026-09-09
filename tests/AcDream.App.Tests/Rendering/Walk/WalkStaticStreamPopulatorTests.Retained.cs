using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Selection;
using AcDream.App.Rendering.Walk;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Meshing;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed partial class WalkStaticStreamPopulatorTests
{
    private const uint RetainedBlock = 0x8C040000;
    private const uint RetainedCell = RetainedBlock | 1;
    private const uint RetainedMesh = 0x01000E01;

    private sealed class RetainedWorld : IWalkFrameWorldData
    {
        public (RenderSceneGeneration Generation, uint TupleLandblockId) RetainedContext { get; set; } = (RenderSceneGeneration.FromRaw(1), 0x8C04);
        public readonly Dictionary<uint, ulong> Revisions = new();
        public readonly Dictionary<uint, RenderProjectionRecord[]> Cells = new();
        public readonly Dictionary<uint, RenderProjectionRecord> Current = new();
        public bool SupportsRevisions = true;
        public bool IsComplete = true;
        public int Reads;
        public uint? ThrowOnceForCell;
        public ulong? GetOutdoorCellRenderRevision(uint cellId) => SupportsRevisions ? Revisions.GetValueOrDefault(cellId) : null;
        public bool TryGetCurrentProjection(uint id, out RenderProjectionRecord record) => Current.TryGetValue(id, out record);
        public void Set(params RenderProjectionRecord[] records)
        {
            Cells[RetainedCell] = records;
            Revisions[RetainedCell] = Revisions.GetValueOrDefault(RetainedCell) + 1;
            Current.Clear();
            foreach (var record in records) Current[record.Source.LocalEntityId] = record;
        }
        public WalkFrameStaticRecords GetOutdoorObjects(uint cell)
        {
            Reads++;
            if (ThrowOnceForCell == cell)
            {
                ThrowOnceForCell = null;
                throw new InvalidOperationException("Injected cell read failure.");
            }
            return new(new ArraySegment<RenderProjectionRecord>(Cells.GetValueOrDefault(cell) ?? []), RetainedContext.TupleLandblockId, IsComplete);
        }
        public WalkFrameStaticRecords GetCellObjects(uint cell) => GetOutdoorObjects(cell);
        public WalkFrameStaticRecords GetCellStatics(uint cell) => WalkFrameStaticRecords.Empty;
        public WalkFrameStaticRecords GetCellDynamics(uint cell) => WalkFrameStaticRecords.Empty;
        public WalkFrameStaticRecords GetOutdoorStatics(uint cell) => WalkFrameStaticRecords.Empty;
        public WalkFrameStaticRecords GetOutdoorDynamics(uint cell) => WalkFrameStaticRecords.Empty;
        public WalkFrameStaticRecords GetBuildingShellStatics(WalkBuilding building) => WalkFrameStaticRecords.Empty;
        public Matrix4x4 GetBuildingWorldTransform(WalkBuilding building) => Matrix4x4.Identity;
    }

    private sealed class RetainedViews : IWalkLookInViewSource
    {
        public bool Visible = true;
        public IReadOnlyList<uint> LookInCellTurns => Array.Empty<uint>();
        public bool SphereVisibleInLookInTurn(int routeIndex, in Vector3 center, float radius, bool testSphere = true) => Visible;
    }

    private sealed class RetainedLighting : IRetailSelectionLightingSource, IRetailSelectionRenderSink
    {
        public readonly RecordingSelectionSink Sink = new();
        public RetailSelectionLighting Value = new(0.1f, 0.9f);
        public void AddVisiblePart(uint serverGuid, uint localEntityId, int partIndex, uint gfxObjId, Matrix4x4 partWorld) =>
            Sink.AddVisiblePart(serverGuid, localEntityId, partIndex, gfxObjId, partWorld);
        public void TickLighting() { }
        public bool TryGetLighting(uint serverGuid, uint localEntityId, out RetailSelectionLighting lighting)
        {
            lighting = Value;
            return true;
        }
    }

    private static RenderProjectionRecord RetainedRecord(uint id, uint mesh = RetainedMesh) =>
        MakeRecord(id, id + 100, new Vector3(id, 0, 0), [new MeshRef(mesh, Matrix4x4.Identity)]);

    private static void InstallRetainedMesh(DispatcherFixture fx, uint mesh = RetainedMesh, uint firstIndex = 3) =>
        InjectRenderData(fx.Manager, mesh, MakeFlatMesh(MakeBatch(mesh, TranslucencyKind.Opaque, firstIndex, 0, 3, 1)));

    private static OrderedDrawStream AppendRetainedFrame(DispatcherFixture fx, FarLandscapeDrawCache cache, RetainedViews? views = null)
    {
        fx.Dispatcher.BeginWalkPartFrame();
        try
        {
            cache.BeginFrame();
            var stream = new OrderedDrawStream();
            Assert.True(cache.TryAppend(RetainedBlock, 4, 0, stream, views ?? new RetainedViews(), 0,
                Vector3.Zero, new List<WbDrawDispatcher.WalkClassifiedBatch>()));
            return stream;
        }
        finally { fx.Dispatcher.EndWalkPartFrame(); }
    }

    [Fact]
    public void RetainedCells_ReuseGroupedGeometryThenRebuildOnCellRevision()
    {
        using var fx = new DispatcherFixture();
        InstallRetainedMesh(fx);
        InstallRetainedMesh(fx, RetainedMesh + 1, 9);
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1), RetainedRecord(2, RetainedMesh + 1), RetainedRecord(3));
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);
        var first = AppendRetainedFrame(fx, cache);
        Assert.Equal(new[] { 3u, 3u, 9u }, first.Keys.Select(key => key.FirstIndex));
        Assert.All(first.AllowInstanceMerges, value => Assert.True(value));
        Assert.Equal(new[] { 1f, 3f, 2f }, first.Transforms.Select(transform => transform.M41));
        int reads = world.Reads;
        var warm = AppendRetainedFrame(fx, cache);
        Assert.Equal(first.Keys, warm.Keys);
        Assert.Equal(reads, world.Reads);
        Assert.Equal(1, cache.RebuildCount);
        world.Set(RetainedRecord(4, RetainedMesh + 1));
        var changed = AppendRetainedFrame(fx, cache);
        Assert.Equal(9u, Assert.Single(changed.Keys).FirstIndex);
        Assert.Equal(4f, Assert.Single(changed.Transforms).M41);
        Assert.Equal(2, cache.RebuildCount);
    }

    [Fact]
    public void RetainedCells_RemovalRecreationAndContextChangesDiscardOldGeometry()
    {
        using var fx = new DispatcherFixture();
        InstallRetainedMesh(fx);
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1));
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);
        AppendRetainedFrame(fx, cache);
        world.Cells.Clear(); world.Current.Clear(); world.Revisions.Clear();
        cache.BeginFrame();
        Assert.Equal(0, cache.EntryCount);
        world.Set(RetainedRecord(7));
        Assert.Equal(7f, Assert.Single(AppendRetainedFrame(fx, cache).Transforms).M41);
        world.RetainedContext = (RenderSceneGeneration.FromRaw(2), 0x8C04);
        cache.BeginFrame();
        Assert.Equal(0, cache.EntryCount);
        Assert.Single(AppendRetainedFrame(fx, cache).Keys);
        world.RetainedContext = (RenderSceneGeneration.FromRaw(2), 0x8C05);
        cache.BeginFrame();
        Assert.Equal(0, cache.EntryCount);
        Assert.Single(AppendRetainedFrame(fx, cache).Keys);
        Assert.Equal(4, cache.RebuildCount);
    }

    [Fact]
    public void RetainedCells_PendingMeshRetriesAndPublishedReplacementInvalidatesGeometry()
    {
        using var fx = new DispatcherFixture();
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1));
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);
        Assert.Empty(AppendRetainedFrame(fx, cache).Keys);
        InstallRetainedMesh(fx);
        Assert.Equal(3u, Assert.Single(AppendRetainedFrame(fx, cache).Keys).FirstIndex);
        InstallRetainedMesh(fx, firstIndex: 15);
        typeof(ObjectMeshManager).GetMethod("MarkRenderDataAvailabilityChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fx.Manager, null);
        Assert.Equal(15u, Assert.Single(AppendRetainedFrame(fx, cache).Keys).FirstIndex);
        Assert.Equal(3, cache.RebuildCount);
    }

    [Fact]
    public void RetainedCells_ReevaluateVisibilitySelectionAndPartDedupWithoutReclassification()
    {
        var lighting = new RetainedLighting();
        var sink = lighting.Sink;
        using var fx = new DispatcherFixture(selectionSink: lighting);
        InstallRetainedMesh(fx);
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1));
        world.Cells[RetainedCell + 1] = world.Cells[RetainedCell];
        world.Revisions[RetainedCell + 1] = 1;
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);
        var views = new RetainedViews { Visible = false };
        Assert.Empty(AppendRetainedFrame(fx, cache, views).Keys);
        Assert.Empty(sink.Calls);
        views.Visible = true;
        lighting.Value = new(0.6f, 0.4f);
        fx.Dispatcher.BeginWalkPartFrame();
        try
        {
            cache.BeginFrame();
            var stream = new OrderedDrawStream();
            var alpha = new List<WbDrawDispatcher.WalkClassifiedBatch>();
            Assert.True(cache.TryAppend(RetainedBlock, 4, 0, stream, views, 0, Vector3.Zero, alpha));
            Assert.True(cache.TryAppend(RetainedBlock, 4, 0, stream, views, 0, Vector3.Zero, alpha));
            Assert.Single(stream.Keys);
            Assert.Equal(new Vector2(0.6f, 0.4f), Assert.Single(stream.SelectionLighting));
            Assert.Equal(1u, Assert.Single(sink.Calls).LocalEntityId);
        }
        finally { fx.Dispatcher.EndWalkPartFrame(); }
        Assert.Equal(1, cache.RebuildCount);
        Assert.Single(AppendRetainedFrame(fx, cache, views).Keys);
        Assert.Equal(2, sink.Calls.Count);
    }

    [Fact]
    public void RetainedCells_UnsupportedRevisionSourceFallsBackWithoutConsumingRecords()
    {
        using var fx = new DispatcherFixture();
        var world = new RetainedWorld { SupportsRevisions = false };
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);
        cache.BeginFrame();
        Assert.False(cache.TryAppend(RetainedBlock, 4, 0, new OrderedDrawStream(), new RetainedViews(), 0,
            Vector3.Zero, new List<WbDrawDispatcher.WalkClassifiedBatch>()));
        Assert.Equal(0, world.Reads);
        Assert.Equal(0, cache.EntryCount);
    }

    [Fact]
    public void RetainedCells_TransparentDistanceOrderFollowsTheCameraOnWarmFrames()
    {
        using var fx = new DispatcherFixture();
        InjectRenderData(fx.Manager, RetainedMesh, MakeFlatMesh(
            MakeBatch(1, TranslucencyKind.AlphaBlend, 3, 0, 3, 1)));
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1), RetainedRecord(9));
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);
        foreach (int cameraX in new[] { 0, 10 })
        {
            fx.Dispatcher.BeginWalkPartFrame();
            try
            {
                cache.BeginFrame();
                var stream = new OrderedDrawStream();
                var alpha = new List<WbDrawDispatcher.WalkClassifiedBatch>();
                Assert.True(cache.TryAppend(RetainedBlock, 4, 0, stream, new RetainedViews(), 0,
                    new Vector3(cameraX, 0, 0), alpha));
                Assert.Empty(stream.Keys);
                Assert.Equal(cameraX == 0 ? new[] { 9f, 1f } : new[] { 1f, 9f },
                    alpha.Select(batch => batch.Transform.M41));
                Assert.Equal(new[] { 81f, 1f }, alpha.Select(batch => batch.SortDistanceSq));
            }
            finally { fx.Dispatcher.EndWalkPartFrame(); }
        }
        Assert.Equal(1, cache.RebuildCount);
    }

    [Fact]
    public void FullDetailPopulator_KeepsDistanceOrderAndDoesNotOptIntoMeshMerging()
    {
        using var fx = new DispatcherFixture();
        InstallRetainedMesh(fx);
        InstallRetainedMesh(fx, RetainedMesh + 1, 9);
        RenderProjectionRecord[] records = [RetainedRecord(1), RetainedRecord(2, RetainedMesh + 1), RetainedRecord(3)];
        var stream = new OrderedDrawStream();
        new WalkStaticStreamPopulator(fx.Dispatcher).PopulateCellObjects(
            stream, WalkDrawStage.OutdoorStatic, RetainedCell, records, 0x8C04,
            Vector3.Zero, Matrix4x4.Identity, null, -1, new List<WbDrawDispatcher.WalkClassifiedBatch>());
        Assert.Equal(new[] { 3f, 2f, 1f }, stream.Transforms.Select(transform => transform.M41));
        Assert.Equal(new[] { 3u, 9u, 3u }, stream.Keys.Select(key => key.FirstIndex));
        Assert.All(stream.AllowInstanceMerges, value => Assert.False(value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedCells_IncompleteProjectionRetriesWithoutARevisionChange(bool hasInitialRecord)
    {
        using var fx = new DispatcherFixture();
        InstallRetainedMesh(fx);
        var world = new RetainedWorld { IsComplete = false };
        world.Set(hasInitialRecord ? new[] { RetainedRecord(1) } : Array.Empty<RenderProjectionRecord>());
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);
        Assert.Equal(hasInitialRecord ? 1 : 0, AppendRetainedFrame(fx, cache).Count);
        ulong revision = world.Revisions[RetainedCell];
        RenderProjectionRecord added = RetainedRecord(8);
        world.Cells[RetainedCell] = hasInitialRecord ? [world.Current[1], added] : [added];
        world.Current[8] = added;
        world.IsComplete = true;

        var complete = AppendRetainedFrame(fx, cache);
        Assert.Equal(hasInitialRecord ? new[] { 1f, 8f } : new[] { 8f },
            complete.Transforms.Select(transform => transform.M41));
        Assert.Equal(revision, world.Revisions[RetainedCell]);
        Assert.Equal(2, cache.RebuildCount);
        int reads = world.Reads;
        Assert.Equal(complete.Transforms, AppendRetainedFrame(fx, cache).Transforms);
        Assert.Equal(reads, world.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedCells_WarmedUnchangedReplayDoesNotAllocate(bool transparent)
    {
        using var fx = new DispatcherFixture();
        InjectRenderData(fx.Manager, RetainedMesh, MakeFlatMesh(
            MakeBatch(1, transparent ? TranslucencyKind.AlphaBlend : TranslucencyKind.Opaque, 3, 0, 3, 1)));
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1), RetainedRecord(2), RetainedRecord(3));
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);
        var views = new RetainedViews();
        var stream = new OrderedDrawStream();
        var alpha = new List<WbDrawDispatcher.WalkClassifiedBatch>();
        for (int warmup = 0; warmup < 16; warmup++)
            _ = MeasureRetainedReplayAllocations(fx.Dispatcher, cache, views, stream, alpha, 256);
        int reads = world.Reads;
        long allocated = MeasureRetainedReplayAllocations(fx.Dispatcher, cache, views, stream, alpha, 256);
        Assert.Equal(0, allocated);
        Assert.Equal(reads, world.Reads);
        Assert.Equal(1, cache.RebuildCount);
        Assert.Equal(transparent ? 0 : 3, stream.Count);
        Assert.Equal(transparent ? 3 : 0, alpha.Count);
    }

    // Keep setup, disposal and assertion work outside the measured method's optimization boundary.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureRetainedReplayAllocations(
        WbDrawDispatcher dispatcher,
        FarLandscapeDrawCache cache,
        RetainedViews views,
        OrderedDrawStream stream,
        List<WbDrawDispatcher.WalkClassifiedBatch> alpha,
        int iterations)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            stream.Reset();
            alpha.Clear();
            dispatcher.BeginWalkPartFrame();
            cache.BeginFrame();
            cache.TryAppend(RetainedBlock, 4, 0, stream, views, 0, Vector3.Zero, alpha);
            dispatcher.EndWalkPartFrame();
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void RetainedCells_AlphaEndsKeepFineCellPartitionsDespiteOppositeDepthOrder()
    {
        using var fx = new DispatcherFixture();
        InjectRenderData(fx.Manager, RetainedMesh, MakeFlatMesh(
            MakeBatch(1, TranslucencyKind.AlphaBlend, 3, 0, 3, 1)));
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1), RetainedRecord(3));
        world.Cells[RetainedCell + 1] = [RetainedRecord(7), RetainedRecord(9)];
        world.Revisions[RetainedCell + 1] = 1;
        foreach (var record in world.Cells[RetainedCell + 1])
            world.Current[record.Source.LocalEntityId] = record;
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);
        foreach (int cameraX in new[] { 0, 10 })
        {
            fx.Dispatcher.BeginWalkPartFrame();
            try
            {
                cache.BeginFrame();
                var alpha = new List<WbDrawDispatcher.WalkClassifiedBatch> { default };
                Assert.True(cache.TryAppend(RetainedBlock, 4, 0, new OrderedDrawStream(), new RetainedViews(), 0,
                    new Vector3(cameraX, 0, 0), alpha));
                Assert.Equal(cameraX == 0 ? new[] { 3f, 1f, 9f, 7f } : new[] { 1f, 3f, 7f, 9f },
                    alpha.Skip(1).Select(batch => batch.Transform.M41));
                Assert.Equal(new[] { 3, 5, 5, 5 }, cache.AlphaEnds[..4].ToArray());
            }
            finally { fx.Dispatcher.EndWalkPartFrame(); }
        }
        Assert.Equal(1, cache.RebuildCount);
    }
}
