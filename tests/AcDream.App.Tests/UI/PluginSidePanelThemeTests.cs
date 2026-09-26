using System.Numerics;
using AcDream.App.UI;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Tests.UI;

public sealed class PluginSidePanelThemeTests
{
    [Theory]
    [InlineData(PluginUiTheme.Moss)]
    [InlineData(PluginUiTheme.Brass)]
    public void CompactShelfRemains18PixelsAndScrollsEveryEntryIntoView(PluginUiTheme theme)
    {
        var root = new UiRoot { Width = 800, Height = 260 };
        var settings = new PluginUiThemeSettings { Theme = theme };
        using var shelf = new PluginSidePanel(root.WindowManager, _ => (0u, 0, 0), null, settings);
        root.AddChild(shelf);
        for (int i = 0; i < 12; i++) Add(root, shelf, i);
        root.Tick(0.016, 16);
        Assert.Equal(18f, shelf.Width);
        Assert.True(shelf.Top + shelf.Height <= root.Height);
        var buttons = shelf.Children.OfType<PluginSidePanel.PluginShelfButton>().ToArray();
        Assert.True(buttons[0].Visible);
        Assert.False(buttons[^1].Visible);
        for (int i = 0; i < 20; i++) shelf.OnEvent(new UiEvent { Type = UiEventType.Scroll, Data0 = -1 });
        Assert.True(buttons[^1].Visible);
        Assert.False(buttons[0].Visible);
        Assert.All(buttons.Where(b => b.Visible), b =>
        {
            Assert.Equal(1f, b.Left);
            Assert.Equal(16f, b.Width);
            Assert.True(b.Top >= shelf.ExpandedGripBandHeight);
            Assert.True(b.Top + b.Height <= shelf.Height);
        });
        settings.Theme = PluginUiTheme.Classic;
        root.Tick(0.016, 32);
        Assert.True(shelf.Width > 36f);
        Assert.All(buttons, b =>
        {
            Assert.True(b.Visible);
            Assert.Equal(28f, b.Width);
            Assert.True(b.Outline);
            Assert.Equal(Vector4.One, b.TextColor);
        });
    }

    [Theory]
    [InlineData(PluginUiTheme.Classic)]
    [InlineData(PluginUiTheme.Moss)]
    [InlineData(PluginUiTheme.Brass)]
    public void RightClickOnEntryOpensAppearanceWithoutTogglingPlugin(PluginUiTheme theme)
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var settings = new PluginUiThemeSettings { Theme = theme };
        int requests = 0;
        using var shelf = new PluginSidePanel(root.WindowManager, _ => (0u, 0, 0), null, settings,
            () => requests++);
        var handle = Add(root, shelf, 0);
        root.AddChild(shelf);
        root.Tick(0.016, 16);
        var button = Assert.Single(shelf.Children.OfType<PluginSidePanel.PluginShelfButton>());
        int x = (int)(shelf.Left + button.Left + button.Width / 2);
        int y = (int)(shelf.Top + button.Top + button.Height / 2);
        bool visible = handle.IsVisible;
        root.OnMouseDown(UiMouseButton.Right, x, y, 0);
        root.OnMouseUp(UiMouseButton.Right, x, y, 0);
        Assert.Equal(1, requests);
        Assert.Equal(visible, handle.IsVisible);
    }

    private static RetailWindowHandle Add(UiRoot root, PluginSidePanel shelf, int index)
    {
        var frame = new UiPanel { Left = 100, Width = 200, Height = 100 };
        root.AddChild(frame);
        var handle = root.WindowManager.Register($"plugin:test:{index}", frame);
        shelf.Add(new PluginUiOwner($"test.{index}", $"Plugin {index}"),
            new PluginPanelDescriptor("main", $"Plugin {index}") { IconText = "TP" }, handle);
        return handle;
    }
}

