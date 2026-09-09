using System.Numerics;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;

namespace AcDream.App.Physics;

internal readonly record struct AcceptedMotionNetworkUpdate(
    WorldSession.EntityMotionUpdate Update,
    LiveEntityRecord Record,
    AcceptedPhysicsTimestamps Timestamps,
    ulong MovementAuthorityVersion,
    ulong VelocityAuthorityVersion);

internal readonly record struct AcceptedVectorNetworkUpdate(
    VectorUpdate.Parsed Update,
    LiveEntityRecord Record,
    ulong VectorAuthorityVersion,
    ulong VelocityAuthorityVersion);

internal readonly record struct AcceptedStateNetworkUpdate(
    SetState.Parsed Update,
    LiveEntityRecord Record,
    ulong StateAuthorityVersion);

internal readonly record struct AcceptedPositionNetworkUpdate(
    WorldSession.EntityPositionUpdate Update,
    RuntimeEntityRecord Canonical,
    WorldSession.EntitySpawn Spawn,
    AcceptedPhysicsTimestamps Timestamps,
    PositionTimestampDisposition TimestampDisposition,
    ulong PositionAuthorityVersion,
    ulong VelocityAuthorityVersion);

internal sealed class LiveEntityInboundAuthorityGate
{
    private readonly LiveEntityRuntime _liveEntities;
    private readonly Action<uint, AcceptedPhysicsTimestamps> _publishTimestamps;

    public LiveEntityInboundAuthorityGate(
        LiveEntityRuntime liveEntities,
        Action<uint, AcceptedPhysicsTimestamps> publishTimestamps)
    {
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _publishTimestamps = publishTimestamps
            ?? throw new ArgumentNullException(nameof(publishTimestamps));
    }

    internal uint? LastLivePlayerLandblockId { get; private set; }

    internal void ResetSessionState() => LastLivePlayerLandblockId = null;

    internal bool TryAcceptMotion(
        WorldSession.EntityMotionUpdate update,
        bool retainPayload,
        out AcceptedMotionNetworkUpdate accepted,
        out bool timestampAccepted)
    {
        accepted = default;
        timestampAccepted = _liveEntities.TryApplyMotion(
            update,
            retainPayload,
            out _,
            out AcceptedPhysicsTimestamps timestamps);
        if (!timestampAccepted)
            return false;

        LiveEntityRecord? record = null;
        ulong movementAuthorityVersion = 0;
        ulong velocityAuthorityVersion = 0;
        if (retainPayload)
        {
            if (!_liveEntities.TryGetRecord(update.Guid, out record))
                return false;
            movementAuthorityVersion = record.MovementAuthorityVersion;
            velocityAuthorityVersion = record.VelocityAuthorityVersion;
        }

        _publishTimestamps(update.Guid, timestamps);
        if (!retainPayload)
            return false;
        if (!_liveEntities.IsCurrentMovementAuthority(
                record!,
                movementAuthorityVersion)
            || !_liveEntities.IsCurrentVelocityAuthority(
                record!,
                velocityAuthorityVersion))
        {
            return false;
        }

        accepted = new AcceptedMotionNetworkUpdate(
            update,
            record!,
            timestamps,
            movementAuthorityVersion,
            velocityAuthorityVersion);
        return true;
    }

    internal bool TryAcceptVector(
        VectorUpdate.Parsed update,
        bool payloadIsValid,
        out AcceptedVectorNetworkUpdate accepted)
    {
        accepted = default;
        if (!payloadIsValid
            || !_liveEntities.TryApplyVector(update, out _)
            || !_liveEntities.TryGetRecord(update.Guid, out LiveEntityRecord record))
        {
            return false;
        }

        accepted = new AcceptedVectorNetworkUpdate(
            update,
            record,
            record.VectorAuthorityVersion,
            record.VelocityAuthorityVersion);
        return true;
    }

    internal bool TryAcceptState(
        SetState.Parsed update,
        out AcceptedStateNetworkUpdate accepted)
    {
        accepted = default;
        if (!_liveEntities.TryApplyState(update, out _, out _)
            || !_liveEntities.TryGetRecord(update.Guid, out LiveEntityRecord record))
        {
            return false;
        }

        accepted = new AcceptedStateNetworkUpdate(
            update,
            record,
            record.StateAuthorityVersion);
        return true;
    }

    internal bool TryAcceptPosition(
        WorldSession.EntityPositionUpdate update,
        uint localPlayerGuid,
        Quaternion? forcePositionRotation,
        Vector3? currentLocalVelocity,
        bool payloadIsValid,
        out AcceptedPositionNetworkUpdate accepted)
    {
        accepted = default;
        if (!payloadIsValid)
            return false;

        bool isLocalPlayer = update.Guid == localPlayerGuid;
        bool known = _liveEntities.TryApplyPosition(
            update,
            isLocalPlayer,
            isLocalPlayer ? forcePositionRotation : null,
            isLocalPlayer ? currentLocalVelocity : null,
            out PositionTimestampDisposition disposition,
            out WorldSession.EntitySpawn spawn,
            out AcceptedPhysicsTimestamps timestamps);
        if (!known)
        {
            if (isLocalPlayer)
                LastLivePlayerLandblockId = update.Position.LandblockId;
            return false;
        }

        RuntimeEntityRecord? canonical = null;
        ulong positionAuthorityVersion = 0;
        ulong velocityAuthorityVersion = 0;
        if (disposition is not PositionTimestampDisposition.Rejected)
        {
            if (!_liveEntities.TryGetCanonical(update.Guid, out canonical))
                return false;
            positionAuthorityVersion = canonical.PositionAuthorityVersion;
            velocityAuthorityVersion = canonical.VelocityAuthorityVersion;
        }

        _publishTimestamps(update.Guid, timestamps);
        if (disposition is PositionTimestampDisposition.Rejected)
            return false;
        if (!_liveEntities.IsCurrentPositionAuthority(
                canonical!,
                positionAuthorityVersion))
        {
            return false;
        }

        accepted = new AcceptedPositionNetworkUpdate(
            update,
            canonical!,
            spawn,
            timestamps,
            disposition,
            positionAuthorityVersion,
            velocityAuthorityVersion);
        return true;
    }

    internal void ObserveAcceptedLocalPosition(uint landblockId) =>
        LastLivePlayerLandblockId = landblockId;
}
