using System.Numerics;
using AcDream.Core.Physics.Motion;

namespace AcDream.App.Rendering;

internal enum ChargenRotateDirection
{
    Invalid = 0,
    Clockwise = 1,
    CounterClockwise = 2,
}

internal sealed class ChargenPreviewRotationController
{
    private const double InvalidTimeSentinel = -1.0;

    private double _lastRotateTime = InvalidTimeSentinel;
    private ChargenRotateDirection _direction = ChargenRotateDirection.Invalid;
    private bool _rotating;

    public const float RetailDefaultHeadingDegrees = 180f;

    public bool IsRotating => _rotating;
    public ChargenRotateDirection Direction => _direction;

    public ChargenPreviewRotationController(
        float initialHeadingDegrees = RetailDefaultHeadingDegrees)
    {
        HeadingDegrees = initialHeadingDegrees;
    }

    public float HeadingDegrees { get; private set; }

    public void Toggle(ChargenRotateDirection direction)
    {
        if (_rotating && direction == _direction)
        {
            _rotating = false;
            return;
        }
        _direction = direction;
        _lastRotateTime = InvalidTimeSentinel;
        _rotating = true;
    }

    public void Tick(double now)
    {
        if (!_rotating)
            return;
        if (_lastRotateTime <= 0d)
            _lastRotateTime = now;

        double deltaDegrees = ((now - _lastRotateTime) / ChargenPreviewCamera.RotationSecondsPerRevolution) * 360.0;
        HeadingDegrees = _direction == ChargenRotateDirection.Clockwise
            ? HeadingDegrees + (float)deltaDegrees
            : HeadingDegrees - (float)deltaDegrees;

        if (HeadingDegrees < 0f)
            HeadingDegrees += 360f;
        if (HeadingDegrees > 360f)
            HeadingDegrees -= 360f;

        _lastRotateTime = now;
    }

    public Quaternion ToOrientation() =>
        MoveToMath.SetHeading(Quaternion.Identity, HeadingDegrees);
}
