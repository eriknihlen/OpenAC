using System.Numerics;
using System.Reflection;
using AcDream.App.Composition;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.Tests.Architecture;
using AcDream.App.World;
using AcDream.Core.Lighting;
using AcDream.Core.Physics;
using AcDream.Core.Rendering;
using AcDream.Core.World;
using AcDream.Core.World.Cells;

namespace AcDream.App.Tests.Rendering;

[Collection(CameraDiagnosticsCollection.Name)]
public sealed class WorldRenderFrameBuilderTests
{
    [Fact]
    public void Build_orders_world_preparation_and_combines_borrowed_results()
    {
        List<string> calls = [];
        var camera = new FlyCamera { Position = new Vector3(11f, 22f, 33f) };
        var cameraFrame = new WorldCameraFrame(
            camera,
            camera.Projection,
            camera.View * camera.Projection,
            default,
            Matrix4x4.Identity,
            camera.Position);
        WorldRootFrame roots = default;
        var animated = new HashSet<uint> { 41u, 42u };
        var buildings = new List<LoadedCell>();
        var foundation = new RenderFrameFoundation(
            PortalViewportVisible: false,
            Sky: default,
            Atmosphere: default);
        var visibility = new RecordingVisibility(calls);
        var environment = new RecordingEnvironment(calls);
        var builder = new WorldRenderFrameBuilder(
            new RecordingCamera(calls, cameraFrame),
            visibility,
            new RecordingSettings(calls),
            new RecordingRoots(calls, roots),
            environment,
            new RecordingAnimated(calls, animated),
            new RecordingBuildings(calls, new WorldBuildingFrame(null, buildings)),
            new RecordingMembership());

        WorldRenderFrame result = builder.Build(
            in foundation,
            waitingForLogin: true,
            activeDayGroup: null);

        Assert.Equal(
            [
                "visibility:capture",
                "camera",
                "visibility:begin",
                "settings",
                "roots",
                "environment",
                "visibility:projection",
                "animated",
                "buildings",
            ],
            calls);
        Assert.Same(camera, result.Camera.Camera);
        Assert.Same(animated, result.AnimatedEntityIds);
        Assert.Same(buildings, result.Buildings.NearbyBuildingCells);
        Assert.Null(result.ClipRoot);
        Assert.True(visibility.WaitingForLogin);
        Assert.Equal(foundation, environment.Foundation);
    }

    [Fact]
    public void Required_sources_fail_fast()
    {
        var camera = new RecordingCamera([], default);
        var visibility = new RecordingVisibility([]);
        var settings = new RecordingSettings([]);
        var roots = new RecordingRoots([], default);
        var environment = new RecordingEnvironment([]);
        var animated = new RecordingAnimated([], []);
        var buildings = new RecordingBuildings([], default);
        var membership = new RecordingMembership();

        Assert.Throws<ArgumentNullException>(() =>
            new WorldRenderFrameBuilder(null!, visibility, settings, roots, environment, animated, buildings, membership));
        Assert.Throws<ArgumentNullException>(() =>
            new WorldRenderFrameBuilder(camera, null!, settings, roots, environment, animated, buildings, membership));
        Assert.Throws<ArgumentNullException>(() =>
            new WorldRenderFrameBuilder(camera, visibility, null!, roots, environment, animated, buildings, membership));
        Assert.Throws<ArgumentNullException>(() =>
            new WorldRenderFrameBuilder(camera, visibility, settings, null!, environment, animated, buildings, membership));
        Assert.Throws<ArgumentNullException>(() =>
            new WorldRenderFrameBuilder(camera, visibility, settings, roots, null!, animated, buildings, membership));
        Assert.Throws<ArgumentNullException>(() =>
            new WorldRenderFrameBuilder(camera, visibility, settings, roots, environment, null!, buildings, membership));
        Assert.Throws<ArgumentNullException>(() =>
            new WorldRenderFrameBuilder(camera, visibility, settings, roots, environment, animated, null!, membership));
        Assert.Throws<ArgumentNullException>(() =>
            new WorldRenderFrameBuilder(camera, visibility, settings, roots, environment, animated, buildings, null!));
    }

    [Fact]
    public void Render_range_state_updates_both_tiers_without_replacing_the_source()
    {
        IWorldRenderRangeSource source = new WorldRenderRangeState(4, 12);
        var mutable = Assert.IsType<WorldRenderRangeState>(source);

        mutable.NearRadius = 7;
        mutable.FarRadius = 19;

        Assert.Equal(7, source.NearRadius);
        Assert.Equal(19, source.FarRadius);
    }

