using System.Globalization;

namespace AcDream.Core.Chat;

public static class OptionsPanelText
{
    public static string CameraStiffnessChanged(float from, float to) => string.Format(
        CultureInfo.InvariantCulture,
        "Camera Stiffness was changed from {0} to the mouse turning default of {1}.",
        from.ToString("F6", CultureInfo.InvariantCulture),
        to.ToString("F6", CultureInfo.InvariantCulture));

    public static string CameraAdjustmentChanged(float from, float to) => string.Format(
        CultureInfo.InvariantCulture,
        "Camera Adjustment was changed from {0} to the mouse turning default of {1}.",
        from.ToString("F6", CultureInfo.InvariantCulture),
        to.ToString("F6", CultureInfo.InvariantCulture));

    public static string MouseSensitivityChanged(float from, float to) => string.Format(
        CultureInfo.InvariantCulture,
        "Mouse Sensitivity was changed from {0} to the mouse turning default of {1}.",
        from.ToString("F6", CultureInfo.InvariantCulture),
        to.ToString("F6", CultureInfo.InvariantCulture));

    public const string AlignToSlopeChanged =
        "Align To Slope was changed from TRUE to the mouse turning default of FALSE.";

    public const string InvertMouseLookAxesChanged =
        "Invert Mouselook Axes was changed from FALSE to the mouse turning default of TRUE.";

    public const string TurnToFaceCameraChanged =
        "Turn to Face Camera was changed from FALSE to the mouse turning default of TRUE.";

    public const string SupportUrl =
        "http://support.turbine.com/ics/support/ticketnewwizard.asp?style=classic";

    public static string UrgentAssistanceUnavailable => string.Format(
        CultureInfo.InvariantCulture,
        "An error occurred while trying to launch your web browser.\n"
        + "The web site to submit an urgent assistance request is listed below. "
        + "Please go there to complete your request.\n{0}",
        SupportUrl);

    public static string ReportAbuseUnavailable => string.Format(
        CultureInfo.InvariantCulture,
        "An error occurred while trying to launch your web browser.\n"
        + "The web site to submit an abuse report is listed below. "
        + "Please go there to complete your request.\n{0}",
        SupportUrl);
}
