using AcDream.Core.Selection;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.UI.Layout;

public sealed class ToolbarInputController
{
    private readonly ToolbarController _toolbar;
    private readonly SelectionState _selection;

    public ToolbarInputController(ToolbarController toolbar, SelectionState selection)
    {
        _toolbar = toolbar ?? throw new ArgumentNullException(nameof(toolbar));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
    }

    /// <summary>Returns true when <paramref name="action"/> belongs to the toolbar.</summary>
    public bool Handle(InputAction action)
    {
        if (TryMapShortcut(action, out int slot, out bool use))
        {
            _toolbar.UseShortcut(slot, use);
            return true;
        }

        if (action == InputAction.CreateShortcut)
        {
            _toolbar.CreateShortcutToItem(_selection.SelectedObjectId ?? 0u);
            return true;
        }

        return false;
    }

    internal static bool TryMapShortcut(InputAction action, out int slot, out bool use)
    {
        int value = (int)action;
        if (value >= (int)InputAction.UseQuickSlot_1
            && value <= (int)InputAction.UseQuickSlot_9)
        {
            slot = value - (int)InputAction.UseQuickSlot_1;
            use = true;
            return true;
        }

        if (value >= (int)InputAction.SelectQuickSlot_1
            && value <= (int)InputAction.SelectQuickSlot_9)
        {
            slot = value - (int)InputAction.SelectQuickSlot_1;
            use = false;
            return true;
        }

        if (action is InputAction.UseQuickSlot_10
            or InputAction.UseQuickSlot_11
            or InputAction.UseQuickSlot_12
            or InputAction.UseQuickSlot_13)
        {
            slot = action switch
            {
                InputAction.UseQuickSlot_10 => 9,
                InputAction.UseQuickSlot_11 => 10,
                InputAction.UseQuickSlot_12 => 11,
                InputAction.UseQuickSlot_13 => 12,
                _ => -1,
            };
            use = true;
            return true;
        }

        if (value >= (int)InputAction.UseQuickSlot_14
            && value <= (int)InputAction.UseQuickSlot_18)
        {
            slot = 13 + value - (int)InputAction.UseQuickSlot_14;
            use = true;
            return true;
        }

        slot = -1;
        use = false;
        return false;
    }
}
