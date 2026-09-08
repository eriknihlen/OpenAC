using System.Reflection;
using System.Reflection.Emit;
using AcDream.App.Combat;
using AcDream.App.Composition;
using AcDream.App.Diagnostics;
using AcDream.App.Input;
using AcDream.App.Net;
using AcDream.App.Plugins;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Sky;
using AcDream.App.Settings;
using AcDream.App.Streaming;
using AcDream.App.Tests.Architecture;
using AcDream.App.UI;
using AcDream.App.Update;
using AcDream.App.World;
using AcDream.Runtime.Session;
using Silk.NET.Windowing;

namespace AcDream.App.Tests.Rendering;

public sealed class GameWindowHostBoundaryTests
{
    private const BindingFlags Declared = BindingFlags.Instance
        | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.DeclaredOnly;

    [Fact]
    public void Run_PreservesNativeAttributeAndCallbackOrder()
    {
        MethodInfo run = RequiredMethod(typeof(GameWindow), nameof(GameWindow.Run));
        AssertCallOrder(
            run,
            (typeof(RuntimeSettingsController), "get_Startup"),
            (typeof(DisplayFramePacingController),
                nameof(DisplayFramePacingController.InitializeStartup)),
            (typeof(Silk.NET.Windowing.Window), nameof(Silk.NET.Windowing.Window.Create)),
            (typeof(DisplayFramePacingController),
                nameof(DisplayFramePacingController.BindSurface)),
            (typeof(WindowCallbackTargets), ".ctor"),
            (typeof(SilkWindowCallbackBinding), nameof(SilkWindowCallbackBinding.Create)),
            (typeof(SilkWindowCallbackBinding), nameof(SilkWindowCallbackBinding.Attach)));
        AssertSilkWindowCallAfter(
            run,
            nameof(IWindow.Run),
            typeof(SilkWindowCallbackBinding),
            nameof(SilkWindowCallbackBinding.Attach));

        IReadOnlyList<CompiledCall> references =
            CompiledCallGraph.ReadMethodReferences(run);
        AssertTargetOrder(
            references,
            typeof(GameWindow),
            "OnLoad",
            "OnUpdate",
            "OnRender",
            "OnClosing",
            "OnFocusChanged",
            "OnFramebufferResize");
        Assert.DoesNotContain(
            references,
            call => call.Target.Name is "add_Load" or "add_Update" or "add_Render"
                or "add_Closing");
    }

    [Fact]
    public void SessionStart_FollowsFrameGraphPublication()
    {
        AssertOnLoadPhaseOrder(
            typeof(HostInputCameraCompositionPhase),
            typeof(ContentEffectsAudioCompositionPhase),
            typeof(SettingsDevToolsCompositionPhase),
            typeof(WorldRenderCompositionPhase),
            typeof(InteractionRetainedUiCompositionPhase),
            typeof(LivePresentationCompositionPhase),
            typeof(SessionPlayerCompositionPhase),
            typeof(FrameRootCompositionPhase),
            typeof(SessionStartCompositionPhase));

        AssertCallOrder(
            RequiredMethod(typeof(FrameRootCompositionPhase), "ComposeCore"),
            (typeof(RenderFrameOrchestrator), ".ctor"),
            (typeof(UpdateFrameOrchestrator), ".ctor"),
            (typeof(GameFrameGraphSlot), nameof(GameFrameGraphSlot.PublishOwned)),
            (typeof(IGameWindowFrameRootPublication),
                nameof(IGameWindowFrameRootPublication.PublishFrameRoots)));

        MethodInfo complete = RequiredMethod(
            typeof(SessionPlayerCompositionPhase),
            "CompleteSessionPlayer");
        AssertCallOrder(
            complete,
            (typeof(LiveSessionRuntimeFactory), nameof(LiveSessionRuntimeFactory.Create)),
            (typeof(LiveCombatModeCommandSlot), "BindOwned"),
            (typeof(RuntimeDiagnosticCommandSlot), "BindOwned"),
            (typeof(GameplayInputActionRouter), nameof(GameplayInputActionRouter.Create)),
            (typeof(GameplayInputActionRouter), nameof(GameplayInputActionRouter.Attach)),
            (typeof(IGameWindowSessionPlayerPublication),
                nameof(IGameWindowSessionPlayerPublication.PublishSessionPlayer)));

        MethodInfo start = RequiredMethod(typeof(SessionStartCompositionPhase), "Start");
        IReadOnlyList<CompiledCall> startCalls = CompiledCallGraph.Read(start);
        Assert.Contains(startCalls, call => call.Target.Name == "Start");
        Assert.Contains(startCalls, call => call.Target.Name == "Report");
    }

