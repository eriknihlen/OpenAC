using System.Numerics;

namespace AcDream.Core.Vfx;

public static class RetailParticleFacing
{
    public static bool Faces(uint degradeMode)
        => degradeMode >= 2u && degradeMode <= 5u;

    public static (Vector3 XDir, Vector3 YDir) OrientQuad(
        uint degradeMode,
        Quaternion orientation,
        Vector3 localAxisX,
        Vector3 localAxisY,
        Vector3 toViewerUnit,
        Vector3 fallbackRight,
        Vector3 fallbackUp)
    {
        if (degradeMode == 2u)
            return FaceViewerRollFree(toViewerUnit, fallbackRight, fallbackUp);

        Vector3 worldX = Vector3.Transform(localAxisX, orientation);
        Vector3 worldY = Vector3.Transform(localAxisY, orientation);
        if (degradeMode < 3u || degradeMode > 5u)
            return (worldX, worldY);

        Vector3 localAxis = degradeMode switch
        {
            3u => Vector3.UnitX,
            4u => Vector3.UnitY,
            _ => Vector3.UnitZ,
        };
        Vector3 axis = Vector3.Transform(localAxis, orientation);
        Vector3 normal = Vector3.Cross(worldX, worldY);
        if (normal.LengthSquared() < 1e-10f)
            return (worldX, worldY);
        normal = Vector3.Normalize(normal);

        Vector3 targetInPlane = toViewerUnit - axis * Vector3.Dot(toViewerUnit, axis);
        Vector3 normalInPlane = normal - axis * Vector3.Dot(normal, axis);
        if (targetInPlane.LengthSquared() < 1e-8f
            || normalInPlane.LengthSquared() < 1e-8f)
        {
            return (worldX, worldY);
        }

        targetInPlane = Vector3.Normalize(targetInPlane);
        normalInPlane = Vector3.Normalize(normalInPlane);
        float cos = Math.Clamp(Vector3.Dot(normalInPlane, targetInPlane), -1f, 1f);
        float sin = Vector3.Dot(Vector3.Cross(normalInPlane, targetInPlane), axis);
        float angle = MathF.Atan2(sin, cos);
        var spin = Quaternion.CreateFromAxisAngle(axis, angle);
        return (Vector3.Transform(worldX, spin), Vector3.Transform(worldY, spin));
    }

    private static (Vector3 XDir, Vector3 YDir) FaceViewerRollFree(
        Vector3 toViewerUnit,
        Vector3 fallbackRight,
        Vector3 fallbackUp)
    {
        Vector3 right = Vector3.Cross(toViewerUnit, Vector3.UnitZ);
        if (right.LengthSquared() < 1e-8f)
        {
            return (fallbackRight, fallbackUp);
        }

        right = Vector3.Normalize(right);
        Vector3 up = Vector3.Cross(right, toViewerUnit);
        return (right, up);
    }
}
