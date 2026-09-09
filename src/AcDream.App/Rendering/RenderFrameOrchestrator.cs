namespace AcDream.App.Rendering;

internal readonly record struct RenderFrameInput(
    double DeltaSeconds,
    int ViewportWidth,
    int ViewportHeight);

internal readonly record struct WorldRenderFrameOutcome(
    int VisibleLandblocks,
    int TotalLandblocks,
    bool NormalWorldDrawn);

internal readonly record struct PrivatePresentationFrameOutcome(
    bool PortalViewportDrawn,
    bool ScreenshotCaptured);

internal readonly record struct RenderFrameOutcome(
    WorldRenderFrameOutcome World,
    PrivatePresentationFrameOutcome Presentation,
    bool SkippedZeroArea = false)
{
    internal static RenderFrameOutcome ZeroArea => new(default, default, SkippedZeroArea: true);
}

internal interface IRenderFrameLifetime
{
    void BeginFrame();

    void EndFrame();
}

internal interface IBuildingDegradeFrameTick
{
    void Tick(double elapsedSeconds);
}

internal interface IRenderFrameResourcePhase
{
    void Prepare(RenderFrameInput input);
}

internal interface IRenderFrameGpuMeasurement
{
    void BeginFrame();

    void EndFrame();
}

internal interface IWorldSceneFramePhase
{
    WorldRenderFrameOutcome Render(RenderFrameInput input);
}

internal interface IPrivatePresentationFramePhase
{
    PrivatePresentationFrameOutcome Render(
        RenderFrameInput input,
        WorldRenderFrameOutcome world);
}

internal interface IRenderFrameDiagnosticsPhase
{
    void Publish(RenderFrameInput input, RenderFrameOutcome outcome);
}

internal interface IRenderFramePostDiagnosticsPhase
{
    void Process(RenderFrameInput input, RenderFrameOutcome outcome);
}

internal sealed class NullRenderFramePostDiagnosticsPhase :
    IRenderFramePostDiagnosticsPhase
{
    public static NullRenderFramePostDiagnosticsPhase Instance { get; } = new();

    private NullRenderFramePostDiagnosticsPhase()
    {
    }

    public void Process(RenderFrameInput input, RenderFrameOutcome outcome)
    {
    }
}

internal sealed class SerialRenderFramePostDiagnosticsPhase :
    IRenderFramePostDiagnosticsPhase
{
    private readonly IRenderFramePostDiagnosticsPhase[] _phases;

    public SerialRenderFramePostDiagnosticsPhase(
        params IRenderFramePostDiagnosticsPhase[] phases)
    {
        ArgumentNullException.ThrowIfNull(phases);
        if (phases.Length == 0 || phases.Any(static phase => phase is null))
        {
            throw new ArgumentException(
                "At least one non-null post-diagnostic phase is required.",
                nameof(phases));
        }
        _phases = [.. phases];
    }

    public void Process(RenderFrameInput input, RenderFrameOutcome outcome)
    {
        for (int i = 0; i < _phases.Length; i++)
            _phases[i].Process(input, outcome);
    }
}

internal interface IRenderFrameFailureRecovery
{
    void AbortFrame();
}

internal sealed class NullRenderFrameFailureRecovery : IRenderFrameFailureRecovery
{
    public static NullRenderFrameFailureRecovery Instance { get; } = new();

    private NullRenderFrameFailureRecovery()
    {
    }

    public void AbortFrame()
    {
    }
}

internal sealed class RenderFrameOrchestrator : IGameRenderFrameRoot
{
    private readonly IRenderFrameLifetime _lifetime;
    private readonly IRenderFrameGpuMeasurement _gpuMeasurement;
    private readonly IRenderFrameResourcePhase _resources;
    private readonly IWorldSceneFramePhase _world;
    private readonly IPrivatePresentationFramePhase _presentation;
    private readonly IRenderFrameDiagnosticsPhase _diagnostics;
    private readonly IRenderFramePostDiagnosticsPhase _postDiagnostics;
    private readonly IRenderFrameFailureRecovery _recovery;
    private readonly IBuildingDegradeFrameTick? _buildingDegrades;
    private readonly IPrivateFrameScreenshot? _screenshots;