    [Fact]
    public void Runtime_animated_source_reuses_scratch_and_removes_stale_ids()
    {
        var slot = new LiveEntityRuntimeSlot();
        var live = new LiveEntityAnimationRuntimeView<LiveEntityAnimationState>(slot);
        var source = new RuntimeWorldFrameAnimatedEntitySource(
            live,
            statics: null,
            equipped: null);

        HashSet<uint> first = source.Capture();
        first.Add(0xDEADBEEFu);
        HashSet<uint> second = source.Capture();

        Assert.Same(first, second);
        Assert.Empty(second);
    }

    [Fact]
    public void Runtime_building_source_reuses_scratch_and_rebuilds_outdoor_root()
    {
        var pipeline = new LandblockPresentationPipeline(
            publishBeforeSpatialCommit: (_, _) => { },
            state: new GpuWorldState());
        var source = new RuntimeWorldFrameBuildingSource(
            pipeline,
            new CellVisibility());
        FrustumPlanes frustum = default;

        WorldBuildingFrame first = source.Gather(
            viewerRoot: null,
            viewerCellId: 0x01010001u,
            in frustum);
        var scratch = Assert.IsType<List<LoadedCell>>(first.NearbyBuildingCells);
        scratch.Add(new LoadedCell { CellId = 0x01010100u });
        WorldBuildingFrame second = source.Gather(
            viewerRoot: null,
            viewerCellId: 0x02020001u,
            in frustum);

        Assert.Same(scratch, second.NearbyBuildingCells);
        Assert.Empty(second.NearbyBuildingCells);
        Assert.Equal(0x01010001u, first.OutdoorNode?.CellId);
        Assert.Equal(0x02020001u, second.OutdoorNode?.CellId);
    }

    [Fact]
    public void Runtime_root_source_keeps_player_lighting_and_viewer_render_cells_distinct()
    {
        const uint playerCellId = 0x01010100u;
        const uint viewerCellId = 0x01010101u;
        var physics = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        physics.DataCache.CellGraph.Add(CoreCell(playerCellId, seenOutside: false));
        physics.UpdatePlayerCurrCell(playerCellId);
        var cells = new CellVisibility();
        var playerCell = new LoadedCell { CellId = playerCellId, SeenOutside = false };
        var viewerCell = new LoadedCell { CellId = viewerCellId, SeenOutside = true };
        cells.AddCell(playerCell);
        cells.AddCell(viewerCell);
        var retailChase = new RetailChaseCamera();
        retailChase.Update(
            playerPosition: Vector3.Zero,
            playerYaw: 0f,
            playerVelocity: Vector3.Zero,
            isOnGround: true,
            contactPlaneNormal: Vector3.UnitZ,
            dt: 1f / 60f,
            cellId: viewerCellId);
        var mode = new LocalPlayerModeState { IsPlayerMode = true };
        var chase = new ChaseCameraInputState { Retail = retailChase };
        var origin = new LiveWorldOriginState();
        origin.SetPlaceholder(0x01, 0x01);
        var source = new RuntimeWorldFrameRootSource(
            physics,
            cells,
            mode,
            chase,
            new RuntimeLocalPlayerMovementState(),
            origin);
        var camera = new FlyCamera { Position = new Vector3(5f, 6f, 7f) };
        WorldCameraFrame cameraFrame = CameraFrame(camera);
        bool previousRetailCamera = CameraDiagnostics.UseRetailChaseCamera;

        try
        {
            CameraDiagnostics.UseRetailChaseCamera = true;
            WorldRootFrame result = source.Resolve(in cameraFrame);

            Assert.Same(playerCell, result.PlayerRoot);
            Assert.Same(viewerCell, result.ViewerRoot);
            Assert.True(result.PlayerInsideCell);
            Assert.True(result.CameraInsideCell);
            Assert.False(result.PlayerSeenOutside);
            Assert.True(result.RootSeenOutside);
            Assert.True(result.RenderSky);
            Assert.False(result.CameraInsideEnclosedCell);
            Assert.True(result.PlayerOrCameraInsideEnclosedCell);
            Assert.True(result.IsAtmosphericallyOutdoor);
            Assert.Equal(playerCellId, result.PlayerCellId);
            Assert.Equal(viewerCellId, result.ViewerCellId);
            Assert.Equal(camera.Position, result.ViewerEyePosition);
        }
        finally
        {
            CameraDiagnostics.UseRetailChaseCamera = previousRetailCamera;
        }
    }

