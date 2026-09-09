using AcDream.Core.World;
using AcDream.App.Physics;
using AcDream.App.World;

namespace AcDream.App.Input;

internal interface ILocalPlayerProjectionRuntime
{
    WorldEntity? ResolveEntity();
    int LiveCenterX { get; }
    int LiveCenterY { get; }
    void SyncShadow(WorldEntity entity, uint cellId);
    void Rebucket(uint serverGuid, uint landblockId);
    bool IsCurrentVisibleProjection(WorldEntity entity);
    void SuspendShadow(WorldEntity entity);
}

internal sealed class LiveLocalPlayerProjectionRuntime
    : ILocalPlayerProjectionRuntime
{
    private readonly LiveEntityRuntime _liveEntities;
    private readonly ILocalPlayerIdentitySource _identity;
    private readonly LiveWorldOriginState _origin;
    private readonly LocalPlayerShadowSynchronizer _shadow;

    public LiveLocalPlayerProjectionRuntime(
        LiveEntityRuntime liveEntities,
        ILocalPlayerIdentitySource identity,
        LiveWorldOriginState origin,
        LocalPlayerShadowSynchronizer shadow)
    {
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _shadow = shadow ?? throw new ArgumentNullException(nameof(shadow));
    }

    public WorldEntity? ResolveEntity() =>
        _liveEntities.TryGetWorldEntity(_identity.ServerGuid, out var entity)
            ? entity
            : null;

    public int LiveCenterX => _origin.CenterX;
    public int LiveCenterY => _origin.CenterY;

    public void SyncShadow(WorldEntity entity, uint cellId) =>
        _shadow.Sync(entity, cellId);

    public void Rebucket(uint serverGuid, uint landblockId) =>
        _liveEntities.RebucketLiveEntity(serverGuid, landblockId);

    public bool IsCurrentVisibleProjection(WorldEntity entity) =>
        _shadow.IsCurrentVisibleProjection(entity);

    public void SuspendShadow(WorldEntity entity) => _shadow.Suspend(entity);
}

public sealed class LocalPlayerProjectionController
{
    private readonly ILocalPlayerProjectionRuntime _runtime;

    internal LocalPlayerProjectionController(ILocalPlayerProjectionRuntime runtime) =>
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public void Project(
        PlayerMovementController controller,
        MovementResult movement,
        bool hidden)
    {
        WorldEntity? entity = _runtime.ResolveEntity();
        if (entity is null)
            return;

        entity.SetPosition(movement.RenderPosition);
        entity.ParentCellId = movement.CellId;
        entity.Rotation = controller.BodyOrientation;

        uint currentLandblock;
        if (movement.CellId != 0 && (movement.CellId & 0xFFFFu) >= 0x0100u)
        {
            currentLandblock = (movement.CellId & 0xFFFF0000u) | 0xFFFFu;
        }
        else
        {
            System.Numerics.Vector3 position = controller.Position;
            int landblockX = _runtime.LiveCenterX + (int)Math.Floor(position.X / 192f);
            int landblockY = _runtime.LiveCenterY + (int)Math.Floor(position.Y / 192f);
            currentLandblock = (uint)((landblockX << 24) | (landblockY << 16) | 0xFFFF);
        }

        if (controller.State == PlayerState.PortalSpace)
            return;

        _runtime.Rebucket(entity.ServerGuid, currentLandblock);
        if (hidden
            || movement.CellId == 0
            || !_runtime.IsCurrentVisibleProjection(entity))
        {
            _runtime.SuspendShadow(entity);
            return;
        }

        _runtime.SyncShadow(entity, movement.CellId);
    }
}
