using System.Linq;
using System.Numerics;
using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public class UiRootInputTests
{
    [Fact]
    public void KeypadEnter_DoesNotUseTheRawChatActivationFallback()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var field = new UiField { Width = 100, Height = 20 };
        root.AddChild(field);
        root.DefaultTextInput = field;

        root.OnKeyDown((int)Silk.NET.Input.Key.KeypadEnter);

        Assert.Null(root.KeyboardFocus);
    }

    [Fact]
    public void SemanticChatActivation_SuppressesTheSameNativeEnterTail()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var field = new UiField { Width = 100, Height = 20 };
        int submissions = 0;
        field.SetText("hello");
        field.OnSubmit = _ => submissions++;
        root.AddChild(field);
        root.DefaultTextInput = field;
        root.SetKeyboardFocus(field);
        root.SuppressPhysicalKeyUntilRelease(Silk.NET.Input.Key.Enter);

        root.OnKeyDown((int)Silk.NET.Input.Key.Enter);
        root.OnChar('x');

        Assert.Equal(0, submissions);
        Assert.Equal("hello", field.Text);
        Assert.Same(field, root.KeyboardFocus);

        root.OnKeyUp((int)Silk.NET.Input.Key.Enter);
        root.OnChar('x');
        Assert.Equal("hellox", field.Text);
    }

    [Fact]
    public void UiNineSlicePanel_IsNotAnchorManaged_SoUserMoveResizeSticks()
    {
        // Regression: the per-frame anchor pass must NOT reset a window's rect,
        // or move/resize get undone every frame. Windows are user-positioned.
        var panel = new UiNineSlicePanel(_ => ((uint)1, 32, 32));
        Assert.Equal(AnchorEdges.None, panel.Anchors);
    }

    private sealed class ClickRecorder : UiElement
    {
        private readonly bool _handlesClick;
        public bool Clicked;
        public int Clicks;
        public int DoubleClicks;
        public Action? ClickAction;
        public ClickRecorder(bool handlesClick) => _handlesClick = handlesClick;
        public override bool HandlesClick => _handlesClick;
        public override bool OnEvent(in UiEvent e)
        {
            if (e.Type == UiEventType.Click)
            {
                Clicked = true;
                Clicks++;
                ClickAction?.Invoke();
                return true;
            }
            if (e.Type == UiEventType.DoubleClick) { DoubleClicks++; return true; }
            return false;
        }
    }

    [Fact]
    public void HandlesClickWidget_insideDraggableWindow_stillEmitsClick()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var frame = new UiPanel { Left = 10, Top = 300, Width = 200, Height = 60, Draggable = true };
        var btn = new ClickRecorder(handlesClick: true) { Left = 5, Top = 5, Width = 120, Height = 14 };
        frame.AddChild(btn);
        root.AddChild(frame);

        root.OnMouseDown(UiMouseButton.Left, 20, 310);   // press over the button (screen rect 15,305..135,319)
        root.OnMouseUp(UiMouseButton.Left, 20, 310);
        Assert.True(btn.Clicked);
    }

    [Fact]
    public void DoubleClick_EmitsRealDoubleClickEvent()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var btn = new ClickRecorder(handlesClick: true) { Left = 5, Top = 5, Width = 120, Height = 14 };
        root.AddChild(btn);

        root.Tick(0, nowMs: 1_000);
        root.OnMouseDown(UiMouseButton.Left, 20, 10);
        root.OnMouseUp(UiMouseButton.Left, 20, 10);
        root.Tick(0, nowMs: 1_300);
        root.OnMouseDown(UiMouseButton.Left, 20, 10);
        root.OnMouseUp(UiMouseButton.Left, 20, 10);

        Assert.Equal(2, btn.Clicks);
        Assert.Equal(1, btn.DoubleClicks);
    }

    [Fact]
    public void DoubleClick_WhenClickHidesCapturedWindow_KeepsOriginalEventTarget()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = new UiPanel { Width = 200, Height = 100 };
        var btn = new ClickRecorder(handlesClick: true) { Width = 120, Height = 20 };
        window.AddChild(btn);
        root.AddChild(window);
        root.RegisterWindow("test", window);

        root.Tick(0, nowMs: 1_000);
        root.OnMouseDown(UiMouseButton.Left, 10, 10);
        root.OnMouseUp(UiMouseButton.Left, 10, 10);

        btn.ClickAction = () => root.HideWindow("test");
        root.Tick(0, nowMs: 1_300);
        root.OnMouseDown(UiMouseButton.Left, 10, 10);
        root.OnMouseUp(UiMouseButton.Left, 10, 10);

        Assert.False(window.Visible);
        Assert.Null(root.Captured);
        Assert.Equal(2, btn.Clicks);
        Assert.Equal(1, btn.DoubleClicks);
    }

    [Fact]
    public void MouseUp_WhenClickTransfersCapture_PreservesNewCaptureOwner()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var btn = new ClickRecorder(handlesClick: true) { Width = 120, Height = 20 };
        var modal = new UiPanel { Left = 200, Width = 100, Height = 100 };
        root.AddChild(btn);
        root.AddChild(modal);
        btn.ClickAction = () => root.SetCapture(modal);

        root.OnMouseDown(UiMouseButton.Left, 10, 10);
        root.OnMouseUp(UiMouseButton.Left, 10, 10);

        Assert.Same(modal, root.Captured);
    }

    [Fact]
    public void ItemDragReleasedOutsideUi_raisesOutsideReleaseEvent()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var cell = new UiItemSlot { Left = 0, Top = 0, Width = 32, Height = 32 };
        cell.SetItem(0x50000A01u, 0x99u);
        root.AddChild(cell);
        object? payload = null;
        int releaseX = 0;
        int releaseY = 0;
        root.DragReleasedOutsideUi += (p, x, y) =>
        {
            payload = p;
            releaseX = x;
            releaseY = y;
        };

        root.OnMouseDown(UiMouseButton.Left, 5, 5);
        root.OnMouseMove(20, 20);
        root.OnMouseUp(UiMouseButton.Left, 200, 200);

        var drag = Assert.IsType<ItemDragPayload>(payload);
        Assert.Equal(0x50000A01u, drag.ObjId);
        Assert.Equal(200, releaseX);
        Assert.Equal(200, releaseY);
    }

    [Fact]
    public void PlainWidget_insideDraggableWindow_doesNotEmitClick()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var frame = new UiPanel { Left = 10, Top = 300, Width = 200, Height = 60, Draggable = true };
        var plain = new ClickRecorder(handlesClick: false) { Left = 5, Top = 5, Width = 120, Height = 14 };
        frame.AddChild(plain);
        root.AddChild(frame);

        root.OnMouseDown(UiMouseButton.Left, 20, 310);
        root.OnMouseUp(UiMouseButton.Left, 20, 310);
        Assert.False(plain.Clicked);
    }

    private sealed class CoordRecorder : UiElement
    {
        public (int x, int y)? Down, Move;
        public CoordRecorder() { CapturesPointerDrag = true; }
        public override bool OnEvent(in UiEvent e)
        {
            if (e.Type == UiEventType.MouseDown) { Down = (e.Data1, e.Data2); return true; }
            if (e.Type == UiEventType.MouseMove) { Move = (e.Data1, e.Data2); return true; }
            return false;
        }
    }

    [Fact]
    public void MouseDown_And_MouseMove_DeliverSameTargetLocalFrame_ForNestedChild()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var panel = new UiPanel { Left = 50, Top = 60, Width = 200, Height = 100 };
        var child = new CoordRecorder { Left = 8, Top = 8, Width = 150, Height = 80 };
        panel.AddChild(child);
        root.AddChild(panel);

        root.OnMouseDown(UiMouseButton.Left, 100, 100);
        Assert.Equal((42, 32), child.Down);

        // drag to (120,110) -> local (62,42); MUST share the MouseDown frame.
        root.OnMouseMove(120, 110);
        Assert.Equal((62, 42), child.Move);
    }

    [Fact]
    public void ApplyAnchor_None_IsNoOp()
    {
        var e = new UiPanel { Left = 50, Top = 60, Width = 100, Height = 40, Anchors = AnchorEdges.None };
        e.ApplyAnchor(800, 600);
        Assert.Equal(50f, e.Left);
        Assert.Equal(60f, e.Top);
        Assert.Equal(100f, e.Width);
        Assert.Equal(40f, e.Height);
    }

    [Fact]
    public void WantsMouse_TrueOverWidget_FalseOverEmptySpace()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var panel = new UiPanel { Left = 10, Top = 10, Width = 100, Height = 50 };
        root.AddChild(panel);

        root.OnMouseMove(50, 30);    // inside the panel
        Assert.True(root.WantsMouse);

        root.OnMouseMove(500, 400);  // empty space
        Assert.False(root.WantsMouse);
    }

    [Fact]
    public void WindowDrag_RepositionsDraggablePanel_StopsOnRelease()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var panel = new UiPanel { Left = 10, Top = 10, Width = 100, Height = 50, Draggable = true };
        root.AddChild(panel);

        root.OnMouseDown(UiMouseButton.Left, 20, 20); // grab at (10,10) into the panel
        root.OnMouseMove(120, 90);                     // drag
        Assert.Equal(110f, panel.Left);                // 120 - 10
        Assert.Equal(80f, panel.Top);                  // 90 - 10

        root.OnMouseUp(UiMouseButton.Left, 120, 90);
        root.OnMouseMove(300, 300);                    // released — must not move
        Assert.Equal(110f, panel.Left);
        Assert.Equal(80f, panel.Top);
    }

    [Fact]
    public void DragHandle_MovesNonDraggableWindow_AndStopsOnRelease()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = new UiPanel { Left = 100, Top = 500, Width = 610, Height = 90 };
        var handle = new UiPanel
        {
            Left = 5, Top = 0, Width = 600, Height = 5,
            WindowMoveHandle = true,
        };
        window.AddChild(handle);
        root.AddChild(window);

        root.OnMouseDown(UiMouseButton.Left, 110, 502);  // press inside the top strip
        Assert.True(root.IsWindowMoveActive);
        root.OnMouseMove(160, 452);
        Assert.Equal(150f, window.Left);
        Assert.Equal(450f, window.Top);

        root.OnMouseUp(UiMouseButton.Left, 160, 452);
        root.OnMouseMove(300, 300);                      // released — must not move
        Assert.Equal(150f, window.Left);
        Assert.Equal(450f, window.Top);
    }

    [Fact]
    public void DragHandle_MovedAnchoredWindow_SurvivesTheNextLayoutPass()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = new UiPanel
        {
            Left = 0, Top = 510, Width = 610, Height = 90,
            Anchors = AnchorEdges.Left | AnchorEdges.Bottom,
        };
        var handle = new UiPanel
        {
            Left = 5, Top = 0, Width = 600, Height = 5,
            WindowMoveHandle = true,
        };
        window.AddChild(handle);
        root.AddChild(window);
        window.ApplyAnchor(root.Width, root.Height);

        root.OnMouseDown(UiMouseButton.Left, 10, 512); // press inside the top strip
        root.OnMouseMove(210, 312);
        window.ApplyAnchor(root.Width, root.Height);   // the next frame's layout pass

        Assert.Equal(200f, window.Left);
        Assert.Equal(310f, window.Top);
    }

    [Fact]
    public void DragHandle_Hover_ShowsMoveCursor_WindowBodyDoesNot()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = new UiPanel { Left = 100, Top = 500, Width = 610, Height = 90 };
        var handle = new UiPanel
        {
            Left = 5, Top = 0, Width = 600, Height = 5,
            WindowMoveHandle = true,
        };
        window.AddChild(handle);
        root.AddChild(window);

        root.OnMouseMove(110, 502);            // over the authored strip
        Assert.True(root.HoverWindowMove);

        root.OnMouseMove(110, 550);            // over the body of the non-Draggable window
        Assert.False(root.HoverWindowMove);
    }

    [Fact]
    public void DragHandle_UiLocked_NeitherMovesNorShowsCursor()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = new UiPanel { Left = 100, Top = 500, Width = 610, Height = 90 };
        var handle = new UiPanel
        {
            Left = 5, Top = 0, Width = 600, Height = 5,
            WindowMoveHandle = true,
        };
        window.AddChild(handle);
        root.AddChild(window);
        root.UiLocked = true;

        root.OnMouseMove(110, 502);
        Assert.False(root.HoverWindowMove);

        root.OnMouseDown(UiMouseButton.Left, 110, 502);
        root.OnMouseMove(160, 452);
        Assert.Equal(100f, window.Left);
        Assert.Equal(500f, window.Top);
    }

    [Fact]
    public void WindowDrag_ConstrainedPanel_StaysFullyInsideParent()
    {
        var root = new UiRoot { Width = 200, Height = 150 };
        var panel = new UiPanel
        {
            Left = 10,
            Top = 10,
            Width = 120,
            Height = 100,
            Draggable = true,
            ConstrainDragToParent = true,
        };
        root.AddChild(panel);
        root.RegisterWindow("radar", panel);
        (string name, UiElement window)? moved = null;
        root.WindowMoved += (name, window) => moved = (name, window);

        root.OnMouseDown(UiMouseButton.Left, 20, 20);
        root.OnMouseMove(500, 500);
        Assert.Equal(80f, panel.Left);
        Assert.Equal(50f, panel.Top);

        root.OnMouseMove(-500, -500);
        Assert.Equal(0f, panel.Left);
        Assert.Equal(0f, panel.Top);
        root.OnMouseUp(UiMouseButton.Left, -500, -500);
        Assert.Equal("radar", moved?.name);
        Assert.Same(panel, moved?.window);
    }

    [Fact]
    public void UiLocked_BlocksWindowMoveWithoutDisablingWindow()
    {
        var root = new UiRoot { Width = 200, Height = 150, UiLocked = true };
        var panel = new UiPanel
        {
            Left = 10,
            Top = 10,
            Width = 100,
            Height = 60,
            Draggable = true,
        };
        root.AddChild(panel);

        root.OnMouseDown(UiMouseButton.Left, 20, 20);
        root.OnMouseMove(100, 100);

        Assert.Equal(10f, panel.Left);
        Assert.Equal(10f, panel.Top);
        Assert.True(panel.Enabled);
    }

    [Fact]
    public void NonDraggablePanel_DoesNotMoveOnDrag()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var panel = new UiPanel { Left = 10, Top = 10, Width = 100, Height = 50 }; // Draggable defaults false
        root.AddChild(panel);

        root.OnMouseDown(UiMouseButton.Left, 20, 20);
        root.OnMouseMove(120, 90);
        Assert.Equal(10f, panel.Left);
        Assert.Equal(10f, panel.Top);
    }

    [Fact]
    public void CapturesPointerDragChild_DoesNotMoveDraggableAncestor_OnInteriorDrag()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = new UiPanel { Left = 10, Top = 10, Width = 200, Height = 100, Draggable = true };
        var child = new UiPanel { Left = 20, Top = 20, Width = 120, Height = 60, CapturesPointerDrag = true };
        window.AddChild(child);
        root.AddChild(window);

        root.OnMouseDown(UiMouseButton.Left, 60, 60);
        root.OnMouseMove(160, 160);

        Assert.Equal(10f, window.Left);
        Assert.Equal(10f, window.Top);
        Assert.Same(child, root.Captured);

        root.OnMouseUp(UiMouseButton.Left, 160, 160);
        Assert.Equal(10f, window.Left);
        Assert.Equal(10f, window.Top);
    }

    [Fact]
    public void CapturesPointerDragChild_StillAllowsEdgeResizeOfResizableWindow()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = new UiPanel { Left = 100, Top = 100, Width = 200, Height = 100,
            Draggable = true, Resizable = true, MinWidth = 40, MinHeight = 40 };
        var child = new UiPanel { Left = 0, Top = 0, Width = 200, Height = 100,
            CapturesPointerDrag = true,
            Anchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Right | AnchorEdges.Bottom };
        window.AddChild(child);
        root.AddChild(window);

        // Grab within ResizeGrip(5) of the right edge (x=298 of right edge x=300) → resize.
        root.OnMouseDown(UiMouseButton.Left, 298, 150);
        root.OnMouseMove(338, 150);
        Assert.Equal(240f, window.Width);
        Assert.Equal(100f, window.Left);
        root.OnMouseUp(UiMouseButton.Left, 338, 150);
    }

    [Fact]
    public void BottomResize_ConstrainedToParent_ReachesCurrentScreenEdge()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = new UiPanel
        {
            Left = 100,
            Top = 80,
            Width = 300,
            Height = 200,
            Draggable = true,
            Resizable = true,
            ResizeX = false,
            ResizeY = true,
            ResizableEdges = ResizeEdges.Bottom,
            ConstrainResizeToParent = true,
            MinHeight = 100,
        };
        root.AddChild(window);

        root.OnMouseDown(UiMouseButton.Left, 250, 279);
        root.OnMouseMove(250, 2_000);

        Assert.Equal(520f, window.Height);
        Assert.Equal(root.Height, window.Top + window.Height);
        root.OnMouseUp(UiMouseButton.Left, 250, 2_000);
    }

    [Fact]
    public void TopWithoutTopResizeEdge_IsWindowMoveAffordance()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = new UiPanel
        {
            Left = 100,
            Top = 80,
            Width = 300,
            Height = 200,
            Draggable = true,
            Resizable = true,
            ResizableEdges = ResizeEdges.Left | ResizeEdges.Right | ResizeEdges.Bottom,
        };
        root.AddChild(window);

        root.OnMouseMove(250, 80);

        Assert.Equal(ResizeEdges.None, root.HoverResizeEdges);
        Assert.True(root.HoverWindowMove);
    }

    [Fact]
    public void ResizeRect_RightBottom_GrowsSizeOnly()
    {
        var (x, y, w, h) = UiRoot.ResizeRect(10, 20, 100, 50,
            ResizeEdges.Right | ResizeEdges.Bottom, dx: 30, dy: 15, minW: 40, minH: 40, maxW: float.MaxValue, maxH: float.MaxValue);
        Assert.Equal(10f, x); Assert.Equal(20f, y);
        Assert.Equal(130f, w); Assert.Equal(65f, h);
    }

    [Fact]
    public void ResizeRect_LeftTop_MovesOriginAndClampsToMin()
    {
        var (x, _, w, _) = UiRoot.ResizeRect(10, 20, 100, 50,
            ResizeEdges.Left, dx: 80, dy: 0, minW: 40, minH: 40, maxW: float.MaxValue, maxH: float.MaxValue);
        Assert.Equal(40f, w);
        Assert.Equal(70f, x);
    }

    [Fact]
    public void ResizeRect_Bottom_ClampsToMaxH()
    {
        var (_, y, _, h) = UiRoot.ResizeRect(10, 20, 100, 50,
            ResizeEdges.Bottom, dx: 0, dy: 1000, minW: 40, minH: 40, maxW: float.MaxValue, maxH: 128f);
        Assert.Equal(128f, h);
        Assert.Equal(20f, y);
    }

    [Fact]
    public void ResizeRect_Top_ClampsToMaxH()
    {
        var (_, y, _, h) = UiRoot.ResizeRect(10, 20, 100, 50,
            ResizeEdges.Top, dx: 0, dy: -1000, minW: 40, minH: 40, maxW: float.MaxValue, maxH: 128f);
        Assert.Equal(128f, h);
        Assert.Equal(20f + 50f - 128f, y);
    }

    [Fact]
    public void HitEdges_DetectsCornerAndInteriorNone()
    {
        var panel = new UiPanel { Left = 100, Top = 100, Width = 200, Height = 100 };
        Assert.Equal(ResizeEdges.Right | ResizeEdges.Bottom, UiRoot.HitEdges(panel, 300, 200, 5));
        // deep interior → no edges
        Assert.Equal(ResizeEdges.None, UiRoot.HitEdges(panel, 200, 150, 5));
    }

    [Fact]
    public void EdgeDrag_ResizesPanel_InteriorDragMoves()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var panel = new UiPanel { Left = 100, Top = 100, Width = 200, Height = 100,
            Draggable = true, Resizable = true, MinWidth = 40, MinHeight = 40 };
        root.AddChild(panel);

        // grab just inside the right edge (x=298, within ResizeGrip=5 of x=300) and drag right → wider, same origin
        root.OnMouseDown(UiMouseButton.Left, 298, 150);
        root.OnMouseMove(338, 150);
        Assert.Equal(240f, panel.Width);
        Assert.Equal(100f, panel.Left);
        root.OnMouseUp(UiMouseButton.Left, 338, 150);

        // grab the interior and drag → moves
        root.OnMouseDown(UiMouseButton.Left, 200, 150);
        root.OnMouseMove(220, 170);
        Assert.Equal(120f, panel.Left);
        Assert.Equal(120f, panel.Top);
        root.OnMouseUp(UiMouseButton.Left, 220, 170);
    }

    [Fact]
    public void HitEdges_RespectsResizeAxisLock()
    {
        var panel = new UiPanel { Left = 100, Top = 100, Width = 200, Height = 100, ResizeY = false };
        // right edge still detected (X allowed)
        Assert.True((UiRoot.HitEdges(panel, 300, 150, 5) & ResizeEdges.Right) != 0);
        // bottom edge masked out (Y locked)
        Assert.True((UiRoot.HitEdges(panel, 200, 200, 5) & ResizeEdges.Bottom) == 0);
    }


    private static UiPanel WindowWithCornerGrips(
        float left = 100, float top = 100, float width = 200, float height = 100)
    {
        var window = new UiPanel
        {
            Left = left, Top = top, Width = width, Height = height,
            Draggable = true, Resizable = true,
            MinWidth = 40, MinHeight = 40, MaxWidth = 2000, MaxHeight = 2000,
        };
        window.AddChild(new UiResizeGrip { BorderLocation = UiResizeGrip.Border.UpperLeft, Left = 0, Top = 0, Width = 5, Height = 5 });
        window.AddChild(new UiResizeGrip { BorderLocation = UiResizeGrip.Border.UpperRight, Left = width - 5, Top = 0, Width = 5, Height = 5 });
        window.AddChild(new UiResizeGrip { BorderLocation = UiResizeGrip.Border.LowerLeft, Left = 0, Top = height - 5, Width = 5, Height = 5 });
        window.AddChild(new UiResizeGrip { BorderLocation = UiResizeGrip.Border.LowerRight, Left = width - 5, Top = height - 5, Width = 5, Height = 5 });
        return window;
    }

    [Theory]
    [InlineData(UiResizeGrip.Border.UpperLeft)]
    [InlineData(UiResizeGrip.Border.UpperRight)]
    [InlineData(UiResizeGrip.Border.LowerLeft)]
    [InlineData(UiResizeGrip.Border.LowerRight)]
    public void CornerGrip_GrowsBothAxes_WhenDraggedOutward(UiResizeGrip.Border corner)
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = WindowWithCornerGrips();
        root.AddChild(window);
        var grip = window.Children.OfType<UiResizeGrip>().Single(g => g.BorderLocation == corner);

        var gs = grip.ScreenPosition;
        int pressX = (int)(gs.X + 2), pressY = (int)(gs.Y + 2);
        bool growsLeft = corner is UiResizeGrip.Border.UpperLeft or UiResizeGrip.Border.LowerLeft;
        bool growsUp = corner is UiResizeGrip.Border.UpperLeft or UiResizeGrip.Border.UpperRight;
        int dx = growsLeft ? -30 : 30;
        int dy = growsUp ? -30 : 30;

        root.OnMouseDown(UiMouseButton.Left, pressX, pressY);
        Assert.True(root.ActiveResizeEdges != ResizeEdges.None);
        root.OnMouseMove(pressX + dx, pressY + dy);
        root.OnMouseUp(UiMouseButton.Left, pressX + dx, pressY + dy);

        Assert.Equal(230f, window.Width);
        Assert.Equal(130f, window.Height);
        Assert.Equal(growsLeft ? 70f : 100f, window.Left);
        Assert.Equal(growsUp ? 70f : 100f, window.Top);
    }

    [Theory]
    [InlineData(UiResizeGrip.Border.UpperLeft)]
    [InlineData(UiResizeGrip.Border.UpperRight)]
    [InlineData(UiResizeGrip.Border.LowerLeft)]
    [InlineData(UiResizeGrip.Border.LowerRight)]
    public void CornerGrip_ShrinksBothAxes_WhenDraggedInward(UiResizeGrip.Border corner)
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = WindowWithCornerGrips();
        root.AddChild(window);
        var grip = window.Children.OfType<UiResizeGrip>().Single(g => g.BorderLocation == corner);

        var gs = grip.ScreenPosition;
        int pressX = (int)(gs.X + 2), pressY = (int)(gs.Y + 2);
        bool growsLeft = corner is UiResizeGrip.Border.UpperLeft or UiResizeGrip.Border.LowerLeft;
        bool growsUp = corner is UiResizeGrip.Border.UpperLeft or UiResizeGrip.Border.UpperRight;
        // Shrink: drag the opposite direction from "grow".
        int dx = growsLeft ? 30 : -30;
        int dy = growsUp ? 30 : -30;

        root.OnMouseDown(UiMouseButton.Left, pressX, pressY);
        root.OnMouseMove(pressX + dx, pressY + dy);
        root.OnMouseUp(UiMouseButton.Left, pressX + dx, pressY + dy);

        Assert.Equal(170f, window.Width);
        Assert.Equal(70f, window.Height);
        Assert.Equal(growsLeft ? 130f : 100f, window.Left);
        Assert.Equal(growsUp ? 130f : 100f, window.Top);
    }

    [Fact]
    public void CornerGrip_TakesPriorityOverBlanketResizableEdgesMask()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = WindowWithCornerGrips();
        window.ResizableEdges = ResizeEdges.Left | ResizeEdges.Right | ResizeEdges.Top | ResizeEdges.Bottom;
        root.AddChild(window);
        var grip = window.Children.OfType<UiResizeGrip>().Single(g => g.BorderLocation == UiResizeGrip.Border.UpperLeft);

        var gs = grip.ScreenPosition;
        int pressX = (int)(gs.X + 2), pressY = (int)(gs.Y + 2);
        root.OnMouseDown(UiMouseButton.Left, pressX, pressY);

        Assert.Equal(ResizeEdges.Left | ResizeEdges.Top, root.ActiveResizeEdges);
        root.OnMouseUp(UiMouseButton.Left, pressX, pressY);
    }

    [Fact]
    public void GripEdges_AreMaskedByResizableEdges_WhenTheWindowExcludesAnAxis()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = WindowWithCornerGrips();
        window.ResizableEdges = ResizeEdges.Left | ResizeEdges.Bottom;   // no Right, no Top
        root.AddChild(window);
        var grip = window.Children.OfType<UiResizeGrip>().Single(g => g.BorderLocation == UiResizeGrip.Border.UpperRight);

        var gs = grip.ScreenPosition;
        int pressX = (int)(gs.X + 2), pressY = (int)(gs.Y + 2);
        root.OnMouseDown(UiMouseButton.Left, pressX, pressY);

        // Border.UpperRight decodes to Right|Top; masked by Left|Bottom leaves nothing.
        Assert.Equal(ResizeEdges.None, root.ActiveResizeEdges);
        root.OnMouseUp(UiMouseButton.Left, pressX, pressY);
    }

    [Fact]
    public void MoveHandle_TakesPriorityOverOverlappingAmbientResizeProximity()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = new UiPanel
        {
            Left = 100, Top = 100, Width = 200, Height = 100,
            Draggable = true, Resizable = true,
            ResizableEdges = ResizeEdges.Left | ResizeEdges.Right | ResizeEdges.Top | ResizeEdges.Bottom,
        };
        var handle = new UiPanel { Left = 5, Top = 0, Width = 190, Height = 5, WindowMoveHandle = true };
        window.AddChild(handle);
        root.AddChild(window);

        // Press well inside the handle strip but still within ResizeGrip(5) of
        // the window's own top edge (y=100..105).
        root.OnMouseDown(UiMouseButton.Left, 150, 102);

        Assert.Equal(ResizeEdges.None, root.ActiveResizeEdges);
        Assert.True(root.IsWindowMoveActive);
        root.OnMouseMove(200, 152);
        Assert.Equal(150f, window.Left);
        Assert.Equal(150f, window.Top);
        root.OnMouseUp(UiMouseButton.Left, 200, 152);
    }

    [Fact]
    public void HoverResizeEdges_OverAGrip_ReturnsTheGripsOwnEdges()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = WindowWithCornerGrips();
        root.AddChild(window);
        var grip = window.Children.OfType<UiResizeGrip>().Single(g => g.BorderLocation == UiResizeGrip.Border.LowerRight);

        var gs = grip.ScreenPosition;
        root.OnMouseMove((int)(gs.X + 2), (int)(gs.Y + 2));

        Assert.Equal(ResizeEdges.Right | ResizeEdges.Bottom, root.HoverResizeEdges);
    }

    [Fact]
    public void HoverResizeEdges_OverAMoveHandle_IsNoneEvenWithinProximityOfTheTopEdge()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = new UiPanel
        {
            Left = 100, Top = 100, Width = 200, Height = 100,
            Draggable = true, Resizable = true,
            ResizableEdges = ResizeEdges.Left | ResizeEdges.Right | ResizeEdges.Top | ResizeEdges.Bottom,
        };
        var handle = new UiPanel { Left = 5, Top = 0, Width = 190, Height = 5, WindowMoveHandle = true };
        window.AddChild(handle);
        root.AddChild(window);

        root.OnMouseMove(150, 102);

        Assert.Equal(ResizeEdges.None, root.HoverResizeEdges);
        Assert.True(root.HoverWindowMove);
    }

    [Fact]
    public void ComputeAnchoredRect_LeftRight_StretchesWidth()
    {
        // bar at x=8,w=200 in a 220-wide parent (right margin 12). Parent grows to 300.
        var (x, _, w, _) = UiElement.ComputeAnchoredRect(
            AnchorEdges.Left | AnchorEdges.Right | AnchorEdges.Top,
            mL: 8, mT: 24, mR: 12, mB: 58, w0: 200, h0: 14, parentW: 300, parentH: 96);
        Assert.Equal(8f, x);
        Assert.Equal(280f, w);   // 300 - 12 - 8
    }

    [Fact]
    public void ComputeAnchoredRect_LeftTopOnly_KeepsFixedSizeAndOrigin()
    {
        var (x, y, w, h) = UiElement.ComputeAnchoredRect(
            AnchorEdges.Left | AnchorEdges.Top,
            mL: 8, mT: 24, mR: 12, mB: 58, w0: 200, h0: 14, parentW: 300, parentH: 96);
        Assert.Equal(8f, x); Assert.Equal(24f, y);
        Assert.Equal(200f, w); Assert.Equal(14f, h);
    }

    [Fact]
    public void ToggleWindow_FlipsVisible_AndReturnsNewState()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var win = new UiPanel { Width = 100, Height = 100, Visible = false };
        root.AddChild(win);
        root.RegisterWindow("inventory", win);

        Assert.True(root.ToggleWindow("inventory"));   // hidden -> shown
        Assert.True(win.Visible);
        Assert.False(root.ToggleWindow("inventory"));  // shown -> hidden
        Assert.False(win.Visible);
    }

    [Fact]
    public void ShowHideWindow_SetVisibility_UnknownNameIsNoOp()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var win = new UiPanel { Width = 100, Height = 100, Visible = false };
        root.AddChild(win);
        root.RegisterWindow("inventory", win);

        Assert.False(root.IsWindowVisible("inventory"));
        Assert.True(root.ShowWindow("inventory"));
        Assert.True(win.Visible);
        Assert.True(root.IsWindowVisible("inventory"));
        Assert.True(root.HideWindow("inventory"));
        Assert.False(win.Visible);
        Assert.False(root.IsWindowVisible("inventory"));

        Assert.False(root.ShowWindow("nope"));
        Assert.False(root.HideWindow("nope"));
        Assert.False(root.ToggleWindow("nope"));
        Assert.False(root.IsWindowVisible("nope"));
    }

    [Fact]
    public void ShowWindow_RaisesAbovePeers()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var a = new UiPanel { Width = 100, Height = 100 };
        var b = new UiPanel { Width = 100, Height = 100 };
        var win = new UiPanel { Width = 100, Height = 100, Visible = false };
        root.AddChild(a); root.AddChild(b); root.AddChild(win);
        root.RegisterWindow("inventory", win);

        root.ShowWindow("inventory");
        Assert.True(win.ZOrder > a.ZOrder);
        Assert.True(win.ZOrder > b.ZOrder);
    }

    [Fact]
    public void BringToFront_SetsStrictlyGreatestZOrder()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var a = new UiPanel { Width = 100, Height = 100, ZOrder = 5 };
        var b = new UiPanel { Width = 100, Height = 100, ZOrder = 9 };
        root.AddChild(a); root.AddChild(b);

        root.BringToFront(a);
        Assert.True(a.ZOrder > b.ZOrder);   // 10 > 9
    }

    [Fact]
    public void MouseDown_OnWindow_RaisesItAbovePeers()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var peer    = new UiPanel { Left = 300, Top = 0, Width = 100, Height = 100, ZOrder = 9 };
        var clicked = new UiPanel { Left = 0,   Top = 0, Width = 100, Height = 100, ZOrder = 0, Draggable = true };
        root.AddChild(peer); root.AddChild(clicked);

        root.OnMouseDown(UiMouseButton.Left, 50, 50);
        Assert.True(clicked.ZOrder > peer.ZOrder);
        root.OnMouseUp(UiMouseButton.Left, 50, 50);
    }

    [Fact]
    public void AddChild_AssignsEventIdsRecursively()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var parent = new UiPanel { Width = 100, Height = 100 };
        var child = new UiPanel { Width = 50, Height = 50 };
        var grandchild = new UiPanel { Width = 25, Height = 25 };
        child.AddChild(grandchild);
        parent.AddChild(child);

        root.AddChild(parent);

        Assert.NotEqual(0u, parent.EventId);
        Assert.NotEqual(0u, child.EventId);
        Assert.NotEqual(0u, grandchild.EventId);
        Assert.Equal(3, new[] { parent.EventId, child.EventId, grandchild.EventId }.Distinct().Count());
    }

    [Fact]
    public void RemovingSubtree_ClearsAllRootOwnership()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = new UiPanel { Width = 200, Height = 100 };
        var field = new UiField { Width = 100, Height = 20 };
        window.AddChild(field);
        root.AddChild(window);
        root.RegisterWindow("test", window);
        root.DefaultTextInput = field;
        root.Modal = window;
        root.SetKeyboardFocus(field);
        root.SetCapture(field);

        Assert.True(root.RemoveChild(window));

        Assert.Null(root.KeyboardFocus);
        Assert.Null(root.Captured);
        Assert.Null(root.DefaultTextInput);
        Assert.Null(root.Modal);
        Assert.False(root.ShowWindow("test"));
    }

    [Fact]
    public void Tick_ChecksTooltipBeforeGlobalTimeMessage_AtRetailDelay()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var recorder = new TimeOrderRecorder { Width = 100, Height = 100 };
        root.AddChild(recorder);

        root.Tick(0, 1_000);
        root.OnMouseMove(10, 10);
        recorder.Events.Clear();
        root.Tick(0, 1_249);
        Assert.DoesNotContain("tooltip", recorder.Events);
        recorder.Events.Clear();

        root.Tick(0, 1_250);

        Assert.Equal(new[] { "tooltip", "global" }, recorder.Events);
    }

    [Fact]
    public void Tick_GlobalTimeRemoval_SkipsDetachedSiblingWithoutInvalidatingWalk()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var victim = new TimeOrderRecorder { Width = 100, Height = 100 };
        var remover = new GlobalTimeAction(() => root.RemoveChild(victim))
            { Width = 100, Height = 100 };
        root.AddChild(remover);
        root.AddChild(victim);

        root.Tick(0, 1_000);

        Assert.Empty(victim.Events);
        Assert.Null(victim.Parent);
    }

    private sealed class TimeOrderRecorder : UiElement, IUiGlobalTimeListener
    {
        public List<string> Events { get; } = new();

        public override bool OnEvent(in UiEvent e)
        {
            if (e.Type != UiEventType.Tooltip) return false;
            Events.Add("tooltip");
            return true;
        }

        public void OnGlobalUiTime(double nowSeconds) => Events.Add("global");
    }

    private sealed class GlobalTimeAction(Action action) : UiElement, IUiGlobalTimeListener
    {
        public void OnGlobalUiTime(double nowSeconds) => action();
    }
}
