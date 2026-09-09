namespace AcDream.Headless.Hosting;

internal static class HeadlessMonotonicTime
{
    internal static long Add(
        TimeProvider timeProvider,
        long timestamp,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        double ticks =
            duration.TotalSeconds * timeProvider.TimestampFrequency;
        if (!double.IsFinite(ticks) || ticks > long.MaxValue)
            return long.MaxValue;
        long delta = Math.Max(
            1L,
            checked((long)Math.Ceiling(ticks)));
        return timestamp > long.MaxValue - delta
            ? long.MaxValue
            : timestamp + delta;
    }
}
