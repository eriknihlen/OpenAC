using System.Numerics;

namespace AcDream.Core.Selection;

public static class RetailWorldPicker
{
    private const double RetailRayEpsilon = 0.0002;

    public static RetailSelectionHit? Pick(
        Vector3 worldOrigin,
        Vector3 worldDirection,
        IEnumerable<RetailSelectionPart> visibleParts,
        uint skipServerGuid = 0u)
    {
        if (worldDirection.LengthSquared() < 1e-10f)
            return null;

        RetailSelectionHit? closestSphere = null;
        RetailSelectionHit? closestPolygon = null;

        foreach (var part in visibleParts)
        {
            if (part.ServerGuid == 0u || part.ServerGuid == skipServerGuid)
                continue;
            if (part.Mesh.SphereRadius <= 0f
                || !Matrix4x4.Invert(part.LocalToWorld, out var worldToLocal))
                continue;

            Vector3 localOrigin = Vector3.Transform(worldOrigin, worldToLocal);
            Vector3 localDirection = Vector3.TransformNormal(worldDirection, worldToLocal);

            if (!TryIntersectSphere(
                    localOrigin,
                    localDirection,
                    part.Mesh.SphereCenter,
                    part.Mesh.SphereRadius,
                    out double sphereT))
                continue;

            if (closestPolygon is { } polygonWinner && sphereT > polygonWinner.Distance)
                continue;

            if (closestSphere is null || sphereT < closestSphere.Value.Distance)
                closestSphere = new RetailSelectionHit(
                    part.ServerGuid, part.LocalEntityId, part.PartIndex, sphereT, PolygonHit: false);

            foreach (var polygon in part.Mesh.Polygons)
            {
                if (!TryIntersectPolygon(localOrigin, localDirection, polygon, out double polygonT))
                    continue;

                if (closestPolygon is null || polygonT < closestPolygon.Value.Distance)
                    closestPolygon = new RetailSelectionHit(
                        part.ServerGuid, part.LocalEntityId, part.PartIndex, polygonT, PolygonHit: true);
                break;
            }
        }

        return closestPolygon ?? closestSphere;
    }

    internal static bool TryIntersectSphere(
        Vector3 origin,
        Vector3 direction,
        Vector3 center,
        float radius,
        out double distance)
    {
        distance = 0d;
        Vector3 offset = origin - center;
        double c = Vector3.Dot(offset, offset) - (double)radius * radius;
        if (c <= 0d)
            return false;

        double a = Vector3.Dot(direction, direction);
        if (a < RetailRayEpsilon)
            return false;

        double b = -Vector3.Dot(offset, direction);
        double discriminant = b * b - c * a;
        if (discriminant < 0d)
            return false;

        double root = Math.Sqrt(discriminant);
        distance = b > root ? (b - root) / a : (b + root) / a;
        return true;
    }

    internal static bool TryIntersectPolygon(
        Vector3 origin,
        Vector3 direction,
        RetailSelectionPolygon polygon,
        out double distance)
    {
        distance = 0d;
        if (polygon.Vertices.Count < 3
            || !TryPlane(polygon.Vertices, out Vector3 normal, out float planeD))
            return false;

        double denominator = Vector3.Dot(direction, normal);
        if (polygon.SingleSided && denominator > 0d)
            return false;
        if (Math.Abs(denominator) < RetailRayEpsilon)
            return false;

        distance = -(Vector3.Dot(origin, normal) + planeD) / denominator;
        if (distance < 0d)
            return false;

        Vector3 point = origin + direction * (float)distance;
        return PointInPolygon(point, polygon.Vertices, normal);
    }

    private static bool TryPlane(
        IReadOnlyList<Vector3> vertices,
        out Vector3 normal,
        out float planeD)
    {
        Vector3 first = vertices[0];
        Vector3 normalSum = Vector3.Zero;
        for (int i = 1; i + 1 < vertices.Count; i++)
            normalSum += Vector3.Cross(vertices[i] - first, vertices[i + 1] - first);

        if (normalSum.LengthSquared() > 1e-12f)
        {
            normal = Vector3.Normalize(normalSum);
            double averageDot = 0d;
            foreach (Vector3 vertex in vertices)
                averageDot += Vector3.Dot(normal, vertex);
            planeD = (float)-(averageDot / vertices.Count);
            return true;
        }

        normal = default;
        planeD = 0f;
        return false;
    }

    private static bool PointInPolygon(
        Vector3 point,
        IReadOnlyList<Vector3> vertices,
        Vector3 normal)
    {
        Vector3 previous = vertices[^1];
        for (int i = 0; i < vertices.Count; i++)
        {
            Vector3 current = vertices[i];
            Vector3 inward = Vector3.Cross(normal, current - previous);
            if (Vector3.Dot(point - previous, inward) < 0f)
                return false;
            previous = current;
        }
        return true;
    }
}
