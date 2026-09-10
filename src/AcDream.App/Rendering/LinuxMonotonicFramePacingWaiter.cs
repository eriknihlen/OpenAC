using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AcDream.App.Rendering;

internal sealed partial class LinuxMonotonicFramePacingWaiter
    : IFramePacingWaiter,
      IDisposable
{
    private const int ClockMonotonic = 1;
    private const int TimerAbsolute = 1;
    private const int Interrupted = 4;
    private const long NanosecondsPerSecond = 1_000_000_000L;
    private bool _disposed;

    private LinuxMonotonicFramePacingWaiter()
    {
    }

    internal static LinuxMonotonicFramePacingWaiter Create()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "The monotonic Linux frame waiter requires Linux.");
        }

        return new LinuxMonotonicFramePacingWaiter();
    }

    public void Wait(long durationTicks, long clockFrequency)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (durationTicks <= 0 || clockFrequency <= 0)
            return;

        int result = ClockGetTime(ClockMonotonic, out Timespec now);
        if (result != 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Could not read the Linux monotonic clock.");
        }

        long durationNanoseconds =
            FramePacingDuration.ConvertTicksToNanoseconds(
                durationTicks,
                clockFrequency);
        Timespec deadline = AddNanoseconds(now, durationNanoseconds);
        do
        {
            result = ClockNanosleep(
                ClockMonotonic,
                TimerAbsolute,
                in deadline,
                0);
        }
        while (result == Interrupted);

        if (result != 0)
        {
            throw new Win32Exception(
                result,
                "Waiting on the Linux monotonic frame deadline failed.");
        }
    }

    private static Timespec AddNanoseconds(
        Timespec timestamp,
        long nanoseconds)
    {
        long seconds = nanoseconds / NanosecondsPerSecond;
        long remainder = nanoseconds % NanosecondsPerSecond;
        long resultSeconds = timestamp.Seconds > long.MaxValue - seconds
            ? long.MaxValue
            : timestamp.Seconds + seconds;
        if (resultSeconds == long.MaxValue)
        {
            return new Timespec(
                long.MaxValue,
                NanosecondsPerSecond - 1L);
        }

        long resultNanoseconds = timestamp.Nanoseconds + remainder;
        if (resultNanoseconds >= NanosecondsPerSecond)
        {
            if (resultSeconds == long.MaxValue)
            {
                return new Timespec(
                    long.MaxValue,
                    NanosecondsPerSecond - 1L);
            }

            resultSeconds++;
            resultNanoseconds -= NanosecondsPerSecond;
        }

        return new Timespec(resultSeconds, resultNanoseconds);
    }

    public void Dispose() => _disposed = true;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Timespec(
        long Seconds,
        long Nanoseconds);

    [LibraryImport(
        "libc",
        EntryPoint = "clock_gettime",
        SetLastError = true)]
    private static partial int ClockGetTime(
        int clockId,
        out Timespec timestamp);

    [LibraryImport("libc", EntryPoint = "clock_nanosleep")]
    private static partial int ClockNanosleep(
        int clockId,
        int flags,
        in Timespec request,
        nint remainder);
}
