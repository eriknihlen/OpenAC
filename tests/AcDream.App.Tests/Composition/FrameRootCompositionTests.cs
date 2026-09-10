using System.Reflection;
using AcDream.App.Composition;
using AcDream.App.Diagnostics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Architecture;
using AcDream.App.Update;
using AcDream.App.World;

namespace AcDream.App.Tests.Composition;

public sealed class FrameRootCompositionTests
{
    [Fact]
    public void RuntimeBindingsClearSkyActivationGateOnRollback()
    {
        var calls = new List<string>();
        var gate = new RecordingSkyPesActivationGate(calls);
        var slot = new SkyPesActivationGateSlot();
        var bindings = new FrameRootRuntimeBindings();
        bindings.Adopt("sky presentation effects activation", slot.BindOwned(gate));
        var scope = new CompositionAcquisitionScope();
        scope.Own(
            "frame-root runtime bindings",
            bindings,
            static value => value.Dispose());

        Assert.Throws<InvalidOperationException>(() =>
            scope.RollbackAndThrow(new InvalidOperationException("composition failed")));
        slot.Tick();

        Assert.Empty(calls);
    }

    [Fact]
    public void RuntimeBindingsReleaseInReverseAndRetryOnlyFailedEdges()
    {
        var calls = new List<string>();
        var bindings = new FrameRootRuntimeBindings();
        bindings.Adopt("first", new RetryBinding("first", calls, 0));
        bindings.Adopt("second", new RetryBinding("second", calls, 1));

        Assert.Throws<AggregateException>(bindings.Dispose);
        Assert.Equal(["second", "first"], calls);

        bindings.Dispose();
        bindings.Dispose();

        Assert.Equal(["second", "first", "second"], calls);
        Assert.Throws<ObjectDisposedException>(() =>
            bindings.Adopt("late", new RetryBinding("late", calls, 0)));
    }

    [Fact]
    public void ProductionPhasePublishesOnlyAfterBothRootsExist()
    {
        MethodInfo compose = typeof(FrameRootCompositionPhase).GetMethod(
            "ComposeCore",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(FrameRootCompositionPhase).FullName,
                "ComposeCore");
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(compose);

        int resources = CallIndex(calls, typeof(RenderFrameResourceController), ".ctor");
        int scene = CallIndex(calls, typeof(WorldSceneRenderer), ".ctor", resources + 1);
        int automation = CallIndex(
            calls,
            typeof(WorldLifecycleAutomationController),
            ".ctor",
            scene + 1);
        int diagnostics = CallIndex(
            calls,
            typeof(SerialRenderFramePostDiagnosticsPhase),
            ".ctor",
            automation + 1);
        int render = CallIndex(calls, typeof(RenderFrameOrchestrator), ".ctor", diagnostics + 1);
        int live = CallIndex(calls, typeof(RetailLiveFrameCoordinator), ".ctor", render + 1);
        int update = CallIndex(calls, typeof(UpdateFrameOrchestrator), ".ctor", live + 1);
        int graph = CallIndex(calls, typeof(GameFrameGraphSlot), "PublishOwned", update + 1);
        int publish = CallIndex(
            calls,
            typeof(IGameWindowFrameRootPublication),
            "PublishFrameRoots",
            graph + 1);
        int firstTransfer = calls
            .Select((call, index) => (call, index))
            .First(pair => pair.index > publish
                && pair.call.Target.Name == "Transfer")
            .index;
        int secondTransfer = calls
            .Select((call, index) => (call, index))
            .First(pair => pair.index > firstTransfer
                && pair.call.Target.Name == "Transfer")
            .index;

        Assert.True(secondTransfer > firstTransfer);
    }

