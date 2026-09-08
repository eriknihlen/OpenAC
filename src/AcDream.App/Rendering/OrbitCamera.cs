using System.Numerics;

namespace AcDream.App.Rendering;

public sealed class OrbitCamera : ICamera
{
    public Vector3 Target { get; set; } = new(96, 96, 0);
    public float Distance { get; set; } = 300f;
    public float Yaw { get; set; } = MathF.PI / 4f;
    public float Pitch { get; set; } = MathF.PI / 6f;
    public float FovY { get; set; } = RetailFieldOfView.DefaultAppliedFovY;
    public float Aspect { get; set; } = 16f / 9f;

    public Matrix4x4 View
    {
        get
        {
            var eye = Target + new Vector3(
                Distance * MathF.Cos(Pitch) * MathF.Cos(Yaw),
                Distance * MathF.Cos(Pitch) * MathF.Sin(Yaw),
                Distance * MathF.Sin(Pitch));
            return Matrix4x4.CreateLookAt(eye, Target, Vector3.UnitZ);
        }
    }

    public Matrix4x4 Projection
        => Matrix4x4.CreatePerspectiveFieldOfView(FovY, Aspect, 0.1f, 5000f);
}
