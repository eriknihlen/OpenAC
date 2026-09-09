using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using RemoteMotion = AcDream.Runtime.Physics.RemoteMotion;

namespace AcDream.App.Physics;

internal interface IProjectileSetupResolver
{
    Setup? Resolve(uint setupId);
}

internal sealed class DatProjectileSetupResolver : IProjectileSetupResolver
{
    private readonly IDatReaderWriter _dats;
    private readonly object _datLock;

    public DatProjectileSetupResolver(IDatReaderWriter dats, object datLock)
    {
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        _datLock = datLock ?? throw new ArgumentNullException(nameof(datLock));
    }

    public Setup? Resolve(uint setupId)
    {
        lock (_datLock)
            return _dats.Get<Setup>(setupId);
    }
}

internal sealed class ProjectileController
{
    internal readonly record struct QuantumStep(
        LiveEntityRecord Record,
        WorldEntity Entity,
        RuntimeProjectilePhysicsCommit RuntimeCommit);

    private readonly LiveEntityRuntime _liveEntities;
    private readonly RuntimeProjectilePhysicsUpdater _runtimeUpdater;
    private readonly ShadowObjectRegistry _shadows;
    private readonly IProjectileSetupResolver? _setupResolver;
    private readonly IEntityRootPosePublisher? _rootPoses;
    private readonly LiveWorldOriginState? _origin;
    private readonly List<LiveEntityRecord> _spatialProjectileSnapshot = new();
    private double _lastFiniteGameTime;

    internal ProjectileController(
        LiveEntityRuntime liveEntities,
        IProjectileSetupResolver? setupResolver = null,
        IEntityRootPosePublisher? rootPoses = null,
        LiveWorldOriginState? origin = null)
    {
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _runtimeUpdater = new RuntimeProjectilePhysicsUpdater(
            liveEntities.Physics);
        _shadows = liveEntities.Physics.Engine.ShadowObjects;
        _setupResolver = setupResolver;
        _rootPoses = rootPoses;
        _origin = origin;
        _liveEntities.ProjectionVisibilityChanged += OnProjectionVisibilityChanged;
    }

    internal Action<string>? DiagnosticSink { get; set; }

    internal int Count => _liveEntities.Records.Count(
        static record => record.ProjectileRuntime is not null);

    internal bool CanAcceptVectorPayload(
        uint serverGuid,
        Vector3 velocity,
        Vector3 angularVelocity)
    {
        bool valid = IsFinite(velocity) && IsFinite(angularVelocity);
        if (!valid)
            DiagnosticSink?.Invoke(
                $"Rejected invalid VectorUpdate for live object 0x{serverGuid:X8}.");
        return valid;
    }

    internal bool CanAcceptPositionPayload(
        uint serverGuid,
        CreateObject.ServerPosition position,
        Vector3? velocity)
    {
        var origin = new Vector3(
            position.PositionX,
            position.PositionY,
            position.PositionZ);
        var orientation = new Quaternion(
            position.RotationX,
            position.RotationY,
            position.RotationZ,
            position.RotationW);
        bool valid = IsFinite(origin)
            && PositionFrameValidation.IsValid(
                position.LandblockId,
                origin,
                orientation)
            && (velocity is null || IsFinite(velocity.Value));
        if (!valid)
            DiagnosticSink?.Invoke(
                $"Rejected invalid PositionUpdate for live object 0x{serverGuid:X8}.");
        return valid;
    }

    internal bool TryBind(
        LiveEntityRecord record,
        Setup setup,
        double currentTime,
        int liveCenterX = 0,
        int liveCenterY = 0)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(setup);

        if (!_liveEntities.TryGetRecord(record.ServerGuid, out var liveRecord)
            || !ReferenceEquals(liveRecord, record)
            || (record.FinalPhysicsState & PhysicsStateFlags.Missile) == 0)
            return false;

        if (record.ProjectileRuntime is RuntimeProjectile retained)
        {
            retained.Body.State = record.FinalPhysicsState;
            return true;
        }

        if (record.WorldEntity is not { } entity
            || record.Snapshot.Physics is not { } physics
            || record.Snapshot.Position is not { } wirePosition)
            return false;

