using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

/// <summary>Pins the nine-region reading of a window's border: the four corner
/// squares always offer their own two sides (narrowed to whichever axis the
/// window can actually resize), while the flat runs between them offer only the
/// sides the window declares resizable and are otherwise the move affordance.
/// </summary>
public sealed class UiWindowBorderRegionTests
{
    /// <summary>A window that can only change height, whose flat bottom run is
    /// the resize handle — the shape every shared main panel is mounted with.
    /// </summary>
    private static UiPanel HeightOnlyWindow() => new()
    {
        Left = 100,
        Top = 100,
        Width = 300,
        Height = 200,
        Draggable = true,
        Resizable = true,
        ResizeX = false,
        ResizeY = true,
        ResizableEdges = ResizeEdges.Bottom,
        MinWidth = 40,
        MinHeight = 40,
        MaxWidth = 2000,
        MaxHeight = 2000,
    };

    [Theory]
    // top-left and top-right corners resize from the top …
    [InlineData(102, 102, ResizeEdges.Top)]
    [InlineData(398, 102, ResizeEdges.Top)]
    // … bottom corners from the bottom.
    [InlineData(102, 298, ResizeEdges.Bottom)]
    [InlineData(398, 298, ResizeEdges.Bottom)]
    public void Corners_OfAHeightOnlyWindow_OfferTheVerticalResize(int x, int y, ResizeEdges expected)
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = HeightOnlyWindow();
        root.AddChild(window);

        root.OnMouseMove(x, y);

        Assert.Equal(expected, root.HoverResizeEdges);
        Assert.False(root.HoverWindowMove);

        var cursor = new CursorFeedbackController().Update(root);
        Assert.Equal(CursorFeedbackKind.ResizeVertical, cursor.Kind);
    }

    [Theory]
    [InlineData(250, 102)]   // flat top run
    [InlineData(102, 200)]   // flat left run
    [InlineData(398, 200)]   // flat right run
    public void FlatRuns_OutsideTheDeclaredResizeEdges_StayMoveHandles(int x, int y)
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = HeightOnlyWindow();
        root.AddChild(window);

        root.OnMouseMove(x, y);

        Assert.Equal(ResizeEdges.None, root.HoverResizeEdges);
        Assert.True(root.HoverWindowMove);

        var cursor = new CursorFeedbackController().Update(root);
        Assert.Equal(CursorFeedbackKind.WindowMove, cursor.Kind);
    }

    [Fact]
    public void TopLeftCornerDrag_OnAHeightOnlyWindow_MovesTheOriginAndGrowsHeightOnly()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = HeightOnlyWindow();
        root.AddChild(window);

        root.OnMouseDown(UiMouseButton.Left, 102, 102);
        Assert.Equal(ResizeEdges.Top, root.ActiveResizeEdges);

        root.OnMouseMove(62, 62);   // drag up-and-left by 40 px on both axes
        root.OnMouseUp(UiMouseButton.Left, 62, 62);

        Assert.Equal(60f, window.Top);       // top edge followed the pointer …
        Assert.Equal(240f, window.Height);   // … and the bottom edge stayed put
        Assert.Equal(100f, window.Left);     // horizontal component ignored
        Assert.Equal(300f, window.Width);
    }

    [Fact]
    public void BottomRightCorner_OfAWindowResizableOnBothAxes_OffersTheDiagonal()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = new UiPanel
        {
            Left = 100, Top = 100, Width = 300, Height = 200,
            Draggable = true, Resizable = true,
            MinWidth = 40, MinHeight = 40, MaxWidth = 2000, MaxHeight = 2000,
        };
        root.AddChild(window);

        root.OnMouseMove(398, 298);

        Assert.Equal(ResizeEdges.Right | ResizeEdges.Bottom, root.HoverResizeEdges);
        Assert.False(root.HoverWindowMove);

        var cursor = new CursorFeedbackController().Update(root);
        Assert.Equal(CursorFeedbackKind.ResizeDiagonalNwse, cursor.Kind);
    }

    [Fact]
    public void Corner_OfAWindowWithBothAxesLocked_IsStillOnlyAMoveHandle()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = new UiPanel
        {
            Left = 100, Top = 100, Width = 300, Height = 200,
            Draggable = true, Resizable = true,
            ResizeX = false, ResizeY = false,
        };
        root.AddChild(window);

        root.OnMouseMove(102, 102);

        Assert.Equal(ResizeEdges.None, root.HoverResizeEdges);
        Assert.True(root.HoverWindowMove);

        var cursor = new CursorFeedbackController().Update(root);
        Assert.Equal(CursorFeedbackKind.WindowMove, cursor.Kind);
    }
}