    [Fact]
    public void WorldEnvironment_IsOwnedAndGameWindowOnlyComposesItsTypedEdges()
    {
        AssertField(typeof(GameWindow), "_worldEnvironment", typeof(WorldEnvironmentController));

        MethodBase worldDelegate = OnLoadMethodConstructing(
            typeof(WorldRenderCompositionPhase));
        Assert.Contains(
            CompiledCallGraph.ReadFieldReferences(worldDelegate),
            reference => reference.Field.Name == "_worldEnvironment");

        MethodInfo compose = RequiredMethod(typeof(WorldRenderCompositionPhase), "Compose");
        AssertCallOrder(
            compose,
            (typeof(IWorldRenderCompositionFactory),
                nameof(IWorldRenderCompositionFactory.LoadRegion)),
            (typeof(IWorldRenderCompositionFactory),
                nameof(IWorldRenderCompositionFactory.InitializeEnvironment)));
        Assert.Contains(
            CompiledCallGraph.Read(RequiredMethod(
                typeof(RetailWorldRenderCompositionFactory),
                nameof(RetailWorldRenderCompositionFactory.InitializeEnvironment))),
            call => call.Target.DeclaringType == typeof(WorldEnvironmentController)
                && call.Target.Name == nameof(WorldEnvironmentController.Initialize));

        MethodInfo router = RequiredMethod(typeof(LiveSessionRuntimeFactory), "CreateEventRouter");
        IReadOnlyList<CompiledCall> routerReferences =
            CompiledCallGraph.ReadMethodReferences(router);
        Assert.Contains(
            routerReferences,
            call => call.Target.DeclaringType == typeof(LiveEnvironmentSessionSink)
                && call.Target.IsConstructor);
        AssertTargetOrder(
            routerReferences,
            typeof(WorldEnvironmentController),
            nameof(WorldEnvironmentController.ApplyAdminEnvirons),
            nameof(WorldEnvironmentController.SynchronizeFromServer));

        AssertMembersAbsent(
            typeof(GameWindow),
            ["_loadedSkyDesc", "_loadedSkyDayIndex"],
            ["RefreshSkyForCurrentDay", "OnEnvironChanged", "CycleTimeOfDay", "CycleWeather"]);

        MethodInfo complete = RequiredMethod(
            typeof(SessionPlayerCompositionPhase),
            "CompleteSessionPlayer");
        AssertCallOrder(
            complete,
            (typeof(RuntimeDiagnosticCommandController), ".ctor"),
            (typeof(RuntimeDiagnosticCommandSlot), "BindOwned"));
    }

