using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class RollingTimingSampleWindowTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveCapacity(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RollingTimingSampleWindow(capacity));
    }

    [Fact]
    public void Snapshot_EmptyOrNonPositiveSamplesReturnsZeroes()
    {
        var samples = new RollingTimingSampleWindow(4);
        samples.PushHundredthsMicroseconds(0);
        samples.PushHundredthsMicroseconds(-10);

        Assert.Equal(default, samples.Snapshot());
    }

    [Fact]
    public void Snapshot_UsesTheAcceptedUpperMedianAndP95Rules()
    {
        var samples = new RollingTimingSampleWindow(8);
        samples.PushHundredthsMicroseconds(100);
        samples.PushHundredthsMicroseconds(200);

        RollingTimingPercentiles snapshot = samples.Snapshot();

        Assert.Equal(200, snapshot.MedianHundredthsMicroseconds);
        Assert.Equal(200, snapshot.Percentile95HundredthsMicroseconds);
    }

    [Fact]
    public void Push_WrapsAndRetainsOnlyTheNewestCapacitySamples()
    {
        var samples = new RollingTimingSampleWindow(3);
        samples.PushHundredthsMicroseconds(100);
        samples.PushHundredthsMicroseconds(200);
        samples.PushHundredthsMicroseconds(300);
        samples.PushHundredthsMicroseconds(400);

        RollingTimingPercentiles snapshot = samples.Snapshot();

        Assert.Equal(3, samples.Capacity);
        Assert.Equal(300, snapshot.MedianHundredthsMicroseconds);
        Assert.Equal(400, snapshot.Percentile95HundredthsMicroseconds);
    }

    [Fact]
    public void PushStopwatchTicks_UsesHundredthsOfAMicrosecondUnits()
    {
        var samples = new RollingTimingSampleWindow(1);

        samples.PushStopwatchTicks(System.Diagnostics.Stopwatch.Frequency);

        Assert.Equal(
            new RollingTimingPercentiles(100_000_000, 100_000_000),
            samples.Snapshot());
    }
}
