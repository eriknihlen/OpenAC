using Xunit;

namespace AcDream.App.Tests;

public class ZeroAllocationProbeTests
{
    [Fact]
    public void GenuinelyAllocatingStep_IsReportedAboveZero()
    {
        object? sink = null;
        long allocated = ZeroAllocationProbe.MeasureWarmed(
            () => sink = new byte[1024]);

        Assert.NotNull(sink);
        Assert.True(
            allocated >= 1024,
            $"Expected at least the 1 KiB the step allocates, measured {allocated:N0}.");
    }

    [Fact]
    public void GenuinelyAllocatingStep_FailsTheAssertion()
    {
        object? sink = null;

        var failure = Assert.Throws<Xunit.Sdk.TrueException>(
            () => ZeroAllocationProbe.AssertAllocatesNothing(
                "deliberately allocating step",
                () => sink = new byte[1024]));

        Assert.Contains("deliberately allocating step", failure.Message);
        Assert.Contains("expected 0", failure.Message);
    }

    [Fact]
    public void NonAllocatingStep_IsReportedAsZero()
    {
        int counter = 0;
        long allocated = ZeroAllocationProbe.MeasureWarmed(() => counter++);

        Assert.True(
            counter
                > ZeroAllocationProbe.DefaultBatchSize
                    * ZeroAllocationProbe.DefaultWarmupBatches);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void OneTimeCost_IsExcluded()
    {
        int invocations = 0;
        object? sink = null;

        long allocated = ZeroAllocationProbe.MeasureWarmed(() =>
        {
            if (invocations++ == 0)
                sink = new byte[4096];
        });

        Assert.NotNull(sink);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void PeriodicCost_ShorterThanTheBatch_IsNotExcluded()
    {
        int invocations = 0;
        object? sink = null;

        long allocated = ZeroAllocationProbe.MeasureWarmed(() =>
        {
            if (invocations++ % 10 == 0)
                sink = new byte[4096];
        });

        Assert.NotNull(sink);
        Assert.True(
            allocated > 0,
            "A cost recurring every tenth invocation is steady-state and must "
            + $"not read as zero; measured {allocated:N0}.");
    }

    [Fact]
    public void PeriodicCost_IsCaughtWheneverTheBatchCoversThePeriod()
    {
        Assert.True(MeasurePeriodicCost(period: 8, batchSize: 16) > 0);
        Assert.Equal(0, MeasurePeriodicCost(period: 8, batchSize: 4));

        static long MeasurePeriodicCost(int period, int batchSize)
        {
            int invocations = 0;
            object? sink = null;

            long allocated = ZeroAllocationProbe.MeasureWarmed(
                () =>
                {
                    if (invocations++ % period == period - 1)
                        sink = new byte[4096];
                },
                batchSize: batchSize);

            Assert.NotNull(sink);
            return allocated;
        }
    }
}
