using AcDream.App.Input;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Tests.Input;

public sealed class RetailEmoteMotionTableTests
{
    [Fact]
    public void EveryRetailEmoteActionHasExactMotionConsumer()
    {
        InputAction[] emotes = RetailActionIdentityTable.Map
            .Where(entry => entry.Key.InputMapId == 0x10000006u)
            .OrderBy(entry => entry.Key.ActionId)
            .Select(entry => entry.Value)
            .ToArray();

        Assert.Equal(87, RetailEmoteMotionTable.Count);
        Assert.Equal(87, emotes.Length);
        foreach (InputAction emote in emotes)
            Assert.True(RetailEmoteMotionTable.TryGetMotion(emote, out _));
    }

    [Theory]
    [InlineData(InputAction.EmoteAfkState, 0x43000118u)]
    [InlineData(InputAction.Cheer, 0x1300004Cu)]
    [InlineData(InputAction.Cry, 0x1300007Fu)]
    [InlineData(InputAction.Laugh, 0x13000080u)]
    [InlineData(InputAction.PointState, 0x430000F0u)]
    [InlineData(InputAction.Wave, 0x13000087u)]
    [InlineData(InputAction.EmoteYmca, 0x1200009Bu)]
    public void RepresentativeActionsMatchNamedRetailGlobals(
        InputAction action,
        uint expectedMotion)
    {
        Assert.True(RetailEmoteMotionTable.TryGetMotion(action, out uint motion));
        Assert.Equal(expectedMotion, motion);
    }

    [Theory]
    [InlineData(InputAction.Ready)]
    [InlineData(InputAction.ToggleOptionsPanel)]
    [InlineData(InputAction.UseQuickSlot_1)]
    public void NonEmoteActionsAreRejected(InputAction action) =>
        Assert.False(RetailEmoteMotionTable.TryGetMotion(action, out _));
}
