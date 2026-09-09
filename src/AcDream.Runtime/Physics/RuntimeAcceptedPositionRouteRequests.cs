using System;
using AcDream.Core.Net;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Physics;

internal static class RuntimeAcceptedPositionRouteRequests
{
    internal static RuntimeAcceptedPositionRouteRequest Build(
        RuntimeGenerationToken generation,
        RuntimeEntityRecord canonical,
        RuntimeEntityKey key,
        in WorldSession.EntityPositionUpdate update,
        RuntimePositionEntityKind entityKind,
        RuntimeAcceptedPositionSource source,
        PositionTimestampDisposition disposition,
        ushort previousTeleportSequence,
        ushort acceptedTeleportSequence,
        float playerDistance,
        bool usePositionFromServer) =>
        Build(
            generation,
            canonical,
            key,
            update,
            entityKind,
            source,
            disposition,
            previousTeleportSequence,
            acceptedTeleportSequence,
            playerDistance,
            usePositionFromServer,
            committedCellId: canonical.FullCellId);

    internal static RuntimeAcceptedPositionRouteRequest Build(
        RuntimeGenerationToken generation,
        RuntimeEntityRecord canonical,
        RuntimeEntityKey key,
        in WorldSession.EntityPositionUpdate update,
        RuntimePositionEntityKind entityKind,
        RuntimeAcceptedPositionSource source,
        PositionTimestampDisposition disposition,
        ushort previousTeleportSequence,
        ushort acceptedTeleportSequence,
        float playerDistance,
        bool usePositionFromServer,
        uint? committedCellId)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        var authority = new RuntimeAuthoritativePositionAuthority(
            generation,
            key,
            canonical.PositionAuthorityVersion,
            update.PositionSequence,
            previousTeleportSequence,
            acceptedTeleportSequence,
            disposition);

        bool hasAnimations = (canonical.Snapshot.MotionTableId
                ?? canonical.Snapshot.Physics?.MotionTableId) is { } motionTableId
            && motionTableId != 0u;

        return new RuntimeAcceptedPositionRouteRequest(
            authority,
            entityKind,
            source,
            update.Position,
            update.PlacementId,
            update.Velocity,
            committedCellId,
            update.IsGrounded,
            playerDistance,
            usePositionFromServer,
            hasAnimations,
            new RuntimePositionPlacementFacts(
                canonical.FinalPhysicsState,
                canonical.Snapshot.SetupTableId is not null));
    }

    internal static bool TryBuild(
        RuntimeGenerationToken generation,
        RuntimeEntityRecord canonical,
        in WorldSession.EntityPositionUpdate update,
        RuntimePositionEntityKind entityKind,
        RuntimeAcceptedPositionSource source,
        PositionTimestampDisposition disposition,
        ushort previousTeleportSequence,
        ushort acceptedTeleportSequence,
        float? playerDistance,
        bool usePositionFromServer,
        out RuntimeAcceptedPositionRouteRequest request)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        if (canonical.Key is not { } key || playerDistance is not { } distance)
        {
            request = default;
            return false;
        }

        request = Build(
            generation,
            canonical,
            key,
            update,
            entityKind,
            source,
            disposition,
            previousTeleportSequence,
            acceptedTeleportSequence,
            distance,
            usePositionFromServer);
        return true;
    }

    internal static bool TryBuild(
        RuntimeGenerationToken generation,
        RuntimeEntityRecord canonical,
        in WorldSession.EntityPositionUpdate update,
        RuntimePositionEntityKind entityKind,
        RuntimeAcceptedPositionSource source,
        PositionTimestampDisposition disposition,
        ushort previousTeleportSequence,
        ushort acceptedTeleportSequence,
        float? playerDistance,
        bool usePositionFromServer,
        uint? committedCellId,
        out RuntimeAcceptedPositionRouteRequest request)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        if (canonical.Key is not { } key
            || playerDistance is not { } distance
            || committedCellId is null)
        {
            request = default;
            return false;
        }

        request = Build(
            generation,
            canonical,
            key,
            update,
            entityKind,
            source,
            disposition,
            previousTeleportSequence,
            acceptedTeleportSequence,
            distance,
            usePositionFromServer,
            committedCellId);
        return true;
    }
}
