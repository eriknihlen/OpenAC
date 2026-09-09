using System.Numerics;

namespace AcDream.Core.Physics;

public readonly record struct ShadowShape
{
    private ShadowShape(
        uint gfxObjId,
        Vector3 localPosition,
        Quaternion localRotation,
        float scale,
        ShadowCollisionType collisionType,
        float radius,
        float cylHeight,
        Vector3 boundsCenter,
        Vector3 localBoundsMin,
        Vector3 localBoundsMax)
    {
        GfxObjId = gfxObjId;
        LocalPosition = localPosition;
        LocalRotation = localRotation;
        Scale = scale;
        CollisionType = collisionType;
        Radius = radius;
        CylHeight = cylHeight;
        BoundsCenter = boundsCenter;
        LocalBoundsMin = localBoundsMin;
        LocalBoundsMax = localBoundsMax;
    }

    /// <summary>Source GfxObj id, for the BSP walk and for diagnostics.</summary>
    public uint GfxObjId { get; }

    /// <summary>Part placement in the entity's own frame, entity-scaled.</summary>
    public Vector3 LocalPosition { get; }

    /// <summary>Part orientation in the entity's own frame.</summary>
    public Quaternion LocalRotation { get; }

    /// <summary>The entity (or part) scale already applied to the geometry.</summary>
    public float Scale { get; }

    public ShadowCollisionType CollisionType { get; }

    public float Radius { get; }

    public float CylHeight { get; }

    public Vector3 BoundsCenter { get; }

    public Vector3 LocalBoundsMin { get; }

    public Vector3 LocalBoundsMax { get; }

    public static ShadowShape Bsp(
        uint gfxObjId,
        Vector3 localPosition,
        Quaternion localRotation,
        float scale,
        ShadowPartGeometry localGeometry)
        => new(
            gfxObjId,
            localPosition,
            localRotation,
            scale,
            ShadowCollisionType.BSP,
            localGeometry.Sphere.Radius * scale,
            0f,
            localGeometry.Sphere.Origin * scale,
            localGeometry.BoxMin * scale,
            localGeometry.BoxMax * scale);

    public static ShadowShape Cylinder(
        uint gfxObjId,
        Vector3 localPosition,
        Quaternion localRotation,
        float scale,
        float radius,
        float cylHeight)
        => new(
            gfxObjId,
            localPosition,
            localRotation,
            scale,
            ShadowCollisionType.Cylinder,
            radius,
            cylHeight,
            Vector3.Zero,
            new Vector3(-radius, -radius, 0f),
            new Vector3(radius, radius, cylHeight));

    public static ShadowShape Sphere(
        uint gfxObjId,
        Vector3 localPosition,
        Quaternion localRotation,
        float scale,
        float radius)
        => new(
            gfxObjId,
            localPosition,
            localRotation,
            scale,
            ShadowCollisionType.Sphere,
            radius,
            0f,
            Vector3.Zero,
            new Vector3(-radius),
            new Vector3(radius));
}
