using System.Reflection;
using System.Reflection.Emit;
using AcDream.App.Tests.Architecture;
using AcDream.App.Physics;
using AcDream.Core.World;
using AcDream.Runtime.Session;

namespace AcDream.App.Tests.Physics;

public sealed class LiveEntityNetworkBranchRoutingTests
{
    [Fact]
    public void Vector_ProjectileStopsCanonicalAndOrdinaryRoutes()
    {
        var calls = new List<string>();

        LiveEntityVectorRoute route = LiveEntityVectorRouter.Route(
            () => { calls.Add("projectile"); return true; },
            () => { calls.Add("canonical"); return true; },
            () => calls.Add("ordinary"));

        Assert.Equal(LiveEntityVectorRoute.Projectile, route);
        Assert.Equal(["projectile"], calls);
    }

    [Fact]
    public void Vector_CanonicalBodyRunsOnlyAfterProjectileDeclines()
    {
        var calls = new List<string>();

        LiveEntityVectorRoute route = LiveEntityVectorRouter.Route(
            () => { calls.Add("projectile"); return false; },
            () => { calls.Add("canonical"); return true; },
            () => calls.Add("ordinary"));

        Assert.Equal(LiveEntityVectorRoute.CanonicalBody, route);
        Assert.Equal(["projectile", "canonical"], calls);
    }

    [Fact]
    public void Vector_OrdinaryRemoteIsTheLastFallback()
    {
        var calls = new List<string>();

        LiveEntityVectorRoute route = LiveEntityVectorRouter.Route(
            () => { calls.Add("projectile"); return false; },
            () => { calls.Add("canonical"); return false; },
            () => calls.Add("ordinary"));

        Assert.Equal(LiveEntityVectorRoute.OrdinaryRemote, route);
        Assert.Equal(["projectile", "canonical", "ordinary"], calls);
    }


    public sealed class LiveEntityNetworkUpdateControllerForcePositionWiringTests
    {
        [Fact]
        public void GenericTailWriteIsNeverDuplicatedForTheLocalForcePositionPath()
        {
            MethodInfo genericTail = typeof(LiveEntityNetworkUpdateController)
                .GetMethod(
                    "TryApplyGenericRemoteRenderPose",
                    BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(
                    typeof(LiveEntityNetworkUpdateController).FullName,
                    "TryApplyGenericRemoteRenderPose");
            Assert.Single(
                CompiledCallGraph.Read(genericTail),
                call => call.Target.DeclaringType == typeof(WorldEntity)
                    && call.Target.Name == nameof(WorldEntity.SetPosition));
        }

        [Fact]
        public void CommittedOrDeferredCellReturnsBeforeReachingTheGenericTail()
        {
            MethodInfo onPosition = typeof(LiveEntityNetworkUpdateController)
                .GetMethod(
                    "OnPosition",
                    BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMethodException(
                    typeof(LiveEntityNetworkUpdateController).FullName,
                    "OnPosition");
            IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(onPosition);
            CompiledCall drive = Assert.Single(calls, call =>
                call.Target.DeclaringType
                    == typeof(RuntimeAcceptedPositionDriveController)
                && call.Target.Name == "TryExecuteAcceptedLocalPosition");
            CompiledCall observe = calls.First(call =>
                call.Offset > drive.Offset
                && call.Target.Name == "ObserveAcceptedLocalPosition");
            IReadOnlyList<CompiledInstruction> instructions =
                CompiledCallGraph.ReadInstructions(onPosition);
            int observeInstruction = instructions
                .Select((instruction, index) => (instruction, index))
                .Single(pair => pair.instruction.Offset == observe.Offset)
                .index;

            Assert.Equal(OpCodes.Ret, instructions[observeInstruction + 1].OpCode);
        }
    }
}