    [Fact]
    public void InputAction_IsOneTypedOwnerHandoff()
    {
        AssertMembersAbsent(
            typeof(GameWindow),
            [],
            ["OnInputAction", "SetInputCombatScope", "ToggleLiveCombatMode",
                "OnUiDragReleasedOutside"]);
        Assert.DoesNotContain(
            CompiledCallGraph.ReadDeclared(typeof(GameWindow)),
            call => call.Target.Name == "add_DragReleasedOutsideUi");

        MethodInfo session = RequiredMethod(
            typeof(SessionPlayerCompositionPhase),
            "CompleteSessionPlayer");
        AssertSingleCall(session, typeof(GameplayInputActionRouter),
            nameof(GameplayInputActionRouter.Create));
        AssertSingleCall(session, typeof(GameplayInputActionRouter),
            nameof(GameplayInputActionRouter.Attach));

        MethodInfo live = RequiredMethod(
            typeof(LivePresentationCompositionPhase),
            "CompletePresentation");
        MethodBase retainedFactory = ReferencedMethodConstructingOrCalling(
            live,
            typeof(RetainedUiGameplayBinding),
            nameof(RetainedUiGameplayBinding.Create));
        AssertSingleCall(retainedFactory, typeof(RetainedUiGameplayBinding),
            nameof(RetainedUiGameplayBinding.Create));
        Assert.Contains(
            CompiledCallGraph.Read(live),
            call => call.Target.DeclaringType == typeof(RetainedUiGameplayBinding)
                && call.Target.Name == nameof(RetainedUiGameplayBinding.Attach));

        IReadOnlyList<string> labels = ShutdownLabels();
        AssertLabelOrder(
            labels,
            "combat command slot",
            "diagnostic command slot",
            "retained gameplay",
            "gameplay actions",
            "game runtime session",
            "physical ingress cleanup",
            "retained gameplay",
            "gameplay actions",
            "camera pointer",
            "session dependents",
            "mouse capture");
    }

    [Fact]
    public void FramebufferResize_IsOneTypedOwnerHandoff()
    {
        MethodInfo resize = RequiredMethod(typeof(GameWindow), "OnFramebufferResize");
        AssertSingleCall(
            resize,
            typeof(FramebufferResizeController),
            nameof(FramebufferResizeController.Resize));
        Assert.DoesNotContain(
            CompiledCallGraph.Read(resize),
            call => call.Target.Name is "Viewport" or "SetAspect" or "ResetLayout");

        AssertCallOrder(
            RequiredMethod(typeof(HostInputCameraCompositionPhase), "ComposeCore"),
            (typeof(FramebufferResizeController),
                nameof(FramebufferResizeController.BindViewport)),
            (typeof(IGameWindowHostInputCameraPublication),
                nameof(IGameWindowHostInputCameraPublication.PublishCameraController)),
            (typeof(FramebufferResizeController),
                nameof(FramebufferResizeController.BindCamera)),
            (typeof(FramebufferResizeController), nameof(FramebufferResizeController.Resize)));

        Assert.DoesNotContain(
            CompiledCallGraph.ReadMethodReferences(RequiredMethod(typeof(GameWindow), "OnLoad")),
            call => call.Target.Name == "Update"
                && call.Target.DeclaringType?.Name == "ViewportAspectState");
    }

