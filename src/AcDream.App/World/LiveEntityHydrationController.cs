using AcDream.App.Rendering;
using AcDream.App.Input;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Session;

namespace AcDream.App.World;

internal enum LiveProjectionPurpose
{
    LogicalRegistration,
    SpatialRecovery,
    CreateSupersessionRecovery,
    AppearanceMutation,
}

internal interface ILiveEntityProjectionMaterializer
{
    bool TryMaterialize(
        RuntimeEntityRecord expectedCanonical,
        WorldSession.EntitySpawn canonicalSpawn,
        LiveProjectionPurpose purpose,
        ulong expectedCreateIntegrationVersion,
        LiveEntityAppearanceUpdateState? appearanceUpdate = null);

    void ResetSessionState();
}

internal interface ILiveEntityRelationshipProjection
{
    void OnSpawn(WorldSession.EntitySpawn spawn);
    void OnParent(ParentEvent.Parsed update);
    void OnCreateParentAccepted(CreateParentUpdate update);
    ChildUnparentDisposition OnChildBecameUnparented(uint childGuid);
    bool TryApplyAttachedAppearance(
        LiveEntityRecord record,
        ulong objDescAuthorityVersion);
}

internal interface ILiveEntityReadyPublisher
{
    bool Publish(LiveEntityReadyCandidate candidate);
}

internal readonly record struct LiveEntityReadyCandidate(
    LiveEntityRecord Record,
    ulong CreateIntegrationVersion,
    ulong PositionAuthorityVersion,
    ulong ProjectionMutationVersion,
    ulong ObjDescAuthorityVersion,
    LiveEntityProjectionKind ProjectionKind,
    bool IsSpatiallyProjected,
    WorldEntity? WorldEntity)
{
    internal static LiveEntityReadyCandidate Capture(LiveEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new(
            record,
            record.CreateIntegrationVersion,
            record.PositionAuthorityVersion,
            record.ProjectionMutationVersion,
            record.ObjDescAuthorityVersion,
            record.ProjectionKind,
            record.IsSpatiallyProjected,
            record.WorldEntity);
    }

    internal bool IsCurrent(LiveEntityRuntime runtime) =>
        runtime.IsCurrentCreateIntegration(Record, CreateIntegrationVersion)
        && Record.PositionAuthorityVersion == PositionAuthorityVersion
        && Record.ProjectionMutationVersion == ProjectionMutationVersion
        && Record.ObjDescAuthorityVersion == ObjDescAuthorityVersion
        && Record.ProjectionKind == ProjectionKind
        && Record.IsSpatiallyProjected == IsSpatiallyProjected
        && ReferenceEquals(Record.WorldEntity, WorldEntity);
}

internal interface ILiveEntityNetworkUpdateSink
{
    void ApplySameGeneration(SameGenerationCreateObjectEvents events);
}

internal sealed class DeferredLiveEntityNetworkUpdateSink : ILiveEntityNetworkUpdateSink
{
    private ILiveEntityNetworkUpdateSink? _inner;

    public void Bind(ILiveEntityNetworkUpdateSink inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (Interlocked.CompareExchange(ref _inner, inner, null) is not null)
            throw new InvalidOperationException("The live-entity network sink is already bound.");
    }

    public IDisposable BindOwned(ILiveEntityNetworkUpdateSink inner)
    {
        Bind(inner);
        return new Binding(this, inner);
    }

    private void Unbind(ILiveEntityNetworkUpdateSink expected)
    {
        _ = Interlocked.CompareExchange(ref _inner, null, expected);
    }

    public void ApplySameGeneration(SameGenerationCreateObjectEvents events) =>
        (_inner ?? throw new InvalidOperationException(
            "The live-entity network sink must be bound before a session starts."))
        .ApplySameGeneration(events);

    private sealed class Binding : IDisposable
    {
        private DeferredLiveEntityNetworkUpdateSink? _owner;
        private readonly ILiveEntityNetworkUpdateSink _expected;