    public RenderFrameOrchestrator(
        IRenderFrameLifetime lifetime,
        IRenderFrameGpuMeasurement gpuMeasurement,
        IRenderFrameResourcePhase resources,
        IWorldSceneFramePhase world,
        IPrivatePresentationFramePhase presentation,
        IRenderFrameDiagnosticsPhase diagnostics,
        IRenderFramePostDiagnosticsPhase postDiagnostics,
        IRenderFrameFailureRecovery recovery,
        IBuildingDegradeFrameTick? buildingDegrades = null,
        IPrivateFrameScreenshot? screenshots = null)
    {
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _gpuMeasurement = gpuMeasurement
            ?? throw new ArgumentNullException(nameof(gpuMeasurement));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _postDiagnostics = postDiagnostics
            ?? throw new ArgumentNullException(nameof(postDiagnostics));
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _buildingDegrades = buildingDegrades;
        _screenshots = screenshots;
    }

    public RenderFrameOutcome Render(RenderFrameInput input)
    {
        // A window with no area has nothing to render and no swapchain to
        // render into; decide that BEFORE a GPU frame is opened so nothing
        // downstream (the render-pack activation extent, the world target,
        // the presentation viewport) ever sees a zero extent.
        if (input.ViewportWidth <= 0 || input.ViewportHeight <= 0)
            return RenderFrameOutcome.ZeroArea;

        _buildingDegrades?.Tick(input.DeltaSeconds);
        _lifetime.BeginFrame();
        WorldRenderFrameOutcome world;
        PrivatePresentationFrameOutcome presentation;
        try
        {
            Exception? measuredRenderFailure = null;
            _gpuMeasurement.BeginFrame();
            try
            {
                _resources.Prepare(input);
                world = _world.Render(input);
                presentation = _presentation.Render(input, world);
            }
            catch (Exception error)
            {
                measuredRenderFailure = error;
                throw;
            }
            finally
            {
                try
                {
                    _gpuMeasurement.EndFrame();
                }
                catch (Exception measurementFailure)
                    when (measuredRenderFailure is not null)
                {
                    throw new AggregateException(
                        "Rendering failed and its GPU measurement could not be closed.",
                        measuredRenderFailure,
                        measurementFailure);
                }
            }
        }
        catch (Exception renderFailure)
        {
            HandleRenderFailure(renderFailure);
            throw;
        }

        _lifetime.EndFrame();
        bool screenshotCaptured = _screenshots?.CapturePending(
            input.ViewportWidth,
            input.ViewportHeight) == true;
        var outcome = new RenderFrameOutcome(
            world,
            presentation with { ScreenshotCaptured = screenshotCaptured });
        _diagnostics.Publish(input, outcome);
        _postDiagnostics.Process(input, outcome);
        return outcome;
    }

    private void HandleRenderFailure(Exception renderFailure)
    {
        Exception? recoveryFailure = null;
        try
        {
            _recovery.AbortFrame();
        }
        catch (Exception error)
        {
            recoveryFailure = error;
        }

        try
        {
            _lifetime.EndFrame();
        }
        catch (Exception closeFailure)
        {
            if (recoveryFailure is not null)
            {
                throw new AggregateException(
                    "Rendering failed and neither presentation recovery nor the in-flight GPU frame could be closed.",
                    renderFailure,
                    recoveryFailure,
                    closeFailure);
            }

            throw new AggregateException(
                "Rendering failed and the in-flight GPU frame could not be closed.",
                renderFailure,
                closeFailure);
        }

        if (recoveryFailure is not null)
        {
            throw new AggregateException(
                "Rendering failed and the presentation frame could not be aborted.",
                renderFailure,
                recoveryFailure);
        }
    }
}
