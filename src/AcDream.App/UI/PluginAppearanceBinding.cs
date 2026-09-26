namespace AcDream.App.UI;

public sealed class PluginAppearanceBinding(PluginUiThemeSettings settings)
{
    public IReadOnlyList<string> Themes { get; } = ["Classic", "Charcoal + moss", "Warm graphite + brass"];
    public string Selected => Themes[(int)settings.Theme];
    public Action<string> Select => name =>
    {
        for (int i = 0; i < Themes.Count; i++)
            if (Themes[i] == name) { settings.Theme = (PluginUiTheme)i; return; }
    };
    public Action? Close { get; set; }
}
