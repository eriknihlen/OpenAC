using System.Numerics;

namespace AcDream.App.Rendering;

internal sealed class ChargenPreviewZoomController
{
    private const double InvalidDurationSentinel = -0.1;

    private readonly uint _heritageId;
    private readonly ChargenPreviewAnimator _animator;
    private Vector3 _startEye;
    private Vector3 _targetEye;
    private double _animStartTime;
    private double _animDuration;
    private bool _shouldAnimate;

    public ChargenPreviewZoomController(uint heritageId, ChargenPreviewCamera camera, ChargenPreviewAnimator animator)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(animator);
        _heritageId = heritageId;
        Camera = camera;
        _animator = animator;
    }

    public ChargenPreviewCamera Camera { get; }

    public bool IsZoomedIn => _animator.IsZoomedIn;

    public void ZoomIn()
    {
        if (IsZoomedIn)
            return;
        StartTween(ChargenPreviewCamera.ResolveDefaultEye(_heritageId));
        _animator.SetZoomedIn(true);
    }

    public void ZoomOut()
    {
        if (!IsZoomedIn)
            return;
        StartTween(ChargenPreviewCamera.ResolveZoomedOutEye(_heritageId));
        _animator.SetZoomedIn(false);
    }

    private void StartTween(Vector3 targetEye)
    {
        _startEye = Camera.Eye;
        _targetEye = targetEye;
        _shouldAnimate = true;
        _animDuration = InvalidDurationSentinel;
    }

    public void Tick(double now)
    {
        if (!_shouldAnimate)
            return;

        if (_animDuration <= 0d)
        {
            _animDuration = ChargenPreviewCamera.ZoomTweenDurationSeconds;
            _animStartTime = now;
        }

        double elapsed = now - _animStartTime;
        if (elapsed >= _animDuration)
        {
            _shouldAnimate = false;
            elapsed = _animDuration;
        }

        float t = (float)(elapsed / _animDuration);
        Camera.Eye = Vector3.Lerp(_startEye, _targetEye, t);
    }
}
