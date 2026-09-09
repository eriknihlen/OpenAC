using AcDream.App.UI.Layout;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Tests.UI.Layout;

public sealed class ToolbarInputControllerTests
{
    [Fact]
    public void EveryRetailQuickslotRowHasALiveToolbarConsumer()
    {
        InputAction[] actions = RetailActionIdentityTable.Map
            .Where(entry => entry.Key.InputMapId == 0x1000000Cu)
            .OrderBy(entry => entry.Key.ActionId)
            .Select(entry => entry.Value)
            .ToArray();

        Assert.Equal(28, actions.Length);
        Assert.Equal(InputAction.CreateShortcut, actions[21]);
        foreach (InputAction action in actions)
        {
            if (action == InputAction.CreateShortcut)
                continue;

            Assert.True(
                ToolbarInputController.TryMapShortcut(
                    action,
                    out int slot,
                    out _),
                $"No toolbar consumer for {action}");
            Assert.InRange(slot, 0, 17);
        }
    }

    [Theory]
    [InlineData(InputAction.UseQuickSlot_1, 0, true)]
    [InlineData(InputAction.UseQuickSlot_9, 8, true)]
    [InlineData(InputAction.SelectQuickSlot_1, 0, false)]
    [InlineData(InputAction.SelectQuickSlot_9, 8, false)]
    [InlineData(InputAction.UseQuickSlot_10, 9, true)]
    [InlineData(InputAction.UseQuickSlot_11, 10, true)]
    [InlineData(InputAction.UseQuickSlot_12, 11, true)]
    [InlineData(InputAction.UseQuickSlot_13, 12, true)]
    [InlineData(InputAction.UseQuickSlot_14, 13, true)]
    [InlineData(InputAction.UseQuickSlot_18, 17, true)]
    public void ShortcutActions_mapRetailSlotAndIntent(InputAction action, int slot, bool use)
    {
        Assert.True(ToolbarInputController.TryMapShortcut(action, out int actualSlot, out bool actualUse));
        Assert.Equal(slot, actualSlot);
        Assert.Equal(use, actualUse);
    }

    [Theory]
    [InlineData(InputAction.CreateShortcut)]
    [InlineData(InputAction.CombatToggleCombat)]
    [InlineData(InputAction.ToggleChatEntry)]
    public void NonSlotActions_doNotMapAsShortcut(InputAction action)
    {
        Assert.False(ToolbarInputController.TryMapShortcut(action, out int slot, out bool use));
        Assert.Equal(-1, slot);
        Assert.False(use);
    }
}
