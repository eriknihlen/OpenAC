using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class FramePacingControllerTests
{
    [Fact]
    public void Software_pacing_waits_only_for_remaining_deadline_time()
    {
        var clock = new FakeClock(frequency: 1_000);
        var waiter = new AdvancingWaiter(clock);
        var controller = new FramePacingController(clock, waiter);
        controller.Apply(new FramePacingPolicy(UseVSync: false, SoftwareLimitHz: 10d));

        clock.Timestamp = 40;
        controller.CompleteFrame();
        Assert.Equal([60L], waiter.Durations);

        clock.Timestamp = 150;
        controller.CompleteFrame();
        Assert.Equal([60L, 50L], waiter.Durations);
    }

    [Fact]
    public void Missed_deadline_rebases_instead_of_running_a_catch_up_frame()
    {
        var clock = new FakeClock(frequency: 1_000);
        var waiter = new AdvancingWaiter(clock);
        var controller = new FramePacingController(clock, waiter);
        controller.Apply(new FramePacingPolicy(UseVSync: false, SoftwareLimitHz: 10d));

        clock.Timestamp = 250;
        controller.CompleteFrame();
        Assert.Empty(waiter.Durations);

        // Rebased deadline is 350, not one of the elapsed 100/200/300
        // deadlines. The next frame therefore waits instead of bursting.
        clock.Timestamp = 300;
        controller.CompleteFrame();
        Assert.Equal([50L], waiter.Durations);
    }

    [Fact]
    public void Wait_overshoot_rebases_future_deadline()
    {
        var clock = new FakeClock(frequency: 1_000);
        var waiter = new AdvancingWaiter(clock) { OvershootTicks = 150 };
        var controller = new FramePacingController(clock, waiter);
        controller.Apply(new FramePacingPolicy(UseVSync: false, SoftwareLimitHz: 10d));

        controller.CompleteFrame();
        Assert.Equal(250, clock.Timestamp);

        waiter.OvershootTicks = 0;
        clock.Timestamp = 300;
        controller.CompleteFrame();
        Assert.Equal([100L, 50L], waiter.Durations);
    }

    [Fact]
    public void VSync_and_explicit_uncapped_policies_do_not_software_wait()
    {
        var clock = new FakeClock(frequency: 1_000);
        var waiter = new AdvancingWaiter(clock);
        var controller = new FramePacingController(clock, waiter);

        controller.Apply(new FramePacingPolicy(UseVSync: true, SoftwareLimitHz: null));
        clock.Timestamp = 1_000;
        controller.CompleteFrame();

        controller.Apply(new FramePacingPolicy(UseVSync: false, SoftwareLimitHz: null));
        clock.Timestamp = 2_000;
        controller.CompleteFrame();

        Assert.Empty(waiter.Durations);
    }

    [Fact]
    public void Rate_change_resets_deadline_from_current_monotonic_time()
    {
        var clock = new FakeClock(frequency: 1_000);
        var waiter = new AdvancingWaiter(clock);
        var controller = new FramePacingController(clock, waiter);
        controller.Apply(new FramePacingPolicy(UseVSync: false, SoftwareLimitHz: 10d));

        clock.Timestamp = 25;
        controller.Apply(new FramePacingPolicy(UseVSync: false, SoftwareLimitHz: 20d));
        clock.Timestamp = 50;
        controller.CompleteFrame();

        Assert.Equal([25L], waiter.Durations);
    }

    [Theory]
    [InlineData(1L, 10_000_000L, 1L)]
    [InlineData(1L, 3L, 3_333_334L)]
    [InlineData(6_000L, 1_000_000L, 60_000L)]
    [InlineData(1_000L, 1_000L, 10_000_000L)]
    public void High_resolution_timer_conversion_rounds_up_without_losing_time(
        long durationTicks,
        long frequency,
        long expectedHundredNanoseconds)
    {
        Assert.Equal(
            expectedHundredNanoseconds,
            WindowsHighResolutionFramePacingWaiter.ConvertTicksToHundredNanoseconds(
                durationTicks,
                frequency));
    }

    [Fact]
    public void High_resolution_timer_conversion_saturates_instead_of_overflowing()
    {
        Assert.Equal(
            long.MaxValue,
            WindowsHighResolutionFramePacingWaiter.ConvertTicksToHundredNanoseconds(
                long.MaxValue,
                clockFrequency: 1));
    }

    [Fact]
    [Trait("Lane", "Windows")]
    public void Windows_high_resolution_timer_arms_and_disposes_its_handle()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
            throw new PlatformNotSupportedException("Lane=Windows requires a Windows host.");

        using var timer = WindowsHighResolutionFramePacingWaiter.Create();
        timer.Wait(1, System.Diagnostics.Stopwatch.Frequency);

        timer.Dispose();
        timer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => timer.Wait(1, 1));
    }

    [Fact]
    public void Controller_disposes_owned_waiter_exactly_once()
    {
        var waiter = new DisposableWaiter();
        var controller = new FramePacingController(
            new FakeClock(frequency: 1_000),
            waiter,
            ownsWaiter: true);

        controller.Dispose();
        controller.Dispose();

        Assert.Equal(1, waiter.DisposeCount);
    }

    private sealed class FakeClock(long frequency) : IFramePacingClock
    {
        public long Frequency { get; } = frequency;

        public long Timestamp { get; set; }

        public long GetTimestamp() => Timestamp;
    }

    private sealed class AdvancingWaiter(FakeClock clock) : IFramePacingWaiter
    {
        public List<long> Durations { get; } = [];

        public long OvershootTicks { get; set; }

        public void Wait(long durationTicks, long clockFrequency)
        {
            Assert.Equal(clock.Frequency, clockFrequency);
            Durations.Add(durationTicks);
            clock.Timestamp += durationTicks + OvershootTicks;
        }
    }

    private sealed class DisposableWaiter : IFramePacingWaiter, IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Wait(long durationTicks, long clockFrequency)
        {
        }

        public void Dispose() => DisposeCount++;
    }
}
