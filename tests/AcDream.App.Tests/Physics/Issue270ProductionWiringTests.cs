using System.Reflection;
using AcDream.App.Net;
using AcDream.App.Physics;
using AcDream.App.Tests.Architecture;
using AcDream.Core.Physics;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.Physics;

public sealed class Issue270ProductionWiringTests
{
    [Fact]
    public void MovementStats_UseOneEdgeTrackerAndResetItWithTheSession()
    {
        _ = Assert.Single(
            typeof(LiveMovementStatsApplier).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(StaminaExhaustionEdgeTracker));

        IReadOnlyList<CompiledCall> applierCalls =
            CompiledCallGraph.ReadDeclared(typeof(LiveMovementStatsApplier));
        Assert.Single(
            applierCalls,
            call =>
                call.Target.DeclaringType == typeof(StaminaExhaustionEdgeTracker)
                && call.Target.Name == nameof(StaminaExhaustionEdgeTracker.Observe));
        Assert.Single(
            applierCalls,
            call =>
                call.Target.DeclaringType
                    == typeof(RuntimeLocalPlayerMovementState)
                && call.Target.Name == nameof(
                    RuntimeLocalPlayerMovementState.ReportExhaustion));
        Assert.Single(
            applierCalls,
            call =>
                call.Target.DeclaringType == typeof(StaminaExhaustionEdgeTracker)
                && call.Target.Name == nameof(StaminaExhaustionEdgeTracker.Reset));

        IReadOnlyList<CompiledCall> factoryCalls =
            CompiledCallGraph.ReadDeclared(typeof(LiveSessionRuntimeFactory));
        Assert.Single(
            factoryCalls,
            call =>
                call.Target.DeclaringType == typeof(LiveMovementStatsApplier)
                && call.Target.Name == nameof(LiveMovementStatsApplier.Reset));
        Assert.Equal(
            2,
            factoryCalls.Count(call =>
                call.Target.DeclaringType == typeof(LiveMovementStatsApplier)
                && call.Target.Name == nameof(LiveMovementStatsApplier.Apply)));
        Assert.DoesNotContain(
            factoryCalls,
            call => call.Target.DeclaringType == typeof(MotionInterpreter)
                && call.Target.Name == nameof(MotionInterpreter.ReportExhaustion));
    }

    [Fact]
    public void RemoteSpawnSettle_IsRetriedAndCoversBothCreationRoutes()
    {
        const BindingFlags flags = BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;
        MethodInfo[] callers = typeof(LiveEntityNetworkUpdateController)
            .GetMethods(flags)
            .Where(method => method.GetMethodBody() is not null)
            .Where(method => CompiledCallGraph.Read(method).Any(call =>
                call.Target.DeclaringType
                    == typeof(LiveEntityNetworkUpdateController)
                && call.Target.Name == "SeedRemoteSpawnPlacement"))
            .ToArray();

        Assert.Equal(2, callers.Length);
        Assert.Equal(
            ["DispatchRemoteInboundMotion", "OnPosition"],
            callers.Select(method => method.Name).Order().ToArray());
        MethodInfo retryingInboundRoute = Assert.Single(
            callers,
            method => method.Name == "DispatchRemoteInboundMotion");
        IReadOnlyList<CompiledCall> retryCalls =
            CompiledCallGraph.Read(retryingInboundRoute);
        int contact = CompiledCallGraph.IndexOf(
            retryCalls,
            typeof(PhysicsBody),
            "get_InContact");
        int settle = CompiledCallGraph.IndexOf(
            retryCalls,
            typeof(LiveEntityNetworkUpdateController),
            "SeedRemoteSpawnPlacement");
        Assert.True(contact >= 0 && settle > contact);
        MethodInfo seed = typeof(LiveEntityNetworkUpdateController).GetMethod(
            "SeedRemoteSpawnPlacement",
            flags)
            ?? throw new MissingMethodException(
                typeof(LiveEntityNetworkUpdateController).FullName,
                "SeedRemoteSpawnPlacement");
        Assert.Contains(
            CompiledCallGraph.Read(seed),
            call => call.Target.DeclaringType == typeof(SpawnPlacementSettler)
                && call.Target.Name == nameof(SpawnPlacementSettler.TrySettle));
    }
}
