using System;

namespace AcDream.App.Rendering;

public static class RetailFieldOfView
{
    public const float DefaultGameFovRadians = 1.57079637f;

    public const float AspectBias = 0.100000001f;

    public static readonly float DefaultAppliedFovY = ComputeDefault();

    private static float ComputeDefault()
    {
        bool ok = TryAppliedVerticalFov(DefaultGameFovRadians, 16f / 9f, out float fovY);
        System.Diagnostics.Debug.Assert(ok, "the default game FOV/aspect pair must satisfy the SetFOVRad gate");
        return fovY;
    }

    public static bool TryAppliedVerticalFov(float gameFovRadians, float aspect, out float fovY)
    {
        float divisor = aspect - AspectBias;
        if (divisor <= 0f)
        {
            fovY = float.NaN;
            return false;
        }

        fovY = gameFovRadians / divisor;
        return fovY > 0f && fovY < MathF.PI;
    }
}
