using System.Numerics;

namespace AcDream.Core.Physics;

public readonly record struct ShadowPartGeometry
{
    private ShadowPartGeometry(
        FlatCollisionSphere sphere,
        Vector3 boxMin,
        Vector3 boxMax)
    {
        Sphere = sphere;
        BoxMin = boxMin;
        BoxMax = boxMax;
    }

    public FlatCollisionSphere Sphere { get; }

    public Vector3 BoxMin { get; }

    public Vector3 BoxMax { get; }

    public static ShadowPartGeometry Create(
        FlatCollisionSphere sphere,
        FlatGfxObjVisualBounds? visualBounds)
    {
        if (visualBounds is { } bounds)
            return new ShadowPartGeometry(sphere, bounds.Min, bounds.Max);

        var extent = new Vector3(sphere.Radius);
        return new ShadowPartGeometry(
            sphere,
            sphere.Origin - extent,
            sphere.Origin + extent);
    }
}

public readonly record struct ShadowPartBox
{
    private ShadowPartBox(
        Vector3 localMin,
        Vector3 localMax,
        Vector3 worldPosition,
        Quaternion worldRotation)
    {
        LocalMin = localMin;
        LocalMax = localMax;
        WorldPosition = worldPosition;
        WorldRotation = worldRotation;
    }

    /// <summary>Authored box minimum in the part's own frame, entity-scaled.</summary>
    public Vector3 LocalMin { get; }

    /// <summary>Authored box maximum in the part's own frame, entity-scaled.</summary>
    public Vector3 LocalMax { get; }

    public Vector3 WorldPosition { get; }

    public Quaternion WorldRotation { get; }

    public static ShadowPartBox FromShape(
        in ShadowShape shape,
        Vector3 entityWorldPosition,
        Quaternion entityWorldRotation)
        => new(
            shape.LocalBoundsMin,
            shape.LocalBoundsMax,
            entityWorldPosition
                + Vector3.Transform(shape.LocalPosition, entityWorldRotation),
            entityWorldRotation * shape.LocalRotation);

    public void RefitTo(Vector3 frameOrigin, out Vector3 min, out Vector3 max)
    {
        Vector3 offset = WorldPosition - frameOrigin;
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            var local = new Vector3(
                (corner & 1) == 0 ? LocalMin.X : LocalMax.X,
                (corner & 2) == 0 ? LocalMin.Y : LocalMax.Y,
                (corner & 4) == 0 ? LocalMin.Z : LocalMax.Z);
            Vector3 world = Vector3.Transform(local, WorldRotation) + offset;
            min = Vector3.Min(min, world);
            max = Vector3.Max(max, world);
        }
    }

    public void RefitToLocal(Matrix4x4 worldToLocal, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            var local = new Vector3(
                (corner & 1) == 0 ? LocalMin.X : LocalMax.X,
                (corner & 2) == 0 ? LocalMin.Y : LocalMax.Y,
                (corner & 4) == 0 ? LocalMin.Z : LocalMax.Z);
            Vector3 world = Vector3.Transform(local, WorldRotation) + WorldPosition;
            Vector3 dest = Vector3.Transform(world, worldToLocal);
            min = Vector3.Min(min, dest);
            max = Vector3.Max(max, dest);
        }
    }
}
