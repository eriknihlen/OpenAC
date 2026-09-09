using AcDream.App.UI;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.UI;

public sealed partial class RetailWindowLayoutPersistenceTests
{
    [Fact]
    public void NewCharacterGetsDefaultsInsteadOfPreviousCharactersArrangement()
    {
        var root = new UiRoot { Width = 1200, Height = 800 };
        var chat = Mount(root, WindowNames.Chat, width: 400, height: 200);
        chat.OuterFrame.MaxWidth = 1000;
        var radar = Mount(root, WindowNames.Radar, width: 160, height: 160);
        var toolbar = Mount(root, WindowNames.Toolbar, width: 300, height: 90);
        var combat = Mount(root, WindowNames.Combat, width: 600, height: 110);
        var indicators = Mount(root, WindowNames.Indicators, width: 180, height: 30);
        var vitals = Mount(root, WindowNames.Vitals, width: 200, height: 50);
        var shelf = Mount(root, WindowNames.PluginShelf, width: 40, height: 200);
        string character = "Alice";
        var store = new SettingsStore(PathName);
        using var persistence = new RetailWindowLayoutPersistence(root.WindowManager, store,
            () => character, () => (1200, 800));
        persistence.ResetToDefaults();
        persistence.RestoreAll();
        radar.MoveTo(123, 234);
        chat.ResizeTo(650, 300);
        chat.MoveTo(500, 20);
        character = "Bob";
        persistence.RestoreAll();

        Assert.Equal((10f, 590f, 400f, 200f), (chat.Left, chat.Top, chat.Width, chat.Height));
        Assert.Equal((1030f, 10f), (radar.Left, radar.Top));
        Assert.Equal((890f, 700f), (toolbar.Left, toolbar.Top));
        Assert.Equal((590f, 580f), (combat.Left, combat.Top));
        Assert.Equal((10f, 10f), (indicators.Left, indicators.Top));
        Assert.Equal((200f, 10f), (vitals.Left, vitals.Top));
        Assert.Equal((10f, 300f), (shelf.Left, shelf.Top));

        character = "Alice";
        persistence.RestoreAll();
        Assert.Equal((123f, 234f), (radar.Left, radar.Top));
        Assert.Equal((500f, 20f, 650f, 300f), (chat.Left, chat.Top, chat.Width, chat.Height));
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 0f)]
    [InlineData(600f, 500f, 1400f, 1100f)]
    [InlineData(300f, 250f, 700f, 550f)]
    public void ViewportChangePreservesRelativePositionOnTheFirstFrame(
        float x, float y, float expectedX, float expectedY)
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var handle = Mount(root, WindowNames.Options, width: 200, height: 100);
        var screen = (Width: 800, Height: 600);
        using var persistence = new RetailWindowLayoutPersistence(root.WindowManager, null,
            () => "Alice", () => screen);
        handle.MoveTo(x, y);
        screen = (1600, 1200);
        root.Width = screen.Width;
        root.Height = screen.Height;
        persistence.ReflowToScreen();
        Assert.Equal((expectedX, expectedY, 200f, 100f),
            (handle.Left, handle.Top, handle.Width, handle.Height));
        screen = (800, 600);
        root.Width = screen.Width;
        root.Height = screen.Height;
        persistence.ReflowToScreen();
        Assert.Equal((x, y), (handle.Left, handle.Top));
    }

    [Fact]
    public void ShrinkAndGrowRestoresSizeAndPositionWithoutPersistingClamps()
    {
        var root = new UiRoot { Width = 1200, Height = 800 };
        var handle = Mount(root, WindowNames.Options, width: 700, height: 300);
        handle.OuterFrame.MaxWidth = 1000;
        handle.OuterFrame.ConstrainResizeToParent = true;
        var screen = (Width: 1200, Height: 800);
        var store = new SettingsStore(PathName);
        using var persistence = new RetailWindowLayoutPersistence(root.WindowManager, store,
            () => "Alice", () => screen);
        handle.MoveTo(450, 400);
        screen = (400, 250);
        root.Width = screen.Width;
        root.Height = screen.Height;
        persistence.ReflowToScreen();
        Assert.Equal((0f, 0f, 400f, 250f), (handle.Left, handle.Top, handle.Width, handle.Height));
        handle.Hide();
        persistence.SaveAll();
        screen = (1200, 800);
        root.Width = screen.Width;
        root.Height = screen.Height;
        persistence.ReflowToScreen();
        Assert.Equal((450f, 400f, 700f, 300f), (handle.Left, handle.Top, handle.Width, handle.Height));
        Assert.False(handle.IsVisible);
        persistence.RestoreAll();
        Assert.Equal((450f, 400f, 700f, 300f), (handle.Left, handle.Top, handle.Width, handle.Height));
    }

    [Fact]
    public void ReturningToAnOldResolutionKeepsTheCurrentArrangement()
    {
        var store = new SettingsStore(PathName);
        store.SaveWindowLayout("Alice", "1600x1200", WindowNames.Options,
            new UiWindowLayout(20, 30, 200, 100, false, false, false));
        var root = new UiRoot { Width = 800, Height = 600 };
        var handle = Mount(root, WindowNames.Options, width: 200, height: 100);
        var screen = (Width: 800, Height: 600);
        using var persistence = new RetailWindowLayoutPersistence(root.WindowManager, store,
            () => "Alice", () => screen);
        handle.MoveTo(300, 250);
        screen = (1600, 1200);
        root.Width = screen.Width;
        root.Height = screen.Height;
        persistence.ReflowToScreen();
        Assert.Equal((700f, 550f), (handle.Left, handle.Top));
        Assert.True(handle.IsVisible);
        persistence.RestoreAll();
        Assert.Equal((700f, 550f), (handle.Left, handle.Top));
        Assert.True(handle.IsVisible);
    }
}
