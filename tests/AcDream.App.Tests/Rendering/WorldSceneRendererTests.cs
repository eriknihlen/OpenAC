using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using AcDream.App.Composition;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Packs;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Selection;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Walk;
using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.App.Tests.Architecture;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Content;
using AcDream.Core.Physics;
using AcDream.Core.Rendering;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.World;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Lib.IO;
using Microsoft.Extensions.Logging.Abstractions;

namespace AcDream.App.Tests.Rendering;

public sealed class WorldSceneRendererTests
{
    [Fact]
    public void EnhancedPreparation_BuildsCanonicalWorldOnceBeforeExecution()
    {
        var rig = new Rig(portalVisible: false, waitingForLogin: false, clipRoot: null);

        PreparedWorldSceneFrame prepared = rig.Renderer.PrepareEnhanced(default);
        WorldRenderFrameOutcome result = rig.Renderer.RenderPreparedEnhanced(
            default,
            in prepared);

        Assert.True(result.NormalWorldDrawn);
        Assert.True(prepared.ShouldRender);
        Assert.Equal(1, rig.Calls.Count(value => value == "frame:build"));
        Assert.True(rig.Calls.IndexOf("frame:build") < rig.Calls.IndexOf("selection:begin"));
        Assert.Equal(prepared.World.Camera.Frustum, rig.Selection.PreparedViewFrustum);
        Assert.Equal(rig.Entities.ResidentWindow, prepared.World.ResidentStreamingWindow);
        Assert.Equal((0, 0), rig.Entities.LastResidentWindowCenter);
    }

    [Fact]
    public void EnhancedPreparation_AttachesTheSelectedAuthoredCelestialSource()
    {
        var rig = new Rig(portalVisible: false, waitingForLogin: false, clipRoot: null);
        rig.DayGroup.SkyObjects =
        [
            new SkyObjectData
            {
                GfxObjId = AuthoredCelestialShadowSourceResolver.SunGfxObjId,
                AuthoredSortCenter = Vector3.UnitX,
                BeginTime = 0f,
                EndTime = 0f,
                BeginAngle = 90f,
                EndAngle = 90f,
            },
        ];

        PreparedWorldSceneFrame prepared = rig.Renderer.PrepareEnhanced(default);

        Assert.True(prepared.ShouldRender);
        Assert.Equal(
            AuthoredCelestialShadowSourceKind.Sun,
            prepared.World.CelestialShadowSource.Kind);
        Assert.Equal(0, prepared.World.CelestialShadowSource.ObjectIndex);
        Assert.Equal(
            AuthoredCelestialShadowSourceResolver.SunGfxObjId,
            prepared.World.CelestialShadowSource.GfxObjId);
        Assert.InRange(
            Vector3.Distance(
                Vector3.UnitZ,
                prepared.World.CelestialShadowSource.SurfaceToLightDirection),
            0f,
            1e-5f);

        rig.Renderer.CancelPreparedEnhanced(in prepared);
    }

    [Fact]
    public void EnhancedPreparation_SkippedWorldStillPublishesEmptySelectionFrame()
    {
        var rig = new Rig(portalVisible: true, waitingForLogin: false, clipRoot: null);

        PreparedWorldSceneFrame prepared = rig.Renderer.PrepareEnhanced(default);
        WorldRenderFrameOutcome result = rig.Renderer.RenderPreparedEnhanced(
            default,
            in prepared);

        Assert.False(prepared.ShouldRender);
        Assert.Equal(default, result);
        Assert.Equal(["selection:begin", "selection:complete"], rig.Calls);
    }

    [Fact]
    public void CancelledEnhancedPrepass_DoesNotPoisonTheNextFrame()
    {
        var rig = new Rig(portalVisible: false, waitingForLogin: false, clipRoot: null);
        PreparedWorldSceneFrame failed = rig.Renderer.PrepareEnhanced(default);

        // Models an exception in the shadow prepass before a world pass opens.
        rig.Renderer.CancelPreparedEnhanced(in failed);
        PreparedWorldSceneFrame recovered = rig.Renderer.PrepareEnhanced(default);
        WorldRenderFrameOutcome result = rig.Renderer.RenderPreparedEnhanced(
            default,
            in recovered);

        Assert.True(result.NormalWorldDrawn);
        Assert.Equal(2, rig.Calls.Count(value => value == "frame:build"));
    }

    [Fact]
    public void PortalViewport_PublishesEmptySelectionFrameAndSkipsWorldOwners()
    {
        var rig = new Rig(portalVisible: true, waitingForLogin: false, clipRoot: null);

        WorldRenderFrameOutcome result = rig.Renderer.Render(default);

        Assert.Equal(default, result);
        Assert.Equal(["selection:begin", "selection:complete"], rig.Calls);
    }

    [Fact]
    public void QuiescedGeneration_PublishesEmptySelectionFrameAndSkipsEveryWorldOwner()
    {
        var transit = new RuntimeWorldTransitState();
        var availability = new WorldGenerationAvailabilityState(transit);
        RuntimeWorldTransitTestDriver.BeginPortal(
            transit,
            0x11340021u);
        var rig = new Rig(
            portalVisible: false,
            waitingForLogin: false,
            clipRoot: null,
            availability: availability);

        WorldRenderFrameOutcome result = rig.Renderer.Render(default);

        Assert.Equal(default, result);
        Assert.Equal(["selection:begin", "selection:complete"], rig.Calls);
    }

