using System;
using System.Numerics;
using AcDream.Core.CharGen;

namespace AcDream.App.Rendering;

public sealed class ChargenPreviewCamera : ICamera
{
    private static readonly Vector3 Up = Vector3.UnitZ;

    private Vector3 _eye;

    public ChargenPreviewCamera(uint heritageId = 0u)
    {
        _eye = ResolveDefaultEye(heritageId);
    }

    public Vector3 Eye
    {
        get => _eye;
        set => _eye = value;
    }

    public void SetHeritage(uint heritageId) => _eye = ResolveDefaultEye(heritageId);

    public static Vector3 ResolveDefaultEye(uint heritageId) => heritageId switch
    {
        (uint)ChargenHeritageGroup.Olthoi => new Vector3(0f, -1.85000002f, 1.85000002f),
        (uint)ChargenHeritageGroup.OlthoiAcid => new Vector3(0f, -3.04999995f, 2.75f),
        (uint)ChargenHeritageGroup.Tumerok => new Vector3(0f, -0.850000024f, 1.64999998f),
        _ => new Vector3(0f, -0.550000012f, 1.64999998f),
    };

    public static Vector3 ResolveZoomedOutEye(uint heritageId) => heritageId switch
    {
        (uint)ChargenHeritageGroup.Olthoi => new Vector3(0f, -3.79999995f, 1.14999998f),
        (uint)ChargenHeritageGroup.OlthoiAcid => new Vector3(0f, -5.69999981f, 1.64999998f),
        _ => new Vector3(0f, -2.5f, 0.95f),
    };

    public const float RotationSecondsPerRevolution = 3.0f;

    public const float ZoomTweenDurationSeconds = 0.6f;

    public float FovRadians { get; set; } = MathF.PI / 4f;
    public float Near { get; set; } = 0.1f;
    public float Far { get; set; } = 50f;
    public float Aspect { get; set; } = 1f;

    public Matrix4x4 View =>
        Matrix4x4.CreateLookAt(_eye, _eye + Vector3.UnitY, Up);

    public Matrix4x4 Projection =>
        Matrix4x4.CreatePerspectiveFieldOfView(FovRadians, Aspect <= 0f ? 1f : Aspect, Near, Far);
}

internal sealed class ChargenPreviewViewportCamera : IPrivateEntityViewportCamera
{
    private readonly ChargenPreviewCamera _camera;

    public ChargenPreviewViewportCamera(uint heritageId = 0u)
    {
        _camera = new ChargenPreviewCamera(heritageId);
    }

    public ChargenPreviewViewportCamera(ChargenPreviewCamera camera)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
    }

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
