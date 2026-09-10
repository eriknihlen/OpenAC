using System.Runtime.InteropServices;

namespace AcDream.App.Rendering;

/// <summary>
/// macOS has no <c>clock_nanosleep</c>, so the frame deadline is held with
/// mach's absolute-time timer. Mach units are not nanoseconds: on Apple
/// silicon the timebase is 125/3, so every duration is converted through
/// <see cref="MachTimebase"/> before it becomes a deadline.
/// </summary>
internal sealed partial class MacMonotonicFramePacingWaiter
    : IFramePacingWaiter,
      IDisposable
{
    private const int KernSuccess = 0;
    private const int KernAborted = 14;

    private readonly uint _timebaseNumerator;
    private readonly uint _timebaseDenominator;
    private bool _disposed;

    private MacMonotonicFramePacingWaiter(
        uint timebaseNumerator,
        uint timebaseDenominator)
    {
        _timebaseNumerator = timebaseNumerator;
        _timebaseDenominator = timebaseDenominator;
    }

    internal static MacMonotonicFramePacingWaiter Create()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "The monotonic mach frame waiter requires macOS.");
        }

        if (MachTimebaseInfo(out MachTimebase timebase) != KernSuccess
            || timebase.Numerator == 0
            || timebase.Denominator == 0)
        {
            throw new InvalidOperationException(
                "Could not read the mach clock timebase.");
        }

        return new MacMonotonicFramePacingWaiter(
            timebase.Numerator,
            timebase.Denominator);
    }

    public void Wait(long durationTicks, long clockFrequency)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (durationTicks <= 0 || clockFrequency <= 0)
            return;

        long durationNanoseconds =
            FramePacingDuration.ConvertTicksToNanoseconds(
                durationTicks,
                clockFrequency);
        ulong deadline = AddMachUnits(
            MachAbsoluteTime(),
            ConvertNanosecondsToMachUnits(
                durationNanoseconds,
                _timebaseNumerator,
                _timebaseDenominator));

        int result;
        do
        {
            result = MachWaitUntil(deadline);
        }
        while (result == KernAborted);

        if (result != KernSuccess)
        {
            throw new InvalidOperationException(
                "Waiting on the mach frame deadline failed with " +
                $"kern_return_t {result}.");
        }
    }

    internal static ulong ConvertNanosecondsToMachUnits(
        long nanoseconds,
        uint timebaseNumerator,
        uint timebaseDenominator)
    {
        ArgumentOutOfRangeException.ThrowIfZero(timebaseNumerator);
        ArgumentOutOfRangeException.ThrowIfZero(timebaseDenominator);
        if (nanoseconds <= 0)
            return 0UL;

        UInt128 machUnits =
            (UInt128)(ulong)nanoseconds
            * timebaseDenominator
            / timebaseNumerator;
        if (machUnits > ulong.MaxValue)
            return ulong.MaxValue;

        // A positive duration never rounds down to "do not wait": the shared
        // tick conversion makes the same guarantee one unit up the chain.
        return Math.Max(1UL, (ulong)machUnits);
    }

    private static ulong AddMachUnits(ulong timestamp, ulong delta) =>
        timestamp > ulong.MaxValue - delta
            ? ulong.MaxValue
            : timestamp + delta;

    public void Dispose() => _disposed = true;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct MachTimebase(
        uint Numerator,
        uint Denominator);

    [LibraryImport("libSystem.dylib", EntryPoint = "mach_timebase_info")]
    private static partial int MachTimebaseInfo(out MachTimebase timebase);

    [LibraryImport("libSystem.dylib", EntryPoint = "mach_absolute_time")]
    private static partial ulong MachAbsoluteTime();

    [LibraryImport("libSystem.dylib", EntryPoint = "mach_wait_until")]
    private static partial int MachWaitUntil(ulong deadline);
}
