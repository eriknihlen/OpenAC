using AcDream.Plugin.Abstractions;
using AcDream.DrakBot.Spells;
using AcDream.DrakBot.Tests.Fakes;

namespace AcDream.DrakBot.Tests;

public sealed class CastTrackerTests
{
    [Fact]
    public void ACastTheHostStillCallsInProgressTimesOutAllTheSame()
    {
        // A cast refused before it left the client can leave the host saying
        // 'casting' for good; the tracker waits longer for one of those than
        // for a request that never started, but not for ever.
        var surface = new FakeAutomationSurface();
        surface.SelfBuffs.Add(Spell.SelfBuff(50, "Heal Self V", 200, 5));
        var clock = new TickClock();
        var tracker = new CastTracker(surface, clock);

        Assert.Equal(PluginCastRequestResult.Sent, tracker.Request(50u, 0u));
        surface.IsCasting = true;
        clock.Advance(CastTracker.RequestTimeoutSeconds + 1d);
        Assert.Null(tracker.Poll());
        Assert.True(tracker.HasPendingRequest);

        clock.Advance(CastTracker.CastingTimeoutSeconds);
        Assert.Equal(CastOutcome.TimedOut, tracker.Poll());
        Assert.False(tracker.HasPendingRequest);
    }
}
