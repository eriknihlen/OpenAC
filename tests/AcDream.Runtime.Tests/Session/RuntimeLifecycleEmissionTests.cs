using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Session;

// GameRuntime.Lifecycle.State is computed live from Session.IsInWorld; the
// Starting -> InWorld edge lands asynchronously, over several ticks, when a
// host polls its connect during ticks (PollConnectionDuringTicks) rather
// than blocking Start() until the handshake completes. A plugin observer
// (the shape AppAutomationSurface.OnLifecycle uses) must see exactly one
// transition to InWorld for that edge, and exactly one transition away from
// it on Stop -- whether the edge lands at the Start()/Stop() command
// boundary or, as here, only later inside a per-frame Tick().
public sealed class RuntimeLifecycleEmissionTests
{
    [Fact]
    public void AnAsyncInWorldEdgeThatLandsDuringTickStillFiresExactlyOnce()
    {
        using var host = new NoWindowGameRuntimeHost(deferredConnectTickCount: 3);
        var observer = new LoginTrackingObserver();
        using IDisposable subscription = host.Runtime.Subscribe(observer);

        host.Start();

        // The connect is still polling; the command boundary must not have
        // reported InWorld yet -- there is nothing to report.
        Assert.Equal(
            RuntimeLifecycleState.Starting,
            host.Runtime.Lifecycle.State);
        Assert.Equal(0, observer.LoginCount);

        // Ticks 1 and 2 do not complete the poll yet.
        host.Session.Tick();
        Assert.Equal(0, observer.LoginCount);
        host.Session.Tick();
        Assert.Equal(0, observer.LoginCount);

        // Tick 3 is where PollConnect finally returns true and CompleteStart
        // runs -- entirely inside Tick(), never touching Start() again. Before
        // the fix, nothing observed this edge: GameRuntime never emitted a
        // lifecycle transition outside a Start/Reconnect/Stop call.
        host.Session.Tick();

        Assert.Equal(RuntimeLifecycleState.InWorld, host.Runtime.Lifecycle.State);
        Assert.Equal(1, observer.LoginCount);
        Assert.Equal(0, observer.LogoffCount);

        // Ticking further after convergence must not re-fire.
        host.Session.Tick();
        host.Session.Tick();
        Assert.Equal(1, observer.LoginCount);

        host.Stop();
        Assert.Equal(1, observer.LoginCount);
        Assert.Equal(1, observer.LogoffCount);
    }

    [Fact]
    public void ReconnectFiresLoginCompleteAgainThroughTheSameRealPath()
    {
        using var host = new NoWindowGameRuntimeHost();
        var observer = new LoginTrackingObserver();
        using IDisposable subscription = host.Runtime.Subscribe(observer);

        host.Start();
        Assert.Equal(1, observer.LoginCount);

        host.Stop();
        Assert.Equal(1, observer.LogoffCount);

        // A reconnect must report a fresh in-world edge, not a memory of the
        // first one -- exercised through the real Reconnect()/Tick() path
        // rather than a synthetic EmitLifecycle call.
        host.Reconnect();
        Assert.Equal(2, observer.LoginCount);
        Assert.Equal(1, observer.LogoffCount);
    }

    [Fact]
    public void TheAsyncInWorldEdgeStillFiresExactlyOnceAcrossAReconnect()
    {
        using var host = new NoWindowGameRuntimeHost(deferredConnectTickCount: 2);
        var observer = new LoginTrackingObserver();
        using IDisposable subscription = host.Runtime.Subscribe(observer);

        host.Start();
        Assert.Equal(0, observer.LoginCount);
        host.Session.Tick();
        Assert.Equal(0, observer.LoginCount);
        host.Session.Tick();
        Assert.Equal(RuntimeLifecycleState.InWorld, host.Runtime.Lifecycle.State);
        Assert.Equal(1, observer.LoginCount);

        host.Stop();
        Assert.Equal(1, observer.LogoffCount);

        // Re-arm the fixture's poll countdown -- PollConnect otherwise
        // returns true immediately on a second connect attempt, which
        // would hide a regression where the async edge only worked once.
        host.RearmDeferredConnectTicks(2);
        host.Reconnect();
        Assert.Equal(
            RuntimeLifecycleState.Starting,
            host.Runtime.Lifecycle.State);
        Assert.Equal(1, observer.LoginCount);

        host.Session.Tick();
        Assert.Equal(1, observer.LoginCount);
        host.Session.Tick();

        Assert.Equal(RuntimeLifecycleState.InWorld, host.Runtime.Lifecycle.State);
        Assert.Equal(2, observer.LoginCount);
        Assert.Equal(1, observer.LogoffCount);
    }

    private sealed class LoginTrackingObserver : IRuntimeEventObserver
    {
        private bool _wasInWorld;

        public int LoginCount { get; private set; }
        public int LogoffCount { get; private set; }

        public void OnLifecycle(in RuntimeLifecycleDelta delta)
        {
            bool isInWorld = delta.Current == RuntimeLifecycleState.InWorld;
            if (_wasInWorld == isInWorld)
                return;
            _wasInWorld = isInWorld;
            if (isInWorld)
                LoginCount++;
            else
                LogoffCount++;
        }

        public void OnCommand(in RuntimeCommandDelta delta) { }
        public void OnEntity(in RuntimeEntityDelta delta) { }
        public void OnInventory(in RuntimeInventoryDelta delta) { }
        public void OnChat(in RuntimeChatDelta delta) { }
        public void OnMovement(in RuntimeMovementDelta delta) { }
        public void OnPortal(in RuntimePortalDelta delta) { }
        public void OnCombat(in RuntimeCombatDelta delta) { }
    }
}
