using AcDream.App.Net;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.World;
using AcDream.Core.Physics;

namespace AcDream.App.Input;

internal interface ILocalPlayerPresentationRuntime
{
    bool CanPresentPlayer { get; }
    PlayerMovementController? Controller { get; }
}

internal interface ILocalPlayerFrameRuntime :
    ILocalPlayerPresentationRuntime,
    IRuntimeLocalPlayerFrameHost
{
    bool IRuntimeLocalPlayerFrameHost.CanAdvancePlayer =>
        CanPresentPlayer;
}

internal sealed class LiveLocalPlayerFrameRuntime : ILocalPlayerFrameRuntime
{
    private readonly CameraController _camera;
    private readonly ILocalPlayerModeSource _mode;
    private readonly IRuntimeLocalPlayerControllerSource _controller;
    private readonly IChaseCameraSource _chase;
    private readonly DispatcherMovementInputSource _input;
    private readonly IInputCaptureSource _capture;
    private readonly LiveEntityRuntime _liveEntities;
    private readonly ILocalPlayerIdentitySource _identity;
    private readonly ILocalPlayerPhysicsHostSource _physicsHost;
    private readonly LocalPlayerProjectionController _projection;
    private readonly LocalPlayerOutboundController _outbound;
    private readonly ILiveWorldSessionSource _session;

    public LiveLocalPlayerFrameRuntime(
        CameraController camera,
        ILocalPlayerModeSource mode,
        IRuntimeLocalPlayerControllerSource controller,
        IChaseCameraSource chase,
        DispatcherMovementInputSource input,
        IInputCaptureSource capture,
        LiveEntityRuntime liveEntities,
        ILocalPlayerIdentitySource identity,
        ILocalPlayerPhysicsHostSource physicsHost,
        LocalPlayerProjectionController projection,
        LocalPlayerOutboundController outbound,
        ILiveWorldSessionSource session)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _chase = chase ?? throw new ArgumentNullException(nameof(chase));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _physicsHost = physicsHost ?? throw new ArgumentNullException(nameof(physicsHost));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public bool CanPresentPlayer =>
        !_camera.IsFlyMode
        && _mode.IsPlayerMode
        && _controller.Controller is not null
        && _chase.Legacy is not null
        && _input.IsAvailable
        && !_capture.DevToolsWantCaptureKeyboard;

    public PlayerMovementController? Controller => _controller.Controller;

    public uint ResolveLocalEntityId() =>
        _liveEntities.TryGetWorldEntity(_identity.ServerGuid, out var entity)
            ? entity.Id
            : 0u;

    public void HandleTargeting() => _physicsHost.Host?.HandleTargetting();

    public bool IsHidden => _liveEntities.IsHidden(_identity.ServerGuid);

    public RetailObjectClockDisposition ObjectClockDisposition =>
        _liveEntities.GetRootObjectClockDisposition(_identity.ServerGuid);

    public void Project(
        PlayerMovementController controller,
        MovementResult movement,
        bool hidden) =>
        _projection.Project(controller, movement, hidden);

    public void SendPreNetwork(
        PlayerMovementController controller,
        MovementResult movement,
        bool hidden) =>
        _outbound.SendPreNetworkActions(
            _session.CurrentSession,
            controller,
            movement,
            hidden);

    public void SendPostNetwork(
        PlayerMovementController controller,
        bool hidden) =>
        _outbound.SendPostNetworkPosition(
            _session.CurrentSession,
            controller,
            hidden);
}
