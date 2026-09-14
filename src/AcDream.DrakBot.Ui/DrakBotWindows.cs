using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Ui;

/// <summary>The graphical host's <see cref="DrakBotWindowsFactory"/>.</summary>
public static class DrakBotWindows
{
    public static Action Create(BotController controller, IAutomationSurface surface, IImmediateUiHost ui)
    {
        var dashboard = new BotDashboard(controller, surface);
        var markers = new NavMarkerOverlay(controller, surface, ui);
        return () =>
        {
            // The overlay first, so windows sit over the markers.
            if (surface.IsAvailable && surface.Character.IsInWorld)
                markers.Draw();
            dashboard.Draw();
        };
    }
}
