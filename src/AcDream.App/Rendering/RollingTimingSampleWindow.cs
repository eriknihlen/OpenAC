namespace AcDream.App.Rendering;

internal readonly record struct RollingTimingPercentiles(
    long MedianHundredthsMicroseconds,
    long Percentile95HundredthsMicroseconds);

internal sealed class RollingTimingSampleWindow
{
    private readonly long[] _samples;
    private int _cursor;

    public RollingTimingSampleWindow(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _samples = new long[capacity];
    }

    public int Capacity => _samples.Length;

    public void PushHundredthsMicroseconds(long sample)
    {
        _samples[_cursor] = sample;
        _cursor = (_cursor + 1) % _samples.Length;
    }

    public void PushStopwatchTicks(long ticks) =>
        PushHundredthsMicroseconds(
            (long)(ticks * 100_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    public RollingTimingPercentiles Snapshot()
    {
        var copy = (long[])_samples.Clone();
        Array.Sort(copy);
        int populated = 0;
        foreach (long sample in copy)
        {
            if (sample > 0)
                populated++;
        }

        if (populated == 0)
            return default;

        long median = copy[copy.Length - 1 - (populated - 1) / 2];
        int p95Offset = (int)((populated - 1) * 0.05);
        long percentile95 = copy[copy.Length - 1 - p95Offset];
        return new RollingTimingPercentiles(median, percentile95);
    }
}
