using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Physics;

internal readonly record struct RuntimeRemotePhysicsSnapshot(
    System.Numerics.Vector3 Position,
    System.Numerics.Quaternion Orientation,
    uint FullCellId);

internal sealed class RuntimeRemotePhysicsUpdater
{
    private const double ServerControlledVelocityStaleSeconds = 0.60;

    private readonly RuntimePhysicsState _physics;

    internal RuntimeRemotePhysicsUpdater(
        RuntimePhysicsState physics)
    {
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
    }

    private static bool IsPlayerGuid(uint guid) => (guid & 0xFF000000u) == 0x50000000u;


    internal bool Tick(
        RuntimeEntityRecord record,
        RemoteMotion rm,
        float objectScale,
        AcDream.Core.Physics.AnimationSequencer? sequencer,
        float dt,
        ulong objectClockEpoch,
        AcDream.Core.Physics.Motion.MotionDeltaFrame rootMotionLocalFrame,
        float radius,
        float height,
        int liveCenterX,
        int liveCenterY,
        System.Action<uint, AcDream.Core.Physics.AnimationSequencer>?
            processAnimationHooks = null,
        System.Action<System.Numerics.Vector3>? applyStaleVelocityCycle = null,
        System.Func<RuntimeRemotePhysicsSnapshot, bool>?
            acknowledgeProjection = null,
        System.Func<bool>? externalOwnerValid = null,
        System.Collections.Immutable.ImmutableArray<AcDream.Core.Physics.FlatCollisionSphere>
            sphereList = default,
        float sphereScale = 1f,
        float stepUpHeight = 0.4f,
        float stepDownHeight = 0.4f,
        AcDream.Core.Physics.ObjectInfoState moverPvpState =
            AcDream.Core.Physics.ObjectInfoState.None)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(rm);
        ArgumentNullException.ThrowIfNull(rootMotionLocalFrame);
        if (!IsCurrentOwner(
                record,
                rm,
                objectClockEpoch,
                externalOwnerValid))
        {
            return false;
        }
        uint serverGuid = record.ServerGuid;
        AcDream.Core.Physics.PhysicsDiagnostics.BeginRemoteSlideAttribution(
            serverGuid);
        uint localEntityId = record.LocalEntityId
            ?? throw new InvalidOperationException(
                $"Runtime entity 0x{serverGuid:X8}/{record.Incarnation} has no local identity.");
        {
            double nowSec = _physics.UtcNowSeconds;

            bool bodyOnWalkableAtTickStart = rm.Body.OnWalkable;
            System.Numerics.Vector3 scaledRootMotionLocalOrigin =
                bodyOnWalkableAtTickStart
                    ? rootMotionLocalFrame.Origin * objectScale
                    : System.Numerics.Vector3.Zero;

            bool slideForcedContact = !rm.Body.InContact;
            bool slideForcedWalkable = !rm.Body.OnWalkable;
            System.Numerics.Vector3 slideVelocityBeforeZero = rm.Body.Velocity;

            rm.Body.TransientState |=
                AcDream.Core.Physics.TransientStateFlags.Active;

            if (!rm.Airborne)
            {
                bool moveToArmed = rm.MoveTo is
                    { MovementTypeState: not AcDream.Core.Physics.MovementType.Invalid };
                bool stickyArmed =
                    (rm.Host?.PositionManager.GetStickyObjectId() ?? 0u) != 0u;
                if (!IsPlayerGuid(serverGuid) && rm.HasServerVelocity
                    && !moveToArmed && !stickyArmed)
                {
                    double velocityAge = nowSec - rm.LastServerPosTime;
                    if (velocityAge > ServerControlledVelocityStaleSeconds)
                    {
                        rm.ServerVelocity = System.Numerics.Vector3.Zero;
                        rm.HasServerVelocity = false;
                        applyStaleVelocityCycle?.Invoke(
                            System.Numerics.Vector3.Zero);
                    }
                }

            }


            var preIntegratePos = rm.Body.Position;
            if (rm.Host is { } npcHost)
            {
                AcDream.Core.Physics.Motion.MotionDeltaFrame pmDelta =
                    rm.PositionManagerDeltaScratch;
                pmDelta.Origin = scaledRootMotionLocalOrigin;
                pmDelta.Orientation = rootMotionLocalFrame.Orientation;
                float maxSpeedNpc = rm.Motion.GetAdjustedMaxSpeed();
                rm.Position.ComposeOffset(
                    dt,
                    rm.Body.Position,
                    rm.Body.Orientation,
                    pmDelta,
                    rm.Interp,
                    maxSpeedNpc,
                    pmDelta,
                    inContact: rm.Body.InContact);
                npcHost.PositionManager.AdjustOffset(pmDelta, dt);
                rm.Body.IsFullyConstrained = npcHost.PositionManager.IsFullyConstrained();
                ApplyPositionManagerDelta(rm.Body, pmDelta);
            }
            else
            {
                AcDream.Core.Physics.Motion.MotionDeltaFrame pmDelta =
                    rm.PositionManagerDeltaScratch;
                pmDelta.Origin = scaledRootMotionLocalOrigin;
                pmDelta.Orientation = rootMotionLocalFrame.Orientation;
                float maxSpeedNpc = rm.Motion.GetAdjustedMaxSpeed();
                rm.Position.ComposeOffset(
                    dt,
                    rm.Body.Position,
                    rm.Body.Orientation,
                    pmDelta,
                    rm.Interp,
                    maxSpeedNpc,
                    pmDelta,
                    inContact: rm.Body.InContact);
                ApplyPositionManagerDelta(rm.Body, pmDelta);
            }
            rm.Body.calc_acceleration();
            rm.Body.UpdatePhysicsInternal(dt);
            if (sequencer is { } hookSequencer)
                processAnimationHooks?.Invoke(localEntityId, hookSequencer);
            if (!IsCurrentOwner(
                    record,
                    rm,
                    objectClockEpoch,
                    externalOwnerValid))
            {
                return false;
            }
            var postIntegratePos = rm.Body.Position;
            uint committedCellId = rm.CellId;

            if (rm.CellId != 0 && _physics.Engine.LandblockCount > 0)
            {
                float deR = radius;
                float deH = height;
                if (deR < 0.05f) { deR = 0.48f; deH = 1.835f; }
                bool previousContact = rm.Body.InContact;
                bool previousOnWalkable = rm.Body.OnWalkable;
                var resolveResult = _physics.Engine.ResolveWithTransition(
                    preIntegratePos, postIntegratePos, rm.CellId,
                    sphereRadius: deR,
                    sphereHeight: deH,
                    stepUpHeight: stepUpHeight,
                    stepDownHeight: stepDownHeight,
                    sphereList: sphereList,
                    sphereScale: sphereScale,
                    isOnGround: previousOnWalkable,
                    body: rm.Body,
                    moverFlags: (IsPlayerGuid(serverGuid)
                        ? AcDream.Core.Physics.ObjectInfoState.IsPlayer
                          | AcDream.Core.Physics.ObjectInfoState.EdgeSlide
                        : AcDream.Core.Physics.ObjectInfoState.EdgeSlide)
                        | moverPvpState,
                    movingEntityId: localEntityId);

                committedCellId = CommitSweepOutcome(
                    rm.Body, resolveResult, preIntegratePos, dt, committedCellId);

                bool candidateMoved = postIntegratePos != preIntegratePos;
                if (resolveResult.Ok && candidateMoved)
                {
                    bool finalOnWalkable = AcDream.Core.Physics.PhysicsObjUpdate
                        .CommitSetPositionContactPrefix(
                            rm.Body,
                            resolveResult.InContact,
                            resolveResult.OnWalkable,
                            previousOnWalkable);

                    if (!previousOnWalkable && finalOnWalkable)
                    {
                        rm.Movement.HitGround();

                        if (!IsCurrentOwner(
                                record,
                                rm,
                                objectClockEpoch,
                                externalOwnerValid))
                        {
                            return false;
                        }

                        rm.Interp.Clear();
                        if (Environment.GetEnvironmentVariable("ACDREAM_DUMP_MOTION") == "1")
                            Console.WriteLine($"VU.land guid=0x{serverGuid:X8} Z={rm.Body.Position.Z:F2}");
                    }
                    else if (previousOnWalkable && !finalOnWalkable)
                    {
                        rm.Motion.LeaveGround();
                        if (!IsCurrentOwner(
                                record,
                                rm,
                                objectClockEpoch,
                                externalOwnerValid))
                        {
                            return false;
                        }
                    }

                    AcDream.Core.Physics.PhysicsObjUpdate
                        .CommitSetPositionPostGround(rm.Body);

                    AcDream.Core.Physics.PhysicsObjUpdate.HandleAllCollisions(
                        rm.Body,
                        resolveResult.CollisionNormalValid,
                        resolveResult.CollisionNormal,
                        previousContact,
                        previousOnWalkable,
                        rm.Body.OnWalkable);

                    rm.Airborne = !rm.Body.OnWalkable;
                }

                {
                    bool slideBodyCpValid = rm.Body.ContactPlaneValid;
                    float slideBodyCpNz = rm.Body.ContactPlane.Normal.Z;
                    int slideSignature =
                          (rm.Airborne                    ? 1 << 0 : 0)
                        | (slideForcedContact             ? 1 << 1 : 0)
                        | (slideForcedWalkable            ? 1 << 2 : 0)
                        | (resolveResult.InContact        ? 1 << 3 : 0)
                        | (resolveResult.OnWalkable       ? 1 << 4 : 0)
                        | (resolveResult.IsOnGround       ? 1 << 5 : 0)
                        | (rm.Body.InContact              ? 1 << 6 : 0)
                        | (rm.Body.OnWalkable             ? 1 << 7 : 0)
                        | (rm.Body.HasGravity             ? 1 << 8 : 0)
                        | (slideBodyCpValid               ? 1 << 9 : 0)
                        | (slideBodyCpValid
                           && slideBodyCpNz
                              < AcDream.Core.Physics.PhysicsGlobals.FloorZ
                                                          ? 1 << 10 : 0)
                        | (System.Numerics.Vector3.Distance(
                               preIntegratePos, rm.Body.Position) > 0.01f
                                                          ? 1 << 11 : 0);
                    if (AcDream.Core.Physics.PhysicsDiagnostics
                            .ShouldEmitRemoteSlideTick(serverGuid, slideSignature))
                    {
                        AcDream.Core.Physics.PhysicsDiagnostics.LogRemoteSlideTick(
                            guid: serverGuid,
                            airborne: rm.Airborne,
                            forcedContact: slideForcedContact,
                            forcedWalkable: slideForcedWalkable,
                            velocityBeforeZero: slideVelocityBeforeZero,
                            resolved: true,
                            resolveInContact: resolveResult.InContact,
                            resolveOnWalkable: resolveResult.OnWalkable,
                            resolveIsOnGround: resolveResult.IsOnGround,
                            resolveContactPlaneValid: resolveResult.InContact,
                            resolveContactPlaneNormalZ:
                                resolveResult.ContactPlane.Normal.Z,
                            bodyContactPlaneValid: slideBodyCpValid,
                            bodyContactPlaneNormalZ: slideBodyCpNz,
                            contact: rm.Body.InContact,
                            onWalkable: rm.Body.OnWalkable,
                            gravity: rm.Body.HasGravity,
                            velocity: rm.Body.Velocity,
                            acceleration: rm.Body.Acceleration,
                            preIntegratePosition: preIntegratePos,
                            postIntegratePosition: postIntegratePos,
                            resolvedPosition: rm.Body.Position);
                    }
                }

            }
            else
            {
                bool skipBodyCpValid = rm.Body.ContactPlaneValid;
                float skipBodyCpNz = rm.Body.ContactPlane.Normal.Z;
                int skipSignature =
                      (rm.Airborne          ? 1 << 0 : 0)
                    | (slideForcedContact   ? 1 << 1 : 0)
                    | (slideForcedWalkable  ? 1 << 2 : 0)
                    | (rm.Body.InContact    ? 1 << 6 : 0)
                    | (rm.Body.OnWalkable   ? 1 << 7 : 0)
                    | (rm.Body.HasGravity   ? 1 << 8 : 0)
                    | (skipBodyCpValid      ? 1 << 9 : 0)
                    | (1 << 12);
                if (AcDream.Core.Physics.PhysicsDiagnostics
                        .ShouldEmitRemoteSlideTick(serverGuid, skipSignature))
                {
                    AcDream.Core.Physics.PhysicsDiagnostics.LogRemoteSlideTick(
                        guid: serverGuid,
                        airborne: rm.Airborne,
                        forcedContact: slideForcedContact,
                        forcedWalkable: slideForcedWalkable,
                        velocityBeforeZero: slideVelocityBeforeZero,
                        resolved: false,
                        resolveInContact: false,
                        resolveOnWalkable: false,
                        resolveIsOnGround: false,
                        resolveContactPlaneValid: false,
                        resolveContactPlaneNormalZ: 0f,
                        bodyContactPlaneValid: skipBodyCpValid,
                        bodyContactPlaneNormalZ: skipBodyCpNz,
                        contact: rm.Body.InContact,
                        onWalkable: rm.Body.OnWalkable,
                        gravity: rm.Body.HasGravity,
                        velocity: rm.Body.Velocity,
                        acceleration: rm.Body.Acceleration,
                        preIntegratePosition: preIntegratePos,
                        postIntegratePosition: postIntegratePos,
                        resolvedPosition: rm.Body.Position);
                }
            }

            if (!(acknowledgeProjection?.Invoke(
                    new RuntimeRemotePhysicsSnapshot(
                        rm.Body.Position,
                        rm.Body.Orientation,
                        committedCellId)) ?? true)
                || !IsCurrentOwner(
                    record,
                    rm,
                    objectClockEpoch,
                    externalOwnerValid))
            {
                return false;
            }
            bool cellChanged = committedCellId != 0
                && committedCellId != rm.CellId;
            if (cellChanged)
                rm.CellId = committedCellId;
            if (!IsCurrentOwner(
                    record,
                    rm,
                    objectClockEpoch,
                    externalOwnerValid))
            {
                return false;
            }

            if (ShouldSynchronizeShadow(
                    cellChanged,
                    rm.Body.Position,
                    rm.Body.Orientation,
                    rm.LastShadowSyncPos,
                    rm.LastShadowSyncOrientation))
            {
                SyncRemoteShadowToBody(
                    localEntityId,
                    rm,
                    liveCenterX,
                    liveCenterY);
            }
        }

