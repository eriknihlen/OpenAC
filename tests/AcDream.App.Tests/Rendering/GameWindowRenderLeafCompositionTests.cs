using System.Reflection;
using AcDream.App.Composition;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Streaming;
using AcDream.App.Tests.Architecture;
using AcDream.App.UI;

namespace AcDream.App.Tests.Rendering;

public sealed class GameWindowRenderLeafCompositionTests
{
    private const BindingFlags Declared = BindingFlags.Instance
        | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.DeclaredOnly;

    [Fact]
    public void ProductionRender_PreservesPrivatePresentationAndCaptureOrder()
    {
        MethodInfo presentation = RequiredMethod(
            typeof(PrivatePresentationRenderer),
            nameof(PrivatePresentationRenderer.Render));
        AssertCallOrder(
            presentation,
            (typeof(IPrivatePortalViewport), nameof(IPrivatePortalViewport.Draw)),
            (typeof(IPrivateEntityViewportFrame), nameof(IPrivateEntityViewportFrame.Render)),
            (typeof(IRetainedGameplayUiFrame), nameof(IRetainedGameplayUiFrame.Render)),
            (typeof(IDevToolsFrameLifecycle), nameof(IDevToolsFrameLifecycle.Render)));

        MethodInfo orchestrator = RequiredMethod(
            typeof(RenderFrameOrchestrator),
            nameof(RenderFrameOrchestrator.Render));
        AssertCallOrder(
            orchestrator,
            (typeof(IWorldSceneFramePhase), nameof(IWorldSceneFramePhase.Render)),
            (typeof(IPrivatePresentationFramePhase), nameof(IPrivatePresentationFramePhase.Render)),
            (typeof(IRenderFrameLifetime), nameof(IRenderFrameLifetime.EndFrame)),
            (typeof(IPrivateFrameScreenshot), nameof(IPrivateFrameScreenshot.CapturePending)),
            (typeof(IRenderFrameDiagnosticsPhase), nameof(IRenderFrameDiagnosticsPhase.Publish)));
    }

    [Fact]
    public void ProductionRender_Prepares_resources_then_devtools_weather_and_world()
    {
        MethodInfo prepare = RequiredMethod(
            typeof(RenderFramePreparationController),
            nameof(RenderFramePreparationController.Prepare));
        AssertCallOrder(
            prepare,
            (typeof(IRenderFrameResourcePhase), nameof(IRenderFrameResourcePhase.Prepare)),
            (typeof(IDevToolsFrameLifecycle), nameof(IDevToolsFrameLifecycle.BeginFrame)),
            (typeof(IRenderWeatherFramePhase), nameof(IRenderWeatherFramePhase.Tick)));
    }

    [Fact]
    public void Composition_transfers_portal_tunnel_before_constructing_frame_borrowers()
    {
        MethodInfo complete = RequiredMethod(
            typeof(SessionPlayerCompositionPhase),
            "CompleteSessionPlayer");
        AssertCallOrder(
            complete,
            (typeof(TransferableResourceSlot<>), "Transfer"),
            (typeof(DeferredLocalPlayerTeleportNetworkSink), "BindOwned"),
            (typeof(IGameWindowSessionPlayerPublication), "PublishSessionPlayer"));

        MethodBase tunnelFactory = ReferencedMethodConstructing(
            complete,
            typeof(LocalPlayerTeleportPresentation));
        Assert.NotNull(ReferencedMethodConstructing(
            tunnelFactory,
            typeof(LocalPlayerTeleportController)));

        MethodInfo onLoad = RequiredMethod(typeof(GameWindow), "OnLoad");
        IReadOnlyList<CompiledCall> loadReferences =
            CompiledCallGraph.ReadMethodReferences(onLoad);
        CompiledCall sessionPhase = Assert.Single(
            loadReferences,
            reference => Constructs(
                reference.Target,
                typeof(SessionPlayerCompositionPhase)));
        CompiledCall framePhase = Assert.Single(
            loadReferences,
            reference => Constructs(
                reference.Target,
                typeof(FrameRootCompositionPhase)));
        Assert.True(sessionPhase.Offset < framePhase.Offset);

        MethodInfo composeFrame = RequiredMethod(
            typeof(FrameRootCompositionPhase),
            "ComposeCore");
        AssertCallOrder(
            composeFrame,
            (typeof(LocalPlayerTeleportRenderStateSource), ".ctor"),
            (typeof(RenderFrameResourceController), ".ctor"),
            (typeof(PrivatePresentationRenderer), ".ctor"),
            (typeof(RenderFrameOrchestrator), ".ctor"));
    }