    [Fact]
    public void RuntimeSettings_IsOneTwoPhaseOwnerWithoutWindowStateMirrors()
    {
        AssertField(typeof(GameWindow), "_runtimeSettings", typeof(RuntimeSettingsController));
        AssertSingleCall(
            FindConstructorConstructing(typeof(GameWindow), typeof(RuntimeSettingsController)),
            typeof(RuntimeSettingsController),
            ".ctor");
        Assert.DoesNotContain(
            CompiledCallGraph.ReadDeclared(typeof(GameWindow)),
            call => call.Target.DeclaringType?.Name == "SettingsStore"
                || call.Target.Name is "LoadDisplay" or "LoadAudio" or "LoadGameplay"
                    or "LoadChat" or "LoadCharacter");
        AssertMembersAbsent(
            typeof(GameWindow),
            ["_persistedDisplay", "_persistedAudio", "_persistedGameplay",
                "_persistedChat", "_persistedCharacter", "_activeToonKey",
                "_settingsStore", "_settingsVm"],
            ["LoadAndApplyPersistedSettings", "ApplyDisplayWindowState",
                "ReapplyQualityPreset"]);

        MethodInfo session = RequiredMethod(
            typeof(SessionPlayerCompositionPhase),
            "ComposeCore");
        Assert.Contains(
            CompiledCallGraph.Read(session),
            call => call.Target.DeclaringType == typeof(RuntimeSettingsController)
                && call.Target.Name == nameof(RuntimeSettingsController.BindRuntimeTargetsOwned));
        Assert.Contains(
            CompiledCallGraph.Read(RequiredMethod(
                typeof(SettingsDevToolsCompositionPhase),
                "Compose")),
            call => call.Target.DeclaringType == typeof(RuntimeSettingsController)
                && call.Target.Name == nameof(RuntimeSettingsController.ApplyStartup));

        MethodInfo acquireAtlas = RequiredMethod(
            typeof(RetailWorldRenderCompositionFactory),
            nameof(RetailWorldRenderCompositionFactory.AcquireBackendNeutralTerrainAtlas));
        Assert.Contains(
            CompiledCallGraph.Read(acquireAtlas),
            call => call.Target.DeclaringType == typeof(IGameRenderResourceLifetime)
                && call.Target.Name == nameof(IGameRenderResourceLifetime.AcquireTerrainAtlas));
        MethodBase atlasFactory = ReferencedMethodConstructingOrCalling(
            acquireAtlas,
            typeof(TerrainAtlas),
            nameof(TerrainAtlas.BuildBackendNeutral));
        Assert.NotNull(atlasFactory);

        AssertLabelOrder(
            ShutdownLabels(),
            "physical ingress cleanup",
            "frame borrowers",
            "frame-root bindings",
            "retail UI",
            "streamer",
            "mesh draw dispatcher",
            "terrain");
    }

    [Fact]
    public void UpdateRenderFocusAndCloseRemainNarrowHostEdges()
    {
        MethodInfo update = RequiredMethod(typeof(GameWindow), "OnUpdate");
        AssertSingleCall(update, typeof(GameFrameGraphSlot), nameof(GameFrameGraphSlot.Tick));

        MethodInfo render = RequiredMethod(typeof(GameWindow), "OnRender");
        AssertSilkWindowCallBefore(render, "get_Size", typeof(RenderFrameInput), ".ctor");
        AssertCallOrder(
            render,
            (typeof(RenderFrameInput), ".ctor"),
            (typeof(GameFrameGraphSlot), nameof(GameFrameGraphSlot.Render)));
        Assert.DoesNotContain(
            CompiledCallGraph.Read(render),
            call => call.Target.Name == "get_FramebufferSize");

        AssertOnlyProductionCall(
            RequiredMethod(typeof(GameWindow), "OnFocusChanged"),
            typeof(CameraPointerInputController),
            nameof(CameraPointerInputController.HandleFocusChanged));
        AssertOnlyProductionCall(
            RequiredMethod(typeof(GameWindow), "OnClosing"),
            typeof(GameWindow),
            "CompleteShutdown");

        MethodInfo shutdown = RequiredMethod(typeof(GameWindow), "CompleteShutdown");
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(shutdown);
        Assert.Contains(calls, call => call.Target.DeclaringType == typeof(GameWindowLifetime)
            && call.Target.Name == "get_HasShutdownRoots");
        Assert.Contains(calls, call => call.Target.DeclaringType == typeof(GameWindow)
            && call.Target.Name == "CaptureShutdownRoots");
        Assert.Contains(calls, call => call.Target.DeclaringType == typeof(GameWindowLifetime)
            && call.Target.Name == nameof(GameWindowLifetime.PublishShutdownRoots));
        Assert.Contains(calls, call => call.Target.DeclaringType == typeof(GameWindowLifetime)
            && call.Target.Name == nameof(GameWindowLifetime.CompleteAndReleaseNativeWindow));
        Assert.Contains(calls, call => call.Target.DeclaringType == typeof(GameWindowLifetime)
            && call.Target.Name == nameof(GameWindowLifetime.TryComplete));
        Assert.DoesNotContain(calls, call => call.Target.DeclaringType?.Name == "ResourceShutdownStage"
            || call.Target.Name == "CompleteOrThrow");
    }

