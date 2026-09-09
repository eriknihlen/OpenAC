using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class RenderFrameRecoveryIntegrationTests
{
    private static readonly RenderFrameInput Input = new(1d / 60d, 1280, 720);

    [Theory]
    [InlineData("devtools-begin")]
    [InlineData("weather")]
    [InlineData("world")]
    [InlineData("portal")]
    [InlineData("paperdoll")]
    [InlineData("gameplay-ui")]
    [InlineData("devtools-render")]
    public void DownstreamFailure_AbortsDevToolsBeforeGpuClose_ThenNextFrameSucceeds(
        string failurePoint)
    {
        var failure = new FailureSwitch { Point = failurePoint };
        var gpu = new GpuLifetime(failure);
        var devTools = new DevTools(failure);
        var preparation = new RenderFramePreparationController(
            new Resources(),
            devTools,
            new Weather(failure));
        var presentation = new PrivatePresentationRenderer(
            new Portal(failure),
            new FoundationSource(),
            new Paperdoll(failure),
            new GameplayUi(failure),
            devTools);
        var orchestrator = new RenderFrameOrchestrator(
            gpu,
            new GpuMeasurement(),
            preparation,
            new World(failure),
            presentation,
            new Diagnostics(),
            NullRenderFramePostDiagnosticsPhase.Instance,
            devTools,
            buildingDegrades: null,
            screenshots: new Screenshots());

        Assert.Throws<InvalidOperationException>(() => orchestrator.Render(Input));

        Assert.False(devTools.IsOpen);
        Assert.Equal(1, devTools.AbortCount);
        Assert.Equal(1, gpu.EndCount);
        Assert.True(devTools.AbortSequence < gpu.LastEndSequence);

        failure.Point = null;
        RenderFrameOutcome outcome = orchestrator.Render(Input);

        Assert.False(devTools.IsOpen);
        Assert.Equal(2, devTools.BeginCount);
        Assert.Equal(2, gpu.EndCount);
        Assert.True(outcome.World.NormalWorldDrawn);
    }

    private sealed class FailureSwitch
    {
        private int _sequence;

        public string? Point { get; set; }

        public int NextSequence() => ++_sequence;

        public void ThrowIf(string point)
        {
            if (Point == point)
                throw new InvalidOperationException(point);
        }
    }

    private sealed class GpuLifetime(FailureSwitch failure) : IRenderFrameLifetime
    {
        public int EndCount { get; private set; }
        public int LastEndSequence { get; private set; }

        public void BeginFrame()
        {
        }

        public void EndFrame()
        {
            EndCount++;
            LastEndSequence = failure.NextSequence();
        }
    }

    private sealed class GpuMeasurement : IRenderFrameGpuMeasurement
    {
        public void BeginFrame()
        {
        }

        public void EndFrame()
        {
        }
    }

    private sealed class DevTools(FailureSwitch failure) : IDevToolsFrameLifecycle
    {
        public bool IsOpen { get; private set; }
        public int BeginCount { get; private set; }
        public int AbortCount { get; private set; }
        public int AbortSequence { get; private set; }

        public void BeginFrame(float deltaSeconds)
        {
            Assert.False(IsOpen);
            IsOpen = true;
            BeginCount++;
            failure.ThrowIf("devtools-begin");
        }

        public void Render(double deltaSeconds, int viewportWidth, int viewportHeight)
        {
            Assert.True(IsOpen);
            failure.ThrowIf("devtools-render");
            IsOpen = false;
        }

        public void AbortFrame()
        {
            if (!IsOpen)
                return;

            AbortCount++;
            AbortSequence = failure.NextSequence();
            IsOpen = false;
        }
    }

    private sealed class Resources : IRenderFrameResourcePhase
    {
        public void Prepare(RenderFrameInput input)
        {
        }
    }

    private sealed class Weather(FailureSwitch failure) : IRenderWeatherFramePhase
    {
        public void Tick(double deltaSeconds) => failure.ThrowIf("weather");
    }

    private sealed class World(FailureSwitch failure) : IWorldSceneFramePhase
    {
        public WorldRenderFrameOutcome Render(RenderFrameInput input)
        {
            failure.ThrowIf("world");
            return new WorldRenderFrameOutcome(1, 1, NormalWorldDrawn: true);
        }
    }

    private sealed class Portal(FailureSwitch failure) : IPrivatePortalViewport
    {
        public void Draw(int width, int height) => failure.ThrowIf("portal");
    }

    private sealed class Paperdoll(FailureSwitch failure) : IPrivateEntityViewportFrame
    {
        public void Render() => failure.ThrowIf("paperdoll");
    }

    private sealed class GameplayUi(FailureSwitch failure) : IRetainedGameplayUiFrame
    {
        public void Render(double deltaSeconds, int width, int height) =>
            failure.ThrowIf("gameplay-ui");
    }

    private sealed class FoundationSource : IRenderFrameFoundationSource
    {
        public RenderFrameFoundation Foundation { get; } = new(
            PortalViewportVisible: false,
            default,
            default);
    }

    private sealed class Screenshots : IPrivateFrameScreenshot
    {
        public bool CapturePending(int width, int height) => false;
    }

    private sealed class Diagnostics : IRenderFrameDiagnosticsPhase
    {
        public void Publish(RenderFrameInput input, RenderFrameOutcome outcome)
        {
        }
    }
}
