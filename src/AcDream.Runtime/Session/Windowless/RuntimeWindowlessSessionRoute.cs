using AcDream.Content.Vfx;
using AcDream.Core.Net;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime.World;

namespace AcDream.Runtime.Session.Windowless;

/// <summary>
/// The drives a client keeps for the life of its session, when it keeps them
/// itself: the same instances then serve this route and any other route that
/// holds the connection in turn (a window's), so work in flight is handed
/// from one to the next.
/// </summary>
internal sealed record RuntimeWindowlessSessionDrives(
    RuntimeFirstEntryDriveController FirstEntry,
    RuntimeAcceptedPositionDriveController AcceptedPosition,
    RuntimeRemoteArming RemoteArming);

/// <summary>
/// How a session with nothing drawn takes hold of a world connection: the
/// route every client without a window builds, one implementation. It keeps
/// the drives that carry the character and every other creature into the
/// world across connections, the session's world projection over the
/// collision around the character, the expiry of objects out of sight, and
/// the event route of the current connection.
///
/// A client says when each part runs (<see cref="AdvanceRemoteBodies"/>
/// before the network, <see cref="PumpAfterNetwork"/> after it) and hands
/// over what only it knows: its character bindings, what to do once the
/// character has arrived, and where to report.
/// </summary>
internal sealed class RuntimeWindowlessSessionRoute : IDisposable
{
    private readonly GameRuntime _runtime;
    private readonly IRuntimeCollisionContent? _content;
    private readonly Func<IRuntimeCollisionNeighborhood>? _collision;
    private readonly IRuntimePlacementProjectionSink? _placementSinkOverride;
    private readonly string _hostKind;
    private readonly Action<string> _diagnostic;
    private RuntimeFirstEntryDriveController? _firstEntryDrive;
    private RuntimeAcceptedPositionDriveController? _acceptedPositionDrive;
    private RuntimeRemoteArming? _remoteArming;
    private RuntimeRemotePlacementDriveController? _sharedRemotePlacement;
    private RuntimeRemoteBodyDrive? _remoteBodies;
    private RuntimeWindowlessWorldProjection? _worldProjection;
    private RuntimeLiveEntitySessionController? _entities;
    private RuntimeEntityLivenessController? _liveness;
    private RuntimeWindowlessEventRoute? _eventRoute;
    private WorldSession? _currentSession;

    /// <param name="runtime">The session's runtime.</param>
    /// <param name="content">
    /// The game files the character and every other creature are carried
    /// by; with none, nothing is carried and bodies stand where the server
    /// put them.
    /// </param>
    /// <param name="collision">
    /// The collision around the character, when the client keeps one of its
    /// own for the life of the process; otherwise each connection's world
    /// projection keeps one over <paramref name="content"/>.
    /// </param>
    /// <param name="hostKind">How this client names itself in diagnostics.</param>
    /// <param name="diagnostic">Where the route reports.</param>
    /// <param name="placementSinkOverride">A placement sink for tests.</param>
    /// <param name="drives">
    /// The drives the client keeps for the life of its session; with none,
    /// this route builds its own the first time it has game files.
    /// </param>
    internal RuntimeWindowlessSessionRoute(
        GameRuntime runtime,
        IRuntimeCollisionContent? content,
        string hostKind,
        Action<string> diagnostic,
        Func<IRuntimeCollisionNeighborhood>? collision = null,
        IRuntimePlacementProjectionSink? placementSinkOverride = null,
        RuntimeWindowlessSessionDrives? drives = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentException.ThrowIfNullOrWhiteSpace(hostKind);
        _content = content;
        _hostKind = hostKind;
        _diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        _collision = collision;
        _placementSinkOverride = placementSinkOverride;
        if (drives is not null)
        {
            _firstEntryDrive = drives.FirstEntry;
            _acceptedPositionDrive = drives.AcceptedPosition;
            _remoteArming = drives.RemoteArming;
            _sharedRemotePlacement = drives.RemoteArming.PlacementDrive;
        }
    }

    /// <summary>The connection the current route was built for.</summary>
    internal WorldSession? CurrentSession => _currentSession;

    /// <summary>The current connection's event route, once one was built.</summary>
    internal RuntimeWindowlessEventRoute? EventRoute => _eventRoute;

    /// <summary>The live entities of the current connection, once one was built.</summary>
    internal RuntimeLiveEntitySessionController? Entities => _entities;

