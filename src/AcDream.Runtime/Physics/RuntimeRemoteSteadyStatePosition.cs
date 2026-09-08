using System;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;

namespace AcDream.Runtime.Physics;

internal static class RuntimeRemoteSteadyStatePosition
{
    private const float BodySnapThreshold = 4f;

    internal const float DiagnosticBodySnapThreshold = BodySnapThreshold;

    internal enum Action : byte
    {
        Snapped,

        Enqueued,
    }

    internal static bool OwnsSteadyState(RuntimeAuthoritativePositionRoute? route) =>
        IsAirborneNoOperation(route) || IsNearInterpolate(route);

    internal static bool IsAirborneNoOperation(
        RuntimeAuthoritativePositionRoute? route) =>
        route is
        {
            Disposition: RuntimeAuthoritativePositionDisposition.NoPositionOperation,
        };

    internal static bool IsNearInterpolate(
        RuntimeAuthoritativePositionRoute? route) =>
        route is
        {
            Disposition: RuntimeAuthoritativePositionDisposition.Interpolate,
        };

    internal static Action ApplyInterpolate(
        RemoteMotion remote,
        Vector3 worldPosition,
        Quaternion orientation,
        bool isMovingTo,
        bool willBeDrTicked)
    {
        ArgumentNullException.ThrowIfNull(remote);

        bool firstUp = remote.LastServerPosTime <= 0.0;
        float bodyToTarget = Vector3.Distance(remote.Body.Position, worldPosition);
        if (firstUp || !willBeDrTicked || bodyToTarget > BodySnapThreshold)
        {
            if (AcDream.Core.Physics.PhysicsDiagnostics.ShouldLogRemoteSlide(
                    AcDream.Core.Physics.PhysicsDiagnostics.RemoteSlideAttributionGuid))
            {
                (int depth, int failCount) =
                    remote.Interp.DiagnosticInterpolationState;
                AcDream.Core.Physics.PhysicsDiagnostics.LogRemoteSlideBodySnap(
                    guid: AcDream.Core.Physics.PhysicsDiagnostics
                        .RemoteSlideAttributionGuid,
                    firstUp: firstUp,
                    willBeDrTicked: willBeDrTicked,
                    bodyToTarget: bodyToTarget,
                    threshold: BodySnapThreshold,
                    bodyPosition: remote.Body.Position,
                    targetPosition: worldPosition,
                    interpQueueDepth: depth,
                    interpFailCount: failCount);
            }
            remote.Interp.Clear();
            remote.Body.Position = worldPosition;
            remote.Body.Orientation = orientation;
            return Action.Snapped;
        }

        Quaternion? immediate = remote.Interp.Enqueue(
            worldPosition,
            orientation,
            isMovingTo,
            remote.Body.Position,
            remote.Body.Orientation);
        if (immediate is { } close)
            remote.Body.Orientation = close;
        if (AcDream.Core.Physics.PhysicsDiagnostics.ShouldLogRemoteSlide(
                AcDream.Core.Physics.PhysicsDiagnostics.RemoteSlideAttributionGuid))
        {
            (int depth, int failCount) =
                remote.Interp.DiagnosticInterpolationState;
            AcDream.Core.Physics.PhysicsDiagnostics.LogRemoteSlideEnqueue(
                guid: AcDream.Core.Physics.PhysicsDiagnostics
                    .RemoteSlideAttributionGuid,
                bodyToTarget: bodyToTarget,
                targetPosition: worldPosition,
                interpQueueDepth: depth,
                interpFailCount: failCount);
        }
        return Action.Enqueued;
    }

    internal static bool TryArmConstraintAfterOperation(
        RuntimeRemoteAcceptedPositionArm arm,
        RemoteMotion remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        bool arms = arm switch
        {
            RuntimeRemoteAcceptedPositionArm.TeleportPlacement => true,
            RuntimeRemoteAcceptedPositionArm.FarSnapPlacement => true,
            RuntimeRemoteAcceptedPositionArm.NearInterpolate => true,
            RuntimeRemoteAcceptedPositionArm.UnroutedCatchUp => true,
            RuntimeRemoteAcceptedPositionArm.AirborneNoOperation => false,
            _ => false,
        };
        if (!arms || remote.Host is not { } host)
            return false;

        ArmConstraintAfterOperation(host);
        return true;
    }

    internal static void ArmConstraintAfterOperation(EntityPhysicsHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Position anchor = host.Position;
        host.PositionManager.ConstrainTo(
            anchor,
            ConstraintDistance.GetStartConstraintDistance(anchor.ObjCellId),
            ConstraintDistance.GetMaxConstraintDistance(anchor.ObjCellId));
    }
}
