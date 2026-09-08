using System.Numerics;

namespace AcDream.App.Rendering;

public sealed class PortalTunnelCamera : ICamera
{
    public static readonly Vector3 RetailEye = new(0.24f, -2.7f, 0.88f);

    public float DirectionDegrees { get; set; }
    public float FovRadians { get; set; } = MathF.PI / 4f;
    public float Near { get; set; } = 0.1f;
    public float Far { get; set; } = 4000f;
    public float Aspect { get; set; } = 1f;

    public void UseSmartBoxFov(Matrix4x4 smartBoxProjection)
    {
        float verticalScale = smartBoxProjection.M22;
        if (!float.IsFinite(verticalScale) || verticalScale <= 0f)
            return;

        float fov = 2f * MathF.Atan(1f / verticalScale);
        if (float.IsFinite(fov) && fov > 0f && fov < MathF.PI)
            FovRadians = fov;

        float near = smartBoxProjection.M33 != 0f
            ? smartBoxProjection.M43 / smartBoxProjection.M33
            : float.NaN;
        float far = smartBoxProjection.M33 != -1f
            ? smartBoxProjection.M43 / (smartBoxProjection.M33 + 1f)
            : float.NaN;
        if (float.IsFinite(far) && far > 0f)
            Far = far;
        if (float.IsFinite(near) && near > 0f && near < Far)
            Near = near;
    }

    public Matrix4x4 View
    {
        get
        {
            float radians = DirectionDegrees * (MathF.PI / 180f);
            Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians);
            Vector3 forward = Vector3.UnitY;
            Vector3 up = Vector3.Transform(Vector3.UnitZ, rotation);
            return Matrix4x4.CreateLookAt(RetailEye, RetailEye + forward, up);
        }
    }

    public Matrix4x4 Projection => Matrix4x4.CreatePerspectiveFieldOfView(
        FovRadians,
        Aspect <= 0f ? 1f : Aspect,
        Near,
        Far);
}
