using Xunit;

namespace AcDream.App.Tests;

internal static class ZeroAllocationProbe
{
    internal const int DefaultBatchSize = 32;

    internal const int DefaultWarmupBatches = 4;

    internal const int DefaultSamples = 4;

    internal static long MeasureWarmed(
        Action step,
        int batchSize = DefaultBatchSize,
        int warmupBatches = DefaultWarmupBatches,
        int samples = DefaultSamples)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(warmupBatches);
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);

        for (int batch = 0; batch < warmupBatches; batch++)
            RunBatch(step, batchSize);

        long smallest = long.MaxValue;
        for (int sample = 0; sample < samples; sample++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            RunBatch(step, batchSize);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            if (allocated < smallest)
                smallest = allocated;

            // Nothing can go below the floor, so stop once it is reached.
            if (smallest == 0)
                break;
        }

        return smallest;
    }

    private static void RunBatch(Action step, int batchSize)
    {
        for (int invocation = 0; invocation < batchSize; invocation++)
            step();
    }

    /// <summary>
    /// Asserts that a warmed invocation of <paramref name="step"/> allocates no
    /// managed bytes at all. The bound is exact and deliberately has no slack.
    /// </summary>
    /// <param name="what">
    /// What the path is, for the failure message — e.g.
    /// "ArchRenderScene.Apply(updates)".
    /// </param>
    internal static void AssertAllocatesNothing(
        string what,
        Action step,
        int batchSize = DefaultBatchSize,
        int warmupBatches = DefaultWarmupBatches,
        int samples = DefaultSamples)
    {
        long allocated = MeasureWarmed(step, batchSize, warmupBatches, samples);

        Assert.True(
            allocated == 0,
            $"{what} allocated {allocated:N0} managed bytes per warmed batch of "
            + $"{batchSize} invocations, expected 0. Measured as the minimum of "
            + $"{samples} such batches after {warmupBatches} warmup batches, so "
            + "this is a steady-state cost rather than a one-time startup cost "
            + "(allocation accounting must remain enabled).");
    }
}
