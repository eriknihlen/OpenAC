using AcDream.App.Streaming;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using System.Numerics;
using System.Runtime.ExceptionServices;

namespace AcDream.App.World;

public interface ILiveEntityAnimationRuntime
{
    WorldEntity Entity { get; }
    uint CurrentMotion { get; }
}

/// <summary>Marker for the DAT-driven effect profile added by the effect phase.</summary>
public interface ILiveEntityEffectProfile { }

public enum LiveEntityProjectionKind
{
    World,
    Attached,
}

internal enum LiveEntityMaterializationResidence
{
    LegacyImmediate,
    AwaitRuntimePlacement,
}

internal enum EquippedChildPresentationRebucketDisposition
{
    Moved,
    NotAttached,
    NoProjection,
    Displaced,
}

public interface ILiveEntityResourceLifecycle
{
    void Register(WorldEntity entity);
    void Unregister(WorldEntity entity);
}

internal interface ILiveEntityRuntimeComponentLifecycle
{
    void TearDown(LiveEntityRecord record);
}

internal sealed class NullLiveEntityRuntimeComponentLifecycle :
    ILiveEntityRuntimeComponentLifecycle
{
    public static NullLiveEntityRuntimeComponentLifecycle Instance { get; } = new();
    private NullLiveEntityRuntimeComponentLifecycle() { }
    public void TearDown(LiveEntityRecord record) { }
}

internal sealed class DelegateLiveEntityRuntimeComponentLifecycle(
    Action<LiveEntityRecord> tearDown) : ILiveEntityRuntimeComponentLifecycle
{
    private readonly Action<LiveEntityRecord> _tearDown =
        tearDown ?? throw new ArgumentNullException(nameof(tearDown));

    public void TearDown(LiveEntityRecord record) => _tearDown(record);
}

internal sealed class DeferredLiveEntityRuntimeComponentLifecycle :
    ILiveEntityRuntimeComponentLifecycle
{
    private ILiveEntityRuntimeComponentLifecycle? _inner;

    public bool IsBound => Volatile.Read(ref _inner) is not null;

    public void Bind(ILiveEntityRuntimeComponentLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        if (ReferenceEquals(lifecycle, this))
            throw new ArgumentException("A lifecycle bridge cannot bind to itself.", nameof(lifecycle));
        if (Interlocked.CompareExchange(ref _inner, lifecycle, null) is not null)
            throw new InvalidOperationException("The live-entity runtime component lifecycle is already bound.");
    }

    public IDisposable BindOwned(ILiveEntityRuntimeComponentLifecycle lifecycle)
    {
        Bind(lifecycle);
        return new Binding(this, lifecycle);
    }

    private void Unbind(ILiveEntityRuntimeComponentLifecycle expected)
    {
        _ = Interlocked.CompareExchange(ref _inner, null, expected);
    }

    public void TearDown(LiveEntityRecord record)
    {
        ILiveEntityRuntimeComponentLifecycle lifecycle = Volatile.Read(ref _inner)
            ?? throw new InvalidOperationException(
                "The live-entity runtime component lifecycle has not been bound.");
        lifecycle.TearDown(record);
    }

    private sealed class Binding : IDisposable
    {
        private DeferredLiveEntityRuntimeComponentLifecycle? _owner;
        private readonly ILiveEntityRuntimeComponentLifecycle _expected;

        public Binding(
            DeferredLiveEntityRuntimeComponentLifecycle owner,
            ILiveEntityRuntimeComponentLifecycle expected)
        {
            _owner = owner;
            _expected = expected;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unbind(_expected);
    }
}

public sealed class DelegateLiveEntityResourceLifecycle : ILiveEntityResourceLifecycle
{
    private readonly Action<WorldEntity> _register;
    private readonly Action<WorldEntity> _unregister;

    public DelegateLiveEntityResourceLifecycle(
        Action<WorldEntity> register,
        Action<WorldEntity> unregister)
    {
        _register = register ?? throw new ArgumentNullException(nameof(register));
        _unregister = unregister ?? throw new ArgumentNullException(nameof(unregister));
    }

    public void Register(WorldEntity entity) => _register(entity);
    public void Unregister(WorldEntity entity) => _unregister(entity);
}

public sealed class LiveEntityRecord
{
    private readonly RuntimeEntityDirectory _directory;

    internal LiveEntityRecord(
        RuntimeEntityDirectory directory,
        RuntimeEntityRecord canonical)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        Canonical = canonical ?? throw new ArgumentNullException(nameof(canonical));
    }

    internal RuntimeEntityRecord Canonical { get; }
    internal RuntimeEntityKey? ProjectionKey { get; set; }
    public uint ServerGuid => Canonical.ServerGuid;
    public ushort Generation => Canonical.Incarnation;
    public WorldSession.EntitySpawn Snapshot
    {
        get => Canonical.Snapshot;
        internal set => _directory.RefreshSnapshot(Canonical, value);
    }
    public WorldEntity? WorldEntity { get; internal set; }
    public uint? LocalEntityId => Canonical.LocalEntityId ?? WorldEntity?.Id;
    public uint FullCellId
    {
        get => Canonical.FullCellId;
        internal set => _directory.SetFullCell(
            Canonical,
            value,
            Canonical.CanonicalLandblockId);
    }
    public uint CanonicalLandblockId
    {
        get => Canonical.CanonicalLandblockId;
        internal set => _directory.SetFullCell(
            Canonical,
            Canonical.FullCellId,
            value);
    }
    public uint RawPhysicsState => Canonical.RawPhysicsState;
    public PhysicsStateFlags FinalPhysicsState
    {
        get => Canonical.FinalPhysicsState;
        internal set => _directory.SetFinalPhysicsState(Canonical, value);
    }
    public RetailObjectQuantumClock ObjectClock => Canonical.ObjectClock;
    internal ulong ObjectClockEpoch => Canonical.ObjectClockEpoch;
    public bool HasPartArray
    {
        get => Canonical.HasPartArray;
        internal set => _directory.SetHasPartArray(Canonical, value);
    }
    public PhysicsBody? PhysicsBody
    {
        get => Canonical.PhysicsBody;
    }
    internal bool PhysicsBodyAcquisitionInProgress
    {
        get => Canonical.PhysicsBodyAcquisitionInProgress;
    }
    public ILiveEntityAnimationRuntime? AnimationRuntime { get; internal set; }
    public IRuntimeRemoteMotion? RemoteMotionRuntime
    {
        get => Canonical.RemoteMotion;
    }
    internal bool RemoteMotionBindingInProgress
    {
        get => Canonical.RemoteMotionBindingInProgress;
    }
    public AcDream.Core.Physics.Motion.IPhysicsObjHost? PhysicsHost
    {
        get => Canonical.PhysicsHost;
    }
    internal bool RequiresRemotePlacementRuntime
    {
        get => Canonical.RequiresRemotePlacementRuntime;
    }
    public IRuntimeProjectile? ProjectileRuntime => Canonical.Projectile;
    public ILiveEntityEffectProfile? EffectProfile { get; internal set; }
    public bool ResourcesRegistered { get; internal set; }
    internal LiveEntityMaterializationResidence? MaterializationResidence
        { get; set; }
    public bool IsSpatiallyProjected { get; internal set; }
    public bool IsSpatiallyVisible { get; internal set; }
    internal ulong ProjectionMutationVersion { get; set; }
    internal ulong PositionAuthorityVersion => Canonical.PositionAuthorityVersion;
    internal ulong StateAuthorityVersion => Canonical.StateAuthorityVersion;
    internal ulong VectorAuthorityVersion => Canonical.VectorAuthorityVersion;
    internal ulong VelocityAuthorityVersion => Canonical.VelocityAuthorityVersion;
    internal ulong MovementAuthorityVersion => Canonical.MovementAuthorityVersion;
    internal ulong ObjDescAuthorityVersion => Canonical.ObjDescAuthorityVersion;
    internal ulong CreateIntegrationVersion => Canonical.CreateIntegrationVersion;
    public bool WorldSpawnPublished { get; internal set; }
    public bool InitialHydrationCompleted { get; internal set; }
    internal bool CreateProjectionSynchronizationPending { get; set; }
    internal bool ProjectionHydrationInProgress { get; set; }
    internal ulong ProjectionHydrationCreateVersion { get; set; }
    internal bool ProjectionHydrationRetryRequested { get; set; }
    /// <summary>
    /// A renderer mesh transaction can be re-entered by a newer accepted
    /// ObjDesc. Keep that independent projection obligation on the exact
    /// incarnation until the newest visual description publishes.
    /// </summary>
    internal bool AppearanceProjectionSynchronizationPending { get; set; }
    internal bool AppearanceHydrationInProgress { get; set; }
    internal ulong AppearanceHydrationVersion { get; set; }
    internal bool AppearanceHydrationRetryRequested { get; set; }
    public LiveEntityProjectionKind ProjectionKind { get; internal set; }
    internal bool RuntimeComponentsTeardownCompleted { get; set; }
    internal LiveEntityTeardownPlan? RuntimeComponentTeardownPlan { get; set; }
    internal bool SpatialProjectionTeardownCompleted { get; set; }
    internal bool TeardownInProgress { get; set; }
    internal bool DeleteAcceptedForTeardown
    {
        get => Canonical.DeleteAcceptedForTeardown;
        set => _directory.SetDeleteAcceptedForTeardown(Canonical, value);
    }

    internal bool TryDequeueStateTransition(out RetailPhysicsStateTransition transition) =>
        _directory.TryDequeueStateTransition(Canonical, out transition);

    internal void SuspendObjectClock() => _directory.SuspendObjectClock(Canonical);

    internal void ResetObjectClockForEnterWorld(bool isStatic) =>
        _directory.ResetObjectClockForEnterWorld(Canonical, isStatic);

    internal RetailPhysicsStateTransition ApplyRawPhysicsState(uint rawState) =>
        _directory.ApplyRawPhysicsState(Canonical, rawState);

    internal void NoteObjDescProjectionSynchronization()
    {
        AppearanceProjectionSynchronizationPending = true;
        if (AppearanceHydrationInProgress)
            AppearanceHydrationRetryRequested = true;
    }

    public uint ProjectionCellId => WorldEntity is not null
        ? FullCellId
        : Snapshot.Position?.LandblockId ?? FullCellId;

    internal void RefreshDerivedState(bool refreshPosition = true) =>
        _directory.RefreshSnapshot(Canonical, Snapshot, refreshPosition);
}

public readonly record struct LiveEntityRegistrationResult(
    InboundCreateResult Inbound,
    RuntimeEntityRecord? Canonical,
    LiveEntityRecord? Projection,
    bool LogicalRegistrationCreated,
    bool ReplacedExistingGeneration,
    Exception? PriorGenerationCleanupFailure = null);

public sealed class LiveEntityRuntime : ILiveEntityRadarSource
{
    public const uint FirstLiveEntityId = RuntimeEntityDirectory.FirstLocalEntityId;
    public const uint LastLiveEntityId = RuntimeEntityDirectory.LastLocalEntityId;

    private readonly GpuWorldState _spatial;
    private readonly ILiveEntityResourceLifecycle _resources;
    private readonly ILiveEntityRuntimeComponentLifecycle _runtimeComponentLifecycle;
    private readonly RuntimeEntityObjectLifetime _entityObjects;
    private readonly RuntimeEntityDirectory _directory;
    private readonly RuntimePhysicsState _physics;
    private readonly LiveEntityProjectionStore _projections;
    private readonly Dictionary<RuntimeEntityKey, ILiveEntityAnimationRuntime> _spatialAnimations = new();
    private readonly List<RuntimeEntityRecord> _spatialRootCanonicalScratch = new();
    private readonly List<RuntimeEntityRecord> _spatialRemoteCanonicalScratch = new();
    private readonly Dictionary<RuntimeEntityKey, int>
        _presentationOnlySpatialMutationDepth = new();
    private bool _isClearing;
    private bool _sessionClearPendingFinalization;
    private bool _isRegisteringResources;
    private int _logicalTeardownDepth;
    private uint _rebucketingGuid;

