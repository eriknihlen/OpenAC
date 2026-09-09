using System.Diagnostics;
using AcDream.App.Diagnostics;

namespace AcDream.App.Rendering.Packs;

internal readonly record struct AtmosphericCpuStageFrame(
    long FrameSerial,
    long ShadowCasterBuildTicks,
    long ShadowEnvironmentTicks,
    long ShadowPreparedDrawsAndTransformsTicks,
    long ShadowFitAndUniformTicks,
    long ShadowLayeredPassRecordingTicks,
    long ShadowBookkeepingTicks,
    long PostSetupAndOtherTicks,
    long PostSunRaysTicks,
    long PostFilmicTicks);

internal readonly record struct RenderPackCpuStageDiagnostics(
    string Stage,
    int SampleCount,
    double CpuMillisecondsP50,
    double CpuMillisecondsP95,
    double CpuMillisecondsP99);

internal sealed class AtmosphericCpuStageProfiler
{
    internal const int SampleIntervalFrames = AtmosphericGpuTimerSampling.LowIntervalFrames;

    private static readonly string[] StageNames =
    [
        "target-preparation",
        "shadow-caster-build",
        "shadow-environment",
        "shadow-prepared-draws-and-transforms",
        "shadow-fit-and-uniform",
        "shadow-layered-pass-recording",
        "shadow-bookkeeping",
        "post-setup-and-other",
        "post-sun-rays",
        "post-filmic",
        "performance-observe-bookkeeping",
        "measured-pack-total",
        "measured-pack-unattributed",
    ];

    private readonly FrameStatsBuffer[] _microseconds;

    internal AtmosphericCpuStageProfiler(int capacity = RenderPackPerformanceWindow.DefaultCapacity)
    {
        _microseconds = new FrameStatsBuffer[StageNames.Length];
        for (int i = 0; i < _microseconds.Length; i++)
            _microseconds[i] = new FrameStatsBuffer(capacity);
    }

    internal static bool ShouldMeasure(long frameSerial)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameSerial);
        return frameSerial % SampleIntervalFrames == 0;
    }

    internal void Observe(
        in AtmosphericCpuStageFrame frame,
        long targetPreparationTicks,
        long measuredPackTotalTicks,
        long observeBookkeepingTicks)
    {
        if (frame.FrameSerial <= 0)
            throw new ArgumentOutOfRangeException(nameof(frame));
        ArgumentOutOfRangeException.ThrowIfNegative(targetPreparationTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(measuredPackTotalTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(observeBookkeepingTicks);

        long attributedTicks = checked(
            targetPreparationTicks
            + frame.ShadowCasterBuildTicks
            + frame.ShadowEnvironmentTicks
            + frame.ShadowPreparedDrawsAndTransformsTicks
            + frame.ShadowFitAndUniformTicks
            + frame.ShadowLayeredPassRecordingTicks
            + frame.ShadowBookkeepingTicks
            + frame.PostSetupAndOtherTicks
            + frame.PostSunRaysTicks
            + frame.PostFilmicTicks);
        long unattributedTicks = Math.Max(0L, measuredPackTotalTicks - attributedTicks);

        Push(0, targetPreparationTicks);
        Push(1, frame.ShadowCasterBuildTicks);
        Push(2, frame.ShadowEnvironmentTicks);
        Push(3, frame.ShadowPreparedDrawsAndTransformsTicks);
        Push(4, frame.ShadowFitAndUniformTicks);
        Push(5, frame.ShadowLayeredPassRecordingTicks);
        Push(6, frame.ShadowBookkeepingTicks);
        Push(7, frame.PostSetupAndOtherTicks);
        Push(8, frame.PostSunRaysTicks);
        Push(9, frame.PostFilmicTicks);
        Push(10, observeBookkeepingTicks);
        Push(11, measuredPackTotalTicks);
        Push(12, unattributedTicks);
    }

    internal IReadOnlyList<RenderPackCpuStageDiagnostics> Snapshot()
    {
        var result = new RenderPackCpuStageDiagnostics[StageNames.Length];
        for (int i = 0; i < result.Length; i++)
        {
            FrameStatsBuffer samples = _microseconds[i];
            result[i] = new RenderPackCpuStageDiagnostics(
                StageNames[i],
                samples.Count,
                samples.Percentile(0.50) / 1000d,
                samples.Percentile(0.95) / 1000d,
                samples.Percentile(0.99) / 1000d);
        }
        return result;
    }

    internal void Reset()
    {
        for (int i = 0; i < _microseconds.Length; i++)
            _microseconds[i].Reset();
    }

    private void Push(int stage, long ticks)
    {
        long microseconds = checked((long)Math.Round(
            ticks * 1_000_000d / Stopwatch.Frequency,
            MidpointRounding.AwayFromZero));
        _microseconds[stage].Push(microseconds);
    }
}

internal interface IAtmosphericCpuStageProfileRuntime
{
    bool ShouldProfileCpuFrame(long frameSerial);

    void CompleteCpuProfile(
        long frameSerial,
        long targetPreparationTicks,
        long measuredPackTotalTicks,
        long observeBookkeepingTicks,
        bool stableFrameBoundary);
}