    [Fact]
    public void Shutdown_PreservesDependencyStagesAndNativeWindowLast()
    {
        IReadOnlyList<string> labels = ShutdownLabels();
        string[] stages =
        [
            "host and session barriers",
            "physical ingress cleanup",
            "plugin host",
            "frame borrowers",
            "session dependents",
            "live entities",
            "effect dispatch edges",
            "live entity dependents",
            "submitted GPU work",
            "render frontends",
            "game runtime root",
            "shared texture owners",
            "mesh adapter",
            "remaining render owners",
            "dedicated render resources",
            "failed render construction cleanup",
            "frame flight owner",
            "content mappings",
            "input context",
            "graphics API context",
        ];
        AssertLabelOrder(labels, stages);
        AssertLabelOrder(
            labels,
            "combat command slot",
            "diagnostic command slot",
            "retained gameplay",
            "gameplay actions",
            "game runtime session",
            "physical ingress cleanup");

        Assert.Equal(typeof(UiHost),
            Nullable.GetUnderlyingType(typeof(IngressShutdownRoots)
                .GetProperty("RetainedUiHost", Declared)?.PropertyType
                ?? throw new MissingMemberException())
            ?? typeof(IngressShutdownRoots)
                .GetProperty("RetainedUiHost", Declared)!.PropertyType);
        Assert.Equal(typeof(IDisposable),
            typeof(IngressShutdownRoots).GetProperty("Plugins", Declared)?.PropertyType);
        IReadOnlyList<CompiledFieldReference> captureFields =
            CompiledCallGraph.ReadFieldReferences(
                RequiredMethod(typeof(GameWindow), "CaptureShutdownRoots"));
        Assert.Contains(captureFields, field => field.Field.Name == "_uiHost");
        Assert.Contains(captureFields, field => field.Field.Name == "_pluginSession");

        MethodBase entry = typeof(GameWindow).Assembly.GetTypes()
            .Where(type => type.Name == "Program")
            .SelectMany(type => type.GetMethods(Declared))
            .Single(method => CompiledCallGraph.Read(method).Any(call =>
                call.Target.DeclaringType == typeof(GraphicalPluginSession)
                && call.Target.Name == nameof(GraphicalPluginSession.Create)));
        AssertCallOrder(
            entry,
            (typeof(GraphicalPluginSession), nameof(GraphicalPluginSession.Create)),
            (typeof(GameWindow), nameof(GameWindow.StartPluginHosting)),
            (typeof(GameWindow), nameof(GameWindow.Run)));

        MethodInfo attach = RequiredMethod(typeof(GameWindow), nameof(GameWindow.StartPluginHosting));
        CompiledFieldReference store = Assert.Single(
            CompiledCallGraph.ReadFieldReferences(attach),
            reference => reference.Field.Name == "_pluginSession"
                && reference.OpCode == OpCodes.Stfld);
        CompiledCall start = Assert.Single(
            CompiledCallGraph.Read(attach),
            call => call.Target.DeclaringType == typeof(GraphicalPluginSession)
                && call.Target.Name == nameof(GraphicalPluginSession.Start));
        Assert.True(store.Offset < start.Offset);

        AssertLabelOrder(labels, "plugins", "retail UI", "game runtime");
        AssertCallOrder(
            RequiredMethod(
                typeof(GameWindowLifetime),
                nameof(GameWindowLifetime.CompleteAndReleaseNativeWindow)),
            (typeof(GameWindowLifetime), nameof(GameWindowLifetime.TryComplete)),
            (typeof(GameWindowLifetime), "ReleaseNativeWindow"));

        MethodInfo dispose = RequiredMethod(typeof(GameWindow), nameof(GameWindow.Dispose));
        CompiledCall complete = Assert.Single(
            CompiledCallGraph.Read(dispose),
            call => call.Target.DeclaringType == typeof(GameWindow)
                && call.Target.Name == "CompleteShutdown");
        CompiledFieldReference clear = Assert.Single(
            CompiledCallGraph.ReadFieldReferences(dispose),
            reference => reference.Field.Name == "_window"
                && reference.OpCode == OpCodes.Stfld);
        Assert.True(complete.Offset < clear.Offset);
        Assert.DoesNotContain(
            CompiledCallGraph.Read(dispose),
            call => call.Target.DeclaringType == typeof(IWindow)
                && call.Target.Name == nameof(IDisposable.Dispose));
    }

