using System.Numerics;

namespace AcDream.Core.Physics;

public readonly record struct ResolveResult(
    Vector3 Position,
    uint CellId,
    bool IsOnGround,
    bool CollisionNormalValid = false,
    /// <summary>Outward surface normal of the wall the sphere hit. Used
    /// by the velocity-reflection step. Pointing away from the wall.</summary>
    Vector3 CollisionNormal = default,
    bool Ok = true,
    Quaternion Orientation = default,
    bool InContact = false,
    bool OnWalkable = false,
    Plane ContactPlane = default,
    uint ContactPlaneCellId = 0,
    bool ContactPlaneIsWater = false)
{
    public uint LastCollidedObjectId { get; init; }

    /// <summary>Whether resident environment geometry blocked the sweep.</summary>
    public bool CollidedWithEnvironment { get; init; }
}