    [Fact]
    public void Runtime_root_source_preserves_null_root_fallback_before_player_membership()
    {
        var physics = new PhysicsEngine();
        var source = new RuntimeWorldFrameRootSource(
            physics,
            new CellVisibility(),
            new LocalPlayerModeState(),
            new ChaseCameraInputState(),
            new RuntimeLocalPlayerMovementState(),
            new LiveWorldOriginState());
        WorldCameraFrame camera = CameraFrame(new FlyCamera());

        WorldRootFrame result = source.Resolve(in camera);

        Assert.Null(result.PlayerRoot);
        Assert.Null(result.ViewerRoot);
        Assert.Equal(0u, result.PlayerCellId);
        Assert.Equal(0u, result.ViewerCellId);
        Assert.False(result.PlayerInsideCell);
        Assert.False(result.CameraInsideCell);
        Assert.True(result.RenderSky);
        Assert.False(result.CameraInsideEnclosedCell);
        Assert.False(result.PlayerOrCameraInsideEnclosedCell);
        Assert.True(result.IsAtmosphericallyOutdoor);
    }

    [Fact]
    public void Runtime_environment_uses_all_resident_lights()
    {
        const uint visibleCell = 0x01010100u;
        const uint hiddenCell = 0x01010101u;
        var lighting = new LightManager();
        var visibleLight = new LightSource
        {
            Kind = LightKind.Point,
            RankingOrigin = Vector3.Zero,
            CellId = visibleCell,
        };
        var hiddenLight = new LightSource
        {
            Kind = LightKind.Point,
            RankingOrigin = Vector3.Zero,
            CellId = hiddenCell,
        };
        lighting.Register(visibleLight);
        lighting.Register(hiddenLight);
        var environment = new RuntimeWorldFrameEnvironmentPreparation(
            RuntimeOptions.Parse("test-dat", _ => null),
            new WorldTimeService(SkyStateProvider.Default()),
            lighting,
            dispatcher: null,
            environmentCells: null,
            lightingUbo: null,
            new WorldRenderRangeState(4, 12),
            skyPes: null);
        WorldCameraFrame camera = CameraFrame(new FlyCamera());
        WorldRootFrame roots = default;
        RenderFrameFoundation foundation = default;

        environment.Prepare(in camera, in roots, in foundation, activeDayGroup: null);

        Assert.Contains(visibleLight, lighting.PointSnapshot);
        Assert.Contains(hiddenLight, lighting.PointSnapshot);

        environment.Prepare(in camera, in roots, in foundation, activeDayGroup: null);

        Assert.Contains(visibleLight, lighting.PointSnapshot);
        Assert.Contains(hiddenLight, lighting.PointSnapshot);
    }

    [Fact]
    public void PersistentAtDay_UsesNoonLandscapeLightingWithoutChangingTheSkyClock()
    {
        SkyStateProvider sky = SkyStateProvider.Default();
        var clock = new WorldTimeService(sky);
        clock.PinnedDayFraction = 0f;
        var lighting = new LightManager();
        bool persistentDaylight = true;
        var environment = new RuntimeWorldFrameEnvironmentPreparation(
            RuntimeOptions.Parse("test-dat", _ => null),
            clock,
            lighting,
            dispatcher: null,
            environmentCells: null,
            lightingUbo: null,
            new WorldRenderRangeState(4, 12),
            skyPes: null,
            persistentDaylight: () => persistentDaylight);
        WorldCameraFrame camera = CameraFrame(new FlyCamera());
        WorldRootFrame roots = default;
        SkyKeyframe midnight = sky.Interpolate(0f);
        SkyKeyframe noon = sky.Interpolate(0.5f);
        var foundation = new RenderFrameFoundation(
            PortalViewportVisible: false,
            Sky: midnight,
            Atmosphere: default);

        environment.Prepare(
            in camera,
            in roots,
            in foundation,
            activeDayGroup: null);

        Assert.Equal(noon.AmbientColor, lighting.CurrentAmbient.AmbientColor);
        Assert.NotNull(lighting.Sun);
        Assert.Equal(noon.SunColor, lighting.Sun!.ColorLinear);
        Assert.Equal(0d, clock.DayFraction, precision: 5);

        persistentDaylight = false;
        environment.Prepare(
            in camera,
            in roots,
            in foundation,
            activeDayGroup: null);

        Assert.Equal(midnight.AmbientColor, lighting.CurrentAmbient.AmbientColor);
        Assert.NotNull(lighting.Sun);
        Assert.Equal(midnight.SunColor, lighting.Sun!.ColorLinear);
    }

