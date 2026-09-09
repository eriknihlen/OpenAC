using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Net;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Update;
using AcDream.App.World;
using AcDream.Core.Net.Messages;
using AcDream.Core.Audio;
using AcDream.Core.Physics;
using AcDream.Core.Rendering;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;
using AcDream.Runtime.World;

namespace AcDream.App.Streaming;

internal interface ILocalPlayerTeleportNetworkSink
{
    void OnTeleportStarted(uint sequence);

    void OfferDestination(
        RuntimeTeleportDestination destination,
        bool teleportTimestampAdvanced);

    void OnLocalPlayerFirstEntryCompleted();

    void ArmLoginTunnel();

    void RequestLogout();

    void ResetSession();

    void ResetGenerationPresentation();
}

internal sealed class DeferredLocalPlayerTeleportNetworkSink
    : ILocalPlayerTeleportNetworkSink
{
    private ILocalPlayerTeleportNetworkSink? _inner;

    public void Bind(ILocalPlayerTeleportNetworkSink inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (Interlocked.CompareExchange(ref _inner, inner, null) is not null)
            throw new InvalidOperationException("The local teleport sink is already bound.");
    }

    public IDisposable BindOwned(ILocalPlayerTeleportNetworkSink inner)
    {
        Bind(inner);
        return new Binding(this, inner);
    }

    private void Unbind(ILocalPlayerTeleportNetworkSink expected)
    {
        _ = Interlocked.CompareExchange(ref _inner, null, expected);
    }

    public void OnTeleportStarted(uint sequence) => Required().OnTeleportStarted(sequence);

    public void OfferDestination(
        RuntimeTeleportDestination destination,
        bool teleportTimestampAdvanced) =>
        Required().OfferDestination(destination, teleportTimestampAdvanced);

    public void OnLocalPlayerFirstEntryCompleted() =>
        Required().OnLocalPlayerFirstEntryCompleted();

    public void ArmLoginTunnel() => Required().ArmLoginTunnel();

    public void RequestLogout() => Required().RequestLogout();

    public void ResetSession() => Required().ResetSession();

    public void ResetGenerationPresentation() =>
        Required().ResetGenerationPresentation();

    private ILocalPlayerTeleportNetworkSink Required() =>
        _inner ?? throw new InvalidOperationException(
            "The local teleport sink was used before composition completed.");

    private sealed class Binding : IDisposable
    {
        private DeferredLocalPlayerTeleportNetworkSink? _owner;
        private readonly ILocalPlayerTeleportNetworkSink _expected;

        public Binding(
            DeferredLocalPlayerTeleportNetworkSink owner,
            ILocalPlayerTeleportNetworkSink expected)
        {
            _owner = owner;
            _expected = expected;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unbind(_expected);
    }
}

internal interface ILocalPlayerTeleportInputLifetime
{
    void EndMouseLook();
}

internal interface ILocalPlayerTeleportModeOperations
{
    PlayerMovementController? Controller { get; }
    Matrix4x4 Projection { get; }
    bool TryEnterPortalSpace();

    bool TryEnterPortalSpaceForLogin();
    void EnterWorld();
}

internal interface ILocalPlayerTeleportAuthority
{
    bool IsFreshStart(ushort sequence);
}

internal interface ILocalPlayerLoginLifecycleSource
{
    RuntimeCharacterSelectionLifecycle SelectionLifecycle { get; }
}

internal sealed class RuntimeLoginLifecycleSource
    : ILocalPlayerLoginLifecycleSource
{
    private readonly GameRuntime _runtime;

    public RuntimeLoginLifecycleSource(GameRuntime runtime) =>
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public RuntimeCharacterSelectionLifecycle SelectionLifecycle =>
        _runtime.CharacterSelection.Snapshot.Lifecycle;
}

internal interface ILocalPlayerLogoutOperations
{
    bool IsLocalPlayerKiller { get; }

    bool BeginCharacterLogOff();

    /// <summary>The server's opcode-only 0xF653 echo has landed.</summary>
    bool IsCharacterLogOffConfirmed { get; }

    bool CompleteCharacterLogOff();
}

internal sealed class RuntimeLocalPlayerLogoutOperations
    : ILocalPlayerLogoutOperations
{
    private readonly GameRuntime _runtime;
    private readonly RuntimeLocalPlayerMovementState _movement;
    private readonly ILiveWorldSessionSource _session;
    private readonly AcDream.Core.Items.ClientObjectTable _objects;
    private readonly ILocalPlayerIdentitySource _identity;

    public RuntimeLocalPlayerLogoutOperations(
        GameRuntime runtime,
        RuntimeLocalPlayerMovementState movement,
        ILiveWorldSessionSource session,
        AcDream.Core.Items.ClientObjectTable objects,
        ILocalPlayerIdentitySource identity)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public bool IsLocalPlayerKiller
    {
        get
        {
            var bitfield = (AcDream.Core.Items.PublicWeenieFlags)(_objects.Get(_identity.ServerGuid)
                ?.PublicWeenieBitfield ?? 0u);
            return (bitfield & AcDream.Core.Items.PublicWeenieFlags.PlayerKiller) != 0
                || (bitfield & AcDream.Core.Items.PublicWeenieFlags.PlayerKillerLite) != 0;
        }
    }

    public bool BeginCharacterLogOff()
    {
        if (!_runtime.Session.BeginCharacterLogOff(_runtime.Generation)
                .Accepted)
        {
            return false;
        }

        _runtime.CommunicationOwner.AddText(
            "Logging off...",
            AcDream.Core.Chat.RetailLogTextType.Default);
        _movement.DisableCommandInterpreter();
        return true;
    }

    public bool IsCharacterLogOffConfirmed =>
        _session.CurrentSession?.IsCharacterLogOffConfirmed == true;

    public bool CompleteCharacterLogOff() =>
        _runtime.Session.CompleteCharacterLogOff(_runtime.Generation)
            .Accepted;
}

internal sealed class LiveLocalPlayerTeleportAuthority
    : ILocalPlayerTeleportAuthority
{
    private readonly LiveEntityRuntime _liveEntities;
    private readonly ILocalPlayerIdentitySource _identity;

    public LiveLocalPlayerTeleportAuthority(
        LiveEntityRuntime liveEntities,
        ILocalPlayerIdentitySource identity)
    {
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public bool IsFreshStart(ushort sequence) =>
        _liveEntities.IsFreshTeleportStart(_identity.ServerGuid, sequence);
}

internal interface ILocalPlayerTeleportStreamingOperations
{
    int CenterX { get; }
    int CenterY { get; }
    bool IsRecenterPending { get; }
    bool BeginRecenter(int x, int y, bool isSealedDungeon);
    bool ResetRecenter(bool sessionEnding);
    bool IsSealedDungeon(uint cellId);
}

internal sealed class LocalPlayerTeleportStreamingOperations
    : ILocalPlayerTeleportStreamingOperations
{
    private readonly LiveWorldOriginState _origin;
    private readonly StreamingOriginRecenterCoordinator _recenter;
    private readonly StreamingController _streaming;
    private readonly ISealedDungeonCellClassifier _sealedDungeonCells;

    public LocalPlayerTeleportStreamingOperations(
        LiveWorldOriginState origin,
        StreamingOriginRecenterCoordinator recenter,
        StreamingController streaming,
        ISealedDungeonCellClassifier sealedDungeonCells)
    {
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _recenter = recenter ?? throw new ArgumentNullException(nameof(recenter));
        _streaming = streaming ?? throw new ArgumentNullException(nameof(streaming));
        _sealedDungeonCells = sealedDungeonCells
            ?? throw new ArgumentNullException(nameof(sealedDungeonCells));
    }

    public int CenterX => _origin.CenterX;
    public int CenterY => _origin.CenterY;
    public bool IsRecenterPending => _recenter.IsPending;

    public bool BeginRecenter(int x, int y, bool isSealedDungeon) =>
        _recenter.Begin(x, y, isSealedDungeon);

    public bool ResetRecenter(bool sessionEnding) =>
        _recenter.Reset(sessionEnding);

    public bool IsSealedDungeon(uint cellId) =>
        _sealedDungeonCells.IsSealedDungeon(cellId);

}

internal interface ILocalPlayerTeleportPlacement
{
    void Place(Quaternion rotation);
}

internal sealed class LocalPlayerTeleportPlacement : ILocalPlayerTeleportPlacement
{
    private readonly LiveEntityRuntime _liveEntities;
    private readonly ILocalPlayerIdentitySource _identity;
    private readonly IRuntimeLocalPlayerControllerSource _controller;
    private readonly ILocalPlayerPhysicsHostSource _host;
    private readonly ChaseCameraInputState _cameras;
    private readonly ILiveSpatialReconcilePhase _spatial;

    public LocalPlayerTeleportPlacement(
        LiveEntityRuntime liveEntities,
        ILocalPlayerIdentitySource identity,
        IRuntimeLocalPlayerControllerSource controller,
        ILocalPlayerPhysicsHostSource host,
        ChaseCameraInputState cameras,
        ILiveSpatialReconcilePhase spatial)
    {
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _cameras = cameras ?? throw new ArgumentNullException(nameof(cameras));
        _spatial = spatial ?? throw new ArgumentNullException(nameof(spatial));
    }

    public void Place(Quaternion rotation)
    {
        PlayerMovementController controller = _controller.Controller
            ?? throw new InvalidOperationException(
                "Teleport Place ran without the local player controller.");

        uint playerGuid = _identity.ServerGuid;
        if (_liveEntities.TryGetWorldEntity(
                playerGuid,
                out WorldEntity? entity))
        {
            entity.SetPosition(controller.Position);
            entity.ParentCellId = controller.CellId;
            entity.Rotation = rotation;

            if (!_liveEntities.RebucketLiveEntity(playerGuid, controller.CellId))
            {
                throw new InvalidOperationException(
                    $"Teleport Place could not commit local player 0x{playerGuid:X8} "
                    + $"to destination cell 0x{controller.CellId:X8}.");
            }
        }

        _host.Host?.NotifyTeleported();

        _cameras.Legacy?.Update(controller.Position, controller.Yaw);
        _cameras.Retail?.ResetViewerToPlayer(controller.Position, controller.Yaw);
        _spatial.Reconcile();

        PhysicsDiagnostics.LogTeleport(
            "PLACED",
            controller.CellId,
            "readiness=complete");
        Console.WriteLine(
            $"live: teleport materialized - snapped to {controller.Position} "
            + $"cell=0x{controller.CellId:X8}");
    }
}

internal interface ILocalPlayerTeleportSession
{
    void SendLoginComplete();
}

internal sealed class LocalPlayerTeleportSession : ILocalPlayerTeleportSession
{
    private readonly ILiveWorldSessionSource _session;

    public LocalPlayerTeleportSession(ILiveWorldSessionSource session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    public void SendLoginComplete() =>
        _session.CurrentSession?.SendGameAction(
            GameActionLoginComplete.Build());
}

internal interface ILocalPlayerTeleportPresentation : IDisposable
{
    bool IsPortalViewportVisible { get; }
    int CurrentTunnelFrame { get; }
    void Begin(Matrix4x4 projection);

    void BeginLogout(Matrix4x4 projection);
    (TeleportAnimSnapshot Snapshot, IReadOnlyList<TeleportAnimEvent> Events)
        Tick(float deltaSeconds, bool worldReady);
    void TickTunnel(float deltaSeconds);
    void PlayEnterCue();
    void PlayExitCue();
    void EnterTunnel();
    void ExitTunnel();
    void SetWaitCue(bool visible);
    void Reset();
    Matrix4x4 ApplyViewPlane(Matrix4x4 projection);
    ICamera ApplyViewPlane(ICamera camera);
    void DrawPortalViewport(int width, int height, Matrix4x4 projection);
}

internal sealed class LocalPlayerTeleportPresentation
    : ILocalPlayerTeleportPresentation
{
    private readonly TeleportAnimSequencer _animation = new();
    private readonly TeleportViewPlaneController _viewPlane = new();
    private readonly PortalTunnelPresentation _tunnel;

    public LocalPlayerTeleportPresentation(PortalTunnelPresentation tunnel) =>
        _tunnel = tunnel ?? throw new ArgumentNullException(nameof(tunnel));

    public bool IsPortalViewportVisible => _tunnel.IsVisible;
    public int CurrentTunnelFrame => _tunnel.CurrentAnimationFrame;

    public void Begin(Matrix4x4 projection)
    {
        _viewPlane.Begin(projection);
        _animation.Begin(TeleportEntryKind.Portal);
    }

    public void BeginLogout(Matrix4x4 projection)
    {
        _viewPlane.Begin(projection);
        _animation.Begin(TeleportEntryKind.Logout);
    }

    public (TeleportAnimSnapshot Snapshot, IReadOnlyList<TeleportAnimEvent> Events)
        Tick(float deltaSeconds, bool worldReady)
    {
        var (snapshot, events) = _animation.Tick(
            deltaSeconds,
            worldReady,
            CurrentTunnelFrame,
            holdInTunnel: StreamingDiagnostics.TunnelFreezeFrame.HasValue);

        if (!snapshot.ShowTunnel && _tunnel.IsVisible)
            _tunnel.Exit();

        _viewPlane.Update(snapshot);
        return (snapshot, events);
    }

    public void TickTunnel(float deltaSeconds) => _tunnel.Tick(deltaSeconds);

    public Action<SoundId>? UiSoundSink { get; set; }

    public void PlayEnterCue() => UiSoundSink?.Invoke(SoundId.UI_EnterPortal);

    public void PlayExitCue() => UiSoundSink?.Invoke(SoundId.UI_ExitPortal);

    public void EnterTunnel() => _tunnel.Enter();

    public void ExitTunnel() => _tunnel.Exit();
    public void SetWaitCue(bool visible) => _tunnel.SetWaitCue(visible);

    public void Reset()
    {
        _animation.Reset();
        _viewPlane.Reset();
        _tunnel.Exit();
    }

    public Matrix4x4 ApplyViewPlane(Matrix4x4 projection) =>
        _viewPlane.Apply(projection);

    public ICamera ApplyViewPlane(ICamera camera) => _viewPlane.ApplyTo(camera);

    public void DrawPortalViewport(int width, int height, Matrix4x4 projection) =>
        _tunnel.Draw(width, height, ApplyViewPlane(projection));

    public void Dispose() => _tunnel.Dispose();
}

internal sealed class LocalPlayerTeleportController
    : ILocalPlayerTeleportFramePhase,
      ILocalPlayerTeleportNetworkSink,
      AcDream.App.Interaction.ISelectionViewPlaneSource,
      IDisposable
{
    private readonly RuntimeWorldTransitState _transit;
    private readonly ILocalPlayerTeleportAuthority _authority;
    private readonly ILocalPlayerTeleportInputLifetime _input;
    private readonly ILocalPlayerTeleportModeOperations _mode;
    private readonly ILocalPlayerTeleportStreamingOperations _streaming;
    private readonly WorldRevealCoordinator _worldReveal;
    private readonly ILocalPlayerTeleportPlacement _placement;
    private readonly ILocalPlayerTeleportSession _session;
    private readonly ILocalPlayerTeleportPresentation _presentation;
    private readonly RuntimeAcceptedPositionDriveController _acceptedPositionDrive;

    private uint _pendingCell;
    private Quaternion _pendingRotation = Quaternion.Identity;
    private long _pendingRevealGeneration;
    private RuntimeTeleportDestination _pendingDestination;
    private bool _hasPendingDestination;

    private bool _placementCommitted;

    private bool _awaitingDeferredWake;

    private float _holdSeconds;
    private long _lifetimeGeneration;
    private bool _disposed;

    private long _loginRevealGeneration;
    private bool _loginPresentationActive;

    private bool _loginTunnelArmed;

    private bool _loginPlacementCompleted;
    private float _loginHoldSeconds;

    private readonly ILocalPlayerLoginLifecycleSource _loginLifecycle;

    private readonly ILocalPlayerLogoutOperations _logout;

    private bool _logoutStreamingRetirementPrepared;

    public LocalPlayerTeleportController(
        ILocalPlayerTeleportAuthority authority,
        ILocalPlayerTeleportInputLifetime input,
        ILocalPlayerTeleportModeOperations mode,
        ILocalPlayerTeleportStreamingOperations streaming,
        RuntimeWorldTransitState transit,
        WorldRevealCoordinator worldReveal,
        ILocalPlayerTeleportPlacement placement,
        ILocalPlayerTeleportSession session,
        ILocalPlayerTeleportPresentation presentation,
        RuntimeAcceptedPositionDriveController acceptedPositionDrive,
        ILocalPlayerLoginLifecycleSource loginLifecycle,
        ILocalPlayerLogoutOperations logout)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _streaming = streaming ?? throw new ArgumentNullException(nameof(streaming));
        _transit = transit ?? throw new ArgumentNullException(nameof(transit));
        _worldReveal = worldReveal ?? throw new ArgumentNullException(nameof(worldReveal));
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        _acceptedPositionDrive = acceptedPositionDrive
            ?? throw new ArgumentNullException(nameof(acceptedPositionDrive));
        _loginLifecycle = loginLifecycle
            ?? throw new ArgumentNullException(nameof(loginLifecycle));
        _logout = logout ?? throw new ArgumentNullException(nameof(logout));
    }

    public bool IsActive => _transit.IsTeleportActive;
    public bool IsPortalViewportVisible => _presentation.IsPortalViewportVisible;

    public uint ActiveDestinationCell
    {
        get
        {
            if (_transit.IsTeleportActive)
                return _pendingCell;
            if (_loginPresentationActive)
            {
                RuntimePortalSnapshot snapshot = _transit.Snapshot;
                if (snapshot.Kind == RuntimePortalKind.Login
                    && snapshot.Generation == _loginRevealGeneration)
                {
                    return snapshot.Readiness.DestinationCell;
                }
            }
            return 0u;
        }
    }

    public void OnTeleportStarted(uint sequence)
    {
        ThrowIfDisposed();
        if (_transit.IsLogoutActive)
        {
            Console.WriteLine(
                $"live: teleport start ignored during logout (seq={sequence})");
            return;
        }
        ushort teleportSequence = (ushort)sequence;
        if (!_authority.IsFreshStart(teleportSequence)
            || !_transit.CanQueueTeleportStart(teleportSequence))
        {
            return;
        }

        long observedGeneration = _lifetimeGeneration;
        _input.EndMouseLook();
        if (_lifetimeGeneration != observedGeneration)
            return;

        long resetGeneration = ResetTransit(clearSession: false);
        if (_lifetimeGeneration != resetGeneration)
            return;

        if (!_transit.TryQueueTeleportStart(teleportSequence))
            return;
        TryActivatePendingPresentation();
        Console.WriteLine($"live: teleport queued (seq={sequence})");
    }

    public void OfferDestination(
        RuntimeTeleportDestination destination,
        bool teleportTimestampAdvanced)
    {
        ThrowIfDisposed();
        _transit.OfferTeleportDestination(
            destination,
            teleportTimestampAdvanced);
        TryAimAcceptedDestination();
    }

    public void OnLocalPlayerFirstEntryCompleted()
    {
        ThrowIfDisposed();
        _loginPlacementCompleted = true;
    }

    public void ArmLoginTunnel()
    {
        ThrowIfDisposed();
        if (_loginTunnelArmed
            || _loginPresentationActive
            || _transit.IsTeleportActive
            || _transit.HasPendingTeleportStart)
        {
            return;
        }

        long generation = _lifetimeGeneration;
        _presentation.Begin(_mode.Projection);
        if (_lifetimeGeneration != generation)
            return;

        var (_, events) = _presentation.Tick(0f, worldReady: false);
        if (_lifetimeGeneration != generation)
            return;
        if (!ProcessArmedLoginTunnelEvents(events, generation))
            return;

        _loginTunnelArmed = true;
        _loginHoldSeconds = 0f;
        Console.WriteLine("live: login tunnel armed at enter click");
    }

    private bool ProcessArmedLoginTunnelEvents(
        IReadOnlyList<TeleportAnimEvent> events,
        long generation)
    {
        foreach (TeleportAnimEvent teleportEvent in events)
        {
            switch (teleportEvent)
            {
                case TeleportAnimEvent.PlayEnterSound:
                    Console.WriteLine(
                        "live: login portal-space enter cue "
                        + "(Sound_UI_EnterPortal)");
                    _presentation.PlayEnterCue();
                    if (_lifetimeGeneration != generation)
                        return false;
                    break;
                case TeleportAnimEvent.EnterTunnel:
                    _presentation.EnterTunnel();
                    if (_lifetimeGeneration != generation)
                        return false;
                    break;
                default:
                    break;
            }
        }

        return true;
    }


    public void RequestLogout()
    {
        if (!TryRequestLogout())
            Console.WriteLine("live: character logoff request refused");
    }

    public bool TryRequestLogout()
    {
        ThrowIfDisposed();
        if (_transit.IsLogoutActive
            || _transit.IsTeleportActive
            || _transit.HasPendingTeleportStart
            || _loginPresentationActive
            || _loginTunnelArmed)
        {
            return false;
        }

        _logoutStreamingRetirementPrepared = false;

        if (!_transit.TryBeginLogoutRequest(_logout.IsLocalPlayerKiller))
            return false;

        long generation = _lifetimeGeneration;
        if (!_logout.BeginCharacterLogOff())
        {
            // Nothing went on the wire — roll the request back rather than
            // running a wormhole for a logoff the server never heard.
            if (_lifetimeGeneration == generation)
                _transit.CancelLogoutRequest();
            return false;
        }
        if (_lifetimeGeneration != generation)
            return true;

        _input.EndMouseLook();
        Console.WriteLine("live: character logoff requested");
        return true;
    }

    private void TickLogout(float deltaSeconds)
    {
        long generation = _lifetimeGeneration;

        if (_transit.LogoutStage is RuntimeLogoutStage.Requested
                or RuntimeLogoutStage.PresentationActive
            && _logout.IsCharacterLogOffConfirmed)
        {
            _transit.AcknowledgeLogoutConfirmed();
        }

        switch (_transit.LogoutStage)
        {
            case RuntimeLogoutStage.Requested:
                // The 3 s (23 s PK) hold: the server-broadcast LogOut
                // motion is playing on the player in-world.
                if (_transit.AdvanceLogoutHold(deltaSeconds))
                {
                    _presentation.BeginLogout(_mode.Projection);
                    if (_lifetimeGeneration != generation)
                        return;
                    PumpLogoutPresentation(0f, generation);
                }
                return;
            case RuntimeLogoutStage.PresentationActive:
                PumpLogoutPresentation(deltaSeconds, generation);
                return;
            case RuntimeLogoutStage.Confirmed:
                CompleteLogoutHandoff(generation);
                return;
            default:
                return;
        }
    }

    private void PumpLogoutPresentation(float deltaSeconds, long generation)
    {
        var (_, events) = _presentation.Tick(deltaSeconds, worldReady: false);
        if (_lifetimeGeneration != generation)
            return;

        foreach (TeleportAnimEvent teleportEvent in events)
        {
            switch (teleportEvent)
            {
                case TeleportAnimEvent.PlayEnterSound:
                    Console.WriteLine(
                        "live: logout portal-space enter cue "
                        + "(Sound_UI_EnterPortal)");
                    _presentation.PlayEnterCue();
                    if (_lifetimeGeneration != generation)
                        return;
                    break;
                case TeleportAnimEvent.EnterTunnel:
                    _presentation.EnterTunnel();
                    if (_lifetimeGeneration != generation)
                        return;
                    break;
                default:
                    break;
            }
        }

        _presentation.TickTunnel(deltaSeconds);
    }

    private void CompleteLogoutHandoff(long generation)
    {
        if (!_streaming.ResetRecenter(sessionEnding: true)
            || _lifetimeGeneration != generation)
        {
            return;
        }

        _logoutStreamingRetirementPrepared = true;
        if (!_transit.CompleteLogout() || _lifetimeGeneration != generation)
        {
            _logoutStreamingRetirementPrepared = false;
            return;
        }

        Console.WriteLine(
            "live: logout confirmed — returning to character select");
        if (_logout.CompleteCharacterLogOff())
        {
            return;
        }

        _logoutStreamingRetirementPrepared = false;

        if (_lifetimeGeneration == generation)
        {
            Console.Error.WriteLine(
                "live: return-to-character-select refused — retiring the "
                + "logout presentation");
            _presentation.Reset();
        }
    }

    public void Tick(float deltaSeconds)
    {
        ThrowIfDisposed();
        if (_transit.IsLogoutActive)
        {
            TickLogout(deltaSeconds);
            return;
        }

        TryActivatePendingPresentation();
        TryAimAcceptedDestination();
        if (!_transit.IsTeleportActive)
        {
            TickLoginPresentation(deltaSeconds);
            return;
        }

        long generation = _lifetimeGeneration;
        ushort sequence = _transit.ActiveTeleportSequence;
        if (!_mode.TryEnterPortalSpace()
            || !IsCurrentLifetime(generation, sequence)
            || _mode.Controller is null)
        {
            return;
        }

        bool haveDestination = _pendingCell != 0u;
        bool originReady = !_streaming.IsRecenterPending;
        bool dataReady = haveDestination
            && originReady
            && _worldReveal.Evaluate(_pendingCell).IsReady;
        if (!IsCurrentLifetime(generation, sequence))
            return;

        bool placementReady = dataReady && TryAdvancePortalCommit(sequence);
        if (!IsCurrentLifetime(generation, sequence))
            return;

        if (haveDestination && !placementReady)
            _holdSeconds += deltaSeconds;
        _presentation.SetWaitCue(
            haveDestination
            && !placementReady
            && _worldReveal.ObserveWait(
                TimeSpan.FromSeconds(_holdSeconds)));

        var (_, events) = _presentation.Tick(deltaSeconds, placementReady);
        if (!IsCurrentLifetime(generation, sequence))
            return;

        foreach (TeleportAnimEvent teleportEvent in events)
        {
            switch (teleportEvent)
            {
                case TeleportAnimEvent.Place:
                    if (!_placementCommitted)
                        return;
                    if (!_worldReveal.CanPlacePortalDestination(
                            _pendingRevealGeneration, sequence, _pendingCell))
                    {
                        return;
                    }
                    _placement.Place(_pendingRotation);
                    if (!IsCurrentLifetime(generation, sequence))
                        return;
                    _worldReveal.ObserveMaterialized(
                        _pendingRevealGeneration,
                        sequence,
                        _pendingCell);
                    if (!IsCurrentLifetime(generation, sequence))
                        return;
                    break;
                case TeleportAnimEvent.PlayEnterSound:
                    _presentation.PlayEnterCue();
                    if (!IsCurrentLifetime(generation, sequence))
                        return;
                    break;
                case TeleportAnimEvent.EnterTunnel:
                    _presentation.EnterTunnel();
                    if (!IsCurrentLifetime(generation, sequence))
                        return;
                    break;
                case TeleportAnimEvent.PlayExitSound:
                    _presentation.ExitTunnel();
                    if (!IsCurrentLifetime(generation, sequence))
                        return;
                    _worldReveal.RevealWorldViewport();
                    if (!IsCurrentLifetime(generation, sequence))
                        return;
                    _presentation.PlayExitCue();
                    if (!IsCurrentLifetime(generation, sequence))
                        return;
                    break;
                case TeleportAnimEvent.FireLoginComplete:
                    _mode.EnterWorld();
                    if (!IsCurrentLifetime(generation, sequence))
                        return;
                    _session.SendLoginComplete();
                    if (!IsCurrentLifetime(generation, sequence))
                        return;
                    _worldReveal.Complete();
                    if (!IsCurrentLifetime(generation, sequence))
                        return;
                    ResetTransit(clearSession: false);
                    return;
                default:
                    break;
            }
        }

        _presentation.TickTunnel(deltaSeconds);
    }

    private bool TryAdvancePortalCommit(ushort sequence)
    {
        if (_placementCommitted)
            return true;

        if (_awaitingDeferredWake)
        {
            if (_acceptedPositionDrive.PendingCount != 0)
                return false;
            _awaitingDeferredWake = false;
            if (_acceptedPositionDrive.TryConsumePortalCommit(
                    _pendingRevealGeneration, sequence))
            {
                _placementCommitted = true;
                return true;
            }
        }

        if (!_worldReveal.CanPlacePortalDestination(
                _pendingRevealGeneration,
                sequence,
                _pendingCell))
        {
            PhysicsDiagnostics.LogTeleport(
                "REFUSED", _pendingCell, "cause=stale-reveal");
            PhysicsDiagnostics.LogLocalTeleportArrival(
                cause: "stale-reveal",
                placementStatus: "Refused",
                portalGeneration: _pendingRevealGeneration,
                teleportSequence: sequence,
                destinationCell: _pendingCell,
                resolvedCell: 0u,
                hookTailRan: false,
                leashArmed: false,
                autorunCancelled: false);
            return false;
        }

        RuntimeAcceptedPositionExecutionStatus status =
            TryExecuteCanonicalPortalPlacementCore(sequence);
        switch (status)
        {
            case RuntimeAcceptedPositionExecutionStatus.Committed:
                _placementCommitted = true;
                return true;
            case RuntimeAcceptedPositionExecutionStatus.DeferredCell:
                _awaitingDeferredWake = true;
                return false;
            default:
                return false;
        }
    }

    private RuntimeAcceptedPositionExecutionStatus
        TryExecuteCanonicalPortalPlacementCore(ushort sequence)
    {
        System.Diagnostics.Debug.Assert(
            !_hasPendingDestination
                || _pendingDestination.TeleportSequence == sequence,
            "The transit's active sequence and the Aim-time destination's "
            + "own sequence must never diverge (A9).");
        if (!_hasPendingDestination
            || !_transit.TryRegisterHostProjection(
                _pendingRevealGeneration,
                _pendingCell,
                out RuntimeWorldHostProjectionToken hostToken))
        {
            PhysicsDiagnostics.LogTeleport(
                "REFUSED", _pendingCell, "cause=host-token-unavailable");
            PhysicsDiagnostics.LogLocalTeleportArrival(
                cause: "host-token-unavailable",
                placementStatus: "Refused",
                portalGeneration: _pendingRevealGeneration,
                teleportSequence: sequence,
                destinationCell: _pendingCell,
                resolvedCell: 0u,
                hookTailRan: false,
                leashArmed: false,
                autorunCancelled: false);
            return RuntimeAcceptedPositionExecutionStatus.Rejected;
        }

        RuntimeTeleportDestination destination = _pendingDestination;
        var portal = new RuntimePortalPlacementAuthority(
            Present: true,
            RevealGeneration: _pendingRevealGeneration,
            TeleportSequence: destination.TeleportSequence,
            Projection: hostToken);
        return _acceptedPositionDrive.TryExecuteAcceptedPortalArrival(
            destination,
            portal);
    }

    public void ResetSession()
    {
        ThrowIfDisposed();
        ResetTransit(
            clearSession: true,
            resetCanonicalTransit: true);
    }

    public void ResetGenerationPresentation()
    {
        ThrowIfDisposed();
        ResetTransit(
            clearSession: true,
            resetCanonicalTransit: false);
    }

    public Matrix4x4 ApplyViewPlane(Matrix4x4 projection) =>
        _presentation.ApplyViewPlane(projection);

    public ICamera ApplyViewPlane(ICamera camera) =>
        _presentation.ApplyViewPlane(camera);

    public void DrawPortalViewport(int width, int height, Matrix4x4 projection) =>
        _presentation.DrawPortalViewport(width, height, projection);

    private void TryActivatePendingPresentation()
    {
        if (!_transit.HasPendingTeleportStart)
            return;

        long generation = _lifetimeGeneration;
        if (!_mode.TryEnterPortalSpace()
            || _lifetimeGeneration != generation
            || !_transit.HasPendingTeleportStart)
        {
            return;
        }

        _holdSeconds = 0f;
        _presentation.Begin(_mode.Projection);
        if (_lifetimeGeneration != generation
            || !_transit.HasPendingTeleportStart)
        {
            return;
        }

        if (!_transit.ActivateQueuedTeleport())
            return;
        TryAimAcceptedDestination();
        Console.WriteLine(
            $"live: teleport presentation started "
            + $"(seq={_transit.ActiveTeleportSequence})");
    }

    private void TryActivateLoginPresentation()
    {
        if (_transit.HasPendingTeleportStart || _transit.IsTeleportActive)
            return;

        RuntimePortalSnapshot snapshot = _transit.Snapshot;
        if (snapshot.Kind != RuntimePortalKind.Login
            || snapshot.Generation == 0
            || snapshot.Completed
            || snapshot.Cancelled
            || _loginRevealGeneration == snapshot.Generation)
        {
            return;
        }

        if (_mode.Controller is not { CanExecuteLiveMovement: true })
            return;

        long generation = _lifetimeGeneration;
        if (!_mode.TryEnterPortalSpaceForLogin()
            || _lifetimeGeneration != generation
            || _mode.Controller is null)
        {
            return;
        }

        // Re-read after the mode entry: TryEnterPortalSpaceForLogin can run
        // arbitrary presentation attach work.
        snapshot = _transit.Snapshot;
        if (snapshot.Kind != RuntimePortalKind.Login
            || snapshot.Generation == 0
            || snapshot.Completed
            || snapshot.Cancelled)
        {
            return;
        }

        bool adoptedArmedTunnel = _loginTunnelArmed;
        _loginTunnelArmed = false;
        _loginRevealGeneration = snapshot.Generation;
        _loginPresentationActive = true;
        if (!adoptedArmedTunnel)
        {
            _loginHoldSeconds = 0f;
            _presentation.Begin(_mode.Projection);
        }
        Console.WriteLine(
            $"live: login portal-space presentation started "
            + $"(gen={snapshot.Generation} "
            + $"cell=0x{snapshot.Readiness.DestinationCell:X8} "
            + $"adoptedArmedTunnel={(adoptedArmedTunnel ? 1 : 0)})");
    }

    private void TickLoginPresentation(float deltaSeconds)
    {
        TryActivateLoginPresentation();

        RuntimePortalSnapshot snapshot = _transit.Snapshot;
        bool revealActive = snapshot.Kind == RuntimePortalKind.Login
            && snapshot.Generation != 0
            && !snapshot.Completed
            && !snapshot.Cancelled;
        if (!revealActive || _loginRevealGeneration != snapshot.Generation)
        {
            if (_loginPresentationActive)
            {
                _loginRevealGeneration = 0;
                _loginPresentationActive = false;
                _loginTunnelArmed = false;
                _loginHoldSeconds = 0f;
                _presentation.Reset();
            }
            else if (_loginTunnelArmed)
            {
                TickArmedLoginTunnel(deltaSeconds);
            }
            return;
        }

        if (!_loginPresentationActive)
            return;

        long generation = _lifetimeGeneration;
        long revealGeneration = snapshot.Generation;
        uint destinationCell = snapshot.Readiness.DestinationCell;

        bool originReady = !_streaming.IsRecenterPending;
        bool worldReady = _loginPlacementCompleted
            && originReady
            && _worldReveal.Evaluate(destinationCell).IsReady;
        if (!IsCurrentLoginLifetime(generation, revealGeneration))
            return;

        if (!worldReady)
            _loginHoldSeconds += deltaSeconds;
        _presentation.SetWaitCue(
            !worldReady
            && _worldReveal.ObserveWait(
                TimeSpan.FromSeconds(_loginHoldSeconds)));

        var (_, events) = _presentation.Tick(deltaSeconds, worldReady);
        if (!IsCurrentLoginLifetime(generation, revealGeneration))
            return;

        foreach (TeleportAnimEvent teleportEvent in events)
        {
            switch (teleportEvent)
            {
                case TeleportAnimEvent.PlayEnterSound:
                    Console.WriteLine(
                        "live: login portal-space enter cue "
                        + "(Sound_UI_EnterPortal)");
                    _presentation.PlayEnterCue();
                    if (!IsCurrentLoginLifetime(generation, revealGeneration))
                        return;
                    break;
                case TeleportAnimEvent.EnterTunnel:
                    _presentation.EnterTunnel();
                    if (!IsCurrentLoginLifetime(generation, revealGeneration))
                        return;
                    break;
                case TeleportAnimEvent.Place:
                    _worldReveal.ObserveLoginMaterialized(revealGeneration);
                    if (!IsCurrentLoginLifetime(generation, revealGeneration))
                        return;
                    break;
                case TeleportAnimEvent.PlayExitSound:
                    _presentation.ExitTunnel();
                    if (!IsCurrentLoginLifetime(generation, revealGeneration))
                        return;
                    _worldReveal.RevealWorldViewport();
                    if (!IsCurrentLoginLifetime(generation, revealGeneration))
                        return;
                    _presentation.PlayExitCue();
                    if (!IsCurrentLoginLifetime(generation, revealGeneration))
                        return;
                    break;
                case TeleportAnimEvent.FireLoginComplete:
                    _mode.EnterWorld();
                    if (!IsCurrentLoginLifetime(generation, revealGeneration))
                        return;
                    _session.SendLoginComplete();
                    if (!IsCurrentLoginLifetime(generation, revealGeneration))
                        return;
                    _worldReveal.Complete();
                    _loginRevealGeneration = 0;
                    _loginPresentationActive = false;
                    _loginHoldSeconds = 0f;
                    Console.WriteLine(
                        "live: login portal-space presentation complete");
                    return;
                default:
                    break;
            }
        }

        _presentation.TickTunnel(deltaSeconds);
    }

    private void TickArmedLoginTunnel(float deltaSeconds)
    {
        RuntimeCharacterSelectionLifecycle lifecycle =
            _loginLifecycle.SelectionLifecycle;
        if (lifecycle is not (
            RuntimeCharacterSelectionLifecycle.EnteringWorld
            or RuntimeCharacterSelectionLifecycle.InWorld))
        {
            _loginTunnelArmed = false;
            _loginHoldSeconds = 0f;
            _presentation.Reset();
            Console.WriteLine(
                $"live: login tunnel disarmed (lifecycle={lifecycle})");
            return;
        }

        long generation = _lifetimeGeneration;
        _loginHoldSeconds += deltaSeconds;
        var (_, events) = _presentation.Tick(deltaSeconds, worldReady: false);
        if (_lifetimeGeneration != generation || !_loginTunnelArmed)
            return;
        if (!ProcessArmedLoginTunnelEvents(events, generation))
            return;
        _presentation.TickTunnel(deltaSeconds);
    }

    private bool IsCurrentLoginLifetime(
        long lifetimeGeneration,
        long revealGeneration) =>
        _lifetimeGeneration == lifetimeGeneration
        && !_transit.IsTeleportActive
        && _loginRevealGeneration == revealGeneration
        && _transit.Snapshot.Generation == revealGeneration;

    private void TryAimAcceptedDestination()
    {
        if (!_transit.TryGetAcceptedTeleportDestination(
                out RuntimeTeleportDestination destination))
        {
            return;
        }

        long generation = _lifetimeGeneration;
        ushort sequence = _transit.ActiveTeleportSequence;
        PlayerMovementController? controller = _mode.Controller;
        if (controller is null)
        {
            if (!_mode.TryEnterPortalSpace()
                || !IsCurrentLifetime(generation, sequence))
            {
                return;
            }

            controller = _mode.Controller;
            if (controller is null)
                return;
        }

        if (!AimDestination(destination, controller, generation, sequence))
            return;
    }

    private bool AimDestination(
        RuntimeTeleportDestination destination,
        PlayerMovementController controller,
        long generation,
        ushort sequence)
    {
        Position position = destination.Position;
        int landblockX = (int)((position.ObjCellId >> 24) & 0xFFu);
        int landblockY = (int)((position.ObjCellId >> 16) & 0xFFu);
        uint streamingOriginLandblockId = StreamingRegion.EncodeLandblockId(
            _streaming.CenterX,
            _streaming.CenterY);
        var origin = new Vector3(
            (landblockX - _streaming.CenterX) * 192f,
            (landblockY - _streaming.CenterY) * 192f,
            0f);
        Vector3 translated = position.Frame.Origin + origin;

        TeleportLandblockTransition transition = TeleportLandblockTransition.Classify(
            controller.CellId,
            position.ObjCellId,
            streamingOriginLandblockId);
        int oldX = (int)((transition.SourceLandblockId >> 24) & 0xFFu);
        int oldY = (int)((transition.SourceLandblockId >> 16) & 0xFFu);

        Console.WriteLine(
            $"live: teleport arrival - old lb=({oldX},{oldY}) "
            + $"new lb=({landblockX},{landblockY}) "
            + $"dist={Vector3.Distance(translated, controller.Position):F1}");

        if (!_worldReveal.TryBeginPortal(
                sequence,
                position.ObjCellId,
                out long revealGeneration))
        {
            return false;
        }
        _pendingRevealGeneration = revealGeneration;
        if (!IsCurrentLifetime(generation, sequence))
            return false;

        if (transition.ChangesStreamingCenter)
        {
            bool isSealedDungeon = _streaming.IsSealedDungeon(
                position.ObjCellId);
            if (!IsCurrentLifetime(generation, sequence))
                return false;

            _streaming.BeginRecenter(
                landblockX,
                landblockY,
                isSealedDungeon);
            if (!IsCurrentLifetime(generation, sequence))
                return false;
        }

        _pendingRotation = position.Frame.Orientation;
        _pendingCell = position.ObjCellId;
        _pendingDestination = destination;
        _hasPendingDestination = true;
        _holdSeconds = 0f;
        PhysicsDiagnostics.LogTeleport(
            "AIM",
            position.ObjCellId,
            $"seq={destination.TeleportSequence} lb={landblockX},{landblockY} "
            + $"indoor={((position.ObjCellId & 0xFFFFu) >= 0x0100u)} "
            + $"playerCross={transition.CrossesLandblock} "
            + $"centerChange={transition.ChangesStreamingCenter}");
        return true;
    }

    private long ResetTransit(
        bool clearSession,
        bool resetCanonicalTransit = false)
    {
        bool streamingRetirementPrepared = clearSession
            && _logoutStreamingRetirementPrepared;
        if (clearSession)
            _logoutStreamingRetirementPrepared = false;

        long generation = checked(++_lifetimeGeneration);

        _pendingCell = 0u;
        _pendingRotation = Quaternion.Identity;
        _pendingRevealGeneration = 0;
        _pendingDestination = default;
        _hasPendingDestination = false;
        _placementCommitted = false;
        _awaitingDeferredWake = false;
        _holdSeconds = 0f;
        _loginRevealGeneration = 0;
        _loginPresentationActive = false;
        _loginTunnelArmed = false;
        _loginHoldSeconds = 0f;
        if (clearSession)
            _loginPlacementCompleted = false;

        if (!streamingRetirementPrepared)
            _streaming.ResetRecenter(clearSession);
        if (_lifetimeGeneration != generation)
            return generation;

        if (clearSession)
        {
            if (resetCanonicalTransit)
                _worldReveal.ResetSession();
            else
                _worldReveal.ResetHostSession();
        }
        else
        {
            _transit.EndTeleport();
            _worldReveal.Cancel();
        }
        if (_lifetimeGeneration != generation)
            return generation;

        _presentation.Reset();
        if (_lifetimeGeneration != generation)
            return generation;

        return generation;
    }

    private bool IsCurrentLifetime(long generation, ushort sequence) =>
        _lifetimeGeneration == generation
        && _transit.IsTeleportActive
        && _transit.ActiveTeleportSequence == sequence;

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
            return;
        _presentation.Dispose();
        _disposed = true;
    }
}
