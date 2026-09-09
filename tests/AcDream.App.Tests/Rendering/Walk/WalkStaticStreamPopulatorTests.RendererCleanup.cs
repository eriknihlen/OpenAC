using System.Reflection;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Walk;
using AcDream.Core.Physics;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed partial class WalkStaticStreamPopulatorTests
{
    [Fact]
    public void ProductionRendererCleanupAndDriverRebindPreserveRetainedGeometry()
    {
        using var fx = new DispatcherFixture();
        InstallRetainedMesh(fx);
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1));
        var leaf = new WalkFrameDriverTests.RecordingLeafRenderer(new List<string>());
        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, world);
        var cache = (FarLandscapeDrawCache)typeof(WalkFrameDriver)
            .GetField("_farDrawCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(driver)!;

        using var scene = new RenderSceneShadowRuntime(RenderSceneGeneration.FromRaw(1));
        var renderer = new RetailPViewRenderer(scene, new WalkBuildingRegistry(),
            new WalkLandscapeAssembler(), new CellVisibility(), new ShadowObjectRegistry());
        typeof(RetailPViewRenderer)
            .GetField("_walkFrameDriverScratch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(renderer, driver);
        MethodInfo cleanup = typeof(RetailPViewRenderer).GetMethod(
            "ClearWalkFrameBindings", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var first = AppendRetainedFrame(fx, cache);
        Assert.Single(first.Keys);
        int reads = world.Reads;
        int rebuilds = cache.RebuildCount;
        cleanup.Invoke(renderer, null);
        Assert.Equal(1, cache.EntryCount);
        driver.RebindFrame(leaf, clipFrame: null);
        var second = AppendRetainedFrame(fx, cache);
        Assert.Equal(first.Keys, second.Keys);
        Assert.Equal(first.Transforms, second.Transforms);
        Assert.Equal(reads, world.Reads);
        Assert.Equal(rebuilds, cache.RebuildCount);

        driver.AbortFrame();
        Assert.Equal(0, cache.EntryCount);
        Assert.Single(AppendRetainedFrame(fx, cache).Keys);
        Assert.Equal(rebuilds + 1, cache.RebuildCount);
    }

    [Fact]
    public void FailedRetainedRebuildRetriesAllCellsBeforePublishingGeometry()
    {
        using var fx = new DispatcherFixture();
        InstallRetainedMesh(fx);
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1));
        var secondRecord = RetainedRecord(2);
        world.Cells[RetainedCell + 1] = [secondRecord];
        world.Current[secondRecord.Source.LocalEntityId] = secondRecord;
        world.Revisions[RetainedCell + 1] = 1;
        world.ThrowOnceForCell = RetainedCell + 1;
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);

        Assert.Throws<InvalidOperationException>(() => AppendRetainedFrame(fx, cache));
        Assert.Equal(0, cache.RebuildCount);
        var recovered = AppendRetainedFrame(fx, cache);
        Assert.Equal(new[] { 1f, 2f }, recovered.Transforms.Select(matrix => matrix.M41));
        Assert.Equal(1, cache.RebuildCount);
        int reads = world.Reads;
        Assert.Equal(recovered.Keys, AppendRetainedFrame(fx, cache).Keys);
        Assert.Equal(reads, world.Reads);
    }
}
