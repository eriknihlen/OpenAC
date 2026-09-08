using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Entities;

public readonly record struct RuntimeEntityRegistrationResult(
    InboundCreateResult Inbound,
    RuntimeEntityRecord? Canonical,
    bool LogicalRegistrationCreated,
    bool ReplacedExistingGeneration,
    Exception? PriorGenerationCleanupFailure = null,
    bool DeferredForParent = false);

public readonly record struct RuntimeEntityObjectOwnershipSnapshot(
    int ActiveEntityCount,
    int TeardownEntityCount,
    int ClaimedLocalIdCount,
    int AcceptedSnapshotCount,
    int UnresolvedParentRelationCount,
    int DeferredParentCreateCount,
    int StagedParentRelationCount,
    int RecoveryParentRelationCount,
    int CommittedParentRelationCount,
    int ObjectCount,
    int ContainerCount,
    int ContainerProjectionCount,
    int EquipmentOwnerCount,
    int PendingMoveCount,
    int InitialCreateResidenceLeaseCount,
    int InitialCreateExecutorProgressCount,
    int StreamSubscriberCount,
    int PlacementStreamSubscriberCount,
    long StreamDispatchFailureCount,
    bool HasLastStreamDispatchFailure,
    int PendingDispatchCount,
    bool IsDispatching,
    bool IsSessionClearInProgress,
    bool IsDisposed,
    int DeferredAcceptedRelationCount = 0,
    long ReplayFailureCount = 0,
    bool HasLastReplayFailure = false,
    int PendingCompletionReceiptCount = 0,
    int LocalPlayerFirstEntryActiveCount = 0,
    int RemoteFirstEntryActiveCount = 0,
    int FirstEntryDrivePendingCount = 0,
    int AcceptedPositionDrivePendingCount = 0,
    int RemotePlacementDrivePendingCount = 0)
{
    public bool IsConverged =>
        IsDisposed
        && ActiveEntityCount == 0
        && TeardownEntityCount == 0
        && ClaimedLocalIdCount == 0
        && AcceptedSnapshotCount == 0
        && UnresolvedParentRelationCount == 0
        && DeferredParentCreateCount == 0
        && DeferredAcceptedRelationCount == 0
        && StagedParentRelationCount == 0
        && RecoveryParentRelationCount == 0
        && CommittedParentRelationCount == 0
        && ObjectCount == 0
        && ContainerCount == 0
        && ContainerProjectionCount == 0
        && EquipmentOwnerCount == 0
        && PendingMoveCount == 0
        && InitialCreateResidenceLeaseCount == 0
        && InitialCreateExecutorProgressCount == 0
        && PendingCompletionReceiptCount == 0
        && LocalPlayerFirstEntryActiveCount == 0
        && RemoteFirstEntryActiveCount == 0
        && FirstEntryDrivePendingCount == 0
        && AcceptedPositionDrivePendingCount == 0
        && RemotePlacementDrivePendingCount == 0
        && StreamSubscriberCount == 0
        && PlacementStreamSubscriberCount == 0
        && PendingDispatchCount == 0
        && !IsDispatching
        && !IsSessionClearInProgress;
}

public sealed class RuntimeEntityDeleteAcceptance
{
    internal RuntimeEntityDeleteAcceptance(
        RuntimeEntityObjectLifetime owner,
        DeleteObject.Parsed delete,
        RuntimeEntityRecord? retiredCanonical,
        bool removeRetainedObject)
    {
        Owner = owner;
        Delete = delete;
        RetiredCanonical = retiredCanonical;
        RemoveRetainedObject = removeRetainedObject;
    }

    internal RuntimeEntityObjectLifetime Owner { get; }
    internal bool Completed { get; set; }
    public DeleteObject.Parsed Delete { get; }
    public RuntimeEntityRecord? RetiredCanonical { get; }
    public bool RemoveRetainedObject { get; }
}

public sealed class RuntimeEntityObjectLifetime : IDisposable
{
    private bool _sessionClearInProgress;
    private bool _disposed;
    private Action<RuntimeEntityRecord>? _initialResidenceBegan;
    private readonly List<Func<int>> _firstEntryDriveOwnership = [];
    private readonly List<Func<int>> _acceptedPositionDriveOwnership = [];
    private readonly List<Func<int>> _remotePlacementDriveOwnership = [];
    private Func<RuntimeGenerationToken>? _generation;
    private readonly RuntimeEntityPvpBitfieldSnapshotSync _pvpBitfieldSync;

    public RuntimeEntityObjectLifetime(
        uint firstLocalEntityId = RuntimeEntityDirectory.FirstLocalEntityId,
        TimeProvider? timeProvider = null,
        IGameRuntimeClock? gameClock = null)
    {
        Entities = new RuntimeEntityDirectory(firstLocalEntityId);
        Physics = new RuntimePhysicsState(
            Entities,
            timeProvider: timeProvider,
            gameClock: gameClock);
        Objects = new ClientObjectTable();
        _pvpBitfieldSync = new RuntimeEntityPvpBitfieldSnapshotSync(Entities, Objects);
        Physics.Engine.Objects = Objects;
        var views = new RuntimeEntityObjectViews(Entities, Objects);
        EntityView = views.Entities;
        InventoryView = views.Inventory;
        Events = new RuntimeEntityObjectEventStream(Entities, Objects);
        Physics.SetPosition.BindEventStream(Events);
        InitialCreateResidences = new RuntimeInitialCreateResidenceState(
            Entities,
            Physics.SetPosition);
        InitialCreateExecution = new RuntimeInitialCreateContinuationExecutor(
            Entities,
            InitialCreateResidences,
            Physics,
            Events,
            (spawn, isLocalPlayer) =>
                RegisterEntityWithInitialResidence(spawn, isLocalPlayer),
            (canonical, version, spawn, replaceGeneration) =>
                ApplyAcceptedSpawn(canonical, version, spawn, replaceGeneration));
        LocalPlayerFirstEntry = new RuntimeLocalPlayerFirstEntryState(
            InitialCreateResidences,
            InitialCreateExecution,
            Physics);
        RemoteFirstEntry = new RuntimeRemoteFirstEntryState(
            InitialCreateResidences,
            InitialCreateExecution,
            Physics);
        InitialCreateResidences.BindRetirementNotification(
            key => InitialCreateExecution.DiscardProgress(key));
        InitialCreateResidences.BindRetirementNotification(
            key => LocalPlayerFirstEntry.Forget(key));
        InitialCreateResidences.BindRetirementNotification(
            key => RemoteFirstEntry.Forget(key));
        Physics.SetPosition.BindExecutorCompletionAcknowledgement(
            (key, sequence) =>
                InitialCreateExecution.ForgetCompletionReceipt(key, sequence));
        Placements = new RuntimePlacementProjectionChannel(
            Events,
            Physics.SetPosition,
            InitialCreateExecution);
    }

    internal RuntimeEntityObjectLifetime(
        PhysicsDataCache physicsDataCache,
        uint firstLocalEntityId = RuntimeEntityDirectory.FirstLocalEntityId,
        TimeProvider? timeProvider = null,
        IGameRuntimeClock? gameClock = null)
    {
        ArgumentNullException.ThrowIfNull(physicsDataCache);
        Entities = new RuntimeEntityDirectory(firstLocalEntityId);
        Physics = new RuntimePhysicsState(
            Entities,
            physicsDataCache,
            timeProvider,
            gameClock);
        Objects = new ClientObjectTable();
        _pvpBitfieldSync = new RuntimeEntityPvpBitfieldSnapshotSync(Entities, Objects);
        Physics.Engine.Objects = Objects;
        var views = new RuntimeEntityObjectViews(Entities, Objects);
        EntityView = views.Entities;
        InventoryView = views.Inventory;
        Events = new RuntimeEntityObjectEventStream(Entities, Objects);
        Physics.SetPosition.BindEventStream(Events);
        InitialCreateResidences = new RuntimeInitialCreateResidenceState(
            Entities,
            Physics.SetPosition);
        InitialCreateExecution = new RuntimeInitialCreateContinuationExecutor(
            Entities,
            InitialCreateResidences,
            Physics,
            Events,
            (spawn, isLocalPlayer) =>
                RegisterEntityWithInitialResidence(spawn, isLocalPlayer),
            (canonical, version, spawn, replaceGeneration) =>
                ApplyAcceptedSpawn(canonical, version, spawn, replaceGeneration));
        LocalPlayerFirstEntry = new RuntimeLocalPlayerFirstEntryState(
            InitialCreateResidences,
            InitialCreateExecution,
            Physics);
        RemoteFirstEntry = new RuntimeRemoteFirstEntryState(
            InitialCreateResidences,
            InitialCreateExecution,
            Physics);
        InitialCreateResidences.BindRetirementNotification(
            key => InitialCreateExecution.DiscardProgress(key));
        InitialCreateResidences.BindRetirementNotification(
            key => LocalPlayerFirstEntry.Forget(key));
        InitialCreateResidences.BindRetirementNotification(
            key => RemoteFirstEntry.Forget(key));
        Physics.SetPosition.BindExecutorCompletionAcknowledgement(
            (key, sequence) =>
                InitialCreateExecution.ForgetCompletionReceipt(key, sequence));
        Placements = new RuntimePlacementProjectionChannel(
            Events,
            Physics.SetPosition,
            InitialCreateExecution);
    }

