using System.Numerics;

namespace AcDream.App.Rendering.Walk;

public static class WalkVisibilityMath
{
    public const float Epsilon = 0.000199999995f;

    public const float SkyHeight = 1000f;

    public const float InsideColumn = 0f;

    public const float OutsideColumn = 1001f;

    public static float GetPointLimit(float x, float y, in WalkPlane plane)
    {
        Vector3 n = plane.Normal;
        if (n.Z > Epsilon)
        {
            float h = -((x * n.X + y * n.Y + plane.D) / n.Z);
            if (h >= SkyHeight) return OutsideColumn;
            return h > 0f ? -h : InsideColumn;
        }
        if (n.Z < -Epsilon)
        {
            float h = -((x * n.X + y * n.Y + plane.D) / n.Z);
            if (h <= 0f) return OutsideColumn;
            return h >= SkyHeight ? InsideColumn : h;
        }
        float d = x * n.X + y * n.Y + plane.D;
        return d < -Epsilon ? OutsideColumn : InsideColumn;
    }

    public static void FillClipHeights(
        float x, float y, in WalkPlane cyPlane, ReadOnlySpan<WalkPlane> edgePlanes,
        Span<float> bounds)
    {
        bounds[0] = GetPointLimit(x, y, cyPlane);
        for (int i = 0; i < edgePlanes.Length; i++)
            bounds[i + 1] = GetPointLimit(x, y, edgePlanes[i]);
    }

    public static WalkBoundingType CornerPlaneCheck(float bound, float minZ, float maxZ)
    {
        if (bound == OutsideColumn) return WalkBoundingType.Outside;
        if (bound != InsideColumn)
        {
            if (bound <= 0f)
            {
                // inside is z >= h, h = -bound
                float h = -bound;
                if (h > minZ)
                {
                    if (maxZ <= h) return WalkBoundingType.Outside;
                    return WalkBoundingType.PartiallyInside;
                }
            }
            else if (bound < maxZ)
            {
                // inside is z <= h, h = bound; the block top pokes above h
                if (bound <= minZ) return WalkBoundingType.Outside;
                return WalkBoundingType.PartiallyInside;
            }
        }
        return WalkBoundingType.EntirelyInside;
    }

    public static WalkBoundingType BlockPlaneCheck(
        float b1, float b2, float b3, float b4, float minZ, float maxZ)
    {
        WalkBoundingType c1 = CornerPlaneCheck(b1, minZ, maxZ);
        WalkBoundingType c2 = CornerPlaneCheck(b2, minZ, maxZ);
        WalkBoundingType c3 = CornerPlaneCheck(b3, minZ, maxZ);
        WalkBoundingType c4 = CornerPlaneCheck(b4, minZ, maxZ);
        if (c1 == WalkBoundingType.Outside)
        {
            if (c2 == WalkBoundingType.Outside
                && c3 == WalkBoundingType.Outside
                && c4 == WalkBoundingType.Outside)
            {
                return WalkBoundingType.Outside;
            }
        }
        else if (c1 == WalkBoundingType.EntirelyInside
                 && c2 == WalkBoundingType.EntirelyInside
                 && c3 == WalkBoundingType.EntirelyInside
                 && c4 == WalkBoundingType.EntirelyInside)
        {
            return WalkBoundingType.EntirelyInside;
        }
        return WalkBoundingType.PartiallyInside;
    }

    public static WalkBoundingType BlockCheck(
        ReadOnlySpan<float> corner00, ReadOnlySpan<float> corner01,
        ReadOnlySpan<float> corner10, ReadOnlySpan<float> corner11,
        int planeCount, float maxZ, float minZ)
    {
        WalkBoundingType result = BlockPlaneCheck(
            corner00[0], corner01[0], corner10[0], corner11[0], minZ, maxZ);
        if (result == WalkBoundingType.Outside) return WalkBoundingType.Outside;
        for (int k = 1; k <= planeCount; k++)
        {
            WalkBoundingType r = BlockPlaneCheck(
                corner00[k], corner01[k], corner10[k], corner11[k], minZ, maxZ);
            if (r == WalkBoundingType.Outside) return WalkBoundingType.Outside;
            if (r == WalkBoundingType.PartiallyInside)
                result = WalkBoundingType.PartiallyInside;
        }
        return result;
    }

    public static WalkBoundingType ViewconeCheck(
        Vector3 center, float radius, in WalkPlane cyPlane,
        ReadOnlySpan<WalkPlane> edgePlanes)
    {
        float d = Vector3.Dot(cyPlane.Normal, center) + cyPlane.D;
        if (d < -radius) return WalkBoundingType.Outside;
        bool partial = d <= radius;
        foreach (ref readonly WalkPlane plane in edgePlanes)
        {
            d = Vector3.Dot(plane.Normal, center) + plane.D;
            if (d < -radius) return WalkBoundingType.Outside;
            if (d <= radius) partial = true;
        }
        return partial ? WalkBoundingType.PartiallyInside : WalkBoundingType.EntirelyInside;
    }

    public static bool IsRejectedByPortalPolygonBoundaryGuard(ReadOnlySpan<Vector3> localVertices)
    {
        bool everyVertexOnPlusX = true;
        bool everyVertexOnMinusX = true;
        bool everyVertexOnPlusY = true;
        bool everyVertexOnMinusY = true;

        for (int i = 0; i < localVertices.Length; i++)
        {
            float x = localVertices[i].X;
            float y = localVertices[i].Y;
            if (x != 12f) everyVertexOnPlusX = false;
            if (x != -12f) everyVertexOnMinusX = false;
            if (y != 12f) everyVertexOnPlusY = false;
            if (y != -12f) everyVertexOnMinusY = false;
        }

        return everyVertexOnPlusX || everyVertexOnMinusX
            || everyVertexOnPlusY || everyVertexOnMinusY;
    }
}

public readonly record struct WalkPlane(Vector3 Normal, float D);

public enum WalkBoundingType
{
    Outside = 0,
    PartiallyInside = 1,
    EntirelyInside = 2,
}
