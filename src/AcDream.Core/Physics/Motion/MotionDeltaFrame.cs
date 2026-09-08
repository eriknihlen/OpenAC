using System.Numerics;

namespace AcDream.Core.Physics.Motion;

public sealed class MotionDeltaFrame
{
    public Vector3 Origin;

    public Quaternion Orientation = Quaternion.Identity;

    public void Reset()
    {
        Origin = Vector3.Zero;
        Orientation = Quaternion.Identity;
    }

    public void Combine(
        Vector3 localOrigin,
        Quaternion localOrientation,
        float originScale = 1f)
    {
        Origin += Vector3.Transform(localOrigin * originScale, Orientation);
        Orientation = FrameOps.SetRotate(
            Origin,
            Orientation,
            Orientation * localOrientation);
    }

    public void Combine(MotionDeltaFrame delta, float originScale = 1f)
    {
        ArgumentNullException.ThrowIfNull(delta);
        Combine(delta.Origin, delta.Orientation, originScale);
    }

    public float GetHeading() => MoveToMath.GetHeading(Orientation);

    public void SetHeading(float headingDeg) =>
        Orientation = MoveToMath.SetHeading(Orientation, headingDeg);
}