        float scale = physics.Scale ?? record.Snapshot.ObjScale ?? 1f;
        if (!TryGetCollisionSphere(setup, scale, out ProjectileCollisionSphere sphere))
        {
            DiagnosticSink?.Invoke(
                $"Missile 0x{record.ServerGuid:X8} Setup 0x{record.Snapshot.SetupTableId ?? 0u:X8} " +
                $"does not have the supported retail one-sphere collision shape.");
            return false;
        }

        if (!double.IsFinite(currentTime))
        {
            DiagnosticSink?.Invoke(
                $"Missile 0x{record.ServerGuid:X8} has an invalid physics clock.");
            return false;
        }
        _lastFiniteGameTime = currentTime;

        PhysicsBody body;
        if (record.PhysicsBody is { } sharedBody)
        {
            body = sharedBody;
            uint currentCellId = body.CellPosition.ObjCellId;
            Vector3 currentCellLocal = body.CellPosition.Frame.Origin;
            if (!IsFinite(body.Position)
                || !IsFinite(body.Velocity)
                || !IsFinite(body.Omega)
                || !PositionFrameValidation.IsValid(
                    currentCellId,
                    currentCellLocal,
                    body.Orientation))
            {
                DiagnosticSink?.Invoke(
                    $"Missile 0x{record.ServerGuid:X8} has an invalid current shared physics frame or vector.");
                return false;
            }
            body.State = record.FinalPhysicsState;
            body.Friction = NormalizeFriction(
                physics.Friction ?? record.Snapshot.Friction);
            body.Elasticity = NormalizeElasticity(
                physics.Elasticity ?? record.Snapshot.Elasticity ?? 0.05f);

            body.SnapToCell(
                currentCellId,
                body.Position,
                currentCellLocal);
            body.LastUpdateTime = currentTime;
        }
        else
        {
            Vector3 velocity = physics.Velocity ?? Vector3.Zero;
            Vector3 omega = physics.AngularVelocity ?? Vector3.Zero;
            if (!IsFinite(entity.Position)
                || !PositionFrameValidation.IsValid(
                    wirePosition.LandblockId,
                    new Vector3(
                        wirePosition.PositionX,
                        wirePosition.PositionY,
                        wirePosition.PositionZ),
                    entity.Rotation)
                || !IsFinite(velocity)
                || !IsFinite(omega))
            {
                DiagnosticSink?.Invoke(
                    $"Missile 0x{record.ServerGuid:X8} has an invalid initial physics frame or vector.");
                return false;
            }
            body = _liveEntities.GetOrCreatePhysicsBody(
                record.ServerGuid,
                _ =>
                {
                    var created = new PhysicsBody
                    {
                        Friction = NormalizeFriction(
                            physics.Friction ?? record.Snapshot.Friction),
                        Elasticity = NormalizeElasticity(
                            physics.Elasticity ?? record.Snapshot.Elasticity ?? 0.05f),
                        Orientation = entity.Rotation,
                        LastUpdateTime = currentTime,
                    };
                    created.SnapToCell(
                        wirePosition.LandblockId,
                        entity.Position,
                        new Vector3(
                            wirePosition.PositionX,
                            wirePosition.PositionY,
                            wirePosition.PositionZ));
                    return created;
                });
        }

        uint canonicalCellId = body.CellPosition.ObjCellId;
        entity.SetPosition(body.Position);
        entity.Rotation = body.Orientation;
        entity.ParentCellId = canonicalCellId;
        bool alreadyProjectedInCanonicalCell = record.IsSpatiallyProjected
            && record.FullCellId == canonicalCellId;
        if ((!alreadyProjectedInCanonicalCell
                && !_liveEntities.RebucketLiveEntity(
                    record.ServerGuid,
                    canonicalCellId))
            || !_liveEntities.TryGetRecord(record.ServerGuid, out var currentRecord)
            || !ReferenceEquals(currentRecord, record)
            || !ReferenceEquals(currentRecord.WorldEntity, entity)
            || !ReferenceEquals(currentRecord.PhysicsBody, body)
            || (currentRecord.FinalPhysicsState & PhysicsStateFlags.Missile) == 0)
        {
            return false;
        }

        if (currentRecord.ProjectileRuntime is RuntimeProjectile concurrentRuntime)
            return ReferenceEquals(concurrentRuntime.Body, body);

        canonicalCellId = body.CellPosition.ObjCellId;

