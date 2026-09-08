using System.Numerics;
using AcDream.App.Diagnostics;
using AcDream.App.Streaming;
using AcDream.App.UI;
using Silk.NET.Input;

namespace AcDream.App.Rendering;

internal interface IPrivatePortalViewport
{
    void Draw(int width, int height);
}

internal interface IPrivateEntityViewportFrame
{
    void Render();
}

internal interface IPrivateEntityViewportResourcePreparation
{
    void PrepareResources();
}

internal interface IRetainedGameplayUiFrame
{
    void Render(double deltaSeconds, int width, int height);
}

internal interface IPrivateFrameScreenshot
{
    bool CapturePending(int width, int height);
}

internal sealed class PrivatePresentationRenderer : IPrivatePresentationFramePhase
{
    private readonly IPrivatePortalViewport _portal;
    private readonly IRenderFrameFoundationSource _foundation;
    private readonly IPrivateEntityViewportFrame? _entityViewports;
    private readonly IRetainedGameplayUiFrame? _gameplayUi;
    private readonly IDevToolsFrameLifecycle? _devTools;

    public PrivatePresentationRenderer(
        IPrivatePortalViewport portal,
        IRenderFrameFoundationSource foundation,
        IPrivateEntityViewportFrame? entityViewports,
        IRetainedGameplayUiFrame? gameplayUi,
        IDevToolsFrameLifecycle? devTools)
    {
        _portal = portal ?? throw new ArgumentNullException(nameof(portal));
        _foundation = foundation
            ?? throw new ArgumentNullException(nameof(foundation));
        _entityViewports = entityViewports;
        _gameplayUi = gameplayUi;
        _devTools = devTools;
    }

    public PrivatePresentationFrameOutcome Render(
        RenderFrameInput input,
        WorldRenderFrameOutcome world)
    {
        _ = world;
        bool portalViewportVisible =
            _foundation.Foundation.PortalViewportVisible;
        _portal.Draw(input.ViewportWidth, input.ViewportHeight);
        _entityViewports?.Render();
        _gameplayUi?.Render(
            input.DeltaSeconds,
            input.ViewportWidth,
            input.ViewportHeight);
        _devTools?.Render(
            input.DeltaSeconds,
            input.ViewportWidth,
            input.ViewportHeight);
        return new PrivatePresentationFrameOutcome(
            portalViewportVisible,
            ScreenshotCaptured: false);
    }
}

internal sealed class PrivateEntityViewportFrameGroup :
    IPrivateEntityViewportFrame
{
    private readonly IPrivateEntityViewportFrame[] _frames;

    public PrivateEntityViewportFrameGroup(
        params IPrivateEntityViewportFrame?[] frames)
    {
        _frames = frames.Where(static frame => frame is not null)
            .Cast<IPrivateEntityViewportFrame>()
            .ToArray();
    }

    public void Render()
    {
        foreach (IPrivateEntityViewportFrame frame in _frames)
            frame.Render();
    }
}

internal sealed class LocalPlayerPortalViewport : IPrivatePortalViewport
{
    private readonly LocalPlayerTeleportController _teleport;
    private readonly CameraController _camera;

    public LocalPlayerPortalViewport(
        LocalPlayerTeleportController teleport,
        CameraController camera)
    {
        _teleport = teleport ?? throw new ArgumentNullException(nameof(teleport));
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
    }

    public void Draw(int width, int height) =>
        _teleport.DrawPortalViewport(width, height, _camera.Active.Projection);
}

internal sealed class RetainedGameplayUiFrame : IRetainedGameplayUiFrame
{
    private readonly RetailUiRuntime _runtime;
    private readonly IInputContext? _input;

    public RetainedGameplayUiFrame(
        RetailUiRuntime runtime,
        IInputContext? input)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _input = input;
    }

    public void Render(double deltaSeconds, int width, int height)
    {
        _runtime.Tick(deltaSeconds);
        if (_input is not null)
            _runtime.UpdateCursor(_input.Mice);
        _runtime.Draw(new Vector2(width, height));
    }
}

internal sealed class PrivateFrameScreenshot : IPrivateFrameScreenshot
{
    private readonly FrameScreenshotController _screenshots;

    public PrivateFrameScreenshot(FrameScreenshotController screenshots) =>
        _screenshots = screenshots
            ?? throw new ArgumentNullException(nameof(screenshots));

    public bool CapturePending(int width, int height) =>
        _screenshots.CapturePending(width, height);
}
