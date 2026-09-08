using System;
using System.Numerics;

namespace AcDream.Core.Physics.Motion;

public static class MoveToMath
{
    public const float Epsilon = 0.000199999995f;

    public static float HeadingDiff(float h1, float h2, uint turnCmd)
    {
        float d = h1 - h2;
        if (MathF.Abs(h1 - h2) < Epsilon)
        {
            d = 0f;
        }
        if (d < -Epsilon)
        {
            d += 360f;
        }
        if (Epsilon < d && turnCmd != MotionCommand.TurnRight)
        {
            d = 360f - d;
        }
        return d;
    }

    public static bool HeadingGreater(float a, float b, uint turnCmd)
    {
        bool greater = MathF.Abs(a - b) > 180f
            ? b > a
            : a > b;

        return turnCmd == MotionCommand.TurnRight ? greater : !greater;
    }

    public static float PositionHeading(Vector3 from, Vector3 to)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float headingDeg = 450f - MathF.Atan2(dy, dx) * (180f / MathF.PI);
        headingDeg %= 360f;
        if (headingDeg < 0f) headingDeg += 360f;
        return headingDeg;
    }

    public static float GetHeading(Quaternion orientation)
    {
        var forward = Vector3.Transform(new Vector3(0f, 1f, 0f), orientation);
        float yawDeg = MathF.Atan2(forward.Y, forward.X) * (180f / MathF.PI);
        float headingDeg = 90f - yawDeg;
        headingDeg %= 360f;
        if (headingDeg < 0f) headingDeg += 360f;
        return headingDeg;
    }

    public static Quaternion SetHeading(Quaternion baseOrientation, float headingDeg)
    {
        _ = baseOrientation;
        float yawDeg = 90f - headingDeg;
        float yaw = yawDeg * (MathF.PI / 180f);
        return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw - MathF.PI / 2f);
    }

    public static float HeadingFromYaw(float yawRad)
    {
        float headingDeg = 90f - yawRad * (180f / MathF.PI);
        headingDeg %= 360f;
        if (headingDeg < 0f) headingDeg += 360f;
        return headingDeg;
    }

    public static float YawFromHeading(float headingDeg)
    {
        float yaw = (90f - headingDeg) * (MathF.PI / 180f);
        while (yaw > MathF.PI) yaw -= 2f * MathF.PI;
        while (yaw < -MathF.PI) yaw += 2f * MathF.PI;
        return yaw;
    }

    public static float CylinderDistance(
        float ownRadius, float ownHeight, Vector3 ownPos,
        float targetRadius, float targetHeight, Vector3 targetPos)
    {
        float radialGap = Vector3.Distance(ownPos, targetPos)
            - (ownRadius + targetRadius);
        float verticalGap = ownPos.Z <= targetPos.Z
            ? targetPos.Z - (ownPos.Z + ownHeight)
            : ownPos.Z - (targetPos.Z + targetHeight);

        if (verticalGap > 0f && radialGap > 0f)
            return MathF.Sqrt(verticalGap * verticalGap + radialGap * radialGap);
        if (verticalGap < 0f && radialGap < 0f)
            return -MathF.Sqrt(verticalGap * verticalGap + radialGap * radialGap);
        return radialGap;
    }

    public static float CylinderDistanceNoZ(
        float ownRadius, Vector3 ownPos, float targetRadius, Vector3 targetPos)
    {
        float dx = targetPos.X - ownPos.X;
        float dy = targetPos.Y - ownPos.Y;
        float centerDist = MathF.Sqrt(dx * dx + dy * dy);
        return centerDist - ownRadius - targetRadius;
    }

    public static bool NormalizeCheckSmall(ref Vector3 v)
    {
        float length = v.Length();
        if (length < 1e-8f)
            return true;
        v /= length;
        return false;
    }

    public static Vector3 GlobalToLocalVec(Quaternion frameOrientation, Vector3 worldVec)
        => Vector3.Transform(worldVec, Quaternion.Conjugate(frameOrientation));

    public static Vector3 OriginToWorld(
        uint originCellId,
        float originX,
        float originY,
        float originZ,
        int liveCenterLandblockX,
        int liveCenterLandblockY)
    {
        int lbX = (int)((originCellId >> 24) & 0xFFu);
        int lbY = (int)((originCellId >> 16) & 0xFFu);
        return new Vector3(
            originX + (lbX - liveCenterLandblockX) * 192f,
            originY + (lbY - liveCenterLandblockY) * 192f,
            originZ);
    }
}
