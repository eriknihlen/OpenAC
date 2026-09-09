using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;

namespace AcDream.App.Tests.Rendering;

public sealed class ResourceCleanupGroupTests
{
    [Fact]
    public void CleanupRunsInReverseOrderAndNeverReplaysSuccess()
    {
        var calls = new List<string>();
        var resources = new ResourceCleanupGroup();
        int middleFailures = 1;
        resources.Add("first", () => calls.Add("first"));
        resources.Add("middle", () =>
        {
            calls.Add("middle");
            if (middleFailures-- > 0)
                throw new InvalidOperationException("middle failed");
        });
        resources.Add("last", () => calls.Add("last"));

        Assert.Throws<AggregateException>(resources.RetryCleanup);
        Assert.Equal(["last", "middle", "first"], calls);

        resources.RetryCleanup();
        resources.RetryCleanup();

        Assert.True(resources.IsCleanupComplete);
        Assert.Equal(["last", "middle", "first", "middle"], calls);
    }

    [Fact]
    public void TransferAllLeavesPublishedResourcesUntouched()
    {
        int releaseCalls = 0;
        var resources = new ResourceCleanupGroup();
        resources.Add("published", () => releaseCalls++);

        resources.TransferAll();
        resources.RetryCleanup();

        Assert.True(resources.IsCleanupComplete);
        Assert.Equal(0, releaseCalls);
        Assert.Throws<InvalidOperationException>(() =>
            resources.Add("late", () => { }));
    }

    [Fact]
    public void FailedConstructionRollbackRetainsOnlyUnreleasedOwnership()
    {
        int releases = 0;
        bool releaseFails = true;
        var resources = new ResourceCleanupGroup();
        resources.Add("retryable", () =>
        {
            releases++;
            if (releaseFails)
                throw new InvalidOperationException("release failed");
        });

        ResourceConstructionException failure =
            Assert.Throws<ResourceConstructionException>(() =>
                resources.RollbackConstructionAndThrow(
                    "construction failed",
                    new InvalidOperationException("original failure")));

        Assert.False(failure.IsCleanupComplete);
        Assert.Equal(1, releases);
        releaseFails = false;
        failure.RetryCleanup();
        failure.RetryCleanup();

        Assert.True(failure.IsCleanupComplete);
        Assert.Equal(2, releases);
    }

    [Fact]
    public void TextRendererConstructionCreatesAndDisposesOnlyOnePipeline()
    {
        using var device = new RecordingGpuDevice();
        int buffersBefore = device.CreatedBuffers.Count;
        int pipelinesBefore = device.CreatedPipelines.Count;
        int samplersBefore = device.CreatedSamplers.Count;
        int texturesBefore = device.CreatedTextures.Count;
        int slotsBefore = device.LiveTextureSlotCount;

        var renderer = new TextRenderer(
            device,
            new NullGpuFrameSource(),
            shaderDir: "unused");

        RecordingGpuPipeline pipeline = Assert.Single(
            device.CreatedPipelines.Skip(pipelinesBefore));
        Assert.Equal(buffersBefore, device.CreatedBuffers.Count);
        Assert.Equal(samplersBefore, device.CreatedSamplers.Count);
        Assert.Equal(texturesBefore, device.CreatedTextures.Count);
        Assert.Equal(slotsBefore, device.LiveTextureSlotCount);

        renderer.Dispose();

        Assert.True(pipeline.IsDisposed);
    }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }
}
