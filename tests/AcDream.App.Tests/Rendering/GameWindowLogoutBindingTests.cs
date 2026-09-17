using AcDream.App.Rendering;
using AcDream.App.Streaming;
using AcDream.App.Tests.Architecture;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Session;
using AcDream.Runtime.World;

namespace AcDream.App.Tests.Rendering;

public sealed class GameWindowLogoutBindingTests
{
    [Fact]
    public void BindLogoutDelegatesUseTeleportRequestAndTheTransitAndWorldGuards()
    {
        var owned = CompiledCallGraph.ReadOwned(typeof(GameWindow));

        Assert.Contains(
            owned,
            call => call.Target.DeclaringType == typeof(RuntimeAutomationSurface)
                && call.Target.Name == nameof(RuntimeAutomationSurface.BindLogout));
        Assert.Contains(
            owned,
            call => call.Target.DeclaringType == typeof(LocalPlayerTeleportController)
                && call.Target.Name
                    == nameof(LocalPlayerTeleportController.TryRequestLogout));
        Assert.Contains(
            owned,
            call => call.Target.DeclaringType == typeof(LiveSessionController)
                && call.Target.Name == "get_" + nameof(LiveSessionController.IsInWorld));
        Assert.Contains(
            owned,
            call => call.Target.DeclaringType == typeof(RuntimeWorldTransitState)
                && call.Target.Name == "get_" + nameof(RuntimeWorldTransitState.IsLogoutActive));
        Assert.Contains(
            owned,
            call => call.Target.DeclaringType == typeof(RuntimeWorldTransitState)
                && call.Target.Name == "get_" + nameof(RuntimeWorldTransitState.IsTeleportActive));
        Assert.Contains(
            owned,
            call => call.Target.DeclaringType == typeof(RuntimeWorldTransitState)
                && call.Target.Name
                    == "get_" + nameof(RuntimeWorldTransitState.HasPendingTeleportStart));
        Assert.DoesNotContain(
            owned,
            call => call.Target.DeclaringType == typeof(LocalPlayerTeleportController)
                && call.Target.Name == nameof(LocalPlayerTeleportController.RequestLogout));
    }
}