    [Fact]
    public void ProductionComposition_RemovesLegacyLeafOwnership()
    {
        FieldInfo[] windowFields = typeof(GameWindow).GetFields(Declared);
        HashSet<string> removedFieldNames =
        [
            "_imguiBootstrap",
            "_panelHost",
            "_paperdollDollDirty",
            "_lastRenderSignature",
            "_lastVisibleLandblocks",
            "_perfAccum",
            "_entityUploadTiming",
            "_weatherAccum",
        ];
        HashSet<string> removedFieldTypes =
        [
            nameof(WorldRenderDiagnostics),
            nameof(WorldRenderFrameBuilder),
            nameof(SkyPesFrameController),
            nameof(RetailPViewRenderer),
            nameof(RetailPViewPassExecutor),
            nameof(RetailPViewCellSource),
            nameof(TerrainDrawDiagnosticsController),
        ];
        Assert.DoesNotContain(
            windowFields,
            field => removedFieldNames.Contains(field.Name)
                || removedFieldTypes.Contains(field.FieldType.Name));

        HashSet<string> removedMethods =
        [
            "RefreshPaperdollDoll",
            "ApplyPaperdollPose",
            "ResolvePaperdollPoseDid",
            "EnumerateDebugPanel",
            "ResetPanelLayout",
            "SetPanelLayout",
            "EmitRenderSignatureIfChanged",
            "EmitRetailPViewDiagnostics",
            "EmitGlStateTripwireIfChanged",
            "EmitClipRouteScissorProbe",
            "TryGetLoginWorldCell",
            "ApplyFramePacingPreference",
            "RefreshActiveMonitorFramePacing",
            "OnFrameRendered",
        ];
        Assert.DoesNotContain(
            typeof(GameWindow).GetMethods(Declared),
            method => removedMethods.Contains(method.Name));

        Assert.Contains(
            CompiledCallGraph.ReadDeclared(typeof(LivePresentationCompositionPhase)),
            call => call.Target.DeclaringType == typeof(PaperdollFramePresenter)
                && call.Target.IsConstructor);
        IReadOnlyList<CompiledCall> frameCalls =
            CompiledCallGraph.ReadDeclared(typeof(FrameRootCompositionPhase));
        Assert.All(
            new[]
            {
                typeof(RenderFrameResourceController),
                typeof(RenderWeatherFrameController),
                typeof(PrivatePresentationRenderer),
                typeof(RenderFrameOrchestrator),
            },
            type => Assert.Contains(
                frameCalls,
                call => call.Target.DeclaringType == type
                    && call.Target.IsConstructor));
        Assert.Contains(
            CompiledCallGraph.ReadDeclared(typeof(GameWindow)),
            call => call.Target.DeclaringType == typeof(DisplayFramePacingController)
                && call.Target.IsConstructor);
        Assert.Contains(
            CompiledCallGraph.ReadDeclared(typeof(CameraPointerInputController)),
            call => call.Target.DeclaringType == typeof(IInputCaptureSource)
                && call.Target.Name == "get_WantCaptureMouse");
    }

