using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.World;

namespace AcDream.App.Physics;

internal static class LiveEntityShadowPublisher
{
    internal static bool TryPublishRemote(
        LiveEntityRuntime runtime,
        LiveEntityRecord record,
        WorldEntity entity,
        IRuntimeRemoteMotion remote,
        ulong positionAuthorityVersion,
        Action publish)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(publish);

        if (!CanPublish(
                runtime,
                record,
                entity,
                remote,
                positionAuthorityVersion))
        {
            return false;
        }

        publish();
        return CanPublish(
            runtime,
            record,
            entity,
            remote,
            positionAuthorityVersion);
    }

    private static bool CanPublish(
        LiveEntityRuntime runtime,
        LiveEntityRecord record,
        WorldEntity entity,
        IRuntimeRemoteMotion remote,
        ulong positionAuthorityVersion) =>
        (record.FinalPhysicsState & PhysicsStateFlags.Hidden) == 0
        && ReferenceEquals(record.WorldEntity, entity)
        && runtime.IsCurrentPositionAuthority(record, positionAuthorityVersion)
        && runtime.IsCurrentSpatialRemoteMotion(record, remote);
}
