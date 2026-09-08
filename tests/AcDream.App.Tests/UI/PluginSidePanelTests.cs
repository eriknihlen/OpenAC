using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.Plugin.Abstractions;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.UI;

public sealed class PluginSidePanelTests
{
    [Fact]
    public void ManyPluginsWrapIntoReachableColumnsWithinTheLiveScreenHeight()
    {
        var root = new UiRoot { Width = 800f, Height = 260f };
        using var shelf = new PluginSidePanel(
            root.WindowManager,
            _ => (0u, 0, 0),
            font: null);
        root.AddChild(shelf);

        for (int i = 0; i < 12; i++)
        {
            var frame = new UiPanel { Width = 200f, Height = 100f };
            root.AddChild(frame);
            RetailWindowHandle handle = root.WindowManager.Register(
                $"plugin:test:{i}",
                frame);
            shelf.Add(
                new PluginUiOwner($"test.{i}", $"Plugin {i}"),
                new PluginPanelDescriptor("main", $"Plugin {i}"),
                handle);
        }

        root.Tick(0.016d, 16L);

        Assert.Equal(12, shelf.EntryCount);
        Assert.True(shelf.Width > 36f);
        Assert.True(shelf.Top + shelf.Height <= root.Height);
        Assert.All(
            shelf.Children,
            child => Assert.True(child.Top + child.Height <= shelf.Height));
        Assert.Equal(root.Width - shelf.Width - 4f, shelf.Left);
    }

    [Fact]
    public void ShelfAndMinimizeButtonsHideAndRestoreWithoutUnregisteringWindow()
    {
        var root = new UiRoot { Width = 1280f, Height = 720f };
        var frame = new UiPanel
        {
            Width = 320f,
            Height = 180f,
            Visible = true,
        };
        root.AddChild(frame);
        RetailWindowHandle handle = root.WindowManager.Register(
            "plugin:acdream.test:main",
            frame);
        using var shelf = new PluginSidePanel(
            root.WindowManager,
            _ => (0u, 0, 0),
            font: null);
        root.AddChild(shelf);

        shelf.Add(
            new PluginUiOwner("acdream.test", "Test Plugin"),
            new PluginPanelDescriptor("main", "Test Plugin")
            {
                IconText = "TP",
            },
            handle);

        Assert.True(shelf.Visible);
        Assert.Equal(1, shelf.EntryCount);
        PluginSidePanel.PluginShelfButton shelfButton = Assert.Single(
            shelf.Children.OfType<PluginSidePanel.PluginShelfButton>());

        shelfButton.OnEvent(new UiEvent { Type = UiEventType.Click });
        Assert.False(handle.IsVisible);
        Assert.True(handle.IsRegistered);

        shelfButton.OnEvent(new UiEvent { Type = UiEventType.Click });
        Assert.True(handle.IsVisible);

        UiSimpleButton minimize = Assert.IsAssignableFrom<UiSimpleButton>(
            Assert.Single(frame.Children));
        minimize.OnEvent(new UiEvent { Type = UiEventType.Click });
        Assert.False(handle.IsVisible);
        Assert.True(handle.IsRegistered);

        root.WindowManager.Unregister(handle.Name);
        Assert.Equal(0, shelf.EntryCount);
        Assert.False(shelf.Visible);
    }

    [Fact]
    public void FullWidthPluginWindowStartsAndStaysReachableAtMinimumCanvas()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var frame = new UiPanel
        {
            Left = 28f,
            Top = 42f,
            Width = 800f,
            Height = 244f,
            Visible = true,
        };
        root.AddChild(frame);
        RetailWindowHandle handle = root.WindowManager.Register(
            "plugin:acdream.mosstank:main",
            frame);
        using var shelf = new PluginSidePanel(
            root.WindowManager,
            _ => (0u, 0, 0),
            font: null);
        root.AddChild(shelf);

        shelf.Add(
            new PluginUiOwner("acdream.mosstank", "MossTank"),
            new PluginPanelDescriptor("main", "MossTank"),
            handle);

        Assert.Equal(0f, handle.Left);
        Assert.Equal(42f, handle.Top);
        Assert.True(frame.ConstrainDragToParent);
        Assert.True(frame.ConstrainResizeToParent);

        frame.Left = 700f;
        frame.Top = 590f;
        root.Tick(0.016d, 16L);

