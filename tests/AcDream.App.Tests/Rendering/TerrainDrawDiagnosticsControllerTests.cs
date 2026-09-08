using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class TerrainDrawDiagnosticsControllerTests
{
    [Fact]
    public void Disabled_controller_still_closes_the_terrain_sample_without_publication()
    {
        var log = new RecordingLog();
        var facts = new RecordingFacts();
        var world = new WorldRenderDiagnostics(log);
        var controller = new TerrainDrawDiagnosticsController(false, world, facts, log);

        controller.Begin();
        controller.Complete(10_000);

        Assert.Equal(0, facts.TerrainCaptureCount);
        Assert.Equal(0, facts.FrameCaptureCount);
        Assert.Empty(log.Messages);
    }

    [Fact]
    public void Publication_failure_does_not_commit_the_shared_cadence()
    {
        var log = new ThrowOnceLog();
        var facts = new RecordingFacts();
        var world = new WorldRenderDiagnostics(log);
        var controller = new TerrainDrawDiagnosticsController(true, world, facts, log);

        controller.Begin();
        Assert.Throws<InvalidOperationException>(() => controller.Complete(10_000));

        controller.Begin();
        controller.Complete(10_001);

        Assert.Equal(2, facts.TerrainCaptureCount);
        Assert.Equal(1, facts.FrameCaptureCount);
        Assert.Collection(
            log.Messages,
            terrain => Assert.StartsWith("[TERRAIN-DIAG]", terrain),
            frame => Assert.StartsWith("[FRAME-DIAG]", frame));
    }

    [Fact]
    public void Frame_log_failure_retries_both_shared_publications()
    {
        var log = new ThrowOnFirstFrameLog();
        var facts = new RecordingFacts();
        var world = new WorldRenderDiagnostics(log);
        var controller = new TerrainDrawDiagnosticsController(true, world, facts, log);

        controller.Begin();
        Assert.Throws<InvalidOperationException>(() => controller.Complete(10_000));

        controller.Begin();
        controller.Complete(10_001);

        Assert.Equal(2, facts.TerrainCaptureCount);
        Assert.Equal(2, facts.FrameCaptureCount);
        Assert.Collection(
            log.Messages,
            firstTerrain => Assert.StartsWith("[TERRAIN-DIAG]", firstTerrain),
            retryTerrain => Assert.StartsWith("[TERRAIN-DIAG]", retryTerrain),
            frame => Assert.StartsWith("[FRAME-DIAG]", frame));
    }

    [Fact]
    public void Successful_publication_waits_for_the_next_interval()
    {
        var log = new RecordingLog();
        var facts = new RecordingFacts();
        var world = new WorldRenderDiagnostics(log);
        var controller = new TerrainDrawDiagnosticsController(true, world, facts, log);

        controller.Begin();
        controller.Complete(10_000);
        controller.Begin();
        controller.Complete(14_999);
        controller.Begin();
        controller.Complete(15_000);
        controller.Begin();
        controller.Complete(15_001);

        Assert.Equal(2, facts.TerrainCaptureCount);
        Assert.Equal(2, facts.FrameCaptureCount);
        Assert.Equal(4, log.Messages.Count);
    }


    [Fact]
    public void CompleteWalkFrame_PublishesOnTheSameCadenceAsComplete()
    {
        var log = new RecordingLog();
        var facts = new RecordingFacts();
        var world = new WorldRenderDiagnostics(log);
        var controller = new TerrainDrawDiagnosticsController(true, world, facts, log);

        controller.AccumulateWalkBatch(1_000);
        controller.AccumulateWalkBatch(2_000);
        controller.CompleteWalkFrame(10_000);

        Assert.Equal(1, facts.TerrainCaptureCount);
        Assert.Equal(1, facts.FrameCaptureCount);
        Assert.Equal(2, log.Messages.Count);
        Assert.StartsWith("[TERRAIN-DIAG]", log.Messages[0]);
    }

    [Fact]
    public void CompleteWalkFrame_WithNoAccumulatedBatchesStillPublishesOnCadence()
    {
        var log = new RecordingLog();
        var facts = new RecordingFacts();
        var world = new WorldRenderDiagnostics(log);
        var controller = new TerrainDrawDiagnosticsController(true, world, facts, log);

        controller.CompleteWalkFrame(10_000);

        Assert.Equal(1, facts.TerrainCaptureCount);
        Assert.Equal(2, log.Messages.Count);
    }

    [Fact]
    public void CompleteWalkFrame_ResetsTheAccumulatorAcrossFrames()
    {
        var log = new RecordingLog();
        var facts = new RecordingFacts();
        var world = new WorldRenderDiagnostics(log);
        var controller = new TerrainDrawDiagnosticsController(true, world, facts, log);

        controller.AccumulateWalkBatch(5_000);
        controller.CompleteWalkFrame(10_000);
        controller.CompleteWalkFrame(15_001);

        Assert.Equal(2, facts.TerrainCaptureCount);
        Assert.Equal(4, log.Messages.Count);
    }

    private sealed class RecordingFacts : IFramePipelineDiagnosticFactsSource
    {
        public int TerrainCaptureCount { get; private set; }
        public int FrameCaptureCount { get; private set; }

        public TerrainRenderDiagnosticFacts CaptureTerrain()
        {
            TerrainCaptureCount++;
            return new TerrainRenderDiagnosticFacts(VisibleSlots: 1, Draws: 1, LoadedSlots: 2, CapacitySlots: 3);
        }

        public FramePipelineDiagnosticFacts CaptureFrame()
        {
            FrameCaptureCount++;
            return default;
        }
    }

    private sealed class RecordingLog : IRenderFrameDiagnosticLog
    {
        public List<string> Messages { get; } = [];
        public void WriteLine(string message) => Messages.Add(message);
    }

    private sealed class ThrowOnceLog : IRenderFrameDiagnosticLog
    {
        private bool _throw = true;
        public List<string> Messages { get; } = [];

        public void WriteLine(string message)
        {
            if (_throw)
            {
                _throw = false;
                throw new InvalidOperationException("diagnostic failure");
            }

            Messages.Add(message);
        }
    }

    private sealed class ThrowOnFirstFrameLog : IRenderFrameDiagnosticLog
    {
        private bool _throw = true;
        public List<string> Messages { get; } = [];

        public void WriteLine(string message)
        {
            if (_throw && message.StartsWith("[FRAME-DIAG]", StringComparison.Ordinal))
            {
                _throw = false;
                throw new InvalidOperationException("frame diagnostic failure");
            }

            Messages.Add(message);
        }
    }
}
