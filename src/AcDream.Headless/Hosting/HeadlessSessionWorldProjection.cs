using System.Numerics;
using AcDream.Content;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;
using AcDream.Runtime.World;

namespace AcDream.Headless.Hosting;

internal interface IHeadlessCollisionNeighborhood
{
    void CenterOn(uint fullCellId);

    bool IsReady(uint fullCellId);

    bool IsWithinServiceWindow(uint fullCellId);

    bool IsQuiescent { get; }
}

internal readonly record struct HeadlessCollisionGenerationAdvance(
    bool Completed,
    bool Progressed,
    bool WaitingForProjectionAcknowledgement,
    bool YieldToCaller);

internal sealed class HeadlessCollisionGenerationTransaction
{
    private readonly RuntimePhysicsState _physics;
    private readonly RuntimeCollisionAdmission _admission;
    private readonly PreparedLandblockCollisionGeneration _prepared;
    private bool _ownerCaptureCommitted;
    private int _refreshCursor;
    private bool _sealCommitted;

    private HeadlessCollisionGenerationTransaction(
        RuntimePhysicsState physics,
        RuntimeCollisionAdmission admission,
        PreparedLandblockCollisionGeneration prepared)
    {
        _physics = physics;
        _admission = admission;
        _prepared = prepared;
    }

    internal uint LandblockId => _admission.LandblockId;
    internal bool EngineMutationCommitted { get; private set; }
    internal bool CompletionCommitted { get; private set; }
    internal bool CancellationRequested { get; private set; }

    internal static HeadlessCollisionGenerationTransaction Begin(
        RuntimePhysicsState physics,
        uint landblockId,
        Action<RuntimeCollisionAdmission>? afterAdmission,
        Action<RuntimeCollisionAdmission,
            PreparedLandblockCollisionGeneration> stage)
    {
        ArgumentNullException.ThrowIfNull(physics);
        ArgumentNullException.ThrowIfNull(stage);

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(landblockId);
        PreparedLandblockCollisionGeneration? prepared = null;
        try
        {
            afterAdmission?.Invoke(admission);
            prepared = physics.PrepareCollisionGeneration(admission);
            stage(admission, prepared);
            return new HeadlessCollisionGenerationTransaction(
                physics,
                admission,
                prepared);
        }
        catch (Exception publicationError)
        {
            if (!physics.CancelCollisionGeneration(admission, prepared))
            {
                throw new AggregateException(
                    "Headless collision preparation failed and its pre-engine cancellation did not converge.",
                    publicationError);
            }
            throw;
        }
    }

    internal HeadlessCollisionGenerationAdvance Advance()
    {
        if (CancellationRequested)
            throw new InvalidOperationException(
                "A cancelled headless collision generation cannot resume.");
        if (CompletionCommitted)
            return new(true, false, false, false);

        if (!EngineMutationCommitted)
        {
            if (!_ownerCaptureCommitted)
            {
                RuntimeCollisionOwnerCaptureStep capture =
                    _physics.AdvanceCollisionRetainedOwnerCapture(
                        _admission,
                        _prepared);
                _ownerCaptureCommitted = capture.Completed;
                return new(false, true, false, false);
            }

            if (_refreshCursor < _prepared.RetainedOwnerIds.Count)
            {
                _physics.RefreshCollisionRetainedOwner(
                    _admission,
                    _prepared,
                    _prepared.RetainedOwnerIds[_refreshCursor]);
                _refreshCursor++;
                return new(false, true, false, false);
            }

            if (!_sealCommitted)
            {
                RuntimeCollisionSealStep seal =
                    _physics.AdvanceCollisionGenerationSeal(
                        _admission,
                        _prepared);
                _sealCommitted = seal.Completed;
                if (seal.Restarted)
                {
                    _ownerCaptureCommitted = false;
                    _refreshCursor = 0;
                }
                return new(false, true, false, false);
            }
        }

        RuntimeCollisionGenerationCommit commit =
            _physics.CommitCollisionGeneration(_admission, _prepared);
        EngineMutationCommitted |= commit.EngineCommitted;
        CompletionCommitted = commit.Completed;
        bool waiting = !commit.Completed
            && _physics.CaptureOwnership()
                .PendingCollisionPrefixProjectionCount != 0;
        if (!commit.Completed && !commit.EngineCommitted)
            _sealCommitted = false;
        return new(
            commit.Completed,
            Progressed: true,
            WaitingForProjectionAcknowledgement: waiting,
            YieldToCaller: !commit.Completed);
    }