    [Fact]
    public void Environment_preparation_keeps_lighting_snapshot_before_ubo_upload()
    {
        MethodInfo prepare = typeof(RuntimeWorldFrameEnvironmentPreparation).GetMethod(
            nameof(RuntimeWorldFrameEnvironmentPreparation.Prepare))!;
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(prepare);

        Type[] ownerOrder =
        [
            typeof(RuntimeWorldFrameEnvironmentPreparation),
            typeof(LightManager),
            typeof(LightManager),
            typeof(LightManager),
            typeof(WbDrawDispatcher),
            typeof(EnvCellRenderer),
            typeof(SceneLightingUbo),
            typeof(SceneLightingUboBinding),
        ];
        string[] methodOrder =
        [
            "UpdateSunFromSky",
            nameof(LightManager.UpdateViewerLight),
            nameof(LightManager.Tick),
            nameof(LightManager.BuildPointLightSnapshot),
            nameof(WbDrawDispatcher.SetSceneLights),
            nameof(EnvCellRenderer.SetPointSnapshot),
            nameof(SceneLightingUbo.Build),
            nameof(SceneLightingUboBinding.Upload),
        ];

        AssertCompiledCallOrder(calls, ownerOrder, methodOrder);
    }

    [Fact]
    public void World_scene_uses_the_typed_builder_and_orchestrator_owns_its_local_composition()
    {
        MethodInfo[] windowMethods = typeof(GameWindow).GetMethods(
            BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(
            windowMethods,
            method => method.Name is "UpdateSunFromSky" or "UpdateSkyPes" or "ParseEnvFloat");

        FieldInfo[] windowFields = typeof(GameWindow).GetFields(
            BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(
            windowFields,
            field => field.FieldType == typeof(WorldRenderFrameBuilder)
                || field.FieldType == typeof(SkyPesFrameController));

        MethodInfo render = typeof(WorldSceneRenderer).GetMethod(
            nameof(WorldSceneRenderer.Render))!;
        IReadOnlyList<CompiledCall> renderCalls = CompiledCallGraph.Read(render);
        AssertCompiledCallOrder(
            renderCalls,
            [
                typeof(IWorldSceneAlphaFrame),
                typeof(IWorldRenderFrameBuilder),
                typeof(IWorldScenePassExecutor),
                typeof(WorldRenderFrameOutcome),
            ],
            [
                nameof(IWorldSceneAlphaFrame.BeginFrame),
                nameof(IWorldRenderFrameBuilder.Build),
                nameof(IWorldScenePassExecutor.DrawFlatTerrain),
                ".ctor",
            ]);
        Assert.Single(
            renderCalls,
            call => call.Target.DeclaringType == typeof(IWorldRenderFrameBuilder)
                && call.Target.Name == nameof(IWorldRenderFrameBuilder.Build));

        MethodInfo compose = typeof(FrameRootCompositionPhase).GetMethod(
            "ComposeCore",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<CompiledCall> composeCalls = CompiledCallGraph.Read(compose);
        AssertCompiledCallOrder(
            composeCalls,
            [
                typeof(SkyPesFrameController),
                typeof(WorldRenderFrameBuilder),
                typeof(RenderFrameOrchestrator),
                typeof(GameFrameGraphSlot),
            ],
            [".ctor", ".ctor", ".ctor", nameof(GameFrameGraphSlot.PublishOwned)]);

    }

    private static WorldCameraFrame CameraFrame(FlyCamera camera) => new(
        camera,
        camera.Projection,
        camera.View * camera.Projection,
        default,
        Matrix4x4.Identity,
        camera.Position);

    private static AcDream.Core.World.Cells.EnvCell CoreCell(
        uint id,
        bool seenOutside) => new(
            id,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector3.Zero,
            Vector3.One,
            Array.Empty<CellPortal>(),
            Array.Empty<uint>(),
            seenOutside,
            containmentBsp: null);

    private static void AssertCompiledCallOrder(
        IReadOnlyList<CompiledCall> calls,
        IReadOnlyList<Type> declaringTypes,
        IReadOnlyList<string> methodNames)
    {
        Assert.Equal(declaringTypes.Count, methodNames.Count);
        int cursor = -1;
        for (int index = 0; index < declaringTypes.Count; index++)
        {
            int next = CompiledCallGraph.IndexOf(
                calls,
                declaringTypes[index],
                methodNames[index],
                cursor + 1);
            Assert.True(
                next > cursor,
                $"Missing or out-of-order compiled call: {declaringTypes[index].Name}.{methodNames[index]}");
            cursor = next;
        }
    }

    private sealed class RecordingCamera(
        List<string> calls,
        WorldCameraFrame result) : IWorldFrameCameraSource
    {
        public WorldCameraFrame Resolve()
        {
            calls.Add("camera");
            return result;
        }
    }

    private sealed class RecordingVisibility(List<string> calls)
        : IWorldFrameVisibilityPreparation
    {
        public bool WaitingForLogin { get; private set; }

        public RetailLandscapeVisibilityFrame CaptureCompletedLandscapeVisibility()
        {
            calls.Add("visibility:capture");
            return RetailLandscapeVisibilityFrame.None;
        }

        public void Begin(in WorldCameraFrame camera, bool waitingForLogin)
        {
            calls.Add("visibility:begin");
            WaitingForLogin = waitingForLogin;
        }

        public void PublishViewProjection(in WorldCameraFrame camera) =>
            calls.Add("visibility:projection");
    }

    [Fact]
    public void Build_BorrowsExactPriorCompletedLandscapeBeforeCurrentBegin()
    {
        var particles = new ParticleVisibilityController();
        particles.BeginFrame(Vector3.Zero);
        particles.UseWorldView();
        particles.MarkVisibleLandscapeCells([0x12340002u]);
        particles.CompleteFrame();
        RetailLandscapeVisibilityFrame ownerBefore =
            particles.CaptureCompletedLandscapeVisibility();
        var visibility = new RuntimeWorldFrameVisibilityPreparation(
            selection: null,
            particles,
            terrain: null,
            reveal: null,
            environmentFrustum: null);
        var camera = new FlyCamera();
        var builder = new WorldRenderFrameBuilder(
            new RecordingCamera([], CameraFrame(camera)),
            visibility,
            new RecordingSettings([]),
            new RecordingRoots([], default),
            new RecordingEnvironment([]),
            new RecordingAnimated([], []),
            new RecordingBuildings([], default),
            new RecordingMembership());
        RenderFrameFoundation foundation = default;

        WorldRenderFrame world = builder.Build(
            in foundation,
            waitingForLogin: false,
            activeDayGroup: null);

        Assert.Same(ownerBefore.CellIds, world.PriorLandscapeVisibility.CellIds);
        Assert.True(world.PriorLandscapeVisibility.HasCompletedWorldView);
        Assert.Contains(0x12340002u, world.PriorLandscapeVisibility.CellIds);
        Assert.Same(
            ownerBefore.CellIds,
            particles.CaptureCompletedLandscapeVisibility().CellIds);

        WorldRenderFrame login = builder.Build(
            in foundation,
            waitingForLogin: true,
            activeDayGroup: null);
        Assert.False(login.PriorLandscapeVisibility.HasCompletedWorldView);
        Assert.Empty(login.PriorLandscapeVisibility.CellIds);
    }

    private sealed class RecordingMembership : IDirectionalShadowCellMembership
    {
        public bool TryGetRetailCellArray(
            uint entityId,
            out IReadOnlyList<uint> cells)
        {
            _ = entityId;
            cells = Array.Empty<uint>();
            return false;
        }
    }

    private sealed class RecordingSettings(List<string> calls)
        : IWorldFrameSettingsPreview
    {
        public void Apply(in WorldCameraFrame camera) => calls.Add("settings");
    }

    private sealed class RecordingRoots(
        List<string> calls,
        WorldRootFrame result) : IWorldFrameRootSource
    {
        public WorldRootFrame Resolve(in WorldCameraFrame camera)
        {
            calls.Add("roots");
            return result;
        }
    }

    private sealed class RecordingEnvironment(List<string> calls)
        : IWorldFrameEnvironmentPreparation
    {
        public RenderFrameFoundation Foundation { get; private set; }

        public void Prepare(
            in WorldCameraFrame camera,
            in WorldRootFrame roots,
            in RenderFrameFoundation foundation,
            DayGroupData? activeDayGroup)
        {
            calls.Add("environment");
            Foundation = foundation;
        }

    }

    private sealed class RecordingAnimated(
        List<string> calls,
        HashSet<uint> result) : IWorldFrameAnimatedEntitySource
    {
        public HashSet<uint> Capture()
        {
            calls.Add("animated");
            return result;
        }
    }

    private sealed class RecordingBuildings(
        List<string> calls,
        WorldBuildingFrame result) : IWorldFrameBuildingSource
    {
        public WorldBuildingFrame Gather(
            LoadedCell? viewerRoot,
            uint viewerCellId,
            in FrustumPlanes frustum)
        {
            calls.Add("buildings");
            return result;
        }
    }
}
