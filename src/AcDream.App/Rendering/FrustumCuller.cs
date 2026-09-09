using System.Numerics;

namespace AcDream.App.Rendering;

/// <summary>
/// Six normalized view-frustum planes extracted from a View×Projection matrix.
/// Each plane is represented as (normal.X, normal.Y, normal.Z, distance) where
/// dot(normal, point) + distance >= 0 means the point is on the visible side.
/// </summary>
public readonly struct FrustumPlanes
{
    public readonly Vector4 Left;
    public readonly Vector4 Right;
    public readonly Vector4 Bottom;
    public readonly Vector4 Top;
    public readonly Vector4 Near;
    public readonly Vector4 Far;

    private FrustumPlanes(Vector4 left, Vector4 right, Vector4 bottom, Vector4 top, Vector4 near, Vector4 far)
    {
        Left   = left;
        Right  = right;
        Bottom = bottom;
        Top    = top;
        Near   = near;
        Far    = far;
    }

    public static FrustumPlanes FromViewProjection(Matrix4x4 vp)
    {
        var col1 = new Vector4(vp.M11, vp.M21, vp.M31, vp.M41);
        var col2 = new Vector4(vp.M12, vp.M22, vp.M32, vp.M42);
        var col3 = new Vector4(vp.M13, vp.M23, vp.M33, vp.M43);
        var col4 = new Vector4(vp.M14, vp.M24, vp.M34, vp.M44);

        var left   = Normalize(col4 + col1);
        var right  = Normalize(col4 - col1);
        var bottom = Normalize(col4 + col2);
        var top    = Normalize(col4 - col2);
        var near   = Normalize(col3);
        var far    = Normalize(col4 - col3);

        return new FrustumPlanes(left, right, bottom, top, near, far);
    }

    private static Vector4 Normalize(Vector4 plane)
    {
        float length = MathF.Sqrt(plane.X * plane.X + plane.Y * plane.Y + plane.Z * plane.Z);
        return plane / length;
    }
}

public static class FrustumCuller
{
    public static bool IsAabbVisible(FrustumPlanes planes, Vector3 min, Vector3 max)
    {
        return TestPlane(planes.Left,   min, max)
            && TestPlane(planes.Right,  min, max)
            && TestPlane(planes.Bottom, min, max)
            && TestPlane(planes.Top,    min, max)
            && TestPlane(planes.Near,   min, max)
            && TestPlane(planes.Far,    min, max);
    }

    private static bool TestPlane(Vector4 plane, Vector3 min, Vector3 max)
    {
        float px = plane.X >= 0 ? max.X : min.X;
        float py = plane.Y >= 0 ? max.Y : min.Y;
        float pz = plane.Z >= 0 ? max.Z : min.Z;

        // If the positive vertex is behind the plane, the box is
        // fully outside this half-space.
        return plane.X * px + plane.Y * py + plane.Z * pz + plane.W >= 0;
    }
}