    [Fact]
    public void Shutdown_DrainsGpuBeforeFrontendsAndPreservesFrameBorrowerOrder()
    {
        MethodInfo create = RequiredMethod(
            typeof(GameWindowShutdownManifest),
            nameof(GameWindowShutdownManifest.Create));
        IReadOnlyList<string> labels = CompiledCallGraph.ReadStringLiterals(create);

        AssertLabelOrder(
            labels,
            "submitted GPU work",
            "render frontends",
            "portal tunnel",
            "paperdoll viewport",
            "graphics API context");
        AssertLabelOrder(
            labels,
            "frame borrowers",
            "world frame composition",
            "frame-root bindings",
            "session dependents");
        AssertLabelOrder(labels, "frame pacing", "frame profiler");
        AssertLabelOrder(
            labels,
            "world frame composition",
            "render frontends",
            "input context",
            "graphics API context");
    }

    [Fact]
    public void ProductionOutcome_UsesObservedWorldAndScreenshotFacts()
    {
        MethodInfo presentation = RequiredMethod(
            typeof(PrivatePresentationRenderer),
            nameof(PrivatePresentationRenderer.Render));
        AssertCallOrder(
            presentation,
            (typeof(RenderFrameFoundation), "get_PortalViewportVisible"),
            (typeof(IPrivatePortalViewport), nameof(IPrivatePortalViewport.Draw)),
            (typeof(PrivatePresentationFrameOutcome), ".ctor"));

        MethodInfo orchestrator = RequiredMethod(
            typeof(RenderFrameOrchestrator),
            nameof(RenderFrameOrchestrator.Render));
        AssertCallOrder(
            orchestrator,
            (typeof(IRenderFrameGpuMeasurement), nameof(IRenderFrameGpuMeasurement.BeginFrame)),
            (typeof(IWorldSceneFramePhase), nameof(IWorldSceneFramePhase.Render)),
            (typeof(IPrivatePresentationFramePhase), nameof(IPrivatePresentationFramePhase.Render)),
            (typeof(IRenderFrameGpuMeasurement), nameof(IRenderFrameGpuMeasurement.EndFrame)),
            (typeof(IRenderFrameLifetime), nameof(IRenderFrameLifetime.EndFrame)),
            (typeof(IPrivateFrameScreenshot), nameof(IPrivateFrameScreenshot.CapturePending)),
            (typeof(RenderFrameOutcome), ".ctor"),
            (typeof(IRenderFrameDiagnosticsPhase), nameof(IRenderFrameDiagnosticsPhase.Publish)));
    }

    [Fact]
    public void GameWindow_OnRenderIsOneImmutableOrchestratorHandoff()
    {
        MethodInfo onRender = RequiredMethod(typeof(GameWindow), "OnRender");
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(onRender);
        Assert.Single(
            calls,
            call => call.Target.DeclaringType == typeof(GameFrameGraphSlot)
                && call.Target.Name == nameof(GameFrameGraphSlot.Render));

        HashSet<string> forbiddenFields =
        [
            "_gpuFrameFlights",
            "_worldScene",
            "_devToolsFramePresenter",
            "_retailUiRuntime",
            "_frameScreenshots",
        ];
        Assert.DoesNotContain(
            CompiledCallGraph.ReadFieldReferences(onRender),
            reference => forbiddenFields.Contains(reference.Field.Name));
    }

    [Fact]
    public void PaperdollComposition_SkipsEitherMissingOptionalUiSurface()
    {
        MethodBase compose = MethodConstructing(
            typeof(LivePresentationCompositionPhase),
            typeof(PaperdollFramePresenter));
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(compose);
        CompiledCall viewport = Assert.Single(
            calls,
            call => call.Target.DeclaringType == typeof(RetailUiRuntime)
                && call.Target.Name == "get_PaperdollViewportWidget");
        CompiledCall inventory = Assert.Single(
            calls,
            call => call.Target.DeclaringType == typeof(RetailUiRuntime)
                && call.Target.Name == "get_InventoryFrame");
        CompiledCall presenter = Assert.Single(
            calls,
            call => call.Target.DeclaringType == typeof(PaperdollFramePresenter)
                && call.Target.IsConstructor);
        Assert.True(viewport.Offset < inventory.Offset);
        Assert.True(inventory.Offset < presenter.Offset);

        IReadOnlyList<CompiledBranch> branches = CompiledCallGraph.ReadBranches(compose);
        Assert.Contains(
            branches,
            branch => branch.OpCode.FlowControl == System.Reflection.Emit.FlowControl.Cond_Branch
                && branch.Offset > viewport.Offset
                && branch.Offset < inventory.Offset
                && branch.TargetOffset > presenter.Offset);
        Assert.Contains(
            branches,
            branch => branch.OpCode.FlowControl == System.Reflection.Emit.FlowControl.Cond_Branch
                && branch.Offset > inventory.Offset
                && branch.Offset < presenter.Offset
                && branch.TargetOffset > presenter.Offset);
        Assert.DoesNotContain(
            CompiledCallGraph.ReadStringLiterals(compose),
            value => value == "Paperdoll inventory frame is required.");
    }

