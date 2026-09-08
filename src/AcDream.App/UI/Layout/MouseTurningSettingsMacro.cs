using System.Collections.Generic;
using AcDream.Core.Chat;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.UI.Layout;

public static class MouseTurningSettingsMacro
{
    public readonly record struct Result(
        CameraTurningSettings Updated,
        bool UseMouseTurningChanged,
        IReadOnlyList<string> ChatLines);

    public static Result Compute(CameraTurningSettings current, bool useMouseTurningCurrent)
    {
        CameraTurningSettings target = CameraTurningSettings.MouseTurningTarget;
        var lines = new List<string>();

        float stiffness = current.Stiffness;
        if (stiffness != target.Stiffness)
        {
            lines.Add(OptionsPanelText.CameraStiffnessChanged(stiffness, target.Stiffness));
            stiffness = target.Stiffness;
        }

        float adjustmentSpeed = current.AdjustmentSpeed;
        if (adjustmentSpeed != target.AdjustmentSpeed)
        {
            lines.Add(OptionsPanelText.CameraAdjustmentChanged(adjustmentSpeed, target.AdjustmentSpeed));
            adjustmentSpeed = target.AdjustmentSpeed;
        }

        float sensitivity = current.MouseLookSensitivity;
        if (sensitivity != target.MouseLookSensitivity)
        {
            lines.Add(OptionsPanelText.MouseSensitivityChanged(sensitivity, target.MouseLookSensitivity));
            sensitivity = target.MouseLookSensitivity;
        }

        bool alignToSlope = current.AlignToSlope;
        if (alignToSlope != target.AlignToSlope)
        {
            lines.Add(OptionsPanelText.AlignToSlopeChanged);
            alignToSlope = target.AlignToSlope;
        }

        bool invertY = current.InvertMouseLookYAxis;
        if (invertY != target.InvertMouseLookYAxis)
        {
            lines.Add(OptionsPanelText.InvertMouseLookAxesChanged);
            invertY = target.InvertMouseLookYAxis;
        }

        bool useMouseTurningChanged = useMouseTurningCurrent != true;
        if (useMouseTurningChanged)
            lines.Add(OptionsPanelText.TurnToFaceCameraChanged);

        return new Result(
            new CameraTurningSettings(stiffness, adjustmentSpeed, sensitivity, alignToSlope, invertY),
            useMouseTurningChanged,
            lines);
    }
}
