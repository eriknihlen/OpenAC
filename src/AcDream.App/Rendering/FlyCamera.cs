// src/AcDream.App/Rendering/FlyCamera.cs
using System.Numerics;

namespace AcDream.App.Rendering;

public sealed class FlyCamera : ICamera
{
    public Vector3 Position { get; set; } = new(96, 96, 150);
    public float Yaw { get; set; } = MathF.PI / 2f;  // facing +Y
    public float Pitch { get; set; } = -0.3f;         // looking slightly down
    public float FovY { get; set; } = RetailFieldOfView.DefaultAppliedFovY;
    public float Aspect { get; set; } = 16f / 9f;

    public float MoveSpeed { get; set; } = 12f;
    public float BoostSpeed { get; set; } = 50f;
    public float MouseSensitivity { get; set; } = 0.003f;

    private const float PitchLimit = 1.5533f;  // ~89 degrees

    public Matrix4x4 View
    {
        get
        {
            var forward = Forward();
            return Matrix4x4.CreateLookAt(Position, Position + forward, Vector3.UnitZ);
        }
    }

    public Matrix4x4 Projection
        => Matrix4x4.CreatePerspectiveFieldOfView(FovY, Aspect, 0.1f, 5000f);

    public void Update(double dt, bool w, bool a, bool s, bool d, bool up, bool down, bool boost = false)
    {
        float speed = boost ? BoostSpeed : MoveSpeed;
        float step = (float)(speed * dt);

        // Forward in the horizontal plane (ignore pitch so W doesn't dive into ground).
        var flatForward = new Vector3(MathF.Cos(Yaw), MathF.Sin(Yaw), 0f);
        var right = new Vector3(MathF.Sin(Yaw), -MathF.Cos(Yaw), 0f);

        if (w) Position += flatForward * step;
        if (s) Position -= flatForward * step;
        if (a) Position -= right * step;
        if (d) Position += right * step;
        if (up) Position += Vector3.UnitZ * step;
        if (down) Position -= Vector3.UnitZ * step;
    }

    /// <summary>
    /// Apply accumulated mouse deltas (pixels since last frame). Positive deltaX
    /// rotates the view to the right (decreases yaw), positive deltaY rotates
    /// down (decreases pitch).
    /// </summary>
    public void Look(float deltaX, float deltaY)
    {
        Yaw -= deltaX * MouseSensitivity;
        Pitch = Math.Clamp(Pitch - deltaY * MouseSensitivity, -PitchLimit, PitchLimit);
    }

    private Vector3 Forward()
    {
        float cp = MathF.Cos(Pitch);
        return new Vector3(
            cp * MathF.Cos(Yaw),
            cp * MathF.Sin(Yaw),
            MathF.Sin(Pitch));
    }
}