    [Fact]
    public void TerrainAndFrameDiagnostics_AreComposedAsOneFocusedOwner()
    {
        MethodInfo compose = RequiredMethod(
            typeof(FrameRootCompositionPhase),
            "ComposeCore");
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(compose);
        AssertCallOrder(
            compose,
            (typeof(TerrainDrawDiagnosticsController), ".ctor"),
            (typeof(WorldScenePassExecutor), ".ctor"));
        Assert.DoesNotContain(
            calls,
            call => call.Target.DeclaringType == typeof(TerrainDrawDiagnosticsController)
                && (call.Target.Name == nameof(TerrainDrawDiagnosticsController.Begin)
                    || call.Target.Name == nameof(TerrainDrawDiagnosticsController.Complete)));
    }

    private static MethodInfo RequiredMethod(Type type, string name) =>
        type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(type.FullName, name);

    private static MethodBase MethodConstructing(Type owner, Type constructed) =>
        Assert.Single(
            owner.GetMethods(Declared)
                .Cast<MethodBase>()
                .Concat(owner.GetConstructors(Declared))
                .Where(method => method.GetMethodBody() is not null),
            method => Constructs(method, constructed));

    private static MethodBase ReferencedMethodConstructing(
        MethodBase owner,
        Type constructed) =>
        Assert.Single(
            CompiledCallGraph.ReadMethodReferences(owner)
                .Select(reference => reference.Target)
                .Where(method => method.GetMethodBody() is not null)
                .Distinct(),
            method => Constructs(method, constructed));

    private static bool Constructs(MethodBase method, Type type) =>
        method.GetMethodBody() is not null
        && CompiledCallGraph.Read(method).Any(call =>
            call.Target.DeclaringType == type && call.Target.IsConstructor);

    private static void AssertCallOrder(
        MethodBase method,
        params (Type Type, string Method)[] expected)
    {
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(method);
        int cursor = -1;
        foreach ((Type type, string name) in expected)
        {
            int found = calls
                .Select((call, index) => (call, index))
                .Where(pair => pair.index > cursor)
                .Select(pair => pair.call)
                .Select((call, index) => (call, index: index + cursor + 1))
                .FirstOrDefault(
                    pair => MatchesType(pair.call.Target.DeclaringType, type)
                        && pair.call.Target.Name == name,
                    defaultValue: (default, -1))
                .index;
            Assert.True(
                found > cursor,
                $"Missing compiled edge after index {cursor}: {type.FullName}.{name}.");
            cursor = found;
        }
    }

    private static bool MatchesType(Type? actual, Type expected) =>
        actual == expected
        || expected.IsGenericTypeDefinition
            && actual?.IsGenericType == true
            && actual.GetGenericTypeDefinition() == expected;

    private static void AssertLabelOrder(
        IReadOnlyList<string> labels,
        params string[] expected)
    {
        int cursor = -1;
        foreach (string label in expected)
        {
            int found = Enumerable.Range(cursor + 1, labels.Count - cursor - 1)
                .FirstOrDefault(index => labels[index] == label, -1);
            Assert.True(found > cursor, $"Missing shutdown label after {cursor}: {label}.");
            cursor = found;
        }
    }
}