    public LiveEntityRuntime(
        GpuWorldState spatial,
        ILiveEntityResourceLifecycle resources,
        RuntimeEntityObjectLifetime entityObjects)
        : this(
            spatial,
            resources,
            NullLiveEntityRuntimeComponentLifecycle.Instance,
            entityObjects)
    {
    }

    public LiveEntityRuntime(
        GpuWorldState spatial,
        ILiveEntityResourceLifecycle resources,
        Action<LiveEntityRecord> tearDownRuntimeComponents,
        RuntimeEntityObjectLifetime entityObjects)
        : this(
            spatial,
            resources,
            new DelegateLiveEntityRuntimeComponentLifecycle(tearDownRuntimeComponents),
            entityObjects)
    {
    }

    internal LiveEntityRuntime(
        GpuWorldState spatial,
        ILiveEntityResourceLifecycle resources,
        ILiveEntityRuntimeComponentLifecycle? runtimeComponentLifecycle,
        RuntimeEntityObjectLifetime entityObjects)
    {
        _spatial = spatial ?? throw new ArgumentNullException(nameof(spatial));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _runtimeComponentLifecycle = runtimeComponentLifecycle
            ?? throw new ArgumentNullException(nameof(runtimeComponentLifecycle));
        _entityObjects = entityObjects
            ?? throw new ArgumentNullException(nameof(entityObjects));
        _directory = _entityObjects.Entities;
        _physics = _entityObjects.Physics;
        _projections = new LiveEntityProjectionStore(_directory);
        _spatial.LiveProjectionVisibilityChanged += OnSpatialVisibilityChanged;
        _physics.CellCommitted += OnRuntimePhysicsCellCommitted;
    }