    [Fact]
    public void ResourceRootsAndFramePairHaveExplicitAcquireTransferAndReleaseBoundaries()
    {
        MethodInfo live = RequiredMethod(
            typeof(LivePresentationCompositionPhase),
            "CompletePresentation");
        IReadOnlyList<CompiledCall> liveCalls = CompiledCallGraph.Read(live);
        CompiledCall acquirePrepared = Assert.Single(
            liveCalls,
            call => call.Target.Name == "AcquirePrepared");
        CompiledCall skyFactory = Assert.Single(
            CompiledCallGraph.ReadMethodReferences(live),
            call => call.Offset > acquirePrepared.Offset
                && call.Target.GetMethodBody() is not null
                && Constructs(call.Target, typeof(SkyRenderer)));
        Assert.True(acquirePrepared.Offset < skyFactory.Offset);
        Assert.NotNull(ReferencedMethodConstructingOrCalling(
            live,
            typeof(PortalTunnelPresentation),
            nameof(PortalTunnelPresentation.PrepareResources)));

        AssertOnLoadPhaseOrder(
            typeof(WorldRenderCompositionPhase),
            typeof(LivePresentationCompositionPhase),
            typeof(SessionPlayerCompositionPhase),
            typeof(FrameRootCompositionPhase));
        AssertCallOrder(
            RequiredMethod(typeof(FrameRootCompositionPhase), "ComposeCore"),
            (typeof(RenderFrameOrchestrator), ".ctor"),
            (typeof(UpdateFrameOrchestrator), ".ctor"),
            (typeof(GameFrameGraphSlot), nameof(GameFrameGraphSlot.PublishOwned)));

        MethodInfo session = RequiredMethod(
            typeof(SessionPlayerCompositionPhase),
            "CompleteSessionPlayer");
        AssertCallOrder(
            session,
            (typeof(TransferableResourceSlot<>), "Transfer"),
            (typeof(IGameWindowSessionPlayerPublication),
                nameof(IGameWindowSessionPlayerPublication.PublishSessionPlayer)));
        MethodBase teleportFactory = ReferencedMethodConstructingOrCalling(
            session,
            typeof(LocalPlayerTeleportPresentation),
            ".ctor");
        Assert.NotNull(ReferencedMethodConstructingOrCalling(
            teleportFactory,
            typeof(LocalPlayerTeleportController),
            ".ctor"));

        MethodInfo atlas = RequiredMethod(
            typeof(RetailWorldRenderCompositionFactory),
            nameof(RetailWorldRenderCompositionFactory.AcquireBackendNeutralTerrainAtlas));
        Assert.NotNull(ReferencedMethodConstructingOrCalling(
            atlas,
            typeof(TerrainAtlas),
            nameof(TerrainAtlas.BuildBackendNeutral)));

        IReadOnlyList<string> labels = ShutdownLabels();
        AssertLabelOrder(
            labels,
            "world frame composition",
            "frame-root bindings",
            "retail UI",
            "portal tunnel",
            "sky",
            "terrain",
            "terrain atlas",
            "resource construction ledger",
            "graphics API");

        MethodInfo onLoad = RequiredMethod(typeof(GameWindow), "OnLoad");
        IReadOnlyList<CompiledCall> loadCalls =
            CompiledCallGraph.ReadMethodReferences(onLoad);
        Assert.DoesNotContain(loadCalls, call =>
            call.Target.DeclaringType == typeof(RetailUiRuntime)
                && call.Target.Name == "Mount");
        Assert.DoesNotContain(loadCalls, call =>
            call.Target.DeclaringType == typeof(PortalTunnelPresentation)
                && call.Target.Name == nameof(PortalTunnelPresentation.PrepareResources));
        AssertMembersAbsent(
            typeof(GameWindow),
            ["_portalTunnel", "_renderFrameOrchestrator", "_updateFrameOrchestrator"],
            []);

        AssertCallOrder(
            RequiredMethod(typeof(GameWindow), nameof(GameWindow.Run)),
            (typeof(ResourceConstructionCleanupLedger),
                nameof(ResourceConstructionCleanupLedger.RetainFrom)));
        AssertSilkWindowCallBefore(
            RequiredMethod(typeof(GameWindow), nameof(GameWindow.Run)),
            nameof(IWindow.Run),
            typeof(ResourceConstructionCleanupLedger),
            nameof(ResourceConstructionCleanupLedger.RetainFrom));
        Assert.Contains(
            CompiledCallGraph.ReadFieldReferences(
                RequiredMethod(typeof(GameWindow), "CaptureShutdownRoots")),
            reference => reference.Field.Name == "_constructionCleanup");
        Assert.Contains(labels, label => label == "resource construction ledger");
    }

