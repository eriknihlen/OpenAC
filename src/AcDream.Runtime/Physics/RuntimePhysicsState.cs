using System.Collections.Immutable;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Physics;

public readonly record struct RuntimePhysicsOwnershipSnapshot(
    int LandblockCount,
    int RetainedShadowRegistrationCount,
    int SpatialRootCount,
    int SpatialRemoteCount,
    int SpatialProjectileCount,
    int SetPositionOperationCount,
    int AwaitingSetPositionPreparationCount,
    int DeferredSetPositionCount,
    int PendingSetPositionHostAcknowledgementCount,
    int LostCellDeadlineCount,
    int LostCellDeadlineNodeCount,
    int LostCellDeadlineIndexCount,
    int ExpiredLostCellCount,
    int ExpiredLostCellIndexCount,
    int DeferredSetPositionBucketCount,
    int DeferredSetPositionBucketOrderCount,
    int UnboundDeferredSetPositionCellCount,
    int UnboundDeferredSetPositionCellOrderCount,
    int PreparedSetPositionMoverCount,
    int CollisionReportOwnerCount,
    int TrackedCollisionObjectCount,
    int CollisionReportReversePeerCount,
    int CollisionReportObserverCount,
    int PendingCollisionReportCount,
    int LeavingCollisionReportOwnerCount,
    int CollisionReportAdmissionBlockedOwnerCount,
    int PendingCollisionSetPositionDispatchCount,
    int PendingShadowSetPositionDispatchCount,
    bool IsCollisionReportDispatching,
    int CollisionPrefixQuiescenceCount,
    int PendingCollisionPrefixProjectionCount,
    int CollisionPrefixMutationCount,
    int CommittedCollisionPrefixMutationCount,
    int CollisionAdmissionCount,
    int CollisionGenerationCount,
    bool OwnsProductionDataCache,
    bool IsDisposed)
{
    public bool IsConverged =>
        IsDisposed
        && LandblockCount == 0
        && RetainedShadowRegistrationCount == 0
        && SpatialRootCount == 0
        && SpatialRemoteCount == 0
        && SpatialProjectileCount == 0
        && SetPositionOperationCount == 0
        && AwaitingSetPositionPreparationCount == 0
        && DeferredSetPositionCount == 0
        && PendingSetPositionHostAcknowledgementCount == 0
        && LostCellDeadlineCount == 0
        && LostCellDeadlineNodeCount == 0
        && LostCellDeadlineIndexCount == 0
        && ExpiredLostCellCount == 0
        && ExpiredLostCellIndexCount == 0
        && DeferredSetPositionBucketCount == 0
        && DeferredSetPositionBucketOrderCount == 0
        && UnboundDeferredSetPositionCellCount == 0
        && UnboundDeferredSetPositionCellOrderCount == 0
        && PreparedSetPositionMoverCount == 0
        && CollisionReportOwnerCount == 0
        && TrackedCollisionObjectCount == 0
        && CollisionReportReversePeerCount == 0
        && CollisionReportObserverCount == 0
        && PendingCollisionReportCount == 0
        && LeavingCollisionReportOwnerCount == 0
        && CollisionReportAdmissionBlockedOwnerCount == 0
        && PendingCollisionSetPositionDispatchCount == 0
        && PendingShadowSetPositionDispatchCount == 0
        && !IsCollisionReportDispatching
        && CollisionPrefixQuiescenceCount == 0
        && PendingCollisionPrefixProjectionCount == 0
        && CollisionPrefixMutationCount == 0
        && CommittedCollisionPrefixMutationCount == 0
        && CollisionAdmissionCount == 0
        && CollisionGenerationCount == 0
        && OwnsProductionDataCache;
}

public readonly record struct RuntimePhysicsCellCommit(
    RuntimeEntityRecord Record,
    uint PreviousFullCellId,
    uint FullCellId,
    ulong SpatialAuthorityVersion);

public sealed record RuntimeLandblockCollisionAssets(
    uint LandblockId,
    TerrainSurface Terrain,
    IReadOnlyList<CellSurface> CellSurfaces,
    IReadOnlyList<PortalPlane> PortalPlanes,
    float WorldOffsetX,
    float WorldOffsetY,
    uint CurrentCellId);

public sealed class RuntimeCollisionAdmission
{
    internal RuntimeCollisionAdmission(
        RuntimePhysicsState owner,
        uint landblockId,
        ulong generation,
        ulong previousGeneration)
    {
        Owner = owner;
        LandblockId = landblockId;
        Generation = generation;
        PreviousGeneration = previousGeneration;
    }

    internal RuntimePhysicsState Owner { get; }
    internal bool AssetsPrepared { get; set; }
    internal bool Completed { get; set; }
    public uint LandblockId { get; }
    public ulong Generation { get; }
    internal ulong PreviousGeneration { get; }
}

internal enum RuntimeCollisionPrefixMutationKind : byte
{
    Activation,
    Demotion,
    Withdrawal,
}

internal sealed class RuntimeCollisionPrefixMutation
{
    internal required RuntimeCollisionPrefixMutationKind Kind { get; init; }
    internal required uint LandblockId { get; init; }
    internal required ulong PreviousGeneration { get; init; }
    internal required ulong TargetGeneration { get; init; }
    internal ulong InvalidatedGeneration { get; init; }
    internal required RuntimeCollisionPrefixQuiescenceToken Quiescence
        { get; init; }
    internal RuntimeCollisionAdmission? Admission { get; init; }
    internal PreparedLandblockCollisionGeneration? Prepared { get; init; }
    internal RuntimeCollisionPrefixMutationPermission Permission { get; set; }
    internal bool EngineMutationCommitted { get; set; }
    internal bool CancellationRequested { get; set; }
    internal bool WasResident { get; set; }
    internal bool Ready { get; set; }
}

public readonly record struct RuntimeCollisionAcknowledgement(
    uint LandblockId,
    ulong Generation,
    bool WasResident,
    bool Ready);

public readonly record struct RuntimeCollisionMutationResult(
    RuntimeCollisionAcknowledgement Acknowledgement,
    bool Completed)
{
    public uint LandblockId => Acknowledgement.LandblockId;
    public ulong Generation => Acknowledgement.Generation;
    public bool WasResident => Acknowledgement.WasResident;
    public bool Ready => Acknowledgement.Ready;
}

public readonly record struct RuntimeCollisionGenerationCommit(
    RuntimeCollisionAcknowledgement Acknowledgement,
    uint[] DirtyRetainedOwnerIds,
    bool EngineCommitted,
    bool Completed)
{
    public bool Committed => Completed;
}

public readonly record struct RuntimeCollisionGenerationCommitted(
    uint LandblockId,
    ulong Generation,
    bool Ready);

internal sealed class PreparedLandblockCollisionGeneration : IDisposable
{
    internal const int MaxConcurrentCollisionPreparations = 256;
    private readonly RuntimePhysicsState _owner;
    private readonly RuntimeCollisionAdmission _admission;
    private readonly List<uint> _retainedOwnerIds = new();
    private readonly HashSet<uint> _retainedOwnerSet = new();
    private ShadowObjectRegistry.RetainedRefloodOwnerScan? _retainedOwnerScan;
    private PhysicsEngine.LandblockReplacementBuilder? _sealBuilder;
    private PhysicsEngine.PreparedPhysicsEngineLandblock? _sealedReplacement;
    private bool _disposed;

    internal PreparedLandblockCollisionGeneration(
        RuntimePhysicsState owner,
        RuntimeCollisionAdmission admission,
        PhysicsEngine.CollisionStagingBuilder stagingBuilder,
        long sequence)
    {
        _owner = owner;
        _admission = admission;
        ArgumentNullException.ThrowIfNull(stagingBuilder);
        DataCache = stagingBuilder.StagingCache;
        Engine = stagingBuilder.StagingEngine;
        Sequence = sequence;
    }

    internal PhysicsDataCache DataCache { get; }
    internal PhysicsEngine Engine { get; }
    internal long Sequence { get; }
    internal uint[] GfxObjectIds { get; private set; } = Array.Empty<uint>();
    internal uint[] SetupIds { get; private set; } = Array.Empty<uint>();
    internal bool IsDisposed => _disposed;
    internal bool RetainedOwnerCaptureComplete { get; private set; }
    internal bool IsSealed => _sealedReplacement is not null;
    internal bool IsReadyForActivation => _sealedReplacement is not null;

    internal RuntimeCollisionPreparationStep AdvanceStagingClone()
    {
        EnsureUsable();
        return new RuntimeCollisionPreparationStep(
            Completed: true,
            WorkUnits: 0);
    }

    internal bool Matches(
        RuntimePhysicsState owner,
        RuntimeCollisionAdmission admission) =>
        ReferenceEquals(_owner, owner)
        && ReferenceEquals(_admission, admission);

    internal void SetAssetClosure(uint[] gfxObjectIds, uint[] setupIds)
    {
        EnsureUsable();
        GfxObjectIds = gfxObjectIds
            ?? throw new ArgumentNullException(nameof(gfxObjectIds));
        SetupIds = setupIds ?? throw new ArgumentNullException(nameof(setupIds));
    }

    internal void RefreshRetainedOwner(uint ownerId)
    {
        EnsureUsable();
        EnsureRetainedOwner(ownerId);
        _sealBuilder?.RefreshRetainedOwner(ownerId);
    }

    internal RuntimeCollisionOwnerCaptureStep AdvanceRetainedOwnerCapture()
    {
        EnsureUsable();
        if (RetainedOwnerCaptureComplete)
        {
            return new RuntimeCollisionOwnerCaptureStep(
                Completed: true,
                Restarted: false,
                HasOwner: false,
                OwnerId: 0u);
        }
        _retainedOwnerScan ??= _owner.Engine.ShadowObjects
            .CreateRetainedRefloodOwnerScan(_admission.LandblockId);
        ShadowObjectRegistry.RetainedRefloodOwnerScanStep step =
            _retainedOwnerScan.Advance();
        if (step.HasOwner)
            EnsureRetainedOwner(step.OwnerId);
        if (step.Completed)
        {
            _retainedOwnerScan.Dispose();
            _retainedOwnerScan = null;
            RetainedOwnerCaptureComplete = true;
        }
        return new RuntimeCollisionOwnerCaptureStep(
            RetainedOwnerCaptureComplete,
            Restarted: false,
            step.HasOwner,
            step.OwnerId);
    }

