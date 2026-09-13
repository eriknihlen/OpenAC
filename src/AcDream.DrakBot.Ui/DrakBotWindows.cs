using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Ui;

/// <summary>The graphical host's <see cref="DrakBotWindowsFactory"/>.</summary>
public static class DrakBotWindows
{
    public static Action Create(BotController controller, IAutomationSurface surface) =>
        new BotDashboard(controller, surface).Draw;
}