    [Fact]
    public void ProductionWiresTheOptionalScreenshotAdapterIntoTheOuterRenderOwner()
    {
        MethodInfo compose = typeof(FrameRootCompositionPhase).GetMethod(
            "ComposeCore",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(FrameRootCompositionPhase).FullName,
                "ComposeCore");
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(compose);

        int screenshot = CallIndex(calls, typeof(PrivateFrameScreenshot), ".ctor");
        int presentation = CallIndex(
            calls,
            typeof(PrivatePresentationRenderer),
            ".ctor",
            screenshot + 1);
        int orchestrator = CallIndex(
            calls,
            typeof(RenderFrameOrchestrator),
            ".ctor",
            presentation + 1);

        Assert.True(orchestrator > presentation);
        Assert.Contains(
            typeof(RenderFrameOrchestrator).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(IPrivateFrameScreenshot));
        Assert.DoesNotContain(
            typeof(PrivatePresentationRenderer).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(IPrivateFrameScreenshot));
    }

    [Fact]
    public void GameWindowRetainsOnlyThePhaseBoundaryAndFrameHandoffs()
    {
        IReadOnlyList<CompiledCall> calls =
            CompiledCallGraph.ReadDeclared(typeof(GameWindow));

        Assert.Single(
            calls,
            call => call.Target.DeclaringType == typeof(FrameRootCompositionPhase)
                && call.Target.IsConstructor);
        Assert.DoesNotContain(
            calls,
            call => call.Target.IsConstructor
                && call.Target.DeclaringType is { } type
                && (type == typeof(WorldSceneRenderer)
                    || type == typeof(UpdateFrameOrchestrator)
                    || type == typeof(WorldLifecycleAutomationController)));
        Assert.DoesNotContain(
            calls,
            call => call.Target.DeclaringType
                    == typeof(WorldLifecycleResourceSnapshotSource)
                && call.Target.Name == nameof(
                    WorldLifecycleResourceSnapshotSource.Capture));
    }

    [Fact]
    public void FramePhaseAndSnapshotSourceRetainNoWindowOwner()
    {
        Assert.DoesNotContain(
            typeof(FrameRootCompositionPhase).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(GameWindow));

        Assert.DoesNotContain(
            typeof(WorldLifecycleResourceSnapshotSource).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(GameWindow));

        MethodInfo capture = typeof(WorldLifecycleResourceSnapshotSource)
            .GetMethod(nameof(WorldLifecycleResourceSnapshotSource.Capture))!;
        Assert.Equal(
            typeof(RenderFrameOutcome),
            Assert.Single(capture.GetParameters()).ParameterType);
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(capture);
        Assert.Contains(
            calls,
            call => call.Target.DeclaringType == typeof(LiveEntityRuntime)
                && call.Target.Name == "get_PendingTeardownCount");
        Assert.Contains(
            calls,
            call => call.Target.DeclaringType == typeof(GpuMemoryTracker)
                && call.Target.Name == "get_AllocatedBytes");
        Assert.Contains(
            calls,
            call => call.Target.DeclaringType == typeof(FrameProfiler)
                && call.Target.Name == "get_LastReport");
        Assert.Contains(
            calls,
            call => call.Target.DeclaringType == typeof(WorldRenderFrameOutcome)
                && call.Target.Name == "get_VisibleLandblocks");
        Assert.Contains(
            calls,
            call => call.Target.DeclaringType == typeof(WorldRenderFrameOutcome)
                && call.Target.Name == "get_TotalLandblocks");
    }

    private sealed class RetryBinding(
        string name,
        List<string> calls,
        int failures) : IDisposable
    {
        private int _failures = failures;

        public void Dispose()
        {
            calls.Add(name);
            if (_failures-- > 0)
                throw new InvalidOperationException("retry");
        }
    }

    private sealed class RecordingSkyPesActivationGate(List<string> calls)
        : ISkyPesActivationGate
    {
        public void Tick() => calls.Add("tick");
    }

    private static int CallIndex(
        IReadOnlyList<CompiledCall> calls,
        Type declaringType,
        string methodName,
        int startIndex = 0)
    {
        int index = CompiledCallGraph.IndexOf(
            calls,
            declaringType,
            methodName,
            startIndex);
        Assert.True(
            index >= startIndex,
            $"Missing compiled edge {declaringType.FullName}.{methodName}.");
        return index;
    }
}
