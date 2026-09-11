using AcDream.App.UI;
using AcDream.App.UI.Layout;
using Xunit;

namespace AcDream.App.Tests.UI.Layout;

/// <summary>
/// Element-level pins for #31 and #33 that only use the bar's public event
/// surface, so they read the same on any build.
/// </summary>
public sealed class ScrollbarThumbBehaviorTests
{
    [Fact]
    public void VendorItemStrip_HorizontalThumbHasArt()
    {
        ImportedLayout layout = FixtureLoader.LoadVendor();
        UiScrollbar bar = Assert.IsType<UiScrollbar>(layout.FindElement(VendorUiController.ItemScrollbarId));

        Assert.NotEqual(0u, bar.ThumbSprite);
        Assert.NotEqual(0u, bar.ThumbTopSprite);
        Assert.NotEqual(0u, bar.ThumbBotSprite);
    }

    [Fact]
    public void FixedThumbBar_DragCoversTheWholeTrack()
    {
        ElementInfo info = ScrollbarFixtures.FixedThumbBar(proportional: false, minThumb: 37);
        UiScrollbar bar = Assert.IsType<UiScrollbar>(
            DatWidgetFactory.Create(info, _ => (1u, 37, 39), null, null, null));
        bar.Width = 37f;
        bar.Height = 307f;
        var model = new UiScrollable { ContentHeight = 400, ViewHeight = 300 };
        bar.Model = model;

        // Grab the 39 px thumb 3 px below its top edge (track starts at 17),
        // drag to the middle of its 234 px travel: the content is half way.
        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseDown, Data1: 10, Data2: 20)));
        Assert.True(bar.IsDragging);
        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseMove, Data1: 10, Data2: 137)));
        Assert.Equal(50, model.ScrollY);

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseMove, Data1: 10, Data2: 254)));
        Assert.Equal(model.MaxScroll, model.ScrollY);
        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseUp, Data1: 10, Data2: 254)));
    }
}

internal static class ScrollbarFixtures
{
    /// <summary>
    /// The character-creation skills list bar as authored: 37 x 307, 17 px
    /// arrows, a 37 x 39 single-sprite thumb, minimum thumb 37.
    /// </summary>
    public static ElementInfo FixedThumbBar(bool proportional, int minThumb)
    {
        var bar = new ElementInfo { Id = 0x100003F8u, Type = 11u, Width = 37f, Height = 307f };
        var direct = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        direct.Properties.Values[0x77u] = new UiPropertyValue { Kind = UiPropertyKind.Enum, UnsignedValue = 0x10000071u };
        direct.Properties.Values[0x78u] = new UiPropertyValue { Kind = UiPropertyKind.Enum, UnsignedValue = 0x10000072u };
        direct.Properties.Values[0x82u] = new UiPropertyValue { Kind = UiPropertyKind.Bool, BoolValue = proportional };
        direct.Properties.Values[0x89u] = new UiPropertyValue { Kind = UiPropertyKind.Integer, IntegerValue = minThumb };
        bar.States[UiStateInfo.DirectStateId] = direct;
        bar.Children.Add(Button(1u, y: 0f, height: 39f, sprite: 0x06001111u));
        bar.Children.Add(Button(0x10000071u, y: 290f, height: 17f, sprite: 0x06002222u));
        bar.Children.Add(Button(0x10000072u, y: 0f, height: 17f, sprite: 0x06003333u));
        return bar;
    }

    private static ElementInfo Button(uint id, float y, float height, uint sprite)
    {
        var button = new ElementInfo { Id = id, Type = 1u, X = 0f, Y = y, Width = 37f, Height = height };
        button.StateMedia["Normal"] = (sprite, 1);
        return button;
    }
}