    [Fact]
    public void LoginWait_IsOwnedByTheFrameGate_NotByARendererShortCircuit()
    {
        var rig = new Rig(portalVisible: false, waitingForLogin: true, clipRoot: null);

        WorldRenderFrameOutcome result = rig.Renderer.Render(default);

        Assert.True(result.NormalWorldDrawn);
        Assert.True(rig.Frames.WaitingForLogin);
        Assert.Contains("flat:terrain", rig.Calls);

        var covered = new Rig(portalVisible: true, waitingForLogin: true, clipRoot: null);
        Assert.Equal(default, covered.Renderer.Render(default));
        Assert.Equal(["selection:begin", "selection:complete"], covered.Calls);
    }

    [Fact]
    public void FlatWorld_PreservesTerrainEntityParticleWeatherAndCompletionOrder()
    {
        var rig = new Rig(portalVisible: false, waitingForLogin: false, clipRoot: null);

        WorldRenderFrameOutcome result = rig.Renderer.Render(default);

        Assert.Equal(new WorldRenderFrameOutcome(3, 8, true), result);
        Assert.Equal(
            [
                "selection:begin",
                "alpha:begin",
                "frame:build",
                "passes:begin",
                "flat:clip",
                "flat:sky",
                "flat:terrain",
                "flat:entities",
                "passes:disable-clip",
                "particles:global",
                "flat:weather",
                "diagnostics:draw",
                "alpha:end",
                "visibility:complete",
                "selection:complete",
            ],
            rig.Calls);

        Assert.Equal(rig.Foundation, rig.Frames.Foundation);
        Assert.False(rig.Frames.WaitingForLogin);
        Assert.Same(rig.DayGroup, rig.Frames.ActiveDayGroup);
        Assert.Equal(rig.Foundation, rig.Passes.SkyFoundation);
        Assert.Equal(rig.Foundation, rig.Passes.WeatherFoundation);
        Assert.Same(rig.DayGroup, rig.Passes.SkyDayGroup);
        Assert.Same(rig.DayGroup, rig.Passes.WeatherDayGroup);
        Assert.Equal(rig.DayFraction, rig.Passes.SkyDayFraction);
        Assert.Equal(rig.DayFraction, rig.Passes.WeatherDayFraction);
    }

    [Fact]
    public void PViewWorld_PublishesTheWalkLandscapeCellsOnce()
    {
        var root = new LoadedCell
        {
            CellId = 0x01010001u,
            IsOutdoorNode = false,
        };
        var rig = new Rig(portalVisible: false, waitingForLogin: false, clipRoot: root);

        WorldRenderFrameOutcome result = rig.Renderer.Render(default);

        Assert.True(result.NormalWorldDrawn);
        Assert.Equal(
            [
                "selection:begin",
                "alpha:begin",
                "frame:build",
                "passes:begin",
                "pview:draw",
                "visibility:mark",
                "passes:disable-clip",
                "particles:skipped",
                "diagnostics:draw",
                "alpha:end",
                "visibility:complete",
                "selection:complete",
            ],
            rig.Calls);
        Assert.DoesNotContain("flat:clip", rig.Calls);
        Assert.DoesNotContain("flat:terrain", rig.Calls);
        Assert.DoesNotContain("flat:entities", rig.Calls);
        Assert.NotNull(rig.PView.LastInput);
        Assert.Same(rig.DayGroup, rig.PView.LastInput!.ActiveDayGroup);
        Assert.Equal(rig.DayFraction, rig.PView.LastInput.DayFraction);
        Assert.Equal(rig.Foundation.Sky, rig.PView.LastInput.SkyKeyframe);
        Assert.True(rig.PView.LastInput.RenderWeather);
        Assert.Equal(root.CellId, rig.PView.LastInput.ViewerCellId);
        Assert.Equal(0x01010002u, rig.PView.LastInput.PlayerCellId);
        Assert.Equal(4, rig.PView.LastInput.RenderRadius);
    }

    [Fact]
    public void PViewWorld_PublishesOnlyLandscapeCellsToParticleVisibility()
    {
        var root = new LoadedCell
        {
            CellId = 0x01010001u,
            IsOutdoorNode = false,
        };
        var rig = new Rig(portalVisible: false, waitingForLogin: false, clipRoot: root);

        rig.Renderer.Render(default);

        Assert.Equal([0x0101_0003u], rig.Visibility.MarkedCells);
        Assert.DoesNotContain(0x0101_0100u, rig.Visibility.MarkedCells);
    }

    [Fact]
    public void PViewWorld_ReusesOneSynchronousFrameInputAcrossFrames()
    {
        var root = new LoadedCell
        {
            CellId = 0x01010001u,
            IsOutdoorNode = false,
        };
        var rig = new Rig(
            portalVisible: false,
            waitingForLogin: false,
            clipRoot: root);

        rig.Renderer.Render(default);
        RetailPViewFrameInput first = Assert.IsType<RetailPViewFrameInput>(
            rig.PView.LastInput);
        rig.Calls.Clear();

        rig.Renderer.Render(default);

        Assert.Same(first, rig.PView.LastInput);
        Assert.Same(root, rig.PView.LastInput!.RootCell);
        Assert.Equal(rig.DayFraction, rig.PView.LastInput.DayFraction);
        Assert.Contains("pview:draw", rig.Calls);
    }