        public Binding(
            DeferredLiveEntityNetworkUpdateSink owner,
            ILiveEntityNetworkUpdateSink expected)
        {
            _owner = owner;
            _expected = expected;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unbind(_expected);
    }
}

internal sealed class DelegateLiveEntityNetworkUpdateSink(
    Action<SameGenerationCreateObjectEvents> apply) : ILiveEntityNetworkUpdateSink
{
    private readonly Action<SameGenerationCreateObjectEvents> _apply =
        apply ?? throw new ArgumentNullException(nameof(apply));

    public void ApplySameGeneration(SameGenerationCreateObjectEvents events) =>
        _apply(events);
}

internal readonly record struct LiveEntityOriginInitialization(
    bool IsKnown,
    IReadOnlyList<uint> AlreadyLoadedLandblocks);

internal interface ILiveEntityWorldOriginCoordinator
{
    bool IsKnown { get; }
    LiveEntityOriginInitialization TryInitialize(WorldSession.EntitySpawn spawn);
}

internal sealed class LiveEntityHydrationController : ILiveEntityLandblockLoadedSink
{
    private readonly LiveEntityRuntime _runtime;
    private readonly RuntimeEntityObjectLifetime _entityObjects;
    private readonly object _datLock;
    private readonly ILiveEntityProjectionMaterializer _materializer;
    private readonly ILiveEntityRelationshipProjection _relationships;
    private readonly ILiveEntityReadyPublisher _ready;
    private readonly ILiveEntityWorldOriginCoordinator _origin;
    private readonly ILiveEntityNetworkUpdateSink _networkUpdates;
    private readonly IAcceptedLocalPhysicsTimestampPublisher _timestamps;
    private readonly ILocalPlayerIdentitySource _identity;
    private readonly LiveEntityDeletionController _deletion;
    private readonly DormantLiveEntityStore _dormant;
    private readonly RuntimeFirstEntryDriveController? _firstEntry;
    private readonly RuntimeAcceptedPositionDriveController? _acceptedPositionDrive;
    private readonly Dictionary<RuntimeEntityRecord, CanonicalProjectionOperation>
        _projectionOperations =
            new(ReferenceEqualityComparer.Instance);

    private sealed class CanonicalProjectionOperation
    {
        public ulong CreateIntegrationVersion { get; set; }
        public bool RetryRequested { get; set; }
    }

    public LiveEntityHydrationController(
        LiveEntityRuntime runtime,
        RuntimeEntityObjectLifetime entityObjects,
        object datLock,
        ILiveEntityProjectionMaterializer materializer,
        ILiveEntityRelationshipProjection relationships,
        ILiveEntityReadyPublisher ready,
        ILiveEntityWorldOriginCoordinator origin,
        ILiveEntityNetworkUpdateSink networkUpdates,
        IAcceptedLocalPhysicsTimestampPublisher timestamps,
        ILocalPlayerIdentitySource identity,
        LiveEntityDeletionController deletion,
        DormantLiveEntityStore? dormant = null,
        RuntimeFirstEntryDriveController? firstEntry = null,
        RuntimeAcceptedPositionDriveController? acceptedPositionDrive = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _entityObjects = entityObjects
            ?? throw new ArgumentNullException(nameof(entityObjects));
        _datLock = datLock ?? throw new ArgumentNullException(nameof(datLock));
        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
        _relationships = relationships ?? throw new ArgumentNullException(nameof(relationships));
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _networkUpdates = networkUpdates ?? throw new ArgumentNullException(nameof(networkUpdates));
        _timestamps = timestamps ?? throw new ArgumentNullException(nameof(timestamps));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _deletion = deletion ?? throw new ArgumentNullException(nameof(deletion));
        _dormant = dormant ?? new DormantLiveEntityStore();
        _firstEntry = firstEntry;
        _acceptedPositionDrive = acceptedPositionDrive;
    }

    internal event Action<uint>? AppearanceApplied;

