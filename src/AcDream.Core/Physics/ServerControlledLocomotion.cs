using System;
using System.Numerics;

namespace AcDream.Core.Physics;

public static class ServerControlledLocomotion
{
    public const float StopSpeed = 0.20f;
    public const float RunThreshold = 1.25f;
    public const float MinSpeedMod = 0.25f;
    public const float MaxSpeedMod = 3.00f;


    public static LocomotionCycle PlanFromVelocity(Vector3 worldVelocity)
    {
        float horizontalSpeed = MathF.Sqrt(
            worldVelocity.X * worldVelocity.X +
            worldVelocity.Y * worldVelocity.Y);

        if (horizontalSpeed < StopSpeed)
            return new LocomotionCycle(MotionCommand.Ready, 1f, false);

        if (horizontalSpeed < RunThreshold)
        {
            float speedMod = Math.Clamp(
                horizontalSpeed / MotionInterpreter.WalkAnimSpeed,
                MinSpeedMod,
                MaxSpeedMod);
            return new LocomotionCycle(MotionCommand.WalkForward, speedMod, true);
        }

        return new LocomotionCycle(
            MotionCommand.RunForward,
            Math.Clamp(horizontalSpeed / MotionInterpreter.RunAnimSpeed, MinSpeedMod, MaxSpeedMod),
            true);
    }

    public static bool CanApplyVelocityCycle(uint currentMotion)
        => currentMotion == MotionCommand.Ready || IsLocomotion(currentMotion);

    public static bool IsLocomotion(uint motion)
    {
        uint low = motion & 0xFFu;
        return low is 0x05 or 0x06 or 0x07 or 0x0F or 0x10;
    }

    public readonly record struct LocomotionCycle(
        uint Motion,
        float SpeedMod,
        bool IsMoving);

    private static float SanitizePositive(float value)
    {
        return float.IsFinite(value) && value > 0f ? value : 1f;
    }
}
