using AcDream.App.UI;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using Silk.NET.Input;

namespace AcDream.App.Tests.UI;

public sealed class UiMenuSearchTests
{
    private static (UiRoot root, UiMenu menu) Create()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var menu = new UiMenu
        {
            Left = 10, Top = 20, Width = 180, Height = 20,
            Searchable = true, Scrollable = true, OpenUpward = false,
            RowsPerColumn = 2, RowHeight = 18,
            Items = new[] {
                new UiMenu.MenuItem("Olthoi north route", new object()),
                new UiMenu.MenuItem("Olthoi south route", new object()),
                new UiMenu.MenuItem("North town", new object()),
                new UiMenu.MenuItem("Olthoi north cave", new object()) },
        };
        root.AddChild(menu);
        root.OnMouseDown(UiMouseButton.Left, 20, 30);
        root.OnMouseUp(UiMouseButton.Left, 20, 30);
        return (root, menu);
    }

    private static void Type(UiRoot root, string text)
    {
        foreach (char c in text) root.OnChar(c);
    }

    [Fact]
    public void WordsFilterCaseInsensitivelyWithoutSelecting_EnterRetainsPayload()
    {
        var (root, menu) = Create();
        object? picked = null;
        menu.Selected = menu.Items[1].Payload;
        menu.OnSelect = value => picked = value;
        Type(root, "NORTH olthoi");
        Assert.Equal(2, menu.FilteredItemsForTest.Count);
        Assert.Null(picked);
        Assert.Same(menu.Items[1].Payload, menu.Selected);
        root.OnKeyDown((int)Key.Enter);
        Assert.Same(menu.Items[0].Payload, picked);
        Assert.False(menu.IsOpen);
        Assert.Null(root.KeyboardFocus);
    }

    [Fact]
    public void EmptyResultDoesNotSelect_EditingRestoresResults_EscapeDismisses()
    {
        var (root, menu) = Create();
        int selections = 0;
        menu.OnSelect = _ => selections++;
        Type(root, "zz");
        Assert.Empty(menu.FilteredItemsForTest);
        root.OnKeyDown((int)Key.Enter);
        Assert.True(menu.IsOpen);
        root.OnKeyDown((int)Key.Backspace);
        root.OnKeyUp((int)Key.Backspace);
        root.OnKeyDown((int)Key.Backspace);
        root.OnKeyUp((int)Key.Backspace);
        Assert.Equal(4, menu.FilteredItemsForTest.Count);
        root.OnKeyDown((int)Key.Escape);
        Assert.False(menu.IsOpen);
        Assert.Equal(0, selections);
    }

    [Fact]
    public void FilterResetsScrollAndMouseSelectionUsesOffsetBelowSearchBand()
    {
        var (root, menu) = Create();
        menu.PopupScroll.SetExtents(200, 36);
        menu.PopupScroll.SetScrollY(100);
        Type(root, "south");
        Assert.Equal(0, menu.PopupScroll.ScrollY);
        object? picked = null;
        menu.OnSelect = value => picked = value;
        root.OnMouseDown(UiMouseButton.Left, 25, 75);
        root.OnMouseUp(UiMouseButton.Left, 25, 75);
        Assert.Same(menu.Items[1].Payload, picked);
    }

    [Fact]
    public void ReopenRefreshesItemsAndClearsQuery()
    {
        var (root, menu) = Create();
        Type(root, "south");
        root.OnKeyDown((int)Key.Escape);
        menu.BeforeOpen = () => menu.Items = new[] { new UiMenu.MenuItem("New profile", "new") };
        root.OnMouseDown(UiMouseButton.Left, 20, 30);
        root.OnMouseUp(UiMouseButton.Left, 20, 30);
        Assert.Single(menu.FilteredItemsForTest);
        Assert.Equal("new", menu.FilteredItemsForTest[0].Payload);
    }

    [Fact]
    public void ArrowNavigationSkipsDisabledRowsAndScrollsBeforeCommit()
    {
        var (root, menu) = Create();
        menu.EnabledProvider = payload => !ReferenceEquals(payload, menu.Items[1].Payload);
        object? picked = null;
        menu.OnSelect = value => picked = value;
        root.OnKeyDown((int)Key.Down);
        root.OnKeyDown((int)Key.Down);
        Assert.Null(picked);
        Assert.True(menu.PopupScroll.ScrollY > 0);
        root.OnKeyDown((int)Key.Enter);
        Assert.Same(menu.Items[2].Payload, picked);
    }

    private sealed class EmptyFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private static void PrepareSearchField(UiMenu menu)
    {
        var glyphs = "northsou".Distinct().ToDictionary(c => c,
            c => new DatReaderWriter.Types.FontCharDesc { Unicode = c, Width = 8, Height = 8 });
        menu.DatFont = new UiDatFont(1, 128, 128, 0, 0, 0, 16, 12, glyphs);
        menu.RetailButtonArt = false;
        using var renderer = new TextRenderer(new RecordingGpuDevice(), new EmptyFrameSource(), "unused");
        renderer.Begin(new Vector2(800, 600));
        menu.DrawOverlays(new UiRenderContext(renderer, new Vector2(800, 600)));
    }

    [Fact]
    public void SearchDragSelectsTextAcrossRowsWithoutHoveringAResult()
    {
        var (root, menu) = Create();
        Type(root, "north");
        PrepareSearchField(menu);
        root.OnMouseDown(UiMouseButton.Left, 65, 50);
        root.OnMouseMove(15, 100);
        root.OnMouseUp(UiMouseButton.Left, 15, 100);
        Assert.Equal(-1, menu.HoveredPopupIndexForTest);
        Type(root, "south");
        Assert.Single(menu.FilteredItemsForTest);
        Assert.Same(menu.Items[1].Payload, menu.FilteredItemsForTest[0].Payload);
    }

    [Fact]
    public void CaptureLossEndsSearchDrag()
    {
        var (root, menu) = Create();
        Type(root, "north");
        PrepareSearchField(menu);
        menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 55, 30));
        menu.OnEvent(new UiEvent(0, menu, UiEventType.CaptureChanged));
        menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseMove, 0, 5, 60));
        root.OnKeyDown((int)Key.Backspace);
        root.OnKeyUp((int)Key.Backspace);
        Type(root, "h");
        Assert.Equal(3, menu.FilteredItemsForTest.Count);
    }

    [Fact]
    public void PlainPopupScrollbarDoesNotResolveSpriteChrome()
    {
        var (_, menu) = Create();
        menu.RetailButtonArt = false;
        menu.PlainPopupScrollbar = true;
        int calls = 0;
        menu.SpriteResolve = _ => { calls++; return (77u, 16, 16); };
        menu.ScrollTrackSprite = 1;
        menu.ScrollUpSprite = 2;
        menu.ScrollDownSprite = 3;
        using var renderer = new TextRenderer(new RecordingGpuDevice(), new EmptyFrameSource(), "unused");
        renderer.Begin(new Vector2(800, 600));
        menu.DrawOverlays(new UiRenderContext(renderer, new Vector2(800, 600)));
        Assert.True(menu.PopupScroll.HasOverflow);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void DefaultMenuDoesNotAcquireTextFocus()
    {
        var root = new UiRoot { Width = 400, Height = 400 };
        var menu = new UiMenu { Width = 100, Height = 20, Items = new[] { new UiMenu.MenuItem("A", "a") } };
        root.AddChild(menu);
        root.OnMouseDown(UiMouseButton.Left, 10, 10);
        Assert.False(menu.Searchable);
        Assert.Null(root.KeyboardFocus);
        Assert.False(menu.IsEditControl);
    }
}
