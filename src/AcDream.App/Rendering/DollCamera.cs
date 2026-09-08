using System;
using System.Numerics;

namespace AcDream.App.Rendering;

public sealed class DollCamera : ICamera
{
    internal static readonly Vector3 RetailEye = new(0.12f, -2.4f, 0.88f);
    // Identity view orientation ⇒ look straight down +Y (no yaw/pitch). Target = Eye + (0,1,0).
    private static readonly Vector3 Target = new(0.12f, -1.4f, 0.88f);
    private static readonly Vector3 Up     = Vector3.UnitZ;

    public float FovRadians { get; set; } = MathF.PI / 4f;

    public float Near   { get; set; } = 0.1f;
    public float Far    { get; set; } = 50f;     // doll scene is small; 50 m is ample
    public float Aspect { get; set; } = 1f;

    /// <inheritdoc/>
    public Matrix4x4 View =>
        Matrix4x4.CreateLookAt(RetailEye, Target, Up);

    /// <inheritdoc/>
    public Matrix4x4 Projection =>
        Matrix4x4.CreatePerspectiveFieldOfView(FovRadians, Aspect <= 0f ? 1f : Aspect, Near, Far);
}

internal sealed class DollViewportCamera : IPrivateEntityViewportCamera
{
    private readonly DollCamera _camera = new();

    public Vector3 Eye => DollCamera.RetailEye;
    public float FovRadians
    {
        get => _camera.FovRadians;
        set => _camera.FovRadians = value;
    }
    public float Near
    {
        get => _camera.Near;
        set => _camera.Near = value;
    }
    public float Far
    {
        get => _camera.Far;
        set => _camera.Far = value;
    }
    public float Aspect
    {
        get => _camera.Aspect;
        set => _camera.Aspect = value;
    }
    public Matrix4x4 View => _camera.View;
    public Matrix4x4 Projection => _camera.Projection;
}
