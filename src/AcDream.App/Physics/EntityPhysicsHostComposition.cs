using System.Numerics;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Physics;

namespace AcDream.App.Physics;

internal static class EntityPhysicsHostComposition
{
    internal static EntityPhysicsHost InstallOrRebind(
        LiveEntityRuntime runtime,
        LiveEntityRecord expectedRecord,
        EntityPhysicsHost configuration)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(expectedRecord);
        ArgumentNullException.ThrowIfNull(configuration);
        bool ProjectionIsCurrent() =>
            runtime.TryGetRecord(
                expectedRecord.ServerGuid,
                out LiveEntityRecord current)
            && ReferenceEquals(current, expectedRecord);
        return runtime.Physics.InstallOrRebindPhysicsHost(
            expectedRecord.Canonical,
            configuration,
            ProjectionIsCurrent);
    }

    internal static EntityPhysicsHost SelectStableHostWithoutRebind(
        LiveEntityRuntime runtime,
        LiveEntityRecord expectedRecord,
        EntityPhysicsHost configuration)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(expectedRecord);
        ArgumentNullException.ThrowIfNull(configuration);
        bool ProjectionIsCurrent() =>
            runtime.TryGetRecord(
                expectedRecord.ServerGuid,
                out LiveEntityRecord current)
            && ReferenceEquals(current, expectedRecord);
        return runtime.Physics.SelectStablePhysicsHostWithoutRebind(
            expectedRecord.Canonical,
            configuration,
            ProjectionIsCurrent);
    }

    internal static EntityPhysicsHost CreateMinimal(
        LiveEntityRecord record,
        Func<uint, IPhysicsObjHost?> resolve,
        Func<double> now)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(now);
        return new EntityPhysicsHost(
            record.ServerGuid,
            getPosition: () => new Position(
                record.FullCellId,
                record.WorldEntity?.Position
                    ?? record.PhysicsBody?.Position
                    ?? Vector3.Zero,
                record.WorldEntity?.Rotation
                    ?? record.PhysicsBody?.Orientation
                    ?? Quaternion.Identity),
            getVelocity: () => record.PhysicsBody?.Velocity ?? Vector3.Zero,
            getRadius: () => 0f,
            inContact: () => record.PhysicsBody?.InContact ?? true,
            minterpMaxSpeed: () => null,
            curTime: now,
            physicsTimerTime: now,
            getObjectA: resolve,
            handleUpdateTarget: _ => { },
            interruptCurrentMovement: () => { });
    }
}
