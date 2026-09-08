using System.Numerics;

namespace AcDream.Core.Physics;

public readonly record struct PortalPlane(
    Vector3 Normal,
    float D,
    uint TargetCellId,
    uint OwnerCellId,    // the EnvCell that owns this portal
    ushort Flags,        // PortalFlags value
    Vector3 Centroid,
    float Radius)        // bounding radius of the portal polygon
{
    public static PortalPlane FromVertices(
        ReadOnlySpan<Vector3> vertices,
        uint targetCellId, uint ownerCellId, ushort flags)
    {
        if (vertices.Length < 3)
            throw new ArgumentException("Need at least 3 vertices", nameof(vertices));

        var edge1 = vertices[1] - vertices[0];
        var edge2 = vertices[2] - vertices[0];
        var normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));
        float d = -Vector3.Dot(normal, vertices[0]);

        var sum = Vector3.Zero;
        foreach (var v in vertices) sum += v;
        var centroid = sum / vertices.Length;

        float maxR = 0f;
        foreach (var v in vertices)
        {
            float r = Vector3.Distance(centroid, v);
            if (r > maxR) maxR = r;
        }

        return new PortalPlane(normal, d, targetCellId, ownerCellId, flags, centroid, maxR);
    }

    public static PortalPlane FromVertices(
        Vector3 v0, Vector3 v1, Vector3 v2,
        uint targetCellId, uint ownerCellId, ushort flags)
    {
        ReadOnlySpan<Vector3> verts = stackalloc Vector3[] { v0, v1, v2 };
        return FromVertices(verts, targetCellId, ownerCellId, flags);
    }

    public bool IsCrossing(Vector3 oldPos, Vector3 newPos)
    {
        float dx = MathF.Min(MathF.Abs(oldPos.X - Centroid.X), MathF.Abs(newPos.X - Centroid.X));
        float dy = MathF.Min(MathF.Abs(oldPos.Y - Centroid.Y), MathF.Abs(newPos.Y - Centroid.Y));
        float minDist2D = MathF.Sqrt(dx * dx + dy * dy);
        if (minDist2D > Radius) return false;

        float oldDist = Vector3.Dot(Normal, oldPos) + D;
        float newDist = Vector3.Dot(Normal, newPos) + D;
        return oldDist * newDist < 0f;
    }
}
