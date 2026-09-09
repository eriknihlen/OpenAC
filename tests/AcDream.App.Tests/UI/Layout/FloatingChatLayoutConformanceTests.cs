using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public class FloatingChatLayoutConformanceTests
{
    private static ElementInfo? Find(ElementInfo n, uint id)
    {
        if (n.Id == id) return n;
        foreach (var c in n.Children)
        {
            var f = Find(c, id);
            if (f is not null) return f;
        }
        return null;
    }

    [Fact]
    public void FloatyFixture_ResolvesKnownElements()
    {
        var root = FixtureLoader.LoadFloatyChatInfos();
        Assert.NotNull(Find(root, 0x10000011u)); // transcript
        Assert.NotNull(Find(root, 0x10000016u)); // input
        Assert.NotNull(Find(root, 0x10000012u)); // scrollbar track
        Assert.NotNull(Find(root, 0x10000019u)); // send button
        Assert.NotNull(Find(root, 0x100004D9u)); // title bar
        Assert.NotNull(Find(root, 0x1000052Au));
        Assert.NotNull(Find(root, 0x10000529u)); // title-bar drag handle
        // No talk-focus menu, no max/min button, no 1-4 indicators — a floaty
        // window has none of these (research doc §2.2).
        Assert.Null(Find(root, 0x10000014u));
        Assert.Null(Find(root, 0x1000046Fu));
        Assert.Null(Find(root, 0x10000522u));
    }

    [Fact]
    public void FloatyFixture_ResolvedTypes_MatchRetailRegistry()
    {
        var root = FixtureLoader.LoadFloatyChatInfos();
        Assert.Equal(12u, Find(root, 0x10000016u)!.Type); // Text/style-prototype (input) + Editable 0x16
        Assert.True(Find(root, 0x10000016u)!.TryGetEffectiveBool(0x16u, out bool editable) && editable);
        Assert.Equal(12u, Find(root, 0x100004D9u)!.Type); // Text (title bar) — no Editable
        Assert.False(Find(root, 0x100004D9u)!.TryGetEffectiveBool(0x16u, out bool titleEditable) && titleEditable);
        Assert.Equal(1u, Find(root, 0x10000019u)!.Type); // Button (Send)
        Assert.Equal(1u, Find(root, 0x1000052Au)!.Type);
        Assert.Equal(11u, Find(root, 0x10000012u)!.Type); // Scrollbar
        Assert.Equal(2u, Find(root, 0x10000529u)!.Type); // Dragbar (title-bar move handle)
    }

    [Fact]
    public void MountedFloatyWindow_ResolvesToTheExpectedWidgetTypes()
    {
        var layout = FixtureLoader.LoadFloatyChat();
        Assert.IsType<UiField>(layout.FindElement(0x10000016u));   // Editable 0x16 -> UiField
        Assert.IsType<UiText>(layout.FindElement(0x100004D9u));    // no Editable -> UiText
        Assert.IsType<UiButton>(layout.FindElement(0x10000019u));  // Send
        Assert.IsType<UiButton>(layout.FindElement(0x1000052Au));
        Assert.IsType<UiScrollbar>(layout.FindElement(0x10000012u));
    }


    [Theory]
    [InlineData(0x100004FCu)]
    [InlineData(0x1000000Fu)]   // top edge
    [InlineData(0x100004FEu)]
    [InlineData(0x100004D2u)]   // left edge
    [InlineData(0x10000501u)]
    [InlineData(0x100004D4u)]   // bottom edge
    [InlineData(0x10000503u)]
    [InlineData(0x100004D3u)]   // right edge
    public void FloatyFixture_EveryBorderElement_IsALiveResizeGrip(uint elementId)
    {
        var root = FixtureLoader.LoadFloatyChatInfos();
        Assert.Equal(9u, Find(root, elementId)!.Type);
    }

    [Theory]
    [InlineData(0x100004FCu)]
    [InlineData(0x1000000Fu)]
    [InlineData(0x100004FEu)]
    [InlineData(0x100004D2u)]
    [InlineData(0x10000501u)]
    [InlineData(0x100004D4u)]
    [InlineData(0x10000503u)]
    [InlineData(0x100004D3u)]
    public void MountedFloatyWindow_EveryLiveGrip_ResolvesNonZeroSprite(uint elementId)
    {
        var layout = FixtureLoader.LoadFloatyChat();
        var grip = Assert.IsType<UiResizeGrip>(layout.FindElement(elementId));
        Assert.NotEqual(0u, grip.SpriteFile);
    }
}