    [Fact]
    public void PViewWorld_OverheadDetailOverrideIsReplacedOnEveryFrame()
    {
        var root = new LoadedCell { CellId = 0x01010001u };
        var rig = new Rig(false, false, root);
        WorldRenderFrame normal = rig.Frames.Frame;
        rig.Frames.Frame = normal with { Camera = normal.Camera with { IsOverheadView = true } };
        rig.Renderer.Render(default);
        Assert.True(rig.PView.LastInput!.BuildingDegradesDisabled);

        rig.Frames.Frame = normal;
        rig.Renderer.Render(default);
        Assert.False(rig.PView.LastInput!.BuildingDegradesDisabled);
    }

    [Fact]
    public void OutdoorPView_SkipsPostWorldParticleReplayAndFlatWeather()
    {
        var root = new LoadedCell
        {
            CellId = 0x01010001u,
            IsOutdoorNode = true,
        };
        var rig = new Rig(portalVisible: false, waitingForLogin: false, clipRoot: root);

        WorldRenderFrameOutcome result = rig.Renderer.Render(default);

        Assert.True(result.NormalWorldDrawn);
        Assert.Contains("particles:skipped", rig.Calls);
        Assert.DoesNotContain("flat:weather", rig.Calls);
    }

    [Fact]
    public void SealedInteriorPView_DisablesWeatherAndPropagatesEnvironmentOverride()
    {
        var root = new LoadedCell
        {
            CellId = 0x01010001u,
            IsOutdoorNode = false,
        };
        var rig = new Rig(
            portalVisible: false,
            waitingForLogin: false,
            clipRoot: root,
            playerSeenOutside: false,
            environOverride: EnvironOverride.BlueFog);

        rig.Renderer.Render(default);

        Assert.NotNull(rig.PView.LastInput);
        Assert.False(rig.PView.LastInput!.RenderWeather);
        Assert.True(rig.PView.LastInput.EnvironOverrideActive);
    }

    [Fact]
    public void PViewFailure_AbortsPortalStateAndRecoversOnTheNextFrame()
    {
        var root = new LoadedCell
        {
            CellId = 0x01010001u,
            IsOutdoorNode = false,
        };
        var rig = new Rig(portalVisible: false, waitingForLogin: false, clipRoot: root);
        rig.PView.ThrowOnDraw = true;

        Assert.Throws<InvalidOperationException>(() => rig.Renderer.Render(default));

        Assert.Equal(
            [
                "selection:begin",
                "alpha:begin",
                "frame:build",
                "passes:begin",
                "pview:draw",
                "pview:abort",
                "passes:abort",
                "visibility:abort",
                "alpha:abort",
                "selection:abort",
            ],
            rig.Calls);

        rig.Calls.Clear();
        rig.PView.ThrowOnDraw = false;

        Assert.True(rig.Renderer.Render(default).NormalWorldDrawn);
        Assert.Contains("selection:complete", rig.Calls);
    }

    [Fact]
    public void DrawFailure_DoesNotPublishPartialAlphaSelectionOrParticleFrames()
    {
        var rig = new Rig(portalVisible: false, waitingForLogin: false, clipRoot: null);
        rig.Passes.ThrowOnTerrain = true;

        Assert.Throws<InvalidOperationException>(() => rig.Renderer.Render(default));

        Assert.Equal(
            [
                "selection:begin",
                "alpha:begin",
                "frame:build",
                "passes:begin",
                "flat:clip",
                "flat:sky",
                "flat:terrain",
                "passes:abort",
                "visibility:abort",
                "alpha:abort",
                "selection:abort",
            ],
            rig.Calls);

        rig.Calls.Clear();
        rig.Passes.ThrowOnTerrain = false;
        WorldRenderFrameOutcome recovered = rig.Renderer.Render(default);

        Assert.True(recovered.NormalWorldDrawn);
        Assert.Contains("alpha:end", rig.Calls);
        Assert.Contains("selection:complete", rig.Calls);
    }

    [Fact]
    public void AbortFailure_IsAggregatedAfterEveryOtherFrameOwnerIsAborted()
    {
        var rig = new Rig(portalVisible: false, waitingForLogin: false, clipRoot: null);
        rig.Passes.ThrowOnTerrain = true;
        rig.Passes.ThrowOnAbort = true;

        AggregateException failure = Assert.Throws<AggregateException>(
            () => rig.Renderer.Render(default));

        Assert.Contains(
            failure.InnerExceptions,
            error => error is InvalidOperationException
                && error.Message == "terrain failed");
        Assert.Contains(
            failure.InnerExceptions,
            error => error is InvalidOperationException
                && error.Message == "pass abort failed");
        Assert.Contains("visibility:abort", rig.Calls);
        Assert.Contains("alpha:abort", rig.Calls);
        Assert.Contains("selection:abort", rig.Calls);

        rig.Calls.Clear();
        rig.Passes.ThrowOnTerrain = false;
        rig.Passes.ThrowOnAbort = false;
        Assert.True(rig.Renderer.Render(default).NormalWorldDrawn);
    }

