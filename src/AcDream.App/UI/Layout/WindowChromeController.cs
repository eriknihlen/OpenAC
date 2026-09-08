using System;
using AcDream.App.UI;

namespace AcDream.App.UI.Layout;

internal static class WindowChromeController
{
    public const uint MaxMinButtonId = 0x1000046Fu;
    public const uint InventoryCloseButtonId = 0x100001D2u;
    public const uint CharacterCloseButtonId = 0x1000022Au;

    public static int BindCloseButton(ImportedLayout layout, Action? onClose)
    {
        if (onClose is null)
            return 0;

        return BindCloseButton(layout.Root, onClose);
    }

    private static int BindCloseButton(UiElement node, Action onClose)
    {
        int count = TryBindCloseButton(node, onClose) ? 1 : 0;
        foreach (var child in node.Children)
            count += BindCloseButton(child, onClose);
        return count;
    }

    private static bool TryBindCloseButton(UiElement element, Action onClose)
    {
        switch (element)
        {
            case UiButton button when IsCloseButtonId(button.ElementId):
                button.OnClick = onClose;
                return true;

            case UiDatElement datElement when IsCloseButtonId(datElement.ElementId):
                datElement.ClickThrough = false;
                datElement.CapturesPointerDrag = true;
                datElement.OnClick = onClose;
                return true;

            default:
                return false;
        }
    }

    private static bool IsCloseButtonId(uint id) =>
        id is InventoryCloseButtonId or CharacterCloseButtonId;
}