    internal bool TryCancel()
    {
        if (CompletionCommitted)
            return true;
        CancellationRequested = true;
        bool completed = _physics.CancelCollisionGeneration(
            _admission,
            _prepared);
        if (completed && EngineMutationCommitted)
            CompletionCommitted = true;
        return completed;
    }

}

internal sealed class HeadlessCollisionNeighborhood
    : IHeadlessCollisionNeighborhood,
      AcDream.Runtime.Session.IRuntimeRemotePlacementServiceWindow
{
    private readonly record struct PublicationSpec(
        uint LandblockId,
        Vector3 Origin,
        bool Required);

    private readonly GameRuntime _runtime;
    private readonly HeadlessProcessContentOwner
        .HeadlessProcessContentLease _content;
    private readonly HashSet<uint> _resident = [];
    private readonly Queue<uint> _retirementQueue = [];
    private readonly Queue<PublicationSpec> _publicationQueue = [];
    private HeadlessCollisionGenerationTransaction? _pendingPublication;
    private bool _pendingPublicationCancellation;
    private bool _resetRequired;
    private bool _publicationPlanBuilt;
    private uint _requestedCenterLandblock;
    private uint _requestedFullCell;
    private uint _centerLandblock;

    internal HeadlessCollisionNeighborhood(
        GameRuntime runtime,
        HeadlessProcessContentOwner.HeadlessProcessContentLease content)
    {
        _runtime = runtime
            ?? throw new ArgumentNullException(nameof(runtime));
        _content = content
            ?? throw new ArgumentNullException(nameof(content));
    }

    public void CenterOn(uint fullCellId)
    {
        uint center = CanonicalLandblock(fullCellId);
        if (center == 0x0000FFFFu)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fullCellId),
                "A collision neighborhood requires a real destination cell.");
        }
        if (_requestedCenterLandblock != center)
        {
            _requestedCenterLandblock = center;
            _requestedFullCell = fullCellId;
            _resetRequired = true;
            _publicationPlanBuilt = false;
            _publicationQueue.Clear();
            _pendingPublicationCancellation =
                _pendingPublication is not null;
        }
        else
        {
            _requestedFullCell = fullCellId;
        }

        if (_centerLandblock == center
            && _resident.Contains(center)
            && _runtime.EntityObjects.Physics.Engine
                .IsLandblockTerrainResident(center))
        {
            _runtime.EntityObjects.Physics.Engine
                .UpdatePlayerCurrCell(fullCellId);
            return;
        }
        AdvanceWork();
    }

    public bool IsWithinServiceWindow(uint fullCellId)
    {
        if (_requestedCenterLandblock == 0u)
            return true;
        uint target = CanonicalLandblock(fullCellId);
        int dx = Math.Abs(
            (int)((target >> 24) & 0xFFu)
            - (int)((_requestedCenterLandblock >> 24) & 0xFFu));
        int dy = Math.Abs(
            (int)((target >> 16) & 0xFFu)
            - (int)((_requestedCenterLandblock >> 16) & 0xFFu));
        return dx <= 1 && dy <= 1;
    }

    public bool IsQuiescent =>
        _pendingPublication is null
        && _publicationQueue.Count == 0
        && !_pendingPublicationCancellation;

    public bool IsReady(uint fullCellId)
    {
        uint center = CanonicalLandblock(fullCellId);
        if (_requestedCenterLandblock == center)
        {
            _requestedFullCell = fullCellId;
            AdvanceWork();
        }
        if (_centerLandblock != center
            || !_resident.Contains(center)
            || !_runtime.EntityObjects.Physics.Engine
                .IsLandblockTerrainResident(center))
        {
            return false;
        }
        return (fullCellId & 0xFFFFu) < 0x0100u
            || _runtime.EntityObjects.Physics.DataCache
                .GetCellStruct(fullCellId) is not null;
    }

    private bool IsCollisionCurrentlyPublished(uint fullCellId)
    {
        uint landblock = CanonicalLandblock(fullCellId);
        if (!_resident.Contains(landblock)
            || !_runtime.EntityObjects.Physics.Engine
                .IsLandblockTerrainResident(landblock))
        {
            return false;
        }
        return (fullCellId & 0xFFFFu) < 0x0100u
            || _runtime.EntityObjects.Physics.DataCache
                .GetCellStruct(fullCellId) is not null;
    }

    bool AcDream.Runtime.Session.IRuntimeRemotePlacementServiceWindow
        .IsWithinServiceWindow(uint fullCellId) =>
        IsCollisionCurrentlyPublished(fullCellId);

    private HeadlessCollisionGenerationTransaction? CreatePublication(
        uint landblockId,
        Vector3 origin,
        uint currentCellId,
        bool required)
    {
        LoadedLandblock? source =
            LandblockLoader.Load(_content.Dats, landblockId);
        if (source is null)
        {
            if (required)
            {
                throw new InvalidDataException(
                    $"Required headless landblock 0x{landblockId:X8} is missing.");
            }
            return null;
        }

        IReadOnlyList<WorldEntity> staticEntities =
            LandblockPhysicsContentBuilder.HydrateStaticEntities(
                _content.Dats,
                source,
                origin,
                includeVisualBounds: false);
        IReadOnlyList<WorldEntity> scenery =
            LandblockPhysicsContentBuilder.HydrateProceduralScenery(
                _content.Dats,
                source,
                origin,
                _content.HeightTable.AsSpan(),
                includeVisualBounds: false);
        var entities = new List<WorldEntity>(
            staticEntities.Count + scenery.Count);
        entities.AddRange(staticEntities);
        entities.AddRange(scenery);
        PhysicsDatBundle dats =
            LandblockPhysicsContentBuilder.BuildDatBundle(
                _content.Dats,
                landblockId,
                entities);
        var landblock = new LoadedLandblock(
            source.LandblockId,
            source.Heightmap,
            entities,
            dats);
        LandblockCollisionBuild collisions =
            LandblockPhysicsContentBuilder
                .BuildPreparedCollisionClosure(
                    _content.PreparedCollision,
                    landblock);

        RuntimePhysicsState physics = _runtime.EntityObjects.Physics;
        return HeadlessCollisionGenerationTransaction.Begin(
            physics,
            landblockId,
            afterAdmission: null,
            (admission, prepared) =>
            {
                prepared.SetAssetClosure(
                    [.. collisions.GfxObjIds],
                    [.. collisions.SetupIds]);
                PhysicsDataCache cache = prepared.DataCache;
                TerrainSurface terrain =
                    LandblockPhysicsContentBuilder.BuildTerrainSurface(
                        landblock,
                        _content.HeightTable.AsSpan());
                var cellSurfaces = new List<CellSurface>();
                var portalPlanes = new List<PortalPlane>();
                LandblockPhysicsContentBuilder.PublishPreparedCells(
                    cache,
                    landblock,
                    collisions,
                    origin,
                    cellSurfaces,
                    portalPlanes);
                LandblockPhysicsContentBuilder.CacheBuildings(
                    cache,
                    landblock,
                    terrain,
                    origin);
                LandblockPhysicsContentBuilder.CachePreparedObjects(
                    cache,
                    collisions);
                physics.StageCollisionAssets(
                    admission,
                    prepared,
                    new RuntimeLandblockCollisionAssets(
                        landblockId,
                        terrain,
                        cellSurfaces,
                        portalPlanes,
                        origin.X,
                        origin.Y,
                        currentCellId));
                _ = LandblockPhysicsContentBuilder
                    .PublishStaticCollision(
                        prepared.Engine,
                        cache,
                        landblock,
                        collisions,
                        origin);
            });
    }

    private void AdvanceWork()
    {
        if (_pendingPublicationCancellation
            && _pendingPublication is { } cancelling)
        {
            bool wasCommitted = cancelling.EngineMutationCommitted;
            if (!cancelling.TryCancel())
                return;
            if (wasCommitted || cancelling.EngineMutationCommitted)
                _resident.Add(cancelling.LandblockId);
            _pendingPublication = null;
            _pendingPublicationCancellation = false;
        }

        if (_resetRequired)
        {
            _retirementQueue.Clear();
            foreach (uint landblock in _resident)
                _retirementQueue.Enqueue(landblock);
            _centerLandblock = 0u;
            _resetRequired = false;
        }

        while (_retirementQueue.TryPeek(out uint retiring))
        {
            RuntimeCollisionMutationResult result = _runtime.EntityObjects
                .Physics.WithdrawCollision(retiring);
            if (!result.Completed)
                return;
            _retirementQueue.Dequeue();
            _resident.Remove(retiring);
        }

        if (!_publicationPlanBuilt)
        {
            BuildPublicationPlan(
                _requestedCenterLandblock,
                _publicationQueue);
            _publicationPlanBuilt = true;
        }

        while (true)
        {
            if (_pendingPublication is null)
            {
                if (!_publicationQueue.TryDequeue(out PublicationSpec spec))
                {
                    _centerLandblock = _requestedCenterLandblock;
                    _runtime.EntityObjects.Physics.Engine
                        .UpdatePlayerCurrCell(_requestedFullCell);
                    return;
                }
                _pendingPublication = CreatePublication(
                    spec.LandblockId,
                    spec.Origin,
                    _requestedFullCell,
                    spec.Required);
                if (_pendingPublication is null)
                    continue;
            }

            HeadlessCollisionGenerationAdvance advance =
                _pendingPublication.Advance();
            if (advance.Completed)
            {
                _resident.Add(_pendingPublication.LandblockId);
                _pendingPublication = null;
                continue;
            }
            if (advance.WaitingForProjectionAcknowledgement)
                return;
            if (advance.YieldToCaller)
                return;
            if (!advance.Progressed)
                throw new InvalidOperationException(
                    "Headless collision publication made no progress.");
        }
    }

    private static void BuildPublicationPlan(
        uint center,
        Queue<PublicationSpec> destination)
    {
        destination.Enqueue(new PublicationSpec(
            center,
            Vector3.Zero,
            Required: true));
        int centerX = (int)((center >> 24) & 0xFFu);
        int centerY = (int)((center >> 16) & 0xFFu);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0)
                    continue;
                int x = centerX + dx;
                int y = centerY + dy;
                if ((uint)x > byte.MaxValue || (uint)y > byte.MaxValue)
                    continue;
                destination.Enqueue(new PublicationSpec(
                    ((uint)x << 24) | ((uint)y << 16) | 0xFFFFu,
                    new Vector3(dx * 192f, dy * 192f, 0f),
                    Required: false));
            }
        }
    }

    private static uint CanonicalLandblock(uint fullCellId) =>
        (fullCellId & 0xFFFF0000u) | 0xFFFFu;
}