    [Fact]
    public void GameWindow_DelegatesWorldRenderingWithoutOwningDrawBranches()
    {
        MethodInfo compose = typeof(FrameRootCompositionPhase).GetMethod(
            "ComposeCore",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(compose);

        int worldRenderer = RequiredCallIndex(
            calls,
            typeof(WorldSceneRenderer),
            ".ctor");
        int frameOrchestrator = RequiredCallIndex(
            calls,
            typeof(RenderFrameOrchestrator),
            ".ctor");
        Assert.True(worldRenderer < frameOrchestrator);
        Assert.Single(
            calls,
            call => call.Target.DeclaringType == typeof(WorldSceneRenderer)
                && call.Target.Name == ".ctor");
        Assert.Single(
            calls,
            call => call.Target.DeclaringType == typeof(RenderFrameOrchestrator)
                && call.Target.Name == ".ctor");

        FieldInfo[] windowFields = typeof(GameWindow).GetFields(
            BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(
            windowFields,
            field => field.FieldType == typeof(WorldSceneRenderer)
                || field.FieldType == typeof(RetailPViewRenderer)
                || field.FieldType == typeof(TerrainDrawDiagnosticsController)
                || field.FieldType == typeof(RenderFrameOrchestrator));

        FieldInfo worldPhase = Assert.Single(
            typeof(RenderFrameOrchestrator).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(IWorldSceneFramePhase));
        Assert.Equal("_world", worldPhase.Name);
    }

    [Fact]
    public void ExtractedWorldOwners_RetainNoWindowDelegateOrBorrowedPViewProduct()
    {
        Type[] ownerTypes =
        [
            typeof(WorldSceneRenderer),
            typeof(WorldScenePassExecutor),
            typeof(WorldSceneDiagnosticsController),
        ];
        Type[] borrowedTypes =
        [
            typeof(RetailPViewFrameResult),
            typeof(ClipFrameAssembly),
        ];

        foreach (Type owner in ownerTypes)
        {
            foreach (FieldInfo field in owner.GetFields(
                         BindingFlags.Instance
                         | BindingFlags.Public
                         | BindingFlags.NonPublic))
            {
                Assert.False(
                    typeof(GameWindow).IsAssignableFrom(field.FieldType),
                    $"{owner.Name}.{field.Name} retains GameWindow.");
                Assert.False(
                    typeof(Delegate).IsAssignableFrom(field.FieldType),
                    $"{owner.Name}.{field.Name} retains a delegate callback.");
                Assert.DoesNotContain(field.FieldType, borrowedTypes);
            }
        }
    }

    [Fact]
    public void OutdoorProductionPView_DrainsBuildingThenRenderNormalModeWithoutLandscapeFlush()
    {
        var drains = new List<RetailAlphaFlushSite>();
        using var fixture = new OutdoorAlphaOwnerFixture(drains.Add);

        WorldRenderFrameOutcome outcome = fixture.Renderer.Render(default);

        Assert.True(outcome.NormalWorldDrawn);
        Assert.Equal(1, fixture.PView.DrawCount);
        Assert.Equal(
            [RetailAlphaFlushSite.DrawBuilding, RetailAlphaFlushSite.RenderNormalMode],
            drains);
        Assert.DoesNotContain(RetailAlphaFlushSite.LandscapeFlush, drains);
    }

    private sealed class Rig
    {
        public Rig(
            bool portalVisible,
            bool waitingForLogin,
            LoadedCell? clipRoot,
            bool? playerSeenOutside = null,
            EnvironOverride environOverride = EnvironOverride.None,
            IWorldGenerationAvailability? availability = null)
        {
            Calls = [];
            DayGroup = new DayGroupData { Name = "sentinel-day-group" };
            DayFraction = 0.375f;
            Foundation = new RenderFrameFoundation(
                portalVisible,
                new SkyKeyframe(
                    0.25f,
                    35f,
                    45f,
                    new Vector3(0.1f, 0.2f, 0.3f),
                    0.8f,
                    new Vector3(0.4f, 0.5f, 0.6f),
                    0.7f,
                    new Vector3(0.7f, 0.8f, 0.9f),
                    0.2f),
                new AtmosphereSnapshot(
                    WeatherKind.Clear,
                    1f,
                    new Vector3(0.1f, 0.2f, 0.3f),
                    1f,
                    100f,
                    FogMode.Linear,
                    0f,
                    environOverride));
            var foundation = new FoundationSource(Foundation);
            var login = new LoginSource(waitingForLogin);
            var sky = new SkySource(DayGroup, DayFraction);
            var frame = CreateFrame(
                clipRoot,
                playerSeenOutside ?? clipRoot is not null);
            Frames = new FrameBuilder(Calls, frame);
            Selection = new SelectionFrame(Calls);
            var alpha = new AlphaFrame(Calls);
            Visibility = new ParticleVisibility(Calls);
            var visibility = Visibility;
            PView = new PViewRenderer(Calls);
            Passes = new PassExecutor(Calls);
            var diagnostics = new Diagnostics(Calls);
            Entities = new EntitySource();

            Renderer = new WorldSceneRenderer(
                foundation,
                login,
                sky,
                Frames,
                Entities,
                Selection,
                alpha,
                visibility,
                PView,
                new PViewCells(),
                Passes,
                new WorldRenderRangeState(4, 12),
                diagnostics,
                availability);
        }

        public List<string> Calls { get; }

        public RenderFrameFoundation Foundation { get; }

        public DayGroupData DayGroup { get; }

        public float DayFraction { get; }

        public FrameBuilder Frames { get; }

        public EntitySource Entities { get; }

        public SelectionFrame Selection { get; }

        public ParticleVisibility Visibility { get; }

        public PViewRenderer PView { get; }

        public PassExecutor Passes { get; }

        public WorldSceneRenderer Renderer { get; }
    }

    private sealed class OutdoorAlphaOwnerFixture : IDisposable
    {
        private readonly RecordingGpuDevice _device;
        private readonly TextureCache _textures;
        private readonly WbMeshAdapter _meshAdapter;
        private readonly WbDrawDispatcher _dispatcher;
        private readonly EnvCellRenderer _envCells;
        private readonly ClipFrame _clipFrame;
        private readonly RenderSceneShadowRuntime _renderScene;
        private readonly IGpuPassEncoder _pass;
        private readonly IDisposable _scopePublication;

        public OutdoorAlphaOwnerFixture(Action<RetailAlphaFlushSite> observeDrain)
        {
            _device = new RecordingGpuDevice();
            var frames = new GpuDeviceFrameLifetime(_device);
            var scope = new FixedWorldPassScope();
            var dat = new NoopDatReaderWriter();
            var prepared = new NullPreparedAssetSource();
            _textures = new TextureCache(_device, dat);
            _meshAdapter = new WbMeshAdapter(
                _device,
                dat,
                prepared,
                NullLogger<WbMeshAdapter>.Instance,
                _device.Retirement);
            var spawns = new EntitySpawnAdapter(
                _textures,
                _ => throw new NotSupportedException("The empty owner-path fixture never spawns entities."));
            var alpha = new RetailAlphaQueue(drainObserver: observeDrain);
            _dispatcher = new WbDrawDispatcher(
                _device,
                frames,
                scope,
                _textures,
                _meshAdapter,
                spawns,
                new EntityClassificationCache(),
                new AcDream.Core.Rendering.TranslucencyFadeManager(),
                alphaQueue: alpha);
            _envCells = new EnvCellRenderer(
                _device,
                frames,
                scope,
                _meshAdapter.MeshManager!,
                new WbFrustum());
            _clipFrame = ClipFrame.NoClip();

            var glDiagnostics = new WorldRenderDiagnostics(new NullDiagnosticLog());
            var terrainDiagnostics = new TerrainDrawDiagnosticsController(
                enabled: false,
                glDiagnostics,
                new EmptyFrameFacts(),
                new NullDiagnosticLog());
            var pviewPasses = new RetailPViewPassExecutor(
                new NullWorldPassSurface(),
                NullRenderFrameGlState.Instance,
                _clipFrame,
                terrain: null,
                _envCells,
                _dispatcher,
                sky: null,
                particles: null,
                particleRenderer: null,
                portalDepthMask: null,
                alpha,
                terrainDiagnostics);

            const uint landblockId = 0xF4180000u;
            var building = new WalkBuilding
            {
                PositionCellId = landblockId | 1u,
                GfxObjId = 0x0100_0001u,
                DrawingBsp = new WalkBspNode { InPortals = [] },
            };
            var entry = new WalkBuildingFactory.Entry(
                building,
                Matrix4x4.Identity,
                Matrix4x4.Identity);
            var buildings = new WalkBuildingRegistry();
            buildings.Publish(landblockId, [entry]);
            var landscape = new WalkLandscapeAssembler();
            landscape.PublishLandblock(landblockId, maxZ: 10f, minZ: -1f, [entry]);
            var cells = new CellVisibility();
            _renderScene = new RenderSceneShadowRuntime(RenderSceneGeneration.FromRaw(1));
            var retailPView = new RetailPViewRenderer(
                _renderScene,
                buildings,
                landscape,
                cells,
                new ShadowObjectRegistry());
            PView = new CountingPView(new WorldScenePViewRenderer(retailPView, pviewPasses));

            frames.BeginFrame();
            IGpuFrame frame = frames.CurrentFrame!;
            _pass = frame.BeginPass(
                GpuPassDescription.BackbufferClear(
                    "s4-c2-outdoor-owner-path",
                    Vector4.Zero,
                    sampleCount: 1));
            _scopePublication = scope.Publish(_pass);
            _dispatcher.BeginFrame(frameSlot: 0);
            _envCells.BeginFrame(frameSlot: 0);

            var calls = new List<string>();
            var root = new LoadedCell
            {
                CellId = landblockId | 1u,
                IsOutdoorNode = true,
                WorldTransform = Matrix4x4.Identity,
                InverseWorldTransform = Matrix4x4.Identity,
            };
            var day = new DayGroupData { Name = "s4-c2-owner" };
            var foundation = new RenderFrameFoundation(
                PortalViewportVisible: false,
                Sky: default,
                Atmosphere: default);
            Renderer = new WorldSceneRenderer(
                new FoundationSource(foundation),
                new LoginSource(false),
                new SkySource(day, dayFraction: 0f),
                new FrameBuilder(calls, CreateFrame(root, playerSeenOutside: true)),
                new EntitySource(),
                selection: null,
                alpha,
                new ParticleVisibility(calls),
                PView,
                new PViewCells(),
                new PassExecutor(calls),
                new WorldRenderRangeState(nearRadius: 4, farRadius: 12),
                new Diagnostics(calls));
        }

        public CountingPView PView { get; }

        public WorldSceneRenderer Renderer { get; }

        public void Dispose()
        {
            _scopePublication.Dispose();
            _pass.Dispose();
            _envCells.Dispose();
            _dispatcher.Dispose();
            _meshAdapter.Dispose();
            _textures.Dispose();
            _renderScene.Dispose();
            _clipFrame.Dispose();
            _device.Dispose();
        }
    }

    private sealed class CountingPView(IWorldScenePViewRenderer inner) : IWorldScenePViewRenderer
    {
        public int DrawCount { get; private set; }

        public RetailPViewFrameResult DrawInside(RetailPViewFrameInput input)
        {
            DrawCount++;
            return inner.DrawInside(input);
        }

        public void AbortFrame() => inner.AbortFrame();
    }

    private sealed class NullWorldPassSurface : IWorldPassSurface
    {
        public void PrepareClipFrame() { }

        public void EnableClipDistances() { }

        public void DisableClipDistances() { }

        public void ClearInteriorDepth() =>
            throw new InvalidOperationException("An outdoor root cannot clear interior depth.");
    }

    private sealed class FixedWorldPassScope : IWorldPassScope
    {
        private IGpuPassEncoder? _encoder;

        public int SampleCount => 1;

        public IGpuPassEncoder? CurrentEncoder => _encoder;

        public int AttachmentWidth => 1024;

        public int AttachmentHeight => 720;

        public WorldFrameSections Sections { get; } = new();

        public IGpuPassEncoder RequireEncoder() =>
            _encoder ?? throw new InvalidOperationException("No recording world pass is open.");

        public void ClearInteriorDepth() =>
            throw new InvalidOperationException("An outdoor root cannot clear interior depth.");

        public IDisposable Publish(IGpuPassEncoder encoder)
        {
            Assert.Null(_encoder);
            _encoder = encoder;
            Sections.Reset();
            return new Publication(this);
        }

        private sealed class Publication(FixedWorldPassScope owner) : IDisposable
        {
            public void Dispose()
            {
                owner._encoder = null;
                owner.Sections.Reset();
            }
        }
    }

    private sealed class NullDiagnosticLog : IRenderFrameDiagnosticLog
    {
        public void WriteLine(string message) { }
    }

    private sealed class EmptyFrameFacts : IFramePipelineDiagnosticFactsSource
    {
        public TerrainRenderDiagnosticFacts CaptureTerrain() => default;

        public FramePipelineDiagnosticFacts CaptureFrame() => default;
    }

    private sealed class FoundationSource(RenderFrameFoundation foundation) :
        IRenderFrameFoundationSource
    {
        public RenderFrameFoundation Foundation { get; } = foundation;
    }

    private sealed class LoginSource(bool waiting) : IRenderLoginStateSource
    {
        public bool IsWaitingForLogin { get; } = waiting;
    }

    private sealed class SkySource(
        DayGroupData activeDayGroup,
        float dayFraction) : IWorldSceneSkyStateSource
    {
        public DayGroupData? ActiveDayGroup { get; } = activeDayGroup;

        public float DayFraction { get; } = dayFraction;
    }

    private sealed class FrameBuilder(List<string> calls, WorldRenderFrame frame) :
        IWorldRenderFrameBuilder
    {
        public WorldRenderFrame Frame { get; set; } = frame;
        public RenderFrameFoundation Foundation { get; private set; }

        public bool WaitingForLogin { get; private set; }

        public DayGroupData? ActiveDayGroup { get; private set; }

        public WorldRenderFrame Build(
            in RenderFrameFoundation foundation,
            bool waitingForLogin,
            DayGroupData? activeDayGroup)
        {
            calls.Add("frame:build");
            Foundation = foundation;
            WaitingForLogin = waitingForLogin;
            ActiveDayGroup = activeDayGroup;
            return Frame;
        }

    }

    private sealed class EntitySource : IWorldSceneEntitySource
    {
        public ResidentStreamingWindowFact ResidentWindow { get; } = new(
            Revision: 77,
            CenterX: 0,
            CenterY: 0,
            CompleteRadiusLandblocks: 2,
            PublishedLandblockCount: 25,
            HasPublishedCenter: true);

        public (int X, int Y)? LastResidentWindowCenter { get; private set; }

        public IReadOnlyList<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
            IReadOnlyList<WorldEntity> Entities,
            IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> LandblockEntries =>
            Array.Empty<(uint, Vector3, Vector3, IReadOnlyList<WorldEntity>,
                IReadOnlyDictionary<uint, WorldEntity>?)>();

        public IReadOnlyList<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax)> LandblockBounds =>
            Array.Empty<(uint, Vector3, Vector3)>();

        public ResidentStreamingWindowFact CaptureResidentStreamingWindow(
            int centerX,
            int centerY)
        {
            LastResidentWindowCenter = (centerX, centerY);
            return ResidentWindow;
        }
    }

    private sealed class SelectionFrame(List<string> calls) : IWorldSceneSelectionFrame
    {
        public FrustumPlanes? PreparedViewFrustum { get; private set; }

        public void BeginFrame(FrustumPlanes? preparedViewFrustum = null)
        {
            PreparedViewFrustum = preparedViewFrustum;
            calls.Add("selection:begin");
        }

        public void CompleteFrame() => calls.Add("selection:complete");

        public void AbortFrame() => calls.Add("selection:abort");
    }

    private sealed class AlphaFrame(List<string> calls) : IWorldSceneAlphaFrame
    {
        public void BeginFrame() => calls.Add("alpha:begin");

        public void EndFrame() => calls.Add("alpha:end");

        public void AbortFrame() => calls.Add("alpha:abort");
    }

    private sealed class ParticleVisibility(List<string> calls) :
        IWorldSceneParticleVisibility
    {
        public HashSet<uint> MarkedCells { get; } = [];

        public void MarkVisibleLandscapeCells(HashSet<uint> cellIds)
        {
            calls.Add("visibility:mark");
            MarkedCells.UnionWith(cellIds);
        }

        public void CompleteFrame() => calls.Add("visibility:complete");

        public void AbortFrame() => calls.Add("visibility:abort");
    }

    private sealed class PViewRenderer : IWorldScenePViewRenderer
    {
        private readonly List<string> _calls;
        private readonly RetailPViewFrameResult _interiorResult;
        private readonly RetailPViewFrameResult _outdoorResult;

        public PViewRenderer(List<string> calls)
        {
            _calls = calls;
            // Distinct typed products: EnvCell shell preparation, diagnostic
            // union, and the outdoor landscape IsInView answer.
            _interiorResult = new RetailPViewFrameResult().Reset(
                ClipFrameAssembler.BeginWalkFrame(
                    ClipFrame.NoClip(), outdoorRoot: false),
                [0x0101_0100u],
                [0x0101_0100u, 0x0101_0003u],
                [0x0101_0003u],
                default,
                default,
                diagnosticPartition: null);
            _outdoorResult = new RetailPViewFrameResult().Reset(
                ClipFrameAssembler.BeginWalkFrame(
                    ClipFrame.NoClip(), outdoorRoot: true),
                [],
                [],
                [],
                default,
                default,
                diagnosticPartition: null);
        }

        public RetailPViewFrameInput? LastInput { get; private set; }

        public bool ThrowOnDraw { get; set; }

        public RetailPViewFrameResult DrawInside(RetailPViewFrameInput input)
        {
            _calls.Add("pview:draw");
            LastInput = input;
            if (ThrowOnDraw)
                throw new InvalidOperationException("pview failed");
            return input.RootCell.IsOutdoorNode ? _outdoorResult : _interiorResult;
        }

        public void AbortFrame() => _calls.Add("pview:abort");
    }

    private sealed class PViewCells : IRetailPViewCellSource
    {
        public LoadedCell? Find(uint cellId) => null;
    }

    private sealed class PassExecutor(List<string> calls) : IWorldScenePassExecutor
    {
        public bool ThrowOnTerrain { get; set; }

        public bool ThrowOnAbort { get; set; }

        public RenderFrameFoundation? SkyFoundation { get; private set; }

        public RenderFrameFoundation? WeatherFoundation { get; private set; }

        public DayGroupData? SkyDayGroup { get; private set; }

        public DayGroupData? WeatherDayGroup { get; private set; }

        public float SkyDayFraction { get; private set; }

        public float WeatherDayFraction { get; private set; }


        public void BeginFrame() => calls.Add("passes:begin");

        public void PrepareFlatWorldClip() => calls.Add("flat:clip");

        public void DrawFlatSky(
            in WorldCameraFrame camera,
            in RenderFrameFoundation foundation,
            DayGroupData? activeDayGroup,
            float dayFraction)
        {
            calls.Add("flat:sky");
            SkyFoundation = foundation;
            SkyDayGroup = activeDayGroup;
            SkyDayFraction = dayFraction;
        }

        public void DrawFlatTerrain(in WorldCameraFrame camera, uint? playerLandblockId)
        {
            calls.Add("flat:terrain");
            if (ThrowOnTerrain)
                throw new InvalidOperationException("terrain failed");
        }

        public void DrawFlatEntities(
            in WorldCameraFrame camera,
            IEnumerable<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
                IReadOnlyList<WorldEntity> Entities,
                IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> entries,
            uint? playerLandblockId,
            HashSet<uint> animatedEntityIds) => calls.Add("flat:entities");

        public void DrawPostWorldParticles(
            LoadedCell? clipRoot,
            ClipFrameAssembly? clipAssembly,
            in WorldCameraFrame camera)
        {
            string kind = clipRoot is null ? "global" : "skipped";
            calls.Add($"particles:{kind}");
        }

        public void DrawFlatWeather(
            in WorldCameraFrame camera,
            in RenderFrameFoundation foundation,
            DayGroupData? activeDayGroup,
            float dayFraction)
        {
            calls.Add("flat:weather");
            WeatherFoundation = foundation;
            WeatherDayGroup = activeDayGroup;
            WeatherDayFraction = dayFraction;
        }

        public void DisableClipDistances() => calls.Add("passes:disable-clip");

        public void AbortFrame()
        {
            calls.Add("passes:abort");
            if (ThrowOnAbort)
                throw new InvalidOperationException("pass abort failed");
        }
    }

    private sealed class Diagnostics(List<string> calls) : IWorldSceneDiagnostics
    {
        public CameraCellResolution CameraCellResolution => CameraCellResolution.None;

        public WorldSceneDiagnosticOutcome DrawAndPublish(
            in WorldCameraFrame camera,
            IReadOnlyList<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax)> bounds)
        {
            calls.Add("diagnostics:draw");
            return new WorldSceneDiagnosticOutcome(3, 8);
        }
    }

