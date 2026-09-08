using AcDream.App.Diagnostics;

namespace AcDream.App.Rendering.Packs;

internal readonly record struct RenderPackPerformanceSnapshot(
    int CpuSampleCount,
    int AbsoluteReceiverCpuSampleCount,
    int GpuSampleCount,
    double IncrementalCpuMillisecondsP50,
    double IncrementalCpuMillisecondsP95,
    double IncrementalCpuMillisecondsP99,
    double AbsoluteReceiverCpuMillisecondsP50,
    double AbsoluteReceiverCpuMillisecondsP95,
    double AbsoluteReceiverCpuMillisecondsP99,
    double InclusiveGpuMillisecondsP50,
    double InclusiveGpuMillisecondsP95,
    double InclusiveGpuMillisecondsP99,
    long ResidentGpuBytes,
    long TransientGpuBytes)
{
    internal bool HasStableAutoWindow(int minimumSamples) =>
        minimumSamples > 0
        && CpuSampleCount >= minimumSamples
        && GpuSampleCount >= minimumSamples;
}

internal readonly record struct RenderPackRuntimePerformanceMetrics(
    long ResourceGeneration,
    bool HasResolvedGpuMeasurement,
    double InclusiveResolvedGpuMilliseconds,
    long RetainedGpuBytes,
    long TransientGpuBytes);

internal interface IRenderPackRuntimePerformanceSource
{
    RenderPackRuntimePerformanceMetrics CapturePerformanceMetrics();
}

internal readonly record struct RenderPackFramePerformanceObservation(
    double PackAddedCpuMilliseconds,
    bool StableFrameBoundary,
    int ViewportWidth,
    int ViewportHeight,
    int SampleCount,
    double AbsoluteEnhancedWorldReceiverCpuMilliseconds = 0d);

internal static class RenderPackPerformanceScopeNames
{
    internal const string EnhancedWorldReceiver = "atmospheric-world-receiver";
}

internal sealed class RenderPackPerformanceWindow
{
    internal const int DefaultCapacity = 2048;

    private readonly FrameStatsBuffer _cpuMicroseconds;
    private readonly FrameStatsBuffer _absoluteReceiverCpuMicroseconds;
    private readonly FrameStatsBuffer _gpuMicroseconds;
    private long _residentGpuBytes;
    private long _transientGpuBytes;

    internal RenderPackPerformanceWindow(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _cpuMicroseconds = new FrameStatsBuffer(capacity);
        _absoluteReceiverCpuMicroseconds = new FrameStatsBuffer(capacity);
        _gpuMicroseconds = new FrameStatsBuffer(capacity);
    }

    internal void Observe(
        double incrementalCpuMilliseconds,
        double absoluteReceiverCpuMilliseconds,
        bool hasResolvedGpuMeasurement,
        double inclusiveResolvedGpuMilliseconds,
        long residentGpuBytes,
        long transientGpuBytes)
    {
        if (!double.IsFinite(incrementalCpuMilliseconds) || incrementalCpuMilliseconds < 0d)
            throw new ArgumentOutOfRangeException(nameof(incrementalCpuMilliseconds));
        if (!double.IsFinite(absoluteReceiverCpuMilliseconds)
            || absoluteReceiverCpuMilliseconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(absoluteReceiverCpuMilliseconds));
        }
        if (hasResolvedGpuMeasurement
            && (!double.IsFinite(inclusiveResolvedGpuMilliseconds)
                || inclusiveResolvedGpuMilliseconds < 0d))
        {
            throw new ArgumentOutOfRangeException(nameof(inclusiveResolvedGpuMilliseconds));
        }
        if (residentGpuBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(residentGpuBytes));
        if (transientGpuBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(transientGpuBytes));

        _cpuMicroseconds.Push(ToMicroseconds(incrementalCpuMilliseconds));
        _absoluteReceiverCpuMicroseconds.Push(
            ToMicroseconds(absoluteReceiverCpuMilliseconds));
        if (hasResolvedGpuMeasurement)
            _gpuMicroseconds.Push(ToMicroseconds(inclusiveResolvedGpuMilliseconds));
        _residentGpuBytes = residentGpuBytes;
        _transientGpuBytes = transientGpuBytes;
    }

    internal RenderPackPerformanceSnapshot Snapshot() => new(
        _cpuMicroseconds.Count,
        _absoluteReceiverCpuMicroseconds.Count,
        _gpuMicroseconds.Count,
        ToMilliseconds(_cpuMicroseconds.Percentile(0.50)),
        ToMilliseconds(_cpuMicroseconds.Percentile(0.95)),
        ToMilliseconds(_cpuMicroseconds.Percentile(0.99)),
        ToMilliseconds(_absoluteReceiverCpuMicroseconds.Percentile(0.50)),
        ToMilliseconds(_absoluteReceiverCpuMicroseconds.Percentile(0.95)),
        ToMilliseconds(_absoluteReceiverCpuMicroseconds.Percentile(0.99)),
        ToMilliseconds(_gpuMicroseconds.Percentile(0.50)),
        ToMilliseconds(_gpuMicroseconds.Percentile(0.95)),
        ToMilliseconds(_gpuMicroseconds.Percentile(0.99)),
        _residentGpuBytes,
        _transientGpuBytes);

    internal int MinimumSampleCount => Math.Min(
        _cpuMicroseconds.Count,
        Math.Min(
            _absoluteReceiverCpuMicroseconds.Count,
            _gpuMicroseconds.Count));

    internal void Reset()
    {
        _cpuMicroseconds.Reset();
        _absoluteReceiverCpuMicroseconds.Reset();
        _gpuMicroseconds.Reset();
        _residentGpuBytes = 0;
        _transientGpuBytes = 0;
    }

    private static long ToMicroseconds(double milliseconds) =>
        checked((long)Math.Round(
            milliseconds * 1000d,
            MidpointRounding.AwayFromZero));

    private static double ToMilliseconds(long microseconds) =>
        microseconds / 1000d;
}
