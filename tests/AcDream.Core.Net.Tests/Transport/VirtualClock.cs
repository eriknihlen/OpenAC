namespace AcDream.Core.Net.Tests.Transport;

internal sealed class VirtualClock
{
    public const long TicksPerSecond = TimeSpan.TicksPerSecond;

    private long _timestamp;

    public VirtualClock(long startTimestamp = 0) => _timestamp = startTimestamp;

    /// <summary><c>Stopwatch.Frequency</c> equivalent.</summary>
    public long Frequency => TicksPerSecond;

    /// <summary><c>Stopwatch.GetTimestamp()</c> equivalent.</summary>
    public long GetTimestamp() => _timestamp;

    public double Seconds => (double)_timestamp / TicksPerSecond;

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delta),
                "the clock is monotonic — it cannot go backwards");
        }

        _timestamp += delta.Ticks;
    }
}