    /// <summary>
    /// Takes hold of an opened world connection: builds its event route over
    /// the runtime and the drives kept across connections.
    /// </summary>
    /// <param name="session">The opened connection.</param>
    /// <param name="character">How the client binds the character events.</param>
    /// <param name="noteLoginCompleteSent">
    /// Told every time the character's arrival is acknowledged to the server.
    /// </param>
    internal ILiveSessionEventRouting Create(
        WorldSession session,
        LiveCharacterSessionBindings character,
        Action? noteLoginCompleteSent = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        _currentSession = session;
        IRuntimeDirectWorldProjection? worldProjection = null;
        if (_content is { } content)
        {
            // Without a window there is no richer body maker, so the shared
            // physics owner is told where authored cylinders come from: a walk
            // ordered at a creature has to know how wide that creature is, or
            // it measures to the line through its middle and can never finish.
            _runtime.EntityObjects.Physics.BindSetupCollisionSource(
                content.PreparedCollision);
            // Animation content comes off the same files, for the same reason:
            // a body that is to move itself between the server's updates needs
            // the table that names its cycles and the frames those cycles
            // play. A session with no files has no bodies, and binds nothing.
            // Both readers take the files' lock: a plugin may be reading the
            // same files from its own thread at the same time.
            _runtime.EntityObjects.Physics.BindMotionContentSource(
                new RuntimeDatMotionContentSource(
                    content.Dats,
                    new RetailAnimationLoader(content.Dats, content.DatLock),
                    content.DatLock));
            _firstEntryDrive ??= new RuntimeFirstEntryDriveController(
                _runtime.EntityObjects,
                _runtime.Clock,
                content.PreparedCollision,
                () => PlayerMovementConstructionOptions.From(
                    _runtime.CharacterOwner.MovementSkills.Snapshot),
                static _ => new RuntimeLocalPlayerPhysicsActivationPreparation(
                    Radius: DefaultPlayerBody.Radius,
                    Height: DefaultPlayerBody.Height,
                    RuntimeLocalPlayerShadowDisposition.ProvenShapeless));
            PhysicsDiagnostics.LocalTeleportHostKind = _hostKind;
            _acceptedPositionDrive ??= new RuntimeAcceptedPositionDriveController(
                _runtime.EntityObjects,
                _runtime.Clock,
                content.PreparedCollision,
                new LocalPlayerOutboundController((_, _, _, _, _, _) => { }),
                () => _runtime.Generation,
                () => _runtime.PlayerIdentity.ServerGuid,
                () => _runtime.MovementOwner.Controller,
                () => _runtime.CharacterOwner.UsePositionFromServer,
                () => _currentSession,
                () => _runtime.MovementOwner,
                isPortalAuthorityCurrent: portal => _runtime.TransitOwner
                    .CanPlacePortalDestination(
                        portal.RevealGeneration,
                        portal.TeleportSequence,
                        portal.Projection.DestinationCell));
            var projection = _collision is { } collision
                ? new RuntimeWindowlessWorldProjection(
                    _runtime,
                    collision(),
                    _firstEntryDrive,
                    _acceptedPositionDrive,
                    onNonQuiescentStall: _diagnostic)
                : new RuntimeWindowlessWorldProjection(
                    _runtime,
                    content,
                    _firstEntryDrive,
                    _acceptedPositionDrive,
                    onNonQuiescentStall: _diagnostic);
            _worldProjection = projection;
            worldProjection = projection;
            // Arming another creature's body is the runtime's, and this client
            // arms the same bodies the same way; it simply draws none of
            // them, so the arming answers its own questions off the record.
            _remoteArming ??= RuntimeRemoteArming.Create(
                _runtime.EntityObjects,
                _runtime.Clock,
                content.PreparedCollision,
                projection.RemotePlacementServiceWindow);
            // Carrying those bodies forward is the runtime's too. This client
            // says when, and hands over the two things only a client knows:
            // which body is the character's, and where the character is.
            _remoteBodies ??= new RuntimeRemoteBodyDrive(
                _runtime.EntityObjects,
                () => _runtime.PlayerIdentity.ServerGuid,
                () => _runtime.MovementOwner.Controller?.Position);
        }
        var entities = new RuntimeLiveEntitySessionController(
            _runtime,
            session,
            _diagnostic,
            worldProjection,
            _acceptedPositionDrive,
            // The first two of three LoginComplete send sites -- see
            // RuntimeLiveEntitySessionController's own doc.
            onLoginCompleteSent: noteLoginCompleteSent);
        if (_remoteArming is { } remoteArming)
            entities.BindRemoteArming(remoteArming);
        _entities = entities;
        // The 25-second out-of-visibility destruction runs here too: the
        // server forgets an object on that schedule without a message and
        // only announces a destroyed object to players still knowing it, so
        // a client that never expired anything would keep every corpse it
        // saw.
        _liveness?.Dispose();
        _liveness = new RuntimeEntityLivenessController(
            _runtime.EntityObjects,
            _runtime.PlayerIdentity,
            new RuntimeCanonicalEntityExpirySink(_runtime.EntityObjects),
            new RuntimePhysicsCurrentCellSource(_runtime.EntityObjects),
            _runtime.InventoryOwner.ExternalContainers,
            _runtime.TradeOwner,
            _runtime.InventoryOwner.Vendor);
        GameRuntime runtime = _runtime;
        var route = new LiveSessionEventRouter(
            session,
            entities.CreateSink(),
            new LiveEnvironmentSessionSink(
                change =>
                    _ = runtime.EnvironmentOwner
                        .ApplyAdminEnvirons(change),
                runtime.EnvironmentOwner.SynchronizeFromServer),
            new LiveInventorySessionBindings(
                runtime.InventoryOwner.Objects,
                () => runtime.PlayerIdentity.ServerGuid,
                runtime.InventoryOwner.Shortcuts.Load,
                error =>
                {
                    bool castReceipt = runtime.InventoryOwner.Transactions.ItemBusyCount == 0;
                    runtime.InventoryOwner.ExternalContainers
                        .ApplyUseDone(error);
                    if (castReceipt)
                        runtime.ActionOwner.SpellCast.CompleteUse(error);
                    runtime.ActionOwner.Transactions.CompleteUse(error);
                },
                runtime.InventoryOwner.ItemMana,
                runtime.InventoryOwner.ExternalContainers,
                appraisal =>
                    runtime.ActionOwner.Transactions
                        .AcceptAppraisalResponse(appraisal.Guid),
                Vendor: runtime.InventoryOwner.Vendor,
                Book: runtime.BookOwner,
                PlayerName: () =>
                    runtime.InventoryOwner.Objects
                        .Get(runtime.PlayerIdentity.ServerGuid)?.Name
                    ?? string.Empty),
            character,
            new LiveSocialSessionBindings(
                runtime.CommunicationOwner.Chat,
                runtime.CommunicationOwner.TurbineChat,
                runtime.CommunicationOwner.Friends,
                runtime.CommunicationOwner.Squelch,
                (text, type) => runtime.CommunicationOwner.AddText(text, type),
                Trade: runtime.TradeOwner,
                Fellowship: runtime.FellowshipOwner,
                Allegiance: runtime.AllegianceOwner,
                House: runtime.HouseOwner,
                HousePurchase: runtime.HousePurchaseOwner,
                Chess: runtime.ChessOwner,
                Contracts: runtime.ContractsOwner,
                PlayerGuid: () => runtime.PlayerIdentity.ServerGuid,
                OnLocalPlayerDeath:
                    runtime.CommunicationOwner.ReportLocalPlayerDeath),
            actions: runtime.ActionOwner);
        var eventRoute = new RuntimeWindowlessEventRoute(
            route,
            runtime,
            _placementSinkOverride
                ?? new RuntimeWindowlessPlacementSink(runtime),
            _firstEntryDrive,
            _ =>
            {
                session.SendLoginComplete();
                noteLoginCompleteSent?.Invoke();
                session.SendHouseQuery();
            },
            _acceptedPositionDrive,
            _sharedRemotePlacement);
        _eventRoute = eventRoute;
        return eventRoute;
    }

