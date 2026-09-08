using System.Reflection;
using AcDream.App.Input;
using AcDream.App.Tests.Architecture;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;

namespace AcDream.App.Tests.Input;

public sealed class C3cF2AutoEntryWiringTests
{
    [Fact]
    public void ProductionAutoEntryRequiresTheRuntimePublishedController()
    {
        MethodInfo readiness = typeof(LivePlayerModeAutoEntryContext)
            .GetProperty(
                "IsPlayerControllerReady",
                BindingFlags.Instance | BindingFlags.Public)!
            .GetMethod!;
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(readiness);

        Assert.Contains(
            calls,
            call => call.Target.DeclaringType == typeof(PlayerMovementController)
                && call.Target.Name == "get_IsRuntimePublished");
        Assert.Contains(
            calls,
            call => call.Target.DeclaringType == typeof(LiveEntityRuntime)
                && call.Target.Name == nameof(LiveEntityRuntime.TryGetRecord));
        Assert.Contains(
            calls,
            call => call.Target.DeclaringType == typeof(LiveEntityRecord)
                && call.Target.Name == "get_PhysicsHost");
        Assert.Contains(
            typeof(EntityPhysicsHost),
            CompiledCallGraph.ReadTypeReferences(readiness));
    }

    [Fact]
    public void PlayerPresentationAttach_DrainsMatchedAnimationsBeforeStartupMotionSuffix()
    {
        MethodInfo enter = typeof(PlayerModeController).GetMethod(
            "BuildControllerAndCamera",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(PlayerModeController).FullName,
                "BuildControllerAndCamera");
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(enter);
        int attach = CompiledCallGraph.IndexOf(
            calls,
            typeof(MotionInterpreter),
            "set_DefaultSink");
        int animationDrain = CompiledCallGraph.IndexOf(
            calls,
            typeof(MotionTableManager),
            nameof(MotionTableManager.HandleEnterWorld),
            attach + 1);
        int unmatchedMotionDrain = CompiledCallGraph.IndexOf(
            calls,
            typeof(MotionInterpreter),
            nameof(MotionInterpreter.HandleExitWorld),
            animationDrain + 1);

        Assert.True(attach >= 0, "The player animation sink was not attached.");
        Assert.True(
            animationDrain > attach,
            "Matching PartArray animations must drain after attaching the sink.");
        Assert.True(
            unmatchedMotionDrain > animationDrain,
            "Only the unmatched pre-attach interpreter suffix may drain last.");
    }
}
