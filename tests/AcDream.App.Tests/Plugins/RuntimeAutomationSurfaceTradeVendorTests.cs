using AcDream.App.Plugins;
using AcDream.Runtime.Plugins;
using AcDream.Core.Net.Messages;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Tests.Plugins;

// The App host's Trade/Vendor projection lives entirely in the shared
// AcDream.Runtime adapters (covered by AcDream.Runtime.Tests); this file
// checks only the App-specific bind/unbind lifecycle and per-tick polling.
public sealed class RuntimeAutomationSurfaceTradeVendorTests
{
    [Fact]
    public void TradeAndVendorAreTheNoOpDefaultBeforeBinding()
    {
        using var surface = new RuntimeAutomationSurface();

        Assert.Same(NoOpAutomationSurface.Instance, surface.Trade);
        Assert.Same(NoOpAutomationSurface.Instance, surface.Vendor);
    }

    [Fact]
    public void BindingReplacesTheNoOpDefaultAndUnbindRestoresIt()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();

        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        Assert.NotSame(NoOpAutomationSurface.Instance, surface.Trade);
        Assert.NotSame(NoOpAutomationSurface.Instance, surface.Vendor);

        surface.Unbind();
        Assert.Same(NoOpAutomationSurface.Instance, surface.Trade);
        Assert.Same(NoOpAutomationSurface.Instance, surface.Vendor);
    }

    [Fact]
    public void EventsTickPollsTradeSoOpenedEventuallyFires()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        var events = new WorldEvents();
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        int opened = 0;
        surface.Trade.Opened += _ => opened++;

        runtime.TradeOwner.ApplyRegister(
            new GameEvents.RegisterTrade(
                runtime.PlayerIdentity.ServerGuid, 0x70000099u, 0uL),
            runtime.PlayerIdentity.ServerGuid);
        Assert.Equal(0, opened);

        events.FireTick(1d / 30d);

        Assert.Equal(1, opened);
    }
}
