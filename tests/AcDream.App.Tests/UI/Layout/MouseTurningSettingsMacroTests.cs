using AcDream.App.UI.Layout;
using AcDream.Core.Chat;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.UI.Layout;

public sealed class MouseTurningSettingsMacroTests
{
    [Fact]
    public void AllSixValuesAtOrdinaryDefaults_ChangesEveryOne()
    {
        MouseTurningSettingsMacro.Result result = MouseTurningSettingsMacro.Compute(
            CameraTurningSettings.Default,
            useMouseTurningCurrent: false);

        CameraTurningSettings target = CameraTurningSettings.MouseTurningTarget;
        Assert.Equal(target, result.Updated);
        Assert.True(result.UseMouseTurningChanged);
        Assert.Equal(6, result.ChatLines.Count);
    }

    [Fact]
    public void AllSixValuesAlreadyAtTarget_ChangesNone()
    {
        MouseTurningSettingsMacro.Result result = MouseTurningSettingsMacro.Compute(
            CameraTurningSettings.MouseTurningTarget,
            useMouseTurningCurrent: true);

        Assert.Equal(CameraTurningSettings.MouseTurningTarget, result.Updated);
        Assert.False(result.UseMouseTurningChanged);
        Assert.Empty(result.ChatLines);
    }

    [Fact]
    public void OnlyStiffnessDiffers_ChangesOnlyStiffness_WithItsOwnChatLine()
    {
        CameraTurningSettings current = CameraTurningSettings.MouseTurningTarget with
        {
            Stiffness = 0.45f,
        };

        MouseTurningSettingsMacro.Result result =
            MouseTurningSettingsMacro.Compute(current, useMouseTurningCurrent: true);

        Assert.Equal(0.95f, result.Updated.Stiffness);
        Assert.Equal(CameraTurningSettings.MouseTurningTarget.AdjustmentSpeed, result.Updated.AdjustmentSpeed);
        Assert.False(result.UseMouseTurningChanged);
        Assert.Equal(
            [OptionsPanelText.CameraStiffnessChanged(0.45f, 0.95f)],
            result.ChatLines);
    }

    [Fact]
    public void OnlyAlignToSlopeDiffers_EmitsTheByteVerifiedLiteralLine()
    {
        CameraTurningSettings current = CameraTurningSettings.MouseTurningTarget with
        {
            AlignToSlope = true,
        };

        MouseTurningSettingsMacro.Result result =
            MouseTurningSettingsMacro.Compute(current, useMouseTurningCurrent: true);

        Assert.False(result.Updated.AlignToSlope);
        Assert.Equal([OptionsPanelText.AlignToSlopeChanged], result.ChatLines);
    }

    [Fact]
    public void OnlyUseMouseTurningDiffers_ReportsChangedWithoutTouchingCameraSettings()
    {
        MouseTurningSettingsMacro.Result result = MouseTurningSettingsMacro.Compute(
            CameraTurningSettings.MouseTurningTarget,
            useMouseTurningCurrent: false);

        Assert.Equal(CameraTurningSettings.MouseTurningTarget, result.Updated);
        Assert.True(result.UseMouseTurningChanged);
        Assert.Equal([OptionsPanelText.TurnToFaceCameraChanged], result.ChatLines);
    }

    [Fact]
    public void ChatLines_AreEmittedInRetailOrder()
    {
        MouseTurningSettingsMacro.Result result = MouseTurningSettingsMacro.Compute(
            CameraTurningSettings.Default,
            useMouseTurningCurrent: false);

        CameraTurningSettings d = CameraTurningSettings.Default;
        CameraTurningSettings t = CameraTurningSettings.MouseTurningTarget;
        Assert.Equal(
            new[]
            {
                OptionsPanelText.CameraStiffnessChanged(d.Stiffness, t.Stiffness),
                OptionsPanelText.CameraAdjustmentChanged(d.AdjustmentSpeed, t.AdjustmentSpeed),
                OptionsPanelText.MouseSensitivityChanged(d.MouseLookSensitivity, t.MouseLookSensitivity),
                OptionsPanelText.AlignToSlopeChanged,
                OptionsPanelText.InvertMouseLookAxesChanged,
                OptionsPanelText.TurnToFaceCameraChanged,
            },
            result.ChatLines);
    }
}