    private sealed class NullPreparedAssetSource : IPreparedAssetSource
    {
        public PreparedAssetSourceStats Stats => default;

        public CacheStats DecodedTextureCacheStats => default;

        public PreparedAssetPresence Probe(
            AcDream.Content.Pak.PakAssetType type,
            uint sourceFileId) =>
            PreparedAssetPresence.Missing;

        public PreparedAssetReadResult Read(
            in PreparedAssetRequest request,
            CancellationToken cancellationToken = default) =>
            PreparedAssetReadResult.Missing;

        public void Dispose() { }
    }

    private sealed class NoopDatReaderWriter : IDatReaderWriter
    {
        private readonly StubDatabase _portal = new();
        private readonly StubDatabase _highRes = new();
        private readonly StubDatabase _language = new();
        private readonly StubDatabase _cell = new();

        public string SourceDirectory => string.Empty;

        public IDatDatabase Portal => _portal;

        public IDatDatabase Cell => _cell;

        public ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; } =
            new(new Dictionary<uint, IDatDatabase>());

        public IDatDatabase HighRes => _highRes;

        public IDatDatabase Language => _language;

        public IDatDatabase Local => _language;

        public ReadOnlyDictionary<uint, uint> RegionFileMap { get; } =
            new(new Dictionary<uint, uint>());

