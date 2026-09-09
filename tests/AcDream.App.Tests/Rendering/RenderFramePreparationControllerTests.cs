using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class RenderFramePreparationControllerTests
{
    private static readonly RenderFrameInput Input = new(0.25d, 1280, 720);

    [Fact]
    public void Prepare_PreservesResourcesThenDevToolsThenWeatherOrder()
    {
        List<string> calls = [];
        var resources = new Resources(calls);
        var devTools = new DevTools(calls);
        var weather = new Weather(calls);
        var preparation = new RenderFramePreparationController(
            resources,
            devTools,
            weather);

        preparation.Prepare(Input);

        Assert.Equal(["resources", "devtools-begin", "weather"], calls);
        Assert.Equal(Input, resources.Input);
        Assert.Equal((float)Input.DeltaSeconds, devTools.DeltaSeconds);
        Assert.Equal(Input.DeltaSeconds, weather.DeltaSeconds);
    }

    [Fact]
    public void Prepare_DevToolsAreOptionalWithoutChangingRequiredOrder()
    {
        List<string> calls = [];
        var preparation = new RenderFramePreparationController(
            new Resources(calls),
            devTools: null,
            new Weather(calls));

        preparation.Prepare(Input);

        Assert.Equal(["resources", "weather"], calls);
    }

    [Fact]
    public void Constructor_RejectsMissingRequiredOwners()
    {
        var resources = new Resources([]);
        var weather = new Weather([]);

        Assert.Throws<ArgumentNullException>(() =>
            new RenderFramePreparationController(null!, null, weather));
        Assert.Throws<ArgumentNullException>(() =>
            new RenderFramePreparationController(resources, null, null!));
    }

    private sealed class Resources(List<string> calls) : IRenderFrameResourcePhase
    {
        public RenderFrameInput Input { get; private set; }

        public void Prepare(RenderFrameInput input)
        {
            Input = input;
            calls.Add("resources");
        }
    }

    private sealed class DevTools(List<string> calls) : IDevToolsFrameLifecycle
    {
        public float DeltaSeconds { get; private set; }

        public void BeginFrame(float deltaSeconds)
        {
            DeltaSeconds = deltaSeconds;
            calls.Add("devtools-begin");
        }

        public void AbortFrame() => calls.Add("devtools-abort");

        public void Render(double deltaSeconds, int viewportWidth, int viewportHeight) =>
            calls.Add("devtools-render");
    }

    private sealed class Weather(List<string> calls) : IRenderWeatherFramePhase
    {
        public double DeltaSeconds { get; private set; }

        public void Tick(double deltaSeconds)
        {
            DeltaSeconds = deltaSeconds;
            calls.Add("weather");
        }
    }
}