    public int Count => _directory.Count;
    public int PendingTeardownCount => _directory.PendingTeardownCount;
    public int MaterializedCount => _projections.MaterializedCount;
    public IReadOnlyCollection<LiveEntityRecord> Records => _projections.ActiveRecords;
    public IReadOnlyCollection<LiveEntityRecord> MaterializedRecords =>
        _projections.MaterializedRecords;
    public IReadOnlyCollection<LiveEntityRecord> VisibleRecords =>
        _projections.VisibleRecords;
    internal IReadOnlyCollection<RuntimeEntityRecord> CanonicalRecords =>
        _directory.ActiveRecords;
    internal ulong SessionLifetimeVersion => _directory.SessionLifetimeVersion;
    internal RuntimePhysicsState Physics => _physics;
    public IReadOnlyDictionary<uint, WorldSession.EntitySpawn> Snapshots => _directory.Snapshots;
    internal int AnimationRuntimeCount
    {
        get
        {
            int count = 0;
            IReadOnlyList<LiveEntityRecord> records = _projections.ActiveRecords;
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].AnimationRuntime is not null)
                    count++;
            }
            return count;
        }
    }
    internal int SpatialAnimationRuntimeCount => _spatialAnimations.Count;
    internal int SpatialRemoteMotionRuntimeCount => _physics.SpatialRemoteCount;
    internal int SpatialProjectileRuntimeCount => _physics.SpatialProjectileCount;
    internal int SpatialRootObjectCount => _physics.SpatialRootCount;
    public ParentAttachmentState ParentAttachments => _directory.ParentAttachments;

    internal bool TryGetCanonical(
        uint serverGuid,
        out RuntimeEntityRecord canonical) =>
        _directory.TryGetActive(serverGuid, out canonical);

    internal bool TryGetProjection(
        RuntimeEntityRecord canonical,
        out LiveEntityRecord record) =>
        _projections.TryGet(canonical, out record);

    internal bool IsCurrentCanonical(RuntimeEntityRecord canonical) =>
        _directory.IsCurrent(canonical);

    internal bool IsCurrentCreateIntegration(
        RuntimeEntityRecord canonical,
        ulong expectedCreateIntegrationVersion) =>
        _directory.IsCurrent(canonical)
        && canonical.CreateIntegrationVersion == expectedCreateIntegrationVersion;

    internal void SetHasPartArray(
        RuntimeEntityRecord canonical,
        bool value) =>
        _directory.SetHasPartArray(canonical, value);

    public event Action<LiveEntityRecord, bool>? ProjectionVisibilityChanged;

    public LiveEntityRegistrationResult RegisterLiveEntity(
        WorldSession.EntitySpawn incoming,
        bool isLocalPlayer = false)
    {
        if (_isClearing || _sessionClearPendingFinalization || _isRegisteringResources)
        {
            throw new InvalidOperationException(
                _isClearing || _sessionClearPendingFinalization
                    ? "A live entity cannot register while the session lifetime is clearing."
                    : "A live entity cannot register from inside atomic resource registration.");
        }

        RuntimeEntityRegistrationResult registration =
            _entityObjects.RegisterEntityWithInitialResidence(
                incoming,
                isLocalPlayer,
                RetirePriorProjection);
        RuntimeEntityRecord? canonical = registration.Canonical;
        LiveEntityRecord? projection = canonical is null
            ? null
            : _projections.GetCurrentOrDefault(canonical.ServerGuid);
        if (projection is not null
            && ReferenceEquals(projection.Canonical, canonical))
        {
            RefreshSpatialRuntimeIndexes(projection);
        }
        return new LiveEntityRegistrationResult(
            registration.Inbound,
            canonical,
            projection,
            registration.LogicalRegistrationCreated,
            registration.ReplacedExistingGeneration,
            registration.PriorGenerationCleanupFailure);
    }

    public WorldEntity? MaterializeLiveEntity(
        uint serverGuid,
        uint fullCellId,
        Func<uint, WorldEntity> factory,
        LiveEntityProjectionKind projectionKind = LiveEntityProjectionKind.World)
    {
        if (!_directory.TryGetActive(
                serverGuid,
                out RuntimeEntityRecord canonical))
        {
            return null;
        }

        return MaterializeLiveEntity(
            canonical,
            fullCellId,
            factory,
            projectionKind,
            initializeProjection: null,
            out _);
    }

    internal WorldEntity? MaterializeLiveEntity(
        RuntimeEntityRecord expectedCanonical,
        uint fullCellId,
        Func<uint, WorldEntity> factory,
        LiveEntityProjectionKind projectionKind,
        Action<LiveEntityRecord>? initializeProjection,
        out LiveEntityRecord? record,
        LiveEntityMaterializationResidence residence =
            LiveEntityMaterializationResidence.LegacyImmediate)
    {
        ArgumentNullException.ThrowIfNull(expectedCanonical);
        ArgumentNullException.ThrowIfNull(factory);
        if (residence is LiveEntityMaterializationResidence
                .AwaitRuntimePlacement
            && projectionKind is not LiveEntityProjectionKind.World)
        {
            throw new ArgumentException(
                "Only a top-level world projection can await canonical Runtime placement.",
                nameof(projectionKind));
        }
        record = null;
        if (_isClearing
            || _sessionClearPendingFinalization
            || _logicalTeardownDepth != 0
            || _isRegisteringResources)
        {
            throw new InvalidOperationException(
                "A live entity cannot materialize inside an active logical-lifetime transition.");
        }
        if (!_directory.IsCurrent(expectedCanonical))
            return null;

        uint serverGuid = expectedCanonical.ServerGuid;
        bool createdSidecar = false;
        if (!_projections.TryGet(expectedCanonical, out record))
        {
            _ = expectedCanonical.LocalEntityId
                ?? throw new InvalidOperationException(
                    "Runtime must issue entity identity before App projection.");
            try
            {
                record = _projections.AddMaterializing(expectedCanonical);
                createdSidecar = true;
                record.MaterializationResidence = residence;
                initializeProjection?.Invoke(record);
            }
            catch
            {
                if (record is not null)
                {
                    _projections.RemoveActive(record);
                    record.ProjectionKey = null;
                    record = null;
                }
                throw;
            }
        }
        else if (record.MaterializationResidence != residence)
        {
            throw new InvalidOperationException(
                $"Live entity 0x{serverGuid:X8}/{expectedCanonical.Incarnation} "
                + $"cannot change its materialization residence from "
                + $"{record.MaterializationResidence} to {residence}.");
        }

        if (record.WorldEntity is null)
        {
            uint localId = expectedCanonical.LocalEntityId
                ?? throw new InvalidOperationException(
                    "A materializing App sidecar lost its Runtime local ID.");
            WorldEntity entity;
            try
            {
                entity = factory(localId)
                    ?? throw new InvalidOperationException(
                        "A live-entity projection factory returned null.");
                if (entity.Id != localId)
                {
                    throw new InvalidOperationException(
                        $"Live projection id 0x{entity.Id:X8} did not match reserved id 0x{localId:X8}.");
                }
                if (entity.ServerGuid != serverGuid)
                {
                    throw new InvalidOperationException(
                        $"Live projection guid 0x{entity.ServerGuid:X8} did not match record 0x{serverGuid:X8}.");
                }
            }
            catch
            {
                if (createdSidecar)
                {
                    _projections.RemoveActive(record);
                    record.ProjectionKey = null;
                    record = null;
                }
                throw;
            }

            record.WorldEntity = entity;
            RefreshPresentation(record);
            _isRegisteringResources = true;
            try
            {
                // Registration may span several App owners. Until the whole
                // edge returns, Unregister remains a required rollback
                // obligation; a failed rollback retains this identity as a
                // teardown tombstone instead of making partial owners
                // unreachable.
                record.ResourcesRegistered = true;
                try
                {
                    _resources.Register(entity);
                }
                catch (Exception registrationError)
                {
                    Exception? rollbackError = null;
                    try
                    {
                        _resources.Unregister(entity);
                        record.ResourcesRegistered = false;
                    }
                    catch (Exception error)
                    {
                        rollbackError = error;
                    }

                    if (rollbackError is null)
                    {
                        _projections.RemoveActive(record);
                        record.ProjectionKey = null;
                        record.WorldEntity = null;
                        record = null;
                        throw;
                    }

                    _entityObjects.RetireAfterProjectionAcquisitionFailure(
                        expectedCanonical);
                    if (_projections.RemoveActive(record))
                    {
                        RetainTeardownRecord(record);
                    }
                    throw new AggregateException(
                        "Live entity resource registration and rollback both failed; " +
                        "the partial owner was retained for teardown retry.",
                        registrationError,
                        rollbackError);
                }
            }
            finally
            {
                _isRegisteringResources = false;
            }
        }

        if (!_directory.IsCurrent(expectedCanonical)
            || !_projections.TryGet(expectedCanonical, out LiveEntityRecord currentRecord)
            || !ReferenceEquals(currentRecord, record))
        {
            record = null;
            return null;
        }

        record.ProjectionKind = projectionKind;
        if (projectionKind is LiveEntityProjectionKind.World)
        {
            record.WorldEntity!.IsAncestorDrawVisible = true;
        }
        RefreshPresentation(record);
        WorldEntity materialized = record.WorldEntity!;
        if (residence is LiveEntityMaterializationResidence
                .AwaitRuntimePlacement)
        {
            record.IsSpatiallyProjected = false;
            record.IsSpatiallyVisible = false;
            RefreshSpatialPresentationIndexes(record);
            RefreshPresentation(record);
            return materialized;
        }
        if (!RebucketLiveEntity(serverGuid, fullCellId)
            || !_projections.TryGet(expectedCanonical, out LiveEntityRecord? current)
            || !ReferenceEquals(current, record)
            || !ReferenceEquals(current.WorldEntity, materialized))
        {
            // Visibility observers may replace this GUID while rebucketing.
            // A failed teardown can retain the displaced entity as a retry
            // tombstone; it is not the result of this materialization.
            record = null;
            return null;
        }
        return materialized;
    }

    public bool RebucketLiveEntity(uint serverGuid, uint spatialCellOrLandblockId)
    {
        if (!_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
            || record.WorldEntity is not { } entity)
            return false;
        if (record.MaterializationResidence is
                LiveEntityMaterializationResidence.AwaitRuntimePlacement
            && HasActiveInitialCreateResidence(record.Canonical))
        {
            return false;
        }

        RuntimeEntityKey key = RequireProjectionKey(record);
        bool wasProjected = record.IsSpatiallyProjected;
        bool wasVisible = record.IsSpatiallyVisible;
        bool wasOrdinaryRoot = _physics.IsSpatialRoot(record.Canonical);
        ulong projectionOperation = ++record.ProjectionMutationVersion;
        // GpuWorldState reports an intermediate false/true pair while moving
        // between two loaded buckets. Suppress those implementation details
        // and publish only the final logical visibility edge.
        record.IsSpatiallyProjected = true;
        bool hasExactDestinationCell = spatialCellOrLandblockId != 0u
            && (spatialCellOrLandblockId & 0xFFFFu) != 0xFFFFu;
        if (hasExactDestinationCell)
        {
            entity.ParentCellId = spatialCellOrLandblockId;
        }
        Exception? spatialNotificationFailure = null;
        uint priorRebucketingGuid = _rebucketingGuid;
        _rebucketingGuid = serverGuid;
        try
        {
            try
            {
                _spatial.RebucketLiveEntity(
                    key,
                    entity,
                    spatialCellOrLandblockId);
            }
            catch (AggregateException error)
            {
                spatialNotificationFailure = error;
            }
        }
        finally
        {
            _rebucketingGuid = priorRebucketingGuid;
        }
        if (!IsCurrentProjectionOperation(serverGuid, record, projectionOperation))
        {
            ThrowAfterCommittedProjectionChange(
                serverGuid,
                spatialNotificationFailure,
                runtimeNotificationFailure: null);
            return false;
        }
        bool visible = _spatial.IsLiveEntityProjectionResident(key);
        record.IsSpatiallyVisible = visible;
        RefreshPresentation(record);
        if (!_entityObjects.CommitWireCellRebucket(
                record.Canonical,
                spatialCellOrLandblockId))
        {
            ThrowAfterCommittedProjectionChange(
                serverGuid,
                spatialNotificationFailure,
                runtimeNotificationFailure: null);
            return false;
        }
        bool isOrdinaryRoot = record.ProjectionKind is LiveEntityProjectionKind.World
            && record.IsSpatiallyProjected
            && record.IsSpatiallyVisible
            && record.FullCellId != 0;
        if (!wasProjected && !isOrdinaryRoot)
        {
            record.SuspendObjectClock();
            SynchronizePhysicsBodyActiveState(record);
        }
        else if (wasOrdinaryRoot != isOrdinaryRoot)
        {
            if (isOrdinaryRoot)
            {
                if (!wasProjected)
                    record.ObjectClock.Deactivate();
                record.ResetObjectClockForEnterWorld(
                    (record.FinalPhysicsState & PhysicsStateFlags.Static) != 0);
                SynchronizePhysicsBodyActiveState(record);
            }
            else
            {
                record.SuspendObjectClock();
                SynchronizePhysicsBodyActiveState(record);
            }
        }
        RefreshSpatialRuntimeIndexes(record);
        Exception? runtimeNotificationFailure = null;
        if (!wasProjected || wasVisible != visible)
        {
            try
            {
                PublishProjectionVisibilityChanged(record, visible);
            }
            catch (Exception error)
            {
                runtimeNotificationFailure = error;
            }
        }
        if (!IsCurrentProjectionOperation(serverGuid, record, projectionOperation))
        {
            ThrowAfterCommittedProjectionChange(
                serverGuid,
                spatialNotificationFailure,
                runtimeNotificationFailure);
            return false;
        }
        ThrowAfterCommittedProjectionChange(
            serverGuid,
            spatialNotificationFailure,
            runtimeNotificationFailure);
        return true;
    }

    private bool RebucketLiveEntityPresentationOnly(
        uint serverGuid,
        LiveEntityRecord record,
        WorldEntity entity,
        uint spatialCellOrLandblockId)
    {
        RuntimeEntityKey key = RequireProjectionKey(record);
        bool wasProjected = record.IsSpatiallyProjected;
        bool wasVisible = record.IsSpatiallyVisible;
        ulong projectionOperation = ++record.ProjectionMutationVersion;
        record.IsSpatiallyProjected = true;
        Exception? spatialNotificationFailure = null;
        uint priorRebucketingGuid = _rebucketingGuid;
        _rebucketingGuid = serverGuid;
        BeginPresentationOnlySpatialMutation(key);
        try
        {
            try
            {
                _spatial.RebucketLiveEntity(
                    key,
                    entity,
                    spatialCellOrLandblockId);
            }
            catch (AggregateException error)
            {
                spatialNotificationFailure = error;
            }
        }
        finally
        {
            EndPresentationOnlySpatialMutation(key);
            _rebucketingGuid = priorRebucketingGuid;
        }
        if (!IsCurrentProjectionOperation(serverGuid, record, projectionOperation))
        {
            ThrowAfterCommittedProjectionChange(
                serverGuid,
                spatialNotificationFailure,
                runtimeNotificationFailure: null);
            return false;
        }
        bool visible = _spatial.IsLiveEntityProjectionResident(key);
        record.IsSpatiallyVisible = visible;
        RefreshSpatialPresentationIndexes(record);
        RefreshPresentation(record);
        RefreshSpatialRuntimeIndexes(record);
        Exception? runtimeNotificationFailure = null;
        if (!wasProjected || wasVisible != visible)
        {
            try
            {
                PublishProjectionVisibilityChanged(record, visible);
            }
            catch (Exception error)
            {
                runtimeNotificationFailure = error;
            }
        }
        if (!IsCurrentProjectionOperation(serverGuid, record, projectionOperation))
        {
            ThrowAfterCommittedProjectionChange(
                serverGuid,
                spatialNotificationFailure,
                runtimeNotificationFailure);
            return false;
        }
        ThrowAfterCommittedProjectionChange(
            serverGuid,
            spatialNotificationFailure,
            runtimeNotificationFailure);
        return true;
    }

    internal EquippedChildPresentationRebucketDisposition RebucketEquippedChildPresentation(
        uint serverGuid,
        uint parentCellId)
    {
        if (!_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
            || record.WorldEntity is not { } entity)
        {
            return EquippedChildPresentationRebucketDisposition.NoProjection;
        }
        if (!_directory.ParentAttachments.HasCommittedParent(serverGuid))
            return EquippedChildPresentationRebucketDisposition.NotAttached;
        if (record.MaterializationResidence is
                LiveEntityMaterializationResidence.AwaitRuntimePlacement
            && HasActiveInitialCreateResidence(record.Canonical))
        {
            return EquippedChildPresentationRebucketDisposition.NotAttached;
        }
        return RebucketLiveEntityPresentationOnly(
                serverGuid,
                record,
                entity,
                parentCellId)
            ? EquippedChildPresentationRebucketDisposition.Moved
            : EquippedChildPresentationRebucketDisposition.Displaced;
    }

    internal bool TryApplyInitialCreateCompletionPresentation(
        in RuntimePlacementProjectionSnapshot projection)
    {
        RuntimePlacementProjectionToken token = projection.Token;
        if (!token.IsValid
            || token.SessionLifetimeVersion != _directory.SessionLifetimeVersion
            || !_projections.TryGet(token.Entity, out LiveEntityRecord? record)
            || !_directory.IsCurrent(record.Canonical)
            || record.Canonical.Key != token.Entity
            || record.WorldEntity is not { } entity)
        {
            return true;
        }
        if (record.FullCellId != token.ExactCellId
            || record.Canonical.PlacementCommitVersion
                != token.PlacementCommitVersion
            || record.Canonical.PhysicsBody is not { } body
            || body.Position != projection.WorldPosition
            || body.Orientation != projection.Orientation)
        {
            return true;
        }

        entity.SetPosition(projection.WorldPosition);
        entity.Rotation = projection.Orientation;
        entity.ParentCellId = token.ExactCellId;
        return RebucketLiveEntityPresentationOnly(
            record.ServerGuid,
            record,
            entity,
            token.ExactCellId);
    }

    internal bool HasActiveInitialCreateResidence(RuntimeEntityKey key) =>
        _directory.TryGetByLocalId(
            key.LocalEntityId,
            out RuntimeEntityRecord canonical)
        && _directory.IsCurrent(canonical)
        && canonical.Key == key
        && _entityObjects.TryGetInitialCreateResidence(canonical, out _);

    internal bool HasActiveInitialCreateResidence(
        RuntimeEntityRecord canonical) =>
        _entityObjects.TryGetInitialCreateResidence(canonical, out _);

    internal void ConvertMaterializationResidenceToLegacyImmediate(
        LiveEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.MaterializationResidence is not
            LiveEntityMaterializationResidence.AwaitRuntimePlacement)
        {
            return;
        }
        if (HasActiveInitialCreateResidence(record.Canonical))
        {
            throw new InvalidOperationException(
                $"Live entity 0x{record.ServerGuid:X8}/"
                + $"{record.Canonical.Incarnation} cannot convert to "
                + "LegacyImmediate residence while its initial-create "
                + "residence lease is still active.");
        }
        record.MaterializationResidence =
            LiveEntityMaterializationResidence.LegacyImmediate;
    }

    internal bool TryApplyRuntimePlacementProjection(
        in RuntimePlacementProjectionSnapshot projection)
    {
        if (projection.Kind is RuntimePlacementProjectionKind.Discard)
        {
            return true;
        }

        if (projection.Kind is RuntimePlacementProjectionKind.ExecutorCompleted)
        {
            return true;
        }

        RuntimePlacementProjectionToken token = projection.Token;
        if (!TryGetRuntimePlacementProjectionRecord(
                token,
                requirePlacementVersions:
                    projection.Kind is RuntimePlacementProjectionKind.Place,
                out LiveEntityRecord? record)
            || record.WorldEntity is not { } entity)
        {
            return false;
        }
        if (projection.Kind is RuntimePlacementProjectionKind.Place
            && !_spatial.IsLoaded(
                (token.ExactCellId & 0xFFFF0000u) | 0xFFFFu))
        {
            return false;
        }

        return projection.Kind switch
        {
            RuntimePlacementProjectionKind.Place =>
                TryApplyRuntimePlacementPlace(in projection, record, entity),
            RuntimePlacementProjectionKind.Withdraw =>
                TryApplyRuntimePlacementWithdrawal(token, record, entity),
            RuntimePlacementProjectionKind.WithdrawalRestored =>
                TryApplyRuntimePlacementPlace(
                    in projection,
                    record,
                    entity,
                    commitPose: false),
            _ => false,
        };
    }

    private bool TryApplyRuntimePlacementPlace(
        in RuntimePlacementProjectionSnapshot projection,
        LiveEntityRecord record,
        WorldEntity entity,
        bool commitPose = true)
    {
        RuntimePlacementProjectionToken token = projection.Token;
        RuntimeEntityKey key = token.Entity;
        ulong projectionOperation = ++record.ProjectionMutationVersion;

        if (commitPose)
        {
            entity.SetPosition(projection.WorldPosition);
            entity.Rotation = projection.Orientation;
        }
        entity.ParentCellId = token.ExactCellId;
        record.IsSpatiallyProjected = true;

        Exception? spatialNotificationFailure = null;
        uint priorRebucketingGuid = _rebucketingGuid;
        _rebucketingGuid = record.ServerGuid;
        BeginPresentationOnlySpatialMutation(key);
        try
        {
            try
            {
                _spatial.RebucketLiveEntity(key, entity, token.ExactCellId);
            }
            catch (AggregateException error)
            {
                spatialNotificationFailure = error;
            }
        }
        finally
        {
            EndPresentationOnlySpatialMutation(key);
            _rebucketingGuid = priorRebucketingGuid;
        }

        if (!IsCurrentProjectionOperation(
                record.ServerGuid,
                record,
                projectionOperation)
            || !TryGetRuntimePlacementProjectionRecord(
                token,
                requirePlacementVersions: true,
                out LiveEntityRecord? current)
            || !ReferenceEquals(current, record)
            || !ReferenceEquals(current.WorldEntity, entity))
        {
            ThrowAfterCommittedProjectionChange(
                record.ServerGuid,
                spatialNotificationFailure,
                runtimeNotificationFailure: null);
            return false;
        }

        bool visible = _spatial.IsLiveEntityProjectionResident(key);
        record.IsSpatiallyVisible = visible;
        RefreshSpatialPresentationIndexes(record);
        RefreshPresentation(record);

        if (!IsCurrentProjectionOperation(
                record.ServerGuid,
                record,
                projectionOperation))
        {
            ThrowAfterCommittedProjectionChange(
                record.ServerGuid,
                spatialNotificationFailure,
                runtimeNotificationFailure: null);
            return false;
        }

        ThrowAfterCommittedProjectionChange(
            record.ServerGuid,
            spatialNotificationFailure,
            runtimeNotificationFailure: null);
        return true;
    }

    private bool TryApplyRuntimePlacementWithdrawal(
        in RuntimePlacementProjectionToken token,
        LiveEntityRecord record,
        WorldEntity entity)
    {
        RuntimeEntityKey key = token.Entity;
        ulong projectionOperation = ++record.ProjectionMutationVersion;
        record.IsSpatiallyProjected = false;

        Exception? spatialNotificationFailure = null;
        uint priorRebucketingGuid = _rebucketingGuid;
        _rebucketingGuid = record.ServerGuid;
        BeginPresentationOnlySpatialMutation(key);
        try
        {
            try
            {
                _spatial.RemoveLiveEntityProjection(entity);
            }
            catch (AggregateException error)
            {
                spatialNotificationFailure = error;
            }
        }
        finally
        {
            EndPresentationOnlySpatialMutation(key);
            _rebucketingGuid = priorRebucketingGuid;
        }

        if (!IsCurrentProjectionOperation(
                record.ServerGuid,
                record,
                projectionOperation)
            || !TryGetRuntimePlacementProjectionRecord(
                token,
                requirePlacementVersions: false,
                out LiveEntityRecord? current)
            || !ReferenceEquals(current, record)
            || !ReferenceEquals(current.WorldEntity, entity))
        {
            ThrowAfterCommittedProjectionChange(
                record.ServerGuid,
                spatialNotificationFailure,
                runtimeNotificationFailure: null);
            return false;
        }

        record.IsSpatiallyVisible = false;
        RefreshSpatialPresentationIndexes(record);
        RefreshPresentation(record);

        if (!IsCurrentProjectionOperation(
                record.ServerGuid,
                record,
                projectionOperation))
        {
            ThrowAfterCommittedProjectionChange(
                record.ServerGuid,
                spatialNotificationFailure,
                runtimeNotificationFailure: null);
            return false;
        }

        ThrowAfterCommittedProjectionChange(
            record.ServerGuid,
            spatialNotificationFailure,
            runtimeNotificationFailure: null);
        return true;
    }

    private bool TryGetRuntimePlacementProjectionRecord(
        in RuntimePlacementProjectionToken token,
        bool requirePlacementVersions,
        out LiveEntityRecord record)
    {
        if (!token.IsValid
            || token.SessionLifetimeVersion != _directory.SessionLifetimeVersion
            || !_projections.TryGet(token.Entity, out record!)
            || !_directory.IsCurrent(record.Canonical)
            || record.Canonical.Key != token.Entity
            || RequireProjectionKey(record) != token.Entity
            || !IsValidPortalPlacementAuthority(token))
        {
            record = null!;
            return false;
        }

        if (requirePlacementVersions
            && (record.Canonical.PositionAuthorityVersion
                    != token.PositionAuthorityVersion
                || record.Canonical.SpatialAuthorityVersion
                    != token.SpatialAuthorityVersion
                || record.Canonical.PlacementCommitVersion
                    != token.PlacementCommitVersion
                || record.Canonical.FullCellId != token.ExactCellId))
        {
            record = null!;
            return false;
        }

        return true;
    }

    private static bool IsValidPortalPlacementAuthority(
        in RuntimePlacementProjectionToken token)
    {
        RuntimePortalPlacementAuthority portal = token.Portal;
        if (!portal.Present)
            return portal.IsEmpty;

        return portal.IsValid
            && portal.Projection.DestinationCell == token.ExactCellId;
    }

    private void BeginPresentationOnlySpatialMutation(RuntimeEntityKey key)
    {
        _presentationOnlySpatialMutationDepth.TryGetValue(key, out int depth);
        _presentationOnlySpatialMutationDepth[key] = checked(depth + 1);
    }

    private void EndPresentationOnlySpatialMutation(RuntimeEntityKey key)
    {
        if (!_presentationOnlySpatialMutationDepth.TryGetValue(
                key,
                out int depth)
            || depth <= 0)
        {
            throw new InvalidOperationException(
                "Presentation-only spatial mutation depth was not balanced.");
        }

        if (depth == 1)
            _presentationOnlySpatialMutationDepth.Remove(key);
        else
            _presentationOnlySpatialMutationDepth[key] = depth - 1;
    }

    public bool WithdrawLiveEntityProjection(uint serverGuid)
    {
        if (!_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
            || record.WorldEntity is null)
            return false;

        RuntimeEntityKey key = RequireProjectionKey(record);
        bool wasOrdinaryRoot = _physics.IsSpatialRoot(record.Canonical);
        ulong objectClockEpoch = record.ObjectClockEpoch;
        ulong projectionOperation = ++record.ProjectionMutationVersion;
        Exception? spatialNotificationFailure = null;
        try
        {
            _spatial.RemoveLiveEntityProjection(record.WorldEntity);
        }
        catch (AggregateException error)
        {
            spatialNotificationFailure = error;
        }
        if (!IsCurrentProjectionOperation(serverGuid, record, projectionOperation))
        {
            ThrowAfterCommittedProjectionChange(
                serverGuid,
                spatialNotificationFailure,
                runtimeNotificationFailure: null);
            return false;
        }
        bool spatialEdgeWasDeferred = record.IsSpatiallyVisible;
        _projections.SetVisible(record, false);
        record.IsSpatiallyProjected = false;
        record.IsSpatiallyVisible = false;
        if (wasOrdinaryRoot && record.ObjectClockEpoch == objectClockEpoch)
            record.SuspendObjectClock();
        SynchronizePhysicsBodyActiveState(record);
        RefreshSpatialRuntimeIndexes(record);
        Exception? runtimeNotificationFailure = null;
        if (spatialEdgeWasDeferred)
        {
            try
            {
                PublishProjectionVisibilityChanged(record, false);
            }
            catch (Exception error)
            {
                runtimeNotificationFailure = error;
            }
        }
        if (!IsCurrentProjectionOperation(serverGuid, record, projectionOperation))
        {
            ThrowAfterCommittedProjectionChange(
                serverGuid,
                spatialNotificationFailure,
                runtimeNotificationFailure);
            return false;
        }
        ThrowAfterCommittedProjectionChange(
            serverGuid,
            spatialNotificationFailure,
            runtimeNotificationFailure);
        return true;
    }

    public bool WithdrawLiveEntityProjection(LiveEntityRecord expectedRecord)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        return WithdrawLiveEntityProjection(
            expectedRecord,
            expectedRecord.PositionAuthorityVersion,
            expectedRecord.ProjectionMutationVersion);
    }

    internal bool WithdrawLiveEntityProjection(
        LiveEntityRecord expectedRecord,
        ulong expectedPositionAuthorityVersion,
        ulong expectedProjectionMutationVersion)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        return IsCurrentRecord(expectedRecord)
            && expectedRecord.PositionAuthorityVersion == expectedPositionAuthorityVersion
            && expectedRecord.ProjectionMutationVersion == expectedProjectionMutationVersion
            && WithdrawLiveEntityProjection(expectedRecord.ServerGuid);
    }

    internal bool WithdrawLiveEntityProjectionToCellless(uint serverGuid)
    {
        if (!_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
            || record.WorldEntity is null)
        {
            return false;
        }

        if (!_entityObjects.CommitWithdrawal(
                record.Canonical,
                AcknowledgeCelllessCanonicalCommit))
        {
            return false;
        }
        record.WorldEntity.ParentCellId = 0u;
        return WithdrawLiveEntityProjection(serverGuid);
    }

    public bool UnregisterLiveEntity(
        DeleteObject.Parsed delete,
        bool isLocalPlayer,
        bool removeRetainedObject = false,
        Action? beforeTeardown = null)
    {
        if (_isRegisteringResources)
        {
            throw new InvalidOperationException(
                "A live entity cannot unregister from inside atomic resource registration.");
        }
        var teardownKey = (delete.Guid, delete.InstanceSequence);
        LiveEntityRecord? record = null;
        RuntimeEntityRecord? canonicalOnly = null;
        if (_directory.TryGetTeardown(
                teardownKey.Guid,
                teardownKey.InstanceSequence,
                out RuntimeEntityRecord retainedCanonical))
        {
            _projections.TryGetTeardown(retainedCanonical, out record);
        }
        bool retryingAcceptedDelete = record is not null
            && (record.DeleteAcceptedForTeardown
                || _isClearing
                || _sessionClearPendingFinalization);
        RuntimeEntityDeleteAcceptance? acceptedDelete = null;
        if (!retryingAcceptedDelete)
        {
            if (!_entityObjects.TryAcceptDelete(
                    delete,
                    isLocalPlayer,
                    removeRetainedObject,
                    out acceptedDelete))
                return false;

            LiveEntityRecord? retainedRegistrationFailure = record;
            if (acceptedDelete.RetiredCanonical is { } activeCanonical)
            {
                if (_projections.TryGet(
                        activeCanonical,
                        out LiveEntityRecord? activeRecord))
                {
                    if (!_projections.RemoveActive(activeRecord))
                    {
                        throw new InvalidOperationException(
                            $"Exact App projection for 0x{delete.Guid:X8}/{delete.InstanceSequence} could not be retired.");
                    }
                    record = activeRecord;
                    RetainTeardownRecord(record);
                }
                else
                {
                    canonicalOnly = activeCanonical;
                }
            }
            else
            {
                record = retainedRegistrationFailure;
            }
            if (record is not null)
                record.DeleteAcceptedForTeardown = true;
        }

        List<Exception>? failures = null;
        _logicalTeardownDepth++;
        try
        {
            if (!retryingAcceptedDelete)
            {
                try
                {
                    _entityObjects.CompleteAcceptedDelete(acceptedDelete!);
                }
                catch (Exception error)
                {
                    (failures ??= new List<Exception>()).Add(error);
                }

                try
                {
                    beforeTeardown?.Invoke();
                }
                catch (Exception error)
                {
                    (failures ??= new List<Exception>()).Add(error);
                }
            }

            if (record is not null)
            {
                try
                {
                    TearDownRecord(record);
                    ReleaseTeardownRecord(record);
                }
                catch (Exception error)
                {
                    (failures ??= new List<Exception>()).Add(error);
                }
            }
            else if (canonicalOnly is not null)
            {
                TearDownCanonicalOnly(canonicalOnly);
            }
        }
        finally
        {
            _logicalTeardownDepth--;
        }

        try
        {
            // Persistence is GUID-scoped. End it only when the accepted
            // delete left no replacement incarnation or unfinished teardown
            // behind.
            if (!_directory.TryGetActive(delete.Guid, out _)
                && !HasPendingTeardown(delete.Guid))
            {
                _spatial.ForgetLiveEntity(delete.Guid);
            }
        }
        catch (Exception error)
        {
            (failures ??= new List<Exception>()).Add(error);
        }

        if (failures is not null)
            throw new AggregateException(
                $"Live entity 0x{delete.Guid:X8} deletion cleanup failed.",
                failures);
        return true;
    }

    public bool TryGetRecord(uint serverGuid, out LiveEntityRecord record) =>
        _projections.TryGetCurrent(serverGuid, out record!);

    public bool TryGetWorldEntity(uint serverGuid, out WorldEntity entity)
    {
        if (_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
            && record.WorldEntity is { } found)
        {
            entity = found;
            return true;
        }

        entity = null!;
        return false;
    }

    public bool TryGetSpatiallyProjectedRecord(
        uint serverGuid,
        out LiveEntityRecord record)
    {
        if (_projections.TryGetCurrent(serverGuid, out LiveEntityRecord found)
            && found.WorldEntity is not null
            && found.ProjectionKind is LiveEntityProjectionKind.World
            && found.IsSpatiallyProjected)
        {
            record = found;
            return true;
        }

        record = null!;
        return false;
    }

    public bool TryGetAttachedProjectedRecord(
        uint serverGuid,
        out LiveEntityRecord record)
    {
        if (_projections.TryGetCurrent(serverGuid, out LiveEntityRecord found)
            && found.WorldEntity is not null
            && found.ProjectionKind is LiveEntityProjectionKind.Attached
            && found.IsSpatiallyProjected)
        {
            record = found;
            return true;
        }

        record = null!;
        return false;
    }

    public bool TryGetPickEligibleRecord(
        uint serverGuid,
        out LiveEntityRecord record)
        => TryGetInteractionEligibleRecord(serverGuid, out record)
            || TryGetAttachedProjectedRecord(serverGuid, out record);

    /// <summary>
    /// Pick eligibility bound to one logical incarnation. A stale published
    /// frame must never retarget a replacement which reused the server GUID.
    /// </summary>
    public bool TryGetPickEligibleRecord(
        uint serverGuid,
        uint localEntityId,
        out LiveEntityRecord record)
    {
        if (serverGuid != 0u
            && localEntityId != 0u
            && TryGetPickEligibleRecord(serverGuid, out LiveEntityRecord found)
            && found.WorldEntity!.Id == localEntityId)
        {
            record = found;
            return true;
        }

        record = null!;
        return false;
    }

    public bool TryGetInteractionEligibleEntity(
        uint serverGuid,
        out WorldEntity entity)
    {
        if (_projections.TryGetVisibleCurrent(
                serverGuid,
                out LiveEntityRecord record)
            && record.WorldEntity is { } visible)
        {
            entity = visible;
            return true;
        }

        entity = null!;
        return false;
    }

    bool ILiveEntityRadarSource.TryGetMaterialized(
        uint serverGuid,
        out WorldEntity entity) =>
        TryGetWorldEntity(serverGuid, out entity);

    bool ILiveEntityRadarSource.TryGetVisible(
        uint serverGuid,
        out WorldEntity entity) =>
        TryGetInteractionEligibleEntity(serverGuid, out entity);

    void ILiveEntityRadarSource.CopyVisibleTo(
        List<KeyValuePair<uint, WorldEntity>> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        foreach (LiveEntityRecord record in _projections.VisibleRecords)
        {
            destination.Add(new KeyValuePair<uint, WorldEntity>(
                record.ServerGuid,
                record.WorldEntity!));
        }
    }

    public bool TryGetInteractionEligibleRecord(
        uint serverGuid,
        out LiveEntityRecord record)
    {
        if (_projections.TryGetVisibleCurrent(
                serverGuid,
                out LiveEntityRecord found)
            && found.WorldEntity is not null)
        {
            record = found;
            return true;
        }

        record = null!;
        return false;
    }

    public bool TryGetInteractionEligibleRecord(
        uint serverGuid,
        uint localEntityId,
        out LiveEntityRecord record)
    {
        if (serverGuid != 0u
            && localEntityId != 0u
            && TryGetInteractionEligibleRecord(serverGuid, out LiveEntityRecord found)
            && found.WorldEntity!.Id == localEntityId)
        {
            record = found;
            return true;
        }

        record = null!;
        return false;
    }

    public bool ContainsWorldEntity(uint serverGuid) =>
        _projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
        && record.WorldEntity is not null;

    public bool TryGetServerGuid(uint localEntityId, out uint serverGuid)
    {
        if (_directory.TryGetByLocalId(
                localEntityId,
                out RuntimeEntityRecord canonical))
        {
            serverGuid = canonical.ServerGuid;
            return true;
        }

        serverGuid = 0u;
        return false;
    }

    internal bool TryGetRecordByLocalEntityId(
        uint localEntityId,
        out LiveEntityRecord record) =>
        _projections.TryGetByLocalId(localEntityId, out record);

    internal bool TryGetRecord(
        RuntimeEntityKey key,
        out LiveEntityRecord record) =>
        _projections.TryGet(key, out record);

    public bool TryGetLocalEntityId(uint serverGuid, out uint localEntityId)
    {
        if (_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
            && record.LocalEntityId is { } found)
        {
            localEntityId = found;
            return true;
        }

        localEntityId = 0;
        return false;
    }

    public void SetAnimationRuntime(uint serverGuid, ILiveEntityAnimationRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
            || record.WorldEntity is not { } entity)
            throw new InvalidOperationException($"Cannot bind animation before live entity 0x{serverGuid:X8} is materialized.");
        if (!ReferenceEquals(runtime.Entity, entity))
            throw new InvalidOperationException("Animation runtime belongs to a different WorldEntity.");

        _ = RequireProjectionKey(record);
        record.AnimationRuntime = runtime;
        RefreshSpatialRuntimeIndexes(record);
    }

    public bool TryGetAnimationRuntime(
        uint localEntityId,
        out ILiveEntityAnimationRuntime runtime)
    {
        if (_projections.TryGetByLocalId(
                localEntityId,
                out LiveEntityRecord record)
            && record.AnimationRuntime is { } found)
        {
            runtime = found;
            return true;
        }

        runtime = null!;
        return false;
    }

    internal bool TryGetProjectionKey(
        uint serverGuid,
        out RuntimeEntityKey key)
    {
        if (_projections.TryGetCurrent(
                serverGuid,
                out LiveEntityRecord? record)
            && record.ProjectionKey is { } found)
        {
            key = found;
            return true;
        }

        key = default;
        return false;
    }

    public bool ClearAnimationRuntime(uint serverGuid)
    {
        if (!_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
            || record.AnimationRuntime is null)
            return false;
        record.AnimationRuntime = null;
        RefreshSpatialRuntimeIndexes(record);
        return true;
    }

    internal bool ClearAnimationRuntime(LiveEntityRecord expectedRecord)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        if (expectedRecord.AnimationRuntime is null
            || expectedRecord.ProjectionKey is not { } key
            || !_projections.TryGet(key, out LiveEntityRecord? record)
            || !ReferenceEquals(record, expectedRecord))
        {
            return false;
        }

        record.AnimationRuntime = null;
        RefreshSpatialRuntimeIndexes(record);
        return true;
    }

    internal PhysicsBody GetOrCreatePhysicsBody(
        uint serverGuid,
        Func<LiveEntityRecord, PhysicsBody> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
            || record.WorldEntity is null)
        {
            throw new InvalidOperationException(
                $"Cannot acquire physics body before live entity 0x{serverGuid:X8} is materialized.");
        }
        bool ProjectionIsCurrent() =>
            _projections.TryGetCurrent(
                serverGuid,
                out LiveEntityRecord? current)
            && ReferenceEquals(current, record)
            && current.WorldEntity is not null;
        return _physics.GetOrCreatePhysicsBody(
            record.Canonical,
            _ => factory(record),
            ProjectionIsCurrent);
    }

    internal void SetRemoteMotionRuntime(uint serverGuid, IRuntimeRemoteMotion runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record))
            throw new InvalidOperationException($"Cannot bind remote motion before live entity 0x{serverGuid:X8} exists.");
        ulong lifetimeVersion = CurrentLifetimeMutation(serverGuid);
        _physics.SetRemoteMotion(
            record.Canonical,
            runtime,
            () => CurrentLifetimeMutation(serverGuid) == lifetimeVersion
                && _projections.TryGetCurrent(
                    serverGuid,
                    out LiveEntityRecord? current)
                && ReferenceEquals(current, record));
        SynchronizePhysicsBodyActiveState(record);
        RefreshSpatialRuntimeIndexes(record);
    }

    public void InstallPhysicsHost(
        LiveEntityRecord expectedRecord,
        AcDream.Core.Physics.Motion.IPhysicsObjHost host)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        ArgumentNullException.ThrowIfNull(host);
        uint serverGuid = expectedRecord.ServerGuid;
        bool ProjectionIsCurrent() =>
            _projections.TryGetCurrent(
                serverGuid,
                out LiveEntityRecord? current)
            && ReferenceEquals(current, expectedRecord);
        _physics.InstallPhysicsHost(
            expectedRecord.Canonical,
            host,
            ProjectionIsCurrent);
    }

    public bool TryGetPhysicsHost(
        uint serverGuid,
        out AcDream.Core.Physics.Motion.IPhysicsObjHost host)
    {
        if (_projections.TryGetCurrent(serverGuid, out _)
            && _physics.TryGetPhysicsHost(serverGuid, out var existing))
        {
            host = existing;
            return true;
        }

        host = null!;
        return false;
    }

    internal void ForceEndCollisionReporting(RuntimeEntityRecord canonical) =>
        _physics.CollisionReports.LeaveWorld(canonical);

    public bool TryGetRemoteMotionRuntime(
        uint serverGuid,
        out IRuntimeRemoteMotion runtime)
    {
        if (_projections.TryGetCurrent(
                serverGuid,
                out LiveEntityRecord? record)
            && record.RemoteMotionRuntime is { } found)
        {
            runtime = found;
            return true;
        }

        runtime = null!;
        return false;
    }

    internal bool ClearRemoteMotionRuntime(uint serverGuid)
    {
        if (!_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
            || record.RemoteMotionRuntime is null)
            return false;
        if (!_physics.ClearRemoteMotion(record.Canonical))
            return false;
        RefreshSpatialRuntimeIndexes(record);
        return true;
    }

    internal RemoteMotion GetOrCreateRemoteMotionRuntime(uint serverGuid)
    {
        if (!_projections.TryGetCurrent(
                serverGuid,
                out LiveEntityRecord? record))
        {
            throw new InvalidOperationException(
                $"Cannot acquire remote motion before live entity 0x{serverGuid:X8} exists.");
        }

        bool ProjectionIsCurrent() =>
            _projections.TryGetCurrent(
                serverGuid,
                out LiveEntityRecord? current)
            && ReferenceEquals(current, record);
        return _physics.GetOrCreateRemoteMotion(
            record.Canonical,
            ProjectionIsCurrent);
    }

    internal IRuntimeProjectile BindProjectileRuntime(
        uint serverGuid,
        PhysicsBody body,
        ProjectileCollisionSphere collisionSphere,
        Func<bool>? externalOwnerValid = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
            || record.WorldEntity is null)
        {
            throw new InvalidOperationException(
                $"Cannot bind projectile physics before live entity 0x{serverGuid:X8} is materialized.");
        }
        if (record.PhysicsBodyAcquisitionInProgress && record.PhysicsBody is null)
            throw new InvalidOperationException(
                $"Live entity 0x{serverGuid:X8} cannot bind projectile physics during physics-body acquisition.");
        if (record.PhysicsBody is { } canonicalBody
            && !ReferenceEquals(canonicalBody, body))
        {
            throw new InvalidOperationException(
                $"Live entity 0x{serverGuid:X8} cannot replace its canonical physics body within one incarnation.");
        }

        IRuntimeProjectile runtime = _physics.BindProjectile(
            record.Canonical,
            body,
            collisionSphere,
            () => IsCurrentRecord(record)
                && record.WorldEntity is { } entity
                && record.LocalEntityId == entity.Id
                && (externalOwnerValid?.Invoke() ?? true));
        RefreshSpatialRuntimeIndexes(record);
        return runtime;
    }

    public bool TryGetProjectileRuntime(
        uint serverGuid,
        out IRuntimeProjectile runtime)
    {
        if (_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
            && record.ProjectileRuntime is { } found)
        {
            runtime = found;
            return true;
        }

        runtime = null!;
        return false;
    }

    public bool ClearProjectileRuntime(uint serverGuid)
    {
        if (!_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
            || record.ProjectileRuntime is null)
            return false;

        _physics.ClearProjectile(record.Canonical);
        RefreshSpatialRuntimeIndexes(record);
        return true;
    }

    public void SetEffectProfile(uint serverGuid, ILiveEntityEffectProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record))
            throw new InvalidOperationException(
                $"Cannot bind an effect profile before live entity 0x{serverGuid:X8} exists.");
        record.EffectProfile = profile;
    }

    public bool TryGetEffectProfile(
        uint serverGuid,
        out ILiveEntityEffectProfile profile)
    {
        if (_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
            && record.EffectProfile is { } found)
        {
            profile = found;
            return true;
        }

        profile = null!;
        return false;
    }

    public bool TryGetSnapshot(uint guid, out WorldSession.EntitySpawn spawn) =>
        _directory.TryGetSnapshot(guid, out spawn);

    public bool TryApplyObjDesc(ObjDescEvent.Parsed update, out WorldSession.EntitySpawn accepted)
        => _entityObjects.TryApplyObjDesc(
            update,
            canonical =>
            {
                if (_projections.TryGet(
                        canonical,
                        out LiveEntityRecord? record))
                {
                    record.NoteObjDescProjectionSynchronization();
                }
            },
            out accepted);

    public bool TryApplyPickup(PickupEvent.Parsed update, out WorldSession.EntitySpawn accepted)
        => _entityObjects.TryApplyPickup(
            update,
            AcknowledgeCelllessCanonicalCommit,
            out accepted);

    public bool TryApplyCreateParent(CreateParentUpdate update, out WorldSession.EntitySpawn accepted)
        => _entityObjects.TryApplyCreateParent(
            update,
            acknowledgeProjection: null,
            out accepted);

    public bool TryApplyParent(ParentEvent.Parsed update, out WorldSession.EntitySpawn accepted)
        => _entityObjects.TryApplyParent(
            update,
            acknowledgeProjection: null,
            out accepted);

    internal bool CommitStagedParent(
        ParentAttachmentRelation relation,
        out WorldSession.EntitySpawn accepted)
        => _entityObjects.TryCommitParent(
            relation,
            acknowledgeProjection: null,
            out accepted);

    internal bool CommitAcceptedParentCellless(
        LiveEntityRecord record,
        ulong positionAuthorityVersion)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _entityObjects.CommitAcceptedParentCellless(
            record.Canonical,
            positionAuthorityVersion,
            AcknowledgeCelllessCanonicalCommit);
    }

    internal bool CommitAcceptedParentCellless(
        RuntimeEntityRecord canonical,
        ulong positionAuthorityVersion)
    {
        return _entityObjects.CommitAcceptedParentCellless(
            canonical,
            positionAuthorityVersion,
            AcknowledgeCelllessCanonicalCommit);
    }

    public bool TryApplyMotion(
        WorldSession.EntityMotionUpdate update,
        bool retainPayload,
        out WorldSession.EntitySpawn accepted,
        out AcceptedPhysicsTimestamps timestamps)
        => _entityObjects.TryApplyMotion(
            update,
            retainPayload,
            acknowledgeProjection: null,
            out accepted,
            out timestamps);

    public bool TryApplyVector(VectorUpdate.Parsed update, out WorldSession.EntitySpawn accepted)
        => _entityObjects.TryApplyVector(
            update,
            acknowledgeProjection: null,
            out accepted);

    public bool TryApplyState(SetState.Parsed update, out WorldSession.EntitySpawn accepted)
        => TryApplyState(update, out accepted, out _);

    public bool TryApplyState(
        SetState.Parsed update,
        out WorldSession.EntitySpawn accepted,
        out RetailPhysicsStateTransition transition)
        => _entityObjects.TryApplyState(
            update,
            (canonical, _) =>
            {
                if (_projections.TryGet(
                        canonical,
                        out LiveEntityRecord? record))
                {
                    RefreshPresentation(record);
                }
            },
            out accepted,
            out transition);

    public bool SetAttachedChildNoDraw(uint childServerGuid, bool noDraw)
    {
        if (!_projections.TryGetCurrent(childServerGuid, out LiveEntityRecord? record))
            return false;
        return _entityObjects.CommitChildNoDraw(
            record.Canonical,
            noDraw,
            _ => RefreshPresentation(record));
    }

    public bool TryApplyPosition(
        WorldSession.EntityPositionUpdate update,
        bool isLocalPlayer,
        System.Numerics.Quaternion? forcePositionRotation,
        System.Numerics.Vector3? currentLocalVelocity,
        out PositionTimestampDisposition disposition,
        out WorldSession.EntitySpawn accepted,
        out AcceptedPhysicsTimestamps timestamps) =>
        _entityObjects.TryApplyPosition(
            update,
            isLocalPlayer,
            forcePositionRotation,
            currentLocalVelocity,
            acknowledgeProjection: null,
            out disposition,
            out accepted,
            out timestamps);

    internal RuntimeAuthoritativePositionRoute? ClassifyRemoteAcceptedPosition(
        RuntimeEntityRecord canonical,
        in AcDream.Core.Net.WorldSession.EntityPositionUpdate update,
        AcDream.Core.Physics.PositionTimestampDisposition disposition,
        in AcceptedPhysicsTimestamps timestamps,
        float? playerDistance) =>
        _entityObjects.ClassifyRemoteAcceptedPosition(
            canonical,
            update,
            disposition,
            timestamps,
            playerDistance);

    public bool IsFreshTeleportStart(uint localPlayerGuid, ushort teleportSequence) =>
        _directory.IsFreshTeleportStart(localPlayerGuid, teleportSequence);

    public RetailObjectClockDisposition GetRootObjectClockDisposition(
        uint serverGuid)
    {
        if (!_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
            || record.ProjectionKind is not LiveEntityProjectionKind.World
            || !record.IsSpatiallyProjected
            || !record.IsSpatiallyVisible
            || record.FullCellId == 0)
        {
            return RetailObjectClockDisposition.Suspend;
        }

        return (record.FinalPhysicsState & PhysicsStateFlags.Frozen) != 0
            ? RetailObjectClockDisposition.Suspend
            : RetailObjectClockDisposition.Advance;
    }

    public bool ShouldAdvanceRootRuntime(uint serverGuid) =>
        GetRootObjectClockDisposition(serverGuid)
            is RetailObjectClockDisposition.Advance;

    internal bool IsCurrentPositionAuthority(
        LiveEntityRecord record,
        ulong authorityVersion) =>
        _projections.TryGetCurrent(record.ServerGuid, out LiveEntityRecord? current)
        && ReferenceEquals(current, record)
        && current.PositionAuthorityVersion == authorityVersion;

    internal bool IsCurrentPositionAuthority(
        RuntimeEntityRecord canonical,
        ulong authorityVersion) =>
        _directory.IsCurrent(canonical)
        && canonical.PositionAuthorityVersion == authorityVersion;

    internal bool IsCurrentStateAuthority(
        LiveEntityRecord record,
        ulong authorityVersion) =>
        _projections.TryGetCurrent(record.ServerGuid, out LiveEntityRecord? current)
        && ReferenceEquals(current, record)
        && current.StateAuthorityVersion == authorityVersion;

    internal bool IsCurrentVectorAuthority(
        LiveEntityRecord record,
        ulong authorityVersion) =>
        _projections.TryGetCurrent(record.ServerGuid, out LiveEntityRecord? current)
        && ReferenceEquals(current, record)
        && current.VectorAuthorityVersion == authorityVersion;

    internal bool IsCurrentVelocityAuthority(
        LiveEntityRecord record,
        ulong authorityVersion) =>
        _projections.TryGetCurrent(record.ServerGuid, out LiveEntityRecord? current)
        && ReferenceEquals(current, record)
        && current.VelocityAuthorityVersion == authorityVersion;

    internal bool IsCurrentVelocityAuthority(
        RuntimeEntityRecord canonical,
        ulong authorityVersion) =>
        _directory.IsCurrent(canonical)
        && canonical.VelocityAuthorityVersion == authorityVersion;

    internal bool IsCurrentMovementAuthority(
        LiveEntityRecord record,
        ulong authorityVersion) =>
        _projections.TryGetCurrent(record.ServerGuid, out LiveEntityRecord? current)
        && ReferenceEquals(current, record)
        && current.MovementAuthorityVersion == authorityVersion;

    internal bool IsCurrentObjDescAuthority(
        LiveEntityRecord record,
        ulong authorityVersion) =>
        _projections.TryGetCurrent(record.ServerGuid, out LiveEntityRecord? current)
        && ReferenceEquals(current, record)
        && current.ObjDescAuthorityVersion == authorityVersion;

    internal void CopySpatialRootObjectRecordsTo(List<LiveEntityRecord> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        _physics.CopySpatialRootsTo(_spatialRootCanonicalScratch);
        for (int index = 0;
             index < _spatialRootCanonicalScratch.Count;
             index++)
        {
            RuntimeEntityRecord canonical =
                _spatialRootCanonicalScratch[index];
            if (_projections.TryGet(
                    canonical,
                    out LiveEntityRecord? current)
                && HasSpatialRuntimeProjection(current))
            {
                destination.Add(current);
            }
        }
    }

    internal bool IsCurrentSpatialRootObject(LiveEntityRecord record) =>
        IsCurrentRecord(record)
        && _physics.IsSpatialRoot(record.Canonical)
        && HasSpatialRuntimeProjection(record);

    internal bool IsCurrentSpatialAnimation(
        LiveEntityRecord record,
        ILiveEntityAnimationRuntime animation) =>
        IsCurrentSpatialRootObject(record)
        && ReferenceEquals(record.AnimationRuntime, animation)
        && record.ProjectionKey is { } key
        && _spatialAnimations.TryGetValue(key, out var indexed)
        && ReferenceEquals(indexed, animation);

    internal bool IsCurrentSpatialAnimation(
        uint localEntityId,
        ILiveEntityAnimationRuntime animation) =>
        _projections.TryGetByLocalId(localEntityId, out LiveEntityRecord record)
        && IsCurrentSpatialAnimation(record, animation);

    internal bool IsCurrentAnimationOwner(
        LiveEntityRecord record,
        ILiveEntityAnimationRuntime animation) =>
        IsCurrentRecord(record)
        && ReferenceEquals(record.AnimationRuntime, animation);

    internal void CopySpatialAnimationRuntimesTo<TAnimation>(
        List<KeyValuePair<uint, TAnimation>> destination)
        where TAnimation : class, ILiveEntityAnimationRuntime
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        foreach ((RuntimeEntityKey key, ILiveEntityAnimationRuntime animation)
                 in _spatialAnimations)
        {
            if (animation is TAnimation typed)
            {
                destination.Add(
                    new KeyValuePair<uint, TAnimation>(
                        key.LocalEntityId,
                        typed));
            }
        }
    }

    internal void CopySpatialAnimationLocalIdsTo(HashSet<uint> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        foreach (RuntimeEntityKey key in _spatialAnimations.Keys)
            destination.Add(key.LocalEntityId);
    }

    internal bool TryCommitAuthoritativeVelocity(
        LiveEntityRecord record,
        PhysicsBody body,
        Vector3 velocity,
        double currentTime) =>
        TryCommitAuthoritativeVectorCore(
            record,
            body,
            velocity,
            angularVelocity: null,
            currentTime);

    internal bool TryCommitAuthoritativeVector(
        LiveEntityRecord record,
        PhysicsBody body,
        Vector3 velocity,
        Vector3 angularVelocity,
        double currentTime) =>
        TryCommitAuthoritativeVectorCore(
            record,
            body,
            velocity,
            angularVelocity,
            currentTime);

    private bool TryCommitAuthoritativeVectorCore(
        LiveEntityRecord record,
        PhysicsBody body,
        Vector3 velocity,
        Vector3? angularVelocity,
        double currentTime)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(body);
        bool ProjectionIsCurrent() =>
            _projections.TryGetCurrent(
                record.ServerGuid,
                out LiveEntityRecord? current)
            && ReferenceEquals(current, record)
            && ReferenceEquals(current.PhysicsBody, body);
        return _physics.TryCommitAuthoritativeVector(
            record.Canonical,
            body,
            velocity,
            angularVelocity,
            currentTime,
            ProjectionIsCurrent);
    }

    internal void CopySpatialRemoteMotionRecordsTo(List<LiveEntityRecord> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        _physics.CopySpatialRemotesTo(_spatialRemoteCanonicalScratch);
        for (int index = 0;
             index < _spatialRemoteCanonicalScratch.Count;
             index++)
        {
            RuntimeEntityRecord canonical =
                _spatialRemoteCanonicalScratch[index];
            if (_projections.TryGet(
                    canonical,
                    out LiveEntityRecord? record)
                && HasSpatialRuntimeProjection(record))
            {
                destination.Add(record);
            }
        }
    }

    internal bool IsCurrentSpatialRemoteMotion(
        LiveEntityRecord record,
        IRuntimeRemoteMotion runtime) =>
        IsCurrentRecord(record)
        && _physics.IsSpatialRemote(record.Canonical, runtime)
        && HasSpatialRuntimeProjection(record);

    internal void CopySpatialProjectileRecordsTo(List<LiveEntityRecord> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();

        _physics.CopySpatialProjectilesTo(_spatialRootCanonicalScratch);
        for (int i = 0; i < _spatialRootCanonicalScratch.Count; i++)
        {
            RuntimeEntityRecord canonical = _spatialRootCanonicalScratch[i];
            if (_projections.TryGet(canonical, out LiveEntityRecord? record)
                && HasSpatialRuntimeProjection(record))
            {
                destination.Add(record);
            }
        }
    }

    internal bool IsCurrentSpatialProjectile(
        LiveEntityRecord record,
        IRuntimeProjectile runtime) =>
        IsCurrentRecord(record)
        && _physics.IsSpatialProjectile(record.Canonical, runtime)
        && HasSpatialRuntimeProjection(record);

    public bool IsHidden(uint serverGuid) =>
        _projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
        && (record.FinalPhysicsState & PhysicsStateFlags.Hidden) != 0;

    public bool TryMarkWorldSpawnPublished(uint serverGuid)
    {
        if (!_projections.TryGetCurrent(serverGuid, out LiveEntityRecord? record)
            || record.WorldEntity is null
            || record.ProjectionKind is not LiveEntityProjectionKind.World
            || record.WorldSpawnPublished)
            return false;
        record.WorldSpawnPublished = true;
        return true;
    }

    internal bool TryMarkInitialHydrationCompleted(
        LiveEntityRecord expectedRecord,
        ulong expectedCreateIntegrationVersion)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        if (!IsCurrentRecord(expectedRecord)
            || expectedRecord.CreateIntegrationVersion != expectedCreateIntegrationVersion
            || expectedRecord.WorldEntity is null
            || !expectedRecord.ResourcesRegistered)
        {
            return false;
        }

        expectedRecord.InitialHydrationCompleted = true;
        return true;
    }

    internal bool TryMarkCreateProjectionSynchronizationPending(
        LiveEntityRecord expectedRecord,
        ulong expectedCreateIntegrationVersion)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        if (!IsCurrentCreateIntegration(
                expectedRecord,
                expectedCreateIntegrationVersion))
        {
            return false;
        }

        expectedRecord.CreateProjectionSynchronizationPending = true;
        return true;
    }

    internal bool TryCompleteCreateProjectionSynchronization(
        LiveEntityRecord expectedRecord,
        ulong expectedCreateIntegrationVersion)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        if (!IsCurrentCreateIntegration(
                expectedRecord,
                expectedCreateIntegrationVersion))
        {
            return false;
        }

        expectedRecord.CreateProjectionSynchronizationPending = false;
        return true;
    }

    internal bool TryBeginProjectionHydration(
        LiveEntityRecord expectedRecord,
        ulong? expectedCreateIntegrationVersion)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        if (!IsCurrentRecord(expectedRecord)
            || (expectedCreateIntegrationVersion is { } expected
                && expectedRecord.CreateIntegrationVersion != expected))
        {
            return false;
        }

        if (expectedRecord.ProjectionHydrationInProgress)
        {
            if (expectedRecord.CreateIntegrationVersion
                != expectedRecord.ProjectionHydrationCreateVersion)
            {
                expectedRecord.ProjectionHydrationRetryRequested = true;
            }
            return false;
        }

        expectedRecord.ProjectionHydrationInProgress = true;
        expectedRecord.ProjectionHydrationCreateVersion =
            expectedRecord.CreateIntegrationVersion;
        expectedRecord.ProjectionHydrationRetryRequested = false;
        return true;
    }

    internal bool EndProjectionHydration(LiveEntityRecord expectedRecord)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        bool retry = IsCurrentRecord(expectedRecord)
            && (expectedRecord.ProjectionHydrationRetryRequested
                || expectedRecord.CreateIntegrationVersion
                    != expectedRecord.ProjectionHydrationCreateVersion);
        if (retry)
        {
            expectedRecord.CreateProjectionSynchronizationPending = true;
        }
        expectedRecord.ProjectionHydrationInProgress = false;
        expectedRecord.ProjectionHydrationCreateVersion = 0UL;
        expectedRecord.ProjectionHydrationRetryRequested = false;
        return retry;
    }

    internal bool TryBeginAppearanceHydration(
        LiveEntityRecord expectedRecord,
        ulong expectedObjDescAuthorityVersion)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        if (!expectedRecord.AppearanceProjectionSynchronizationPending
            || !IsCurrentObjDescAuthority(
                expectedRecord,
                expectedObjDescAuthorityVersion))
        {
            return false;
        }

        expectedRecord.AppearanceProjectionSynchronizationPending = true;
        if (expectedRecord.AppearanceHydrationInProgress)
        {
            expectedRecord.AppearanceHydrationRetryRequested = true;
            return false;
        }

        expectedRecord.AppearanceHydrationInProgress = true;
        expectedRecord.AppearanceHydrationVersion =
            expectedObjDescAuthorityVersion;
        expectedRecord.AppearanceHydrationRetryRequested = false;
        return true;
    }

    internal bool EndAppearanceHydration(LiveEntityRecord expectedRecord)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        bool retry = IsCurrentRecord(expectedRecord)
            && (expectedRecord.AppearanceHydrationRetryRequested
                || expectedRecord.ObjDescAuthorityVersion
                    != expectedRecord.AppearanceHydrationVersion);
        expectedRecord.AppearanceHydrationInProgress = false;
        expectedRecord.AppearanceHydrationVersion = 0UL;
        expectedRecord.AppearanceHydrationRetryRequested = false;
        return retry;
    }

    internal bool TryCompleteAppearanceProjectionSynchronization(
        LiveEntityRecord expectedRecord,
        ulong expectedObjDescAuthorityVersion)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        if (!expectedRecord.AppearanceProjectionSynchronizationPending
            || !IsCurrentObjDescAuthority(
                expectedRecord,
                expectedObjDescAuthorityVersion))
        {
            return false;
        }

        expectedRecord.AppearanceProjectionSynchronizationPending = false;
        return true;
    }

    internal bool IsCurrentCreateIntegration(
        LiveEntityRecord expectedRecord,
        ulong expectedCreateIntegrationVersion) =>
        IsCurrentRecord(expectedRecord)
        && expectedRecord.CreateIntegrationVersion == expectedCreateIntegrationVersion;

    internal void RetireGenerationProjection(
        RuntimeEntityRecord canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        if (_isClearing || _isRegisteringResources)
        {
            throw new InvalidOperationException(
                _isClearing
                    ? "Live entity projection teardown is already in progress."
                    : "Live entity projection teardown cannot begin inside atomic resource registration.");
        }

        LiveEntityRecord? record = null;
        if (_projections.TryGet(canonical, out LiveEntityRecord active))
        {
            if (!_projections.RemoveActive(active))
            {
                throw new InvalidOperationException(
                    $"Exact App projection for 0x{canonical.ServerGuid:X8}/{canonical.Incarnation} could not be retired during generation reset.");
            }
            _projections.RetainTeardown(active);
            record = active;
        }
        else if (_projections.TryGetTeardown(
                     canonical,
                     out LiveEntityRecord retained))
        {
            record = retained;
        }

        if (record is null)
            return;

        _isClearing = true;
        try
        {
            TearDownRecord(record, completeCanonical: false);
            _projections.ReleaseTeardown(record);
        }
        finally
        {
            _isClearing = false;
        }
    }

    internal void CompleteGenerationProjectionRetirement()
    {
        if (_projections.ActiveCount != 0
            || _projections.TeardownCount != 0)
        {
            throw new InvalidOperationException(
                "Live entity projection storage has not converged at the generation-reset boundary.");
        }

        _spatialAnimations.Clear();
        _physics.ClearSpatialWorksets();
        _projections.ClearConverged();
        _spatial.ClearLiveEntityLifetimeState();
        _sessionClearPendingFinalization = false;
    }

    public void Clear()
    {
        if (_isClearing || _isRegisteringResources)
        {
            throw new InvalidOperationException(
                _isClearing
                    ? "Live entity session teardown is already in progress."
                    : "Live entity session teardown cannot begin inside atomic resource registration.");
        }

        _isClearing = true;
        _sessionClearPendingFinalization = true;
        IReadOnlyList<RuntimeEntityRecord> retiredCanonicals =
            _entityObjects.BeginSessionClear();
        List<Exception>? failures = null;
        try
        {
            foreach (RuntimeEntityRecord canonical in retiredCanonicals)
            {
                if (_projections.TryGet(
                        canonical,
                        out LiveEntityRecord? record))
                {
                    if (!_projections.RemoveActive(record))
                    {
                        throw new InvalidOperationException(
                            $"Exact App projection for 0x{record.ServerGuid:X8}/{record.Generation} could not be retired during session clear.");
                    }
                    RetainTeardownRecord(record);
                }
                else
                {
                    TearDownCanonicalOnly(canonical);
                }
            }

            foreach (LiveEntityRecord record in _projections.TeardownRecords.ToArray())
            {
                if (!_projections.IsRetainedTeardown(record))
                {
                    continue;
                }

                if (record.TeardownInProgress)
                    continue;
                try
                {
                    TearDownRecord(record);
                    ReleaseTeardownRecord(record);
                }
                catch (Exception error)
                {
                    (failures ??= new List<Exception>()).Add(error);
                }
            }

            CompleteSessionClearIfConverged();
        }
        finally
        {
            _isClearing = false;
        }

        if (failures is not null)
            throw new AggregateException("One or more live entities failed session teardown.", failures);
    }

    public int RetryPendingTeardowns()
    {
        if (_projections.TeardownCount == 0
            || _isClearing
            || _isRegisteringResources
            || _logicalTeardownDepth != 0)
            return 0;

        int completed = 0;
        List<Exception>? failures = null;
        foreach (LiveEntityRecord record in _projections.TeardownRecords.ToArray())
        {
            if (!_projections.IsRetainedTeardown(record))
            {
                continue;
            }

            if (record.TeardownInProgress)
                continue;

            _logicalTeardownDepth++;
            try
            {
                try
                {
                    TearDownRecord(record);
                    ReleaseTeardownRecord(record);
                    completed++;
                    if (!_directory.TryGetActive(record.ServerGuid, out _)
                        && !HasPendingTeardown(record.ServerGuid)
                        && !_directory.TryGetSnapshot(record.ServerGuid, out _))
                    {
                        _spatial.ForgetLiveEntity(record.ServerGuid);
                    }
                }
                catch (Exception error)
                {
                    (failures ??= new List<Exception>()).Add(error);
                }
            }
            finally
            {
                _logicalTeardownDepth--;
            }
        }

        if (failures is not null)
            throw new AggregateException("One or more pending live-entity teardowns failed again.", failures);
        return completed;
    }

    private void RefreshRecord(
        uint guid,
        WorldSession.EntitySpawn accepted,
        bool refreshPosition = false)
    {
        if (!_directory.TryGetActive(guid, out RuntimeEntityRecord canonical))
            return;
        _directory.RefreshSnapshot(canonical, accepted, refreshPosition);
    }

    private ulong AdvanceLifetimeMutation(uint serverGuid)
        => _directory.AdvanceLifetimeMutation(serverGuid);

    private ulong CurrentLifetimeMutation(uint serverGuid) =>
        _directory.CurrentLifetimeMutation(serverGuid);

    private static RuntimeEntityKey RequireProjectionKey(
        LiveEntityRecord record) =>
        record.ProjectionKey ?? record.Canonical.Key
        ?? throw new InvalidOperationException(
            $"Live entity 0x{record.ServerGuid:X8}/{record.Generation} has no materialized projection key.");

    private bool IsCurrentProjectionOperation(
        uint serverGuid,
        LiveEntityRecord record,
        ulong projectionOperation) =>
        _projections.TryGetCurrent(serverGuid, out LiveEntityRecord? current)
        && ReferenceEquals(current, record)
        && record.ProjectionMutationVersion == projectionOperation;

    private static void ThrowAfterCommittedProjectionChange(
        uint serverGuid,
        Exception? spatialNotificationFailure,
        Exception? runtimeNotificationFailure)
    {
        if (spatialNotificationFailure is not null
            && runtimeNotificationFailure is not null)
        {
            throw new AggregateException(
                $"Projection change for live entity 0x{serverGuid:X8} committed, but spatial and runtime observers failed.",
                spatialNotificationFailure,
                runtimeNotificationFailure);
        }

        Exception? failure = spatialNotificationFailure ?? runtimeNotificationFailure;
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void AcknowledgeCelllessCanonicalCommit(
        RuntimeEntityRecord canonical)
    {
        if (!_directory.IsCurrent(canonical)
            || !_projections.TryGet(
                canonical,
                out LiveEntityRecord? record))
        {
            return;
        }

        SynchronizePhysicsBodyActiveState(record);
        RefreshSpatialRuntimeIndexes(record);
    }

    internal bool IsCurrentRecord(LiveEntityRecord record) =>
        _projections.TryGetCurrent(record.ServerGuid, out LiveEntityRecord? current)
        && ReferenceEquals(current, record);

    private static bool HasSpatialRuntimeProjection(LiveEntityRecord record) =>
        record.WorldEntity is not null
        && record.ProjectionKind is LiveEntityProjectionKind.World
        && record.IsSpatiallyProjected
        && record.IsSpatiallyVisible
        && record.FullCellId != 0;

    private void RefreshSpatialRuntimeIndexes(LiveEntityRecord record)
    {
        bool current = IsCurrentRecord(record);
        bool spatial = current && HasSpatialRuntimeProjection(record);
        if (record.ProjectionKey is not { } key)
        {
            if (record.WorldEntity is not null)
            {
                throw new InvalidOperationException(
                    $"Materialized live entity 0x{record.ServerGuid:X8}/{record.Generation} has no exact projection key.");
            }
            return;
        }

        _physics.AcknowledgeSpatialProjection(record.Canonical, spatial);

        RefreshSpatialPresentationIndexes(record, current, spatial, key);
    }

    private void RefreshSpatialPresentationIndexes(LiveEntityRecord record)
    {
        bool current = IsCurrentRecord(record);
        bool spatial = current && HasSpatialRuntimeProjection(record);
        if (record.ProjectionKey is not { } key)
        {
            if (record.WorldEntity is not null)
            {
                throw new InvalidOperationException(
                    $"Materialized live entity 0x{record.ServerGuid:X8}/{record.Generation} has no exact projection key.");
            }
            return;
        }

        RefreshSpatialPresentationIndexes(record, current, spatial, key);
    }

    private void RefreshSpatialPresentationIndexes(
        LiveEntityRecord record,
        bool current,
        bool spatial,
        RuntimeEntityKey key)
    {

        if (record.WorldEntity is not null)
        {
            if (spatial && record.AnimationRuntime is { } animation)
            {
                _spatialAnimations[key] = animation;
            }
            else if (current
                      || (record.AnimationRuntime is { } retainedAnimation
                          && _spatialAnimations.TryGetValue(key, out var indexedAnimation)
                          && ReferenceEquals(indexedAnimation, retainedAnimation)))
            {
                _spatialAnimations.Remove(key);
            }
        }

    }

    private void RemoveSpatialRuntimeIndexes(LiveEntityRecord record)
    {
        if (record.ProjectionKey is not { } key)
            return;

        _physics.RemoveSpatialProjection(record.Canonical);

        if (record.WorldEntity is not null
            && _spatialAnimations.TryGetValue(key, out var indexedAnimation)
            && ReferenceEquals(indexedAnimation, record.AnimationRuntime))
        {
            _spatialAnimations.Remove(key);
        }

    }

    private void OnSpatialVisibilityChanged(RuntimeEntityKey key, bool visible)
    {
        if (_spatial.IsLiveEntityProjectionResident(key) != visible)
            return;
        if (!_projections.TryGet(
                key,
                out LiveEntityRecord? record)
            || record.WorldEntity is not { } entity
            || entity.Id != key.LocalEntityId)
            return;

        uint serverGuid = record.ServerGuid;
        bool wasVisible = record.IsSpatiallyVisible;
        if (RequireProjectionKey(record) != key)
            return;
        if (_presentationOnlySpatialMutationDepth.ContainsKey(key))
        {
            record.IsSpatiallyVisible = visible;
            RefreshSpatialPresentationIndexes(record);
            RefreshPresentation(record);
            return;
        }
        bool wasOrdinaryRoot = _physics.IsSpatialRoot(record.Canonical);
        record.IsSpatiallyVisible = visible;
        bool isOrdinaryRoot = record.ProjectionKind is LiveEntityProjectionKind.World
            && record.IsSpatiallyProjected
            && visible
            && record.FullCellId != 0;
        if (_rebucketingGuid != serverGuid && wasOrdinaryRoot != isOrdinaryRoot)
        {
            if (isOrdinaryRoot)
                record.ResetObjectClockForEnterWorld(
                    (record.FinalPhysicsState & PhysicsStateFlags.Static) != 0);
            else
                record.SuspendObjectClock();
            SynchronizePhysicsBodyActiveState(record);
        }
        RefreshSpatialRuntimeIndexes(record);
        RefreshPresentation(record);
        if (_rebucketingGuid != serverGuid && wasVisible != visible)
            PublishProjectionVisibilityChanged(record, visible);
    }

    private void OnRuntimePhysicsCellCommitted(
        RuntimePhysicsCellCommit commit)
    {
        if (commit.Record.SpatialAuthorityVersion
                != commit.SpatialAuthorityVersion
            || !_directory.IsCurrent(commit.Record)
            || !_projections.TryGet(
                commit.Record,
                out LiveEntityRecord? projection)
            || projection.WorldEntity is null)
        {
            return;
        }

        RebucketLiveEntity(
            commit.Record.ServerGuid,
            commit.FullCellId);
    }

    private static void SynchronizePhysicsBodyActiveState(
        LiveEntityRecord record)
    {
        if (record.PhysicsBody is not { } body)
            return;
        if (record.ObjectClock.IsActive)
            body.TransientState |= TransientStateFlags.Active;
        else
            body.TransientState &= ~TransientStateFlags.Active;
    }

    private void PublishProjectionVisibilityChanged(LiveEntityRecord record, bool visible)
    {
        Delegate[] subscribers = ProjectionVisibilityChanged?.GetInvocationList()
            ?? Array.Empty<Delegate>();
        List<Exception>? failures = null;
        for (int i = 0; i < subscribers.Length; i++)
        {
            try
            {
                ((Action<LiveEntityRecord, bool>)subscribers[i])(record, visible);
            }
            catch (Exception error)
            {
                (failures ??= new List<Exception>()).Add(error);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(
                $"One or more projection observers failed for live entity 0x{record.ServerGuid:X8}.",
                failures);
        }
    }

    private void RefreshPresentation(LiveEntityRecord record)
    {
        if (record.WorldEntity is not { } entity)
            return;

        PhysicsStateFlags state = record.FinalPhysicsState;
        bool residenceVisible = record.MaterializationResidence is not
                LiveEntityMaterializationResidence.AwaitRuntimePlacement
            || record.IsSpatiallyProjected;
        entity.IsDrawVisible = residenceVisible
            && (state & (PhysicsStateFlags.NoDraw | PhysicsStateFlags.Hidden)) == 0;

        bool interactionVisible = record.IsSpatiallyVisible
            && record.ProjectionKind is LiveEntityProjectionKind.World
            && (state & PhysicsStateFlags.Hidden) == 0;
        if (interactionVisible)
            _projections.SetVisible(record, true);
        else
            _projections.SetVisible(record, false);
    }

    private void RetainTeardownRecord(LiveEntityRecord record)
    {
        _directory.RetainTeardown(record.Canonical);
        _projections.RetainTeardown(record);
    }

    private Exception? RetirePriorProjection(
        RuntimeEntityRecord canonical)
    {
        if (!_projections.TryGet(
                canonical,
                out LiveEntityRecord? projection))
        {
            return _entityObjects.RetireCanonicalOnly(canonical);
        }

        if (!_projections.RemoveActive(projection))
        {
            return new InvalidOperationException(
                $"Exact App projection for 0x{canonical.ServerGuid:X8}/{canonical.Incarnation} could not be retired.");
        }

        RetainTeardownRecord(projection);
        _logicalTeardownDepth++;
        try
        {
            try
            {
                TearDownRecord(projection);
                ReleaseTeardownRecord(projection);
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }
        finally
        {
            _logicalTeardownDepth--;
        }
    }

    private void TearDownCanonicalOnly(RuntimeEntityRecord canonical)
    {
        Exception? failure = _entityObjects.RetireCanonicalOnly(canonical);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void ReleaseTeardownRecord(LiveEntityRecord record)
    {
        _projections.ReleaseTeardown(record);
        _directory.ReleaseTeardown(record.Canonical);
        CompleteSessionClearIfConverged();
    }

    private bool HasPendingTeardown(uint serverGuid) =>
        _directory.HasPendingTeardown(serverGuid);

    private void CompleteSessionClearIfConverged()
    {
        if (!_sessionClearPendingFinalization
            || !_entityObjects.CompleteSessionClearIfConverged())
        {
            return;
        }

        _spatialAnimations.Clear();
        _physics.ClearSpatialWorksets();
        _projections.ClearConverged();
        _spatial.ClearLiveEntityLifetimeState();
        _sessionClearPendingFinalization = false;
    }

    private void TearDownRecord(
        LiveEntityRecord record,
        bool completeCanonical = true)
    {
        if (record.TeardownInProgress)
            throw new InvalidOperationException(
                $"Live entity 0x{record.ServerGuid:X8} teardown is already in progress.");

        record.TeardownInProgress = true;
        List<Exception>? failures = null;
        bool TryCleanup(Action cleanup)
        {
            try
            {
                cleanup();
                return true;
            }
            catch (Exception error)
            {
                (failures ??= new List<Exception>()).Add(error);
                return false;
            }
        }

        try
        {
            RemoveSpatialRuntimeIndexes(record);
            _physics.ClearProjectile(record.Canonical);

            if (!record.RuntimeComponentsTeardownCompleted)
            {
                record.RuntimeComponentsTeardownCompleted =
                    TryCleanup(() => _runtimeComponentLifecycle.TearDown(record));
            }

            if (record.WorldEntity is { } entity)
            {
                if (!record.SpatialProjectionTeardownCompleted)
                {
                    record.SpatialProjectionTeardownCompleted =
                        TryCleanup(() => _spatial.RemoveLiveEntityProjection(entity));
                }
                if (record.ResourcesRegistered
                    && TryCleanup(() => _resources.Unregister(entity)))
                {
                    record.ResourcesRegistered = false;
                }
            }

            if (failures is not null)
            {
                throw new AggregateException(
                    $"Live entity 0x{record.ServerGuid:X8} teardown failed.",
                    failures);
            }

            if (record.WorldEntity is not null)
                _ = RequireProjectionKey(record);
            if (completeCanonical)
            {
                _entityObjects.CompleteProjectionRetirement(
                    record.Canonical);
            }

            record.AnimationRuntime = null;
            record.EffectProfile = null;
            record.IsSpatiallyProjected = false;
            record.IsSpatiallyVisible = false;
            record.WorldSpawnPublished = false;
            record.InitialHydrationCompleted = false;
            record.CreateProjectionSynchronizationPending = false;
            record.ProjectionHydrationInProgress = false;
            record.ProjectionHydrationCreateVersion = 0UL;
            record.ProjectionHydrationRetryRequested = false;
            record.AppearanceProjectionSynchronizationPending = false;
            record.AppearanceHydrationInProgress = false;
            record.AppearanceHydrationVersion = 0UL;
            record.AppearanceHydrationRetryRequested = false;
            record.WorldEntity = null;
        }
        finally
        {
            record.TeardownInProgress = false;
        }
    }

}
