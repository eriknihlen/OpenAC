namespace AcDream.UI.Abstractions.Panels.Settings;

public readonly record struct UiWindowPlacement(
    UiWindowLayout Layout,
    int ScreenWidth,
    int ScreenHeight);