    private static MethodInfo RequiredMethod(Type type, string name) =>
        type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(type.FullName, name);

    private static ConstructorInfo FindConstructorConstructing(Type owner, Type constructed) =>
        Assert.Single(
            owner.GetConstructors(Declared),
            constructor => Constructs(constructor, constructed));

    private static void AssertField(Type owner, string name, Type expectedType)
    {
        FieldInfo field = owner.GetField(name, Declared)
            ?? throw new MissingFieldException(owner.FullName, name);
        Assert.Equal(expectedType, field.FieldType);
    }

    private static void AssertMembersAbsent(
        Type owner,
        IReadOnlyCollection<string> fields,
        IReadOnlyCollection<string> methods)
    {
        Assert.DoesNotContain(owner.GetFields(Declared), field => fields.Contains(field.Name));
        Assert.DoesNotContain(owner.GetMethods(Declared), method => methods.Contains(method.Name));
    }

    private static void AssertOnLoadPhaseOrder(params Type[] phases)
    {
        IReadOnlyList<CompiledCall> references = CompiledCallGraph.ReadMethodReferences(
            RequiredMethod(typeof(GameWindow), "OnLoad"));
        int cursor = -1;
        foreach (Type phase in phases)
        {
            CompiledCall found = references
                .Where(reference => reference.Offset > cursor)
                .FirstOrDefault(reference => Constructs(reference.Target, phase));
            Assert.NotNull(found.Target);
            Assert.True(found.Offset > cursor, $"Missing OnLoad phase: {phase.Name}.");
            cursor = found.Offset;
        }
    }

    private static MethodBase OnLoadMethodConstructing(Type type) =>
        Assert.Single(
            CompiledCallGraph.ReadMethodReferences(
                    RequiredMethod(typeof(GameWindow), "OnLoad"))
                .Select(reference => reference.Target)
                .Where(method => method.GetMethodBody() is not null)
                .Distinct(),
            method => Constructs(method, type));

    private static bool Constructs(MethodBase method, Type type) =>
        method.GetMethodBody() is not null
        && CompiledCallGraph.Read(method).Any(call =>
            call.Target.DeclaringType == type && call.Target.IsConstructor);

    private static MethodBase ReferencedMethodConstructingOrCalling(
        MethodBase owner,
        Type type,
        string methodName) =>
        Assert.Single(
            CompiledCallGraph.ReadMethodReferences(owner)
                .Select(reference => reference.Target)
                .Where(method => method.GetMethodBody() is not null)
                .Distinct(),
            method => CompiledCallGraph.Read(method).Any(call =>
                call.Target.DeclaringType == type
                && call.Target.Name == methodName));

