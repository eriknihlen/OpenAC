namespace AcDream.UI.Abstractions.Panels.Settings;

public readonly record struct UiWindowLayout(
    float X,
    float Y,
    float Width,
    float Height,
    bool Visible,
    bool Collapsed,
    bool Maximized,
    int AuthoredGeometryRevision = 0);
