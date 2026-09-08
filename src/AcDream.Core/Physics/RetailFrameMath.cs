using System.Numerics;

namespace AcDream.Core.Physics;

public static class RetailFrameMath
{
    public static Quaternion SetVectorHeading(
        Quaternion currentOrientation,
        Vector3 direction)
    {
        float lengthSquared = direction.LengthSquared();
        if (lengthSquared < PhysicsGlobals.EpsilonSq || !float.IsFinite(lengthSquared))
            return currentOrientation;

        Vector3 forward = direction / MathF.Sqrt(lengthSquared);

        Vector3 right;
        if (forward.X == 0f && forward.Y == 0f)
        {
            right = Vector3.UnitX;
        }
        else
        {
            float scale = MathF.Max(MathF.Abs(forward.X), MathF.Abs(forward.Y));
            float x = forward.X / scale;
            float y = forward.Y / scale;
            float horizontalLength = MathF.Sqrt((x * x) + (y * y));
            right = new Vector3(y / horizontalLength, -x / horizontalLength, 0f);
        }

        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));

        // System.Numerics uses row-vector transforms. Each row below is the
        // world direction of one local basis axis: +X right, +Y heading,
        // +Z up.
        var rotation = new Matrix4x4(
            right.X,   right.Y,   right.Z,   0f,
            forward.X, forward.Y, forward.Z, 0f,
            up.X,      up.Y,      up.Z,      0f,
            0f,        0f,        0f,        1f);

        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation));
    }
}