    private static void AssertSingleCall(MethodBase owner, Type type, string name) =>
        Assert.Single(
            CompiledCallGraph.Read(owner),
            call => MatchesType(call.Target.DeclaringType, type)
                && call.Target.Name == name);

    private static void AssertOnlyProductionCall(MethodBase owner, Type type, string name)
    {
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(owner)
            .Where(call => call.Target.DeclaringType?.Namespace?.StartsWith("AcDream",
                StringComparison.Ordinal) == true)
            .ToArray();
        CompiledCall call = Assert.Single(calls);
        Assert.True(MatchesType(call.Target.DeclaringType, type));
        Assert.Equal(name, call.Target.Name);
    }

    private static void AssertSilkWindowCallAfter(
        MethodBase owner,
        string silkMethod,
        Type predecessorType,
        string predecessorMethod)
    {
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(owner);
        CompiledCall predecessor = Assert.Single(calls, call =>
            call.Target.DeclaringType == predecessorType
            && call.Target.Name == predecessorMethod);
        CompiledCall silk = Assert.Single(calls, call =>
            call.Target.DeclaringType?.Namespace == "Silk.NET.Windowing"
            && call.Target.Name == silkMethod);
        Assert.True(predecessor.Offset < silk.Offset);
    }

    private static void AssertSilkWindowCallBefore(
        MethodBase owner,
        string silkMethod,
        Type successorType,
        string successorMethod)
    {
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(owner);
        CompiledCall silk = Assert.Single(calls, call =>
            call.Target.DeclaringType?.Namespace == "Silk.NET.Windowing"
            && call.Target.Name == silkMethod);
        CompiledCall successor = Assert.Single(calls, call =>
            call.Target.DeclaringType == successorType
            && call.Target.Name == successorMethod);
        Assert.True(silk.Offset < successor.Offset);
    }

    private static void AssertCallOrder(
        MethodBase method,
        params (Type Type, string Method)[] expected)
    {
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(method);
        int cursor = -1;
        foreach ((Type type, string name) in expected)
        {
            int found = Enumerable.Range(cursor + 1, calls.Count - cursor - 1)
                .FirstOrDefault(index =>
                    MatchesType(calls[index].Target.DeclaringType, type)
                    && calls[index].Target.Name == name,
                    -1);
            Assert.True(found > cursor,
                $"Missing compiled edge after {cursor}: {type.FullName}.{name}. "
                + $"Observed: {string.Join(", ", calls.Select(call =>
                    $"{call.Target.DeclaringType?.Name}.{call.Target.Name}"))}");
            cursor = found;
        }
    }

    private static void AssertTargetOrder(
        IReadOnlyList<CompiledCall> calls,
        Type type,
        params string[] names)
    {
        int cursor = -1;
        foreach (string name in names)
        {
            int found = Enumerable.Range(cursor + 1, calls.Count - cursor - 1)
                .FirstOrDefault(index => calls[index].Target.DeclaringType == type
                    && calls[index].Target.Name == name, -1);
            Assert.True(found > cursor,
                $"Missing referenced target after {cursor}: {type.FullName}.{name}.");
            cursor = found;
        }
    }

    private static bool MatchesType(Type? actual, Type expected) =>
        actual == expected
        || expected.IsGenericTypeDefinition
            && actual?.IsGenericType == true
            && actual.GetGenericTypeDefinition() == expected;

    private static IReadOnlyList<string> ShutdownLabels() =>
        CompiledCallGraph.ReadStringLiterals(RequiredMethod(
            typeof(GameWindowShutdownManifest),
            nameof(GameWindowShutdownManifest.Create)));

    private static void AssertLabelOrder(
        IReadOnlyList<string> labels,
        params string[] expected)
    {
        int cursor = -1;
        foreach (string label in expected)
        {
            int found = Enumerable.Range(cursor + 1, labels.Count - cursor - 1)
                .FirstOrDefault(index => labels[index] == label, -1);
            Assert.True(found > cursor,
                $"Missing shutdown label after {cursor}: {label}.");
            cursor = found;
        }
    }
}