    internal IReadOnlyList<uint> RetainedOwnerIds
    {
        get
        {
            EnsureUsable();
            if (!RetainedOwnerCaptureComplete)
            {
                throw new InvalidOperationException(
                    "Retained collision-owner capture is incomplete.");
            }
            return _retainedOwnerIds;
        }
    }

    internal void ResetRetainedOwnerCapture()
    {
        EnsureUsable();
        _retainedOwnerScan?.Dispose();
        _retainedOwnerScan = null;
        _retainedOwnerIds.Clear();
        _retainedOwnerSet.Clear();
        RetainedOwnerCaptureComplete = false;
        _sealedReplacement = null;
        _sealBuilder?.Dispose();
        _sealBuilder = null;
    }

    internal RuntimeCollisionSealStep AdvanceSeal()
    {
        EnsureUsable();
        if (!RetainedOwnerCaptureComplete)
        {
            return new RuntimeCollisionSealStep(
                Completed: false,
                Restarted: true,
                WorkUnits: 0);
        }

        _sealBuilder ??= _owner.Engine.CreateLandblockReplacementBuilder(
            Engine,
            _admission.LandblockId,
            GfxObjectIds,
            SetupIds,
            _retainedOwnerIds);
        int before = _sealBuilder.WorkUnits;
        bool completed = _sealBuilder.Advance();
        int workUnits = _sealBuilder.WorkUnits - before;
        if (!completed)
        {
            return new RuntimeCollisionSealStep(
                Completed: false,
                Restarted: false,
                workUnits);
        }
        if (_sealBuilder.Prepared is null)
        {
            ResetRetainedOwnerCapture();
            return new RuntimeCollisionSealStep(
                Completed: false,
                Restarted: true,
                workUnits);
        }
        _sealedReplacement = _sealBuilder.Prepared;
        return new RuntimeCollisionSealStep(
            Completed: true,
            Restarted: false,
            workUnits);
    }

    internal PhysicsEngine.PreparedPhysicsEngineLandblock TakeSealedReplacement()
    {
        EnsureUsable();
        return _sealedReplacement
            ?? throw new InvalidOperationException(
                "Collision generation must be sealed before activation.");
    }

    internal void MarkCommitted()
    {
        EnsureUsable();
        _sealBuilder = null;
        _sealedReplacement = null;
        _retainedOwnerIds.Clear();
        _retainedOwnerSet.Clear();
        _disposed = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Engine.Clear();
        _retainedOwnerScan?.Dispose();
        _retainedOwnerScan = null;
        _retainedOwnerIds.Clear();
        _retainedOwnerSet.Clear();
        _sealBuilder?.Dispose();
        _sealBuilder = null;
        _sealedReplacement = null;
        _disposed = true;
    }

    private void EnsureRetainedOwner(uint ownerId)
    {
        if (_retainedOwnerSet.Add(ownerId))
            _retainedOwnerIds.Add(ownerId);
    }

    private void EnsureUsable()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PreparedLandblockCollisionGeneration));
    }
}

internal readonly record struct RuntimeCollisionOwnerCaptureStep(
    bool Completed,
    bool Restarted,
    bool HasOwner,
    uint OwnerId);

internal readonly record struct RuntimeCollisionPreparationStep(
    bool Completed,
    int WorkUnits);

internal readonly record struct RuntimeCollisionSealStep(
    bool Completed,
    bool Restarted,
    int WorkUnits);

