using AcDream.App.Physics;
using AcDream.App.Rendering.Vfx;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal sealed class StaticLiveRootCommitter
{
    private readonly ILiveEntityRuntimeSource _runtime;
    private readonly ShadowObjectRegistry _shadows;
    private readonly LiveWorldOriginState _origin;
    private readonly EntityEffectPoseRegistry _effectPoses;

    public StaticLiveRootCommitter(
        ILiveEntityRuntimeSource runtime,
        ShadowObjectRegistry shadows,
        LiveWorldOriginState origin,
        EntityEffectPoseRegistry effectPoses)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _shadows = shadows ?? throw new ArgumentNullException(nameof(shadows));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _effectPoses = effectPoses ?? throw new ArgumentNullException(nameof(effectPoses));
    }

    public bool Commit(WorldEntity entity, PhysicsBody body)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(body);
        LiveEntityRuntime? runtime = _runtime.Current;
        if (runtime is null
            || !runtime.TryGetRecord(entity.ServerGuid, out LiveEntityRecord record)
            || !ReferenceEquals(record.WorldEntity, entity)
            || !ReferenceEquals(record.PhysicsBody, body))
        {
            return false;
        }

        _effectPoses.UpdateRoot(entity);

        if (!runtime.TryGetRecord(entity.ServerGuid, out LiveEntityRecord current)
            || !ReferenceEquals(current, record)
            || !ReferenceEquals(current.WorldEntity, entity)
            || !ReferenceEquals(current.PhysicsBody, body))
        {
            return false;
        }

        if (record.ProjectionKind is not LiveEntityProjectionKind.World
            || !record.IsSpatiallyProjected
            || !record.IsSpatiallyVisible
            || runtime.IsHidden(entity.ServerGuid))
        {
            return true;
        }

        uint cellId = body.CellPosition.ObjCellId != 0
            ? body.CellPosition.ObjCellId
            : record.FullCellId;
        ShadowPositionSynchronizer.Sync(
            _shadows,
            entity.Id,
            body.Position,
            body.Orientation,
            cellId,
            _origin.CenterX,
            _origin.CenterY);
        return true;
    }
}
