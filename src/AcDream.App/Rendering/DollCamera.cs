using System;
using System.Numerics;

namespace AcDream.App.Rendering;

public sealed class DollCamera : ICamera
{
    private static readonly Vector3 Up = Vector3.UnitZ;

    private Vector3 _eye = PaperdollHeritagePresentation.DefaultEye;

    /// <summary>
    /// Where the doll is viewed from. Identity view orientation ⇒ look straight
    /// down +Y (no yaw/pitch), so the target is always the eye plus one metre
    /// of +Y.
    /// </summary>
    public Vector3 Eye
    {
        get => _eye;
        set => _eye = value;
    }

    /// <summary>Frames the doll for the body this heritage wears.</summary>
    public void SetHeritage(uint heritageId) =>
        _eye = PaperdollHeritagePresentation.ResolveEye(heritageId);

    public float FovRadians { get; set; } = MathF.PI / 4f;

    public float Near   { get; set; } = 0.1f;
    public float Far    { get; set; } = 50f;     // doll scene is small; 50 m is ample
    public float Aspect { get; set; } = 1f;

    /// <inheritdoc/>
    public Matrix4x4 View =>
        Matrix4x4.CreateLookAt(_eye, _eye + Vector3.UnitY, Up);

    /// <inheritdoc/>
    public Matrix4x4 Projection =>
        Matrix4x4.CreatePerspectiveFieldOfView(FovRadians, Aspect <= 0f ? 1f : Aspect, Near, Far);
}

internal sealed class DollViewportCamera : IPrivateEntityViewportCamera
{
    private readonly DollCamera _camera = new();

    public void SetHeritage(uint heritageId) => _camera.SetHeritage(heritageId);

    public Vector3 Eye => _camera.Eye;
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
