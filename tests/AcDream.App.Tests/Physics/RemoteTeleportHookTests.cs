using AcDream.App.Physics;
using AcDream.Core.Physics;

namespace AcDream.App.Tests.Physics;

public sealed class RemoteTeleportHookTests
{
    [Fact]
    public void Execute_UsesRetailOrderAndExactCancelContext()
    {
        var calls = new List<string>();
        WeenieError cancelContext = WeenieError.None;

        RemoteTeleportHook.Execute(new RemoteTeleportHookActions(
            CancelMoveTo: error =>
            {
                cancelContext = error;
                calls.Add("cancel");
            },
            UnStick: () => calls.Add("unstick"),
            StopInterpolating: () => calls.Add("stop-interpolating"),
            UnConstrain: () => calls.Add("unconstrain"),
            NotifyTeleported: () => calls.Add("notify-teleported"),
            ReportCollisionEnd: () => calls.Add("collision-end")));

        Assert.Equal(0x3Cu, (uint)cancelContext);
        Assert.Equal(
            [
                "cancel",
                "unstick",
                "stop-interpolating",
                "unconstrain",
                "notify-teleported",
                "collision-end",
            ],
            calls);
    }
}