    internal RuntimeEntityObjectLifetime(
        PhysicsEngine physicsEngine,
        uint firstLocalEntityId = RuntimeEntityDirectory.FirstLocalEntityId,
        TimeProvider? timeProvider = null,
        IGameRuntimeClock? gameClock = null)
    {
        ArgumentNullException.ThrowIfNull(physicsEngine);
        Entities = new RuntimeEntityDirectory(firstLocalEntityId);
        Physics = new RuntimePhysicsState(
            Entities,
            physicsEngine,
            timeProvider,
            gameClock);
        Objects = new ClientObjectTable();
        _pvpBitfieldSync = new RuntimeEntityPvpBitfieldSnapshotSync(Entities, Objects);
        Physics.Engine.Objects = Objects;
        var views = new RuntimeEntityObjectViews(Entities, Objects);
        EntityView = views.Entities;
        InventoryView = views.Inventory;
        Events = new RuntimeEntityObjectEventStream(Entities, Objects);
        Physics.SetPosition.BindEventStream(Events);
        InitialCreateResidences = new RuntimeInitialCreateResidenceState(
            Entities,
            Physics.SetPosition);
        InitialCreateExecution = new RuntimeInitialCreateContinuationExecutor(
            Entities,
            InitialCreateResidences,
            Physics,
            Events,
            (spawn, isLocalPlayer) =>
                RegisterEntityWithInitialResidence(spawn, isLocalPlayer),
            (canonical, version, spawn, replaceGeneration) =>
                ApplyAcceptedSpawn(canonical, version, spawn, replaceGeneration));
        LocalPlayerFirstEntry = new RuntimeLocalPlayerFirstEntryState(
            InitialCreateResidences,
            InitialCreateExecution,
            Physics);
        RemoteFirstEntry = new RuntimeRemoteFirstEntryState(
            InitialCreateResidences,
            InitialCreateExecution,
            Physics);
        InitialCreateResidences.BindRetirementNotification(
            key => InitialCreateExecution.DiscardProgress(key));
        InitialCreateResidences.BindRetirementNotification(
            key => LocalPlayerFirstEntry.Forget(key));
        InitialCreateResidences.BindRetirementNotification(
            key => RemoteFirstEntry.Forget(key));
        Physics.SetPosition.BindExecutorCompletionAcknowledgement(
            (key, sequence) =>
                InitialCreateExecution.ForgetCompletionReceipt(key, sequence));
        Placements = new RuntimePlacementProjectionChannel(
            Events,
            Physics.SetPosition,
            InitialCreateExecution);
    }

    public RuntimeEntityDirectory Entities { get; }
    public RuntimePhysicsState Physics { get; }
    public ClientObjectTable Objects { get; }
    public IRuntimeEntityView EntityView { get; }
    public IRuntimeInventoryView InventoryView { get; }
    public RuntimeEntityObjectEventStream Events { get; }
    public RuntimePlacementProjectionChannel Placements { get; }
    internal RuntimeInitialCreateResidenceState InitialCreateResidences
        { get; }
    internal RuntimeInitialCreateContinuationExecutor InitialCreateExecution
        { get; }
    internal RuntimeLocalPlayerFirstEntryState LocalPlayerFirstEntry { get; }
    internal RuntimeRemoteFirstEntryState RemoteFirstEntry { get; }

    public RuntimeEntityObjectOwnershipSnapshot CaptureOwnership()
    {
        ParentAttachmentState parents = Entities.ParentAttachments;
        RuntimeInitialCreateResidenceOwnershipSnapshot initialResidence =
            InitialCreateResidences.CaptureOwnership();
        return new RuntimeEntityObjectOwnershipSnapshot(
            Entities.Count,
            Entities.PendingTeardownCount,
            Entities.ClaimedLocalIdCount,
            Entities.Snapshots.Count,
            parents.UnresolvedRelationCount,
            parents.DeferredCreateCount,
            parents.StagedRelationCount,
            parents.RecoveryRelationCount,
            parents.CommittedRelationCount,
            Objects.ObjectCount,
            Objects.ContainerCount,
            Objects.ContainerProjectionCount,
            Objects.EquipmentOwnerCount,
            Objects.PendingMoveCount,
            initialResidence.ActiveLeaseCount
                + initialResidence.PendingAdoptionCount,
            InitialCreateExecution.ProgressCount,
            Events.SubscriberCount,
            Events.PlacementSubscriberCount,
            Events.DispatchFailureCount,
            Events.LastDispatchFailure is not null,
            Events.PendingDispatchCount,
            Events.IsDispatching,
            _sessionClearInProgress,
            _disposed,
            parents.DeferredAcceptedRelationCount,
            InitialCreateExecution.ReplayFailureCount,
            InitialCreateExecution.LastReplayFailure is not null,
            InitialCreateExecution.PendingCompletionReceiptCount,
            LocalPlayerFirstEntry.CaptureOwnership().ActiveCount,
            RemoteFirstEntry.CaptureOwnership().ActiveCount,
            CaptureFirstEntryDrivePendingCount(),
            CaptureAcceptedPositionDrivePendingCount(),
            CaptureRemotePlacementDrivePendingCount());
    }

    private int CaptureFirstEntryDrivePendingCount()
    {
        int total = 0;
        for (int i = 0; i < _firstEntryDriveOwnership.Count; i++)
            total = checked(total + _firstEntryDriveOwnership[i]());
        return total;
    }

    private int CaptureAcceptedPositionDrivePendingCount()
    {
        int total = 0;
        for (int i = 0; i < _acceptedPositionDriveOwnership.Count; i++)
            total = checked(total + _acceptedPositionDriveOwnership[i]());
        return total;
    }

    private int CaptureRemotePlacementDrivePendingCount()
    {
        int total = 0;
        for (int i = 0; i < _remotePlacementDriveOwnership.Count; i++)
            total = checked(total + _remotePlacementDriveOwnership[i]());
        return total;
    }

    public void RegisterFirstEntryDriveOwnership(Func<int> pendingCount)
    {
        ArgumentNullException.ThrowIfNull(pendingCount);
        EnsureNotDisposed();
        _firstEntryDriveOwnership.Add(pendingCount);
    }

    public void RegisterAcceptedPositionDriveOwnership(Func<int> pendingCount)
    {
        ArgumentNullException.ThrowIfNull(pendingCount);
        EnsureNotDisposed();
        _acceptedPositionDriveOwnership.Add(pendingCount);
    }

    public void RegisterRemotePlacementDriveOwnership(Func<int> pendingCount)
    {
        ArgumentNullException.ThrowIfNull(pendingCount);
        EnsureNotDisposed();
        _remotePlacementDriveOwnership.Add(pendingCount);
    }

    public void BindEventContext(
        Func<RuntimeGenerationToken> generation,
        Func<ulong> frameNumber)
    {
        EnsureNotDisposed();
        _generation = generation;
        Events.BindContext(generation, frameNumber);
        Placements.BindGeneration(generation);
        InitialCreateResidences.BindGeneration(generation);
        InitialCreateExecution.BindGeneration(generation);
    }

    internal RuntimeAuthoritativePositionRoute? ClassifyRemoteAcceptedPosition(
        RuntimeEntityRecord canonical,
        in WorldSession.EntityPositionUpdate update,
        PositionTimestampDisposition disposition,
        in AcceptedPhysicsTimestamps timestamps,
        float? playerDistance)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        if (_generation is not { } generation
            || timestamps.PreMergeCommittedCellId is not { } preMergeCommittedCellId)
        {
            return null;
        }
        RuntimePositionEntityKind kind =
            (canonical.FinalPhysicsState & PhysicsStateFlags.Missile) != 0
                && canonical.Projectile is RuntimeProjectile boundProjectile
                && ReferenceEquals(canonical.PhysicsBody, boundProjectile.Body)
                ? RuntimePositionEntityKind.Projectile
                : RuntimePositionEntityKind.Remote;
        if (!RuntimeAcceptedPositionRouteRequests.TryBuild(
                generation(),
                canonical,
                update,
                kind,
                RuntimeAcceptedPositionSource.PositionEvent,
                disposition,
                timestamps.PreviousTeleport,
                timestamps.Teleport,
                playerDistance,
                usePositionFromServer: false,
                preMergeCommittedCellId,
                out RuntimeAcceptedPositionRouteRequest request))
        {
            return null;
        }