        Assert.Equal(0f, handle.Left);
        Assert.Equal(356f, handle.Top);
    }


    [Fact]
    public void DefaultDock_NoSavedLayout_SitsAtRightEdgeTopBar()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        using var shelf = new PluginSidePanel(
            root.WindowManager, _ => (0u, 0, 0), font: null);
        root.AddChild(shelf);
        root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

        var frame = new UiPanel { Width = 200f, Height = 100f };
        root.AddChild(frame);
        RetailWindowHandle pluginHandle = root.WindowManager.Register(
            "plugin:acdream.test:main", frame);
        shelf.Add(
            new PluginUiOwner("acdream.test", "Test Plugin"),
            new PluginPanelDescriptor("main", "Test Plugin"),
            pluginHandle);

        root.Tick(0.016d, 16L);

        Assert.Equal(800f - shelf.Width - 4f, shelf.Left);
        Assert.Equal(116f, shelf.Top);
    }

    [Fact]
    public void TopRightCorner_StaysFixed_WhenReflowChangesWidthWhileStillDocked()
    {
        var root = new UiRoot { Width = 800f, Height = 260f };
        using var shelf = new PluginSidePanel(
            root.WindowManager, _ => (0u, 0, 0), font: null);
        root.AddChild(shelf);
        root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

        for (int i = 0; i < 6; i++)
        {
            var frame = new UiPanel { Width = 200f, Height = 100f };
            root.AddChild(frame);
            RetailWindowHandle handle = root.WindowManager.Register(
                $"plugin:test:{i}", frame);
            shelf.Add(
                new PluginUiOwner($"test.{i}", $"Plugin {i}"),
                new PluginPanelDescriptor("main", $"Plugin {i}"),
                handle);
        }

        root.Tick(0.016d, 16L);
        float rightEdge = shelf.Left + shelf.Width;
        float widthBefore = shelf.Width;

        root.Height = 150f;
        root.Tick(0.016d, 16L);

        Assert.NotEqual(widthBefore, shelf.Width);
        Assert.Equal(rightEdge, shelf.Left + shelf.Width, precision: 3);
    }

    [Fact]
    public void Drag_ViaGripBand_MovesShelf_AndTopLeftSurvivesTheNextReflow()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        using var shelf = new PluginSidePanel(
            root.WindowManager, _ => (0u, 0, 0), font: null);
        root.AddChild(shelf);
        root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

        var frame = new UiPanel { Width = 200f, Height = 100f };
        root.AddChild(frame);
        RetailWindowHandle pluginHandle = root.WindowManager.Register(
            "plugin:acdream.test:main", frame);
        shelf.Add(
            new PluginUiOwner("acdream.test", "Test Plugin"),
            new PluginPanelDescriptor("main", "Test Plugin"),
            pluginHandle);
        root.Tick(0.016d, 16L);   // establishes the initial right-edge dock

        int pressX = (int)shelf.Left + 10;
        int pressY = (int)shelf.Top + 5;
        root.OnMouseDown(UiMouseButton.Left, pressX, pressY);
        root.OnMouseMove(pressX + 100, pressY + 100);
        root.OnMouseUp(UiMouseButton.Left, pressX + 100, pressY + 100);

        Assert.Equal(764f, shelf.Left);
        Assert.Equal(216f, shelf.Top);

        float leftAfterDrag = shelf.Left;
        float widthBeforeCollapse = shelf.Width;

        shelf.RestoreWindowState(new RetainedWindowState(Collapsed: true));

        Assert.NotEqual(widthBeforeCollapse, shelf.Width);
        Assert.Equal(leftAfterDrag, shelf.Left);
    }

    [Fact]
    public void Drag_StartingOnShelfPadding_DoesNotMoveTheShelf()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        using var shelf = new PluginSidePanel(
            root.WindowManager, _ => (0u, 0, 0), font: null);
        root.AddChild(shelf);
        root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

        var frame = new UiPanel { Width = 200f, Height = 100f };
        root.AddChild(frame);
        RetailWindowHandle pluginHandle = root.WindowManager.Register(
            "plugin:acdream.test:main", frame);
        shelf.Add(
            new PluginUiOwner("acdream.test", "Test Plugin"),
            new PluginPanelDescriptor("main", "Test Plugin"),
            pluginHandle);
        root.Tick(0.016d, 16L);   // establishes the initial right-edge dock

        int pressX = (int)shelf.Left + 2;   // left of button.Left (4)
        int pressY = (int)shelf.Top + (int)shelf.ExpandedGripBandHeight + 8;
        float leftBefore = shelf.Left;
        float topBefore = shelf.Top;

        root.OnMouseDown(UiMouseButton.Left, pressX, pressY);
        root.OnMouseMove(pressX + 100, pressY + 100);
        root.OnMouseUp(UiMouseButton.Left, pressX + 100, pressY + 100);

        Assert.Equal(leftBefore, shelf.Left);
        Assert.Equal(topBefore, shelf.Top);
    }

    [Fact]
    public void Drag_Refused_WhileUiLocked()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        using var shelf = new PluginSidePanel(
            root.WindowManager, _ => (0u, 0, 0), font: null);
        root.AddChild(shelf);
        root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

        var frame = new UiPanel { Width = 200f, Height = 100f };
        root.AddChild(frame);
        RetailWindowHandle pluginHandle = root.WindowManager.Register(
            "plugin:acdream.test:main", frame);
        shelf.Add(
            new PluginUiOwner("acdream.test", "Test Plugin"),
            new PluginPanelDescriptor("main", "Test Plugin"),
            pluginHandle);
        root.Tick(0.016d, 16L);

        float leftBefore = shelf.Left;
        float topBefore = shelf.Top;
        root.UiLocked = true;

        int pressX = (int)shelf.Left + 10;
        int pressY = (int)shelf.Top + 5;
        root.OnMouseDown(UiMouseButton.Left, pressX, pressY);
        root.OnMouseMove(pressX + 100, pressY + 100);
        root.OnMouseUp(UiMouseButton.Left, pressX + 100, pressY + 100);

        Assert.Equal(leftBefore, shelf.Left);
        Assert.Equal(topBefore, shelf.Top);
    }

    [Fact]
    public void Collapse_HidesButtonsAndShrinksWidth_RoundTripsThroughWindowState()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        using var shelf = new PluginSidePanel(
            root.WindowManager, _ => (0u, 0, 0), font: null);
        root.AddChild(shelf);
        root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

        var frame = new UiPanel { Width = 200f, Height = 100f };
        root.AddChild(frame);
        RetailWindowHandle pluginHandle = root.WindowManager.Register(
            "plugin:acdream.test:main", frame);
        shelf.Add(
            new PluginUiOwner("acdream.test", "Test Plugin"),
            new PluginPanelDescriptor("main", "Test Plugin"),
            pluginHandle);
        root.Tick(0.016d, 16L);

        float expandedWidth = shelf.Width;
        float leftBeforeCollapse = shelf.Left;
        PluginSidePanel.PluginShelfButton button = Assert.Single(
            shelf.Children.OfType<PluginSidePanel.PluginShelfButton>());
        Assert.True(button.Visible);

        int toggleX = (int)shelf.Left + (int)shelf.Width - 8;
        int toggleY = (int)shelf.Top + 4;
        root.OnMouseDown(UiMouseButton.Left, toggleX, toggleY);
        root.OnMouseUp(UiMouseButton.Left, toggleX, toggleY);

        Assert.False(button.Visible);
        Assert.True(shelf.Width < expandedWidth);
        RetainedWindowState captured = shelf.CaptureWindowState();
        Assert.True(captured.Collapsed);
        // Right edge preserved (still docked — this test never dragged it).
        Assert.Equal(
            leftBeforeCollapse + expandedWidth,
            shelf.Left + shelf.Width,
            precision: 3);

        shelf.RestoreWindowState(new RetainedWindowState(Collapsed: false));

        Assert.True(button.Visible);
        Assert.Equal(expandedWidth, shelf.Width);
        Assert.False(shelf.CaptureWindowState().Collapsed);
    }

    [Fact]
    public void CollapseThenExpand_WhileStillDocked_ReturnsToTheIdenticalLeftAndTop()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        using var shelf = new PluginSidePanel(
            root.WindowManager, _ => (0u, 0, 0), font: null);
        root.AddChild(shelf);
        root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

        var frame = new UiPanel { Width = 200f, Height = 100f };
        root.AddChild(frame);
        RetailWindowHandle pluginHandle = root.WindowManager.Register(
            "plugin:acdream.test:main", frame);
        shelf.Add(
            new PluginUiOwner("acdream.test", "Test Plugin"),
            new PluginPanelDescriptor("main", "Test Plugin"),
            pluginHandle);
        root.Tick(0.016d, 16L);   // establishes the initial right-edge dock

        float leftBefore = shelf.Left;
        float topBefore = shelf.Top;

        shelf.RestoreWindowState(new RetainedWindowState(Collapsed: true));
        Assert.NotEqual(leftBefore, shelf.Left);
        Assert.Equal(topBefore, shelf.Top);

        shelf.RestoreWindowState(new RetainedWindowState(Collapsed: false));

        Assert.Equal(leftBefore, shelf.Left, precision: 3);
        Assert.Equal(topBefore, shelf.Top, precision: 3);
    }

    [Fact]
    public void ToggleVisibility_ShowHideShow_AndStaysHiddenWhenANewWindowRegisters()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        using var shelf = new PluginSidePanel(
            root.WindowManager, _ => (0u, 0, 0), font: null);
        root.AddChild(shelf);
        root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

        var frame1 = new UiPanel { Width = 200f, Height = 100f };
        root.AddChild(frame1);
        RetailWindowHandle handle1 = root.WindowManager.Register(
            "plugin:test:1", frame1);
        shelf.Add(
            new PluginUiOwner("test.1", "Plugin 1"),
            new PluginPanelDescriptor("main", "Plugin 1"),
            handle1);

        Assert.True(shelf.Visible);

        shelf.Hide();
        Assert.False(shelf.Visible);

        shelf.Show();
        Assert.True(shelf.Visible);

        shelf.Hide();
        Assert.False(shelf.Visible);

        var frame2 = new UiPanel { Width = 200f, Height = 100f };
        root.AddChild(frame2);
        RetailWindowHandle handle2 = root.WindowManager.Register(
            "plugin:test:2", frame2);
        shelf.Add(
            new PluginUiOwner("test.2", "Plugin 2"),
            new PluginPanelDescriptor("main", "Plugin 2"),
            handle2);

        Assert.Equal(2, shelf.EntryCount);
        Assert.False(shelf.Visible);   // a new window does not un-hide it
    }

    [Fact]
    public void Persistence_RoundTripsPositionVisibilityAndCollapsed()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "acdream-plugin-shelf-tests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var root = new UiRoot { Width = 800f, Height = 600f };
            using var shelf = new PluginSidePanel(
                root.WindowManager, _ => (0u, 0, 0), font: null);
            root.AddChild(shelf);
            root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

            var frame = new UiPanel { Width = 200f, Height = 100f };
            root.AddChild(frame);
            RetailWindowHandle pluginHandle = root.WindowManager.Register(
                "plugin:acdream.test:main", frame);
            shelf.Add(
                new PluginUiOwner("acdream.test", "Test Plugin"),
                new PluginPanelDescriptor("main", "Test Plugin"),
                pluginHandle);

            using var persistence = new RetailWindowLayoutPersistence(
                root.WindowManager, store, () => "Alice", () => (800, 600));

            root.WindowManager.MoveTo(WindowNames.PluginShelf, 120f, 88f);
            shelf.RestoreWindowState(new RetainedWindowState(Collapsed: true));
            shelf.Hide();

            UiWindowLayout saved = Assert.IsType<UiWindowLayout>(
                store.LoadWindowLayout("Alice", "800x600", WindowNames.PluginShelf, default));
            Assert.Equal((120f, 88f), (saved.X, saved.Y));
            Assert.False(saved.Visible);
            Assert.True(saved.Collapsed);

            // Restore onto a fresh shelf instance, mirroring a relog.
            var root2 = new UiRoot { Width = 800f, Height = 600f };
            using var shelf2 = new PluginSidePanel(
                root2.WindowManager, _ => (0u, 0, 0), font: null);
            root2.AddChild(shelf2);
            root2.WindowManager.Register(WindowNames.PluginShelf, shelf2, shelf2, controller: shelf2);
            var frame2 = new UiPanel { Width = 200f, Height = 100f };
            root2.AddChild(frame2);
            RetailWindowHandle pluginHandle2 = root2.WindowManager.Register(
                "plugin:acdream.test:main", frame2);
            shelf2.Add(
                new PluginUiOwner("acdream.test", "Test Plugin"),
                new PluginPanelDescriptor("main", "Test Plugin"),
                pluginHandle2);

            using var persistence2 = new RetailWindowLayoutPersistence(
                root2.WindowManager, store, () => "Alice", () => (800, 600));
            persistence2.RestoreAll();

            Assert.Equal((120f, 88f), (shelf2.Left, shelf2.Top));
            Assert.False(shelf2.Visible);
            Assert.True(shelf2.CaptureWindowState().Collapsed);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }


    [Fact]
    public void Drag_StartingOnAnEntryButton_DoesNotMoveTheShelf()
    {
        // Finding 1 / finding 7a: a press that lands on a real entry button
        // (UiSimpleButton.HandlesClick => true) must be handled by the button,
        // never promoted to a whole-shelf drag.
        var root = new UiRoot { Width = 800f, Height = 600f };
        using var shelf = new PluginSidePanel(
            root.WindowManager, _ => (0u, 0, 0), font: null);
        root.AddChild(shelf);
        root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

        var frame = new UiPanel { Width = 200f, Height = 100f };
        root.AddChild(frame);
        RetailWindowHandle pluginHandle = root.WindowManager.Register(
            "plugin:acdream.test:main", frame);
        shelf.Add(
            new PluginUiOwner("acdream.test", "Test Plugin"),
            new PluginPanelDescriptor("main", "Test Plugin"),
            pluginHandle);
        root.Tick(0.016d, 16L);

        PluginSidePanel.PluginShelfButton button = Assert.Single(
            shelf.Children.OfType<PluginSidePanel.PluginShelfButton>());
        Vector2 buttonScreen = button.ScreenPosition;
        int pressX = (int)buttonScreen.X + 5;
        int pressY = (int)buttonScreen.Y + 5;
        float leftBefore = shelf.Left;
        float topBefore = shelf.Top;

        root.OnMouseDown(UiMouseButton.Left, pressX, pressY);
        root.OnMouseMove(pressX + 100, pressY + 100);
        root.OnMouseUp(UiMouseButton.Left, pressX + 100, pressY + 100);

        Assert.Equal(leftBefore, shelf.Left);
        Assert.Equal(topBefore, shelf.Top);
    }

    [Fact]
    public void PerTickZOrderRaise_Removed_ADialogAddedAfterKeepsItsHigherZOrder()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        using var shelf = new PluginSidePanel(
            root.WindowManager, _ => (0u, 0, 0), font: null);
        root.AddChild(shelf);
        root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

        var frame = new UiPanel { Width = 200f, Height = 100f };
        root.AddChild(frame);
        RetailWindowHandle pluginHandle = root.WindowManager.Register(
            "plugin:acdream.test:main", frame);
        shelf.Add(
            new PluginUiOwner("acdream.test", "Test Plugin"),
            new PluginPanelDescriptor("main", "Test Plugin"),
            pluginHandle);
        root.Tick(0.016d, 16L);

        int shelfZBefore = shelf.ZOrder;
        var dialog = new UiPanel { Width = 100f, Height = 60f, ZOrder = shelfZBefore + 50 };
        root.AddChild(dialog);

        root.Tick(0.016d, 16L);
        root.Tick(0.016d, 16L);

        Assert.Equal(shelfZBefore, shelf.ZOrder);
        Assert.True(dialog.ZOrder > shelf.ZOrder);
    }

    [Fact]
    public void ToggleClick_SavesCollapsedOnly_WithNoSubsequentHideOrMove()
    {
        // Finding 3a.
        string directory = Path.Combine(
            Path.GetTempPath(), "acdream-plugin-shelf-tests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var root = new UiRoot { Width = 800f, Height = 600f };
            using var shelf = new PluginSidePanel(
                root.WindowManager, _ => (0u, 0, 0), font: null);
            root.AddChild(shelf);
            root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

            var frame = new UiPanel { Width = 200f, Height = 100f };
            root.AddChild(frame);
            RetailWindowHandle pluginHandle = root.WindowManager.Register(
                "plugin:acdream.test:main", frame);
            shelf.Add(
                new PluginUiOwner("acdream.test", "Test Plugin"),
                new PluginPanelDescriptor("main", "Test Plugin"),
                pluginHandle);
            root.Tick(0.016d, 16L);

            using var persistence = new RetailWindowLayoutPersistence(
                root.WindowManager, store, () => "Alice", () => (800, 600),
                stateManagedVisibilityWindows: [WindowNames.PluginShelf]);

            float topBefore = shelf.Top;
            float rightEdgeBefore = shelf.Left + shelf.Width;

            int toggleX = (int)shelf.Left + (int)shelf.Width - 8;
            int toggleY = (int)shelf.Top + 4;
            root.OnMouseDown(UiMouseButton.Left, toggleX, toggleY);
            root.OnMouseUp(UiMouseButton.Left, toggleX, toggleY);

            UiWindowLayout saved = Assert.IsType<UiWindowLayout>(
                store.LoadWindowLayout("Alice", "800x600", WindowNames.PluginShelf, default));
            Assert.True(saved.Collapsed);
            Assert.True(saved.Visible);        // no Hide happened
            Assert.Equal(topBefore, saved.Y);  // no vertical Move happened
            Assert.Equal(shelf.Left, saved.X);
            Assert.Equal(rightEdgeBefore, shelf.Left + shelf.Width, precision: 3);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnregisteringTheLastPlugin_DoesNotPersistTheAvailabilityHideAsAUserHide()
    {
        // Finding 3b.
        string directory = Path.Combine(
            Path.GetTempPath(), "acdream-plugin-shelf-tests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var root = new UiRoot { Width = 800f, Height = 600f };
            using var shelf = new PluginSidePanel(
                root.WindowManager, _ => (0u, 0, 0), font: null);
            root.AddChild(shelf);
            root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

            var frame = new UiPanel { Width = 200f, Height = 100f };
            root.AddChild(frame);
            RetailWindowHandle pluginHandle = root.WindowManager.Register(
                "plugin:acdream.test:main", frame);
            shelf.Add(
                new PluginUiOwner("acdream.test", "Test Plugin"),
                new PluginPanelDescriptor("main", "Test Plugin"),
                pluginHandle);
            root.Tick(0.016d, 16L);

            using var persistence = new RetailWindowLayoutPersistence(
                root.WindowManager, store, () => "Alice", () => (800, 600),
                stateManagedVisibilityWindows: [WindowNames.PluginShelf]);

            // Establish a genuine "the user wants this visible" save.
            shelf.Show();
            Assert.True(shelf.Visible);

            root.WindowManager.Unregister(pluginHandle.Name);
            Assert.Equal(0, shelf.EntryCount);
            Assert.False(shelf.Visible);   // derived Visible DOES flip (no entries)...

            // ...but the persisted INTENT must still read visible=true: the
            // availability-hide is not subscribed for a state-managed window.
            UiWindowLayout saved = Assert.IsType<UiWindowLayout>(
                store.LoadWindowLayout("Alice", "800x600", WindowNames.PluginShelf, default));
            Assert.True(saved.Visible);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RestoreWindowState_HiddenAndCollapsedIntent_AppliesDirectly_ThenShowExpandsAndReveals()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        using var shelf = new PluginSidePanel(
            root.WindowManager, _ => (0u, 0, 0), font: null);
        root.AddChild(shelf);
        root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

        var frame = new UiPanel { Width = 200f, Height = 100f };
        root.AddChild(frame);
        RetailWindowHandle pluginHandle = root.WindowManager.Register(
            "plugin:acdream.test:main", frame);
        shelf.Add(
            new PluginUiOwner("acdream.test", "Test Plugin"),
            new PluginPanelDescriptor("main", "Test Plugin"),
            pluginHandle);
        root.Tick(0.016d, 16L);
        Assert.True(shelf.Visible);

        shelf.RestoreWindowState(new RetainedWindowState(Collapsed: true, RequestedVisible: false));

        Assert.False(shelf.Visible);
        Assert.True(shelf.CaptureWindowState().Collapsed);

        shelf.Show();

        Assert.True(shelf.Visible);
        Assert.False(shelf.CaptureWindowState().Collapsed);
    }

    [Fact]
    public void CollapseExpandViaTheToggle_DoesNotCollapseTheColumnWrapToOneColumn()
    {
        var root = new UiRoot { Width = 800f, Height = 260f };
        using var shelf = new PluginSidePanel(
            root.WindowManager, _ => (0u, 0, 0), font: null);
        root.AddChild(shelf);
        root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

        for (int i = 0; i < 12; i++)
        {
            var frame = new UiPanel { Width = 200f, Height = 100f };
            root.AddChild(frame);
            RetailWindowHandle handle = root.WindowManager.Register($"plugin:test:{i}", frame);
            shelf.Add(
                new PluginUiOwner($"test.{i}", $"Plugin {i}"),
                new PluginPanelDescriptor("main", $"Plugin {i}"),
                handle);
        }
        root.Tick(0.016d, 16L);

        int toggleX = (int)shelf.Left + (int)shelf.Width - 8;
        int toggleY = (int)shelf.Top + 4;
        root.OnMouseDown(UiMouseButton.Left, toggleX, toggleY);
        root.OnMouseUp(UiMouseButton.Left, toggleX, toggleY);

        toggleX = (int)shelf.Left + (int)shelf.Width - 8;
        toggleY = (int)shelf.Top + 4;
        root.OnMouseDown(UiMouseButton.Left, toggleX, toggleY);
        root.OnMouseUp(UiMouseButton.Left, toggleX, toggleY);

        root.Tick(0.016d, 16L);

        Assert.True(shelf.Top + shelf.Height <= root.Height);
        Assert.All(
            shelf.Children.OfType<PluginSidePanel.PluginShelfButton>(),
            button => Assert.True(button.Top + button.Height <= shelf.Height));
    }

    [Fact]
    public void FreshShelf_OneTick_SavesTheDockedPositionImmediately()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "acdream-plugin-shelf-tests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var root = new UiRoot { Width = 800f, Height = 600f };
            using var shelf = new PluginSidePanel(
                root.WindowManager, _ => (0u, 0, 0), font: null);
            root.AddChild(shelf);
            root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

            var frame = new UiPanel { Width = 200f, Height = 100f };
            root.AddChild(frame);
            RetailWindowHandle pluginHandle = root.WindowManager.Register(
                "plugin:acdream.test:main", frame);
            shelf.Add(
                new PluginUiOwner("acdream.test", "Test Plugin"),
                new PluginPanelDescriptor("main", "Test Plugin"),
                pluginHandle);

            using var persistence = new RetailWindowLayoutPersistence(
                root.WindowManager, store, () => "Alice", () => (800, 600));

            root.Tick(0.016d, 16L);   // the one-time dock happens inside this tick

            float expectedLeft = 800f - shelf.Width - 4f;
            Assert.Equal(expectedLeft, shelf.Left);
            Assert.Equal(116f, shelf.Top);

            UiWindowLayout saved = Assert.IsType<UiWindowLayout>(
                store.LoadWindowLayout("Alice", "800x600", WindowNames.PluginShelf, default));
            Assert.Equal((expectedLeft, 116f), (saved.X, saved.Y));

            float rightEdgeBefore = shelf.Left + shelf.Width;
            shelf.RestoreWindowState(new RetainedWindowState(Collapsed: true));
            Assert.Equal(rightEdgeBefore, shelf.Left + shelf.Width, precision: 3);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClampAllToScreen_AfterShrinkingTheRoot_DoesNotFlipAnchoring_ButARealDragStillDoes()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "acdream-plugin-shelf-tests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var root = new UiRoot { Width = 800f, Height = 600f };
            using var shelf = new PluginSidePanel(
                root.WindowManager, _ => (0u, 0, 0), font: null);
            root.AddChild(shelf);
            root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

            var frame = new UiPanel { Width = 200f, Height = 100f };
            root.AddChild(frame);
            RetailWindowHandle pluginHandle = root.WindowManager.Register(
                "plugin:acdream.test:main", frame);
            shelf.Add(
                new PluginUiOwner("acdream.test", "Test Plugin"),
                new PluginPanelDescriptor("main", "Test Plugin"),
                pluginHandle);
            root.Tick(0.016d, 16L);   // dock at Left=760 (800-36-4) under an 800-wide parent

            using var persistence = new RetailWindowLayoutPersistence(
                root.WindowManager, store, () => "Alice", () => (760, 600));

            root.Width = 760f;
            persistence.ClampAllToScreen();

            Assert.Equal(724f, shelf.Left);

            float rightEdgeBefore = shelf.Left + shelf.Width;
            root.Tick(0.016d, 16L);   // a later reflow — still docked, right edge holds
            Assert.Equal(rightEdgeBefore, shelf.Left + shelf.Width, precision: 3);

            shelf.RestoreWindowState(new RetainedWindowState(Collapsed: true));
            Assert.Equal(rightEdgeBefore, shelf.Left + shelf.Width, precision: 3);
            shelf.RestoreWindowState(new RetainedWindowState(Collapsed: false));

            // A REAL drag away from the edge afterward must still flip anchoring.
            int pressX = (int)shelf.Left + 10;
            int pressY = (int)shelf.Top + 5;
            root.OnMouseDown(UiMouseButton.Left, pressX, pressY);
            root.OnMouseMove(pressX - 100, pressY);
            root.OnMouseUp(UiMouseButton.Left, pressX - 100, pressY);

            float leftAfterDrag = shelf.Left;
            Assert.NotEqual(724f, leftAfterDrag);
            shelf.RestoreWindowState(new RetainedWindowState(Collapsed: true));
            Assert.Equal(leftAfterDrag, shelf.Left);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ZeroMovementGripReleaseDoesNotFlipAnchoring_ButARealDragDoes()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        using var shelf = new PluginSidePanel(
            root.WindowManager, _ => (0u, 0, 0), font: null);
        root.AddChild(shelf);
        root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

        var frame = new UiPanel { Width = 200f, Height = 100f };
        root.AddChild(frame);
        RetailWindowHandle pluginHandle = root.WindowManager.Register(
            "plugin:acdream.test:main", frame);
        shelf.Add(
            new PluginUiOwner("acdream.test", "Test Plugin"),
            new PluginPanelDescriptor("main", "Test Plugin"),
            pluginHandle);
        root.Tick(0.016d, 16L);   // establishes the initial right-edge dock

        int pressX = (int)shelf.Left + 10;
        int pressY = (int)shelf.Top + 5;
        root.OnMouseDown(UiMouseButton.Left, pressX, pressY);
        root.OnMouseUp(UiMouseButton.Left, pressX, pressY);   // zero movement

        float rightEdgeBeforeCollapse = shelf.Left + shelf.Width;
        shelf.RestoreWindowState(new RetainedWindowState(Collapsed: true));
        Assert.Equal(rightEdgeBeforeCollapse, shelf.Left + shelf.Width, precision: 3);
        shelf.RestoreWindowState(new RetainedWindowState(Collapsed: false));

        // Now a REAL drag via the grip.
        root.OnMouseDown(UiMouseButton.Left, pressX, pressY);
        root.OnMouseMove(pressX + 40, pressY + 40);
        root.OnMouseUp(UiMouseButton.Left, pressX + 40, pressY + 40);

        float leftAfterDrag = shelf.Left;
        shelf.RestoreWindowState(new RetainedWindowState(Collapsed: true));
        // User-positioned now: the LEFT edge survives instead.
        Assert.Equal(leftAfterDrag, shelf.Left);
    }

    [Fact]
    public void ShelfButton_NormalizesADecalBareIndexDescriptorId_BeforeResolving()
    {
        var resolvedIds = new List<uint>();
        var root = new UiRoot { Width = 800f, Height = 600f };
        using var shelf = new PluginSidePanel(
            root.WindowManager,
            id => { resolvedIds.Add(id); return (9u, 32, 32); },
            font: null);
        root.AddChild(shelf);

        var frame = new UiPanel { Width = 200f, Height = 100f };
        root.AddChild(frame);
        RetailWindowHandle handle = root.WindowManager.Register(
            "plugin:acdream.test:main", frame);
        shelf.Add(
            new PluginUiOwner("acdream.test", "Test Plugin"),
            new PluginPanelDescriptor("main", "Test Plugin")
            {
                IconSurfaceId = 0x165u,
            },
            handle);

        PluginSidePanel.PluginShelfButton button = Assert.Single(
            shelf.Children.OfType<PluginSidePanel.PluginShelfButton>());
        button.DrawSelfAndChildren(TestUiRenderContext());

        Assert.Contains(0x06000165u, resolvedIds);
    }

    private sealed class FakeResolveDidResolver
    {
        public (uint tex, int w, int h) ResolveDid(uint did) => (0u, 0, 0);
    }

    [Fact]
    public void ShelfButton_FallsBackToInitials_WhenTheResolveNeverYieldsATexture()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var resolver = new FakeResolveDidResolver();
        using var shelf = new PluginSidePanel(
            root.WindowManager,
            resolver.ResolveDid,
            font: null);
        root.AddChild(shelf);

        var frame = new UiPanel { Width = 200f, Height = 100f };
        root.AddChild(frame);
        RetailWindowHandle handle = root.WindowManager.Register(
            "plugin:acdream.test:main", frame);
        shelf.Add(
            new PluginUiOwner("acdream.test", "Test Plugin"),
            new PluginPanelDescriptor("main", "Test Plugin")
            {
                IconText = "TP",
                IconSurfaceId = 0x165u,
            },
            handle);

        PluginSidePanel.PluginShelfButton button = Assert.Single(
            shelf.Children.OfType<PluginSidePanel.PluginShelfButton>());
        // Before any draw, the button still shows empty text (a non-zero
        // icon id was supplied and has not been attempted yet).
        Assert.Equal(string.Empty, button.Text);

        button.DrawSelfAndChildren(TestUiRenderContext());

        Assert.Equal("TP", button.Text);
    }

    private static UiRenderContext TestUiRenderContext()
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(
            device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(200f, 200f));
        return new UiRenderContext(renderer, new Vector2(200f, 200f));
    }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }
}