        RuntimeProjectile runtime = (RuntimeProjectile)
            _liveEntities.BindProjectileRuntime(
                record.ServerGuid,
                body,
                sphere,
                () => _liveEntities.IsCurrentRecord(record)
                    && ReferenceEquals(record.WorldEntity, entity)
                    && ReferenceEquals(record.PhysicsBody, body));

        if (HasVisibleCell(record) && !IsHidden(record))
        {
            ShadowPositionSynchronizer.Sync(
                _shadows,
                entity.Id,
                body.Position,
                body.Orientation,
                canonicalCellId,
                liveCenterX,
                liveCenterY);
        }
        else if (HasVisibleCell(record))
        {
            body.InWorld = true;
            body.LastUpdateTime = currentTime;
            _shadows.Suspend(entity.Id);
        }
        else
        {
            SuspendOutsideWorld(runtime, entity.Id);
        }
        _rootPoses?.UpdateRoot(entity);
        return true;
    }

    internal bool ApplyAuthoritativeVector(
        LiveEntityRecord expectedRecord,
        Vector3 velocity,
        Vector3 angularVelocity,
        double currentTime) =>
        ApplyAuthoritativeVector(
            expectedRecord,
            expectedRecord.VectorAuthorityVersion,
            expectedRecord.VelocityAuthorityVersion,
            velocity,
            angularVelocity,
            currentTime);

    internal bool ApplyAuthoritativeVector(
        LiveEntityRecord expectedRecord,
        ulong expectedVectorAuthorityVersion,
        ulong expectedVelocityAuthorityVersion,
        Vector3 velocity,
        Vector3 angularVelocity,
        double currentTime)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        uint serverGuid = expectedRecord.ServerGuid;
        if (!TryGetCurrent(serverGuid, out LiveEntityRecord record, out RuntimeProjectile runtime))
            return false;
        if (!ReferenceEquals(record, expectedRecord)
            || record.VectorAuthorityVersion != expectedVectorAuthorityVersion
            || record.VelocityAuthorityVersion != expectedVelocityAuthorityVersion)
            return false;

        if ((record.FinalPhysicsState & PhysicsStateFlags.Missile) == 0
            && record.RemoteMotionRuntime is not null)
            return false;

        if (!IsFinite(velocity)
            || !IsFinite(angularVelocity)
            || !double.IsFinite(currentTime))
        {
            DiagnosticSink?.Invoke(
                $"Rejected invalid VectorUpdate for missile 0x{serverGuid:X8}.");
            return true;
        }
        _lastFiniteGameTime = currentTime;

        return _runtimeUpdater.ApplyAuthoritativeVector(
                record.Canonical,
                expectedVectorAuthorityVersion,
                expectedVelocityAuthorityVersion,
                velocity,
                angularVelocity,
                currentTime,
                () => TryGetCurrent(
                        serverGuid,
                        out LiveEntityRecord current,
                        out RuntimeProjectile currentRuntime)
                    && ReferenceEquals(current, record)
                    && ReferenceEquals(currentRuntime, runtime));
    }

    internal bool ApplyAuthoritativeState(
        LiveEntityRecord expectedRecord,
        PhysicsStateFlags state,
        double currentTime,
        int liveCenterX,
        int liveCenterY) =>
        ApplyAuthoritativeState(
            expectedRecord,
            expectedRecord.StateAuthorityVersion,
            state,
            currentTime,
            liveCenterX,
            liveCenterY);

    internal bool ApplyAuthoritativeState(
        LiveEntityRecord expectedRecord,
        ulong expectedStateAuthorityVersion,
        PhysicsStateFlags state,
        double currentTime,
        int liveCenterX,
        int liveCenterY)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        uint serverGuid = expectedRecord.ServerGuid;
        if (!_liveEntities.TryGetRecord(serverGuid, out LiveEntityRecord record))
            return false;
        if (!ReferenceEquals(record, expectedRecord)
            || record.StateAuthorityVersion != expectedStateAuthorityVersion)
            return false;

        bool validClock = double.IsFinite(currentTime);
        double effectiveClock = validClock
            ? currentTime
            : _lastFiniteGameTime;
        if (validClock)
            _lastFiniteGameTime = currentTime;
        if (!validClock)
            DiagnosticSink?.Invoke(
                $"Ignored invalid State update clock for live object 0x{serverGuid:X8}; state remains authoritative.");

        if (record.ProjectileRuntime is RuntimeProjectile runtime)
        {
            return _runtimeUpdater.ApplyAuthoritativeState(
                record.Canonical,
                expectedStateAuthorityVersion,
                state,
                effectiveClock,
                liveCenterX,
                liveCenterY,
                () => _liveEntities.TryGetRecord(
                        serverGuid,
                        out LiveEntityRecord current)
                    && ReferenceEquals(current, record)
                    && ReferenceEquals(
                        current.ProjectileRuntime,
                        runtime));
        }

        if (record.RemoteMotionRuntime is { } remote)
            remote.Body.State = state;

        if ((state & PhysicsStateFlags.Missile) == 0
            || record.WorldEntity is not { } entity)
            return false;

        Setup? setup = _setupResolver?.Resolve(entity.SourceGfxObjOrSetupId);
        if (setup is null)
        {
            DiagnosticSink?.Invoke(
                $"Cannot activate missile 0x{serverGuid:X8}: Setup 0x{entity.SourceGfxObjOrSetupId:X8} is unavailable.");
            return true;
        }

        TryBind(record, setup, effectiveClock, liveCenterX, liveCenterY);
        return true;
    }

    internal bool SyncPresentationFromResolvedBody(
        LiveEntityRecord expectedRecord,
        double currentTime)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        if (!TryGetCurrent(
                expectedRecord.ServerGuid,
                out LiveEntityRecord record,
                out RuntimeProjectile runtime)
            || !ReferenceEquals(record, expectedRecord)
            || record.WorldEntity is not { } entity)
        {
            return false;
        }

        if (double.IsFinite(currentTime))
            _lastFiniteGameTime = currentTime;

        entity.SetPosition(runtime.Body.Position);
        entity.Rotation = runtime.Body.Orientation;
        entity.ParentCellId = record.FullCellId;
        _rootPoses?.UpdateRoot(entity);
        return true;
    }

    internal bool HandlesMovement(uint serverGuid) =>
        TryGetCurrent(serverGuid, out LiveEntityRecord record, out _)
        && ((record.FinalPhysicsState & PhysicsStateFlags.Missile) != 0
            || record.RemoteMotionRuntime is null);

    internal bool TryGetBody(uint serverGuid, out PhysicsBody body)
    {
        if (TryGetCurrent(serverGuid, out _, out RuntimeProjectile runtime))
        {
            body = runtime.Body;
            return true;
        }
        body = null!;
        return false;
    }

    internal bool LeaveWorld(LiveEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.ProjectileRuntime is not RuntimeProjectile runtime
            || record.WorldEntity is not { } entity)
            return false;
        SuspendOutsideWorld(runtime, entity.Id);
        return true;
    }

    internal void Tick(
        double currentTime,
        int liveCenterX,
        int liveCenterY,
        Vector3? playerWorldPosition)
    {
        double elapsed = currentTime - _lastFiniteGameTime;
        Tick(
            currentTime,
            double.IsFinite(elapsed) && elapsed > 0.0 ? (float)elapsed : 0f,
            liveCenterX,
            liveCenterY,
            playerWorldPosition);
    }

    internal void Tick(
        double currentTime,
        float elapsedSeconds,
        int liveCenterX,
        int liveCenterY,
        Vector3? playerWorldPosition)
    {
        if (!double.IsFinite(currentTime))
        {
            DiagnosticSink?.Invoke("Rejected non-finite projectile update clock.");
            return;
        }
        _lastFiniteGameTime = currentTime;

        _liveEntities.CopySpatialProjectileRecordsTo(_spatialProjectileSnapshot);
        foreach (LiveEntityRecord record in _spatialProjectileSnapshot)
        {
            if (record.ProjectileRuntime is not RuntimeProjectile runtime
                || !_liveEntities.IsCurrentSpatialProjectile(record, runtime)
                || record.WorldEntity is not { } entity)
                continue;

            runtime.Body.State = record.FinalPhysicsState;
            if (!HasVisibleCell(record))
            {
                SuspendOutsideWorld(runtime, entity.Id);
                continue;
            }

            if (!runtime.Body.InWorld)
            {
                runtime.Body.LastUpdateTime = currentTime;
                runtime.Body.InWorld = true;
                ShadowPositionSynchronizer.Sync(
                    _shadows,
                    entity.Id,
                    runtime.Body.Position,
                    runtime.Body.Orientation,
                    record.FullCellId,
                    liveCenterX,
                    liveCenterY);
                _rootPoses?.UpdateRoot(entity);
            }

            if (IsHidden(record))
            {
                _shadows.Suspend(entity.Id);
                if (record.AnimationRuntime is null
                    && record.RemoteMotionRuntime is null)
                {
                    RetailObjectActivityResult hiddenActivity =
                        RetailObjectActivityGate.Evaluate(
                            record.ObjectClock,
                            runtime.Body,
                            _liveEntities.GetRootObjectClockDisposition(record.ServerGuid)
                                is RetailObjectClockDisposition.Advance,
                            record.HasPartArray,
                            (record.FinalPhysicsState & PhysicsStateFlags.Static) != 0,
                            runtime.Body.Position,
                            playerWorldPosition,
                            elapsedSeconds);
                    if (hiddenActivity is RetailObjectActivityResult.Active)
                        record.ObjectClock.Advance(elapsedSeconds);
                }
                continue;
            }

            bool isMissile =
                (record.FinalPhysicsState & PhysicsStateFlags.Missile) != 0;
            if (!isMissile)
            {
                if (record.RemoteMotionRuntime is not null)
                    continue;
            }

            if (record.AnimationRuntime is not null)
                continue;

            RetailObjectActivityResult activity = RetailObjectActivityGate.Evaluate(
                record.ObjectClock,
                runtime.Body,
                _liveEntities.GetRootObjectClockDisposition(record.ServerGuid)
                    is RetailObjectClockDisposition.Advance,
                record.HasPartArray,
                (record.FinalPhysicsState & PhysicsStateFlags.Static) != 0,
                runtime.Body.Position,
                playerWorldPosition,
                elapsedSeconds);
            if (activity is not RetailObjectActivityResult.Active)
                continue;

            RetailObjectQuantumBatch batch = record.ObjectClock.Advance(elapsedSeconds);
            for (int qi = 0; qi < batch.Count; qi++)
            {
                if (!AdvanceQuantum(
                        record,
                        batch.GetQuantum(qi),
                        liveCenterX,
                        liveCenterY))
                    break;
                if (record.RemoteMotionRuntime is RemoteMotion remote)
                {
                    RetailObjectManagerTail.Run(
                        remote.Host?.TargetManager,
                        remote.Movement,
                        partArray: null,
                        remote.Host?.PositionManager);
                }
            }
        }
    }

    internal bool AdvanceQuantum(
        LiveEntityRecord record,
        float quantum,
        int liveCenterX,
        int liveCenterY)
    {
        if (!TryBeginQuantum(record, quantum, out QuantumStep step))
            return false;
        return CompleteQuantum(step, liveCenterX, liveCenterY);
    }

    internal bool TryBeginQuantum(
        LiveEntityRecord record,
        float quantum,
        out QuantumStep step)
    {
        ArgumentNullException.ThrowIfNull(record);
        step = default;
        if (!TryGetCurrent(
                record.ServerGuid,
                out LiveEntityRecord current,
                out RuntimeProjectile runtime)
            || !ReferenceEquals(record, current)
            || current.WorldEntity is not { } entity
            || IsHidden(current)
            || !HasVisibleCell(current))
            return false;

        bool ExternalOwnerValid() =>
            _liveEntities.IsCurrentRecord(current)
            && ReferenceEquals(current.WorldEntity, entity)
            && ReferenceEquals(current.ProjectileRuntime, runtime);
        if (!_runtimeUpdater.TryBegin(
                current.Canonical,
                quantum,
                current.ObjectClockEpoch,
                ExternalOwnerValid,
                out RuntimeProjectilePhysicsCommit runtimeCommit))
        {
            return false;
        }

        step = new QuantumStep(
            current,
            entity,
            runtimeCommit);
        return true;
    }

    internal bool CompleteQuantum(
        in QuantumStep step,
        int liveCenterX,
        int liveCenterY)
    {
        LiveEntityRecord current = step.Record;
        WorldEntity entity = step.Entity;
        if (!IsCurrentQuantumIdentity(step))
            return false;
        QuantumStep exactStep = step;

        return _runtimeUpdater.Complete(
            exactStep.RuntimeCommit,
            liveCenterX,
            liveCenterY,
            snapshot =>
            {
                if (!IsCurrentQuantumIdentity(exactStep))
                    return false;
                entity.SetPosition(snapshot.Position);
                entity.Rotation = snapshot.Orientation;
                entity.ParentCellId = snapshot.FullCellId;
                if (HasVisibleCell(current))
                    _rootPoses?.UpdateRoot(entity);
                return IsCurrentQuantumIdentity(exactStep);
            });
    }

    private bool IsCurrentQuantumIdentity(in QuantumStep step) =>
        TryGetCurrent(
            step.Record.ServerGuid,
            out LiveEntityRecord current,
            out RuntimeProjectile runtime)
        && ReferenceEquals(current, step.Record)
        && ReferenceEquals(current.WorldEntity, step.Entity)
        && ReferenceEquals(runtime, step.RuntimeCommit.Projectile)
        && runtime.PredictionAuthorityVersion
            == step.RuntimeCommit.PredictionAuthorityVersion;

    private void OnProjectionVisibilityChanged(LiveEntityRecord record, bool visible)
    {
        if (record.WorldEntity is not { } entity
            || !_liveEntities.TryGetRecord(record.ServerGuid, out LiveEntityRecord current)
            || !ReferenceEquals(current, record))
        {
            return;
        }

        if (visible
            && record.ProjectileRuntime is null
            && record.PhysicsBody is not null
            && (record.FinalPhysicsState & PhysicsStateFlags.Missile) != 0)
        {
            Setup? setup = _setupResolver?.Resolve(
                entity.SourceGfxObjOrSetupId);
            if (setup is not null)
            {
                int liveCenterX = _origin?.CenterX ?? 0;
                int liveCenterY = _origin?.CenterY ?? 0;
                _ = TryBind(
                    record,
                    setup,
                    _lastFiniteGameTime,
                    liveCenterX,
                    liveCenterY);
            }
        }

        if (record.ProjectileRuntime is not RuntimeProjectile runtime
            || !ReferenceEquals(current.ProjectileRuntime, runtime))
        {
            return;
        }

        if (visible)
        {
            runtime.Body.State = record.FinalPhysicsState;
            runtime.Body.InWorld = true;
            if (IsHidden(record))
            {
                _shadows.Suspend(entity.Id);
                return;
            }

            int liveCenterX = _origin?.CenterX ?? 0;
            int liveCenterY = _origin?.CenterY ?? 0;
            ShadowPositionSynchronizer.Sync(
                _shadows,
                entity.Id,
                runtime.Body.Position,
                runtime.Body.Orientation,
                record.FullCellId,
                liveCenterX,
                liveCenterY);
            _rootPoses?.UpdateRoot(entity);
            return;
        }

        SuspendOutsideWorld(runtime, entity.Id);
    }

    private static bool HasVisibleCell(LiveEntityRecord record) =>
        record.IsSpatiallyProjected
        && record.IsSpatiallyVisible
        && record.FullCellId != 0;

    private static bool IsHidden(LiveEntityRecord record) =>
        (record.FinalPhysicsState & PhysicsStateFlags.Hidden) != 0;

    private void SuspendOutsideWorld(RuntimeProjectile runtime, uint localEntityId)
    {
        runtime.Body.InWorld = false;
        Deactivate(runtime.Body);
        _shadows.Suspend(localEntityId);
    }

    private static void Deactivate(PhysicsBody body) =>
        body.TransientState &= ~TransientStateFlags.Active;

    private static float NormalizeFriction(float? value) =>
        value is >= 0f and <= 1f && float.IsFinite(value.Value)
            ? value.Value
            : PhysicsBody.DefaultFriction;

    private static float NormalizeElasticity(float value)
    {
        if (float.IsNaN(value) || value <= 0f)
            return 0f;
        return MathF.Min(value, 0.1f);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z);

    private bool TryGetCurrent(
        uint serverGuid,
        out LiveEntityRecord record,
        out RuntimeProjectile runtime)
    {
        if (_liveEntities.TryGetRecord(serverGuid, out record!)
            && record.ProjectileRuntime is RuntimeProjectile found)
        {
            runtime = found;
            return true;
        }

        record = null!;
        runtime = null!;
        return false;
    }

    private static bool TryGetCollisionSphere(
        Setup setup,
        float scale,
        out ProjectileCollisionSphere sphere)
    {
        if (setup.Spheres.Count == 1)
        {
            var source = setup.Spheres[0];
            sphere = new ProjectileCollisionSphere(source.Origin, source.Radius, scale);
            if (sphere.IsValid)
                return true;
        }

        sphere = default;
        return false;
    }
}
