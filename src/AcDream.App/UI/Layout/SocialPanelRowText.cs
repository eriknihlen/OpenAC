namespace AcDream.App.UI.Layout;

internal static class SocialPanelRowText
{
    public static UiText? FindDeepest(UiElement row)
    {
        UiText? found = row as UiText;
        foreach (UiElement child in row.Children)
        {
            if (FindDeepest(child) is { } deeper)
                found = deeper;
        }
        return found;
    }
}
