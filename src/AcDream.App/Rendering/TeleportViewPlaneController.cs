using System.Numerics;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

public sealed class TeleportViewPlaneController
{
    public const float TransitionViewPlaneDistance = 0.001f;

    private float _gameViewPlaneDistance = 1f;
    private TeleportAnimState _state = TeleportAnimState.Off;
    private readonly ProjectionOverrideCamera _projectionCamera = new();

    public bool Enabled { get; private set; }
    public float CurrentViewPlaneDistance { get; private set; } = 1f;

    public void Begin(Matrix4x4 gameProjection)
    {
        float distance = gameProjection.M22;
        if (!float.IsFinite(distance) || distance <= 0f)
            distance = 1f;

        _gameViewPlaneDistance = distance;
        CurrentViewPlaneDistance = distance;
        _state = TeleportAnimState.Off;
        Enabled = false;
    }

    public void Update(TeleportAnimSnapshot snapshot)
    {
        _state = snapshot.State;
        switch (snapshot.State)
        {
            case TeleportAnimState.WorldFadeOut:
            case TeleportAnimState.TunnelFadeIn:
            case TeleportAnimState.TunnelFadeOut:
            case TeleportAnimState.WorldFadeIn:
                Enabled = true;
                CurrentViewPlaneDistance = Lerp(
                    _gameViewPlaneDistance,
                    TransitionViewPlaneDistance,
                    snapshot.ViewPlaneBlend);
                break;

            case TeleportAnimState.Tunnel:
                if (Enabled)
                    CurrentViewPlaneDistance = _gameViewPlaneDistance;
                break;

            case TeleportAnimState.TunnelContinue:
            case TeleportAnimState.Off:
            default:
                Enabled = false;
                CurrentViewPlaneDistance = _gameViewPlaneDistance;
                break;
        }
    }

    public void Reset()
    {
        _state = TeleportAnimState.Off;
        Enabled = false;
        CurrentViewPlaneDistance = _gameViewPlaneDistance;
    }

    public Matrix4x4 Apply(Matrix4x4 baseProjection)
    {
        if (!Enabled)
            return baseProjection;

        float aspect = baseProjection.M11 != 0f
            ? baseProjection.M22 / baseProjection.M11
            : 1f;
        float far = baseProjection.M33 != -1f
            ? baseProjection.M43 / (baseProjection.M33 + 1f)
            : 5000f;

        if (!float.IsFinite(aspect) || aspect <= 0f)
            aspect = 1f;
        if (!float.IsFinite(far) || far <= 0.1f)
            far = 5000f;

        float distance = MathF.Max(CurrentViewPlaneDistance, TransitionViewPlaneDistance);
        float fov = 2f * MathF.Atan(1f / distance);
        float near = WorldTransitionNearPlane(distance);
        if (near >= far)
            near = MathF.Min(0.1f, far * 0.5f);

        return Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, near, far);
    }

    private float WorldTransitionNearPlane(float distance)
    {
        if (_state is TeleportAnimState.WorldFadeOut or TeleportAnimState.WorldFadeIn
            && distance < 0.4f)
        {
            return MathF.Max(0.0001f, distance * 0.25f);
        }

        return MathF.Max(0.1f, distance * 0.25f);
    }

    public ICamera ApplyTo(ICamera baseCamera)
    {
        ArgumentNullException.ThrowIfNull(baseCamera);
        _projectionCamera.Update(baseCamera, Apply(baseCamera.Projection));
        return _projectionCamera;
    }

    private static float Lerp(float from, float to, float amount) =>
        from + (to - from) * Math.Clamp(amount, 0f, 1f);

    private sealed class ProjectionOverrideCamera : ICamera
    {
        private ICamera _source = null!;

        public Matrix4x4 View => _source.View;
        public Matrix4x4 Projection { get; private set; } = Matrix4x4.Identity;
        public float Aspect
        {
            get => _source.Aspect;
            set => _source.Aspect = value;
        }

        public void Update(ICamera source, Matrix4x4 projection)
        {
            _source = source;
            Projection = projection;
        }
    }
}