internal sealed class HeadlessSessionWorldProjection
    : IRuntimeDirectWorldProjection
{
    private readonly GameRuntime _runtime;
    private readonly IHeadlessCollisionNeighborhood _collision;
    private readonly RuntimeFirstEntryDriveController? _firstEntry;
    private readonly RuntimeAcceptedPositionDriveController? _acceptedPositionDrive;
    private readonly Action<string>? _onNonQuiescentStall;
    private uint _requestedLocalPlayerCell;

    private const int NonQuiescentStallPumpThreshold = 300;

    private int _nonQuiescentPumpCount;
    private bool _reportedNonQuiescentStall;

    internal HeadlessSessionWorldProjection(
        GameRuntime runtime,
        HeadlessProcessContentOwner.HeadlessProcessContentLease content,
        RuntimeFirstEntryDriveController? firstEntry = null,
        RuntimeAcceptedPositionDriveController? acceptedPositionDrive = null,
        Action<string>? onNonQuiescentStall = null)
        : this(
            runtime,
            new HeadlessCollisionNeighborhood(runtime, content),
            firstEntry,
            acceptedPositionDrive,
            onNonQuiescentStall)
    {
    }

    internal HeadlessSessionWorldProjection(
        GameRuntime runtime,
        IHeadlessCollisionNeighborhood collision,
        RuntimeFirstEntryDriveController? firstEntry = null,
        RuntimeAcceptedPositionDriveController? acceptedPositionDrive = null,
        Action<string>? onNonQuiescentStall = null)
    {
        _runtime = runtime
            ?? throw new ArgumentNullException(nameof(runtime));
        _collision = collision
            ?? throw new ArgumentNullException(nameof(collision));
        _firstEntry = firstEntry;
        _acceptedPositionDrive = acceptedPositionDrive;
        _onNonQuiescentStall = onNonQuiescentStall;
    }

    public void ProjectSpawn(
        RuntimeEntityRecord record,
        bool isLocalPlayer)
    {
        if (isLocalPlayer
            && record.ServerGuid == _runtime.PlayerIdentity.ServerGuid
            && record.Snapshot.Position is { LandblockId: not 0u } position)
        {
            _requestedLocalPlayerCell = position.LandblockId;
            _collision.CenterOn(position.LandblockId);
        }
        else if (!isLocalPlayer
            && record.Snapshot.Position is
                { LandblockId: not 0u } remotePosition
            && !_collision.IsWithinServiceWindow(remotePosition.LandblockId))
        {
            _ = _runtime.EntityObjects
                .TryConvertInitialResidenceToCellessRoute(record);
        }
        if (!_collision.IsQuiescent)
            return;
        _firstEntry?.DriveAll();
        _acceptedPositionDrive?.Advance();
    }

    public void ProjectPosition(
        RuntimeEntityRecord record,
        bool isLocalPlayer,
        PositionTimestampDisposition disposition)
    {
        if (!isLocalPlayer)
            return;

        if (_runtime.MovementOwner.Controller is null)
        {
            if (record.Snapshot.Position is { LandblockId: not 0u } position)
            {
                _requestedLocalPlayerCell = position.LandblockId;
                _collision.CenterOn(position.LandblockId);
            }
            if (!_collision.IsQuiescent)
                return;
            _firstEntry?.DriveAll();
            _acceptedPositionDrive?.Advance();
        }
    }

    public void CenterOnAcceptedForcePosition(RuntimeEntityRecord record)
    {
        if (record.ServerGuid != _runtime.PlayerIdentity.ServerGuid
            || record.Snapshot.Position is not { LandblockId: not 0u } position)
        {
            return;
        }

        _requestedLocalPlayerCell = position.LandblockId;
        _collision.CenterOn(position.LandblockId);
    }

    internal void PumpFirstEntry()
    {
        if (_requestedLocalPlayerCell != 0u)
            _ = _collision.IsReady(_requestedLocalPlayerCell);
        if (!_collision.IsQuiescent)
        {
            if (++_nonQuiescentPumpCount >= NonQuiescentStallPumpThreshold
                && !_reportedNonQuiescentStall)
            {
                _reportedNonQuiescentStall = true;
                _onNonQuiescentStall?.Invoke(FormattableString.Invariant(
                    $"collision neighborhood non-quiescent for {_nonQuiescentPumpCount} pumps, requested landblock=0x{_requestedLocalPlayerCell:X8}"));
            }
            return;
        }
        _nonQuiescentPumpCount = 0;
        _reportedNonQuiescentStall = false;
        _firstEntry?.DriveAll();
        _acceptedPositionDrive?.Advance();
    }

    public void BeginTeleport()
    {
        if (_runtime.MovementOwner.Controller is { } controller)
            controller.State = PlayerState.PortalSpace;
    }

    private bool _awaitingPortalWake;

    private long _awaitingPortalWakeGeneration;
    private ushort _awaitingPortalWakeSequence;

    private int _notApplicableRetryCount;
    private const int NotApplicableRetryBudget = 50;

    public RuntimeDestinationReadiness PrepareDestination(
        long revealGeneration,
        RuntimeTeleportDestination destination,
        RuntimeWorldHostProjectionToken portal)
    {
        _collision.CenterOn(destination.CellId);
        if (_acceptedPositionDrive is null)
        {
            throw new InvalidOperationException(
                "Headless portal placement requires a wired "
                + "RuntimeAcceptedPositionDriveController - a composition "
                + "regression must not silently disable placement (A3).");
        }

        if (_awaitingPortalWake
            && (_awaitingPortalWakeGeneration != revealGeneration
                || _awaitingPortalWakeSequence != destination.TeleportSequence))
        {
            _awaitingPortalWake = false;
        }

        bool committed;
        if (_awaitingPortalWake)
        {
            if (_acceptedPositionDrive.PendingCount != 0)
            {
                committed = false;
            }
            else
            {
                _awaitingPortalWake = false;
                committed = _acceptedPositionDrive.TryConsumePortalCommit(
                    revealGeneration, destination.TeleportSequence);
            }
        }
        else if (!_collision.IsReady(destination.CellId))
        {
            committed = false;
        }
        else
        {
            var authority = new RuntimePortalPlacementAuthority(
                Present: true,
                RevealGeneration: revealGeneration,
                TeleportSequence: destination.TeleportSequence,
                Projection: portal);
            RuntimeAcceptedPositionExecutionStatus status =
                _acceptedPositionDrive.TryExecuteAcceptedPortalArrival(
                    destination,
                    authority);
            switch (status)
            {
                case RuntimeAcceptedPositionExecutionStatus.Committed:
                    committed = true;
                    _notApplicableRetryCount = 0;
                    break;
                case RuntimeAcceptedPositionExecutionStatus.DeferredCell:
                    _awaitingPortalWake = true;
                    _awaitingPortalWakeGeneration = revealGeneration;
                    _awaitingPortalWakeSequence = destination.TeleportSequence;
                    committed = false;
                    break;
                case RuntimeAcceptedPositionExecutionStatus.Contention:
                    // Transient - some other operation still owns the
                    // entity's placement token. Retried next pump; never a
                    // hard error, matching the graphical arm's D-T5
                    // refusal shape.
                    committed = false;
                    break;
                case RuntimeAcceptedPositionExecutionStatus.NotApplicable:
                    _notApplicableRetryCount++;
                    PhysicsDiagnostics.LogTeleport(
                        "REFUSED",
                        destination.CellId,
                        $"cause=NotApplicable attempt={_notApplicableRetryCount}");
                    if (_notApplicableRetryCount > NotApplicableRetryBudget)
                    {
                        throw new InvalidOperationException(
                            "Headless portal placement stayed NotApplicable "
                            + $"for {_notApplicableRetryCount} consecutive "
                            + "attempts (no canonical body, or an active "
                            + "initial-Create residence still owns the "
                            + "record) - exceeded the bounded retry budget.");
                    }
                    committed = false;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Headless portal placement refused with "
                        + $"status={status} even though the destination's "
                        + "collision neighborhood reported ready - the "
                        + "reveal itself is stale, not recoverable by "
                        + "waiting (contract §4 item 5 forbids "
                        + "acknowledging a materialization that did not "
                        + "happen).");
            }
        }

        if (committed && _runtime.MovementOwner.Controller is { } controller)
            controller.State = PlayerState.InWorld;

        bool indoor = (destination.CellId & 0xFFFFu) >= 0x0100u;
        return new RuntimeDestinationReadiness(
            revealGeneration,
            destination.CellId,
            indoor,
            IsUnhydratable: false,
            RequiredRenderRadius: indoor ? 0 : 1,
            IsRenderNeighborhoodReady: true,
            AreCompositeTexturesReady: true,
            IsCollisionReady: committed);
    }

}
