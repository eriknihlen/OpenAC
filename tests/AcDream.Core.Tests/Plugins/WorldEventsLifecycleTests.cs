using AcDream.Core.Plugins;

namespace AcDream.Core.Tests.Plugins;

public sealed class WorldEventsLifecycleTests
{
    [Fact]
    public void LoginCompleteReachesEverySubscriberEachTimeItFires()
    {
        var events = new WorldEvents();
        int first = 0;
        int second = 0;
        events.LoginComplete += () => first++;
        events.LoginComplete += () => second++;

        events.FireLoginComplete();
        events.FireLoginComplete();

        Assert.Equal(2, first);
        Assert.Equal(2, second);
    }

    [Fact]
    public void LogoffAndDeathReachTheirOwnSubscribers()
    {
        var events = new WorldEvents();
        int logoffs = 0;
        var deaths = new List<string>();
        events.Logoff += () => logoffs++;
        events.LocalPlayerDied += deaths.Add;

        events.FireLogoff();
        events.FireLocalPlayerDied("You have died!");

        Assert.Equal(1, logoffs);
        Assert.Equal(["You have died!"], deaths);
    }

    [Fact]
    public void AThrowingHandlerDoesNotStopTheOthers()
    {
        var events = new WorldEvents();
        bool reached = false;
        events.LoginComplete += static () =>
            throw new InvalidOperationException("boom");
        events.LoginComplete += () => reached = true;

        events.FireLoginComplete();

        Assert.True(reached);
    }

    [Fact]
    public void RemovingAHandlerStopsItBeingCalled()
    {
        var events = new WorldEvents();
        int calls = 0;
        void Handler() => calls++;

        events.LoginComplete += Handler;
        events.FireLoginComplete();
        events.LoginComplete -= Handler;
        events.FireLoginComplete();

        Assert.Equal(1, calls);
    }
}
