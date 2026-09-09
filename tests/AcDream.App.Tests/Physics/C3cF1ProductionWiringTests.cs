using AcDream.App.Tests.Architecture;
using AcDream.App.Physics;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.Physics;

public sealed class C3cF1ProductionWiringTests
{
    [Fact]
    public void LocalPlayerInboundSetState_RoutesThroughTheTypedOwnerEntry()
    {
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.ReadDeclared(
            typeof(LiveEntityNetworkUpdateController));

        Assert.Single(
            calls,
            call =>
                call.Target.DeclaringType == typeof(PlayerMovementController)
                && call.Target.Name == "ApplyServerPhysicsState");
        Assert.DoesNotContain(
            calls,
            call => call.Target.DeclaringType == typeof(PlayerMovementController)
                && call.Target.Name == nameof(PlayerMovementController.ApplyPhysicsState));
    }
}
