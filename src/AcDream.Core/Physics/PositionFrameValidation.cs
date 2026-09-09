using System.Numerics;

namespace AcDream.Core.Physics;

public static class PositionFrameValidation
{
    public static bool IsValid(uint cellId, Vector3 origin, Quaternion rotation)
    {
        if (!LandDefs.InboundValidCellId(cellId)
            || float.IsNaN(origin.X)
            || float.IsNaN(origin.Y)
            || float.IsNaN(origin.Z)
            || float.IsNaN(rotation.W)
            || float.IsNaN(rotation.X)
            || float.IsNaN(rotation.Y)
            || float.IsNaN(rotation.Z))
        {
            return false;
        }

        float normSquared = rotation.LengthSquared();
        return !float.IsNaN(normSquared)
            && MathF.Abs(normSquared - 1f) <= 0.001f;
    }
}
