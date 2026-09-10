using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using AcDream.App.Composition;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.Tests.Architecture;
using AcDream.App.World;
using AcDream.Core.Lighting;
using AcDream.Core.Physics;
using AcDream.Core.Rendering;
using AcDream.Core.Vfx;
using AcDream.Core.World;
using AcDream.Core.World.Cells;
using DatPhysicsScript = DatReaderWriter.DBObjs.PhysicsScript;
using DatAnimationHook = DatReaderWriter.Types.AnimationHook;
using DatPhysicsScriptData = DatReaderWriter.Types.PhysicsScriptData;
using DatSoundTweakedHook = DatReaderWriter.Types.SoundTweakedHook;

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
            Assert.False(result.SkyEffectsActive);
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
        Assert.True(result.SkyEffectsActive);
        Assert.False(result.CameraInsideEnclosedCell);
        Assert.False(result.PlayerOrCameraInsideEnclosedCell);
        Assert.True(result.IsAtmosphericallyOutdoor);
    }

    [Fact]
    public void Map_mode_disables_distance_fog_only_for_the_active_retail_chase_camera()
    {
        var atmosphere = new AtmosphereSnapshot(
            WeatherKind.Storm,
            Intensity: 0.8f,
            FogColor: new Vector3(0.1f, 0.2f, 0.3f),
            FogStart: 30f,
            FogEnd: 180f,
            FogMode: FogMode.Linear,
            LightningFlash: 0.6f,
            Override: EnvironOverride.BlackFog);
        var controller = new CameraController(new OrbitCamera(), new FlyCamera());
        var legacy = new ChaseCamera();
        var retail = new RetailChaseCamera();
        controller.EnterChaseMode(legacy, retail);
        retail.ToggleRetailMapModeView();
        bool savedRetailCamera = CameraDiagnostics.UseRetailChaseCamera;

        try
        {
            CameraDiagnostics.UseRetailChaseCamera = true;
            var viewPlane = new TeleportViewPlaneController();
            viewPlane.Begin(controller.Active.Projection);
            var source = new RuntimeWorldFrameCameraSource(controller, viewPlane.ApplyTo);
            WorldCameraFrame overheadFrame = source.Resolve();
            Assert.True(WorldCameraViewPolicy.IsOverheadView(controller.Active));
            Assert.False(WorldCameraViewPolicy.IsOverheadView(overheadFrame.Camera));
            Assert.True(overheadFrame.IsOverheadView);

            using var device = new RecordingGpuDevice();
            using IGpuFrame gpuFrame = device.BeginFrame();
            var sections = new WorldFrameSections();
            using var lightingUbo = new SceneLightingUboBinding(
                new FixedGpuFrameSource(gpuFrame),
                sections);
            lightingUbo.BeginFrame(gpuFrame.SlotIndex);
            var environment = new RuntimeWorldFrameEnvironmentPreparation(
                RuntimeOptions.Parse("test-dat", _ => null),
                new WorldTimeService(SkyStateProvider.Default()),
                new LightManager(),
                dispatcher: null,
                environmentCells: null,
                lightingUbo,
                new WorldRenderRangeState(4, 12),
                skyPes: null);
            WorldRootFrame roots = default;
            var foundation = new RenderFrameFoundation(
                PortalViewportVisible: false,
                Sky: default,
                Atmosphere: atmosphere);

            environment.Prepare(in overheadFrame, in roots, in foundation, activeDayGroup: null);
            SceneLightingUbo overheadUbo = ReadSceneLighting(sections);
            Assert.Equal((float)FogMode.Off, overheadUbo.FogParams.W);
            Assert.Equal(atmosphere.FogStart, overheadUbo.FogParams.X);
            Assert.Equal(atmosphere.FogEnd, overheadUbo.FogParams.Y);
            Assert.Equal(atmosphere.LightningFlash, overheadUbo.FogParams.Z);

            CameraDiagnostics.UseRetailChaseCamera = false;
            Assert.False(WorldCameraViewPolicy.IsOverheadView(controller.Active));
            Assert.False(source.Resolve().IsOverheadView);

            controller.ToggleFly();
            Assert.False(source.Resolve().IsOverheadView);
            controller.ToggleFly();
            Assert.False(source.Resolve().IsOverheadView);

            controller.EnterChaseMode(legacy, retail);

            retail.ToggleRetailMapModeView();
            CameraDiagnostics.UseRetailChaseCamera = true;
            WorldCameraFrame restoredFrame = source.Resolve();
            Assert.False(restoredFrame.IsOverheadView);
            AtmosphereSnapshot newWeather = atmosphere with
            {
                FogMode = FogMode.Exp2,
                FogStart = 240f,
                FogEnd = 720f,
            };
            RenderFrameFoundation afterMapExit = foundation with { Atmosphere = newWeather };
            environment.Prepare(in restoredFrame, in roots, in afterMapExit, activeDayGroup: null);
            SceneLightingUbo restoredUbo = ReadSceneLighting(sections);
            Assert.Equal((float)newWeather.FogMode, restoredUbo.FogParams.W);
            Assert.Equal(newWeather.FogStart, restoredUbo.FogParams.X);
            Assert.Equal(newWeather.FogEnd, restoredUbo.FogParams.Y);

            AtmosphereSnapshot userDisabled = newWeather with { FogMode = FogMode.Off };
            RenderFrameFoundation disabledByUser = foundation with { Atmosphere = userDisabled };
            environment.Prepare(in restoredFrame, in roots, in disabledByUser, activeDayGroup: null);
            Assert.Equal((float)FogMode.Off, ReadSceneLighting(sections).FogParams.W);

            CameraDiagnostics.UseRetailChaseCamera = false;
            legacy.ToggleRetailMapModeView();
            WorldCameraFrame legacyMapFrame = source.Resolve();
            Assert.True(legacyMapFrame.IsOverheadView);
            environment.Prepare(in legacyMapFrame, in roots, in afterMapExit, activeDayGroup: null);
            Assert.Equal((float)FogMode.Off, ReadSceneLighting(sections).FogParams.W);

            legacy.ToggleRetailMapModeView();
            WorldCameraFrame legacyRestoredFrame = source.Resolve();
            Assert.False(legacyRestoredFrame.IsOverheadView);
            environment.Prepare(in legacyRestoredFrame, in roots, in afterMapExit, activeDayGroup: null);
            Assert.Equal((float)newWeather.FogMode, ReadSceneLighting(sections).FogParams.W);

            CameraDiagnostics.UseRetailChaseCamera = true;
            retail.ToggleRetailLookDownView();
            Assert.False(source.Resolve().IsOverheadView);
            retail.SetRetailFirstPersonView();
            Assert.False(source.Resolve().IsOverheadView);
        }
        finally
        {
            CameraDiagnostics.UseRetailChaseCamera = savedRetailCamera;
        }
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
    public void Runtime_environment_stops_sky_hooks_before_an_enclosed_frame_can_dispatch_them()
    {
        const uint otherOwner = 0x50000001u;
        var calls = new List<(uint EntityId, DatAnimationHook Hook)>();
        var router = new AnimationHookRouter();
        router.Register(new RecordingHookSink(calls));
        var sound = new DatSoundTweakedHook
        {
            SoundId = 0x0A00038Bu,
            Volume = 0.1f,
            Priority = 1f,
        };
        var script = new DatPhysicsScript();
        script.ScriptData.Add(new DatPhysicsScriptData
        {
            StartTime = 1.0,
            Hook = sound,
        });
        var runner = new PhysicsScriptRunner(_ => script, router);
        var poses = new EntityEffectPoseRegistry();
        var particles = new ParticleHookSink(
            new ParticleSystem(new EmitterDescRegistry()),
            poses);
        var stoppedAudioOwners = new List<uint>();
        var skyPes = new SkyPesFrameController(
            runner,
            particles,
            poses,
            effects: null,
            diagnostic: null,
            stopAudio: stoppedAudioOwners.Add);
        var environment = new RuntimeWorldFrameEnvironmentPreparation(
            RuntimeOptions.Parse("test-dat", _ => null),
            new WorldTimeService(SkyStateProvider.Default()),
            new LightManager(),
            dispatcher: null,
            environmentCells: null,
            lightingUbo: null,
            new WorldRenderRangeState(4, 12),
            skyPes);
        WorldCameraFrame camera = CameraFrame(new FlyCamera());
        RenderFrameFoundation foundation = default;
        var sky = new DayGroupData
        {
            Name = "Test",
            SkyObjects =
            [
                new SkyObjectData
                {
                    GfxObjId = 0x02000714u,
                    DefaultScriptId = 0x330007DBu,
                    PesObjectId = 0x330007DBu,
                },
            ],
        };
        WorldRootFrame outdoor = default;
        var enclosed = new WorldRootFrame(
            PlayerRoot: null,
            PlayerSeenOutside: false,
            ViewerCellId: 0x01010100u,
            ViewerEyePosition: Vector3.Zero,
            PlayerViewPosition: Vector3.Zero,
            ViewerRoot: new LoadedCell { CellId = 0x01010100u, SeenOutside = true },
            CameraInsideCell: true,
            RootSeenOutside: true,
            PlayerInsideCell: true,
            PlayerLandblockId: null,
            RenderCenterLandblockX: 0,
            RenderCenterLandblockY: 0,
            PlayerCellId: 0x01010100u,
            PlayerIndoorGate: true);

        Assert.True(enclosed.RenderSky);
        Assert.False(enclosed.SkyEffectsActive);

        environment.Prepare(in camera, in outdoor, in foundation, sky);
        Assert.True(runner.PlayDirect(otherOwner, 0x330007DBu));

        var updateGate = new RuntimeSkyPesActivationGate(
            skyPes,
            new RecordingCamera([], camera),
            new RecordingRoots([], enclosed));
        updateGate.Tick();
        runner.Tick(1.0);

        var otherCall = Assert.Single(calls);
        Assert.Equal(otherOwner, otherCall.EntityId);
        Assert.Equal([0xF0000000u], stoppedAudioOwners);

        environment.Prepare(in camera, in outdoor, in foundation, sky);
        runner.Tick(2.0);

        Assert.Equal(2, calls.Count);
        Assert.Equal(0xF0000000u, calls[1].EntityId);
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

    private sealed class RecordingHookSink(
        List<(uint EntityId, DatAnimationHook Hook)> calls) : IAnimationHookSink
    {
        public void OnHook(
            uint entityId,
            Vector3 entityWorldPosition,
            DatAnimationHook hook)
        {
            _ = entityWorldPosition;
            calls.Add((entityId, hook));
        }
    }

    private static SceneLightingUbo ReadSceneLighting(WorldFrameSections sections)
    {
        GpuBufferSection section = sections.SceneLighting;
        Assert.True(section.IsValid);
        Span<byte> bytes = stackalloc byte[SceneLightingUbo.SizeInBytes];
        section.Buffer!.Read(section.OffsetBytes, bytes);
        return MemoryMarshal.Read<SceneLightingUbo>(bytes);
    }

    private sealed class FixedGpuFrameSource(IGpuFrame frame) : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => frame;
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
