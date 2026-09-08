using System.Collections.Immutable;
using System.Numerics;

namespace AcDream.Core.Physics;

internal enum PhysicsSetPositionError
{
    Ok = 0,
    GeneralFailure = 1,
    NoValidPosition = 2,
    NoCell = 3,
    Collided = 4,
    InvalidArguments = 0x100,
}

internal enum PhysicsResidenceDisposition
{
    Committed,
    DeferredCell,
    Unchanged,
}

internal enum PhysicsShadowCommitAction
{
    None,
    Recalculate,
    Replace,
    Preserve,
}

internal readonly record struct PhysicsSetPositionCollisionReport(
    bool ContactPlaneValid,
    Plane ContactPlane,
    uint ContactPlaneCellId,
    bool ContactPlaneIsWater,
    bool LastKnownContactPlaneValid,
    Plane LastKnownContactPlane,
    uint LastKnownContactPlaneCellId,
    bool LastKnownContactPlaneIsWater,
    bool SlidingNormalValid,
    Vector3 SlidingNormal,
    bool CollisionNormalValid,
    Vector3 CollisionNormal,
    bool CollidedWithEnvironment,
    int FramesStationaryFall,
    Vector3 AdjustOffset,
    uint? LastCollidedObjectId,
    ImmutableArray<uint> CollidedObjectIds);

[Flags]
internal enum PhysicsSetPositionFlags : uint
{
    None = 0,
    Placement = 0x001,
    Teleport = 0x002,
    Restore = 0x004,
    Slide = 0x010,
    DoNotCreateCells = 0x020,
    Scatter = 0x100,
    RandomScatter = 0x200,
    Line = 0x400,
    SendPositionEvent = 0x1000,
}

internal enum PhysicsPlacementClass
{
    Ordinary,
    Hook,
    Storage,
    Corpse,
}

internal readonly record struct PhysicsSetPositionRequest(
    Vector3 Position,
    Quaternion Orientation,
    uint CellId,
    Vector3 CellLocalPosition,
    ImmutableArray<FlatCollisionSphere> Spheres,
    float Scale,
    float StepUpHeight,
    float StepDownHeight,
    PhysicsStateFlags MoverPhysicsState = PhysicsStateFlags.None,
    ObjectInfoState MoverFlags = ObjectInfoState.None,
    uint MovingEntityId = 0u,
    PhysicsPlacementClass PlacementClass = PhysicsPlacementClass.Ordinary,
    PhysicsSetPositionFlags Flags = PhysicsSetPositionFlags.Placement,
    Vector3 Line = default,
    float ScatterRadiusX = 0f,
    float ScatterRadiusY = 0f,
    uint ScatterAttempts = 0u,
    uint? CurrentCellId = null);

internal readonly record struct PhysicsSetPositionResult(
    PhysicsSetPositionError Error,
    PhysicsResidenceDisposition Residence,
    Vector3 Position,
    Quaternion Orientation,
    uint CellId,
    Vector3 CellLocalPosition,
    bool InContact = false,
    bool OnWalkable = false,
    Plane ContactPlane = default,
    uint ContactPlaneCellId = 0u,
    bool ContactPlaneIsWater = false,
    bool SlidingNormalValid = false,
    Vector3 SlidingNormal = default,
    bool CollisionNormalValid = false,
    Vector3 CollisionNormal = default,
    int FramesStationaryFall = 0,
    bool CollidedWithEnvironment = false,
    bool CollisionHandlerResult = false,
    bool CellChanged = false,
    PhysicsShadowCommitAction ShadowAction = PhysicsShadowCommitAction.None,
    ImmutableArray<uint> CrossCellIds = default,
    ImmutableArray<uint> CollidedObjectIds = default,
    ImmutableArray<uint> QueriedCellIds = default)
{
    internal bool IsSuccessful => Error == PhysicsSetPositionError.Ok;
    internal bool IsCommitted =>
        IsSuccessful && Residence == PhysicsResidenceDisposition.Committed;
    internal bool IsDeferred =>
        IsSuccessful && Residence == PhysicsResidenceDisposition.DeferredCell;
}
