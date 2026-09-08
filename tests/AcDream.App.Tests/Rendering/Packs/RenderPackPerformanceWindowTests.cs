using AcDream.App.Rendering.Packs;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class RenderPackPerformanceWindowTests
{
    [Fact]
    public void Snapshot_reports_independent_cpu_and_delayed_gpu_percentiles()
    {
        var window = new RenderPackPerformanceWindow(capacity: 8);
        window.Observe(1, 11, false, 0, 10, 2);
        window.Observe(2, 12, true, 4, 11, 3);
        window.Observe(3, 13, true, 6, 12, 4);
        window.Observe(4, 14, true, 8, 13, 5);

        RenderPackPerformanceSnapshot value = window.Snapshot();

        Assert.Equal(4, value.CpuSampleCount);
        Assert.Equal(4, value.AbsoluteReceiverCpuSampleCount);
        Assert.Equal(3, value.GpuSampleCount);
        Assert.Equal(2, value.IncrementalCpuMillisecondsP50);
        Assert.Equal(4, value.IncrementalCpuMillisecondsP95);
        Assert.Equal(4, value.IncrementalCpuMillisecondsP99);
        Assert.Equal(12, value.AbsoluteReceiverCpuMillisecondsP50);
        Assert.Equal(14, value.AbsoluteReceiverCpuMillisecondsP95);
        Assert.Equal(14, value.AbsoluteReceiverCpuMillisecondsP99);
        Assert.Equal(6, value.InclusiveGpuMillisecondsP50);
        Assert.Equal(8, value.InclusiveGpuMillisecondsP95);
        Assert.Equal(8, value.InclusiveGpuMillisecondsP99);
        Assert.Equal(13, value.ResidentGpuBytes);
        Assert.Equal(5, value.TransientGpuBytes);
        Assert.False(value.HasStableAutoWindow(4));
        Assert.True(value.HasStableAutoWindow(3));
    }

    [Fact]
    public void Capacity_is_a_rolling_window_and_reset_removes_mixed_quality_data()
    {
        var window = new RenderPackPerformanceWindow(capacity: 3);
        for (int i = 1; i <= 4; i++)
            window.Observe(i, i * 10, true, i * 2, i, i);

        RenderPackPerformanceSnapshot rolled = window.Snapshot();
        Assert.Equal(3, rolled.CpuSampleCount);
        Assert.Equal(3, rolled.GpuSampleCount);
        Assert.Equal(3, rolled.IncrementalCpuMillisecondsP50);
        Assert.Equal(4, rolled.IncrementalCpuMillisecondsP99);
        Assert.Equal(30, rolled.AbsoluteReceiverCpuMillisecondsP50);
        Assert.Equal(40, rolled.AbsoluteReceiverCpuMillisecondsP99);
        Assert.Equal(6, rolled.InclusiveGpuMillisecondsP50);
        Assert.Equal(8, rolled.InclusiveGpuMillisecondsP99);

        window.Reset();
        Assert.Equal(default, window.Snapshot());
    }

    [Theory]
    [InlineData(-1, 0, false, 0, 0, 0)]
    [InlineData(double.NaN, 0, false, 0, 0, 0)]
    [InlineData(0, -1, false, 0, 0, 0)]
    [InlineData(0, double.NaN, false, 0, 0, 0)]
    [InlineData(0, 0, true, -1, 0, 0)]
    [InlineData(0, 0, true, double.PositiveInfinity, 0, 0)]
    [InlineData(0, 0, false, 0, -1, 0)]
    [InlineData(0, 0, false, 0, 0, -1)]
    public void Invalid_measurements_are_rejected(
        double cpu,
        double receiverCpu,
        bool hasGpu,
        double gpu,
        long resident,
        long transient)
    {
        var window = new RenderPackPerformanceWindow();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            window.Observe(cpu, receiverCpu, hasGpu, gpu, resident, transient));
    }
}