    public void OnCreate(WorldSession.EntitySpawn spawn)
    {
        DormantCreateDisposition dormantDisposition =
            _dormant.ClassifyCreate(spawn);
        if (dormantDisposition is DormantCreateDisposition.StaleGeneration)
            return;

        if (dormantDisposition is DormantCreateDisposition.ExistingGeneration
            && _dormant.TryGetExact(
                spawn.Guid,
                spawn.InstanceSequence,
                out WorldSession.EntitySpawn retained))
        {
            OnCreateCore(retained, dormantDisposition);
            if (!spawn.Equals(retained))
                OnCreateCore(spawn, DormantCreateDisposition.NoDormantRecord);
            return;
        }

        OnCreateCore(spawn, dormantDisposition);
    }

    private void OnCreateCore(
        WorldSession.EntitySpawn spawn,
        DormantCreateDisposition dormantDisposition)
    {
        lock (_datLock)
        {
            LiveEntityRegistrationResult registration =
                _runtime.RegisterLiveEntity(
                    spawn,
                    isLocalPlayer: spawn.Guid == _identity.ServerGuid);
            InboundCreateResult result = registration.Inbound;
            if (result.Disposition is
                AcDream.Core.Physics.CreateObjectTimestampDisposition.StaleGeneration)
            {
                return;
            }

            if (registration.Canonical is not { } canonical)
                return;

            _dormant.RemoveThroughAcceptedCreate(spawn);
            ulong createIntegrationVersion = canonical.CreateIntegrationVersion;

            try
            {
                _timestamps.Publish(spawn.Guid, result.Timestamps);
                if (_runtime.IsCurrentCreateIntegration(
                        canonical,
                        createIntegrationVersion)
                    && _entityObjects.ApplyAcceptedSpawn(
                        canonical,
                        createIntegrationVersion,
                        spawn,
                        replaceGeneration: result.Disposition is
                                AcDream.Core.Physics.CreateObjectTimestampDisposition.NewGeneration
                            || dormantDisposition is DormantCreateDisposition.NewGeneration)
                    && _runtime.IsCurrentCreateIntegration(
                        canonical,
                        createIntegrationVersion))
                {
                    if (result.Disposition is
                        AcDream.Core.Physics.CreateObjectTimestampDisposition.ExistingGeneration)
                    {
                        if (registration.LogicalRegistrationCreated)
                        {
                            ProjectExact(
                                canonical,
                                result.Snapshot,
                                LiveProjectionPurpose.LogicalRegistration,
                                expectedCreateIntegrationVersion:
                                    createIntegrationVersion);
                        }
                        else
                        {
                            if (result.SameGenerationEvents is { } refresh)
                                _networkUpdates.ApplySameGeneration(refresh);

                            if (_runtime.IsCurrentCreateIntegration(
                                    canonical,
                                    createIntegrationVersion))
                            {
                                _runtime.TryGetProjection(
                                    canonical,
                                    out LiveEntityRecord? projection);
                                if (projection is not null
                                    && !projection.CreateProjectionSynchronizationPending
                                    && projection.InitialHydrationCompleted)
                                {
                                    goto AppearanceSynchronization;
                                }
                                ProjectExact(
                                    canonical,
                                    canonical.Snapshot,
                                    projection?.CreateProjectionSynchronizationPending
                                        is true
                                        ? LiveProjectionPurpose.CreateSupersessionRecovery
                                        : LiveProjectionPurpose.SpatialRecovery,
                                    expectedCreateIntegrationVersion:
                                        createIntegrationVersion);
                            }
                        }
                    }
                    else
                    {
                        ProjectExact(
                            canonical,
                            result.Snapshot,
                            LiveProjectionPurpose.LogicalRegistration,
                            expectedCreateIntegrationVersion:
                                createIntegrationVersion);
                    }

AppearanceSynchronization:
                    if (_runtime.TryGetProjection(
                            canonical,
                            out LiveEntityRecord? currentProjection)
                        && currentProjection.AppearanceProjectionSynchronizationPending)
                    {
                        SynchronizeAppearance(
                            currentProjection,
                            currentProjection.ObjDescAuthorityVersion);
                    }
                }
            }
            catch (Exception applyFailure)
                when (registration.PriorGenerationCleanupFailure is not null)
            {
                throw new AggregateException(
                    $"Live entity 0x{spawn.Guid:X8} replacement cleanup and installation both failed.",
                    registration.PriorGenerationCleanupFailure,
                    applyFailure);
            }

            if (registration.PriorGenerationCleanupFailure is { } cleanupFailure)
            {
                throw new AggregateException(
                    $"Prior incarnation of live entity 0x{spawn.Guid:X8} failed teardown after its replacement was installed.",
                    cleanupFailure);
            }

            _firstEntry?.DriveAll();
            // C4 route 2: same pump for a parked ForcePosition.
            _acceptedPositionDrive?.Advance();
        }
    }