    /// <summary>
    /// Lets go of the connection once the session stopped: nothing the route
    /// keeps sends on it again.
    /// </summary>
    internal void ForgetSession() => _currentSession = null;

    /// <summary>
    /// Another route (a window's) has taken hold of the connection while the
    /// session plays on: what this route kept for it is let go. The drives
    /// it may share stay with whoever holds them; the expiry of objects out
    /// of sight is the new route's. Taking hold of a connection again builds
    /// all of it anew.
    /// </summary>
    internal void ReleaseConnection()
    {
        _liveness?.Dispose();
        _liveness = null;
        _eventRoute = null;
        _entities = null;
        _worldProjection = null;
        _currentSession = null;
    }

    /// <summary>
    /// Ends the portal reveal of the current connection before the world
    /// transit resets, as a client with a window does.
    /// </summary>
    internal void EndPortalRevealForSessionReset() =>
        _entities?.EndPortalRevealForSessionReset();

    /// <summary>
    /// Carries every other creature's body forward by one update, after the
    /// character's own step and before anything the server said is read, so
    /// every body spends the same elapsed time on every client.
    /// </summary>
    internal void AdvanceRemoteBodies(float deltaSeconds) =>
        _remoteBodies?.Tick(deltaSeconds);

    /// <summary>
    /// The route's share of an update after the network was read: the
    /// character's first entry, a portal arrival, placements still waiting,
    /// and the expiry of objects out of sight.
    /// </summary>
    internal void PumpAfterNetwork()
    {
        _worldProjection?.PumpFirstEntry();
        _entities?.PumpPortalCompletion();
        _eventRoute?.RetryPending();
        _liveness?.Tick(_runtime.Clock.SimulationTimeSeconds);
    }

    public void Dispose()
    {
        _liveness?.Dispose();
        _liveness = null;
    }
}
