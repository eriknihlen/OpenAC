using AcDream.App.UI;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.UI;

public sealed partial class RetailWindowLayoutPersistenceTests
{
    [Fact]
    public void TemporaryPregameViewportDoesNotChangeTheCharacterArrangement()
    {
        var root = new UiRoot { Width = 1600, Height = 900 };
        var handle = Mount(root, WindowNames.Options, width: 500, height: 300);
        var screen = (Width: 1600, Height: 900);
        var store = new SettingsStore(PathName);
        using var persistence = new RetailWindowLayoutPersistence(root.WindowManager, store,
            () => "Alice", () => screen);
        handle.MoveTo(1000, 550);
        string before = File.ReadAllText(PathName);
        persistence.SetGameplayActive(false);
        screen = (800, 600);
        root.Width = screen.Width;
        root.Height = screen.Height;
        persistence.ReflowToScreen();
        Assert.Equal((1000f, 550f), (handle.Left, handle.Top));
        handle.MoveTo(1, 2);
        handle.ResizeTo(300, 200);
        handle.Hide();
        persistence.SaveAll();
        persistence.RestoreAll();
        persistence.SaveNamed("temporary");
        Assert.Equal(before, File.ReadAllText(PathName));

        screen = (1600, 900);
        root.Width = screen.Width;
        root.Height = screen.Height;
        persistence.SetGameplayActive(true);
        persistence.RestoreAll();
        Assert.Equal((1000f, 550f, 500f, 300f), (handle.Left, handle.Top, handle.Width, handle.Height));
        Assert.True(handle.IsVisible);
        handle.MoveTo(900, 500);
        Assert.NotEqual(before, File.ReadAllText(PathName));
    }
}
