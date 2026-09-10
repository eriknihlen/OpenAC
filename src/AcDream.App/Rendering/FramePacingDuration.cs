namespace AcDream.App.Rendering;

/// <summary>
/// Frame-deadline arithmetic shared by the platform waiters. The math is
/// the same everywhere; only the clock the deadline is handed to differs.
/// </summary>
internal static class FramePacingDuration
{
    private const long NanosecondsPerSecond = 1_000_000_000L;

    internal static long ConvertTicksToNanoseconds(
        long durationTicks,
        long clockFrequency)
    {
        if (durationTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationTicks));
        if (clockFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(clockFrequency));

        long wholeSeconds = Math.DivRem(
            durationTicks,
            clockFrequency,
            out long remainder);
        if (wholeSeconds >= long.MaxValue / NanosecondsPerSecond)
            return long.MaxValue;

        long wholeNanoseconds = wholeSeconds * NanosecondsPerSecond;
        long fractionalNanoseconds = checked((long)Math.Ceiling(
            remainder * (double)NanosecondsPerSecond / clockFrequency));
        if (wholeNanoseconds > long.MaxValue - fractionalNanoseconds)
            return long.MaxValue;

        return Math.Max(1L, wholeNanoseconds + fractionalNanoseconds);
    }
}
