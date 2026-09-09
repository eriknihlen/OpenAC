using System.Diagnostics;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Packs;
using AcDream.App.Tests.Architecture;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class AtmosphericCpuStageProfilerTests
{
    [Fact]
    public void LowProfilerSamplesOneFrameInFour()
    {
        long[] measured = Enumerable.Range(1, 12)
            .Where(value => AtmosphericCpuStageProfiler.ShouldMeasure(value))
            .Select(value => (long)value)
            .ToArray();

        Assert.Equal([4L, 8L, 12L], measured);
    }

    [Fact]
    public void SnapshotPublishesNonOverlappingStagesTotalAndResidual()
    {
        var profiler = new AtmosphericCpuStageProfiler(capacity: 4);
        var frame = new AtmosphericCpuStageFrame(
            FrameSerial: 4,
            ShadowCasterBuildTicks: Ticks(20),
            ShadowEnvironmentTicks: Ticks(30),
            ShadowPreparedDrawsAndTransformsTicks: Ticks(40),
            ShadowFitAndUniformTicks: Ticks(50),
            ShadowLayeredPassRecordingTicks: Ticks(60),
            ShadowBookkeepingTicks: Ticks(70),
            PostSetupAndOtherTicks: Ticks(80),
            PostSunRaysTicks: Ticks(90),
            PostFilmicTicks: Ticks(100));

        profiler.Observe(
            in frame,
            targetPreparationTicks: Ticks(10),
            measuredPackTotalTicks: Ticks(600),
            observeBookkeepingTicks: Ticks(110));

        IReadOnlyDictionary<string, RenderPackCpuStageDiagnostics> stages = profiler
            .Snapshot()
            .ToDictionary(value => value.Stage, StringComparer.Ordinal);
        Assert.Equal(13, stages.Count);
        Assert.Equal(1, stages["shadow-layered-pass-recording"].SampleCount);
        Assert.Equal(0.060, stages["shadow-layered-pass-recording"].CpuMillisecondsP50, 3);
        Assert.Equal(0.110, stages["performance-observe-bookkeeping"].CpuMillisecondsP50, 3);
        Assert.Equal(0.600, stages["measured-pack-total"].CpuMillisecondsP50, 3);
        Assert.Equal(0.050, stages["measured-pack-unattributed"].CpuMillisecondsP50, 3);
    }

    [Fact]
    public void WarmedObservationAllocatesNothing()
    {
        var profiler = new AtmosphericCpuStageProfiler(capacity: 2048);
        var frame = new AtmosphericCpuStageFrame(
            4,
            Ticks(1), Ticks(2), Ticks(3), Ticks(4), Ticks(5),
            Ticks(6), Ticks(7), Ticks(8), Ticks(9));

        ZeroAllocationProbe.AssertAllocatesNothing(
            "AtmosphericCpuStageProfiler.Observe",
            () => profiler.Observe(
                in frame,
                targetPreparationTicks: Ticks(10),
                measuredPackTotalTicks: Ticks(100),
                observeBookkeepingTicks: Ticks(11)));
    }

    [Fact]
    public void ProductionWorldPhaseCompletesTheSampledProfileAfterObservation()
    {
        var render = typeof(VulkanWorldScenePhase).GetMethod(nameof(VulkanWorldScenePhase.Render))!;
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(render);

        int observe = CompiledCallGraph.IndexOf(
            calls,
            typeof(RenderPackController),
            nameof(RenderPackController.ObserveActiveFrame));
        int complete = CompiledCallGraph.IndexOf(
            calls,
            typeof(IAtmosphericCpuStageProfileRuntime),
            nameof(IAtmosphericCpuStageProfileRuntime.CompleteCpuProfile));

        Assert.True(observe >= 0);
        Assert.True(complete > observe);
    }

    private static long Ticks(int microseconds) => checked((long)Math.Round(
        microseconds * Stopwatch.Frequency / 1_000_000d,
        MidpointRounding.AwayFromZero));
}
