using System.Reflection;
using System.Reflection.Emit;
using AcDream.App.Diagnostics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.App.Tests.Architecture;
using AcDream.App.World;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering;

public sealed class RenderFrameResourceControllerTests
{
    [Fact]
    public void Prepare_runs_begin_clear_and_live_in_order_for_one_gpu_slot()
    {
        List<string> calls = [];
        var clearResult = new RenderFrameFoundation(
            PortalViewportVisible: true,
            Sky: default,
            Atmosphere: default);
        var slots = new AdvancingSlotSource(2);
        var controller = new RenderFrameResourceController(
            slots,
            new RecordingBegin(calls),
            new RecordingClear(calls, clearResult),
            new RecordingLive(calls));

        controller.Prepare(new RenderFrameInput(1d / 60d, 1280, 720));

        Assert.Equal(["begin:2", "clear", "live:2"], calls);
        Assert.Equal(1, slots.ReadCount);
        Assert.Equal(clearResult, controller.Foundation);
    }

    [Fact]
    public void Required_phase_dependencies_fail_fast()
    {
        var slots = new AdvancingSlotSource(0);
        var begin = new RecordingBegin([]);
        var clear = new RecordingClear([], default);
        var live = new RecordingLive([]);

        Assert.Throws<ArgumentNullException>(() =>
            new RenderFrameResourceController(null!, begin, clear, live));
        Assert.Throws<ArgumentNullException>(() =>
            new RenderFrameResourceController(slots, null!, clear, live));
        Assert.Throws<ArgumentNullException>(() =>
            new RenderFrameResourceController(slots, begin, null!, live));
        Assert.Throws<ArgumentNullException>(() =>
            new RenderFrameResourceController(slots, begin, clear, null!));
    }

    [Fact]
    public void Production_begin_resources_preserve_the_exact_frame_order()
    {
        MethodInfo begin = RequiredMethod(
            typeof(RuntimeRenderFrameBeginResources),
            nameof(RuntimeRenderFrameBeginResources.Begin));
        AssertCallOrder(
            begin,
            (typeof(TextureCache), nameof(TextureCache.BeginCompositeTextureFrame)),
            (typeof(TextureCache), nameof(TextureCache.TickCompositeTextureCache)),
            (typeof(WbDrawDispatcher), nameof(WbDrawDispatcher.BeginFrame)),
            (typeof(EnvCellRenderer), nameof(EnvCellRenderer.BeginFrame)),
            (typeof(PortalDepthMaskRenderer), nameof(PortalDepthMaskRenderer.BeginFrame)),
            (typeof(ClipFrame), nameof(ClipFrame.BeginFrame)),
            (typeof(TerrainModernRenderer), nameof(TerrainModernRenderer.BeginFrame)),
            (typeof(SceneLightingUboBinding), nameof(SceneLightingUboBinding.BeginFrame)));
        Assert.DoesNotContain(
            CompiledCallGraph.Read(begin),
            call => call.Target.DeclaringType == typeof(FrameProfiler)
                && call.Target.Name == "FrameBoundary");
    }

    [Fact]
    public void Production_live_preparation_publishes_meshes_before_reveal_and_particles()
    {
        AssertCallOrder(
            RequiredMethod(
                typeof(RuntimeRenderFrameLivePreparation),
                nameof(RuntimeRenderFrameLivePreparation.Prepare)),
            (typeof(WbMeshAdapter), nameof(WbMeshAdapter.Tick)),
            (typeof(WorldRevealCoordinator), nameof(WorldRevealCoordinator.PrepareAndEvaluate)),
            (typeof(ParticleRenderer), nameof(ParticleRenderer.BeginFrame)));
    }


    [Fact]
    public void Weather_frame_clock_advances_only_after_the_weather_tick()
    {
        var controller = new RenderWeatherFrameController(
            new AcDream.Core.World.WorldTimeService(
                AcDream.Core.World.SkyStateProvider.Default()),
            new AcDream.Core.World.WeatherSystem());

        controller.Tick(0.25d);
        controller.Tick(0.5d);

        Assert.Equal(0.75d, controller.ElapsedSeconds);
        MethodInfo tick = RequiredMethod(
            typeof(RenderWeatherFrameController),
            nameof(RenderWeatherFrameController.Tick));
        CompiledCall weather = Assert.Single(
            CompiledCallGraph.Read(tick),
            call => call.Target.DeclaringType == typeof(WeatherSystem)
                && call.Target.Name == nameof(WeatherSystem.Tick));
        CompiledFieldReference elapsedStore = Assert.Single(
            CompiledCallGraph.ReadFieldReferences(tick),
            reference => reference.Field.Name == "_elapsedSeconds"
                && reference.OpCode == OpCodes.Stfld);
        Assert.True(weather.Offset < elapsedStore.Offset);
    }

    [Fact]
    public void Login_state_is_latched_by_the_shared_player_mode_source()
    {
        var mode = new AcDream.App.Input.LocalPlayerModeState();
        var live = new AcDream.App.Rendering.RenderLoginStateSource(
            liveMode: true,
            mode);
        var offline = new AcDream.App.Rendering.RenderLoginStateSource(
            liveMode: false,
            mode);

        Assert.True(live.IsWaitingForLogin);
        Assert.False(offline.IsWaitingForLogin);

        mode.ChaseModeEverEntered = true;

        Assert.False(live.IsWaitingForLogin);
    }

    private static MethodInfo RequiredMethod(Type owner, string name) =>
        owner.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(owner.FullName, name);

    private static void AssertCallOrder(
        MethodBase method,
        params (Type Type, string Method)[] expected)
    {
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(method);
        int cursor = -1;
        foreach ((Type type, string name) in expected)
        {
            int found = Enumerable.Range(cursor + 1, calls.Count - cursor - 1)
                .FirstOrDefault(index => calls[index].Target.DeclaringType == type
                    && calls[index].Target.Name == name, -1);
            Assert.True(found > cursor,
                $"Missing compiled edge after {cursor}: {type.FullName}.{name}.");
            cursor = found;
        }
    }

    private sealed class AdvancingSlotSource(int firstSlot) : IRenderFrameSlotSource
    {
        public int ReadCount { get; private set; }

        public int CurrentSlot => firstSlot + ReadCount++;
    }

    private sealed class RecordingBegin(List<string> calls) : IRenderFrameBeginResources
    {
        public void Begin(int gpuSlot) => calls.Add($"begin:{gpuSlot}");
    }

    private sealed class RecordingClear(
        List<string> calls,
        RenderFrameFoundation result) : IRenderFrameClearPhase
    {
        public RenderFrameFoundation Clear()
        {
            calls.Add("clear");
            return result;
        }
    }

    private sealed class RecordingLive(List<string> calls) : IRenderFrameLivePreparation
    {
        public void Prepare(int gpuSlot) => calls.Add($"live:{gpuSlot}");
    }
}
