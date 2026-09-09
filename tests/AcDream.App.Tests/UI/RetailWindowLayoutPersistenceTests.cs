using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.UI;

public sealed partial class RetailWindowLayoutPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "acdream-window-layout-tests-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(_directory, "settings.json");

    [Fact]
    public void CompletedInteractions_SaveCompleteCharacterResolutionLayout()
    {
        var store = new SettingsStore(PathName);
        var root = new UiRoot { Width = 800, Height = 600 };
        RetailWindowHandle handle = Mount(root, "chat");
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager, store, () => "Alice", () => (800, 600));

        handle.MoveTo(44f, 55f);
        handle.ResizeTo(320f, 180f);
        handle.Hide();

        UiWindowLayout saved = Assert.IsType<UiWindowLayout>(
            store.LoadWindowLayout("Alice", "800x600", "chat", default));
        Assert.Equal((44f, 55f, 320f, 180f), (saved.X, saved.Y, saved.Width, saved.Height));
        Assert.False(saved.Visible);
    }

    [Fact]
    public void MainPanelMove_PersistsCanonicalPositionForEveryPrimaryChild()
    {
        var store = new SettingsStore(PathName);
        var root = new UiRoot { Width = 800, Height = 600 };
        RetailWindowHandle inventory = Mount(root, WindowNames.Inventory);
        RetailWindowHandle character = Mount(root, WindowNames.Character);
        inventory.Hide();
        character.Hide();

        using var panels = new RetailPanelUiController(
            root.IsWindowVisible,
            root.ShowWindow,
            root.HideWindow);
        panels.RegisterMainPanel(
            RetailPanelCatalog.Inventory,
            WindowNames.Inventory,
            inventory);
        panels.RegisterMainPanel(
            RetailPanelCatalog.Character,
            WindowNames.Character,
            character);
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager,
            store,
            () => "Alice",
            () => (800, 600));

        panels.SetPanelVisibility(RetailPanelCatalog.Inventory, visible: true);
        inventory.MoveTo(244f, 133f);

        UiWindowLayout savedInventory = Assert.IsType<UiWindowLayout>(
            store.LoadWindowLayout(
                "Alice", "800x600", WindowNames.Inventory, default));
        UiWindowLayout savedCharacter = Assert.IsType<UiWindowLayout>(
            store.LoadWindowLayout(
                "Alice", "800x600", WindowNames.Character, default));
        Assert.Equal((244f, 133f), (savedInventory.X, savedInventory.Y));
        Assert.Equal((244f, 133f), (savedCharacter.X, savedCharacter.Y));
    }

    [Fact]
    public void Restore_ClampsBoundsAndReappliesPanelStateAndVisibility()
    {
        var store = new SettingsStore(PathName);
        store.SaveWindowLayout(
            "Alice",
            "640x480",
            "chat",
            new UiWindowLayout(900f, 700f, 500f, 300f, false, false, true));

        var root = new UiRoot { Width = 640, Height = 480 };
        var state = new FakeWindowState();
        RetailWindowHandle handle = Mount(root, "chat", state);
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager, store, () => "Alice", () => (640, 480));

        persistence.RestoreAll();

        Assert.Equal((500f, 300f), (handle.Width, handle.Height));
        Assert.Equal((140f, 180f), (handle.Left, handle.Top));
        Assert.False(handle.IsVisible);
        Assert.True(state.Restored.Maximized);
    }

    [Fact]
    public void Restore_OlderAuthoredGeometryRevision_ResetsOnlySavedExtent()
    {
        var store = new SettingsStore(PathName);
        store.SaveWindowLayout(
            "Alice",
            "800x600",
            WindowNames.Examination,
            new UiWindowLayout(
                44f,
                55f,
                310f,
                545f,
                true,
                false,
                false,
                AuthoredGeometryRevision: 0));

        var root = new UiRoot { Width = 800, Height = 600 };
        RetailWindowHandle handle = Mount(
            root,
            WindowNames.Examination,
            authoredGeometryRevision: 1,
            width: 310f,
            height: 400f);
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager,
            store,
            () => "Alice",
            () => (800, 600));

        persistence.RestoreAll();

        Assert.Equal((44f, 55f), (handle.Left, handle.Top));
        Assert.Equal((310f, 400f), (handle.Width, handle.Height));
        UiWindowLayout migrated = Assert.IsType<UiWindowLayout>(
            store.LoadWindowLayout(
                "Alice",
                "800x600",
                WindowNames.Examination,
                default));
        Assert.Equal(1, migrated.AuthoredGeometryRevision);
    }

    [Fact]
    public void RestoreNamed_OlderRevisionUsesMountedAuthoredExtent_NotCurrentSize()
    {
        var store = new SettingsStore(PathName);
        store.SaveNamedWindowLayout(
            "legacy",
            WindowNames.Examination,
            new UiWindowLayout(
                44f,
                55f,
                310f,
                545f,
                true,
                false,
                false,
                AuthoredGeometryRevision: 0));

        var root = new UiRoot { Width = 800, Height = 600 };
        RetailWindowHandle handle = Mount(
            root,
            WindowNames.Examination,
            authoredGeometryRevision: 1,
            width: 310f,
            height: 400f);
        handle.ResizeTo(500f, 300f);
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager,
            store,
            () => "Alice",
            () => (800, 600));

        persistence.RestoreNamed("legacy");

        Assert.Equal((44f, 55f), (handle.Left, handle.Top));
        Assert.Equal((310f, 400f), (handle.Width, handle.Height));
    }

    [Fact]
    public void DefaultPreLoginKey_NeverOverwritesCharacterLayouts()
    {
        var store = new SettingsStore(PathName);
        var root = new UiRoot { Width = 800, Height = 600 };
        RetailWindowHandle handle = Mount(root, "radar");
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager, store, () => "default", () => (800, 600));

        handle.MoveTo(123f, 234f);

        Assert.Null(store.LoadWindowLayout("default", "800x600", "radar", default));
    }

    [Fact]
    public void Restore_StateManagedWindow_PreservesAuthoritativeVisibility()
    {
        var store = new SettingsStore(PathName);
        store.SaveWindowLayout(
            "Alice",
            "800x600",
            "combat",
            new UiWindowLayout(44f, 55f, 200f, 100f, true, false, false));

        var root = new UiRoot { Width = 800, Height = 600 };
        RetailWindowHandle handle = Mount(root, "combat");
        handle.Hide();
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager,
            store,
            () => "Alice",
            () => (800, 600),
            stateManagedVisibilityWindows: ["combat"]);

        persistence.RestoreAll();

        Assert.Equal((44f, 55f), (handle.Left, handle.Top));
        Assert.False(handle.IsVisible);

        handle.Show();
        UiWindowLayout saved = Assert.IsType<UiWindowLayout>(
            store.LoadWindowLayout("Alice", "800x600", "combat", default));
        Assert.False(saved.Visible);
    }

    [Fact]
    public void NamedProfile_SaveAndRestore_AppliesEveryMountedWindow()
    {
        var store = new SettingsStore(PathName);
        var root = new UiRoot { Width = 800, Height = 600 };
        RetailWindowHandle chat = Mount(root, "chat");
        RetailWindowHandle radar = Mount(root, "radar");
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager, store, () => "Alice", () => (800, 600));

        chat.MoveTo(44f, 55f);
        radar.MoveTo(300f, 120f);
        radar.Hide();
        persistence.SaveNamed("hunting");

        chat.MoveTo(1f, 2f);
        radar.MoveTo(3f, 4f);
        radar.Show();
        persistence.RestoreNamed("hunting");

        Assert.Equal((44f, 55f), (chat.Left, chat.Top));
        Assert.Equal((300f, 120f), (radar.Left, radar.Top));
        Assert.False(radar.IsVisible);
    }


    [Fact]
    public void ClampAllToScreen_ReclampsStrandedWindows_TopLeftPriority()
    {
        var store = new SettingsStore(PathName);
        var root = new UiRoot { Width = 2560, Height = 1440 };
        RetailWindowHandle handle = Mount(
            root, WindowNames.Examination, width: 310f, height: 400f);
        handle.MoveTo(2200f, 1000f);
        (int Width, int Height) screen = (2560, 1440);
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager, store, () => "Alice", () => screen);

        screen = (1280, 720);          // the display shrank
        persistence.ClampAllToScreen();

        Assert.Equal(1280f - 310f, handle.Left);
        Assert.Equal(720f - 400f, handle.Top);
    }

    [Fact]
    public void ClampAllToScreen_OversizedWindow_PinsTopLeftToZero()
    {
        var store = new SettingsStore(PathName);
        var root = new UiRoot { Width = 800, Height = 600 };
        RetailWindowHandle handle = Mount(
            root, WindowNames.Examination, width: 310f, height: 400f);
        handle.MoveTo(400f, 300f);
        (int Width, int Height) screen = (800, 600);
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager, store, () => "Alice", () => screen);

        screen = (200, 200);           // smaller than the window itself
        persistence.ClampAllToScreen();

        Assert.Equal(0f, handle.Left);
        Assert.Equal(0f, handle.Top);
    }

    [Fact]
    public void ClampAllToScreen_InBoundsWindow_DoesNotMove()
    {
        var store = new SettingsStore(PathName);
        var root = new UiRoot { Width = 2560, Height = 1440 };
        RetailWindowHandle handle = Mount(
            root, WindowNames.Examination, width: 310f, height: 400f);
        handle.MoveTo(100f, 120f);
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager, store, () => "Alice", () => (1280, 720));

        persistence.ClampAllToScreen();

        Assert.Equal((100f, 120f), (handle.Left, handle.Top));
    }

    [Fact]
    public void ClampAllToScreen_DoesNotSaveTheClampedPositions()
    {
        var store = new SettingsStore(PathName);
        var root = new UiRoot { Width = 2560, Height = 1440 };
        RetailWindowHandle handle = Mount(
            root, WindowNames.Examination, width: 310f, height: 400f);
        handle.MoveTo(2200f, 1000f);   // pre-attach: no save subscription yet
        (int Width, int Height) screen = (2560, 1440);
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager, store, () => "Alice", () => screen);

        screen = (1280, 720);
        persistence.ClampAllToScreen();

        Assert.Equal(1280f - 310f, handle.Left);
        Assert.Null(store.LoadWindowLayout(       // …and did NOT write.
            "Alice", "1280x720", WindowNames.Examination, default));
    }

    [Fact]
    public void RestoreAfterDisplayChange_WritesNothingToTheStore()
    {
        var store = new SettingsStore(PathName);
        var root = new UiRoot { Width = 800, Height = 600 };
        _ = Mount(root, WindowNames.Examination, width: 310f, height: 400f);
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager, store, () => "Alice", () => (800, 600));

        persistence.RestoreAfterDisplayChange();

        Assert.Null(store.LoadWindowLayout(
            "Alice", "800x600", WindowNames.Examination, default));
    }

    [Fact]
    public void RestoreAfterDisplayChange_RestoresGeometryButPreservesLiveVisibility()
    {
        var store = new SettingsStore(PathName);
        store.SaveWindowLayout(
            "Alice",
            "1280x720",
            WindowNames.Options,
            new UiWindowLayout(
                X: 700f,
                Y: 200f,
                Width: 310f,
                Height: 400f,
                Visible: false,
                Collapsed: true,
                Maximized: true));

        var root = new UiRoot { Width = 1280, Height = 720 };
        var state = new FakeWindowState();
        var lifecycle = new FakePanelController();
        RetailWindowHandle handle = Mount(
            root,
            WindowNames.Options,
            state,
            controller: lifecycle,
            width: 300f,
            height: 300f);
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager,
            store,
            () => "Alice",
            () => (1280, 720));

        persistence.RestoreAfterDisplayChange();

        Assert.Equal((700f, 200f, 310f, 400f),
            (handle.Left, handle.Top, handle.Width, handle.Height));
        Assert.True(handle.IsVisible);
        Assert.Equal(0, lifecycle.HiddenCount);
        Assert.Equal(1, state.RestoreCount);
        Assert.True(state.Restored.Collapsed);
        Assert.True(state.Restored.Maximized);
    }


    [Fact]
    public void WindowRegisteredAfterConstruction_StillRoundTrips()
    {
        var store = new SettingsStore(PathName);
        var root = new UiRoot { Width = 800, Height = 600 };
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager, store, () => "Alice", () => (800, 600));

        RetailWindowHandle handle = Mount(root, "late-window");
        handle.MoveTo(77f, 88f);

        UiWindowLayout saved = Assert.IsType<UiWindowLayout>(
            store.LoadWindowLayout("Alice", "800x600", "late-window", default));
        Assert.Equal((77f, 88f), (saved.X, saved.Y));
    }


    [Fact]
    public void ResizableMarkupPluginPanel_ResizedSize_RoundTripsThroughPersistence()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"300\" h=\"200\" resizable=\"true\" minw=\"200\" minh=\"150\"></panel>";
        var panel = MarkupDocument.Build(xml, new object(), _ => (1u, 32, 32));
        var store = new SettingsStore(PathName);
        var root = new UiRoot { Width = 800, Height = 600 };
        root.AddChild(panel);
        RetailWindowHandle handle = root.RegisterWindow("plugin-resizable-panel", panel);
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager, store, () => "Alice", () => (800, 600));

        handle.MoveTo(60f, 70f);
        handle.ResizeTo(420f, 260f);

        UiWindowLayout saved = Assert.IsType<UiWindowLayout>(
            store.LoadWindowLayout("Alice", "800x600", "plugin-resizable-panel", default));
        Assert.Equal((60f, 70f, 420f, 260f), (saved.X, saved.Y, saved.Width, saved.Height));

        var freshPanel = MarkupDocument.Build(xml, new object(), _ => (1u, 32, 32));
        var freshRoot = new UiRoot { Width = 800, Height = 600 };
        freshRoot.AddChild(freshPanel);
        RetailWindowHandle freshHandle = freshRoot.RegisterWindow(
            "plugin-resizable-panel", freshPanel);
        using var freshPersistence = new RetailWindowLayoutPersistence(
            freshRoot.WindowManager, store, () => "Alice", () => (800, 600));

        freshPersistence.RestoreAll();

        Assert.Equal((420f, 260f), (freshPanel.Width, freshPanel.Height));
        Assert.Equal((60f, 70f), (freshPanel.Left, freshPanel.Top));
    }

    [Fact]
    public void ResizableMarkupPluginPanel_RestoreClampsBelowFloorToAuthoredMin()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"300\" h=\"200\" resizable=\"true\" minw=\"200\" minh=\"150\"></panel>";
        var store = new SettingsStore(PathName);
        store.SaveWindowLayout(
            "Alice",
            "800x600",
            "plugin-resizable-floor",
            new UiWindowLayout(60f, 70f, 80f, 60f, true, false, false));

        var panel = MarkupDocument.Build(xml, new object(), _ => (1u, 32, 32));
        var root = new UiRoot { Width = 800, Height = 600 };
        root.AddChild(panel);
        root.RegisterWindow("plugin-resizable-floor", panel);
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager, store, () => "Alice", () => (800, 600));

        persistence.RestoreAll();

        Assert.Equal((200f, 150f), (panel.Width, panel.Height));
    }


    [Fact]
    public void PluginMarkupPanel_AuthoredSizeChanged_ReplacesStoredSizeButKeepsPosition()
    {
        const string oldXml =
            "<panel x=\"0\" y=\"0\" w=\"856\" h=\"236\" resizable=\"true\" minw=\"400\" minh=\"150\"></panel>";
        const string newXml =
            "<panel x=\"0\" y=\"0\" w=\"984\" h=\"271\" resizable=\"true\" minw=\"400\" minh=\"150\"></panel>";
        var store = new SettingsStore(PathName);

        // "Old session": the plugin's previous authored size registers and
        // the user drags the window.
        var oldPanel = MarkupDocument.Build(oldXml, new object(), _ => (1u, 32, 32));
        var oldRoot = new UiRoot { Width = 1280, Height = 720 };
        oldRoot.AddChild(oldPanel);
        RetailWindowHandle oldHandle = oldRoot.RegisterWindow(
            "moss-tank",
            oldPanel,
            authoredGeometryRevision: RetailWindowManager.ComputeAuthoredGeometryRevision(
                oldPanel.Width, oldPanel.Height, oldPanel.MinWidth, oldPanel.MinHeight, oldPanel.Resizable));
        using (var oldPersistence = new RetailWindowLayoutPersistence(
            oldRoot.WindowManager, store, () => "Alice", () => (1280, 720)))
        {
            oldHandle.MoveTo(120f, 90f);
        }

        // "New session": the plugin ships its new authored 984x271 size.
        var newPanel = MarkupDocument.Build(newXml, new object(), _ => (1u, 32, 32));
        var newRoot = new UiRoot { Width = 1280, Height = 720 };
        newRoot.AddChild(newPanel);
        newRoot.RegisterWindow(
            "moss-tank",
            newPanel,
            authoredGeometryRevision: RetailWindowManager.ComputeAuthoredGeometryRevision(
                newPanel.Width, newPanel.Height, newPanel.MinWidth, newPanel.MinHeight, newPanel.Resizable));
        using var persistence = new RetailWindowLayoutPersistence(
            newRoot.WindowManager, store, () => "Alice", () => (1280, 720));

        persistence.RestoreAll();

        Assert.Equal((984f, 271f), (newPanel.Width, newPanel.Height));
        Assert.Equal((120f, 90f), (newPanel.Left, newPanel.Top));
    }

    [Fact]
    public void PluginMarkupPanel_AuthoredSizeUnchanged_KeepsUserResizedSize()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"856\" h=\"236\" resizable=\"true\" minw=\"400\" minh=\"150\"></panel>";
        var store = new SettingsStore(PathName);

        var panel = MarkupDocument.Build(xml, new object(), _ => (1u, 32, 32));
        var root = new UiRoot { Width = 1280, Height = 720 };
        root.AddChild(panel);
        RetailWindowHandle handle = root.RegisterWindow(
            "moss-tank",
            panel,
            authoredGeometryRevision: RetailWindowManager.ComputeAuthoredGeometryRevision(
                panel.Width, panel.Height, panel.MinWidth, panel.MinHeight, panel.Resizable));
        using (var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager, store, () => "Alice", () => (1280, 720)))
        {
            handle.MoveTo(50f, 50f);
            handle.ResizeTo(900f, 300f);
        }

        // Fresh session: the SAME authored geometry (same markup) registers
        // again — the derived revision is unchanged, so the user's own
        // resize must survive.
        var freshPanel = MarkupDocument.Build(xml, new object(), _ => (1u, 32, 32));
        var freshRoot = new UiRoot { Width = 1280, Height = 720 };
        freshRoot.AddChild(freshPanel);
        freshRoot.RegisterWindow(
            "moss-tank",
            freshPanel,
            authoredGeometryRevision: RetailWindowManager.ComputeAuthoredGeometryRevision(
                freshPanel.Width, freshPanel.Height, freshPanel.MinWidth, freshPanel.MinHeight, freshPanel.Resizable));
        using var freshPersistence = new RetailWindowLayoutPersistence(
            freshRoot.WindowManager, store, () => "Alice", () => (1280, 720));

        freshPersistence.RestoreAll();

        Assert.Equal((900f, 300f), (freshPanel.Width, freshPanel.Height));
    }

    [Fact]
    public void PluginMarkupPanel_AuthoredSizeChanged_ClampsPositionToNewScreenBounds()
    {
        const string oldXml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\" resizable=\"true\" minw=\"100\" minh=\"80\"></panel>";
        const string newXml =
            "<panel x=\"0\" y=\"0\" w=\"220\" h=\"110\" resizable=\"true\" minw=\"100\" minh=\"80\"></panel>";
        var store = new SettingsStore(PathName);

        var oldPanel = MarkupDocument.Build(oldXml, new object(), _ => (1u, 32, 32));
        var oldRoot = new UiRoot { Width = 800, Height = 600 };
        oldRoot.AddChild(oldPanel);
        RetailWindowHandle oldHandle = oldRoot.RegisterWindow(
            "moss-tank",
            oldPanel,
            authoredGeometryRevision: RetailWindowManager.ComputeAuthoredGeometryRevision(
                oldPanel.Width, oldPanel.Height, oldPanel.MinWidth, oldPanel.MinHeight, oldPanel.Resizable));
        using (var oldPersistence = new RetailWindowLayoutPersistence(
            oldRoot.WindowManager, store, () => "Alice", () => (800, 600)))
        {
            oldHandle.MoveTo(590f, 490f);   // fits the OLD 200x100 size exactly
        }

        var newPanel = MarkupDocument.Build(newXml, new object(), _ => (1u, 32, 32));
        var newRoot = new UiRoot { Width = 800, Height = 600 };
        newRoot.AddChild(newPanel);
        newRoot.RegisterWindow(
            "moss-tank",
            newPanel,
            authoredGeometryRevision: RetailWindowManager.ComputeAuthoredGeometryRevision(
                newPanel.Width, newPanel.Height, newPanel.MinWidth, newPanel.MinHeight, newPanel.Resizable));
        using var persistence = new RetailWindowLayoutPersistence(
            newRoot.WindowManager, store, () => "Alice", () => (800, 600));

        persistence.RestoreAll();

        Assert.Equal((220f, 110f), (newPanel.Width, newPanel.Height));
        Assert.Equal((580f, 490f), (newPanel.Left, newPanel.Top));
    }

    private static RetailWindowHandle Mount(
        UiRoot root,
        string name,
        IRetainedWindowStateController? state = null,
        int authoredGeometryRevision = 0,
        float width = 200f,
        float height = 100f,
        IRetainedPanelController? controller = null)
    {
        var frame = new UiPanel
        {
            Left = 10f,
            Top = 20f,
            Width = width,
            Height = height,
            MinWidth = 100f,
            MinHeight = 80f,
            MaxWidth = 600f,
            MaxHeight = 400f,
            Draggable = true,
            Resizable = true,
        };
        root.AddChild(frame);
        return root.RegisterWindow(
            name,
            frame,
            controller: controller,
            stateController: state,
            authoredGeometryRevision: authoredGeometryRevision);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeWindowState : IRetainedWindowStateController
    {
        public RetainedWindowState Restored { get; private set; }
        public int RestoreCount { get; private set; }
        public RetainedWindowState CaptureWindowState() => Restored;
        public void RestoreWindowState(RetainedWindowState state)
        {
            Restored = state;
            RestoreCount++;
        }
    }

    private sealed class FakePanelController : IRetainedPanelController
    {
        public int HiddenCount { get; private set; }

        public void OnShown() { }

        public void OnHidden() => HiddenCount++;

        public void Dispose() { }
    }
}
