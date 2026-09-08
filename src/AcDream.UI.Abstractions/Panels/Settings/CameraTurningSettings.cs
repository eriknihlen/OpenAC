namespace AcDream.UI.Abstractions.Panels.Settings;

public sealed record CameraTurningSettings(
    float Stiffness,
    float AdjustmentSpeed,
    float MouseLookSensitivity,
    bool AlignToSlope,
    bool InvertMouseLookYAxis,
    bool UseMouseTurning = false)
{
    public static CameraTurningSettings Default { get; } = new(
        Stiffness: 0.45f,
        AdjustmentSpeed: 40.0f,
        MouseLookSensitivity: 0.55f,
        AlignToSlope: true,
        InvertMouseLookYAxis: false);

    /// <summary>
    /// The mouse-turning macro's recommended targets
    /// (<c>SetMouseTurningDefaults</c>, research doc §4.4): Stiffness 0.95,
    /// AdjustmentSpeed 50.0, MouseLookSensitivity 0.7, AlignToSlope OFF,
    /// InvertMouseLookYAxis ON.
    /// </summary>
    public static CameraTurningSettings MouseTurningTarget { get; } = new(
        Stiffness: 0.95f,
        AdjustmentSpeed: 50.0f,
        MouseLookSensitivity: 0.7f,
        AlignToSlope: false,
        InvertMouseLookYAxis: true);
}
