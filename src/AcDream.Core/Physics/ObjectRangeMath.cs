using System.Numerics;
using AcDream.Core.Physics.Motion;

namespace AcDream.Core.Physics;

public static class ObjectRangeMath
{
    public static bool ObjectsInRange(
        Vector3 firstPosition,
        float firstRadius,
        float firstHeight,
        Vector3 secondPosition,
        float secondRadius,
        float secondHeight,
        double range,
        bool useRadii,
        bool ignoreZDelta)
    {
        double distance;
        if (ignoreZDelta)
        {
            double dx = secondPosition.X - firstPosition.X;
            double dy = secondPosition.Y - firstPosition.Y;
            distance = Math.Sqrt(dx * dx + dy * dy);
        }
        else if (useRadii)
        {
            distance = MoveToMath.CylinderDistance(
                firstRadius,
                firstHeight,
                firstPosition,
                secondRadius,
                secondHeight,
                secondPosition);
        }
        else
        {
            distance = Vector3.Distance(firstPosition, secondPosition);
        }

        return distance <= range;
    }
}
