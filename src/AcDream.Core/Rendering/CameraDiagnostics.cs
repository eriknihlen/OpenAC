using System;

namespace AcDream.Core.Rendering;

public static class CameraDiagnostics
{
    public static bool UseRetailChaseCamera { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_RETAIL_CHASE") != "0";

    public static bool AlignToSlope { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_CAMERA_ALIGN_SLOPE") != "0";

    public static bool CollideCamera { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_CAMERA_COLLIDE") != "0";

    public static float TranslationStiffness { get; set; } = 0.45f;

    public static float RotationStiffness { get; set; } = 0.45f;

    public static float MouseLowPassWindowSec { get; set; } = 0.25f;

    public static float CameraAdjustmentSpeed { get; set; } = 40.0f;
}