    public bool OnAppearance(ObjDescEvent.Parsed update)
    {
        lock (_datLock)
        {
            if (!_runtime.TryApplyObjDesc(update, out _))
            {
                return false;
            }

            if (!_runtime.TryGetRecord(
                    update.Guid,
                    out LiveEntityRecord record))
            {
                return _runtime.TryGetCanonical(
                        update.Guid,
                        out RuntimeEntityRecord canonical)
                    && ProjectExact(
                        canonical,
                        canonical.Snapshot,
                        LiveProjectionPurpose.SpatialRecovery,
                        canonical.CreateIntegrationVersion);
            }

            return SynchronizeAppearance(
                record,
                record.ObjDescAuthorityVersion);
        }
    }

    public bool OnPickup(PickupEvent.Parsed update)
    {
        if (!_runtime.TryApplyPickup(update, out _))
            return false;

        ChildUnparentDisposition disposition =
            _relationships.OnChildBecameUnparented(update.Guid);
        bool accepted = disposition is not ChildUnparentDisposition.Superseded;
        return accepted;
    }

    public bool OnDelete(DeleteObject.Parsed delete)
    {
        return _deletion.Delete(delete);
    }

    public bool OnPrune(LiveEntityPruneCandidate candidate) =>
        _deletion.Prune(candidate);

    public int RetryPendingTeardowns() => _runtime.RetryPendingTeardowns();

    public void OnParent(ParentEvent.Parsed update) =>
        _relationships.OnParent(update);

    internal bool TryAcceptParentForProjection(ParentEvent.Parsed update) =>
        _runtime.TryApplyParent(update, out _);

    internal bool OnCreateParentAccepted(CreateParentUpdate update)
    {
        if (!_runtime.TryApplyCreateParent(update, out _))
            return false;
        _relationships.OnCreateParentAccepted(update);
        return true;
    }

    /// <summary>
    /// Reprojects retained live objects after their landblock is loaded. An
    /// fully hydrated record only rebuckets. A retained partial projection
    /// resumes its exact initial transaction without registering logical
    /// mesh/script resources a second time.
    /// </summary>
    public void OnLandblockLoaded(uint loadedLandblockId)
    {
        WorldSession.EntitySpawn[] dormant =
            _dormant.SnapshotLandblock(loadedLandblockId);
        for (int i = 0; i < dormant.Length; i++)
            OnCreate(dormant[i]);

        if (_runtime.Count == 0)
            return;

        uint canonicalLandblock =
            (loadedLandblockId & 0xFFFF0000u) | 0xFFFFu;
        var candidates = new List<RuntimeEntityRecord>();
        foreach (RuntimeEntityRecord candidate in _runtime.CanonicalRecords)
        {
            _runtime.TryGetProjection(
                candidate,
                out LiveEntityRecord? projection);
            if (candidate.ServerGuid == _identity.ServerGuid
                && projection is not null
                && projection.InitialHydrationCompleted
                && !projection.CreateProjectionSynchronizationPending
                && !projection.AppearanceProjectionSynchronizationPending)
            {
                continue;
            }

            if (_runtime.ParentAttachments.HasCommittedParent(candidate.ServerGuid))
                continue;

            uint projectionCellId = projection?.ProjectionCellId
                ?? candidate.Snapshot.Position?.LandblockId
                ?? candidate.FullCellId;
            if (projectionCellId != 0
                && ((projectionCellId & 0xFFFF0000u) | 0xFFFFu)
                    == canonicalLandblock
                && candidate.Snapshot.SetupTableId is not null)
            {
                candidates.Add(candidate);
            }
        }
        if (candidates.Count == 0)
            return;

        lock (_datLock)
        {
            foreach (RuntimeEntityRecord candidate in candidates)
            {
                if (!_runtime.IsCurrentCanonical(candidate))
                    continue;

                _runtime.TryGetProjection(
                    candidate,
                    out LiveEntityRecord? record);
                ulong projectedObjDescVersion =
                    candidate.ObjDescAuthorityVersion;
                bool projectedCanonicalAppearance = false;

                if (record?.CreateProjectionSynchronizationPending is true)
                {
                    if (ProjectExact(
                            candidate,
                            candidate.Snapshot,
                            LiveProjectionPurpose.CreateSupersessionRecovery))
                    {
                        projectedCanonicalAppearance = true;
                    }
                }
                else if (record?.WorldEntity is not null
                    && record.InitialHydrationCompleted)
                {
                    _runtime.RebucketLiveEntity(
                        record.ServerGuid,
                        record.ProjectionCellId);
                }
                else if (ProjectExact(
                    candidate,
                    candidate.Snapshot,
                    LiveProjectionPurpose.SpatialRecovery))
                {
                    projectedCanonicalAppearance = true;
                }

                _runtime.TryGetProjection(candidate, out record);
                if (record is not null
                    && projectedCanonicalAppearance
                    && record.AppearanceProjectionSynchronizationPending)
                {
                    CompleteAppearanceProjectionSynchronization(
                        record,
                        projectedObjDescVersion);
                }

                if (record is not null
                    && _runtime.IsCurrentRecord(record)
                    && record.AppearanceProjectionSynchronizationPending)
                {
                    SynchronizeAppearance(
                        record,
                        record.ObjDescAuthorityVersion);
                }
            }
        }
    }

