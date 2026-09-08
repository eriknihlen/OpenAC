using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AcDream.App.Rendering;

internal sealed partial class WindowsHighResolutionFramePacingWaiter :
    IFramePacingWaiter,
    IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerModifyState = 0x00000002;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const long HundredNanosecondsPerSecond = 10_000_000;

    private readonly SafeWaitHandle _timer;
    private bool _disposed;

    private WindowsHighResolutionFramePacingWaiter(SafeWaitHandle timer)
        => _timer = timer;

    public static WindowsHighResolutionFramePacingWaiter Create()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
        {
            throw new PlatformNotSupportedException(
                "acdream software frame pacing requires Windows 10 version 1803 or newer.");
        }

        SafeWaitHandle timer = CreateWaitableTimerExW(
            0,
            0,
            CreateWaitableTimerHighResolution,
            TimerModifyState | Synchronize);
        if (timer.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            timer.Dispose();
            throw new Win32Exception(error, "Could not create the high-resolution frame timer.");
        }

        return new WindowsHighResolutionFramePacingWaiter(timer);
    }

    public void Wait(long durationTicks, long clockFrequency)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (durationTicks <= 0 || clockFrequency <= 0)
            return;

        long hundredNanoseconds = ConvertTicksToHundredNanoseconds(
            durationTicks,
            clockFrequency);
        long relativeDueTime = -hundredNanoseconds;

        if (!SetWaitableTimerEx(
                _timer,
                in relativeDueTime,
                periodMilliseconds: 0,
                completionRoutine: 0,
                completionArgument: 0,
                wakeContext: 0,
                tolerableDelayMilliseconds: 0))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Could not arm the high-resolution frame timer.");
        }

        uint result = WaitForSingleObject(
            _timer,
            ComputeFailureTimeoutMilliseconds(hundredNanoseconds));
        if (result == WaitObject0)
            return;
        if (result == WaitTimeout)
        {
            throw new TimeoutException(
                "The high-resolution frame timer did not signal before its safety timeout.");
        }

        int waitError = result == WaitFailed
            ? Marshal.GetLastPInvokeError()
            : unchecked((int)result);
        throw new Win32Exception(waitError, "Waiting on the high-resolution frame timer failed.");
    }

    internal static long ConvertTicksToHundredNanoseconds(
        long durationTicks,
        long clockFrequency)
    {
        if (durationTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationTicks));
        if (clockFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(clockFrequency));

        long wholeSeconds = Math.DivRem(durationTicks, clockFrequency, out long remainder);
        if (wholeSeconds >= long.MaxValue / HundredNanosecondsPerSecond)
            return long.MaxValue;

        long wholeIntervals = wholeSeconds * HundredNanosecondsPerSecond;
        long fractionalIntervals = (long)Math.Ceiling(
            remainder * (double)HundredNanosecondsPerSecond / clockFrequency);
        if (wholeIntervals > long.MaxValue - fractionalIntervals)
            return long.MaxValue;

        return Math.Max(1, wholeIntervals + fractionalIntervals);
    }

    private static uint ComputeFailureTimeoutMilliseconds(long hundredNanoseconds)
    {
        double dueMilliseconds = hundredNanoseconds / 10_000d;
        double timeoutMilliseconds = Math.Ceiling(dueMilliseconds) + 1_000d;
        return timeoutMilliseconds >= uint.MaxValue - 1d
            ? uint.MaxValue - 1
            : Math.Max(1u, (uint)timeoutMilliseconds);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeWaitHandle CreateWaitableTimerExW(
        nint timerAttributes,
        nint timerName,
        uint flags,
        uint desiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimerEx(
        SafeWaitHandle timer,
        in long dueTime,
        int periodMilliseconds,
        nint completionRoutine,
        nint completionArgument,
        nint wakeContext,
        uint tolerableDelayMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(
        SafeWaitHandle handle,
        uint milliseconds);
}
