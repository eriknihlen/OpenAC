using AcDream.Core.Physics;

namespace AcDream.Runtime.Physics;

internal static class RuntimeRemoteTeleportPosition
{
    internal static bool OwnsTeleportPlacement(
        RuntimeAuthoritativePositionRoute? route) =>
        route is
        {
            Disposition: RuntimeAuthoritativePositionDisposition.SetPosition,
            OperationKind: RuntimeSetPositionOperationKind.RemoteAuthoritative,
        }
        && (route.Value.SetPositionFlags & PhysicsSetPositionFlags.Teleport)
            != 0;
}
