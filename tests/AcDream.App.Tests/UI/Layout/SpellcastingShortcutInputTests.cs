using AcDream.App.UI.Layout;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Tests.UI.Layout;

public sealed class SpellcastingShortcutInputTests
{
    [Fact]
    public void EveryRetailFavoriteSpellSlotHasALiveConsumer()
    {
        InputAction[] slots = RetailActionIdentityTable.Map
            .Where(entry => entry.Key.InputMapId == 0x10000005u)
            .Select(entry => entry.Value)
            .Where(action => action.ToString().StartsWith(
                "UseSpellSlot_",
                StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(12, slots.Length);
        Assert.Equal(
            Enumerable.Range(0, 12),
            slots.Select(action =>
            {
                Assert.True(
                    SpellcastingUiController.TryMapSpellShortcut(
                        action,
                        out int index),
                    $"No favorite-spell consumer for {action}");
                return index;
            }).Order());
    }

    [Theory]
    [InlineData(InputAction.UseSpellSlot_1, 0)]
    [InlineData(InputAction.UseSpellSlot_9, 8)]
    [InlineData(InputAction.UseSpellSlot_10, 9)]
    [InlineData(InputAction.UseSpellSlot_11, 10)]
    [InlineData(InputAction.UseSpellSlot_12, 11)]
    public void AllRetailSpellSlotsMapToFavoriteIndex(
        InputAction action,
        int expectedIndex)
    {
        Assert.True(
            SpellcastingUiController.TryMapSpellShortcut(action, out int index));
        Assert.Equal(expectedIndex, index);
    }
}
