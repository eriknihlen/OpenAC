using AcDream.Core.Physics;

namespace AcDream.Runtime.Physics;

internal enum RuntimeRemoteAcceptedPositionArm : byte
{
    AirborneNoOperation,

    NearInterpolate,

    FarSnapPlacement,

    TeleportPlacement,

    UnroutedCatchUp,
}

internal static class RuntimeRemoteFarSnapPosition
{
    internal static bool OwnsFarSnap(RuntimeAuthoritativePositionRoute? route) =>
        route is
        {
            Disposition:
                RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            OperationKind:
                RuntimeSetPositionOperationKind.RemoteAuthoritative,
        }
        && (route.Value.SetPositionFlags & PhysicsSetPositionFlags.Teleport)
            != 0;

    internal static RuntimeRemoteAcceptedPositionArm ResolveArm(
        RuntimeAuthoritativePositionRoute? route)
    {
        if (RuntimeRemoteSteadyStatePosition.IsAirborneNoOperation(route))
            return RuntimeRemoteAcceptedPositionArm.AirborneNoOperation;
        if (RuntimeRemoteSteadyStatePosition.IsNearInterpolate(route))
            return RuntimeRemoteAcceptedPositionArm.NearInterpolate;
        return OwnsFarSnap(route)
            ? RuntimeRemoteAcceptedPositionArm.FarSnapPlacement
            : RuntimeRemoteAcceptedPositionArm.UnroutedCatchUp;
    }
}
