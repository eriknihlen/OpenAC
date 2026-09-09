using System;
using System.Numerics;

namespace AcDream.App.Rendering.Sky;

internal static class SkyProjection
{
    public static Matrix4x4 WithDepthRange(
        in Matrix4x4 activeProjection,
        float near,
        float far)
    {
        if (!float.IsFinite(near) || !float.IsFinite(far)
            || near <= 0f || far <= near)
        {
            throw new ArgumentOutOfRangeException(
                nameof(far),
                "Sky depth range must be finite with 0 < near < far.");
        }

        var result = activeProjection;

        if (activeProjection.M34 < 0f)
        {
            result.M33 = far / (near - far);
            result.M43 = near * far / (near - far);
        }
        else if (activeProjection.M34 > 0f)
        {
            result.M33 = far / (far - near);
            result.M43 = -near * far / (far - near);
        }
        else
        {
            throw new ArgumentException(
                "Sky projection must be perspective (M34 cannot be zero).",
                nameof(activeProjection));
        }

        return result;
    }
}
