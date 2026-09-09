using System.Numerics;
using System.Runtime.CompilerServices;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Walk;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed partial class WalkStaticStreamPopulatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedCells_ChangingOneEntityPreservesStaticGeometryWithoutWarmedAllocations(
        bool liveDynamic)
    {
        RenderProjectionClass projectionClass = liveDynamic
            ? RenderProjectionClass.LiveDynamicRoot : RenderProjectionClass.ActiveAnimatedStatic;
        using var fx = new DispatcherFixture();
        InstallRetainedMesh(fx);
        InstallRetainedMesh(fx, RetainedMesh + 1, 12);
        RenderProjectionRecord moving = RetainedRecord(9, RetainedMesh + 1) with
        {
            ProjectionClass = projectionClass,
        };
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1), moving, RetainedRecord(2));
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);
        var views = new RetainedViews();
        var stream = new OrderedDrawStream();
        var alpha = new List<WbDrawDispatcher.WalkClassifiedBatch>();
        for (int warmup = 0; warmup < 16; warmup++)
            _ = MeasureChangingRetainedReplayAllocations(
                fx.Dispatcher, cache, world, views, stream, alpha, ref moving, 0, 256);
        int reads = world.Reads;
        int classified = cache.EntityClassificationCount;
        long allocated = MeasureChangingRetainedReplayAllocations(
            fx.Dispatcher, cache, world, views, stream, alpha, ref moving, 256, 256);

        Assert.Equal(0, allocated);
        Assert.Equal(reads, world.Reads);
        Assert.Equal(1, cache.RebuildCount);
        Assert.Equal(256, cache.EntityClassificationCount - classified);
        Assert.Equal(new[] { 3u, 3u, 12u }, stream.Keys.Select(key => key.FirstIndex));
        Assert.Equal(new[] { 1f, 2f, 511f }, stream.Transforms.Select(transform => transform.M41));
        Assert.Equal(projectionClass == RenderProjectionClass.LiveDynamicRoot ? WalkDrawStage.Dynamic : WalkDrawStage.OutdoorStatic,
            stream.Stages[2]);
    }

    // Keep setup, disposal and assertion work outside the measured method's optimization boundary.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureChangingRetainedReplayAllocations(
        WbDrawDispatcher dispatcher,
        FarLandscapeDrawCache cache,
        RetainedWorld world,
        RetainedViews views,
        OrderedDrawStream stream,
        List<WbDrawDispatcher.WalkClassifiedBatch> alpha,
        ref RenderProjectionRecord moving,
        int firstFrame,
        int iterations)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = firstFrame; frame < firstFrame + iterations; frame++)
        {
            moving = moving with
            {
                PreviousTransform = new PreviousRenderTransform(moving.Transform.LocalToWorld),
                Transform = new RenderTransform(Matrix4x4.CreateTranslation(frame, 0, 0)),
                MeshSet = moving.MeshSet with { Revision = (ulong)frame },
            };
            world.Current[9] = moving;
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
    public void RetainedCells_ChangedGeometryRegroupsOnlyTheChangedEntity()
    {
        using var fx = new DispatcherFixture();
        InstallRetainedMesh(fx);
        InstallRetainedMesh(fx, RetainedMesh + 1, 12);
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1), RetainedRecord(2, RetainedMesh + 1), RetainedRecord(3));
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);
        Assert.Equal(new[] { 3u, 3u, 12u }, AppendRetainedFrame(fx, cache).Keys.Select(key => key.FirstIndex));
        world.Current[2] = RetainedRecord(2);
        var updated = AppendRetainedFrame(fx, cache);
        Assert.Equal(new[] { 3u, 3u, 3u }, updated.Keys.Select(key => key.FirstIndex));
        Assert.Equal(new[] { 1f, 2f, 3f }, updated.Transforms.Select(transform => transform.M41));
        Assert.Equal(4, cache.EntityClassificationCount);
        Assert.Equal(1, cache.RebuildCount);
    }
}
