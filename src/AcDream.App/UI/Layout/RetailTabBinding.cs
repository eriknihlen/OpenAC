using System;

namespace AcDream.App.UI.Layout;

internal static class RetailTabBinding
{
    public static void SetClick(UiElement? element, Action? action)
    {
        if (element is null) return;

        element.ClickThrough = action is null;
        switch (element)
        {
            case UiButton button: button.OnClick = action; break;
            case UiText text: text.OnClick = action; break;
            case UiDatElement dat: dat.OnClick = action; break;
        }
    }

    public static bool SetOpen(UiElement? element, bool open)
    {
        if (element is IUiDatStateful stateful
            && stateful.TrySetRetailState(open ? RetailUiStateIds.Open : RetailUiStateIds.Closed))
            return true;

        if (element is UiButton button)
        {
            button.Selected = open;
            return true;
        }

        return false;
    }
}
