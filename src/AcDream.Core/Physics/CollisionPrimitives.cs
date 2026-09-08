using System;
using System.Numerics;

namespace AcDream.Core.Physics;

public static class CollisionPrimitives
{

    public const float Epsilon = 1e-4f;

    public const float EpsilonSq = 1e-8f;


    public static bool SphereIntersectsRay(
        Vector3 sphereCenter, float sphereRadius,
        Vector3 rayOrigin, Vector3 rayDir,
        out double t)
    {
        t = 0.0;

        // delta = rayOrigin − sphereCenter
        float dx = rayOrigin.X - sphereCenter.X;
        float dy = rayOrigin.Y - sphereCenter.Y;
        float dz = rayOrigin.Z - sphereCenter.Z;

        // c = |delta|² − r²  (positive ⟹ origin is outside sphere)
        float c = dx * dx + dy * dy + dz * dz - sphereRadius * sphereRadius;
        if (c <= 0f)
            return false;

        // a = |rayDir|²
        float a = rayDir.X * rayDir.X + rayDir.Y * rayDir.Y + rayDir.Z * rayDir.Z;
        if (a < EpsilonSq)
            return false;   // degenerate ray

        // b = −dot(delta, rayDir)
        float b = -(dx * rayDir.X + dy * rayDir.Y + dz * rayDir.Z);

        // discriminant = b²  −  c·a
        float disc = b * b - c * a;
        if (disc < 0f)
            return false;   // no real intersection

        float sqrtDisc = MathF.Sqrt(disc);

        if (b < sqrtDisc)
            t = (b + sqrtDisc) / a;
        else
            t = (b - sqrtDisc) / a;

        return true;
    }


    public static bool RayPlaneIntersect(
        Plane plane,
        Vector3 rayOrigin, Vector3 rayDir,
        out double t)
    {
        t = 0.0;

        // denom = dot(rayDir, plane.Normal)
        float denom = Vector3.Dot(rayDir, plane.Normal);
        if (MathF.Abs(denom) < Epsilon)
            return false;   // ray is (nearly) parallel to plane

        float num = -(Vector3.Dot(rayOrigin, plane.Normal) + plane.D);
        float tVal = num / denom;

        t = tVal;
        return tVal >= 0f;
    }


    public static void CalcNormal(
        ReadOnlySpan<Vector3> vertices,
        out Vector3 normal,
        out float planeD)
    {
        normal = Vector3.Zero;
        planeD = 0f;

        int n = vertices.Length;
        if (n < 3)
            return;

        float accX = 0f, accY = 0f, accZ = 0f;
        var v0 = vertices[0];
        for (int i = 1; i < n - 1; i++)
        {
            var vi = vertices[i];
            var vi1 = vertices[i + 1];

            // edge a = vi − v0,  edge b = vi1 − v0
            float ax = vi.X - v0.X, ay = vi.Y - v0.Y, az = vi.Z - v0.Z;
            float bx = vi1.X - v0.X, by = vi1.Y - v0.Y, bz = vi1.Z - v0.Z;

            accX += ay * bz - az * by;
            accY += az * bx - ax * bz;
            accZ += ax * by - ay * bx;
        }

        float len = MathF.Sqrt(accX * accX + accY * accY + accZ * accZ);
        if (len < EpsilonSq)
            return;

        float invLen = 1f / len;
        normal = new Vector3(accX * invLen, accY * invLen, accZ * invLen);

        // planeD = −(average dot(normal, vertex))
        float dotSum = 0f;
        for (int i = 0; i < n; i++)
            dotSum += Vector3.Dot(normal, vertices[i]);

        planeD = -(dotSum / n);
    }


