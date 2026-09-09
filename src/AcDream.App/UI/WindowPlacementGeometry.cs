using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.UI;

internal static class WindowPlacementGeometry
{
    internal static UiWindowLayout Project(
        UiWindowPlacement placement, int width, int height, float windowWidth, float windowHeight)
    {
        UiWindowLayout layout = placement.Layout;
        return layout with
        {
            X = ProjectAxis(layout.X, placement.ScreenWidth, layout.Width, width, windowWidth),
            Y = ProjectAxis(layout.Y, placement.ScreenHeight, layout.Height, height, windowHeight),
        };
    }

    private static float ProjectAxis(float position, int sourceSize, float sourceExtent,
        int targetSize, float targetExtent)
    {
        float sourceTravel = MathF.Max(0f, sourceSize - sourceExtent);
        float targetTravel = MathF.Max(0f, targetSize - targetExtent);
        if (!float.IsFinite(position)) return 0f;
        if (sourceSize == targetSize) return Math.Clamp(position, 0f, targetTravel);
        return sourceTravel > 0f
            ? Math.Clamp(position / sourceTravel, 0f, 1f) * targetTravel
            : 0f;
    }

    internal static UiWindowLayout Default(string name, UiWindowLayout authored,
        int width, int height, IReadOnlyDictionary<string, UiWindowLayout> windows)
    {
        const float margin = 10f;
        float right = MathF.Max(0f, width - authored.Width - margin);
        float bottom = MathF.Max(0f, height - authored.Height - margin);
        float centerX = MathF.Max(0f, (width - authored.Width) * 0.5f);
        float centerY = MathF.Max(0f, (height - authored.Height) * 0.5f);
        float toolbarHeight = windows.TryGetValue(WindowNames.Toolbar, out var toolbar) ? toolbar.Height : 0f;
        float indicatorWidth = windows.TryGetValue(WindowNames.Indicators, out var indicators) ? indicators.Width : 0f;
        (float x, float y) = name switch
        {
            WindowNames.Chat => (margin, bottom),
            WindowNames.Radar => (right, margin),
            WindowNames.Toolbar => (right, bottom),
            WindowNames.Combat => (right, MathF.Max(0f, bottom - toolbarHeight - margin)),
            WindowNames.Indicators => (margin, margin),
            WindowNames.Vitals or WindowNames.SideVitals => (indicatorWidth + margin * 2f, margin),
            WindowNames.PluginShelf => (margin, centerY),
            _ => (centerX, centerY),
        };
        return authored with
        {
            X = Math.Clamp(x, 0f, MathF.Max(0f, width - authored.Width)),
            Y = Math.Clamp(y, 0f, MathF.Max(0f, height - authored.Height)),
        };
    }
}
