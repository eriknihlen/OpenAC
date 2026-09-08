using AcDream.Core.Physics;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Hosting;

internal sealed class HeadlessMovementInputSource(
    RuntimeLocalPlayerMovementState movement)
    : IRuntimeMovementInputSource
{
    private readonly RuntimeLocalPlayerMovementState _movement =
        movement ?? throw new ArgumentNullException(nameof(movement));

    public MovementInput Capture()
    {
        if (_movement.HasCommandInput)
            return _movement.CommandInput with { IsPersistentCommand = true };
        return new MovementInput(
            Forward: _movement.AutoRunActive,
            Run: true);
    }
}

internal sealed class HeadlessLocalPlayerFrameHost
    : IRuntimeLocalPlayerFrameHost
{
    private readonly GameRuntime _runtime;
    private readonly LiveSessionHost _session;
    private readonly LocalPlayerOutboundController _outbound;

    internal HeadlessLocalPlayerFrameHost(
        GameRuntime runtime,
        LiveSessionHost session)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _outbound = new LocalPlayerOutboundController(
            static (_, _, _, _, _, _) => { });
    }

    public bool CanAdvancePlayer =>
        _runtime.Session.IsInWorld
        && _runtime.MovementOwner.Controller
            is { CanExecuteLiveMovement: true };

    public PlayerMovementController? Controller =>
        _runtime.MovementOwner.Controller;

    public uint ResolveLocalEntityId()
    {
        uint player = _runtime.PlayerIdentity.ServerGuid;
        return player != 0u
            && _runtime.EntityObjects.Entities.TryGetActive(
                player,
                out var record)
                ? record.LocalEntityId ?? 0u
                : 0u;
    }

    public void HandleTargeting()
    {
    }

    public bool IsHidden
    {
        get
        {
            uint player = _runtime.PlayerIdentity.ServerGuid;
            return player != 0u
                && _runtime.EntityObjects.Entities.TryGetActive(
                    player,
                    out var record)
                && (record.FinalPhysicsState
                    & PhysicsStateFlags.Hidden) != 0;
        }
    }

    public RetailObjectClockDisposition ObjectClockDisposition
    {
        get
        {
            uint player = _runtime.PlayerIdentity.ServerGuid;
            if (player == 0u
                || !_runtime.EntityObjects.Entities.TryGetActive(
                    player,
                    out var record)
                || record.FullCellId == 0u
                || (record.FinalPhysicsState
                    & (PhysicsStateFlags.Frozen
                        | PhysicsStateFlags.Static)) != 0)
            {
                return RetailObjectClockDisposition.Suspend;
            }
            return RetailObjectClockDisposition.Advance;
        }
    }

    public void Project(
        PlayerMovementController controller,
        MovementResult movement,
        bool hidden)
    {
    }

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