public sealed class RuntimePhysicsState : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly IGameRuntimeClock? _gameClock;
    private readonly Dictionary<RuntimeEntityKey, RuntimeEntityRecord>
        _spatialRoots = new();
    private readonly Dictionary<RuntimeEntityKey, IRuntimeRemoteMotion>
        _spatialRemotes = new();
    private readonly Dictionary<RuntimeEntityKey, IRuntimeProjectile>
        _spatialProjectiles = new();
    private readonly Dictionary<uint, ulong> _collisionGenerations = new();
    private readonly Dictionary<uint, RuntimeCollisionAdmission>
        _collisionAdmissions = new();
    private readonly Dictionary<uint, PreparedLandblockCollisionGeneration>
        _preparedCollisionGenerations = new();
    private readonly Dictionary<uint, RuntimeCollisionPrefixMutation>
        _collisionPrefixMutations = new();
    private int _collisionMutationThreadId;
    private long _nextCollisionPreparationSequence;
    private ulong _collisionWorldAuthority = 1UL;
    private uint _worldFrameCenterLandblockId;
    private bool _localPlayerCreateObserved;
    private readonly List<Action<RuntimeCollisionGenerationCommitted>>
        _collisionGenerationCommittedObservers = new();
    private bool _disposed;

    internal bool IsDisposed => _disposed;

    public event Action<RuntimePhysicsCellCommit>? CellCommitted;
    public event Action<RuntimeCollisionGenerationCommitted>?
        CollisionGenerationCommitted
    {
        add
        {
            if (value is not null)
                _collisionGenerationCommittedObservers.Add(value);
        }
        remove
        {
            if (value is not null)
                _collisionGenerationCommittedObservers.Remove(value);
        }
    }

    internal RuntimePhysicsState(
        RuntimeEntityDirectory entities,
        PhysicsDataCache? dataCache = null,
        TimeProvider? timeProvider = null,
        IGameRuntimeClock? gameClock = null)
    {
        Entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _gameClock = gameClock;
        DataCache = dataCache ?? PhysicsDataCache.CreateProduction();
        Engine = new PhysicsEngine
        {
            DataCache = DataCache,
        };
        CollisionReports = new RuntimeCollisionReportingState(
            Entities,
            Engine.ShadowObjects);
        SetPosition = new RuntimeSetPositionState(this, Entities);
    }

    internal RuntimePhysicsState(
        RuntimeEntityDirectory entities,
        PhysicsEngine engine,
        TimeProvider? timeProvider = null,
        IGameRuntimeClock? gameClock = null)
    {
        Entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _gameClock = gameClock;
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        DataCache = engine.DataCache
            ?? PhysicsDataCache.CreateProduction(engine.CollisionWorld);
        Engine.DataCache = DataCache;
        CollisionReports = new RuntimeCollisionReportingState(
            Entities,
            Engine.ShadowObjects);
        SetPosition = new RuntimeSetPositionState(this, Entities);
    }

    internal RuntimeEntityDirectory Entities { get; }
    public PhysicsEngine Engine { get; }
    public PhysicsDataCache DataCache { get; }
    internal RuntimeCollisionReportingState CollisionReports { get; }
    internal RuntimeSetPositionState SetPosition { get; }
    public int SpatialRootCount => _spatialRoots.Count;
    public int SpatialRemoteCount => _spatialRemotes.Count;
    public int SpatialProjectileCount => _spatialProjectiles.Count;
    internal double UtcNowSeconds =>
        (_timeProvider.GetUtcNow() - DateTimeOffset.UnixEpoch)
            .TotalSeconds;
    internal double MonotonicNowSeconds =>
        _timeProvider.GetTimestamp()
        / (double)_timeProvider.TimestampFrequency;
    internal double PlacementSimulationTime(double fallback) =>
        _gameClock?.SimulationTimeSeconds ?? fallback;

    internal void ObserveLocalWorldFrame(
        uint fullCellId,
        bool teleportAdvanced)
    {
        EnsureNotDisposed();
        if (fullCellId == 0u)
            return;
        if (_worldFrameCenterLandblockId == 0u || teleportAdvanced)
        {
            _worldFrameCenterLandblockId =
                (fullCellId & 0xFFFF0000u) | 0xFFFFu;
        }
    }

    internal uint WorldFrameCenterLandblockId => _worldFrameCenterLandblockId;

    internal void ObserveLocalPlayerCreate(uint fullCellId)
    {
        EnsureNotDisposed();
        _localPlayerCreateObserved = true;
        ObserveLocalWorldFrame(fullCellId, teleportAdvanced: false);
    }

    internal void ThrowIfWorldFrameUnreachable(uint fullCellId)
    {
        if (!_localPlayerCreateObserved || _worldFrameCenterLandblockId != 0u)
            return;

        throw new InvalidOperationException(
            "Runtime's world frame is unreachable: the local-player Create "
            + "was accepted without publishing a frame, so the placement for "
            + $"landblock 0x{fullCellId & 0xFFFF0000u:X8} can never resolve. "
            + "A local-player CreateObject must carry a non-zero landblock.");
    }

    internal bool TryGetWorldFrameOffset(
        uint fullCellId,
        out float worldOffsetX,
        out float worldOffsetY)
    {
        if (_worldFrameCenterLandblockId == 0u || fullCellId == 0u)
        {
            worldOffsetX = 0f;
            worldOffsetY = 0f;
            return false;
        }

        int centerX = (int)((_worldFrameCenterLandblockId >> 24) & 0xFFu);
        int centerY = (int)((_worldFrameCenterLandblockId >> 16) & 0xFFu);
        int landblockX = (int)((fullCellId >> 24) & 0xFFu);
        int landblockY = (int)((fullCellId >> 16) & 0xFFu);
        worldOffsetX = (landblockX - centerX) * 192f;
        worldOffsetY = (landblockY - centerY) * 192f;
        return true;
    }

    public RuntimePhysicsOwnershipSnapshot CaptureOwnership()
    {
        RuntimeSetPositionOwnershipSnapshot setPosition =
            SetPosition.CaptureOwnership();
        RuntimeCollisionReportingOwnershipSnapshot collisionReports =
            CollisionReports.CaptureOwnership();
        return new(
            Engine.LandblockCount,
            Engine.ShadowObjects.RetainedRegistrationCount,
            _spatialRoots.Count,
            _spatialRemotes.Count,
            _spatialProjectiles.Count,
            setPosition.ActiveOperationCount,
            setPosition.AwaitingPreparationCount,
            setPosition.DeferredCellCount,
            setPosition.PendingProjectionAcknowledgementCount,
            setPosition.LostDeadlineCount,
            setPosition.LostDeadlineNodeCount,
            setPosition.LostDeadlineIndexCount,
            setPosition.ExpiredLostCellCount,
            setPosition.ExpiredLostCellIndexCount,
            setPosition.DeferredBucketCount,
            setPosition.DeferredBucketOrderCount,
            setPosition.UnboundDeferredCellCount,
            setPosition.UnboundDeferredCellOrderCount,
            setPosition.PreparedMoverCount,
            collisionReports.OwnerCount,
            collisionReports.TrackedObjectCount,
            collisionReports.ReversePeerCount,
            collisionReports.ObserverCount,
            collisionReports.PendingReportCount,
            collisionReports.LeavingOwnerCount,
            collisionReports.AdmissionBlockedOwnerCount,
            collisionReports.PendingSetPositionDispatchCount,
            Engine.ShadowObjects.PendingSetPositionDispatchCount,
            collisionReports.IsDispatching,
            setPosition.CollisionPrefixQuiescenceCount,
            setPosition.PendingQuiescenceProjectionCount,
            _collisionPrefixMutations.Count,
            _collisionPrefixMutations.Values.Count(
                mutation => mutation.EngineMutationCommitted),
            _collisionAdmissions.Count,
            _collisionGenerations.Count,
            ReferenceEquals(Engine.DataCache, DataCache),
            _disposed);
    }

    public void AcknowledgeSpatialProjection(
        RuntimeEntityRecord record,
        bool spatial)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        if (record.Key is not { } key)
        {
            if (spatial)
            {
                throw new InvalidOperationException(
                    $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} cannot enter the physics workset without a local identity.");
            }
            return;
        }

        if (spatial && Entities.IsCurrent(record))
        {
            _spatialRoots[key] = record;
            if (record.RemoteMotion is { } remote)
                _spatialRemotes[key] = remote;
            else
                _spatialRemotes.Remove(key);
            if (record.Projectile is { } projectile)
                _spatialProjectiles[key] = projectile;
            else
                _spatialProjectiles.Remove(key);
            return;
        }

        RemoveSpatialProjection(record);
    }

    public void RefreshRemoteComponent(RuntimeEntityRecord record)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        if (record.Key is not { } key
            || !_spatialRoots.TryGetValue(key, out RuntimeEntityRecord? root)
            || !ReferenceEquals(root, record)
            || !Entities.IsCurrent(record))
        {
            RemoveSpatialRemote(record);
            return;
        }

        if (record.RemoteMotion is { } remote)
            _spatialRemotes[key] = remote;
        else
            _spatialRemotes.Remove(key);
    }

    public void RefreshProjectileComponent(RuntimeEntityRecord record)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        if (record.Key is not { } key
            || !_spatialRoots.TryGetValue(key, out RuntimeEntityRecord? root)
            || !ReferenceEquals(root, record)
            || !Entities.IsCurrent(record))
        {
            RemoveSpatialProjectile(record);
            return;
        }

        if (record.Projectile is { } projectile)
            _spatialProjectiles[key] = projectile;
        else
            _spatialProjectiles.Remove(key);
    }

    public IRuntimeProjectile BindProjectile(
        RuntimeEntityRecord record,
        PhysicsBody body,
        ProjectileCollisionSphere collisionSphere,
        Func<bool>? externalOwnerValid = null)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(body);
        EnsureCurrent(record);
        if (!collisionSphere.IsValid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(collisionSphere),
                "A Runtime projectile requires one valid prepared Setup sphere.");
        }
        if (!(externalOwnerValid?.Invoke() ?? true))
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} changed external ownership before projectile binding.");
        }
        if (record.Projectile is { } retained)
        {
            if (!ReferenceEquals(retained.Body, body)
                || !ReferenceEquals(record.PhysicsBody, body))
            {
                throw new InvalidOperationException(
                    $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} projectile changed its canonical physics body.");
            }

            retained.Body.State = record.FinalPhysicsState;
            RefreshProjectileComponent(record);
            return retained;
        }
        if (record.ProjectileBindingInProgress)
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} projectile binding is already in progress.");
        }
        if (!ReferenceEquals(record.PhysicsBody, body))
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} projectile must borrow its canonical physics body.");
        }

        ulong sessionVersion = Entities.SessionLifetimeVersion;
        Entities.SetProjectileBindingInProgress(record, true);
        try
        {
            var component = new RuntimeProjectile(body, collisionSphere);
            if (Entities.SessionLifetimeVersion != sessionVersion
                || !Entities.IsCurrent(record)
                || !ReferenceEquals(record.PhysicsBody, body)
                || record.Projectile is not null
                || !(externalOwnerValid?.Invoke() ?? true))
            {
                throw new InvalidOperationException(
                    $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} changed ownership during projectile binding.");
            }

            Entities.SetProjectile(record, component);
            body.State = record.FinalPhysicsState;
            SynchronizeBodyActiveState(record);
            RefreshProjectileComponent(record);
            return component;
        }
        finally
        {
            if (Entities.IsCurrent(record))
                Entities.SetProjectileBindingInProgress(record, false);
            else
                record.ProjectileBindingInProgress = false;
        }
    }

    public bool ClearProjectile(RuntimeEntityRecord record)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        if (record.Projectile is null)
            return false;

        Entities.SetProjectile(record, null);
        Entities.SetProjectileBindingInProgress(record, false);
        RefreshProjectileComponent(record);
        return true;
    }

    internal bool TryCommitAuthoritativeVector(
        RuntimeEntityRecord record,
        PhysicsBody body,
        System.Numerics.Vector3 velocity,
        System.Numerics.Vector3? angularVelocity,
        double currentTime,
        Func<bool>? externalOwnerValid = null)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(body);
        if (!IsFinite(velocity)
            || (angularVelocity is { } omega && !IsFinite(omega))
            || !double.IsFinite(currentTime)
            || !Entities.IsCurrent(record)
            || !ReferenceEquals(record.PhysicsBody, body)
            || !(externalOwnerValid?.Invoke() ?? true))
        {
            return false;
        }

        bool wasBodyActive =
            (body.TransientState & TransientStateFlags.Active) != 0;
        body.set_velocity(velocity);
        if ((record.FinalPhysicsState & PhysicsStateFlags.Static) != 0)
        {
            if (!wasBodyActive)
                body.TransientState &= ~TransientStateFlags.Active;
        }
        else
        {
            bool clockReactivated = record.ObjectClock.Activate();
            if (clockReactivated || !wasBodyActive)
                body.LastUpdateTime = currentTime;
        }

        if (angularVelocity is { } acceptedOmega)
            body.Omega = acceptedOmega;
        return Entities.IsCurrent(record)
            && ReferenceEquals(record.PhysicsBody, body)
            && (externalOwnerValid?.Invoke() ?? true);
    }

    public void SetRemoteMotion(
        RuntimeEntityRecord record,
        IRuntimeRemoteMotion runtime,
        Func<bool>? externalOwnerValid = null)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(runtime);
        EnsureCurrent(record);
        if (!(externalOwnerValid?.Invoke() ?? true))
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} changed external ownership before remote-motion binding.");
        }
        if (record.RemoteMotionBindingInProgress)
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} remote-motion binding is already in progress.");
        }

        PhysicsBody candidateBody = runtime.Body
            ?? throw new InvalidOperationException(
                "A remote-motion runtime returned no physics body.");
        if (ReferenceEquals(record.RemoteMotion, runtime))
        {
            if (!ReferenceEquals(record.PhysicsBody, candidateBody))
            {
                throw new InvalidOperationException(
                    $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} remote motion changed its canonical physics body.");
            }
            candidateBody.State = record.FinalPhysicsState;
            SynchronizeBodyActiveState(record);
            RefreshRemoteComponent(record);
            return;
        }
        if (record.PhysicsBodyAcquisitionInProgress
            && record.PhysicsBody is null)
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} cannot bind remote motion during physics-body acquisition.");
        }
        if (record.PhysicsBody is { } canonicalBody
            && !ReferenceEquals(canonicalBody, candidateBody))
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} cannot replace its canonical physics body.");
        }
        if (record.RequiresRemotePlacementRuntime
            && runtime is not IRuntimeRemotePlacement)
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} cannot discard its remote-placement contract.");
        }

        ulong sessionVersion = Entities.SessionLifetimeVersion;
        PhysicsBody? expectedBody = record.PhysicsBody;
        IRuntimeRemoteMotion? expectedRuntime = record.RemoteMotion;
        bool expectedPlacementContract =
            record.RequiresRemotePlacementRuntime;
        Func<AcDream.Core.Physics.Motion.IPhysicsObjHost?> readPhysicsHost =
            () => Entities.IsCurrent(record)
                && ReferenceEquals(record.RemoteMotion, runtime)
                    ? record.PhysicsHost
                    : null;
        Func<uint> readCell = () => record.FullCellId;
        Action<uint> writeCell = cellId =>
            CommitCanonicalCell(record, runtime, cellId);

        Entities.SetRemoteMotionBindingInProgress(record, true);
        try
        {
            if (runtime is IRuntimeCanonicalPhysicsConsumer canonicalConsumer)
            {
                canonicalConsumer.BindCanonicalRuntime(
                    readPhysicsHost,
                    readCell,
                    writeCell);
            }
            else
            {
                bool consumesHost = runtime is IRuntimePhysicsHostConsumer;
                bool consumesCell = runtime is IRuntimeCanonicalCellConsumer;
                if (consumesHost && consumesCell)
                {
                    throw new InvalidOperationException(
                        "A remote runtime that consumes both canonical host and cell identity must bind them atomically.");
                }
                if (runtime is IRuntimePhysicsHostConsumer hostConsumer)
                    hostConsumer.BindPhysicsHost(readPhysicsHost);
                if (runtime is IRuntimeCanonicalCellConsumer cellConsumer)
                    cellConsumer.BindCanonicalCell(readCell, writeCell);
            }

            if (Entities.SessionLifetimeVersion != sessionVersion
                || !Entities.IsCurrent(record)
                || !ReferenceEquals(record.PhysicsBody, expectedBody)
                || !ReferenceEquals(record.RemoteMotion, expectedRuntime)
                || record.RequiresRemotePlacementRuntime
                    != expectedPlacementContract
                || !(externalOwnerValid?.Invoke() ?? true))
            {
                throw new InvalidOperationException(
                    $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} changed ownership during remote-motion binding.");
            }

            Entities.SetRequiresRemotePlacementRuntime(
                record,
                expectedPlacementContract
                    || runtime is IRuntimeRemotePlacement);
            if (expectedBody is null)
                InitializeNewPhysicsBody(record, candidateBody);
            Entities.SetRemoteMotion(record, runtime);
            Entities.SetPhysicsBody(record, candidateBody);
            candidateBody.State = record.FinalPhysicsState;
            SynchronizeBodyActiveState(record);
            RefreshRemoteComponent(record);
        }
        finally
        {
            if (Entities.IsCurrent(record))
            {
                Entities.SetRemoteMotionBindingInProgress(record, false);
            }
        }
    }

    internal RemoteMotion GetOrCreateRemoteMotion(
        RuntimeEntityRecord record,
        Func<bool>? externalOwnerValid = null)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        EnsureCurrent(record);
        if (record.RemoteMotion is { } retained)
        {
            return retained as RemoteMotion
                ?? throw new InvalidOperationException(
                    $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} owns a non-production remote-motion component.");
        }

        var created = new RemoteMotion(record.PhysicsBody);
        created.Movement.ActivatePhysicsObject = () =>
            TryActivateOrdinaryObject(record, created);
        SetRemoteMotion(record, created, externalOwnerValid);
        return created;
    }

    internal bool TryActivateOrdinaryObject(
        RuntimeEntityRecord record,
        IRuntimeRemoteMotion runtime,
        Func<bool>? externalOwnerValid = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(runtime);
        if (!Entities.IsCurrent(record)
            || !ReferenceEquals(record.RemoteMotion, runtime)
            || (record.FinalPhysicsState & PhysicsStateFlags.Static) != 0
            || !(externalOwnerValid?.Invoke() ?? true))
        {
            return false;
        }

        record.ObjectClock.Activate();
        runtime.Body.TransientState |= TransientStateFlags.Active;
        return true;
    }

    public PhysicsBody GetOrCreatePhysicsBody(
        RuntimeEntityRecord record,
        Func<RuntimeEntityRecord, PhysicsBody> factory,
        Func<bool>? externalOwnerValid = null)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(factory);
        EnsureCurrent(record);
        if (!(externalOwnerValid?.Invoke() ?? true))
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} changed external ownership before physics-body acquisition.");
        }
        if (record.PhysicsBody is { } retained)
            return retained;
        if (record.PhysicsBodyAcquisitionInProgress)
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} physics-body acquisition is already in progress.");
        }

        Entities.SetPhysicsBodyAcquisitionInProgress(record, true);
        try
        {
            PhysicsBody candidate = factory(record)
                ?? throw new InvalidOperationException(
                    "Physics-body factory returned null.");
            if (!Entities.IsCurrent(record)
                || !(externalOwnerValid?.Invoke() ?? true))
            {
                throw new InvalidOperationException(
                    $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} changed ownership during physics-body acquisition.");
            }
            if (record.PhysicsBody is { } concurrentlyBound)
            {
                if (ReferenceEquals(concurrentlyBound, candidate))
                    return concurrentlyBound;
                throw new InvalidOperationException(
                    $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} acquired two physics bodies within one incarnation.");
            }

            InitializeNewPhysicsBody(record, candidate);
            Entities.SetPhysicsBody(record, candidate);
            candidate.State = record.FinalPhysicsState;
            SynchronizeBodyActiveState(record);
            return candidate;
        }
        finally
        {
            if (Entities.IsCurrent(record))
                Entities.SetPhysicsBodyAcquisitionInProgress(record, false);
            else
                record.PhysicsBodyAcquisitionInProgress = false;
        }
    }

    public void InstallPhysicsHost(
        RuntimeEntityRecord record,
        AcDream.Core.Physics.Motion.IPhysicsObjHost host,
        Func<bool>? externalOwnerValid = null)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(host);
        EnsureCurrent(record);
        if (host.Id != record.ServerGuid)
        {
            throw new ArgumentException(
                "A physics host must match its runtime entity GUID.",
                nameof(host));
        }
        if (!(externalOwnerValid?.Invoke() ?? true))
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} changed external ownership before physics-host installation.");
        }
        if (record.PhysicsHost is not null)
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} already owns its incarnation-stable physics host.");
        }

        Entities.SetPhysicsHost(record, host);
        if (!Entities.IsCurrent(record)
            || !(externalOwnerValid?.Invoke() ?? true))
        {
            if (ReferenceEquals(record.PhysicsHost, host))
                record.PhysicsHost = null;
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} changed ownership during physics-host installation.");
        }
    }

    public EntityPhysicsHost InstallOrRebindPhysicsHost(
        RuntimeEntityRecord record,
        EntityPhysicsHost configuration,
        Func<bool>? externalOwnerValid = null)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureCurrent(record);
        if (!(externalOwnerValid?.Invoke() ?? true))
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} changed external ownership before physics-host composition.");
        }

        if (record.PhysicsHost is null)
        {
            InstallPhysicsHost(record, configuration, externalOwnerValid);
            return configuration;
        }
        if (record.PhysicsHost is not EntityPhysicsHost existing)
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} owns an incompatible physics-host implementation.");
        }

        existing.RebindFrom(configuration);
        if (!Entities.IsCurrent(record)
            || !(externalOwnerValid?.Invoke() ?? true))
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} changed ownership during physics-host composition.");
        }
        return existing;
    }

    public EntityPhysicsHost SelectStablePhysicsHostWithoutRebind(
        RuntimeEntityRecord record,
        EntityPhysicsHost configuration,
        Func<bool>? externalOwnerValid = null)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureCurrent(record);
        if (!(externalOwnerValid?.Invoke() ?? true))
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} changed external ownership during physics-host preparation.");
        }

        return record.PhysicsHost switch
        {
            null => configuration,
            EntityPhysicsHost existing => existing,
            _ => throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} owns an incompatible physics-host implementation."),
        };
    }

    public bool TryGetPhysicsHost(
        uint serverGuid,
        out AcDream.Core.Physics.Motion.IPhysicsObjHost host)
    {
        EnsureNotDisposed();
        if (Entities.TryGetActive(serverGuid, out RuntimeEntityRecord record)
            && record.PhysicsHost is { } existing)
        {
            host = existing;
            return true;
        }

        host = null!;
        return false;
    }

    private Func<uint, AcDream.Core.Physics.Motion.IPhysicsObjHost?>?
        _objectTableHostResolver;

    public void BindObjectTableHostResolver(
        Func<uint, AcDream.Core.Physics.Motion.IPhysicsObjHost?>? resolver)
    {
        EnsureNotDisposed();
        _objectTableHostResolver = resolver;
    }

    public AcDream.Core.Physics.Motion.IPhysicsObjHost? ResolveObjectTableHost(
        uint serverGuid)
    {
        EnsureNotDisposed();
        if (_objectTableHostResolver is { } resolver)
            return resolver(serverGuid);
        return TryGetPhysicsHost(serverGuid, out var host) ? host : null;
    }

    public bool ClearRemoteMotion(RuntimeEntityRecord record)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        if (!Entities.IsCurrent(record)
            || record.RemoteMotion is null)
        {
            return false;
        }

        Entities.SetRemoteMotion(record, null);
        RefreshRemoteComponent(record);
        return true;
    }

    public void RemoveSpatialProjection(RuntimeEntityRecord record)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        if (record.Key is not { } key)
            return;

        if (_spatialRoots.TryGetValue(key, out RuntimeEntityRecord? root)
            && ReferenceEquals(root, record))
        {
            _spatialRoots.Remove(key);
        }
        RemoveSpatialRemote(record);
        RemoveSpatialProjectile(record);
    }

    public bool IsSpatialRoot(RuntimeEntityRecord record) =>
        record.Key is { } key
        && Entities.IsCurrent(record)
        && _spatialRoots.TryGetValue(key, out RuntimeEntityRecord? indexed)
        && ReferenceEquals(indexed, record);

    public bool IsSpatialRemote(
        RuntimeEntityRecord record,
        IRuntimeRemoteMotion remote) =>
        IsSpatialRoot(record)
        && ReferenceEquals(record.RemoteMotion, remote)
        && record.Key is { } key
        && _spatialRemotes.TryGetValue(key, out IRuntimeRemoteMotion? indexed)
        && ReferenceEquals(indexed, remote);

    public bool IsSpatialProjectile(
        RuntimeEntityRecord record,
        IRuntimeProjectile projectile) =>
        IsSpatialRoot(record)
        && ReferenceEquals(record.Projectile, projectile)
        && record.Key is { } key
        && _spatialProjectiles.TryGetValue(key, out IRuntimeProjectile? indexed)
        && ReferenceEquals(indexed, projectile);

    public void CopySpatialRootsTo(List<RuntimeEntityRecord> destination)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        foreach ((RuntimeEntityKey key, RuntimeEntityRecord record)
                 in _spatialRoots)
        {
            if (record.Key == key
                && Entities.IsCurrent(record))
            {
                destination.Add(record);
            }
        }
    }

    public void CopySpatialRemotesTo(List<RuntimeEntityRecord> destination)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        foreach ((RuntimeEntityKey key, IRuntimeRemoteMotion remote)
                 in _spatialRemotes)
        {
            if (Entities.TryGetByLocalId(
                    key.LocalEntityId,
                    out RuntimeEntityRecord record)
                && record.Key == key
                && ReferenceEquals(record.RemoteMotion, remote)
                && IsSpatialRoot(record))
            {
                destination.Add(record);
            }
        }
    }

    public void CopySpatialProjectilesTo(List<RuntimeEntityRecord> destination)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        foreach ((RuntimeEntityKey key, IRuntimeProjectile projectile)
                 in _spatialProjectiles)
        {
            if (Entities.TryGetByLocalId(
                    key.LocalEntityId,
                    out RuntimeEntityRecord record)
                && record.Key == key
                && ReferenceEquals(record.Projectile, projectile)
                && IsSpatialRoot(record))
            {
                destination.Add(record);
            }
        }
    }

    internal bool CommitOrdinaryCell(
        RuntimeEntityRecord record,
        PhysicsBody body,
        ulong objectClockEpoch,
        uint fullCellId,
        Func<bool>? externalOwnerValid)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(body);
        bool IsExactOwner() =>
            IsSpatialRoot(record)
            && record.ObjectClockEpoch == objectClockEpoch
            && ReferenceEquals(record.PhysicsBody, body)
            && record.RemoteMotion is null
            && (externalOwnerValid?.Invoke() ?? true);
        return CommitCanonicalCell(
            record,
            fullCellId,
            IsExactOwner);
    }

    internal bool CommitProjectileCell(
        RuntimeEntityRecord record,
        IRuntimeProjectile projectile,
        ulong predictionAuthorityVersion,
        uint fullCellId,
        Func<bool>? externalOwnerValid)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(projectile);
        bool IsExactOwner() =>
            Entities.IsCurrent(record)
            && ReferenceEquals(record.Projectile, projectile)
            && ReferenceEquals(record.PhysicsBody, projectile.Body)
            && projectile.PredictionAuthorityVersion
                == predictionAuthorityVersion
            && (externalOwnerValid?.Invoke() ?? true);
        return CommitCanonicalCell(
            record,
            fullCellId,
            IsExactOwner);
    }

    public void ClearSpatialWorksets()
    {
        EnsureNotDisposed();
        _spatialRemotes.Clear();
        _spatialProjectiles.Clear();
        _spatialRoots.Clear();
    }

    internal void ResetSessionPhysics()
    {
        EnsureNotDisposed();
        foreach ((_, PreparedLandblockCollisionGeneration prepared) in
                 _preparedCollisionGenerations)
        {
            prepared.Dispose();
        }
        _preparedCollisionGenerations.Clear();
        _collisionPrefixMutations.Clear();
        _collisionAdmissions.Clear();
        SetPosition.ResetSession();
        CollisionReports.ResetSession();
        _worldFrameCenterLandblockId = 0u;
        _localPlayerCreateObserved = false;
        AdvanceCollisionWorldAuthority();
        Volatile.Write(ref _collisionMutationThreadId, 0);
    }

    internal RuntimeCollisionPrefixQuiescenceToken
        BeginCollisionPrefixQuiescence(
            uint landblockId,
            ulong collisionGeneration,
            bool includeOutdoorCells)
    {
        EnsureNotDisposed();
        EnsureCollisionMutationThread();
        if (landblockId == 0u)
            throw new ArgumentOutOfRangeException(nameof(landblockId));
        uint canonical = CanonicalLandblock(landblockId);
        return SetPosition.BeginCollisionPrefixQuiescence(
            canonical,
            collisionGeneration,
            includeOutdoorCells);
    }

    internal bool TryAcquireCollisionPrefixMutationPermission(
        in RuntimeCollisionPrefixQuiescenceToken token,
        out RuntimeCollisionPrefixMutationPermission permission)
    {
        EnsureNotDisposed();
        EnsureCollisionMutationThread();
        return SetPosition.TryAcquireCollisionPrefixMutationPermission(
            token,
            out permission);
    }

    internal bool IsCollisionPrefixMutationPermissionCurrent(
        in RuntimeCollisionPrefixMutationPermission permission)
    {
        EnsureNotDisposed();
        EnsureCollisionMutationThread();
        return SetPosition.IsCollisionPrefixMutationPermissionCurrent(
            permission);
    }

    internal bool CancelCollisionPrefixQuiescence(
        in RuntimeCollisionPrefixQuiescenceToken token,
        ulong successorGeneration = 0UL,
        bool successorReady = false)
    {
        EnsureNotDisposed();
        EnsureCollisionMutationThread();
        return SetPosition.CancelCollisionPrefixQuiescence(
            token,
            successorGeneration,
            successorReady);
    }

    public RuntimeCollisionAdmission BeginCollisionAdmission(
        uint landblockId)
    {
        EnsureNotDisposed();
        EnsureCollisionMutationThread();
        if (landblockId == 0u)
            throw new ArgumentOutOfRangeException(nameof(landblockId));
        uint canonical = CanonicalLandblock(landblockId);
        if (_collisionPrefixMutations.ContainsKey(canonical))
        {
            throw new InvalidOperationException(
                $"Collision prefix 0x{canonical:X8} is still completing its previous mutation transaction.");
        }
        ulong currentGeneration = _collisionGenerations.TryGetValue(
            canonical,
            out ulong current)
                ? current
                : 0UL;
        ulong previousGeneration = currentGeneration;
        AdvanceCollisionWorldAuthority();
        if (_collisionAdmissions.Remove(
                canonical,
                out RuntimeCollisionAdmission? superseded))
        {
            previousGeneration = superseded.PreviousGeneration;
            SetPosition.CancelCollisionGeneration(
                canonical,
                superseded.Generation);
            if (_preparedCollisionGenerations.Remove(
                    canonical,
                    out PreparedLandblockCollisionGeneration? prepared))
            {
                prepared.Dispose();
            }
        }
        ulong generation = checked(currentGeneration + 1UL);
        _collisionGenerations[canonical] = generation;
        var admission = new RuntimeCollisionAdmission(
            this,
            canonical,
            generation,
            previousGeneration);
        _collisionAdmissions[canonical] = admission;
        SetPosition.BeginCollisionGeneration(canonical, generation);
        return admission;
    }

    internal PreparedLandblockCollisionGeneration PrepareCollisionGeneration(
        RuntimeCollisionAdmission admission)
    {
        ValidateAdmission(admission);
        EnsureCollisionMutationThread();
        if (_preparedCollisionGenerations.Count
                >= PreparedLandblockCollisionGeneration
                    .MaxConcurrentCollisionPreparations
            && !_preparedCollisionGenerations.ContainsKey(
                admission.LandblockId))
        {
            throw new InvalidOperationException(
                "Too many collision generations are being prepared concurrently.");
        }
        PhysicsEngine.CollisionStagingBuilder stagingBuilder =
            Engine.CreateCollisionStagingBuilder(admission.LandblockId);
        var prepared = new PreparedLandblockCollisionGeneration(
            this,
            admission,
            stagingBuilder,
            checked(++_nextCollisionPreparationSequence));
        _preparedCollisionGenerations[admission.LandblockId] = prepared;
        return prepared;
    }

    internal RuntimeCollisionPreparationStep
        AdvanceCollisionGenerationPreparation(
        RuntimeCollisionAdmission admission,
        PreparedLandblockCollisionGeneration prepared)
    {
        ValidateAdmission(admission);
        EnsureCollisionMutationThread();
        ValidatePreparedGeneration(admission, prepared);
        RuntimeCollisionPreparationStep clone = prepared.AdvanceStagingClone();
        return clone;
    }

    internal bool CancelCollisionGeneration(
        RuntimeCollisionAdmission admission,
        PreparedLandblockCollisionGeneration? prepared = null)
    {
        EnsureNotDisposed();
        EnsureCollisionMutationThread();
        ArgumentNullException.ThrowIfNull(admission);
        if (!ReferenceEquals(admission.Owner, this))
        {
            throw new ArgumentException(
                "Collision admission belongs to another Runtime.",
                nameof(admission));
        }
        if (prepared is not null && !prepared.Matches(this, admission))
        {
            throw new ArgumentException(
                "Prepared collision generation belongs to another admission.",
                nameof(prepared));
        }

        if (_collisionPrefixMutations.TryGetValue(
                admission.LandblockId,
                out RuntimeCollisionPrefixMutation? mutation))
        {
            if (mutation.Kind is not RuntimeCollisionPrefixMutationKind.Activation
                || !ReferenceEquals(mutation.Admission, admission)
                || (prepared is not null
                    && !ReferenceEquals(mutation.Prepared, prepared)))
            {
                return false;
            }
            if (mutation.EngineMutationCommitted)
                return AdvanceCommittedActivation(mutation).Committed;

            mutation.CancellationRequested = true;
            bool previousReady = mutation.PreviousGeneration != 0UL
                && Engine.IsLandblockTerrainResident(
                    mutation.LandblockId);
            bool released = mutation.PreviousGeneration == 0UL
                ? SetPosition.CancelCollisionPrefixQuiescenceToUnavailable(
                    mutation.Quiescence)
                : CancelCollisionPrefixQuiescence(
                    mutation.Quiescence,
                    mutation.PreviousGeneration,
                    previousReady);
            if (!released)
            {
                return false;
            }
            _collisionPrefixMutations.Remove(mutation.LandblockId);
        }

        prepared?.Dispose();
        if (prepared is not null
            && _preparedCollisionGenerations.TryGetValue(
                admission.LandblockId,
                out PreparedLandblockCollisionGeneration? currentPrepared)
            && ReferenceEquals(currentPrepared, prepared))
        {
            _preparedCollisionGenerations.Remove(admission.LandblockId);
        }
        if (_collisionAdmissions.TryGetValue(
                admission.LandblockId,
                out RuntimeCollisionAdmission? current)
            && ReferenceEquals(current, admission))
        {
            _collisionAdmissions.Remove(admission.LandblockId);
            SetPosition.CancelCollisionGeneration(
                admission.LandblockId,
                admission.Generation);
            _collisionGenerations[admission.LandblockId] = checked(
                admission.Generation + 1UL);
            AdvanceCollisionWorldAuthority();
        }
        return true;
    }

    internal void StageCollisionAssets(
        RuntimeCollisionAdmission admission,
        PreparedLandblockCollisionGeneration prepared,
        RuntimeLandblockCollisionAssets assets)
    {
        ValidateAdmission(admission);
        EnsureCollisionMutationThread();
        ValidatePreparedGeneration(admission, prepared);
        ArgumentNullException.ThrowIfNull(assets);
        if (CanonicalLandblock(assets.LandblockId)
            != admission.LandblockId)
        {
            throw new ArgumentException(
                "Collision assets do not match their admission landblock.",
                nameof(assets));
        }
        if (admission.Completed)
        {
            throw new InvalidOperationException(
                "A completed collision admission cannot publish more assets.");
        }
        if (admission.AssetsPrepared)
        {
            throw new InvalidOperationException(
                "Collision assets were already prepared by this receipt.");
        }

        prepared.Engine.AddLandblock(
            admission.LandblockId,
            assets.Terrain,
            assets.CellSurfaces,
            assets.PortalPlanes,
            assets.WorldOffsetX,
            assets.WorldOffsetY);
        if ((assets.CurrentCellId & 0xFFFF0000u)
            == (admission.LandblockId & 0xFFFF0000u))
        {
            prepared.Engine.UpdatePlayerCurrCell(assets.CurrentCellId);
        }
        admission.AssetsPrepared = true;
    }

    internal RuntimeCollisionOwnerCaptureStep AdvanceCollisionRetainedOwnerCapture(
        RuntimeCollisionAdmission admission,
        PreparedLandblockCollisionGeneration prepared)
    {
        ValidateAdmission(admission);
        EnsureCollisionMutationThread();
        ValidatePreparedGeneration(admission, prepared);
        if (!prepared.AdvanceStagingClone().Completed)
        {
            return new RuntimeCollisionOwnerCaptureStep(
                Completed: false,
                Restarted: false,
                HasOwner: false,
                OwnerId: 0u);
        }
        return prepared.AdvanceRetainedOwnerCapture();
    }

    internal void RefreshCollisionRetainedOwner(
        RuntimeCollisionAdmission admission,
        PreparedLandblockCollisionGeneration prepared,
        uint ownerId)
    {
        ValidateAdmission(admission);
        EnsureCollisionMutationThread();
        ValidatePreparedGeneration(admission, prepared);
        prepared.RefreshRetainedOwner(ownerId);
    }

    internal void RestartCollisionRetainedOwnerCapture(
        RuntimeCollisionAdmission admission,
        PreparedLandblockCollisionGeneration prepared)
    {
        ValidateAdmission(admission);
        EnsureCollisionMutationThread();
        ValidatePreparedGeneration(admission, prepared);
        prepared.ResetRetainedOwnerCapture();
    }

    internal RuntimeCollisionSealStep AdvanceCollisionGenerationSeal(
        RuntimeCollisionAdmission admission,
        PreparedLandblockCollisionGeneration prepared)
    {
        ValidateAdmission(admission);
        EnsureCollisionMutationThread();
        ValidatePreparedGeneration(admission, prepared);
        if (!admission.AssetsPrepared)
        {
            throw new InvalidOperationException(
                "Collision generation cannot seal before its assets are prepared.");
        }
        return prepared.AdvanceSeal();
    }

    internal RuntimeCollisionGenerationCommit CommitCollisionGeneration(
        RuntimeCollisionAdmission admission,
        PreparedLandblockCollisionGeneration prepared)
    {
        EnsureNotDisposed();
        EnsureCollisionMutationThread();
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(prepared);

        if (_collisionPrefixMutations.TryGetValue(
                admission.LandblockId,
                out RuntimeCollisionPrefixMutation? pending))
        {
            if (pending.Kind is not RuntimeCollisionPrefixMutationKind.Activation
                || !ReferenceEquals(pending.Admission, admission)
                || !ReferenceEquals(pending.Prepared, prepared))
            {
                throw new InvalidOperationException(
                    "A different collision-prefix mutation owns this landblock.");
            }
            if (pending.EngineMutationCommitted)
                return AdvanceCommittedActivation(pending);
            if (pending.CancellationRequested)
                return PendingActivation(pending);
        }

        ValidateAdmission(admission);
        ValidatePreparedGeneration(admission, prepared);
        if (!admission.AssetsPrepared)
        {
            throw new InvalidOperationException(
                "Collision generation cannot commit before its assets are prepared.");
        }
        if (admission.Completed)
        {
            throw new InvalidOperationException(
                "Collision generation has already completed.");
        }

        if (!prepared.IsReadyForActivation
            || HasOlderPreparedGeneration(prepared))
        {
            if (!prepared.IsSealed)
            {
                throw new InvalidOperationException(
                    "Collision generation cannot activate before sealing.");
            }
            return new RuntimeCollisionGenerationCommit(
                new RuntimeCollisionAcknowledgement(
                    admission.LandblockId,
                    admission.Generation,
                    Engine.IsLandblockTerrainResident(admission.LandblockId),
                    Ready: false),
                Array.Empty<uint>(),
                EngineCommitted: false,
                Completed: false);
        }

        RuntimeCollisionPrefixMutation mutation;
        if (!_collisionPrefixMutations.TryGetValue(
                admission.LandblockId,
                out mutation!))
        {
            RuntimeCollisionPrefixQuiescenceToken token =
                BeginCollisionPrefixQuiescence(
                    admission.LandblockId,
                    admission.Generation,
                    includeOutdoorCells: true);
            mutation = new RuntimeCollisionPrefixMutation
            {
                Kind = RuntimeCollisionPrefixMutationKind.Activation,
                LandblockId = admission.LandblockId,
                PreviousGeneration = admission.PreviousGeneration,
                TargetGeneration = admission.Generation,
                Quiescence = token,
                Admission = admission,
                Prepared = prepared,
                WasResident = Engine.IsLandblockTerrainResident(
                    admission.LandblockId),
            };
            _collisionPrefixMutations.Add(admission.LandblockId, mutation);

        }

        if (!TryAcquireCollisionPrefixMutationPermission(
                mutation.Quiescence,
                out RuntimeCollisionPrefixMutationPermission permission))
        {
            return PendingActivation(mutation);
        }
        mutation.Permission = permission;

        if (!IsCollisionPrefixMutationPermissionCurrent(permission)
            || !prepared.IsReadyForActivation
            || HasOlderPreparedGeneration(prepared))
        {
            return PendingActivation(mutation);
        }

        PhysicsEngine.PreparedPhysicsEngineLandblock replacement =
            prepared.TakeSealedReplacement();
        Engine.CommitLandblockReplacement(replacement);
        AdvanceCollisionWorldAuthority();
        _preparedCollisionGenerations.Remove(admission.LandblockId);
        prepared.MarkCommitted();
        mutation.EngineMutationCommitted = true;
        mutation.Ready = Engine.IsLandblockTerrainResident(
            admission.LandblockId);
        mutation.WasResident = mutation.Ready;
        SetPosition.CommitCollisionGeneration(
            mutation.LandblockId,
            mutation.TargetGeneration,
            mutation.Ready);
        RuntimeCollisionGenerationCommit completed =
            AdvanceCommittedActivation(mutation);
        return completed;
    }

    private RuntimeCollisionGenerationCommit PendingActivation(
        RuntimeCollisionPrefixMutation mutation) => new(
        new RuntimeCollisionAcknowledgement(
            mutation.LandblockId,
            mutation.TargetGeneration,
            mutation.WasResident,
            Ready: mutation.EngineMutationCommitted && mutation.Ready),
        Array.Empty<uint>(),
        EngineCommitted: mutation.EngineMutationCommitted,
        Completed: false);

    private RuntimeCollisionGenerationCommit AdvanceCommittedActivation(
        RuntimeCollisionPrefixMutation mutation)
    {
        if (!mutation.EngineMutationCommitted)
            throw new InvalidOperationException(
                "Collision activation cannot release before its engine transaction commits.");

        bool completed = SetPosition.ReleaseCollisionPrefixAfterMutation(
            mutation.Quiescence,
            mutation.TargetGeneration,
            mutation.Ready);
        var acknowledgement = new RuntimeCollisionAcknowledgement(
            mutation.LandblockId,
            mutation.TargetGeneration,
            mutation.WasResident,
            mutation.Ready);
        if (!completed)
        {
            return new RuntimeCollisionGenerationCommit(
                acknowledgement,
                Array.Empty<uint>(),
                EngineCommitted: true,
                Completed: false);
        }

        RuntimeCollisionAdmission admission = mutation.Admission
            ?? throw new InvalidOperationException(
                "Collision activation lost its admission owner.");
        admission.Completed = true;
        if (_collisionAdmissions.TryGetValue(
                mutation.LandblockId,
                out RuntimeCollisionAdmission? current)
            && ReferenceEquals(current, admission))
        {
            _collisionAdmissions.Remove(mutation.LandblockId);
        }
        _collisionPrefixMutations.Remove(mutation.LandblockId);
        PublishCollisionGenerationCommitted(
            new RuntimeCollisionGenerationCommitted(
                acknowledgement.LandblockId,
                acknowledgement.Generation,
                acknowledgement.Ready));
        // Admission is closed → prefix admissible. Recover any deferred ops
        // under this landblock whose spawn cell is ready (unbound or stale
        // Expected) so dormant first-entry does not wait for a later Evaluate.
        _ = SetPosition.TryRecoverDeferredForLandblock(
            acknowledgement.LandblockId);
        return new RuntimeCollisionGenerationCommit(
            acknowledgement,
            Array.Empty<uint>(),
            EngineCommitted: true,
            Completed: true);
    }

    private static RuntimeCollisionMutationResult PendingRetirement(
        RuntimeCollisionPrefixMutation mutation) => new(
        new RuntimeCollisionAcknowledgement(
            mutation.LandblockId,
            mutation.TargetGeneration,
            mutation.WasResident,
            Ready: mutation.EngineMutationCommitted && mutation.Ready),
        Completed: false);

    private void CommitCollisionInvalidation(
        RuntimeCollisionPrefixMutation mutation)
    {
        _collisionGenerations[mutation.LandblockId] =
            mutation.TargetGeneration;
        SetPosition.CancelCollisionGeneration(
            mutation.LandblockId,
            mutation.InvalidatedGeneration);
        if (_collisionAdmissions.TryGetValue(
                mutation.LandblockId,
                out RuntimeCollisionAdmission? currentAdmission)
            && ReferenceEquals(currentAdmission, mutation.Admission))
        {
            _collisionAdmissions.Remove(mutation.LandblockId);
        }
        if (_preparedCollisionGenerations.TryGetValue(
                mutation.LandblockId,
                out PreparedLandblockCollisionGeneration? currentPrepared)
            && ReferenceEquals(currentPrepared, mutation.Prepared))
        {
            _preparedCollisionGenerations.Remove(mutation.LandblockId);
            currentPrepared.Dispose();
        }
    }

    public RuntimeCollisionMutationResult DemoteCollisionToTerrain(
        uint landblockId)
        => AdvanceCollisionRetirementMutation(
            landblockId,
            RuntimeCollisionPrefixMutationKind.Demotion);

    public RuntimeCollisionMutationResult WithdrawCollision(
        uint landblockId)
        => AdvanceCollisionRetirementMutation(
            landblockId,
            RuntimeCollisionPrefixMutationKind.Withdrawal);

    private RuntimeCollisionMutationResult AdvanceCollisionRetirementMutation(
        uint landblockId,
        RuntimeCollisionPrefixMutationKind kind)
    {
        EnsureNotDisposed();
        EnsureCollisionMutationThread();
        if (landblockId == 0u)
            throw new ArgumentOutOfRangeException(nameof(landblockId));
        uint canonical = CanonicalLandblock(landblockId);
        if (kind is RuntimeCollisionPrefixMutationKind.Activation)
            throw new ArgumentOutOfRangeException(nameof(kind));

        if (!_collisionPrefixMutations.TryGetValue(
                canonical,
                out RuntimeCollisionPrefixMutation? mutation))
        {
            ulong currentGeneration = _collisionGenerations.TryGetValue(
                canonical,
                out ulong current)
                    ? current
                    : 0UL;
            RuntimeCollisionAdmission? admission =
                _collisionAdmissions.GetValueOrDefault(canonical);
            ulong previousGeneration = admission?.PreviousGeneration
                ?? currentGeneration;
            ulong invalidatedGeneration = admission is not null
                    ? admission.Generation
                    : checked(currentGeneration + 1UL);
            ulong targetGeneration = checked(
                Math.Max(currentGeneration, invalidatedGeneration) + 1UL);
            SetPosition.BeginCollisionGeneration(
                canonical,
                targetGeneration);
            RuntimeCollisionPrefixQuiescenceToken token =
                BeginCollisionPrefixQuiescence(
                    canonical,
                    targetGeneration,
                    includeOutdoorCells:
                        kind is RuntimeCollisionPrefixMutationKind.Withdrawal);
            mutation = new RuntimeCollisionPrefixMutation
            {
                Kind = kind,
                LandblockId = canonical,
                PreviousGeneration = previousGeneration,
                InvalidatedGeneration = invalidatedGeneration,
                TargetGeneration = targetGeneration,
                Quiescence = token,
                Admission = admission,
                Prepared = _preparedCollisionGenerations.GetValueOrDefault(
                    canonical),
                WasResident = Engine.IsLandblockTerrainResident(canonical),
            };
            _collisionPrefixMutations.Add(canonical, mutation);
        }
        if (mutation.Kind != kind)
        {
            throw new InvalidOperationException(
                "A different collision-prefix mutation owns this landblock.");
        }

        if (!mutation.EngineMutationCommitted)
        {
            if (!TryAcquireCollisionPrefixMutationPermission(
                    mutation.Quiescence,
                    out RuntimeCollisionPrefixMutationPermission permission)
                || !IsCollisionPrefixMutationPermissionCurrent(permission))
            {
                return PendingRetirement(mutation);
            }
            mutation.Permission = permission;
            CommitCollisionInvalidation(mutation);

            if (kind is RuntimeCollisionPrefixMutationKind.Demotion)
                Engine.DemoteLandblockToTerrain(canonical);
            else
                Engine.RemoveLandblock(canonical);
            AdvanceCollisionWorldAuthority();
            mutation.EngineMutationCommitted = true;
            mutation.Ready = kind is RuntimeCollisionPrefixMutationKind.Demotion
                && Engine.IsLandblockTerrainResident(canonical);
            if (mutation.Ready)
            {
                SetPosition.CommitCollisionGeneration(
                    canonical,
                    mutation.TargetGeneration,
                    ready: true);
            }
        }

        bool completed = SetPosition.ReleaseCollisionPrefixAfterMutation(
            mutation.Quiescence,
            mutation.TargetGeneration,
            mutation.Ready);
        if (completed)
            _collisionPrefixMutations.Remove(canonical);
        return new RuntimeCollisionMutationResult(
            new RuntimeCollisionAcknowledgement(
                canonical,
                mutation.TargetGeneration,
                mutation.WasResident,
                mutation.Ready),
            completed);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        foreach ((_, PreparedLandblockCollisionGeneration prepared) in
                 _preparedCollisionGenerations)
        {
            prepared.Dispose();
        }
        _preparedCollisionGenerations.Clear();
        SetPosition.Dispose();
        CollisionReports.Dispose();
        Engine.Clear();
        _spatialRemotes.Clear();
        _spatialProjectiles.Clear();
        _spatialRoots.Clear();
        _collisionPrefixMutations.Clear();
        _collisionAdmissions.Clear();
        _collisionGenerations.Clear();
        _worldFrameCenterLandblockId = 0u;
        _localPlayerCreateObserved = false;
        CellCommitted = null;
        _collisionGenerationCommittedObservers.Clear();
        _objectTableHostResolver = null;
        _disposed = true;
    }

    private void CommitCanonicalCell(
        RuntimeEntityRecord record,
        IRuntimeRemoteMotion expectedRuntime,
        uint fullCellId)
    {
        _ = CommitCanonicalCell(
            record,
            fullCellId,
            () => Entities.IsCurrent(record)
                && ReferenceEquals(record.RemoteMotion, expectedRuntime));
    }

    private bool CommitCanonicalCell(
        RuntimeEntityRecord record,
        uint fullCellId,
        Func<bool> exactOwnerValid)
    {
        if (fullCellId == 0 || !exactOwnerValid())
            return false;
        if (fullCellId == record.FullCellId)
            return true;
        uint previousCellId = record.FullCellId;
        Entities.SetFullCell(
            record,
            fullCellId,
            (fullCellId & 0xFFFF0000u) | 0xFFFFu);
        CellCommitted?.Invoke(
            new RuntimePhysicsCellCommit(
                record,
                previousCellId,
                fullCellId,
                record.SpatialAuthorityVersion));
        return exactOwnerValid()
            && record.FullCellId == fullCellId;
    }

    private void RemoveSpatialRemote(RuntimeEntityRecord record)
    {
        if (record.Key is not { } key)
            return;
        if (_spatialRemotes.TryGetValue(
                key,
                out IRuntimeRemoteMotion? remote)
            && (record.RemoteMotion is null
                || ReferenceEquals(remote, record.RemoteMotion)
                || !Entities.IsCurrent(record)))
        {
            _spatialRemotes.Remove(key);
        }
    }

    private void RemoveSpatialProjectile(RuntimeEntityRecord record)
    {
        if (record.Key is not { } key)
            return;
        if (_spatialProjectiles.TryGetValue(
                key,
                out IRuntimeProjectile? projectile)
            && (record.Projectile is null
                || ReferenceEquals(projectile, record.Projectile)
                || !Entities.IsCurrent(record)))
        {
            _spatialProjectiles.Remove(key);
        }
    }

    private void EnsureNotDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private void EnsureCollisionMutationThread()
    {
        int current = Environment.CurrentManagedThreadId;
        int owner = Interlocked.CompareExchange(
            ref _collisionMutationThreadId,
            current,
            0);
        if (owner != 0 && owner != current)
        {
            throw new InvalidOperationException(
                "Collision generations must be staged and committed on one update thread.");
        }
    }

    private void EnsureCurrent(RuntimeEntityRecord record)
    {
        if (!Entities.IsCurrent(record))
        {
            throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} is not current.");
        }
    }

    private void ValidateAdmission(RuntimeCollisionAdmission admission)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(admission);
        if (!ReferenceEquals(admission.Owner, this)
            || !_collisionAdmissions.TryGetValue(
                admission.LandblockId,
                out RuntimeCollisionAdmission? current)
            || !ReferenceEquals(current, admission)
            || !_collisionGenerations.TryGetValue(
                admission.LandblockId,
                out ulong generation)
            || generation != admission.Generation)
        {
            throw new InvalidOperationException(
                "Collision admission is stale or belongs to another Runtime.");
        }
    }

    private void ValidatePreparedGeneration(
        RuntimeCollisionAdmission admission,
        PreparedLandblockCollisionGeneration prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ObjectDisposedException.ThrowIf(prepared.IsDisposed, prepared);
        if (!prepared.Matches(this, admission))
        {
            throw new InvalidOperationException(
                "Prepared collision generation is stale or belongs to another admission.");
        }
    }

    private void PublishCollisionGenerationCommitted(
        RuntimeCollisionGenerationCommitted committed)
    {
        for (int index = 0;
             index < _collisionGenerationCommittedObservers.Count;
             index++)
        {
            try
            {
                _collisionGenerationCommittedObservers[index](committed);
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError(
                    "Collision-generation commit observer failed after activation: {0}",
                    error);
            }
        }
    }

    internal bool TryPrepareSpatialRootAdmission(RuntimeEntityRecord record)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(record);
        if (record.Key is null || !Entities.IsCurrent(record))
            return false;
        _spatialRoots.EnsureCapacity(_spatialRoots.Count + 1);
        _spatialRemotes.EnsureCapacity(_spatialRemotes.Count + 1);
        _spatialProjectiles.EnsureCapacity(_spatialProjectiles.Count + 1);
        return Entities.IsCurrent(record);
    }

    internal ulong ExpectedCollisionGeneration(uint exactCellId)
    {
        uint landblockId = CanonicalLandblock(exactCellId);
        if (landblockId == 0u)
            return 0UL;
        if (_collisionAdmissions.TryGetValue(
                landblockId,
                out RuntimeCollisionAdmission? admission))
        {
            return admission.Generation;
        }
        return _collisionGenerations.TryGetValue(
                landblockId,
                out ulong generation)
            ? checked(generation + 1UL)
            : 1UL;
    }

    internal bool IsCollisionEvaluationPrefixAdmissible(uint exactCellId)
    {
        uint landblockId = CanonicalLandblock(exactCellId);
        return landblockId != 0u
            && !_collisionAdmissions.ContainsKey(landblockId)
            && !SetPosition.IsCollisionPrefixQuiescing(landblockId);
    }

    internal ulong CollisionGenerationAuthority(uint exactCellId)
    {
        uint landblockId = CanonicalLandblock(exactCellId);
        return landblockId != 0u
            && _collisionGenerations.TryGetValue(
                landblockId,
                out ulong generation)
                ? generation
                : 0UL;
    }

    internal ulong CollisionWorldAuthority => _collisionWorldAuthority;

    internal ulong ShadowWorldAuthority =>
        Engine.ShadowObjects.MutationRevision;

    internal ClientObjectTable? ObjectTable => Engine.Objects;

    internal ulong ObjectTableBindingAuthority =>
        Engine.ObjectsBindingRevision;

    internal ulong ObjectTableAuthority =>
        ObjectTable?.MutationRevision ?? 0UL;

    private void AdvanceCollisionWorldAuthority() =>
        _collisionWorldAuthority = checked(_collisionWorldAuthority + 1UL);

    internal void AdvanceCollisionQuiescenceAuthority() =>
        AdvanceCollisionWorldAuthority();

    internal bool TrySealCollisionEvaluationAuthority(
        in PhysicsSetPositionResult result,
        ulong expectedCollisionWorldAuthority,
        ulong expectedShadowWorldAuthority,
        ClientObjectTable? expectedObjectTable,
        ulong expectedObjectTableBindingAuthority,
        ulong expectedObjectTableAuthority,
        out RuntimeCollisionEvaluationAuthority authority)
    {
        authority = default;
        if (_collisionWorldAuthority != expectedCollisionWorldAuthority
            || ShadowWorldAuthority != expectedShadowWorldAuthority
            || !ReferenceEquals(ObjectTable, expectedObjectTable)
            || ObjectTableBindingAuthority
                != expectedObjectTableBindingAuthority
            || ObjectTableAuthority != expectedObjectTableAuthority)
        {
            return false;
        }

        var prefixes = new HashSet<uint>();
        var authorities = ImmutableArray.CreateBuilder<
            RuntimeCollisionGenerationAuthority>();
        void Add(uint cellId)
        {
            uint landblock = CanonicalLandblock(cellId);
            if (landblock == 0u || !prefixes.Add(landblock))
                return;
            authorities.Add(new RuntimeCollisionGenerationAuthority(
                landblock,
                CollisionGenerationAuthority(landblock)));
        }

        Add(result.CellId);
        if (!result.QueriedCellIds.IsDefaultOrEmpty)
        {
            foreach (uint cellId in result.QueriedCellIds)
                Add(cellId);
        }
        foreach (uint prefix in prefixes)
        {
            if (!IsCollisionEvaluationPrefixAdmissible(prefix))
                return false;
        }

        if (_collisionWorldAuthority != expectedCollisionWorldAuthority
            || ShadowWorldAuthority != expectedShadowWorldAuthority
            || !ReferenceEquals(ObjectTable, expectedObjectTable)
            || ObjectTableBindingAuthority
                != expectedObjectTableBindingAuthority
            || ObjectTableAuthority != expectedObjectTableAuthority)
        {
            return false;
        }
        authority = new RuntimeCollisionEvaluationAuthority(
            expectedCollisionWorldAuthority,
            expectedShadowWorldAuthority,
            expectedObjectTable,
            expectedObjectTableBindingAuthority,
            expectedObjectTableAuthority,
            authorities.ToImmutable());
        return true;
    }

    internal bool IsCollisionEvaluationAuthorityCurrent(
        in RuntimeCollisionEvaluationAuthority authority)
    {
        if (!authority.IsValid
            || _collisionWorldAuthority != authority.CollisionWorldAuthority
            || ShadowWorldAuthority != authority.ShadowWorldAuthority
            || !ReferenceEquals(ObjectTable, authority.ObjectTable)
            || ObjectTableBindingAuthority
                != authority.ObjectTableBindingAuthority
            || ObjectTableAuthority != authority.ObjectTableAuthority)
        {
            return false;
        }
        foreach (RuntimeCollisionGenerationAuthority generation
                 in authority.Generations)
        {
            if (generation.LandblockId == 0u
                || _collisionAdmissions.ContainsKey(generation.LandblockId)
                || SetPosition.IsCollisionPrefixQuiescing(
                    generation.LandblockId)
                || CollisionGenerationAuthority(generation.LandblockId)
                    != generation.Generation)
            {
                return false;
            }
        }
        return true;
    }

    internal bool IsCollisionEvaluationFatalAuthorityCurrent(
        in RuntimeCollisionEvaluationAuthority authority)
    {
        if (!authority.IsValid
            || _collisionWorldAuthority != authority.CollisionWorldAuthority
            || !ReferenceEquals(ObjectTable, authority.ObjectTable)
            || ObjectTableBindingAuthority
                != authority.ObjectTableBindingAuthority
            || ObjectTableAuthority != authority.ObjectTableAuthority)
        {
            return false;
        }
        foreach (RuntimeCollisionGenerationAuthority generation
                 in authority.Generations)
        {
            if (generation.LandblockId == 0u
                || _collisionAdmissions.ContainsKey(generation.LandblockId)
                || SetPosition.IsCollisionPrefixQuiescing(
                    generation.LandblockId)
                || CollisionGenerationAuthority(generation.LandblockId)
                    != generation.Generation)
            {
                return false;
            }
        }
        return true;
    }

    internal bool HandleSetPositionCollisions(
        RuntimeEntityRecord record,
        ulong positionAuthorityVersion,
        ulong spatialAuthorityVersion,
        ulong velocityAuthorityVersion,
        double physicsTime,
        bool previousContact,
        bool previousOnWalkable,
        in PhysicsSetPositionCollisionReport report)
    {
        if (!Entities.IsCurrent(record)
            || record.PositionAuthorityVersion != positionAuthorityVersion
            || record.SpatialAuthorityVersion != spatialAuthorityVersion
            || record.PhysicsBody is not { } body)
        {
            return false;
        }

        _ = previousContact;
        _ = previousOnWalkable;
        bool current = HandleSetPositionCollisionReports(
            record,
            positionAuthorityVersion,
            spatialAuthorityVersion,
            physicsTime,
            previousContact: false,
            previousOnWalkable: false,
            collidedWithEnvironment: report.CollidedWithEnvironment,
            collidedObjectIds: report.CollidedObjectIds,
            collisionHandlerResult: out bool collisionHandlerResult);
        if (!current)
            return collisionHandlerResult;
        if (velocityAuthorityVersion != 0UL
            && record.VelocityAuthorityVersion != velocityAuthorityVersion)
        {
            return collisionHandlerResult;
        }

        body.FramesStationaryFall = report.FramesStationaryFall;
        PhysicsObjUpdate.HandleAllCollisions(
            body,
            report.CollisionNormalValid,
            report.CollisionNormal,
            prevContact: false,
            prevOnWalkable: false,
            nowOnWalkable: body.OnWalkable);
        body.TransientState &= ~(TransientStateFlags.StationaryFall
            | TransientStateFlags.StationaryStop
            | TransientStateFlags.StationaryStuck);
        body.TransientState |= report.FramesStationaryFall switch
        {
            1 => TransientStateFlags.StationaryFall,
            2 => TransientStateFlags.StationaryStop,
            3 => TransientStateFlags.StationaryStuck,
            _ => TransientStateFlags.None,
        };
        return collisionHandlerResult;
    }

    internal bool HandleSetPositionCollisionReports(
        RuntimeEntityRecord record,
        ulong positionAuthorityVersion,
        ulong spatialAuthorityVersion,
        double physicsTime,
        bool previousContact,
        bool previousOnWalkable,
        bool collidedWithEnvironment,
        System.Collections.Immutable.ImmutableArray<uint> collidedObjectIds,
        out bool collisionHandlerResult)
    {
        collisionHandlerResult = false;
        if (!Entities.IsCurrent(record)
            || record.PositionAuthorityVersion != positionAuthorityVersion
            || record.SpatialAuthorityVersion != spatialAuthorityVersion
            || record.PhysicsBody is not { } body)
        {
            return false;
        }

        collisionHandlerResult = CollisionReports.HandleReports(
            record,
            body,
            physicsTime,
            previousContact,
            previousOnWalkable,
            collidedWithEnvironment,
            collidedObjectIds);
        return Entities.IsCurrent(record)
            && record.PositionAuthorityVersion == positionAuthorityVersion
            && record.SpatialAuthorityVersion == spatialAuthorityVersion
            && ReferenceEquals(record.PhysicsBody, body)
            && body.InWorld;
    }

    private bool HasOlderPreparedGeneration(
        PreparedLandblockCollisionGeneration candidate)
    {
        foreach ((_, PreparedLandblockCollisionGeneration other) in
                 _preparedCollisionGenerations)
        {
            if (other.Sequence < candidate.Sequence)
                return true;
        }
        return false;
    }

    private static uint CanonicalLandblock(uint value) =>
        (value & 0xFFFF0000u) | 0xFFFFu;

    private static bool IsFinite(System.Numerics.Vector3 value) =>
        float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z);

    private static void SynchronizeBodyActiveState(RuntimeEntityRecord record)
    {
        if (record.PhysicsBody is not { } body)
            return;
        if (record.ObjectClock.IsActive)
            body.TransientState |= TransientStateFlags.Active;
        else
            body.TransientState &= ~TransientStateFlags.Active;
    }

    private static void InitializeNewPhysicsBody(
        RuntimeEntityRecord record,
        PhysicsBody body)
    {
        body.State = record.FinalPhysicsState;
        if (record.Snapshot.Physics is not { } physics)
            return;
        if (physics.Velocity is { } velocity && IsFinite(velocity))
            body.set_velocity(velocity);
        if (physics.AngularVelocity is { } omega && IsFinite(omega))
            body.Omega = omega;
    }
}
