using System.Globalization;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.World;

public readonly record struct RuntimeWorldTransitOwnershipSnapshot(
    int BufferedTeleportDestinationCount,
    int PendingTeleportStartCount,
    int ActiveTeleportCount,
    int AcceptedTeleportDestinationCount,
    int ActiveRevealCount,
    int PendingDestinationReadinessCount,
    int HostProjectionCount,
    int PendingHostAcknowledgementCount,
    int ActiveLogoutCount = 0)
{
    public bool IsSessionIdle =>
        BufferedTeleportDestinationCount == 0
        && PendingTeleportStartCount == 0
        && ActiveTeleportCount == 0
        && AcceptedTeleportDestinationCount == 0
        && ActiveRevealCount == 0
        && PendingDestinationReadinessCount == 0
        && HostProjectionCount == 0
        && PendingHostAcknowledgementCount == 0
        && ActiveLogoutCount == 0;
}

public enum RuntimeLogoutStage
{
    None,
    Requested,
    PresentationActive,
    Confirmed,
}

public sealed class RuntimeWorldTransitState
    : IRuntimePortalView
{
    private sealed class HostProjectionRecord(
        RuntimeWorldHostProjectionToken token)
    {
        public RuntimeWorldHostProjectionToken Token { get; } = token;
        public RuntimeWorldHostAcknowledgementStage Pending { get; set; } =
            RuntimeWorldHostAcknowledgementStage.ProjectionRegistered;
        public bool ProjectionRegistered { get; set; }
        public bool SimulationReleaseProjected { get; set; }
        public bool DestinationReservationReleased { get; set; }
        public bool IsSuperseding { get; set; }

        public RuntimeWorldHostProjectionSnapshot Snapshot => new(
            Token,
            Pending,
            ProjectionRegistered,
            SimulationReleaseProjected,
            DestinationReservationReleased,
            IsSuperseding);
    }

    public static readonly TimeSpan RetailWaitCueDelay =
        TimeSpan.FromSeconds(5);

    public const double RetailLogoutHoldSeconds = 3.0;

    public const double RetailPlayerKillerAdditionalHoldSeconds = 20.0;

    private readonly Action<string> _log;
    private readonly Dictionary<ushort, RuntimeTeleportDestination>
        _bufferedDestinations = [];
    private readonly Dictionary<long, HostProjectionRecord>
        _hostProjections = [];
    private RuntimePortalSnapshot _snapshot = RuntimePortalSnapshot.Idle;
    private long _nextGeneration;
    private bool _hasLastTeleportStart;
    private ushort _lastTeleportStartSequence;
    private bool _hasPendingTeleportStart;
    private ushort _pendingTeleportStartSequence;
    private bool _teleportActive;
    private ushort _activeTeleportSequence;
    private bool _destinationAccepted;
    private bool _hasAcceptedDestination;
    private RuntimeTeleportDestination _acceptedDestination;
    private RuntimeLogoutStage _logoutStage;
    private double _logoutHoldElapsedSeconds;
    private double _logoutHoldRequiredSeconds;

    public RuntimeWorldTransitState(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
    }

    public RuntimePortalSnapshot Snapshot => _snapshot;
    public RuntimeWorldTransitOwnershipSnapshot Ownership =>
        CaptureOwnership();
    public bool IsWorldSimulationAvailable =>
        _snapshot.WorldSimulationAvailable;
    public bool IsTeleportActive => _teleportActive;
    public ushort ActiveTeleportSequence =>
        _teleportActive ? _activeTeleportSequence : (ushort)0;
    public bool HasPendingTeleportStart => _hasPendingTeleportStart;
    public bool HasAcceptedTeleportDestination => _hasAcceptedDestination;
    public int BufferedTeleportDestinationCount =>
        _bufferedDestinations.Count;
    public int DiagnosticFailureCount { get; private set; }
    public Exception? LastDiagnosticFailure { get; private set; }

    public RuntimeWorldTransitOwnershipSnapshot CaptureOwnership()
    {
        int pendingHostAcknowledgements = 0;
        foreach (HostProjectionRecord host in _hostProjections.Values)
        {
            pendingHostAcknowledgements = checked(
                pendingHostAcknowledgements
                + host.Snapshot.PendingAcknowledgementCount);
        }

        bool revealActive = _snapshot.Generation != 0
            && !_snapshot.Completed
            && !_snapshot.Cancelled;
        return new RuntimeWorldTransitOwnershipSnapshot(
            _bufferedDestinations.Count,
            _hasPendingTeleportStart ? 1 : 0,
            _teleportActive ? 1 : 0,
            _hasAcceptedDestination ? 1 : 0,
            revealActive ? 1 : 0,
            revealActive && !_snapshot.IsReady ? 1 : 0,
            _hostProjections.Count,
            pendingHostAcknowledgements,
            _logoutStage != RuntimeLogoutStage.None ? 1 : 0);
    }


    public RuntimeLogoutStage LogoutStage => _logoutStage;
    public bool IsLogoutActive => _logoutStage != RuntimeLogoutStage.None;

    public bool TryBeginLogoutRequest(bool isPlayerKiller)
    {
        if (_logoutStage != RuntimeLogoutStage.None
            || _teleportActive
            || _hasPendingTeleportStart)
        {
            LogRejected(
                "logout-request-refused",
                $"stage={_logoutStage} teleportActive={_teleportActive} "
                + $"pendingStart={_hasPendingTeleportStart}");
            return false;
        }

        _logoutStage = RuntimeLogoutStage.Requested;
        _logoutHoldElapsedSeconds = 0d;
        _logoutHoldRequiredSeconds = RetailLogoutHoldSeconds
            + (isPlayerKiller ? RetailPlayerKillerAdditionalHoldSeconds : 0d);
        SafeLog(
            $"[world-reveal] event=logout-requested "
            + $"holdSeconds={_logoutHoldRequiredSeconds:F1} "
            + $"pk={(isPlayerKiller ? 1 : 0)}");
        return true;
    }

    public bool CancelLogoutRequest()
    {
        if (_logoutStage != RuntimeLogoutStage.Requested)
            return false;

        _logoutStage = RuntimeLogoutStage.None;
        _logoutHoldElapsedSeconds = 0d;
        _logoutHoldRequiredSeconds = 0d;
        SafeLog("[world-reveal] event=logout-request-cancelled");
        return true;
    }

    public bool AdvanceLogoutHold(double deltaSeconds)
    {
        if (_logoutStage != RuntimeLogoutStage.Requested || deltaSeconds < 0d)
            return false;

        _logoutHoldElapsedSeconds += deltaSeconds;
        if (_logoutHoldElapsedSeconds < _logoutHoldRequiredSeconds)
            return false;

        _logoutStage = RuntimeLogoutStage.PresentationActive;
        SafeLog("[world-reveal] event=logout-presentation-begin");
        return true;
    }

    public bool AcknowledgeLogoutConfirmed()
    {
        if (_logoutStage is not (
            RuntimeLogoutStage.Requested
            or RuntimeLogoutStage.PresentationActive))
        {
            return false;
        }

        _logoutStage = RuntimeLogoutStage.Confirmed;
        SafeLog("[world-reveal] event=logout-confirmed");
        return true;
    }

    public bool CompleteLogout()
    {
        if (_logoutStage != RuntimeLogoutStage.Confirmed)
            return false;

        _logoutStage = RuntimeLogoutStage.None;
        _logoutHoldElapsedSeconds = 0d;
        _logoutHoldRequiredSeconds = 0d;
        SafeLog("[world-reveal] event=logout-complete");
        return true;
    }

    public long BeginLoginReveal(uint destinationCell) =>
        BeginRevealCore(RuntimePortalKind.Login, destinationCell);

    public bool CanBeginPortalReveal(
        ushort teleportSequence,
        uint destinationCell)
    {
        if (!_teleportActive
            || teleportSequence != _activeTeleportSequence
            || !_hasAcceptedDestination)
        {
            return false;
        }

        RuntimeTeleportDestination destination = _acceptedDestination;
        return destination.TeleportSequence == teleportSequence
            && destination.CellId != 0u
            && destination.CellId == destinationCell;
    }

    public bool TryBeginPortalReveal(
        ushort teleportSequence,
        uint destinationCell,
        out long generation)
    {
        generation = 0;
        if (!CanBeginPortalReveal(teleportSequence, destinationCell))
        {
            LogRejected(
                "portal-begin-without-destination",
                $"active={_teleportActive} "
                + $"expectedSequence={_activeTeleportSequence} "
                + $"actualSequence={teleportSequence} "
                + $"accepted={_hasAcceptedDestination}");
            return false;
        }

        RuntimeTeleportDestination destination = _acceptedDestination;
        generation = BeginRevealCore(
            RuntimePortalKind.Portal,
            destinationCell);
        _hasAcceptedDestination = false;
        _acceptedDestination = default;
        return true;
    }

    /// <summary>
    /// Registers one presentation-independent host projection against the
    /// exact active reveal. Graphical and direct hosts receive the same token.
    /// </summary>
    public bool TryRegisterHostProjection(
        long generation,
        uint destinationCell,
        out RuntimeWorldHostProjectionToken token)
    {
        token = new RuntimeWorldHostProjectionToken(
            generation,
            destinationCell);
        if (!token.IsValid
            || generation != _snapshot.Generation
            || destinationCell != _snapshot.DestinationCell
            || _snapshot.Cancelled
            || _snapshot.Completed)
        {
            LogRejected(
                "host-register-mismatch",
                $"generation={generation} "
                + $"cell=0x{destinationCell:X8}");
            token = default;
            return false;
        }

        if (_hostProjections.TryGetValue(
                generation,
                out HostProjectionRecord? existing))
        {
            if (existing.Token == token)
                return true;

            LogRejected(
                "host-register-conflict",
                $"generation={generation} "
                + $"expectedCell=0x{existing.Token.DestinationCell:X8} "
                + $"actualCell=0x{destinationCell:X8}");
            token = default;
            return false;
        }

        _hostProjections.Add(
            generation,
            new HostProjectionRecord(token));
        return true;
    }

    public bool TryGetHostProjection(
        in RuntimeWorldHostProjectionToken token,
        out RuntimeWorldHostProjectionSnapshot projection)
    {
        if (token.IsValid
            && _hostProjections.TryGetValue(
                token.Generation,
                out HostProjectionRecord? host)
            && host.Token == token)
        {
            projection = host.Snapshot;
            return true;
        }

        projection = default;
        return false;
    }

    public bool IsCurrentPlacementAuthority(
        in RuntimePortalPlacementAuthority authority,
        uint exactCellId)
    {
        if (!authority.Present)
            return authority.IsEmpty;

        return authority.IsValid
            && authority.Projection.DestinationCell == exactCellId
            && IsCurrentPortalDestination(
                authority.RevealGeneration,
                authority.TeleportSequence,
                exactCellId)
            && TryGetHostProjection(
                authority.Projection,
                out RuntimeWorldHostProjectionSnapshot host)
            && !host.IsSuperseding;
    }

    public bool RequireDestinationReservationRelease(
        in RuntimeWorldHostProjectionToken token)
    {
        if (!TryGetHostRecord(token, out HostProjectionRecord? host))
            return false;
        if (host.DestinationReservationReleased)
            return false;

        host.Pending |= RuntimeWorldHostAcknowledgementStage
            .DestinationReservationReleased;
        return true;
    }

    public bool BeginHostProjectionSupersession(
        in RuntimeWorldHostProjectionToken token)
    {
        if (!TryGetHostRecord(token, out HostProjectionRecord? host))
            return false;

        BeginHostProjectionSupersession(host);
        return true;
    }

    public bool AcknowledgeHostProjection(
        in RuntimeWorldHostAcknowledgement acknowledgement)
    {
        RuntimeWorldHostAcknowledgementStage stage =
            acknowledgement.Stage;
        if (!IsSingleStage(stage)
            || !TryGetHostRecord(
                acknowledgement.Projection,
                out HostProjectionRecord? host)
            || (host.Pending & stage) == 0)
        {
            return false;
        }

        switch (stage)
        {
            case RuntimeWorldHostAcknowledgementStage.ProjectionRegistered:
                if (host.IsSuperseding)
                    return false;
                host.ProjectionRegistered = true;
                break;
            case RuntimeWorldHostAcknowledgementStage
                .SimulationReleaseProjected:
                if (host.Token.Generation == _snapshot.Generation
                    && !_snapshot.WorldSimulationAvailable)
                {
                    return false;
                }
                host.SimulationReleaseProjected = true;
                break;
            case RuntimeWorldHostAcknowledgementStage
                .DestinationReservationReleased:
                host.DestinationReservationReleased = true;
                break;
            case RuntimeWorldHostAcknowledgementStage.TerminalProjected:
                if (!host.IsSuperseding
                    && (host.Token.Generation != _snapshot.Generation
                        || (!_snapshot.Completed
                            && !_snapshot.Cancelled)))
                {
                    return false;
                }
                if ((host.Pending & ~stage) != 0)
                    return false;
                host.Pending &= ~stage;
                _hostProjections.Remove(host.Token.Generation);
                return true;
            default:
                return false;
        }

        host.Pending &= ~stage;
        return true;
    }

    private long BeginRevealCore(
        RuntimePortalKind kind,
        uint destinationCell)
    {
        if (kind is RuntimePortalKind.None)
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (destinationCell == 0u)
            throw new ArgumentOutOfRangeException(nameof(destinationCell));

        if (_snapshot.Generation != 0
            && !_snapshot.Cancelled
            && !_snapshot.WorldViewportObserved)
        {
            Log("superseded", _snapshot);
        }
        if (_hostProjections.TryGetValue(
                _snapshot.Generation,
                out HostProjectionRecord? superseded))
        {
            BeginHostProjectionSupersession(superseded);
        }

        long generation = checked(++_nextGeneration);
        int failures = _snapshot.InvariantFailureCount;
        int materializations = _snapshot.PortalMaterializationCount;
        _snapshot = new RuntimePortalSnapshot(
            generation,
            kind,
            new RuntimeDestinationReadiness(
                generation,
                destinationCell,
                IsIndoor: IsIndoor(destinationCell),
                IsUnhydratable: false,
                RequiredRenderRadius: 0,
                IsRenderNeighborhoodReady: false,
                AreCompositeTexturesReady: false,
                IsCollisionReady: false),
            Materialized: false,
            Completed: false,
            Cancelled: false,
            WorldViewportObserved: false,
            WorldSimulationAvailable: false,
            InvariantFailureCount: failures,
            WaitCueShown: false,
            PortalMaterializationCount: materializations);
        Log("begin", _snapshot);
        return generation;
    }

    public bool CanQueueTeleportStart(ushort sequence) =>
        !_hasLastTeleportStart
        || IsNewer(_lastTeleportStartSequence, sequence);

    public bool TryQueueTeleportStart(ushort sequence)
    {
        if (!CanQueueTeleportStart(sequence))
            return false;

        PruneDestinationsOlderThan(sequence);
        _teleportActive = false;
        _activeTeleportSequence = 0;
        _hasPendingTeleportStart = true;
        _pendingTeleportStartSequence = sequence;
        _hasLastTeleportStart = true;
        _lastTeleportStartSequence = sequence;
        _destinationAccepted = false;
        _hasAcceptedDestination = false;
        _acceptedDestination = default;
        return true;
    }

    /// <summary>
    /// Promotes the queued F751 lifetime once its host can enter portal space.
    /// A Position accepted before that host edge becomes the same active
    /// destination; no position or physics state is applied here.
    /// </summary>
    public bool ActivateQueuedTeleport()
    {
        if (!_hasPendingTeleportStart)
            return false;

        _teleportActive = true;
        _activeTeleportSequence = _pendingTeleportStartSequence;
        _hasPendingTeleportStart = false;
        _pendingTeleportStartSequence = 0;
        _destinationAccepted = false;
        _hasAcceptedDestination = false;
        _acceptedDestination = default;

        if (!_bufferedDestinations.Remove(
                _activeTeleportSequence,
                out RuntimeTeleportDestination buffered))
        {
            return true;
        }

        _destinationAccepted = true;
        _hasAcceptedDestination = true;
        _acceptedDestination = buffered;
        return true;
    }

    public bool OfferTeleportDestination(
        in RuntimeTeleportDestination destination,
        bool teleportTimestampAdvanced)
    {
        ushort sequence = destination.TeleportSequence;
        if (destination.CellId == 0u)
            return false;

        if (teleportTimestampAdvanced)
            PruneDestinationsOlderThan(sequence);

        if (_teleportActive && sequence == _activeTeleportSequence)
        {
            if (_destinationAccepted)
                return false;

            _destinationAccepted = true;
            _hasAcceptedDestination = true;
            _acceptedDestination = destination;
            return true;
        }

        if (_hasPendingTeleportStart
            && sequence == _pendingTeleportStartSequence)
        {
            _bufferedDestinations.TryAdd(sequence, destination);
            return false;
        }

        ushort correlationSequence = _hasPendingTeleportStart
            ? _pendingTeleportStartSequence
            : _activeTeleportSequence;
        bool mayBelongToFutureStart =
            (!_teleportActive && !_hasPendingTeleportStart)
            || IsNewer(correlationSequence, sequence);
        if (!teleportTimestampAdvanced || !mayBelongToFutureStart)
            return false;

        _bufferedDestinations.TryAdd(sequence, destination);
        return false;
    }

    public bool TryGetAcceptedTeleportDestination(
        out RuntimeTeleportDestination destination)
    {
        destination = _acceptedDestination;
        return _teleportActive && _hasAcceptedDestination;
    }

    /// <summary>
    /// Read-only preflight used immediately before the host mutates its
    /// presentation placement. The post-placement acknowledgement repeats the
    /// same exact validation.
    /// </summary>
    public bool CanPlacePortalDestination(
        long generation,
        ushort teleportSequence,
        uint destinationCell) =>
        IsCurrentPortalDestination(
            generation,
            teleportSequence,
            destinationCell);

    public void EndTeleport()
    {
        _teleportActive = false;
        _activeTeleportSequence = 0;
        _hasPendingTeleportStart = false;
        _pendingTeleportStartSequence = 0;
        _destinationAccepted = false;
        _hasAcceptedDestination = false;
        _acceptedDestination = default;
    }

    public bool AcknowledgeDestinationReadiness(
        in RuntimeDestinationReadiness acknowledgement)
    {
        if (!ValidateActive(
                acknowledgement.Generation,
                acknowledgement.DestinationCell,
                "readiness"))
        {
            return false;
        }

        if (_snapshot.Readiness.IsReady)
            return false;

        bool isIndoor = IsIndoor(acknowledgement.DestinationCell);
        bool radiusShapeValid = isIndoor
            ? acknowledgement.RequiredRenderRadius == 0
            : acknowledgement.RequiredRenderRadius >= 1;
        if (acknowledgement.IsIndoor != isIndoor || !radiusShapeValid)
        {
            FailInvariant(
                "invalid-readiness-shape",
                $"indoor={acknowledgement.IsIndoor} "
                + $"expectedIndoor={isIndoor} "
                + $"radius={acknowledgement.RequiredRenderRadius} "
                + $"expectedRadius={(isIndoor ? "0" : ">=1")}");
            return false;
        }

        if (_snapshot.Readiness == acknowledgement)
            return false;

        _snapshot = _snapshot with { Readiness = acknowledgement };
        Log("readiness", _snapshot);
        return true;
    }

    public bool AcknowledgePortalMaterialized(
        long generation,
        ushort teleportSequence,
        uint destinationCell)
    {
        if (!IsCurrentPortalDestination(
                generation,
                teleportSequence,
                destinationCell))
        {
            LogRejected(
                "materialized-portal-mismatch",
                $"generation={generation} "
                + $"sequence={teleportSequence} "
                + $"cell=0x{destinationCell:X8}");
            return false;
        }

        if (!ValidateActive(generation, destinationCell, "materialized"))
            return false;
        if (_snapshot.Materialized)
            return false;
        if (!_snapshot.Readiness.IsReady)
        {
            FailInvariant("materialized-before-ready", null);
            return false;
        }

        int count = _snapshot.PortalMaterializationCount;
        if (_snapshot.Kind == RuntimePortalKind.Portal)
            count = checked(count + 1);
        _snapshot = _snapshot with
        {
            Materialized = true,
            WorldSimulationAvailable = true,
            PortalMaterializationCount = count,
        };
        RequireHostStage(
            generation,
            RuntimeWorldHostAcknowledgementStage
                .SimulationReleaseProjected);
        Log("materialized", _snapshot);
        return true;
    }

    public bool AcknowledgeLoginMaterialized(long generation)
    {
        if (generation == 0
            || generation != _snapshot.Generation
            || _snapshot.Kind != RuntimePortalKind.Login)
        {
            LogRejected(
                "materialized-login-mismatch",
                $"generation={generation} kind={_snapshot.Kind}");
            return false;
        }

        if (!ValidateActive(
                generation,
                _snapshot.DestinationCell,
                "materialized"))
        {
            return false;
        }
        if (_snapshot.Materialized)
            return false;
        if (!_snapshot.Readiness.IsReady)
        {
            FailInvariant("materialized-before-ready", null);
            return false;
        }

        _snapshot = _snapshot with
        {
            Materialized = true,
            WorldSimulationAvailable = true,
        };
        RequireHostStage(
            generation,
            RuntimeWorldHostAcknowledgementStage
                .SimulationReleaseProjected);
        Log("materialized", _snapshot);
        return true;
    }

    public bool AcknowledgeWorldViewportVisible(long generation)
    {
        if (!ValidateGeneration(
                generation,
                "world-visible",
                allowCompleted: true))
        {
            return false;
        }
        if (_snapshot.WorldViewportObserved)
            return false;
        if (!_snapshot.Readiness.IsReady)
        {
            FailInvariant("viewport-before-ready", null);
            return false;
        }

        _snapshot = _snapshot with { WorldViewportObserved = true };
        Log("world-visible", _snapshot);
        return true;
    }

    public bool ObserveWait(long generation, TimeSpan elapsed)
    {
        if (generation == 0
            || generation != _snapshot.Generation
            || _snapshot.Cancelled
            || _snapshot.Completed
            || elapsed < RetailWaitCueDelay)
        {
            return false;
        }

        if (!_snapshot.WaitCueShown)
        {
            _snapshot = _snapshot with { WaitCueShown = true };
            SafeLog(
                $"[world-reveal] event=wait-cue "
                + $"elapsedMs={elapsed.TotalMilliseconds:F0} "
                + Describe(_snapshot));
        }

        return true;
    }

    public bool Complete(long generation)
    {
        if (!ValidateGeneration(
                generation,
                "complete",
                allowCompleted: true))
        {
            return false;
        }
        if (_snapshot.Completed)
            return false;
        if (!_snapshot.Readiness.IsReady)
        {
            FailInvariant("complete-before-ready", null);
            return false;
        }
        if (_snapshot.Kind == RuntimePortalKind.Portal
            && !_snapshot.Materialized)
        {
            FailInvariant("portal-complete-before-materialized", null);
            return false;
        }

        bool simulationWasAvailable =
            _snapshot.WorldSimulationAvailable;
        _snapshot = _snapshot with
        {
            Completed = true,
            WorldSimulationAvailable = true,
        };
        RequireTerminalHostProjection(
            generation,
            requireSimulationRelease: !simulationWasAvailable);
        Log("complete", _snapshot);
        return true;
    }

    public bool Cancel(long generation)
    {
        if (generation == 0
            || generation != _snapshot.Generation
            || _snapshot.Cancelled
            || _snapshot.Completed)
        {
            return false;
        }

        bool simulationWasAvailable =
            _snapshot.WorldSimulationAvailable;
        _snapshot = _snapshot with
        {
            Cancelled = true,
            WorldSimulationAvailable = true,
        };
        RequireTerminalHostProjection(
            generation,
            requireSimulationRelease: !simulationWasAvailable);
        Log("cancel", _snapshot);
        return true;
    }

    public void ResetSession()
    {
        if (_hostProjections.Count != 0)
        {
            RuntimeWorldTransitOwnershipSnapshot ownership =
                CaptureOwnership();
            throw new InvalidOperationException(
                "World transit cannot reset while a host projection "
                + "still owns an acknowledgement suffix "
                + $"(hosts={ownership.HostProjectionCount}, "
                + $"pending={ownership.PendingHostAcknowledgementCount}).");
        }

        _snapshot = RuntimePortalSnapshot.Idle;
        EndTeleport();
        _hasLastTeleportStart = false;
        _lastTeleportStartSequence = 0;
        _bufferedDestinations.Clear();
        _logoutStage = RuntimeLogoutStage.None;
        _logoutHoldElapsedSeconds = 0d;
        _logoutHoldRequiredSeconds = 0d;
    }

    private bool TryGetHostRecord(
        in RuntimeWorldHostProjectionToken token,
        out HostProjectionRecord host)
    {
        if (token.IsValid
            && _hostProjections.TryGetValue(
                token.Generation,
                out HostProjectionRecord? candidate)
            && candidate.Token == token)
        {
            host = candidate;
            return true;
        }

        host = null!;
        return false;
    }

    private void RequireHostStage(
        long generation,
        RuntimeWorldHostAcknowledgementStage stage)
    {
        if (!_hostProjections.TryGetValue(
                generation,
                out HostProjectionRecord? host))
        {
            return;
        }

        bool alreadyAcknowledged = stage switch
        {
            RuntimeWorldHostAcknowledgementStage.ProjectionRegistered =>
                host.ProjectionRegistered,
            RuntimeWorldHostAcknowledgementStage
                .SimulationReleaseProjected =>
                host.SimulationReleaseProjected,
            RuntimeWorldHostAcknowledgementStage
                .DestinationReservationReleased =>
                host.DestinationReservationReleased,
            _ => false,
        };
        if (!alreadyAcknowledged)
            host.Pending |= stage;
    }

    private void RequireTerminalHostProjection(
        long generation,
        bool requireSimulationRelease)
    {
        if (!_hostProjections.TryGetValue(
                generation,
                out HostProjectionRecord? host))
        {
            return;
        }

        // A host that is terminating no longer has to finish acquiring a
        // projection. It must instead release every resource it did acquire.
        host.Pending &=
            ~RuntimeWorldHostAcknowledgementStage.ProjectionRegistered;
        if (requireSimulationRelease
            && !host.SimulationReleaseProjected)
        {
            host.Pending |= RuntimeWorldHostAcknowledgementStage
                .SimulationReleaseProjected;
        }
        if (!host.DestinationReservationReleased)
        {
            host.Pending |= RuntimeWorldHostAcknowledgementStage
                .DestinationReservationReleased;
        }
        host.Pending |=
            RuntimeWorldHostAcknowledgementStage.TerminalProjected;
    }

    private static void BeginHostProjectionSupersession(
        HostProjectionRecord host)
    {
        host.IsSuperseding = true;
        host.Pending &=
            ~(RuntimeWorldHostAcknowledgementStage.ProjectionRegistered
                | RuntimeWorldHostAcknowledgementStage
                    .SimulationReleaseProjected);
        if (!host.DestinationReservationReleased)
        {
            host.Pending |= RuntimeWorldHostAcknowledgementStage
                .DestinationReservationReleased;
        }
        host.Pending |=
            RuntimeWorldHostAcknowledgementStage.TerminalProjected;
    }

    private static bool IsSingleStage(
        RuntimeWorldHostAcknowledgementStage stage)
    {
        int value = (int)stage;
        return value != 0 && (value & (value - 1)) == 0;
    }

    private bool IsCurrentPortalDestination(
        long generation,
        ushort teleportSequence,
        uint destinationCell) =>
        _teleportActive
        && teleportSequence == _activeTeleportSequence
        && generation != 0
        && generation == _snapshot.Generation
        && _snapshot.Kind == RuntimePortalKind.Portal
        && !_snapshot.Cancelled
        && !_snapshot.Completed
        && destinationCell != 0u
        && destinationCell == _snapshot.DestinationCell;

    private bool ValidateActive(
        long generation,
        uint destinationCell,
        string eventName)
    {
        if (!ValidateGeneration(
                generation,
                eventName,
                allowCompleted: false))
        {
            return false;
        }
        if (destinationCell == 0u
            || destinationCell != _snapshot.DestinationCell)
        {
            FailInvariant(
                $"{eventName}-destination-mismatch",
                $"expected=0x{_snapshot.DestinationCell:X8} "
                + $"actual=0x{destinationCell:X8}");
            return false;
        }

        return true;
    }

    private bool ValidateGeneration(
        long generation,
        string eventName,
        bool allowCompleted)
    {
        if (generation == 0 || generation != _snapshot.Generation)
        {
            LogRejected(
                $"{eventName}-generation-mismatch",
                $"expected={_snapshot.Generation} actual={generation}");
            return false;
        }
        if (_snapshot.Cancelled
            || (!allowCompleted && _snapshot.Completed))
        {
            LogRejected(
                $"{eventName}-after-terminal",
                $"completed={_snapshot.Completed} "
                + $"cancelled={_snapshot.Cancelled}");
            return false;
        }

        return true;
    }

    private void FailInvariant(string reason, string? detail)
    {
        _snapshot = _snapshot with
        {
            InvariantFailureCount =
                checked(_snapshot.InvariantFailureCount + 1),
        };
        string suffix =
            string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}";
        SafeLog(
            $"[world-reveal] event=invariant-failure "
            + $"reason={reason}{suffix} {Describe(_snapshot)}");
    }

    private void LogRejected(string reason, string? detail)
    {
        string suffix =
            string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}";
        SafeLog(
            $"[world-reveal] event=rejected "
            + $"reason={reason}{suffix} {Describe(_snapshot)}");
    }

    private void Log(string eventName, RuntimePortalSnapshot snapshot) =>
        SafeLog($"[world-reveal] event={eventName} {Describe(snapshot)}");

    private void SafeLog(string message)
    {
        try
        {
            _log(message);
        }
        catch (Exception error)
        {
            DiagnosticFailureCount =
                checked(DiagnosticFailureCount + 1);
            LastDiagnosticFailure = error;
        }
    }

    private static string Describe(RuntimePortalSnapshot snapshot)
    {
        RuntimeDestinationReadiness ready = snapshot.Readiness;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"generation={snapshot.Generation} kind={snapshot.Kind} "
            + $"cell=0x{ready.DestinationCell:X8} "
            + $"indoor={ready.IsIndoor} "
            + $"radius={ready.RequiredRenderRadius} "
            + $"unhydratable={ready.IsUnhydratable} "
            + $"render={ready.IsRenderNeighborhoodReady} "
            + $"composites={ready.AreCompositeTexturesReady} "
            + $"collision={ready.IsCollisionReady} ready={ready.IsReady} "
            + $"materialized={snapshot.Materialized} "
            + $"completed={snapshot.Completed} "
            + $"cancelled={snapshot.Cancelled} "
            + $"visible={snapshot.WorldViewportObserved} "
            + $"simulation={snapshot.WorldSimulationAvailable} "
            + $"failures={snapshot.InvariantFailureCount}");
    }

    private static bool IsNewer(ushort current, ushort incoming) =>
        AcDream.Core.Physics.PhysicsTimestampGate.IsNewer(
            current,
            incoming);

    private void PruneDestinationsOlderThan(ushort sequence)
    {
        foreach (ushort bufferedSequence
                 in _bufferedDestinations.Keys.ToArray())
        {
            if (IsNewer(bufferedSequence, sequence))
                _bufferedDestinations.Remove(bufferedSequence);
        }
    }

    private static bool IsIndoor(uint cellId) =>
        (cellId & 0xFFFFu) >= 0x0100u;
}