    public static bool SphereIntersectsPoly(
        Plane polyPlane,
        ReadOnlySpan<Vector3> vertices,
        Vector3 sphereCenter, float sphereRadius,
        out Vector3 contactPoint)
    {
        contactPoint = Vector3.Zero;

        float dist = Vector3.Dot(polyPlane.Normal, sphereCenter) + polyPlane.D;
        float rad = sphereRadius - Epsilon;

        if (MathF.Abs(dist) > rad)
            return false;

        contactPoint = sphereCenter - polyPlane.Normal * dist;

        float radSq = rad * rad - dist * dist;   // available slack² for edge tests

        int numVerts = vertices.Length;
        if (numVerts == 0)
            return true;

        bool inside = true;
        int prevIdx = numVerts - 1;

        for (int i = 0; i < numVerts; i++)
        {
            var v0 = vertices[prevIdx];
            var v1 = vertices[i];
            prevIdx = i;

            var edge = v1 - v0;
            var disp = contactPoint - v0;

            var edgePerp = new Vector3(
                edge.Z * polyPlane.Normal.Y - edge.Y * polyPlane.Normal.Z,
                edge.X * polyPlane.Normal.Z - edge.Z * polyPlane.Normal.X,
                edge.Y * polyPlane.Normal.X - edge.X * polyPlane.Normal.Y);

            float dp = Vector3.Dot(disp, edgePerp);

            if (dp < 0f)
            {
                float edgePerpLenSq = edgePerp.LengthSquared();
                if (edgePerpLenSq * radSq < dp * dp)
                    return false;   // too far outside to be reached by sphere radius

                float dispEdge = Vector3.Dot(disp, edge);
                float edgeLenSq = edge.LengthSquared();
                if (dispEdge >= 0f && dispEdge < edgeLenSq)
                    return true;

                inside = false;
            }

            if (disp.LengthSquared() <= radSq)
                return true;
        }

        return inside;
    }


    public static bool FindTimeOfCollision(
        Plane polyPlane,
        ReadOnlySpan<Vector3> vertices,
        Vector3 sphereOrigin, float sphereRadius,
        Vector3 rayDir,
        out float t)
    {
        t = 0f;

        // dot(rayDir, planeNormal) — denominator
        float denom = Vector3.Dot(rayDir, polyPlane.Normal);
        if (MathF.Abs(denom) < Epsilon)
            return false;

        int numVerts = vertices.Length;

        float num = Vector3.Dot(sphereOrigin, polyPlane.Normal) + polyPlane.D;
        t = num / denom;

        var contact = sphereOrigin - rayDir * t;

        float radSq = sphereRadius * sphereRadius;

        bool inside = true;
        int prevIdx = numVerts - 1;

        for (int i = 0; i < numVerts; i++)
        {
            var v0 = vertices[prevIdx];
            var v1 = vertices[i];
            prevIdx = i;

            var edge = v1 - v0;
            var dispFromV0 = contact - v0;

            var edgePerp = new Vector3(
                edge.Z * polyPlane.Normal.Y - edge.Y * polyPlane.Normal.Z,
                edge.X * polyPlane.Normal.Z - edge.Z * polyPlane.Normal.X,
                edge.Y * polyPlane.Normal.X - edge.X * polyPlane.Normal.Y);

            float dp = Vector3.Dot(dispFromV0, edgePerp);

            if (dp < 0f)
            {
                float edgePerpLenSq = edgePerp.LengthSquared();
                if (edgePerpLenSq * radSq < dp * dp)
                    return false;

                float dispEdge = Vector3.Dot(dispFromV0, edge);
                float edgeLenSq = edge.LengthSquared();
                if (dispEdge >= 0f && dispEdge < edgeLenSq)
                    return true;

                inside = false;
            }

            float dispLenSq = dispFromV0.LengthSquared();
            if (dispLenSq < radSq)
                return true;
        }

        return inside;
    }


    public static bool HitsWalkable(
        Plane polyPlane,
        ReadOnlySpan<Vector3> vertices,
        Vector3 sphereCenter, float sphereRadius,
        Vector3 movementDir)
    {
        if (Vector3.Dot(polyPlane.Normal, movementDir) < 0f)
            return false;

        return SphereIntersectsPoly(polyPlane, vertices, sphereCenter, sphereRadius, out _);
    }


