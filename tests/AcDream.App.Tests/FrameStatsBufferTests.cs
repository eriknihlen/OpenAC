using AcDream.App.Diagnostics;
using Xunit;

namespace AcDream.App.Tests;

public class FrameStatsBufferTests
{
    [Fact]
    public void Percentiles_OnKnownDistribution_AreExact()
    {
        var buf = new FrameStatsBuffer(capacity: 100);
        // 1..100 µs — p50 = 50, p95 = 95, p99 = 99, max = 100.
        for (long i = 1; i <= 100; i++) buf.Push(i);

        Assert.Equal(50, buf.Percentile(0.50));
        Assert.Equal(95, buf.Percentile(0.95));
        Assert.Equal(99, buf.Percentile(0.99));
        Assert.Equal(100, buf.Max());
    }

    [Fact]
    public void Push_PastCapacity_KeepsOnlyNewestWindow()
    {
        var buf = new FrameStatsBuffer(capacity: 4);
        foreach (long v in new long[] { 1000, 1000, 1000, 1000, 1, 2, 3, 4 })
            buf.Push(v);
        // The four 1000s were overwritten; window is {1,2,3,4}.
        Assert.Equal(4, buf.Count);
        Assert.Equal(4, buf.Max());
        Assert.Equal(2, buf.Percentile(0.50));
    }

    [Fact]
    public void Percentile_Empty_ReturnsZero()
    {
        var buf = new FrameStatsBuffer(capacity: 8);
        Assert.Equal(0, buf.Percentile(0.95));
        Assert.Equal(0, buf.Max());
        Assert.Equal(0, buf.Count);
    }

    [Fact]
    public void Reset_ClearsWindow()
    {
        var buf = new FrameStatsBuffer(capacity: 8);
        buf.Push(5); buf.Push(7);
        buf.Reset();
        Assert.Equal(0, buf.Count);
        Assert.Equal(0, buf.Percentile(0.5));
    }

    [Fact]
    public void Max_AllNegative_ReturnsTrueMax()
    {
        var buf = new FrameStatsBuffer(capacity: 4);
        buf.Push(-5); buf.Push(-2); buf.Push(-9);
        Assert.Equal(-2, buf.Max());
    }
}