        AcDream.Core.Physics.RetailObjectManagerTail.Run(
            rm.Host?.TargetManager,
            rm.Movement,
            sequencer?.Manager,
            rm.Host?.PositionManager);
        return IsCurrentOwner(
            record,
            rm,
            objectClockEpoch,
            externalOwnerValid);
    }

    internal bool TickHidden(
        RuntimeEntityRecord record,
        RemoteMotion rm,
        float dt,
        ulong objectClockEpoch,
        float radius,
        float height,
        AcDream.Core.Physics.Motion.MotionTableManager?
            partArrayHandleMovement = null,
        System.Action<uint, AcDream.Core.Physics.AnimationSequencer>?
            processAnimationHooks = null,
        AcDream.Core.Physics.AnimationSequencer? sequencer = null,
        System.Func<RuntimeRemotePhysicsSnapshot, bool>?
            acknowledgeProjection = null,
        System.Func<bool>? externalOwnerValid = null,
        System.Collections.Immutable.ImmutableArray<AcDream.Core.Physics.FlatCollisionSphere>
            sphereList = default,
        float sphereScale = 1f,
        float stepUpHeight = 0.4f,
        float stepDownHeight = 0.4f,
        AcDream.Core.Physics.ObjectInfoState moverPvpState =
            AcDream.Core.Physics.ObjectInfoState.None)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(rm);
        if (!IsCurrentOwner(
                record,
                rm,
                objectClockEpoch,
                externalOwnerValid))
        {
            return false;
        }
        uint localEntityId = record.LocalEntityId
            ?? throw new InvalidOperationException(
                $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} has no local identity.");

        AcDream.Core.Physics.PhysicsDiagnostics.BeginRemoteSlideAttribution(
            record.ServerGuid);

        System.Numerics.Vector3 preComposePosition = rm.Body.Position;

        AcDream.Core.Physics.Motion.MotionDeltaFrame positionDelta =
            rm.PositionManagerDeltaScratch;
        positionDelta.Reset();
        rm.Position.ComposeOffset(
            dt,
            rm.Body.Position,
            rm.Body.Orientation,
            positionDelta,
            rm.Interp,
            rm.Motion.GetAdjustedMaxSpeed(),
            positionDelta,
            inContact: rm.Body.InContact);
        rm.Host?.PositionManager.AdjustOffset(positionDelta, dt);
        if (rm.Host is { } hiddenHost)
            rm.Body.IsFullyConstrained = hiddenHost.PositionManager.IsFullyConstrained();
        ApplyPositionManagerDelta(rm.Body, positionDelta);

        if (sequencer is not null)
            processAnimationHooks?.Invoke(localEntityId, sequencer);
        if (!IsCurrentOwner(
                record,
                rm,
                objectClockEpoch,
                externalOwnerValid))
        {
            return false;
        }

        System.Numerics.Vector3 composedPosition = rm.Body.Position;
        uint committedCellId = rm.CellId;
        if (rm.CellId != 0
            && composedPosition != preComposePosition
            && _physics.Engine.LandblockCount > 0)
        {
            if (radius < 0.05f)
            {
                radius = 0.48f;
                height = 1.835f;
            }

            bool previousContact = rm.Body.InContact;
            bool previousOnWalkable = rm.Body.OnWalkable;
            var resolved = _physics.Engine.ResolveWithTransition(
                preComposePosition,
                composedPosition,
                rm.CellId,
                radius,
                height,
                stepUpHeight: stepUpHeight,
                stepDownHeight: stepDownHeight,
                isOnGround: previousOnWalkable,
                body: rm.Body,
                moverFlags: (IsPlayerGuid(record.ServerGuid)
                    ? AcDream.Core.Physics.ObjectInfoState.IsPlayer
                      | AcDream.Core.Physics.ObjectInfoState.EdgeSlide
                    : AcDream.Core.Physics.ObjectInfoState.EdgeSlide)
                    | moverPvpState,
                movingEntityId: localEntityId,
                sphereList: sphereList,
                sphereScale: sphereScale);
            rm.Body.Position = resolved.Position;
            if (resolved.CellId != 0)
                committedCellId = resolved.CellId;
            if (!AcDream.Core.Physics.PhysicsObjUpdate.CommitSetPositionTransition(
                rm.Body,
                resolved.InContact,
                resolved.OnWalkable,
                resolved.CollisionNormalValid,
                resolved.CollisionNormal,
                previousContact,
                previousOnWalkable,
                rm.Movement.HitGround,
                rm.Motion.LeaveGround,
                () => IsCurrentOwner(
                    record,
                    rm,
                    objectClockEpoch,
                    externalOwnerValid)))
            {
                return false;
            }
            rm.Airborne = !rm.Body.OnWalkable;
        }

        if (!(acknowledgeProjection?.Invoke(
                new RuntimeRemotePhysicsSnapshot(
                    rm.Body.Position,
                    rm.Body.Orientation,
                    committedCellId)) ?? true)
            || !IsCurrentOwner(
                record,
                rm,
                objectClockEpoch,
                externalOwnerValid))
        {
            return false;
        }
        if (committedCellId != 0 && committedCellId != rm.CellId)
            rm.CellId = committedCellId;
        if (!IsCurrentOwner(
                record,
                rm,
                objectClockEpoch,
                externalOwnerValid))
        {
            return false;
        }

        AcDream.Core.Physics.RetailObjectManagerTail.Run(
            rm.Host?.TargetManager,
            rm.Movement,
            partArrayHandleMovement,
            rm.Host?.PositionManager);
        return IsCurrentOwner(
            record,
            rm,
            objectClockEpoch,
            externalOwnerValid);
    }

    /// <summary>
    /// Applies a sweep outcome to the body. A successful sweep commits the
    /// resolved origin and cell and publishes the step velocity. A failed
    /// sweep (no valid position found) keeps the pre-step origin, with the
    /// heading already applied, and zeroes the step velocity; committing the
    /// checked position instead is what ratcheted a creature into a wall one
    /// tick at a time. Returns the committed cell id.
    /// </summary>
    internal static uint CommitSweepOutcome(
        AcDream.Core.Physics.PhysicsBody body,
        in AcDream.Core.Physics.ResolveResult result,
        System.Numerics.Vector3 preIntegratePos,
        float dt,
        uint committedCellId)
    {
        if (result.Ok)
        {
            body.Position = result.Position;
            if (result.CellId != 0)
                committedCellId = result.CellId;
            body.CachedVelocity = dt > 0f
                ? (result.Position - preIntegratePos) / dt
                : System.Numerics.Vector3.Zero;
        }
        else
        {
            body.Position = preIntegratePos;
            body.CachedVelocity = System.Numerics.Vector3.Zero;
        }
        return committedCellId;
    }

    private bool IsCurrentOwner(
        RuntimeEntityRecord record,
        RemoteMotion remote,
        ulong objectClockEpoch,
        System.Func<bool>? externalOwnerValid) =>
        _physics.IsSpatialRemote(record, remote)
        && record.ObjectClockEpoch == objectClockEpoch
        && ReferenceEquals(record.PhysicsBody, remote.Body)
        && (externalOwnerValid?.Invoke() ?? true);

    private static void ApplyPositionManagerDelta(
        AcDream.Core.Physics.PhysicsBody body,
        AcDream.Core.Physics.Motion.MotionDeltaFrame delta)
    {
        if (delta.Origin != System.Numerics.Vector3.Zero)
            body.Position += System.Numerics.Vector3.Transform(delta.Origin, body.Orientation);
        if (!delta.Orientation.IsIdentity)
            body.Orientation = AcDream.Core.Physics.Motion.FrameOps.SetRotate(
                body.Position,
                body.Orientation,
                body.Orientation * delta.Orientation);
    }

    internal void SyncRemoteShadowToBody(
        uint entityId,
        AcDream.Runtime.Physics.IRuntimeRemotePlacement rm,
        int liveCenterX,
        int liveCenterY,
        uint? authoritativeCellId = null)
    {
        SyncRemoteShadowToBody(
            entityId,
            rm.Body,
            liveCenterX,
            liveCenterY,
            authoritativeCellId ?? rm.CellId);
        rm.LastShadowSyncPosition = rm.Body.Position;
        rm.LastShadowSyncOrientation = rm.Body.Orientation;
    }

    internal static bool ShouldSynchronizeShadowPose(
        System.Numerics.Vector3 currentPosition,
        System.Numerics.Quaternion currentOrientation,
        System.Numerics.Vector3 lastPosition,
        System.Numerics.Quaternion lastOrientation)
    {
        if (System.Numerics.Vector3.DistanceSquared(
                currentPosition,
                lastPosition) > 1e-4f)
        {
            return true;
        }

        float currentLengthSquared = currentOrientation.LengthSquared();
        float lastLengthSquared = lastOrientation.LengthSquared();
        if (!float.IsFinite(currentLengthSquared)
            || !float.IsFinite(lastLengthSquared)
            || currentLengthSquared < 1e-12f
            || lastLengthSquared < 1e-12f)
        {
            return true;
        }

        float normalizedDot = MathF.Abs(
            System.Numerics.Quaternion.Dot(
                currentOrientation,
                lastOrientation)
            / MathF.Sqrt(currentLengthSquared * lastLengthSquared));
        return !float.IsFinite(normalizedDot) || normalizedDot < 0.99999f;
    }

    internal static bool ShouldSynchronizeShadow(
        bool cellChanged,
        System.Numerics.Vector3 currentPosition,
        System.Numerics.Quaternion currentOrientation,
        System.Numerics.Vector3 lastPosition,
        System.Numerics.Quaternion lastOrientation) =>
        cellChanged
        || ShouldSynchronizeShadowPose(
            currentPosition,
            currentOrientation,
            lastPosition,
            lastOrientation);

    internal void SyncRemoteShadowToBody(
        uint entityId,
        AcDream.Core.Physics.PhysicsBody body,
        int liveCenterX,
        int liveCenterY,
        uint authoritativeCellId)
    {
        ShadowPositionSynchronizer.Sync(
            _physics.Engine.ShadowObjects,
            entityId,
            body.Position,
            body.Orientation,
            authoritativeCellId,
            liveCenterX,
            liveCenterY);
    }
}
