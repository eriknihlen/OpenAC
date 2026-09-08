using AcDream.App.World;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal sealed class LiveStaticAnimationResidency
{
    private readonly ILiveEntityRuntimeSource _runtime;

    public LiveStaticAnimationResidency(ILiveEntityRuntimeSource runtime) =>
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public bool IsResident(WorldEntity entity) =>
        entity.ServerGuid == 0
        || (_runtime.Current?.TryGetRecord(entity.ServerGuid, out LiveEntityRecord resident) == true
            && resident.ProjectionKind is LiveEntityProjectionKind.World
            && ReferenceEquals(resident.WorldEntity, entity)
            && resident.IsSpatiallyProjected
            && resident.IsSpatiallyVisible
            && resident.FullCellId != 0);

    public ulong ProjectionVersion(WorldEntity entity) =>
        entity.ServerGuid == 0
            ? 0UL
            : _runtime.Current?.TryGetRecord(entity.ServerGuid, out LiveEntityRecord resident) == true
              && ReferenceEquals(resident.WorldEntity, entity)
                ? resident.ProjectionMutationVersion
                : ulong.MaxValue;
}
