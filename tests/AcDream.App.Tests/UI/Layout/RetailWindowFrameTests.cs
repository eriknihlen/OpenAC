using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class RetailWindowFrameTests
{
    private static (uint, int, int) NoTex(uint _) => (0u, 0, 0);

    [Theory]
    [InlineData(9, 459f, 288f)]
    [InlineData(13, 587f, 416f)]
    [InlineData(18, 747f, 576f)]
    public void CombatRoot_FitsRequestedFavoriteSlotCapacity(
        int visibleSlots,
        float expectedRootWidth,
        float expectedListWidth)
    {
        ImportedLayout combat = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);

        float width = RetailCombatLayout.FitFavoriteSlots(combat, visibleSlots);

        Assert.Equal(expectedRootWidth, width);
        Assert.Equal(expectedListWidth, combat.FindElement(
            SpellcastingUiController.FavoriteListId)!.Width);
    }

    [Fact]
    public void CombatRoot_EighteenSlotsKeepArrowsAndCastInsideFrame()
    {
        ImportedLayout combat = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);

        RetailCombatLayout.FitFavoriteSlots(combat);

        UiElement group = combat.FindElement(0x100000AAu)!;
        UiElement scrollbar = combat.FindElement(SpellcastingUiController.FavoriteScrollbarId)!;
        UiElement cast = combat.FindElement(SpellcastingUiController.CastButtonId)!;
        Assert.Equal(747f, combat.Root.Width);
        Assert.Equal((40f, 622f), (group.Left, group.Width));
        Assert.Equal(622f, scrollbar.Width);
        Assert.Equal((662f, 75f), (cast.Left, cast.Width));
        Assert.Equal(737f, cast.Left + cast.Width);
    }

    [Fact]
    public void ImportedChrome_MountsRootWithoutASecondFrame()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var content = new UiPanel { Width = 220, Height = 90 };

        RetailWindowHandle handle = RetailWindowFrame.Mount(
            root,
            content,
            NoTex,
            new RetailWindowFrame.Options
            {
                WindowName = "vitals",
                Chrome = RetailWindowChrome.Imported,
                Left = 12,
                Top = 18,
                ResizeX = true,
                ResizeY = false,
                MinWidth = 40,
                ContentClickThrough = false,
            });

        Assert.Same(content, handle.OuterFrame);
        Assert.Same(content, handle.ContentRoot);
        Assert.Same(root, content.Parent);
        Assert.Equal((12f, 18f, 220f, 90f),
            (content.Left, content.Top, content.Width, content.Height));
        Assert.True(content.Draggable);
        Assert.True(content.Resizable);
        Assert.True(content.ResizeX);
        Assert.False(content.ResizeY);
        Assert.Equal(40f, content.MinWidth);
    }

    [Fact]
    public void ImportedChrome_CanPreserveFixedHudDesktopAnchors()
    {
        var root = new UiRoot { Width = 1920, Height = 1080 };
        var content = new UiPanel { Width = 1730, Height = 90 };

        RetailWindowHandle handle = RetailWindowFrame.Mount(
            root,
            content,
            NoTex,
            new RetailWindowFrame.Options
            {
                WindowName = "combat",
                Chrome = RetailWindowChrome.Imported,
                Left = 0f,
                Top = 980f,
                OuterAnchors = AnchorEdges.Left | AnchorEdges.Bottom,
            });

        Assert.Equal(
            AnchorEdges.Left | AnchorEdges.Bottom,
            handle.OuterFrame.Anchors);
    }

    [Fact]
    public void NineSlice_UsesCroppedContentAndDatHeightConstraints()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var content = new UiPanel { Width = 800, Height = 100 };
        var constraints = Constraints(minHeight: 100, maxHeight: 360);

        RetailWindowHandle handle = RetailWindowFrame.Mount(
            root,
            content,
            NoTex,
            new RetailWindowFrame.Options
            {
                WindowName = "chat",
                Chrome = RetailWindowChrome.NineSlice,
                Left = 10,
                Top = 440,
                ContentWidth = 490,
                DatConstraintSource = constraints,
                MinWidth = 200,
                Opacity = 0.75f,
            });

        var frame = Assert.IsType<UiNineSlicePanel>(handle.OuterFrame);
        Assert.Same(content, handle.ContentRoot);
        Assert.Same(frame, content.Parent);
        Assert.Equal((500f, 110f), (frame.Width, frame.Height));
        Assert.Equal((5f, 5f, 490f, 100f),
            (content.Left, content.Top, content.Width, content.Height));
        Assert.Equal(200f, frame.MinWidth);
        Assert.Equal(110f, frame.MinHeight);
        Assert.Equal(370f, frame.MaxHeight);
        Assert.Equal(0.75f, frame.Opacity);
    }

    [Fact]
    public void NineSlice_ContentShapedConstraints_InsetArithmeticClampsProgrammaticResize()
    {
        var root = new UiRoot { Width = 1920, Height = 1080 };
        var content = new UiPanel { Width = 490, Height = 100 };
        var constraints = Constraints(minHeight: 100, maxHeight: 2000);

        RetailWindowHandle handle = RetailWindowFrame.Mount(
            root,
            content,
            NoTex,
            new RetailWindowFrame.Options
            {
                WindowName = "chat-shaped",
                Chrome = RetailWindowChrome.NineSlice,
                Left = 10,
                Top = 20,
                ResizeX = false,
                ResizeY = true,
                DatConstraintSource = constraints,
            });

        Assert.True(handle.ResizeTo(handle.Width, 5f));
        Assert.Equal(110f, handle.Height);

        // Request far above the authored maximum (2000 + 10 = 2010).
        Assert.True(handle.ResizeTo(handle.Width, 50000f));
        Assert.Equal(2010f, handle.Height);

        // A request inside the bounds is honored exactly.
        Assert.True(handle.ResizeTo(handle.Width, 500f));
        Assert.Equal(500f, handle.Height);
    }

    [Fact]
    public void Imported_ChatContract_ClampsAtAuthoredBoundsWithNoChromeInset()
    {
        var root = new UiRoot { Width = 1920, Height = 1080 };
        var content = new UiPanel { Width = 410, Height = 100 };
        var constraints = ChatConstraints();

        RetailWindowHandle handle = RetailWindowFrame.Mount(
            root,
            content,
            NoTex,
            new RetailWindowFrame.Options
            {
                WindowName = "chat",
                Chrome = RetailWindowChrome.Imported,
                Left = 10,
                Top = 440,
                ResizeX = true,
                ResizeY = true,
                DatConstraintSource = constraints,
            });

        Assert.Equal(300f, handle.OuterFrame.MinWidth);
        Assert.Equal(100f, handle.OuterFrame.MinHeight);
        Assert.Equal(2000f, handle.OuterFrame.MaxWidth);
        Assert.Equal(2000f, handle.OuterFrame.MaxHeight);

        Assert.True(handle.ResizeTo(handle.Width, 5f));
        Assert.Equal(100f, handle.Height);

        Assert.True(handle.ResizeTo(handle.Width, 50000f));
        Assert.Equal(2000f, handle.Height);
    }

    [Fact]
    public void NineSlice_CanSupplyBorderWithoutDuplicatingAuthoredCenter()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var content = new UiPanel { Width = 300, Height = 362 };

        RetailWindowHandle handle = RetailWindowFrame.Mount(
            root, content, NoTex,
            new RetailWindowFrame.Options
            {
                WindowName = "character-info",
                DrawChromeCenter = false,
            });

        var frame = Assert.IsType<UiNineSlicePanel>(handle.OuterFrame);
        Assert.False(frame.DrawCenterFill);
    }

    [Fact]
    public void CollapsibleMount_ReturnsToolbarFrameAndIndependentAxes()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var content = new UiPanel { Width = 300, Height = 122 };

        RetailWindowHandle handle = RetailWindowFrame.Mount(
            root,
            content,
            NoTex,
            new RetailWindowFrame.Options
            {
                WindowName = "toolbar",
                Chrome = RetailWindowChrome.CollapsibleNineSlice,
                ResizeX = false,
                ResizeY = true,
                ResizableEdges = ResizeEdges.Bottom,
                ConstrainResizeToParent = true,
                ContentAnchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Right,
                ContentClickThrough = true,
            });

        var frame = Assert.IsType<UiCollapsibleFrame>(handle.OuterFrame);
        Assert.False(frame.ResizeX);
        Assert.True(frame.ResizeY);
        Assert.Equal(ResizeEdges.Bottom, frame.ResizableEdges);
        Assert.True(frame.ConstrainResizeToParent);
        Assert.True(content.ClickThrough);
        Assert.False(content.Draggable);
        Assert.False(content.Resizable);
    }

    [Fact]
    public void HiddenInventory_RestoredExpandedBeforeFirstDraw_GrowsContentsRows()
    {
        ImportedLayout layout = FixtureLoader.LoadInventory();
        UiItemList grid = Assert.IsType<UiItemList>(
            layout.FindElement(InventoryController.ContentsGridId));
        grid.Columns = 6;
        grid.CellWidth = 32f;
        grid.CellHeight = 32f;
        grid.Flush();
        for (int i = 0; i < 102; i++)
            grid.AddItem(new UiItemSlot());

        var root = new UiRoot { Width = 1280, Height = 720 };
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            root,
            layout.Root,
            NoTex,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Inventory,
                Chrome = RetailWindowChrome.NineSlice,
                ContentWidth = layout.Root.Width,
                ContentHeight = layout.Root.Height,
                Visible = false,
                ResizeX = false,
                ResizeY = true,
                ResizableEdges = ResizeEdges.Bottom,
                MaxHeight = 560f,
            });

        InventoryController.ConfigureResizeLayout(layout);

        // Persistence restores the hidden window before its first visible draw.
        handle.ResizeTo(handle.Width, 527f);
        ApplyAnchors(handle.OuterFrame);
        grid.LayoutCells();

        Assert.Equal(251f, grid.Height);
        Assert.Equal(48, grid.Children.Count(child => child.Visible));
        Assert.Equal(224f, grid.GetItem(42)!.Top); // final row intersects 251px view by 27px
    }

    private static void ApplyAnchors(UiElement parent)
    {
        foreach (UiElement child in parent.Children)
        {
            child.ApplyAnchor(parent.Width, parent.Height);
            ApplyAnchors(child);
        }
    }

    private static ElementInfo Constraints(int minHeight, int maxHeight)
    {
        var info = new ElementInfo();
        var direct = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        direct.Properties.Values[0x3Eu] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Integer,
            IntegerValue = minHeight,
        };
        direct.Properties.Values[0x3Cu] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Integer,
            IntegerValue = maxHeight,
        };
        info.States[UiStateInfo.DirectStateId] = direct;
        return info;
    }

    private static ElementInfo ChatConstraints()
    {
        var info = new ElementInfo();
        var direct = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        direct.Properties.Values[0x3Fu] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Integer,
            IntegerValue = 300,
        };
        direct.Properties.Values[0x3Eu] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Integer,
            IntegerValue = 100,
        };
        direct.Properties.Values[0x3Du] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Integer,
            IntegerValue = 2000,
        };
        direct.Properties.Values[0x3Cu] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Integer,
            IntegerValue = 2000,
        };
        info.States[UiStateInfo.DirectStateId] = direct;
        return info;
    }
}
