namespace AcDream.App.Rendering;

internal interface IRenderWeatherFramePhase
{
    void Tick(double deltaSeconds);
}

internal interface IDevToolsFrameLifecycle : IRenderFrameFailureRecovery
{
    void BeginFrame(float deltaSeconds);

    void Render(double deltaSeconds, int viewportWidth, int viewportHeight);
}

internal sealed class RenderFramePreparationController : IRenderFrameResourcePhase
{
    private readonly IRenderFrameResourcePhase _resources;
    private readonly IDevToolsFrameLifecycle? _devTools;
    private readonly IRenderWeatherFramePhase _weather;
    private readonly IPrivateEntityViewportResourcePreparation? _privateViewports;

    public RenderFramePreparationController(
        IRenderFrameResourcePhase resources,
        IDevToolsFrameLifecycle? devTools,
        IRenderWeatherFramePhase weather,
        IPrivateEntityViewportResourcePreparation? privateViewports = null)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _devTools = devTools;
        _weather = weather ?? throw new ArgumentNullException(nameof(weather));
        _privateViewports = privateViewports;
    }

    public void Prepare(RenderFrameInput input)
    {
        _resources.Prepare(input);
        _privateViewports?.PrepareResources();
        _devTools?.BeginFrame((float)input.DeltaSeconds);
        _weather.Tick(input.DeltaSeconds);
    }
}