    public static bool FindWalkableCollision(
        Plane polyPlane,
        ReadOnlySpan<Vector3> vertices,
        Vector3 sphereOrigin,
        Vector3 movementDir,
        out Vector3 edgeNormal)
    {
        edgeNormal = Vector3.Zero;

        float denom = Vector3.Dot(polyPlane.Normal, movementDir);
        if (MathF.Abs(denom) < Epsilon)
            return false;

        int numVerts = vertices.Length;

        // Ray-plane time
        float t = (Vector3.Dot(polyPlane.Normal, sphereOrigin) + polyPlane.D) / denom;

        var contact = sphereOrigin - movementDir * t;

        int prevIdx = numVerts - 1;

        for (int i = 0; i < numVerts; i++)
        {
            var v0 = vertices[prevIdx];
            var v1 = vertices[i];
            prevIdx = i;

            var edge = v1 - v0;
            var disp = contact - v0;

            var nx = polyPlane.Normal.X;
            var ny = polyPlane.Normal.Y;
            var nz = polyPlane.Normal.Z;

            var epX = edge.Z * ny - edge.Y * nz;
            var epY = edge.X * nz - edge.Z * nx;
            var epZ = edge.Y * nx - edge.X * ny;

            float dp = disp.X * epX + disp.Y * epY + disp.Z * epZ;

            if (dp < 0f)
            {
                var raw = new Vector3(epX, epY, epZ);
                float len = raw.Length();
                if (len < EpsilonSq)
                    return false;

                edgeNormal = raw / len;
                return true;
            }
        }

        return false;
    }


    public static float SlideSphere(
        Plane plane, float sphereRadius,
        Vector3 sphereCenter, Vector3 movementDir)
    {
        float dist = Vector3.Dot(sphereCenter, plane.Normal) + plane.D;

        if (MathF.Abs(dist) < sphereRadius)
            return float.MaxValue;  // already touching — no slide needed

        float denom = Vector3.Dot(movementDir, plane.Normal);
        if (MathF.Abs(denom) < Epsilon)
            return 0f;              // movement is parallel to plane

        float offset = dist <= 0f ? -sphereRadius : sphereRadius;

        return (offset - dist) / denom;
    }


    public static bool SweptSphereHitsSphere(
        Vector3 moverCenter, float moverRadius,
        Vector3 sweepDelta,
        Vector3 targetCenter, float targetRadius,
        out float t)
    {
        t = 0f;

        float radSum = moverRadius + targetRadius;

        float mx = sweepDelta.X, my = sweepDelta.Y, mz = sweepDelta.Z;
        float distSq = mx * mx + my * my + mz * mz;
        if (distSq < EpsilonSq)
            return false;   // degenerate sweep (stationary mover)

        float sx = targetCenter.X - moverCenter.X;
        float sy = targetCenter.Y - moverCenter.Y;
        float sz = targetCenter.Z - moverCenter.Z;

        float gap = sx * sx + sy * sy + sz * sz - radSum * radSum;
        if (gap < EpsilonSq)
            return false;   // already overlapping — use static test separately

        // similar = −dot(spherePos, movement)
        // Positive when the sphere is in FRONT of us (moving toward it).
        float similar = -(sx * mx + sy * my + sz * mz);

        // discriminant = similar² − gap · distSq
        float disc = similar * similar - gap * distSq;
        if (disc < 0f)
            return false;

        float cDist = MathF.Sqrt(disc);

        float root = (similar - cDist < 0f) ? -(cDist + similar) : -(similar - cDist);

        // Normalise to [0, 1] scale
        t = root / distSq;

        return t > 0f && t <= 1f;
    }


    public static bool LandOnSphere(
        Plane plane, float sphereRadius,
        ref Vector3 sphereCenter, ref Vector3 movementDir,
        ref float walkInterp)
    {
        float distToPlane = Vector3.Dot(sphereCenter, plane.Normal) + plane.D;

        float denom = Vector3.Dot(movementDir, plane.Normal);

        float tLand;
        if (denom > Epsilon)
        {
            // Moving away from surface (along positive normal direction)
            tLand = (-sphereRadius - distToPlane) / denom;
        }
        else if (denom >= -Epsilon)
        {
            // Parallel to plane
            return false;
        }
        else
        {
            // Moving toward surface (against positive normal direction)
            tLand = (distToPlane - sphereRadius) / denom;
        }

        float newInterp = (1f - tLand) * walkInterp;
        if (newInterp >= walkInterp || newInterp < -0.5f)
            return false;

        // Apply the landing
        sphereCenter -= movementDir * tLand;
        walkInterp = newInterp;
        return true;
    }
}
