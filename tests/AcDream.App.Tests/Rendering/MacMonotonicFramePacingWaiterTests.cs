using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class MacMonotonicFramePacingWaiterTests
{
    // Apple silicon reports a 125/3 timebase, so mach units are not
    // nanoseconds; x86 Macs report 1/1, where they are.
    [Theory]
    [InlineData(1_000L, 125u, 3u, 24UL)]
    [InlineData(125_000L, 125u, 3u, 3_000UL)]
    [InlineData(1_000L, 1u, 1u, 1_000UL)]
    [InlineData(41L, 125u, 3u, 1UL)]
    [InlineData(42L, 125u, 3u, 2UL)]
    public void NanosecondsConvertThroughTheReportedTimebase(
        long nanoseconds,
        uint numerator,
        uint denominator,
        ulong expected)
    {
        Assert.Equal(
            expected,
            MacMonotonicFramePacingWaiter.ConvertNanosecondsToMachUnits(
                nanoseconds,
                numerator,
                denominator));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void NonPositiveDurationsWaitForNothing(long nanoseconds)
    {
        Assert.Equal(
            0UL,
            MacMonotonicFramePacingWaiter.ConvertNanosecondsToMachUnits(
                nanoseconds,
                125u,
                3u));
    }

    [Fact]
    public void AnOverlongDurationSaturatesInsteadOfWrapping()
    {
        Assert.Equal(
            ulong.MaxValue,
            MacMonotonicFramePacingWaiter.ConvertNanosecondsToMachUnits(
                long.MaxValue,
                1u,
                1_000u));
    }

    [Theory]
    [InlineData(0u, 3u)]
    [InlineData(125u, 0u)]
    public void AnUnusableTimebaseIsRejected(uint numerator, uint denominator)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MacMonotonicFramePacingWaiter.ConvertNanosecondsToMachUnits(
                1_000L,
                numerator,
                denominator));
    }
}