        public int PortalIteration => 0;

        public int CellIteration => 0;

        public int HighResIteration => 0;

        public int LanguageIteration => 0;

        public bool TryGetFileBytes(
            uint regionId,
            uint fileId,
            ref byte[] bytes,
            out int bytesRead)
        {
            bytesRead = 0;
            return false;
        }

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
            Array.Empty<uint>();

        public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) =>
            Array.Empty<IDatReaderWriter.IdResolution>();

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        public bool TrySave<T>(
            uint regionId,
            T obj,
            int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : IDBObj => default;

        public bool TryGet<T>(
            uint fileId,
            [MaybeNullWhen(false)] out T value) where T : IDBObj
        {
            value = default;
            return false;
        }

        public void Dispose() { }

        private sealed class StubDatabase : IDatDatabase
        {
            public DatDatabase Db => throw new NotSupportedException();

            public int Iteration => 0;

            public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
                Array.Empty<uint>();

            public bool TryGet<T>(
                uint fileId,
                [MaybeNullWhen(false)] out T value) where T : IDBObj
            {
                value = default;
                return false;
            }

            public bool TryGetFileBytes(
                uint fileId,
                [MaybeNullWhen(false)] out byte[] value)
            {
                value = null;
                return false;
            }

            public bool TryGetFileBytes(
                uint fileId,
                ref byte[] bytes,
                out int bytesRead)
            {
                bytesRead = 0;
                return false;
            }

            public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
                throw new NotSupportedException();

            public void Dispose() { }
        }
    }

    private static WorldRenderFrame CreateFrame(
        LoadedCell? clipRoot,
        bool playerSeenOutside)
    {
        var camera = new FlyCamera { Position = new Vector3(1f, 2f, 3f) };
        var cameraFrame = new WorldCameraFrame(
            camera,
            camera.Projection,
            camera.View * camera.Projection,
            default,
            Matrix4x4.Identity,
            camera.Position);
        var roots = new WorldRootFrame(
            PlayerRoot: null,
            PlayerSeenOutside: playerSeenOutside,
            ViewerCellId: clipRoot?.CellId ?? 0u,
            ViewerEyePosition: camera.Position,
            PlayerViewPosition: camera.Position,
            ViewerRoot: clipRoot,
            CameraInsideCell: clipRoot is not null,
            RootSeenOutside: true,
            PlayerInsideCell: false,
            PlayerLandblockId: null,
            RenderCenterLandblockX: 0,
            RenderCenterLandblockY: 0,
            PlayerCellId: 0x01010002u,
            PlayerIndoorGate: false);
        return new WorldRenderFrame(
            cameraFrame,
            roots,
            new WorldBuildingFrame(null, Array.Empty<LoadedCell>()),
            []);
    }

    private static int RequiredCallIndex(
        IReadOnlyList<CompiledCall> calls,
        Type declaringType,
        string methodName)
    {
        int index = CompiledCallGraph.IndexOf(calls, declaringType, methodName);
        Assert.True(index >= 0, $"Missing compiled call: {declaringType.Name}.{methodName}");
        return index;
    }
}
