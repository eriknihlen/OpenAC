using System.Diagnostics;
using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class LinuxMonotonicFramePacingWaiterTests
{
    [Theory]
    [InlineData(1L, 1_000_000_000L, 1L)]
    [InlineData(1L, 3L, 333_333_334L)]
    [InlineData(15L, 1_000L, 15_000_000L)]
    [InlineData(6_060L, 1_000_000L, 6_060_000L)]
    public void TickConversionRoundsUpWithoutReturningZero(
        long ticks,
        long frequency,
        long expected)
    {
        Assert.Equal(
            expected,
            FramePacingDuration
                .ConvertTicksToNanoseconds(ticks, frequency));
    }

    [Fact]
    public void PlatformFactorySelectsCurrentOperatingSystem()
    {
        IFramePacingWaiter waiter =
            PlatformFramePacingWaiterFactory
                .ForCurrentProcess()
                .Create();

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsHighResolutionFramePacingWaiter>(
                waiter);
            ((IDisposable)waiter).Dispose();
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            Assert.IsType<LinuxMonotonicFramePacingWaiter>(waiter);
            ((IDisposable)waiter).Dispose();
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<MacMonotonicFramePacingWaiter>(waiter);
            ((IDisposable)waiter).Dispose();
            return;
        }

        throw new PlatformNotSupportedException();
    }

    [Fact]
    [Trait("Lane", "Linux")]
    public void LinuxWaitBlocksUntilRequestedMonotonicDeadline()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Lane=Linux requires a native Linux host.");

        LinuxMonotonicFramePacingWaiter waiter =
            LinuxMonotonicFramePacingWaiter.Create();
        long start = Stopwatch.GetTimestamp();
        waiter.Wait(
            durationTicks: Stopwatch.Frequency / 200L,
            clockFrequency: Stopwatch.Frequency);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(4),
            $"Linux monotonic wait returned after {elapsed.TotalMilliseconds:F3} ms.");
        Assert.True(
            elapsed < TimeSpan.FromSeconds(1),
            $"Linux monotonic wait stalled for {elapsed.TotalMilliseconds:F3} ms.");
    }

    [Fact]
    [Trait("Lane", "Linux")]
    public void LinuxWaiterRejectsUseAfterDisposal()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Lane=Linux requires a native Linux host.");

        LinuxMonotonicFramePacingWaiter waiter =
            LinuxMonotonicFramePacingWaiter.Create();
        waiter.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => waiter.Wait(1, Stopwatch.Frequency));
    }
}