        return RuntimeAuthoritativePositionRouteClassifier
            .ClassifyAcceptedPosition(request);
    }

    public void BindInitialResidenceBeginNotification(
        Action<RuntimeEntityRecord> began)
    {
        ArgumentNullException.ThrowIfNull(began);
        EnsureNotDisposed();
        _initialResidenceBegan += began;
    }

    public void BindLiveInputs(
        Func<bool> usePositionFromServer,
        Func<Vector3?> localPlayerPosition)
    {
        EnsureNotDisposed();
        InitialCreateExecution.BindLiveInputs(
            usePositionFromServer, localPlayerPosition);
    }

    public RuntimeEntityRegistrationResult RegisterEntity(
        WorldSession.EntitySpawn incoming,
        Func<RuntimeEntityRecord, Exception?>? retirePriorProjection = null) =>
        RegisterEntityCore(
            incoming,
            beginInitialResidence: false,
            isLocalPlayer: false,
            retirePriorProjection);

    internal RuntimeEntityRegistrationResult RegisterEntityWithInitialResidence(
        WorldSession.EntitySpawn incoming,
        bool isLocalPlayer,
        Func<RuntimeEntityRecord, Exception?>? retirePriorProjection = null) =>
        RegisterEntityCore(
            incoming,
            beginInitialResidence: true,
            isLocalPlayer,
            retirePriorProjection);

    private RuntimeEntityRegistrationResult RegisterEntityCore(
        WorldSession.EntitySpawn incoming,
        bool beginInitialResidence,
        bool isLocalPlayer,
        Func<RuntimeEntityRecord, Exception?>? retirePriorProjection)
    {
        EnsureNotDisposed();
        if (beginInitialResidence && isLocalPlayer)
        {
            Physics.ObserveLocalPlayerCreate(
                (incoming.Physics?.Position ?? incoming.Position)
                    ?.LandblockId ?? 0u);
        }
        if (_sessionClearInProgress)
        {
            throw new InvalidOperationException(
                "A Runtime entity cannot register while its session lifetime is clearing.");
        }
        if (beginInitialResidence
            && !HasConsistentCreateIdentityAndParent(incoming))
        {
            throw new InvalidOperationException(
                $"CreateObject 0x{incoming.Guid:X8} has inconsistent instance or parent projections.");
        }
        if (beginInitialResidence)
            incoming = RuntimeInitialCreateAdmissionFreezer.Freeze(incoming);
        uint parentGuid = incoming.ParentGuid
            ?? incoming.Physics?.Parent?.Guid
            ?? 0u;
        if (beginInitialResidence
            && parentGuid != 0u
            && !Entities.TryGetActive(parentGuid, out _))
        {
            Entities.ParentAttachments.EnqueueDeferredCreate(
                incoming,
                isLocalPlayer);
            return new RuntimeEntityRegistrationResult(
                SupersededCreateResult(),
                Canonical: null,
                LogicalRegistrationCreated: false,
                ReplacedExistingGeneration: false,
                DeferredForParent: true);
        }
        CreateObjectTimestampDisposition preview =
            Entities.PreviewCreateDisposition(incoming);
        bool requiresFreshResidenceAdmission = preview is
            CreateObjectTimestampDisposition.InitialGeneration
            or CreateObjectTimestampDisposition.NewGeneration;
        if (beginInitialResidence
            && requiresFreshResidenceAdmission
            && !InitialCreateResidences.CanAcceptCreate(incoming))
        {
            throw new InvalidOperationException(
                $"CreateObject 0x{incoming.Guid:X8} cannot acquire a structurally valid initial residence lease.");
        }

        RuntimeEntityRecord? pendingResidenceRecord = null;
        RuntimeInitialCreateResidenceLease pendingResidence = default;
        bool admitIntoPendingResidence = beginInitialResidence
            && preview is CreateObjectTimestampDisposition.ExistingGeneration
            && Entities.TryGetActive(
                incoming.Guid,
                out pendingResidenceRecord)
            && InitialCreateResidences.TryGetTransaction(
                pendingResidenceRecord,
                out pendingResidence);
        if (admitIntoPendingResidence
            && !InitialCreateResidences.CanEnqueue(
                pendingResidenceRecord!,
                pendingResidence))
        {
            throw new InvalidOperationException(
                $"CreateObject 0x{incoming.Guid:X8} cannot append to its pending initial residence FIFO.");
        }
        if (admitIntoPendingResidence
            && !IsStructurallyValidDeferredCreate(incoming))
        {
            _ = Entities.TryGetAcceptedTimestamps(
                incoming.Guid,
                out AcceptedPhysicsTimestamps timestamps);
            return new RuntimeEntityRegistrationResult(
                new InboundCreateResult(
                    CreateObjectTimestampDisposition.ExistingGeneration,
                    pendingResidenceRecord!.Snapshot,
                    SameGenerationEvents: null,
                    timestamps),
                pendingResidenceRecord,
                LogicalRegistrationCreated: false,
                ReplacedExistingGeneration: false);
        }

        InboundCreateResult result = admitIntoPendingResidence
            ? Entities.AcceptCreateDeferredSameGeneration(incoming)
            : Entities.AcceptCreate(incoming);
        if (result.Disposition
            is CreateObjectTimestampDisposition.StaleGeneration)
        {
            return new RuntimeEntityRegistrationResult(
                result,
                Canonical: null,
                LogicalRegistrationCreated: false,
                ReplacedExistingGeneration: false);
        }

        ulong sessionVersion = Entities.SessionLifetimeVersion;
        ulong operationVersion =
            Entities.AdvanceLifetimeMutation(incoming.Guid);

        if (result.Disposition
            is CreateObjectTimestampDisposition.ExistingGeneration)
        {
            if (Entities.TryGetActive(
                    incoming.Guid,
                    out RuntimeEntityRecord retained))
            {
                if (admitIntoPendingResidence)
                {
                    if (!ReferenceEquals(retained, pendingResidenceRecord)
                        || !AdmitSameGenerationCreate(
                            retained,
                            pendingResidence,
                            incoming,
                            result,
                            isLocalPlayer))
                    {
                        throw FailInitialResidenceRegistration(
                            retained,
                            publishDeleted: true);
                    }

                    InboundCreateResult dormant = result with
                    {
                        Snapshot = retained.Snapshot,
                        SameGenerationEvents = null,
                    };
                    return new RuntimeEntityRegistrationResult(
                        dormant,
                        retained,
                        LogicalRegistrationCreated: false,
                        ReplacedExistingGeneration: false);
                }

                Entities.RefreshSnapshot(
                    retained,
                    result.Snapshot,
                    refreshPosition: !beginInitialResidence);
                if (!beginInitialResidence)
                    Entities.AdvanceCreateAuthority(retained);
                PublishEntity(RuntimeEntityChange.Updated, retained);
                if (!IsCurrentOperation(
                        incoming.Guid,
                        retained,
                        sessionVersion,
                        operationVersion))
                {
                    return SupersededRegistration(
                        incoming.Guid,
                        replacedExistingGeneration: false);
                }
                return new RuntimeEntityRegistrationResult(
                    result,
                    retained,
                    LogicalRegistrationCreated: false,
                    ReplacedExistingGeneration: false);
            }

            if (Entities.TryGetTeardown(
                    incoming.Guid,
                    result.Snapshot.InstanceSequence,
                    out _))
            {
                return new RuntimeEntityRegistrationResult(
                    result,
                    Canonical: null,
                    LogicalRegistrationCreated: false,
                    ReplacedExistingGeneration: false);
            }

            if (beginInitialResidence
                && !InitialCreateResidences.CanAcceptCreate(result.Snapshot))
            {
                throw new InvalidOperationException(
                    $"Recovered CreateObject 0x{incoming.Guid:X8} cannot acquire a structurally valid initial residence lease.");
            }

            RuntimeEntityRecord recovered = Entities.AddActive(result.Snapshot);
            if (!InitializeAcceptedCreateResidence(
                    recovered,
                    result,
                    beginInitialResidence,
                    isLocalPlayer))
            {
                throw FailInitialResidenceRegistration(
                    recovered,
                    publishDeleted: false);
            }
            PublishEntity(RuntimeEntityChange.Registered, recovered);
            if (!IsCurrentOperation(
                    incoming.Guid,
                    recovered,
                    sessionVersion,
                    operationVersion))
            {
                return SupersededRegistration(
                    incoming.Guid,
                    replacedExistingGeneration: false);
            }
            return new RuntimeEntityRegistrationResult(
                result,
                recovered,
                LogicalRegistrationCreated: true,
                ReplacedExistingGeneration: false);
        }

        bool replaced = Entities.RemoveActive(
            incoming.Guid,
            out RuntimeEntityRecord? prior);
        if (result.Disposition
            is CreateObjectTimestampDisposition.NewGeneration)
        {
            if (prior is not null)
            {
                WithdrawCommittedChildrenToCellless(
                    prior.ServerGuid,
                    prior.Incarnation);
            }
            Entities.ParentAttachments.EndGeneration(
                incoming.Guid,
                result.Snapshot.InstanceSequence);
        }

        Exception? cleanupFailure = null;
        if (prior is not null)
        {
            // Removing the active GUID is an ownership transfer, not a gap.
            // Retain the exact incarnation before arbitrary synchronous
            // observers can re-enter registration, reset, or disposal.
            Entities.RetainTeardown(prior);
            try
            {
                PublishEntity(RuntimeEntityChange.Deleted, prior);
            }
            catch (Exception error)
            {
                cleanupFailure = error;
            }

            Exception? projectionFailure = retirePriorProjection is null
                ? RetireCanonicalOnly(prior)
                : retirePriorProjection(prior);
            cleanupFailure = Combine(cleanupFailure, projectionFailure);
        }

        if (Entities.SessionLifetimeVersion != sessionVersion
            || Entities.CurrentLifetimeMutation(incoming.Guid)
                != operationVersion)
        {
            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    $"Prior incarnation of live entity 0x{incoming.Guid:X8} failed teardown while its incoming replacement was superseded.",
                    cleanupFailure);
            }

            return new RuntimeEntityRegistrationResult(
                SupersededCreateResult(),
                Entities.TryGetActive(
                    incoming.Guid,
                    out RuntimeEntityRecord current)
                        ? current
                        : null,
                LogicalRegistrationCreated: false,
                ReplacedExistingGeneration: replaced);
        }

        RuntimeEntityRecord canonical = Entities.AddActive(result.Snapshot);
        if (!InitializeAcceptedCreateResidence(
                canonical,
                result,
                beginInitialResidence,
                isLocalPlayer))
        {
            throw FailInitialResidenceRegistration(
                canonical,
                publishDeleted: false);
        }
        try
        {
            PublishEntity(RuntimeEntityChange.Registered, canonical);
        }
        catch (Exception error)
        {
            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    $"Live entity 0x{incoming.Guid:X8} registered after prior cleanup and commit observers failed.",
                    cleanupFailure,
                    error);
            }
            throw;
        }

        if (!IsCurrentOperation(
                incoming.Guid,
                canonical,
                sessionVersion,
                operationVersion))
        {
            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    $"Prior incarnation of live entity 0x{incoming.Guid:X8} failed teardown while its committed replacement was superseded.",
                    cleanupFailure);
            }

            return SupersededRegistration(
                incoming.Guid,
                replaced);
        }

        return new RuntimeEntityRegistrationResult(
            result,
            canonical,
            LogicalRegistrationCreated: true,
            ReplacedExistingGeneration: replaced,
            cleanupFailure);
    }

    public Exception? RetireCanonicalOnly(RuntimeEntityRecord canonical)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(canonical);
        try
        {
            Entities.RetainTeardown(canonical);
            try
            {
                CompleteProjectionRetirement(canonical);
            }
            finally
            {
                Entities.ReleaseTeardown(canonical);
            }
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    internal void CompleteProjectionRetirement(
        RuntimeEntityRecord canonical)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(canonical);
        RuntimePlacementCancellationReceipt initialCancellation =
            ForgetInitialCreateResidence(canonical);
        RuntimePlacementCancellationReceipt ordinaryCancellation =
            Physics.SetPosition.Forget(
                canonical,
                releasePreparedMover: true);
        RuntimePlacementCancellationReceipt cancellation =
            PreferCancellation(initialCancellation, ordinaryCancellation);
        Physics.CollisionReports.Forget(canonical);
        Physics.RemoveSpatialProjection(canonical);
        Entities.SetRemoteMotion(canonical, null);
        Entities.SetRemoteMotionBindingInProgress(canonical, false);
        Entities.SetProjectile(canonical, null);
        Entities.SetProjectileBindingInProgress(canonical, false);
        Entities.SetRequiresRemotePlacementRuntime(canonical, false);
        Entities.SetPhysicsHost(canonical, null);
        Entities.SetPhysicsBody(canonical, null);
        Entities.SetPhysicsBodyAcquisitionInProgress(canonical, false);
        Entities.SetHasPartArray(canonical, false);
        Entities.ReleaseLocalId(canonical);
        Physics.SetPosition.PublishCancellation(cancellation);
    }

    public bool ApplyAcceptedSpawn(
        RuntimeEntityRecord canonical,
        ulong expectedCreateIntegrationVersion,
        WorldSession.EntitySpawn spawn,
        bool replaceGeneration)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(canonical);
        if (canonical.ServerGuid != spawn.Guid
            || canonical.Incarnation != spawn.InstanceSequence)
        {
            return false;
        }

        bool IsCurrent() =>
            Entities.IsCurrent(canonical)
            && canonical.CreateIntegrationVersion
                == expectedCreateIntegrationVersion;

        return IsCurrent()
            && ObjectTableWiring.ApplyEntitySpawn(
                Objects,
                spawn,
                replaceGeneration,
                IsCurrent)
            && IsCurrent();
    }

    public bool TryApplyObjDesc(
        ObjDescEvent.Parsed update,
        Action<RuntimeEntityRecord>? acknowledgeProjection,
        out WorldSession.EntitySpawn accepted)
    {
        EnsureNotDisposed();
        if (TryGetPendingInitialResidence(
                update.Guid,
                out RuntimeEntityRecord pending,
                out RuntimeInitialCreateResidenceLease lease))
        {
            if (!InitialCreateResidences.CanEnqueue(pending, lease))
            {
                accepted = pending.Snapshot;
                return false;
            }
            bool acceptedByGate = Entities.TryAcceptDeferredObjDesc(
                update,
                out _);
            accepted = pending.Snapshot;
            if (!acceptedByGate)
                return false;
            EnqueueDormant(
                pending,
                lease,
                RuntimeInitialCreateContinuationKind.ObjDesc,
                RuntimeAcceptedPositionSource.Unknown,
                new RuntimeInitialCreateTailAction(
                    RuntimeInitialCreateTailActionKind.ObjDesc,
                    update.Guid,
                    ObjDesc: update));
            return true;
        }
        bool applied = Entities.TryApplyObjDesc(update, out accepted);
        if (!applied
            || !Entities.TryGetActive(
                update.Guid,
                out RuntimeEntityRecord canonical))
        {
            return applied;
        }

        Entities.RefreshSnapshot(canonical, accepted);
        Entities.AdvanceObjDescAuthority(canonical);
        ulong authorityVersion = canonical.ObjDescAuthorityVersion;
        return AcknowledgeProjectionAndPublish(
            canonical,
            () => acknowledgeProjection?.Invoke(canonical),
            RuntimeEntityChange.Updated,
            () => canonical.ObjDescAuthorityVersion == authorityVersion);
    }

    public bool TryApplyPickup(
        PickupEvent.Parsed update,
        Action<RuntimeEntityRecord>? acknowledgeProjection,
        out WorldSession.EntitySpawn accepted)
    {
        EnsureNotDisposed();
        if (TryGetPendingInitialResidence(
                update.Guid,
                out RuntimeEntityRecord pending,
                out RuntimeInitialCreateResidenceLease lease))
        {
            if (!InitialCreateResidences.CanEnqueue(pending, lease))
            {
                accepted = pending.Snapshot;
                return false;
            }
            bool acceptedByGate = Entities.TryAcceptDeferredPickup(
                update,
                out _);
            accepted = pending.Snapshot;
            if (!acceptedByGate)
                return false;
            EnqueueDormant(
                pending,
                lease,
                RuntimeInitialCreateContinuationKind.Pickup,
                RuntimeAcceptedPositionSource.Unknown,
                new RuntimeInitialCreateTailAction(
                    RuntimeInitialCreateTailActionKind.Pickup,
                    update.Guid,
                    Pickup: update));
            return true;
        }
        bool applied = Entities.TryApplyPickup(update, out accepted);
        if (!applied
            || !Entities.TryGetActive(
                update.Guid,
                out RuntimeEntityRecord canonical))
        {
            return applied;
        }

        Entities.RefreshSnapshot(canonical, accepted);
        RuntimePlacementCancellationReceipt initialCancellation =
            ForgetInitialCreateResidence(canonical);
        Entities.AdvancePositionAuthority(canonical);
        Entities.ParentAttachments.EndChildProjection(update.Guid);
        Physics.CollisionReports.LeaveWorld(canonical);
        RuntimePlacementCancellationReceipt ordinaryCancellation =
            Physics.SetPosition.Forget(canonical);
        RuntimePlacementCancellationReceipt cancellation =
            PreferCancellation(initialCancellation, ordinaryCancellation);
        Entities.SuspendObjectClock(canonical);
        Entities.SetFullCell(canonical, 0u, 0u);
        ulong positionVersion = canonical.PositionAuthorityVersion;
        ulong spatialVersion = canonical.SpatialAuthorityVersion;
        return AcknowledgeProjectionAndPublish(
            canonical,
            () => acknowledgeProjection?.Invoke(canonical),
            RuntimeEntityChange.Withdrawn,
            () => canonical.PositionAuthorityVersion == positionVersion
                && canonical.SpatialAuthorityVersion == spatialVersion,
            cancellation);
    }

    public bool TryApplyCreateParent(
        CreateParentUpdate update,
        Action<RuntimeEntityRecord>? acknowledgeProjection,
        out WorldSession.EntitySpawn accepted)
    {
        EnsureNotDisposed();
        if (TryGetPendingInitialResidence(
                update.ChildGuid,
                out _,
                out _))
        {
            throw new InvalidOperationException(
                "A CreateObject parent relation must be admitted inside its atomic same-generation Create envelope.");
        }
        bool applied = Entities.TryApplyCreateParent(update, out accepted);
        return CommitPositionChannelUpdate(
            applied,
            update.ChildGuid,
            accepted,
            acknowledgeProjection);
    }

    public bool TryApplyParent(
        ParentEvent.Parsed update,
        Action<RuntimeEntityRecord>? acknowledgeProjection,
        out WorldSession.EntitySpawn accepted)
    {
        EnsureNotDisposed();
        if (TryGetPendingInitialResidence(
                update.ChildGuid,
                out RuntimeEntityRecord pending,
                out RuntimeInitialCreateResidenceLease lease))
        {
            if (!InitialCreateResidences.CanEnqueue(pending, lease))
            {
                accepted = pending.Snapshot;
                return false;
            }
            if (!Entities.TryGetActive(
                    update.ParentGuid,
                    out RuntimeEntityRecord parent)
                || parent.Incarnation != update.ParentInstanceSequence)
            {
                Entities.ParentAttachments.Enqueue(update);
                accepted = pending.Snapshot;
                return false;
            }

            bool acceptedByGate = Entities.TryAcceptDeferredParent(
                update,
                out AcceptedPhysicsTimestamps timestamps);
            accepted = pending.Snapshot;
            if (!acceptedByGate)
                return false;
            EnqueueDormant(
                pending,
                lease,
                RuntimeInitialCreateContinuationKind.Parent,
                RuntimeAcceptedPositionSource.Unknown,
                new RuntimeInitialCreateTailAction(
                    RuntimeInitialCreateTailActionKind.Parent,
                    update.ChildGuid,
                    Parent: update,
                    AcceptedTimestamps: timestamps));
            return true;
        }
        bool applied = Entities.TryApplyParent(update, out accepted);
        return CommitPositionChannelUpdate(
            applied,
            update.ChildGuid,
            accepted,
            acknowledgeProjection);
    }

    public bool TryCommitParent(
        ParentAttachmentRelation relation,
        Action<RuntimeEntityRecord>? acknowledgeProjection,
        out WorldSession.EntitySpawn accepted)
    {
        EnsureNotDisposed();
        bool committed = Entities.TryCommitParent(
            relation.ChildGuid,
            relation.ParentGuid,
            relation.ParentLocation,
            relation.PlacementId,
            relation.ChildPositionSequence,
            out accepted);
        if (!committed
            || !Entities.TryGetActive(
                relation.ChildGuid,
                out RuntimeEntityRecord canonical))
        {
            return committed;
        }

        Entities.RefreshSnapshot(canonical, accepted);
        RuntimePlacementCancellationReceipt initialCancellation =
            ForgetInitialCreateResidence(canonical);
        RuntimePlacementCancellationReceipt ordinaryCancellation =
            Physics.SetPosition.Forget(canonical);
        RuntimePlacementCancellationReceipt cancellation =
            PreferCancellation(initialCancellation, ordinaryCancellation);
        Entities.AdvanceParentCommit(canonical);
        ulong parentCommitVersion = canonical.ParentCommitVersion;
        return AcknowledgeProjectionAndPublish(
            canonical,
            () => acknowledgeProjection?.Invoke(canonical),
            RuntimeEntityChange.Updated,
            () => canonical.ParentCommitVersion
                == parentCommitVersion,
            cancellation);
    }

    private void WithdrawCommittedChildrenToCellless(
        uint parentGuid,
        ushort parentInstanceSequence)
    {
        IReadOnlyList<uint> children = Entities.ParentAttachments.ChildrenAttachedToParent(
            parentGuid,
            parentInstanceSequence);
        for (int i = 0; i < children.Count; i++)
        {
            if (!Entities.TryGetActive(children[i], out RuntimeEntityRecord child))
                continue;
            if (PhysicsDiagnostics.ProbeChildCellEnabled)
            {
                Console.WriteLine(FormattableString.Invariant(
                    $"[child-cell] parent=0x{parentGuid:X8} child=0x{child.ServerGuid:X8} old=0x{child.FullCellId:X8} new=0x00000000 cause=delete"));
            }
            Entities.SetFullCell(child, 0u, 0u);
        }
    }

    public bool CommitAcceptedParentCellless(
        RuntimeEntityRecord canonical,
        ulong positionAuthorityVersion,
        Action<RuntimeEntityRecord>? acknowledgeProjection)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(canonical);
        if (!Entities.IsCurrent(canonical)
            || canonical.PositionAuthorityVersion != positionAuthorityVersion)
        {
            return false;
        }

        RuntimePlacementCancellationReceipt initialCancellation =
            ForgetInitialCreateResidence(canonical);
        RuntimePlacementCancellationReceipt ordinaryCancellation =
            Physics.SetPosition.Forget(canonical);
        RuntimePlacementCancellationReceipt cancellation =
            PreferCancellation(initialCancellation, ordinaryCancellation);
        Physics.CollisionReports.LeaveWorld(canonical);
        Entities.SuspendObjectClock(canonical);
        Entities.SetFullCell(canonical, 0u, 0u);
        if (Entities.ParentAttachments.TryGetCommittedParent(
                canonical.ServerGuid,
                out uint parentGuid,
                out ushort parentInstanceSequence)
            && Entities.TryGetActive(parentGuid, out RuntimeEntityRecord parent)
            && parent.Incarnation == parentInstanceSequence
            && parent.FullCellId != 0u)
        {
            if (PhysicsDiagnostics.ProbeChildCellEnabled)
            {
                Console.WriteLine(FormattableString.Invariant(
                    $"[child-cell] parent=0x{parentGuid:X8} child=0x{canonical.ServerGuid:X8} old=0x00000000 new=0x{parent.FullCellId:X8} cause=attach"));
            }
            Entities.SetFullCell(
                canonical,
                parent.FullCellId,
                parent.CanonicalLandblockId);
        }
        ulong spatialVersion = canonical.SpatialAuthorityVersion;
        return AcknowledgeProjectionAndPublish(
            canonical,
            () => acknowledgeProjection?.Invoke(canonical),
            RuntimeEntityChange.Withdrawn,
            () => canonical.PositionAuthorityVersion
                    == positionAuthorityVersion
                && canonical.SpatialAuthorityVersion == spatialVersion,
            cancellation);
    }

    public bool TryApplyMotion(
        WorldSession.EntityMotionUpdate update,
        bool retainPayload,
        Action<RuntimeEntityRecord>? acknowledgeProjection,
        out WorldSession.EntitySpawn accepted,
        out AcceptedPhysicsTimestamps timestamps)
    {
        EnsureNotDisposed();
        if (TryGetPendingInitialResidence(
                update.Guid,
                out RuntimeEntityRecord pending,
                out RuntimeInitialCreateResidenceLease lease))
        {
            if (!InitialCreateResidences.CanEnqueue(pending, lease))
            {
                accepted = pending.Snapshot;
                timestamps = default;
                return false;
            }
            bool payloadApplied = Entities.TryAcceptDeferredMotion(
                update,
                out timestamps,
                out bool timestampMutation);
            accepted = pending.Snapshot;
            if (!payloadApplied && !timestampMutation)
                return false;
            EnqueueDormant(
                pending,
                lease,
                RuntimeInitialCreateContinuationKind.Movement,
                RuntimeAcceptedPositionSource.Unknown,
                new RuntimeInitialCreateTailAction(
                    RuntimeInitialCreateTailActionKind.Movement,
                    update.Guid,
                    Movement: update,
                    AcceptedTimestamps: timestamps,
                    AppliesMovementPayload: payloadApplied,
                    RetainMovementPayload: retainPayload,
                    HasTimestampMutation: timestampMutation));
            return payloadApplied;
        }
        bool applied = Entities.TryApplyMotion(
            update,
            retainPayload,
            out accepted,
            out timestamps);
        if (Entities.TryGetSnapshot(
                update.Guid,
                out WorldSession.EntitySpawn snapshot)
            && Entities.TryGetActive(
                update.Guid,
                out RuntimeEntityRecord canonical))
        {
            Entities.RefreshSnapshot(canonical, snapshot);
            if (applied && retainPayload)
                Entities.AdvanceMovementAuthority(canonical);
            if (applied)
            {
                Entities.AdvanceMovementCommit(canonical);
                ulong movementCommitVersion =
                    canonical.MovementCommitVersion;
                return AcknowledgeProjectionAndPublish(
                    canonical,
                    () => acknowledgeProjection?.Invoke(canonical),
                    RuntimeEntityChange.Updated,
                    () => canonical.MovementCommitVersion
                        == movementCommitVersion);
            }

            acknowledgeProjection?.Invoke(canonical);
        }

        return applied;
    }

    public bool TryApplyVector(
        VectorUpdate.Parsed update,
        Action<RuntimeEntityRecord>? acknowledgeProjection,
        out WorldSession.EntitySpawn accepted)
    {
        EnsureNotDisposed();
        if (TryGetPendingInitialResidence(
                update.Guid,
                out RuntimeEntityRecord pending,
                out RuntimeInitialCreateResidenceLease lease))
        {
            if (!IsFinite(update.Velocity)
                || !IsFinite(update.Omega)
                || !InitialCreateResidences.CanEnqueue(pending, lease))
            {
                accepted = pending.Snapshot;
                return false;
            }
            bool acceptedByGate = Entities.TryAcceptDeferredVector(
                update,
                out _);
            accepted = pending.Snapshot;
            if (!acceptedByGate)
                return false;
            EnqueueDormant(
                pending,
                lease,
                RuntimeInitialCreateContinuationKind.Vector,
                RuntimeAcceptedPositionSource.Unknown,
                new RuntimeInitialCreateTailAction(
                    RuntimeInitialCreateTailActionKind.Vector,
                    update.Guid,
                    Vector: update));
            return true;
        }
        bool applied = Entities.TryApplyVector(update, out accepted);
        if (!applied
            || !Entities.TryGetActive(
                update.Guid,
                out RuntimeEntityRecord canonical))
        {
            return applied;
        }

        Entities.RefreshSnapshot(canonical, accepted);
        Entities.AdvanceVectorAuthority(canonical);
        ulong authorityVersion = canonical.VectorAuthorityVersion;
        return AcknowledgeProjectionAndPublish(
            canonical,
            () => acknowledgeProjection?.Invoke(canonical),
            RuntimeEntityChange.Updated,
            () => canonical.VectorAuthorityVersion == authorityVersion);
    }

    public bool TryApplyState(
        SetState.Parsed update,
        Action<RuntimeEntityRecord, RetailPhysicsStateTransition>?
            acknowledgeProjection,
        out WorldSession.EntitySpawn accepted,
        out RetailPhysicsStateTransition transition)
    {
        EnsureNotDisposed();
        if (TryGetPendingInitialResidence(
                update.Guid,
                out RuntimeEntityRecord pending,
                out RuntimeInitialCreateResidenceLease lease))
        {
            if (!InitialCreateResidences.CanEnqueue(pending, lease))
            {
                accepted = pending.Snapshot;
                transition = default;
                return false;
            }
            bool acceptedByGate = Entities.TryAcceptDeferredState(
                update,
                out _);
            accepted = pending.Snapshot;
            transition = default;
            if (!acceptedByGate)
                return false;
            EnqueueDormant(
                pending,
                lease,
                RuntimeInitialCreateContinuationKind.State,
                RuntimeAcceptedPositionSource.Unknown,
                new RuntimeInitialCreateTailAction(
                    RuntimeInitialCreateTailActionKind.State,
                    update.Guid,
                    State: update));
            return true;
        }
        bool applied = Entities.TryApplyState(update, out accepted);
        transition = default;
        if (!applied
            || !Entities.TryGetActive(
                update.Guid,
                out RuntimeEntityRecord canonical))
        {
            return applied;
        }

        Entities.RefreshSnapshot(canonical, accepted);
        RetailPhysicsStateTransition preview =
            RetailPhysicsStateTransitions.Apply(
                canonical.FinalPhysicsState,
                (PhysicsStateFlags)update.PhysicsState);
        ulong priorPhysicsMutation = canonical.PhysicsStateMutationVersion;
        if (preview.HiddenTransition is RetailHiddenTransition.BecameHidden)
        {
            Physics.CollisionReports.LeaveWorld(canonical);
            if (!Entities.IsCurrent(canonical)
                || canonical.PhysicsStateMutationVersion
                    != priorPhysicsMutation)
            {
                transition = default;
                return false;
            }
        }
        transition = Entities.ApplyRawPhysicsState(
            canonical,
            update.PhysicsState);
        if (canonical.Key is { } key)
        {
            Physics.Engine.ShadowObjects.UpdatePhysicsState(
                key.LocalEntityId,
                (uint)canonical.FinalPhysicsState);
        }
        ulong stateVersion = canonical.StateAuthorityVersion;
        ulong physicsMutationVersion =
            canonical.PhysicsStateMutationVersion;
        RetailPhysicsStateTransition committedTransition = transition;
        return AcknowledgeProjectionAndPublish(
            canonical,
            () => acknowledgeProjection?.Invoke(
                canonical,
                committedTransition),
            committedTransition.HiddenTransition
                is RetailHiddenTransition.BecameHidden
                    ? RuntimeEntityChange.Hidden
                    : RuntimeEntityChange.Updated,
            () => canonical.StateAuthorityVersion == stateVersion
                && canonical.PhysicsStateMutationVersion
                    == physicsMutationVersion);
    }

    public bool TryApplyPosition(
        WorldSession.EntityPositionUpdate update,
        bool isLocalPlayer,
        System.Numerics.Quaternion? forcePositionRotation,
        System.Numerics.Vector3? currentLocalVelocity,
        Action<RuntimeEntityRecord>? acknowledgeProjection,
        out PositionTimestampDisposition disposition,
        out WorldSession.EntitySpawn accepted,
        out AcceptedPhysicsTimestamps timestamps)
    {
        EnsureNotDisposed();
        if (TryGetPendingInitialResidence(
                update.Guid,
                out RuntimeEntityRecord pendingCanonical,
                out RuntimeInitialCreateResidenceLease pendingLease))
        {
            if (!RuntimeAuthoritativePositionRouteClassifier
                    .IsValidCreateWirePosition(update.Position)
                || update.Velocity is { } velocity
                    && !IsFinite(velocity)
                || !InitialCreateResidences.CanEnqueue(
                    pendingCanonical,
                    pendingLease))
            {
                disposition = PositionTimestampDisposition.Rejected;
                accepted = default;
                timestamps = default;
                return false;
            }

            bool deferredKnown = Entities.TryAcceptDeferredPosition(
                update,
                isLocalPlayer,
                out disposition,
                out timestamps,
                out bool timestampMutation);
            accepted = pendingCanonical.Snapshot;
            if (!deferredKnown)
                return false;

            if (isLocalPlayer
                && disposition is not PositionTimestampDisposition.Rejected)
            {
                Physics.ObserveLocalWorldFrame(
                    update.Position.LandblockId,
                    timestamps.TeleportAdvanced);
            }

            if (disposition is PositionTimestampDisposition.Rejected
                && !timestampMutation)
            {
                return true;
            }

            EnqueueDormant(
                pendingCanonical,
                pendingLease,
                RuntimeInitialCreateContinuationKind.Position,
                RuntimeAcceptedPositionSource.PositionEvent,
                new RuntimeInitialCreateTailAction(
                    RuntimeInitialCreateTailActionKind.Position,
                    update.Guid,
                    Position: update,
                    PositionSource:
                        RuntimeAcceptedPositionSource.PositionEvent,
                    PositionDisposition: disposition,
                    PreviousTeleportSequence:
                        timestamps.PreviousTeleport,
                    AcceptedTimestamps: timestamps,
                    HasTimestampMutation: timestampMutation));
            return true;
        }
        bool hadCanonical = Entities.TryGetActive(
            update.Guid,
            out RuntimeEntityRecord beforeCanonical);
        uint beforeCell = beforeCanonical?.FullCellId ?? 0u;
        bool known = Entities.TryApplyPosition(
            update,
            isLocalPlayer,
            forcePositionRotation,
            currentLocalVelocity,
            out disposition,
            out accepted,
            out timestamps);
        if (!known
            || !Entities.TryGetSnapshot(
                update.Guid,
                out WorldSession.EntitySpawn snapshot)
            || !Entities.TryGetActive(
                update.Guid,
                out RuntimeEntityRecord canonical))
        {
            return known;
        }

        if (isLocalPlayer
            && disposition is not PositionTimestampDisposition.Rejected)
        {
            Physics.ObserveLocalWorldFrame(
                update.Position.LandblockId,
                timestamps.TeleportAdvanced);
        }

        bool acceptedPosition =
            disposition is not PositionTimestampDisposition.Rejected;
        timestamps = timestamps with
        {
            PreMergeCommittedCellId = hadCanonical ? beforeCell : null,
        };
        RuntimePlacementCancellationReceipt cancellation = default;
        if (acceptedPosition)
        {
            cancellation = Physics.SetPosition.Forget(
                canonical,
                restoreCancelledPark: true);
        }
        Entities.RefreshSnapshot(
            canonical,
            snapshot,
            refreshPosition: false);
        if (acceptedPosition
            && ReferenceEquals(canonical, beforeCanonical))
        {
            Entities.AdvancePositionAuthority(canonical);
            Entities.ParentAttachments.EndChildProjection(update.Guid);
        }

        ulong positionVersion = canonical.PositionAuthorityVersion;
        ulong spatialVersion = canonical.SpatialAuthorityVersion;
        if (!acceptedPosition)
        {
            acknowledgeProjection?.Invoke(canonical);
            return IsExpectedCanonical(
                canonical,
                () => canonical.PositionAuthorityVersion == positionVersion
                    && canonical.SpatialAuthorityVersion == spatialVersion);
        }

        return AcknowledgeProjectionAndPublish(
            canonical,
            () => acknowledgeProjection?.Invoke(canonical),
            beforeCell != canonical.FullCellId
                ? RuntimeEntityChange.Rebucketed
                : RuntimeEntityChange.Updated,
            () => canonical.PositionAuthorityVersion == positionVersion
                && canonical.SpatialAuthorityVersion == spatialVersion,
            cancellation);
    }

    public bool CommitRebucket(
        RuntimeEntityRecord canonical,
        uint fullCellId,
        uint canonicalLandblockId,
        Action<RuntimeEntityRecord>? acknowledgeProjection = null)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(canonical);
        if (!Entities.IsCurrent(canonical))
            return false;

        uint previous = canonical.FullCellId;
        Entities.SetFullCell(
            canonical,
            fullCellId,
            canonicalLandblockId);
        ulong spatialVersion = canonical.SpatialAuthorityVersion;
        if (previous == fullCellId)
        {
            acknowledgeProjection?.Invoke(canonical);
            return IsExpectedCanonical(
                canonical,
                () => canonical.SpatialAuthorityVersion
                    == spatialVersion);
        }

        return AcknowledgeProjectionAndPublish(
            canonical,
            () => acknowledgeProjection?.Invoke(canonical),
            RuntimeEntityChange.Rebucketed,
            () => canonical.SpatialAuthorityVersion == spatialVersion);
    }

    public bool CommitWireCellRebucket(
        RuntimeEntityRecord canonical,
        uint spatialCellOrLandblockId,
        Action<RuntimeEntityRecord>? acknowledgeProjection = null)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        uint committedFullCell =
            (spatialCellOrLandblockId & 0xFFFFu) != 0xFFFFu
                ? spatialCellOrLandblockId
                : canonical.FullCellId;
        uint committedLandblock = spatialCellOrLandblockId == 0
            ? 0u
            : (spatialCellOrLandblockId & 0xFFFF0000u) | 0xFFFFu;
        return CommitRebucket(
            canonical,
            committedFullCell,
            committedLandblock,
            acknowledgeProjection);
    }

    public bool CommitWithdrawal(
        RuntimeEntityRecord canonical,
        Action<RuntimeEntityRecord>? acknowledgeProjection = null)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(canonical);
        if (!Entities.IsCurrent(canonical))
            return false;

        RuntimePlacementCancellationReceipt initialCancellation =
            ForgetInitialCreateResidence(canonical);
        Physics.CollisionReports.LeaveWorld(canonical);
        RuntimePlacementCancellationReceipt ordinaryCancellation =
            Physics.SetPosition.Forget(canonical);
        RuntimePlacementCancellationReceipt cancellation =
            PreferCancellation(initialCancellation, ordinaryCancellation);
        Entities.SuspendObjectClock(canonical);
        Entities.SetFullCell(canonical, 0u, 0u);
        ulong spatialVersion = canonical.SpatialAuthorityVersion;
        return AcknowledgeProjectionAndPublish(
            canonical,
            () => acknowledgeProjection?.Invoke(canonical),
            RuntimeEntityChange.Withdrawn,
            () => canonical.SpatialAuthorityVersion == spatialVersion,
            cancellation);
    }

    public bool CommitChildNoDraw(
        RuntimeEntityRecord canonical,
        bool noDraw,
        Action<RuntimeEntityRecord>? acknowledgeProjection = null)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(canonical);
        if (!Entities.IsCurrent(canonical))
            return false;

        Entities.SetChildNoDraw(canonical, noDraw);
        ulong physicsMutationVersion =
            canonical.PhysicsStateMutationVersion;
        return AcknowledgeProjectionAndPublish(
            canonical,
            () => acknowledgeProjection?.Invoke(canonical),
            RuntimeEntityChange.Updated,
            () => canonical.PhysicsStateMutationVersion
                == physicsMutationVersion);
    }

    public bool RetireAfterProjectionAcquisitionFailure(
        RuntimeEntityRecord canonical)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(canonical);
        if (!Entities.RemoveActive(canonical))
            return false;

        Entities.AdvanceLifetimeMutation(canonical.ServerGuid);
        Entities.RetainTeardown(canonical);
        PublishEntity(RuntimeEntityChange.Deleted, canonical);
        return true;
    }

    public bool TryAcceptDelete(
        DeleteObject.Parsed delete,
        bool isLocalPlayer,
        bool removeRetainedObject,
        out RuntimeEntityDeleteAcceptance acceptance)
    {
        EnsureNotDisposed();
        if (!isLocalPlayer)
        {
            Entities.ParentAttachments.CancelDeferredChildGeneration(
                delete.Guid,
                delete.InstanceSequence);
        }
        if (!Entities.TryDelete(delete, isLocalPlayer))
        {
            acceptance = null!;
            return false;
        }

        Entities.AdvanceLifetimeMutation(delete.Guid);
        WithdrawCommittedChildrenToCellless(delete.Guid, delete.InstanceSequence);
        Entities.ParentAttachments.DeleteGeneration(
            delete.Guid,
            delete.InstanceSequence);

        RuntimeEntityRecord? retiredCanonical = null;
        if (Entities.TryGetActive(
                delete.Guid,
                out RuntimeEntityRecord active)
            && active.Incarnation == delete.InstanceSequence
            && Entities.RemoveActive(active))
        {
            RuntimePlacementCancellationReceipt initialCancellation =
                ForgetInitialCreateResidence(active);
            Physics.CollisionReports.Forget(active);
            RuntimePlacementCancellationReceipt ordinaryCancellation =
                Physics.SetPosition.Forget(
                    active,
                    releasePreparedMover: true);
            RuntimePlacementCancellationReceipt cancellation =
                PreferCancellation(
                    initialCancellation,
                    ordinaryCancellation);
            retiredCanonical = active;
            Entities.RetainTeardown(active);
            Physics.SetPosition.PublishCancellation(cancellation);
            PublishEntity(
                removeRetainedObject
                    ? RuntimeEntityChange.Deleted
                    : RuntimeEntityChange.Withdrawn,
                active);
        }

        acceptance = new RuntimeEntityDeleteAcceptance(
            this,
            delete,
            retiredCanonical,
            removeRetainedObject);
        return true;
    }

    public void CompleteAcceptedDelete(
        RuntimeEntityDeleteAcceptance acceptance)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(acceptance);
        if (!ReferenceEquals(acceptance.Owner, this))
        {
            throw new InvalidOperationException(
                "A delete acceptance belongs to a different Runtime lifetime.");
        }
        if (acceptance.Completed)
        {
            throw new InvalidOperationException(
                "A delete acceptance has already been completed.");
        }

        acceptance.Completed = true;
        if (acceptance.RemoveRetainedObject)
            ObjectTableWiring.ApplyEntityDelete(Objects, acceptance.Delete);
    }

    public void ApplyAcceptedDormantDelete(DeleteObject.Parsed delete)
    {
        EnsureNotDisposed();
        ObjectTableWiring.ApplyEntityDelete(Objects, delete);
    }

    public void ClearObjects()
    {
        EnsureNotDisposed();
        Objects.Clear();
    }

    public IReadOnlyList<RuntimeEntityRecord> BeginSessionClear()
    {
        EnsureNotDisposed();
        if (_sessionClearInProgress)
            return Array.Empty<RuntimeEntityRecord>();

        _sessionClearInProgress = true;
        RuntimeEntityRecord[] active = Entities.ActiveRecords.ToArray();
        InitialCreateResidences.Clear();
        InitialCreateExecution.DiscardAll();
        LocalPlayerFirstEntry.DiscardAll();
        RemoteFirstEntry.DiscardAll();
        Physics.CollisionReports.LeaveWorldBatch(active);
        Physics.ResetSessionPhysics();
        Entities.BeginSessionClear();
        foreach (RuntimeEntityRecord canonical in active)
        {
            Physics.SetPosition.Forget(canonical);
            if (!Entities.RemoveActive(canonical))
                continue;
            Entities.RetainTeardown(canonical);
            PublishEntity(RuntimeEntityChange.Deleted, canonical);
        }
        return active;
    }

    public IReadOnlyList<RuntimeEntityRecord>
        CaptureSessionClearRetirements()
    {
        EnsureNotDisposed();
        if (!_sessionClearInProgress)
        {
            throw new InvalidOperationException(
                "Session-clear retirements are only available while the "
                + "canonical clear transaction is active.");
        }
        return Entities.TeardownRecords.ToArray();
    }

    public void CompleteSessionEntityRetirement(
        RuntimeEntityRecord canonical)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(canonical);
        if (!_sessionClearInProgress
            || !Entities.TryGetTeardown(
                canonical.ServerGuid,
                canonical.Incarnation,
                out RuntimeEntityRecord retained)
            || !ReferenceEquals(retained, canonical))
        {
            throw new InvalidOperationException(
                $"Live entity 0x{canonical.ServerGuid:X8}/{canonical.Incarnation} is not retained by the active session-clear transaction.");
        }

        CompleteProjectionRetirement(canonical);
        Entities.ReleaseTeardown(canonical);
    }

    public bool CompleteSessionClearIfConverged()
    {
        EnsureNotDisposed();
        if (!_sessionClearInProgress
            || !Entities.CompleteSessionClearIfConverged())
        {
            return false;
        }

        _sessionClearInProgress = false;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Events.DetachObservers();
        List<Exception>? failures = null;
        try
        {
            if (!_sessionClearInProgress)
                _ = BeginSessionClear();

            try
            {
                ClearObjects();
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }

            foreach (RuntimeEntityRecord canonical
                in Entities.TeardownRecords.ToArray())
            {
                Exception? failure = RetireCanonicalOnly(canonical);
                if (failure is not null)
                    (failures ??= []).Add(failure);
            }

            if (!CompleteSessionClearIfConverged())
            {
                (failures ??= []).Add(new InvalidOperationException(
                    "Runtime entity/object disposal did not converge every canonical owner."));
            }
        }
        finally
        {
            _disposed = true;
            _generation = null;
            _pvpBitfieldSync.Dispose();
            Events.Dispose();
            Physics.Dispose();
        }

        if (failures is not null)
        {
            throw new AggregateException(
                "Runtime entity/object disposal failed to converge.",
                failures);
        }
    }

    private bool CommitPositionChannelUpdate(
        bool applied,
        uint guid,
        WorldSession.EntitySpawn accepted,
        Action<RuntimeEntityRecord>? acknowledgeProjection)
    {
        if (!applied
            || !Entities.TryGetActive(
                guid,
                out RuntimeEntityRecord canonical))
        {
            return applied;
        }

        Entities.RefreshSnapshot(canonical, accepted);
        RuntimePlacementCancellationReceipt initialCancellation =
            ForgetInitialCreateResidence(canonical);
        Entities.AdvancePositionAuthority(canonical);
        Physics.CollisionReports.LeaveWorld(canonical);
        RuntimePlacementCancellationReceipt ordinaryCancellation =
            Physics.SetPosition.Forget(canonical);
        RuntimePlacementCancellationReceipt cancellation =
            PreferCancellation(initialCancellation, ordinaryCancellation);
        ulong positionVersion = canonical.PositionAuthorityVersion;
        ulong spatialVersion = canonical.SpatialAuthorityVersion;
        return AcknowledgeProjectionAndPublish(
            canonical,
            () => acknowledgeProjection?.Invoke(canonical),
            RuntimeEntityChange.Updated,
            () => canonical.PositionAuthorityVersion == positionVersion
                && canonical.SpatialAuthorityVersion == spatialVersion,
            cancellation);
    }

    private void PublishEntity(
        RuntimeEntityChange change,
        RuntimeEntityRecord canonical) =>
        Events.PublishEntity(change, canonical);

    private bool AcknowledgeProjectionAndPublish(
        RuntimeEntityRecord canonical,
        Action acknowledgeProjection,
        RuntimeEntityChange change,
        Func<bool> matchesCommittedMutation,
        RuntimePlacementCancellationReceipt cancellation = default)
    {
        Physics.SetPosition.PublishCancellation(cancellation);
        if (!IsExpectedCanonical(canonical, matchesCommittedMutation))
            return false;
        try
        {
            acknowledgeProjection();
        }
        finally
        {
            if (IsExpectedCanonical(
                    canonical,
                    matchesCommittedMutation))
            {
                PublishEntity(change, canonical);
            }
        }

        return IsExpectedCanonical(
            canonical,
            matchesCommittedMutation);
    }

    private bool IsExpectedCanonical(
        RuntimeEntityRecord canonical,
        Func<bool> matchesCommittedMutation) =>
        Entities.IsCurrent(canonical)
        && matchesCommittedMutation();

    private bool TryGetPendingInitialResidence(
        uint guid,
        out RuntimeEntityRecord canonical,
        out RuntimeInitialCreateResidenceLease lease)
    {
        if (Entities.TryGetActive(guid, out canonical)
            && InitialCreateResidences.TryGetTransaction(
                canonical,
                out lease))
        {
            return true;
        }

        canonical = null!;
        lease = default;
        return false;
    }

    private void EnqueueDormant(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceLease prior,
        RuntimeInitialCreateContinuationKind kind,
        RuntimeAcceptedPositionSource positionSource,
        in RuntimeInitialCreateTailAction action)
    {
        RuntimeInitialCreateResidenceLease retained = InitialCreateResidences
            .EnqueueAccepted(
                canonical,
                prior,
                kind,
                positionSource,
                ImmutableArray.Create(action));
        if (!retained.IsValid)
        {
            throw new InvalidOperationException(
                $"Accepted {kind} for 0x{canonical.ServerGuid:X8}/{canonical.Incarnation} could not be retained by its initial-placement FIFO.");
        }
    }

    private static bool IsFinite(System.Numerics.Vector3 value) =>
        float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z);

    private static bool HasConsistentCreateIdentityAndParent(
        in WorldSession.EntitySpawn incoming)
    {
        if (incoming.Guid == 0u)
            return false;

        bool hasTopParentGuid = incoming.ParentGuid is not null;
        bool hasTopParentLocation = incoming.ParentLocation is not null;
        if (hasTopParentGuid != hasTopParentLocation)
            return false;

        if (incoming.Physics is not { } physics)
        {
            return incoming.Position is null
                && incoming.SetupTableId is null
                && incoming.MotionState is null
                && incoming.MotionTableId is null
                && incoming.PhysicsState is null
                && incoming.ObjScale is null
                && incoming.Friction is null
                && incoming.Elasticity is null
                && incoming.InstanceSequence == 0
                && incoming.MovementSequence == 0
                && incoming.ServerControlSequence == 0
                && incoming.PositionSequence == 0
                && !hasTopParentGuid
                && incoming.PlacementId is null;
        }
        if (physics.Timestamps.Instance != incoming.InstanceSequence
            || physics.Timestamps.Position != incoming.PositionSequence
            || physics.Timestamps.Movement != incoming.MovementSequence
            || physics.Timestamps.ServerControlledMove
                != incoming.ServerControlSequence
            || physics.Position != incoming.Position)
            return false;

        PhysicsAttachment? flattenedParent = incoming.ParentGuid is { } parentGuid
            && incoming.ParentLocation is { } parentLocation
                ? new PhysicsAttachment(parentGuid, parentLocation)
                : null;
        if (flattenedParent != physics.Parent
            || incoming.PlacementId != physics.AnimationFrame)
        {
            return false;
        }
        return true;
    }

    private static bool IsStructurallyValidDeferredCreate(
        in WorldSession.EntitySpawn incoming)
    {
        if (incoming.Guid == 0u)
            return false;
        if (incoming.Physics is not { } physics)
            return true;
        if (physics.Parent is null
            && physics.Position is { LandblockId: not 0u } position
            && !RuntimeAuthoritativePositionRouteClassifier
                .IsValidCreateWirePosition(position))
        {
            return false;
        }
        if (physics.Velocity is { } velocity && !IsFinite(velocity)
            || physics.Acceleration is { } acceleration
                && !IsFinite(acceleration)
            || physics.AngularVelocity is { } angularVelocity
                && !IsFinite(angularVelocity)
            || physics.Scale is { } scale && !float.IsFinite(scale)
            || physics.Friction is { } friction && !float.IsFinite(friction)
            || physics.Elasticity is { } elasticity
                && !float.IsFinite(elasticity)
            || physics.Translucency is { } translucency
                && !float.IsFinite(translucency))
        {
            return false;
        }
        return true;
    }

    private bool AdmitSameGenerationCreate(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceLease prior,
        in WorldSession.EntitySpawn incoming,
        in InboundCreateResult admitted,
        bool isLocalPlayer)
    {
        if (!ReferenceEquals(
                canonical,
                Entities.TryGetActive(
                    incoming.Guid,
                    out RuntimeEntityRecord current)
                        ? current
                        : null)
            || canonical.Incarnation != incoming.InstanceSequence
            || admitted.Disposition
                is not CreateObjectTimestampDisposition.ExistingGeneration
            || !InitialCreateResidences.CanEnqueue(canonical, prior))
        {
            return false;
        }

        var actions = ImmutableArray.CreateBuilder<
            RuntimeInitialCreateTailAction>();
        RuntimeAcceptedPositionSource positionSource =
            RuntimeAcceptedPositionSource.Unknown;

        if (admitted.SameGenerationEvents is { } events)
        {
            actions.Add(new RuntimeInitialCreateTailAction(
                RuntimeInitialCreateTailActionKind
                    .PreTailDescriptionAdaptation,
                incoming.Guid,
                Description: events.Description));

            if (Entities.TryAcceptDeferredObjDesc(
                    events.Appearance,
                    out _))
            {
                actions.Add(new RuntimeInitialCreateTailAction(
                    RuntimeInitialCreateTailActionKind.ObjDesc,
                    incoming.Guid,
                    ObjDesc: events.Appearance));
            }

            if (events.Parent is { } parent)
            {
                if (Entities.TryAcceptDeferredCreateParent(
                        parent,
                        out _))
                {
                    actions.Add(new RuntimeInitialCreateTailAction(
                        RuntimeInitialCreateTailActionKind.CreateParent,
                        incoming.Guid,
                        CreateParent: parent));
                }
            }
            else if (events.Position is { } position)
            {
                if (!RuntimeAuthoritativePositionRouteClassifier
                        .IsValidCreateWirePosition(position.Position)
                    || position.Velocity is { } velocity
                        && !IsFinite(velocity))
                {
                    return false;
                }

                if (!Entities.TryAcceptDeferredPosition(
                        position,
                        isLocalPlayer,
                        out PositionTimestampDisposition disposition,
                        out AcceptedPhysicsTimestamps timestamps,
                        out bool timestampMutation))
                {
                    return false;
                }
                if (disposition is not PositionTimestampDisposition.Rejected
                    || timestampMutation)
                {
                    positionSource = RuntimeAcceptedPositionSource
                        .SameIncarnationCreate;
                    actions.Add(new RuntimeInitialCreateTailAction(
                        RuntimeInitialCreateTailActionKind.Position,
                        incoming.Guid,
                        Position: position,
                        PositionSource: positionSource,
                        PositionDisposition: disposition,
                        PreviousTeleportSequence:
                            timestamps.PreviousTeleport,
                        AcceptedTimestamps: timestamps,
                        HasTimestampMutation: timestampMutation));
                }
            }
            else if (events.Pickup is { } pickup
                && Entities.TryAcceptDeferredPickup(pickup, out _))
            {
                actions.Add(new RuntimeInitialCreateTailAction(
                    RuntimeInitialCreateTailActionKind.Pickup,
                    incoming.Guid,
                    Pickup: pickup));
            }

            if (events.Movement is { } movement)
            {
                bool payloadApplied = Entities.TryAcceptDeferredMotion(
                    movement,
                    out AcceptedPhysicsTimestamps timestamps,
                    out bool timestampMutation);
                if (payloadApplied || timestampMutation)
                {
                    actions.Add(new RuntimeInitialCreateTailAction(
                        RuntimeInitialCreateTailActionKind.Movement,
                        incoming.Guid,
                        Movement: movement,
                        AcceptedTimestamps: timestamps,
                        AppliesMovementPayload: payloadApplied,
                        RetainMovementPayload: true,
                        HasTimestampMutation: timestampMutation));
                }
            }

            if (Entities.TryAcceptDeferredState(events.State, out _))
            {
                actions.Add(new RuntimeInitialCreateTailAction(
                    RuntimeInitialCreateTailActionKind.State,
                    incoming.Guid,
                    State: events.State));
            }
            if (Entities.TryAcceptDeferredVector(events.Vector, out _))
            {
                actions.Add(new RuntimeInitialCreateTailAction(
                    RuntimeInitialCreateTailActionKind.Vector,
                    incoming.Guid,
                    Vector: events.Vector));
            }
        }

        actions.Add(new RuntimeInitialCreateTailAction(
            RuntimeInitialCreateTailActionKind.WeenieDescription,
            incoming.Guid,
            WeenieDescription: incoming));
        actions.Add(new RuntimeInitialCreateTailAction(
            RuntimeInitialCreateTailActionKind.ResidentCellCleanup,
            incoming.Guid));

        RuntimeInitialCreateResidenceLease retained = InitialCreateResidences
            .EnqueueAccepted(
                canonical,
                prior,
                RuntimeInitialCreateContinuationKind.SameIncarnationCreate,
                positionSource,
                actions.ToImmutable());
        return retained.IsValid;
    }

    private void EnsureNotDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    internal bool TryGetInitialCreateResidence(
        RuntimeEntityRecord canonical,
        out RuntimeInitialCreateResidenceLease lease)
    {
        EnsureNotDisposed();
        return InitialCreateResidences.TryGetCurrent(canonical, out lease);
    }

    public bool TryConvertInitialResidenceToCellessRoute(
        RuntimeEntityRecord canonical)
    {
        EnsureNotDisposed();
        return InitialCreateResidences.TryConvertToCellessRoute(canonical);
    }

    internal RuntimeInitialCreateResidenceCompletionStatus
        CompleteInitialCreateResidence(
            RuntimeEntityRecord canonical,
            in RuntimeInitialCreateResidenceToken token,
            out RuntimeInitialCreateResidenceReceipt receipt)
    {
        EnsureNotDisposed();
        return InitialCreateResidences.Complete(
            canonical,
            token,
            out receipt);
    }

    internal bool AcknowledgeInitialCreateResidenceAdoption(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceAdoptionToken token)
    {
        EnsureNotDisposed();
        return InitialCreateResidences.AcknowledgeAdoption(
            canonical,
            token);
    }

    private RuntimePlacementCancellationReceipt ForgetInitialCreateResidence(
        RuntimeEntityRecord canonical)
    {
        bool forgotten = InitialCreateResidences.Forget(
            canonical,
            out _,
            out RuntimePlacementCancellationReceipt cancellation);
        if (canonical.Key is { } key)
            InitialCreateExecution.DiscardProgress(key);
        return forgotten ? cancellation : default;
    }

    private static RuntimePlacementCancellationReceipt PreferCancellation(
        in RuntimePlacementCancellationReceipt initial,
        in RuntimePlacementCancellationReceipt ordinary) =>
        initial.IsValid ? initial : ordinary;

    private bool InitializeAcceptedCreateResidence(
        RuntimeEntityRecord canonical,
        in InboundCreateResult accepted,
        bool beginInitialResidence,
        bool isLocalPlayer)
    {
        if (!beginInitialResidence)
            return true;

        if (canonical.PositionAuthorityVersion == 0UL)
            Entities.AdvancePositionAuthority(canonical);
        if (canonical.FullCellId != 0u)
            Entities.SetFullCell(canonical, 0u, 0u);
        RuntimeInitialCreateResidenceLease lease =
            InitialCreateResidences.Begin(
                canonical,
                accepted,
                isLocalPlayer);
        if (!lease.IsValid)
            return false;
        _initialResidenceBegan?.Invoke(canonical);
        return true;
    }

    private Exception FailInitialResidenceRegistration(
        RuntimeEntityRecord canonical,
        bool publishDeleted)
    {
        if (!Entities.RemoveActive(canonical))
        {
            return new InvalidOperationException(
                $"Initial residence for 0x{canonical.ServerGuid:X8} failed after its canonical incarnation was superseded.");
        }

        Exception? failure = null;
        if (publishDeleted)
        {
            try
            {
                PublishEntity(RuntimeEntityChange.Deleted, canonical);
            }
            catch (Exception error)
            {
                failure = error;
            }
        }
        failure = Combine(failure, RetireCanonicalOnly(canonical));
        var cause = new InvalidOperationException(
            $"Initial residence for 0x{canonical.ServerGuid:X8} could not acquire its exact Runtime placement lease.");
        return failure is null
            ? cause
            : new AggregateException(cause, failure);
    }

    private static Exception? Combine(
        Exception? first,
        Exception? second) =>
        first is null
            ? second
            : second is null
                ? first
                : new AggregateException(first, second);

    private static InboundCreateResult SupersededCreateResult() => new(
        CreateObjectTimestampDisposition.StaleGeneration,
        default,
        null,
        default);

    private bool IsCurrentOperation(
        uint guid,
        RuntimeEntityRecord canonical,
        ulong sessionVersion,
        ulong operationVersion) =>
        Entities.SessionLifetimeVersion == sessionVersion
        && Entities.CurrentLifetimeMutation(guid) == operationVersion
        && Entities.IsCurrent(canonical);

    private RuntimeEntityRegistrationResult SupersededRegistration(
        uint guid,
        bool replacedExistingGeneration) =>
        new(
            SupersededCreateResult(),
            Entities.TryGetActive(guid, out RuntimeEntityRecord current)
                ? current
                : null,
            LogicalRegistrationCreated: false,
            ReplacedExistingGeneration: replacedExistingGeneration);
}