    public bool EnsureWorldOrigin(
        LiveEntityRecord expectedRecord,
        ulong positionAuthorityVersion,
        WorldSession.EntitySpawn acceptedSpawn)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        lock (_datLock)
        {
            if (!_runtime.IsCurrentPositionAuthority(
                    expectedRecord,
                    positionAuthorityVersion))
            {
                return false;
            }

            InitializeOriginAndRecoverLoaded(acceptedSpawn);
            return _runtime.IsCurrentPositionAuthority(
                expectedRecord,
                positionAuthorityVersion);
        }
    }

    public bool RecoverProjection(
        LiveEntityRecord expectedRecord,
        ulong positionAuthorityVersion,
        WorldSession.EntitySpawn acceptedSpawn)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        lock (_datLock)
        {
            if (!_runtime.IsCurrentPositionAuthority(
                    expectedRecord,
                    positionAuthorityVersion))
            {
                return false;
            }

            ulong projectedObjDescVersion = expectedRecord.ObjDescAuthorityVersion;
            bool projected = ProjectExact(
                    expectedRecord,
                    acceptedSpawn,
                    expectedRecord.CreateProjectionSynchronizationPending
                        ? LiveProjectionPurpose.CreateSupersessionRecovery
                        : LiveProjectionPurpose.SpatialRecovery)
                && _runtime.IsCurrentPositionAuthority(
                    expectedRecord,
                    positionAuthorityVersion);
            if (!projected)
                return false;

            if (expectedRecord.AppearanceProjectionSynchronizationPending)
            {
                if (_runtime.IsCurrentObjDescAuthority(
                        expectedRecord,
                        projectedObjDescVersion))
                {
                    CompleteAppearanceProjectionSynchronization(
                        expectedRecord,
                        projectedObjDescVersion);
                }
                else if (!SynchronizeAppearance(
                    expectedRecord,
                    expectedRecord.ObjDescAuthorityVersion))
                {
                    return false;
                }
            }

            return _runtime.IsCurrentPositionAuthority(
                expectedRecord,
                positionAuthorityVersion);
        }
    }

    public bool RecoverCanonicalProjection(
        RuntimeEntityRecord expectedCanonical,
        ulong positionAuthorityVersion,
        out LiveEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(expectedCanonical);
        lock (_datLock)
        {
            record = null!;
            if (!_runtime.IsCurrentPositionAuthority(
                    expectedCanonical,
                    positionAuthorityVersion))
            {
                return false;
            }

            if (_runtime.TryGetProjection(
                    expectedCanonical,
                    out record))
            {
                return _runtime.IsCurrentPositionAuthority(
                    record,
                    positionAuthorityVersion);
            }

            WorldSession.EntitySpawn canonicalSpawn =
                expectedCanonical.Snapshot;
            if (canonicalSpawn.Position is null)
                return false;

            InitializeOriginAndRecoverLoaded(canonicalSpawn);
            if (!_runtime.IsCurrentPositionAuthority(
                    expectedCanonical,
                    positionAuthorityVersion)
                || !ProjectExact(
                    expectedCanonical,
                    canonicalSpawn,
                    LiveProjectionPurpose.SpatialRecovery,
                    expectedCanonical.CreateIntegrationVersion)
                || !_runtime.TryGetProjection(
                    expectedCanonical,
                    out record))
            {
                record = null!;
                return false;
            }

            return _runtime.IsCurrentPositionAuthority(
                record,
                positionAuthorityVersion);
        }
    }

    public bool OnEntityReady(LiveEntityReadyCandidate candidate)
    {
        if (!PublishReady(candidate))
        {
            return false;
        }

        if (candidate.Record.AppearanceProjectionSynchronizationPending
            && !candidate.Record.AppearanceHydrationInProgress)
        {
            CompleteAppearanceProjectionSynchronization(
                candidate.Record,
                candidate.ObjDescAuthorityVersion);
        }

        return candidate.IsCurrent(_runtime)
            && (!candidate.Record.CreateProjectionSynchronizationPending
            || _runtime.TryCompleteCreateProjectionSynchronization(
                candidate.Record,
                candidate.CreateIntegrationVersion));
    }

    public void ResetSessionState() => _materializer.ResetSessionState();

    private bool SynchronizeAppearance(
        LiveEntityRecord expectedRecord,
        ulong expectedObjDescAuthorityVersion)
    {
        while (true)
        {
            if (!_runtime.TryBeginAppearanceHydration(
                    expectedRecord,
                    expectedObjDescAuthorityVersion))
            {
                return false;
            }

            bool retry;
            bool published;
            try
            {
                if (expectedRecord.ProjectionKind is
                    LiveEntityProjectionKind.Attached)
                {
                    published = _relationships.TryApplyAttachedAppearance(
                        expectedRecord,
                        expectedObjDescAuthorityVersion);
                }
                else
                {
                    LiveEntityAppearanceUpdateState? appearanceState =
                        LiveEntityAppearanceBinding.Capture(
                            _runtime,
                            expectedRecord.ServerGuid);
                    if (appearanceState is null)
                    {
                        published = ProjectExact(
                            expectedRecord,
                            expectedRecord.Snapshot,
                            LiveProjectionPurpose.SpatialRecovery);
                    }
                    else
                    {
                        published = _materializer.TryMaterialize(
                            expectedRecord.Canonical,
                            expectedRecord.Snapshot,
                            LiveProjectionPurpose.AppearanceMutation,
                            expectedRecord.CreateIntegrationVersion,
                            appearanceState);
                    }
                }
            }
            finally
            {
                retry = _runtime.EndAppearanceHydration(expectedRecord);
            }

            if (retry && _runtime.IsCurrentRecord(expectedRecord))
            {
                expectedObjDescAuthorityVersion =
                    expectedRecord.ObjDescAuthorityVersion;
                continue;
            }

            return published
                && CompleteAppearanceProjectionSynchronization(
                    expectedRecord,
                    expectedObjDescAuthorityVersion);
        }
    }

    private bool ProjectExact(
        LiveEntityRecord expectedRecord,
        WorldSession.EntitySpawn acceptedSpawn,
        LiveProjectionPurpose purpose,
        ulong? expectedCreateIntegrationVersion = null) =>
        ProjectExact(
            expectedRecord.Canonical,
            acceptedSpawn,
            purpose,
            expectedCreateIntegrationVersion);

    private bool ProjectExact(
        RuntimeEntityRecord expectedCanonical,
        WorldSession.EntitySpawn acceptedSpawn,
        LiveProjectionPurpose purpose,
        ulong? expectedCreateIntegrationVersion = null)
    {
        if (_projectionOperations.TryGetValue(
                expectedCanonical,
                out CanonicalProjectionOperation? active))
        {
            if (expectedCanonical.CreateIntegrationVersion
                != active.CreateIntegrationVersion)
            {
                active.RetryRequested = true;
            }
            return false;
        }

        var operation = new CanonicalProjectionOperation();
        _projectionOperations.Add(expectedCanonical, operation);
        try
        {
            while (true)
            {
                ulong createIntegrationVersion = expectedCreateIntegrationVersion
                    ?? expectedCanonical.CreateIntegrationVersion;
                if (!_runtime.IsCurrentCreateIntegration(
                        expectedCanonical,
                        createIntegrationVersion))
                {
                    return false;
                }
                if (purpose is LiveProjectionPurpose.CreateSupersessionRecovery
                    && _runtime.TryGetProjection(
                        expectedCanonical,
                        out LiveEntityRecord? synchronizedProjection)
                    && !_runtime.TryMarkCreateProjectionSynchronizationPending(
                        synchronizedProjection,
                        createIntegrationVersion))
                {
                    return false;
                }

                operation.RetryRequested = false;
                operation.CreateIntegrationVersion =
                    createIntegrationVersion;
                bool result = ProjectExactOnce(
                    expectedCanonical,
                    acceptedSpawn,
                    purpose,
                    createIntegrationVersion);

                bool retry = operation.RetryRequested
                    || (_runtime.IsCurrentCanonical(expectedCanonical)
                        && expectedCanonical.CreateIntegrationVersion
                            != createIntegrationVersion);
                if (!retry || !_runtime.IsCurrentCanonical(expectedCanonical))
                    return result;

                if (_runtime.TryGetProjection(
                        expectedCanonical,
                        out LiveEntityRecord? retryProjection))
                {
                    retryProjection.CreateProjectionSynchronizationPending = true;
                }
                acceptedSpawn = expectedCanonical.Snapshot;
                purpose = LiveProjectionPurpose.CreateSupersessionRecovery;
                expectedCreateIntegrationVersion =
                    expectedCanonical.CreateIntegrationVersion;
            }
        }
        catch
        {
            if (_runtime.IsCurrentCanonical(expectedCanonical)
                && _runtime.TryGetProjection(
                    expectedCanonical,
                    out LiveEntityRecord? interruptedProjection))
            {
                interruptedProjection.CreateProjectionSynchronizationPending = true;
            }
            throw;
        }
        finally
        {
            if (!_projectionOperations.Remove(expectedCanonical, out var removed)
                || !ReferenceEquals(removed, operation))
            {
                throw new InvalidOperationException(
                    "Canonical projection construction ownership was corrupted.");
            }
        }
    }

    private bool ProjectExactOnce(
        RuntimeEntityRecord expectedCanonical,
        WorldSession.EntitySpawn acceptedSpawn,
        LiveProjectionPurpose purpose,
        ulong createIntegrationVersion)
    {
        _relationships.OnSpawn(acceptedSpawn);
        if (!_runtime.IsCurrentCreateIntegration(
                expectedCanonical,
                createIntegrationVersion))
            return false;

        WorldSession.EntitySpawn canonicalSpawn = expectedCanonical.Snapshot;
        InitializeOriginAndRecoverLoaded(canonicalSpawn);
        if (!_runtime.IsCurrentCreateIntegration(
                expectedCanonical,
                createIntegrationVersion)
            || !_origin.IsKnown)
        {
            return false;
        }

        _runtime.TryGetProjection(
            expectedCanonical,
            out LiveEntityRecord? expectedRecord);

        if (canonicalSpawn.Position is null)
        {
            if (canonicalSpawn.ParentGuid is not null and not 0)
            {
                if (expectedRecord is null)
                    return false;

                if (expectedRecord.InitialHydrationCompleted
                    && !expectedRecord.CreateProjectionSynchronizationPending)
                {
                    return _runtime.IsCurrentCreateIntegration(
                        expectedRecord,
                        createIntegrationVersion);
                }

                if (expectedRecord.WorldEntity is null
                    || expectedRecord.ProjectionKind is not
                        LiveEntityProjectionKind.Attached
                    || !expectedRecord.IsSpatiallyProjected
                    || !PublishReady(
                        expectedRecord,
                        createIntegrationVersion))
                {
                    return false;
                }

                return !expectedRecord.CreateProjectionSynchronizationPending
                    || _runtime.TryCompleteCreateProjectionSynchronization(
                        expectedRecord,
                        createIntegrationVersion);
            }

            if (expectedCanonical.FullCellId != 0
                || expectedRecord?.IsSpatiallyProjected is true)
            {
                return false;
            }

            if (expectedRecord?.WorldEntity is null)
            {
                return expectedRecord is null
                    || !expectedRecord.CreateProjectionSynchronizationPending
                    || _runtime.TryCompleteCreateProjectionSynchronization(
                        expectedRecord,
                        createIntegrationVersion);
            }

            if (!PublishReady(
                    expectedRecord,
                    createIntegrationVersion))
            {
                return false;
            }

            return !expectedRecord.CreateProjectionSynchronizationPending
                || _runtime.TryCompleteCreateProjectionSynchronization(
                    expectedRecord,
                    createIntegrationVersion);
        }

        bool materialized = _materializer.TryMaterialize(
            expectedCanonical,
            expectedCanonical.Snapshot,
            purpose,
            createIntegrationVersion);
        if (!materialized
            || !_runtime.IsCurrentCreateIntegration(
                expectedCanonical,
                createIntegrationVersion)
            || purpose is LiveProjectionPurpose.AppearanceMutation)
        {
            return materialized;
        }

        if (!_runtime.TryGetProjection(
                expectedCanonical,
                out expectedRecord))
        {
            return false;
        }

        if (!PublishReady(
                expectedRecord,
                createIntegrationVersion))
        {
            return false;
        }

        return purpose is not LiveProjectionPurpose.CreateSupersessionRecovery
            || _runtime.TryCompleteCreateProjectionSynchronization(
                expectedRecord,
                createIntegrationVersion);
    }

    private bool PublishReady(
        LiveEntityRecord expectedRecord,
        ulong expectedCreateIntegrationVersion)
    {
        if (!_runtime.IsCurrentCreateIntegration(
                expectedRecord,
                expectedCreateIntegrationVersion))
        {
            return false;
        }

        return PublishReady(LiveEntityReadyCandidate.Capture(expectedRecord));
    }

    private bool PublishReady(LiveEntityReadyCandidate candidate)
    {
        LiveEntityRecord expectedRecord = candidate.Record;
        if (!candidate.IsCurrent(_runtime)
            || !_ready.Publish(candidate)
            || !candidate.IsCurrent(_runtime))
        {
            return false;
        }

        return _runtime.TryMarkInitialHydrationCompleted(
                expectedRecord,
                candidate.CreateIntegrationVersion)
            && candidate.IsCurrent(_runtime);
    }

    private bool CompleteAppearanceProjectionSynchronization(
        LiveEntityRecord expectedRecord,
        ulong expectedObjDescAuthorityVersion)
    {
        if (!_runtime.TryCompleteAppearanceProjectionSynchronization(
                expectedRecord,
                expectedObjDescAuthorityVersion))
        {
            return false;
        }

        AppearanceApplied?.Invoke(expectedRecord.ServerGuid);
        return _runtime.IsCurrentObjDescAuthority(
            expectedRecord,
            expectedObjDescAuthorityVersion);
    }

    private void InitializeOriginAndRecoverLoaded(
        WorldSession.EntitySpawn canonicalSpawn)
    {
        LiveEntityOriginInitialization initialization =
            _origin.TryInitialize(canonicalSpawn);
        if (!initialization.IsKnown)
            return;

        for (int i = 0; i < initialization.AlreadyLoadedLandblocks.Count; i++)
            OnLandblockLoaded(initialization.AlreadyLoadedLandblocks[i]);
    }
}
