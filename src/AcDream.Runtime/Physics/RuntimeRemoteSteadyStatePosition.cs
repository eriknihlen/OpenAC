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

    /// <summary>
    /// True when <see cref="ApplyInterpolate"/> would move the body straight
    /// to the wire position rather than queue it: the first sample, a body
    /// that will not be ticked, or a body too far from the target.
    /// </summary>
    internal static bool WouldSnap(
        RemoteMotion remote,
        Vector3 worldPosition,
        bool willBeDrTicked)
    {
        ArgumentNullException.ThrowIfNull(remote);
        bool firstUp = remote.LastServerPosTime <= 0.0;
        float bodyToTarget = Vector3.Distance(remote.Body.Position, worldPosition);
        return firstUp || !willBeDrTicked || bodyToTarget > BodySnapThreshold;
    }

    internal static Action ApplyInterpolate(
        RemoteMotion remote,
        Vector3 worldPosition,
        Quaternion orientation,
        bool isMovingTo,
        bool willBeDrTicked,
        uint targetCellId = 0u)
    {
        ArgumentNullException.ThrowIfNull(remote);

        bool firstUp = remote.LastServerPosTime <= 0.0;
        float bodyToTarget = Vector3.Distance(remote.Body.Position, worldPosition);
        if (WouldSnap(remote, worldPosition, willBeDrTicked))
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
            remote.Body.Orientation,
            targetCellId,
            (remote.CellId & 0xFFFFu) >= 0x100u ? 20f : 100f);
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
