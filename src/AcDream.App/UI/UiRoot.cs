using System;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.App.UI;

[System.Flags]
public enum ResizeEdges { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

public sealed class UiRoot : UiElement
{
    public UiRoot()
    {
        WindowManager = new RetailWindowManager(this);
    }

    /// <summary>Single owner for named retained-window lifecycle and raise policy.</summary>
    public RetailWindowManager WindowManager { get; }

    protected override bool ClipsChildren => false;

    public Vector2? FixedCanvasSize { get; set; }

    private readonly Dictionary<object, Vector2> _fixedCanvasDeclarations = new();

    public void DeclareFixedCanvas(object owner, Vector2 size)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (_fixedCanvasDeclarations.TryGetValue(owner, out Vector2 existing))
        {
            if (existing == size)
                return; // idempotent re-declare (e.g. a re-ticked activation edge)
            throw new InvalidOperationException(
                $"UiRoot.DeclareFixedCanvas: owner {owner} re-declared a different " +
                $"canvas ({existing} -> {size}) without revoking first.");
        }

        foreach (Vector2 declared in _fixedCanvasDeclarations.Values)
        {
            if (declared != size)
            {
                throw new InvalidOperationException(
                    $"UiRoot.DeclareFixedCanvas: owner {owner} declared {size} but " +
                    $"another active owner already declared {declared} — every " +
                    "concurrently-active fixed-canvas screen must author the SAME " +
                    "canvas size.");
            }
        }

        _fixedCanvasDeclarations[owner] = size;
        FixedCanvasSize = size;
    }

    public void RevokeFixedCanvas(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!_fixedCanvasDeclarations.Remove(owner))
            return;

        if (_fixedCanvasDeclarations.Count == 0)
        {
            FixedCanvasSize = null;
            return;
        }

        foreach (Vector2 declared in _fixedCanvasDeclarations.Values)
        {
            FixedCanvasSize = declared;
            break;
        }
    }

    public Vector2 EffectiveCanvasSize =>
        FixedCanvasSize is { X: > 0f, Y: > 0f } canvas
            ? canvas
            : new Vector2(Width, Height);

    public Vector2 CanvasScale =>
        FixedCanvasSize is { X: > 0f, Y: > 0f } canvas && Width > 0f && Height > 0f
            ? new Vector2(Width / canvas.X, Height / canvas.Y)
            : Vector2.One;

    private (int x, int y) MapWindowToCanvas(int x, int y)
    {
        Vector2 scale = CanvasScale;
        return scale == Vector2.One
            ? (x, y)
            : ((int)(x / scale.X), (int)(y / scale.Y));
    }

    // ── Device-level state ───────────────────────────────────────────────
    public int MouseX { get; private set; }
    public int MouseY { get; private set; }
    public bool LeftButtonDown   { get; private set; }
    public bool RightButtonDown  { get; private set; }
    public bool MiddleButtonDown { get; private set; }

    public UiElement? KeyboardFocus { get; private set; }

    private int? _suppressedPhysicalKey;

    public UiElement? DefaultTextInput { get; set; }

    public UiPanel? Modal { get; set; }

    public UiElement? Captured { get; private set; }

    public bool WantsMouse =>
        Captured is not null
        || PopupHit(MouseX, MouseY) is not null
        || HitTestTopDown(MouseX, MouseY).element is not null;

    public bool WantsKeyboard => KeyboardFocus is not null;

    private bool _uiLocked;
    public bool UiLocked
    {
        get => _uiLocked;
        set
        {
            if (_uiLocked == value) return;
            _uiLocked = value;
            UiLockChanged?.Invoke(value);
        }
    }

    public UiElement? DragSource  { get; private set; }
    public object?    DragPayload { get; private set; }
    public bool IsWindowMoveActive => _windowDragTarget is not null;
    public ResizeEdges ActiveResizeEdges => _resizeTarget is not null ? _resizeEdges : ResizeEdges.None;
    public ResizeEdges HoverResizeEdges
    {
        get
        {
            var target = Pick(MouseX, MouseY);
            var window = FindWindow(target);
            if (UiLocked || window is not { Resizable: true })
                return ResizeEdges.None;

            var gripEdges = EffectiveGripEdges(target, window);
            if (gripEdges != ResizeEdges.None)
                return gripEdges;

            if (FindDragHandleWindow(target) is not null)
                return ResizeEdges.None;

            return HitEdges(window, MouseX, MouseY, ResizeGrip);
        }
    }

    private static ResizeEdges EffectiveGripEdges(UiElement? target, UiElement? window)
    {
        if (window is null || target is not UiResizeGrip grip)
            return ResizeEdges.None;

        ResizeEdges edges = grip.Edges;
        if (!window.ResizeX) edges &= ~(ResizeEdges.Left | ResizeEdges.Right);
        if (!window.ResizeY) edges &= ~(ResizeEdges.Top | ResizeEdges.Bottom);
        edges &= window.ResizableEdges;
        return edges;
    }

    public bool HoverWindowMove
    {
        get
        {
            var target = Pick(MouseX, MouseY);
            if (UiLocked || target is null)
                return false;
            if (HoverResizeEdges != ResizeEdges.None)
                return false;
            if (FindDragHandleWindow(target) is not null)
                return true;
            var window = FindWindow(target);
            if (window is not { Draggable: true })
                return false;
            return ReferenceEquals(target, window)
                && WithinBorderBand(window, MouseX, MouseY, MoveBorderBand);
        }
    }

    private static bool WithinBorderBand(UiElement w, int x, int y, int band)
    {
        float l = w.Left, t = w.Top, r = w.Left + w.Width, b = w.Top + w.Height;
        if (x < l || x >= r || y < t || y >= b)
            return false;
        return x - l < band || r - x <= band || y - t < band || b - y <= band;
    }

    private const int MoveBorderBand = 8;
    private (uint tex, int w, int h)? _dragGhost;
    /// <summary>Snapshotted drag-ghost (tex,w,h), exposed for tests. See BeginDrag.</summary>
    internal (uint tex, int w, int h)? DragGhostForTest => _dragGhost;
    private UiElement? _lastDragHoverTarget;
    private int _pressX, _pressY;
    private bool _dragCandidate;
    private UiElement? _windowDragTarget;
    private int _windowDragOffX, _windowDragOffY;
    private UiElement? _lastClickTarget;
    private long _lastClickMs;
    private int _lastClickX, _lastClickY;
    private UiElement? _resizeTarget;
    private ResizeEdges _resizeEdges;
    private float _resizeStartX, _resizeStartY, _resizeStartW, _resizeStartH;
    private int _resizeMouseX, _resizeMouseY;
    private const int ResizeGrip = 5;   // px proximity to an edge to start a resize
    private const int DragDistanceThreshold = 3;
    private const int DoubleClickDelayMs = 500;

    // Hover / tooltip tracking.
    private UiElement? _hoverWidget;
    private long _hoverStartedMs;
    public int TooltipDelayMs { get; set; } = 250;
    public int TooltipDurationMs { get; set; } = 10_000;
    private bool _tooltipFired;
    private long _tooltipShownMs;

    public event Action<UiElement>? TooltipShow;

    public event Action<UiElement>? TooltipHide;

    private int EffectiveTooltipDelayMs(UiElement widget)
        => widget.AuthoredTooltipDelaySeconds is { } seconds
            ? (int)(seconds * 1000f)
            : TooltipDelayMs;

    private long _nowMs;

    private long _lastMouseMoveMs;

    public long MouseIdleMs => _nowMs - _lastMouseMoveMs;

    public long NowMs => _nowMs;

    public event Action<UiMouseButton, int, int, uint>? WorldMouseFallThrough;

    public event Action<int /*vk*/, uint /*lparam*/>? WorldKeyFallThrough;

    public event Action<int, int>? WorldMouseMoveFallThrough;

    /// <summary>Raised on scroll fall-through (world zoom, etc.).</summary>
    public event Action<int /*dy*/>? WorldScrollFallThrough;

    /// <summary>Raised when a drag is released over no UI element.</summary>
    public event Action<object /*payload*/, int /*x*/, int /*y*/>? DragReleasedOutsideUi;

    /// <summary>Raised after a registered top-level window finishes moving.</summary>
    public event Action<string, UiElement>? WindowMoved;

    /// <summary>Raised after a registered top-level window finishes resizing.</summary>
    public event Action<string, UiElement>? WindowResized;

    public event Action<UiElement, bool>? ElementVisibilityChanged;

    public event Action<UiElement?, UiElement?>? KeyboardFocusChanged;

    public event Action<UiElement?, UiElement?>? PointerCaptureChanged;

    public event Action<bool>? UiLockChanged;

    private uint _nextEventId = 0x10000001u;

    public override void AddChild(UiElement child)
    {
        AssignEventIds(child);
        base.AddChild(child);
    }

    private void AssignEventIds(UiElement element)
    {
        if (element.EventId == 0)
            element.EventId = _nextEventId++;
        foreach (var child in element.Children)
            AssignEventIds(child);
    }

    private static void BroadcastGlobalUiTime(UiElement element, double nowSeconds)
    {
        if (element is IUiGlobalTimeListener listener)
            listener.OnGlobalUiTime(nowSeconds);

        foreach (var child in element.ChildrenBackToFrontSnapshot())
            if (ReferenceEquals(child.Parent, element))
                BroadcastGlobalUiTime(child, nowSeconds);
    }

    internal void OnSubtreeRemoving(UiElement subtree)
    {
        bool replacingActiveDragSource = ReferenceEquals(subtree, DragSource);
        ClearSubtreeOwnership(subtree, preserveDetachedDrag: replacingActiveDragSource);
        WindowManager.OnSubtreeRemoving(subtree);
    }

    internal void OnElementVisibilityChanging(UiElement element, bool visible)
    {
        if (visible) return;
        WindowManager.PrepareToHide(element);
        ClearSubtreeOwnership(element);
    }

    internal void OnElementVisibilityChanged(UiElement element, bool visible)
        => ElementVisibilityChanged?.Invoke(element, visible);

    internal void ClearSubtreeOwnership(UiElement subtree, bool preserveDetachedDrag = false)
    {
        if (IsWithinSubtree(KeyboardFocus, subtree))
            SetKeyboardFocus(null);
        if (IsWithinSubtree(Captured, subtree))
        {
            if (preserveDetachedDrag && ReferenceEquals(Captured, DragSource))
                SetCapture(this);
            else
                ReleaseCapture();
            _dragCandidate = false;
        }
        if (IsWithinSubtree(DefaultTextInput, subtree))
            DefaultTextInput = null;
        if (IsWithinSubtree(Modal, subtree))
            Modal = null;
        if (IsWithinSubtree(DragSource, subtree))
        {
            DragSource?.SetDragSourceActive(false, DragPayload);
            if (!preserveDetachedDrag)
            {
                DragSource = null;
                DragPayload = null;
                _dragGhost = null;
                _dragCandidate = false;
            }
        }
        if (IsWithinSubtree(_hoverWidget, subtree))
        {
            var leave = new UiEvent(_hoverWidget!.EventId, _hoverWidget, UiEventType.HoverLeave);
            _hoverWidget.OnEvent(in leave);
            if (_tooltipFired)
                TooltipHide?.Invoke(_hoverWidget);
            _hoverWidget = null;
            _tooltipFired = false;
        }
        if (IsWithinSubtree(_lastDragHoverTarget, subtree))
            _lastDragHoverTarget = null;
        if (IsWithinSubtree(_lastClickTarget, subtree))
            _lastClickTarget = null;
        if (IsWithinSubtree(_windowDragTarget, subtree))
        {
            _windowDragTarget = null;
            _dragCandidate = false;
        }
        if (IsWithinSubtree(_resizeTarget, subtree))
        {
            _resizeTarget = null;
            _dragCandidate = false;
        }
    }

    private static bool IsWithinSubtree(UiElement? element, UiElement subtree)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, subtree)) return true;
            element = element.Parent;
        }
        return false;
    }

    // ── Per-frame pumping ────────────────────────────────────────────────

    public void Tick(double dt, long nowMs)
    {
        _nowMs = nowMs;

        if (_hoverWidget is not null && !_tooltipFired && Captured is null
            && _nowMs - _hoverStartedMs >= EffectiveTooltipDelayMs(_hoverWidget))
        {
            var e = new UiEvent(_hoverWidget.EventId, _hoverWidget, UiEventType.Tooltip);
            _hoverWidget.OnEvent(in e);
            _tooltipFired = true;
            _tooltipShownMs = _nowMs;
            TooltipShow?.Invoke(_hoverWidget);
        }
        else if (_hoverWidget is not null && _tooltipFired
            && _nowMs - _tooltipShownMs >= TooltipDurationMs)
        {
            var leave = new UiEvent(_hoverWidget.EventId, _hoverWidget, UiEventType.HoverLeave);
            _hoverWidget.OnEvent(in leave);
            TooltipHide?.Invoke(_hoverWidget);
            _hoverWidget = null;
            _tooltipFired = false;
        }

        BroadcastGlobalUiTime(this, nowMs / 1000d);
        TickSelfAndChildren(dt);
    }

    public void Draw(UiRenderContext ctx)
    {
        ctx.TextRenderer.CanvasScale = CanvasScale;
        try
        {
            DrawCore(ctx);
        }
        finally
        {
            ctx.TextRenderer.CanvasScale = Vector2.One;
        }
    }

    private void DrawCore(UiRenderContext ctx)
    {
        DrawSelfAndChildren(ctx);
        ctx.BeginOverlayLayer();
        DrawOverlays(ctx);
        DrawDragGhost(ctx);
        ctx.EndOverlayLayer();
    }

    private const float GhostAlpha = 1.0f;

    private void DrawDragGhost(UiRenderContext ctx)
    {
        if (_dragGhost is not { } g || g.tex == 0) return;
        ctx.DrawSprite(g.tex, MouseX - g.w / 2f, MouseY - g.h / 2f, g.w, g.h,
                       0f, 0f, 1f, 1f, new Vector4(1f, 1f, 1f, GhostAlpha));
    }


    public void OnMouseMove(int x, int y)
    {
        (x, y) = MapWindowToCanvas(x, y);
        int dx = x - MouseX;
        int dy = y - MouseY;
        MouseX = x;
        MouseY = y;
        _lastMouseMoveMs = _nowMs;

        // Window resize takes precedence over move / drag-drop / hover.
        if (_resizeTarget is not null)
        {
            float maxWidth = _resizeTarget.MaxWidth;
            float maxHeight = _resizeTarget.MaxHeight;
            if (_resizeTarget.ConstrainResizeToParent
                && _resizeTarget.Parent is { } resizeParent)
            {
                maxWidth = MathF.Min(
                    maxWidth,
                    (_resizeEdges & ResizeEdges.Left) != 0
                        ? _resizeStartX + _resizeStartW
                        : resizeParent.Width - _resizeStartX);
                maxHeight = MathF.Min(
                    maxHeight,
                    (_resizeEdges & ResizeEdges.Top) != 0
                        ? _resizeStartY + _resizeStartH
                        : resizeParent.Height - _resizeStartY);
            }
            var (nx, ny, nw, nh) = ResizeRect(
                _resizeStartX, _resizeStartY, _resizeStartW, _resizeStartH,
                _resizeEdges, x - _resizeMouseX, y - _resizeMouseY,
                _resizeTarget.MinWidth, _resizeTarget.MinHeight,
                MathF.Max(_resizeTarget.MinWidth, maxWidth),
                MathF.Max(_resizeTarget.MinHeight, maxHeight));
            _resizeTarget.Left = nx; _resizeTarget.Top = ny;
            _resizeTarget.Width = nw; _resizeTarget.Height = nh;
            _resizeTarget.ResetAnchorCapture();
            return;
        }

        // Window-move drag takes precedence over drag-drop / hover / fall-through.
        if (_windowDragTarget is not null)
        {
            float left = x - _windowDragOffX;
            float top = y - _windowDragOffY;
            if (_windowDragTarget.ConstrainDragToParent
                && _windowDragTarget.Parent is { } parent)
            {
                left = Math.Clamp(left, 0f, Math.Max(0f, parent.Width - _windowDragTarget.Width));
                top = Math.Clamp(top, 0f, Math.Max(0f, parent.Height - _windowDragTarget.Height));
            }
            _windowDragTarget.Left = left;
            _windowDragTarget.Top  = top;
            _windowDragTarget.ResetAnchorCapture();
            return;
        }

        if (Captured is not null)
        {
            DispatchMouseMove(Captured, x, y);

            if (_dragCandidate && DragSource is null)
            {
                if (Math.Abs(x - _pressX) > DragDistanceThreshold
                    || Math.Abs(y - _pressY) > DragDistanceThreshold)
                {
                    BeginDrag(Captured);
                }
            }
            if (DragSource is not null)
                UpdateDragHover(x, y);
            return;
        }

        UpdateHover(x, y);
        WorldMouseMoveFallThrough?.Invoke(x, y);
    }

    private UiElement? _activePopup;
    private Action? _activePopupDismiss;

    internal void SetActivePopup(UiElement popup, Action dismiss)
    {
        if (!ReferenceEquals(_activePopup, popup))
            _activePopupDismiss?.Invoke();
        _activePopup = popup;
        _activePopupDismiss = dismiss;
    }

    internal void ClearActivePopup(UiElement popup)
    {
        if (!ReferenceEquals(_activePopup, popup)) return;
        _activePopup = null;
        _activePopupDismiss = null;
    }

    private UiElement? PopupHit(int x, int y)
    {
        if (_activePopup is not { } popup) return null;
        for (UiElement? e = popup; e is not null; e = e.Parent)
        {
            if (ReferenceEquals(e, this)) break;
            if (!e.Visible || !e.Enabled || e.Parent is null)
            {
                var stale = _activePopupDismiss;
                _activePopup = null;
                _activePopupDismiss = null;
                stale?.Invoke();
                return null;
            }
        }
        var pp = popup.ScreenPosition;
        return popup.HitTest(x - pp.X, y - pp.Y);
    }

    public void OnMouseDown(UiMouseButton btn, int x, int y, uint flags = 0)
    {
        (x, y) = MapWindowToCanvas(x, y);
        MouseX = x; MouseY = y;
        UpdateButtonFlag(btn, down: true);
        _pressX = x; _pressY = y;

        if (Modal is not null && !ContainsAbsolute(Modal, x, y))
            return;

        UiElement? target;
        if (_activePopup is not null)
        {
            target = PopupHit(x, y);
            if (target is null)
            {
                if (_activePopup is not null)
                {
                    // Press outside a live popup: dismiss it, swallow the press.
                    var dismiss = _activePopupDismiss;
                    _activePopup = null;
                    _activePopupDismiss = null;
                    dismiss?.Invoke();
                    return;
                }
                // Stale registration self-healed inside PopupHit — fall
                // through to the ordinary walk for this press.
                (target, _, _) = HitTestTopDown(x, y);
            }
        }
        else
        {
            (target, _, _) = HitTestTopDown(x, y);
        }
        if (target is null)
        {
            if (btn == UiMouseButton.Left) SetKeyboardFocus(null);
            WorldMouseFallThrough?.Invoke(btn, x, y, flags);
            return;
        }

        if (btn == UiMouseButton.Left)
            SetKeyboardFocus(target.AcceptsFocus ? target : null);

        SetCapture(target);

        var window = FindWindow(target);
        var handleWindow = FindDragHandleWindow(target);
        var raiseWindow = window ?? handleWindow;
        if (raiseWindow is not null) BringToFront(raiseWindow);
        if (btn == UiMouseButton.Left && raiseWindow is not null && !UiLocked)
        {
            var gripEdges = EffectiveGripEdges(target, window);
            var edges = gripEdges != ResizeEdges.None
                ? gripEdges
                : (handleWindow is null && window is { Resizable: true }
                    ? HitEdges(window, x, y, ResizeGrip)
                    : ResizeEdges.None);
            if (edges != ResizeEdges.None)
            {
                _resizeTarget = window;
                _resizeEdges = edges;
                _resizeStartX = window!.Left; _resizeStartY = window.Top;
                _resizeStartW = window.Width; _resizeStartH = window.Height;
                _resizeMouseX = x; _resizeMouseY = y;
                _dragCandidate = false;
            }
            else if (handleWindow is not null)
            {
                _windowDragTarget = handleWindow;
                _windowDragOffX = x - (int)handleWindow.Left;
                _windowDragOffY = y - (int)handleWindow.Top;
                _dragCandidate = false;
            }
            else if (target.IsDragSource)
            {
                _dragCandidate = true;
            }
            else if (target.CapturesPointerDrag || target.HandlesClick)
            {
                _dragCandidate = false;
            }
            else if (window is { Draggable: true })
            {
                _windowDragTarget = window;
                _windowDragOffX = x - (int)window.Left;
                _windowDragOffY = y - (int)window.Top;
                _dragCandidate = false;
            }
            else { _dragCandidate = true; }
        }
        else if (target.CapturesPointerDrag)
        {
            // No window ancestor, but the target still owns its interior drag.
            _dragCandidate = false;
        }
        else
        {
            _dragCandidate = btn == UiMouseButton.Left;
        }

        int rawType = btn switch
        {
            UiMouseButton.Left   => UiEventType.MouseDown,
            UiMouseButton.Right  => UiEventType.RightDown,
            UiMouseButton.Middle => UiEventType.MiddleDown,
            _ => UiEventType.MouseDown,
        };
        var sp = target.ScreenPosition;
        var e = new UiEvent(target.EventId, target, rawType,
                            Data0: (int)flags, Data1: (int)(x - sp.X), Data2: (int)(y - sp.Y));
        BubbleEvent(target, in e);
    }

    public void OnMouseUp(UiMouseButton btn, int x, int y, uint flags = 0)
    {
        (x, y) = MapWindowToCanvas(x, y);
        MouseX = x; MouseY = y;
        UpdateButtonFlag(btn, down: false);

        if (_resizeTarget is not null)
        {
            var resizedWindow = _resizeTarget;
            _resizeTarget = null;
            ReleaseCapture();
            NotifyWindowResized(resizedWindow);
            return;
        }

        if (_windowDragTarget is not null)
        {
            var movedWindow = _windowDragTarget;
            _windowDragTarget = null;
            ReleaseCapture();
            NotifyWindowMoved(movedWindow);
            return;
        }

        if (DragSource is not null)
        {
            FinishDrag(x, y);
            ReleaseCapture();
            _dragCandidate = false;
            return;
        }

        if (Captured is { } target)
        {
            int rawType = btn switch
            {
                UiMouseButton.Left   => UiEventType.MouseUp,
                UiMouseButton.Right  => UiEventType.RightUp,
                UiMouseButton.Middle => UiEventType.MiddleUp,
                _ => UiEventType.MouseUp,
            };

            var sp = target.ScreenPosition;
            var raw = new UiEvent(target.EventId, target, rawType,
                                  Data0: (int)flags,
                                  Data1: (int)(x - sp.X), Data2: (int)(y - sp.Y));
            BubbleEvent(target, in raw);

            if (btn == UiMouseButton.Left && ContainsAbsolute(target, x, y))
            {
                long now = _nowMs != 0 ? _nowMs : Environment.TickCount64;
                bool isDoubleClick =
                    ReferenceEquals(target, _lastClickTarget)
                    && now - _lastClickMs <= DoubleClickDelayMs
                    && Math.Abs(x - _lastClickX) <= DragDistanceThreshold
                    && Math.Abs(y - _lastClickY) <= DragDistanceThreshold;

                var click = new UiEvent(target.EventId, target, UiEventType.Click,
                                        Data0: (int)flags,
                                        Data1: (int)(x - sp.X), Data2: (int)(y - sp.Y));
                BubbleEvent(target, in click);

                if (isDoubleClick)
                {
                    var dbl = new UiEvent(target.EventId, target, UiEventType.DoubleClick,
                                          Data0: (int)flags,
                                          Data1: (int)(x - sp.X), Data2: (int)(y - sp.Y));
                    BubbleEvent(target, in dbl);
                }
                _lastClickTarget = target;
                _lastClickMs = now;
                _lastClickX = x;
                _lastClickY = y;
            }
            else if (btn == UiMouseButton.Right
                && ContainsAbsolute(target, x, y)
                && Math.Abs(x - _pressX) <= DragDistanceThreshold
                && Math.Abs(y - _pressY) <= DragDistanceThreshold)
            {
                var click = new UiEvent(target.EventId, target, UiEventType.RightClick,
                                        Data0: (int)flags);
                BubbleEvent(target, in click);
            }

            if (ReferenceEquals(Captured, target))
                ReleaseCapture();
            _dragCandidate = false;
            return;
        }

        WorldMouseFallThrough?.Invoke(btn, x, y, flags);
    }

    public void OnScroll(int dy)
    {
        if (PopupHit(MouseX, MouseY) is { } popupTarget)
        {
            var pp = popupTarget.ScreenPosition;
            var pe = new UiEvent(popupTarget.EventId, popupTarget, UiEventType.Scroll,
                                 Data0: dy,
                                 Data1: (int)(MouseX - pp.X), Data2: (int)(MouseY - pp.Y));
            BubbleEvent(popupTarget, in pe);
            return;
        }

        var (target, lx, ly) = HitTestTopDown(MouseX, MouseY);
        if (target is null)
        {
            WorldScrollFallThrough?.Invoke(dy);
            return;
        }
        var e = new UiEvent(target.EventId, target, UiEventType.Scroll, Data0: dy,
                            Data1: (int)lx, Data2: (int)ly);
        BubbleEvent(target, in e);
    }

    public void OnKeyDown(int vk, uint lparam = 0)
    {
        if (_suppressedPhysicalKey == vk)
            return;

        if (KeyboardFocus is null && DefaultTextInput is not null
            && (vk == (int)Silk.NET.Input.Key.Tab
                || vk == (int)Silk.NET.Input.Key.Enter))
        {
            SetKeyboardFocus(DefaultTextInput);
            return;
        }

        // Focus widget first.
        if (KeyboardFocus is not null)
        {
            var e = new UiEvent(KeyboardFocus.EventId, KeyboardFocus, UiEventType.KeyDown,
                                Data0: vk, Data1: (int)lparam);
            if (BubbleEvent(KeyboardFocus, in e)) return;
        }

        if (KeyboardFocus is null || !KeyboardFocus.IsEditControl)
        {
            var root = Modal ?? (UiElement)this;
            var e = new UiEvent(root.EventId, root, UiEventType.KeyDown,
                                Data0: vk, Data1: (int)lparam);
            if (BubbleEvent(root, in e)) return;
        }

        WorldKeyFallThrough?.Invoke(vk, lparam);
    }

    public void OnKeyUp(int vk, uint lparam = 0)
    {
        if (_suppressedPhysicalKey == vk)
        {
            _suppressedPhysicalKey = null;
            return;
        }
        if (KeyboardFocus is not null)
        {
            var e = new UiEvent(KeyboardFocus.EventId, KeyboardFocus, UiEventType.KeyUp,
                                Data0: vk, Data1: (int)lparam);
            if (BubbleEvent(KeyboardFocus, in e)) return;
        }
        // Key up rarely falls through; game logic generally keys off KeyDown.
    }

    public void OnChar(int codepoint)
    {
        if (_suppressedPhysicalKey is not null)
            return;
        if (KeyboardFocus is null || !KeyboardFocus.IsEditControl) return;
        var e = new UiEvent(KeyboardFocus.EventId, KeyboardFocus, UiEventType.Char,
                            Data0: codepoint);
        BubbleEvent(KeyboardFocus, in e);
    }

    /// <summary>Suppress the raw retained-UI tail of a semantic key action.</summary>
    public void SuppressPhysicalKeyUntilRelease(Silk.NET.Input.Key key)
        => _suppressedPhysicalKey = (int)key;


    public void SetKeyboardFocus(UiElement? e)
    {
        if (KeyboardFocus == e) return;
        UiElement? previous = KeyboardFocus;
        if (previous is not null)
        {
            var lost = new UiEvent(previous.EventId, previous, UiEventType.FocusLost);
            previous.OnEvent(in lost);
        }
        KeyboardFocus = e;
        if (e is not null)
        {
            var gained = new UiEvent(e.EventId, e, UiEventType.FocusGained);
            e.OnEvent(in gained);
        }
        KeyboardFocusChanged?.Invoke(previous, e);
    }

    public void SetCapture(UiElement e)
    {
        if (ReferenceEquals(Captured, e)) return;
        UiElement? previous = Captured;
        Captured = e;
        NotifyCaptureLost(previous);
        PointerCaptureChanged?.Invoke(previous, e);
    }

    public void ReleaseCapture()
    {
        UiElement? previous = Captured;
        Captured = null;
        _hoverStartedMs = _nowMs;
        _lastMouseMoveMs = _nowMs;
        NotifyCaptureLost(previous);
        if (previous is not null)
            PointerCaptureChanged?.Invoke(previous, null);
    }

    private static void NotifyCaptureLost(UiElement? previous)
    {
        if (previous is null) return;
        var lost = new UiEvent(
            previous.EventId, previous, UiEventType.CaptureChanged);
        previous.OnEvent(in lost);
    }

    // ── Window manager (named top-level windows: Show / Hide / Toggle) ───


    public RetailWindowHandle RegisterWindow(
        string name,
        UiElement window,
        UiElement? contentRoot = null,
        IRetainedPanelController? controller = null,
        IRetainedWindowStateController? stateController = null,
        int authoredGeometryRevision = 0)
        => WindowManager.Register(
            name,
            window,
            contentRoot,
            controller,
            stateController,
            authoredGeometryRevision);

    public bool UnregisterWindow(string name) => WindowManager.Unregister(name);

    /// <summary>Make the named window visible. No-op (returns false) if unknown.</summary>
    public bool ShowWindow(string name)
        => WindowManager.Show(name);

    /// <summary>Hide the named window. No-op (returns false) if unknown.</summary>
    public bool HideWindow(string name)
        => WindowManager.Hide(name);

    public bool CloseWindow(string name) => WindowManager.Close(name);

    public bool IsWindowVisible(string name)
        => WindowManager.IsVisible(name);

    /// <summary>Flip the named window's visibility (Show if hidden, Hide if shown).
    /// Returns the new IsVisible state (false for an unknown name).</summary>
    public bool ToggleWindow(string name)
        => WindowManager.Toggle(name);

    public void BringToFront(UiElement window)
        => WindowManager.BringToFront(window);

    internal void NotifyWindowMoved(UiElement window)
    {
        if (WindowManager.TryGet(window, out var handle))
            WindowMoved?.Invoke(handle.Name, window);
    }

    internal void NotifyWindowResized(UiElement window)
    {
        if (WindowManager.TryGet(window, out var handle))
            WindowResized?.Invoke(handle.Name, window);
    }


    private void BeginDrag(UiElement source)
    {
        var payload = source.GetDragPayload();
        if (payload is null) { _dragCandidate = false; return; }
        DragSource  = source;
        DragPayload = payload;
        _dragGhost  = source.GetDragGhost();
        var e = new UiEvent(source.EventId, source, UiEventType.DragBegin, Payload: payload);
        source.OnEvent(in e);
        source.SetDragSourceActive(true, payload);
    }

    private void UpdateDragHover(int x, int y)
    {
        var (t, lx, ly) = HitTestTopDown(x, y);
        if (ReferenceEquals(t, _lastDragHoverTarget)) return;

        // Leave old target.
        if (_lastDragHoverTarget is not null)
        {
            var eLeave = new UiEvent(DragSource!.EventId, _lastDragHoverTarget,
                                     UiEventType.DragOver, Data1: x, Data2: y,
                                     Payload: DragPayload);
            _lastDragHoverTarget.OnEvent(in eLeave);
        }

        // Enter new target.
        if (t is not null)
        {
            var eEnter = new UiEvent(DragSource!.EventId, t, UiEventType.DragEnter,
                                     Data1: (int)lx, Data2: (int)ly,
                                     Payload: DragPayload);
            t.OnEvent(in eEnter);
        }
        _lastDragHoverTarget = t;
    }

    private void FinishDrag(int x, int y)
    {
        UiElement? source = DragSource;
        object? payload = DragPayload;

        source?.SetDragSourceActive(false, payload);

        var (t, lx, ly) = HitTestTopDown(x, y);
        if (t is not null)
        {
            var e = new UiEvent(source!.EventId, t, UiEventType.DropReleased,
                                Data1: (int)lx, Data2: (int)ly, Payload: payload);
            t.OnEvent(in e);
        }
        else if (payload is not null)
        {
            DragReleasedOutsideUi?.Invoke(payload, x, y);
        }
        DragSource = null;
        DragPayload = null;
        _dragGhost = null;
        _lastDragHoverTarget = null;
    }

    // ── Hover / tooltip ─────────────────────────────────────────────────

    public void ResetTooltipTracking()
    {
        _hoverStartedMs = _nowMs;
        _lastMouseMoveMs = _nowMs;   // same fresh idle deadline for the world-hover dwell
        _tooltipFired = false;
    }

    private void UpdateHover(int x, int y)
    {
        UiElement? w = PopupHit(x, y);
        if (w is null)
            (w, _, _) = HitTestTopDown(x, y);
        if (ReferenceEquals(w, _hoverWidget))
        {
            if (w?.ReceivesHoverMouseMove == true)
                DispatchMouseMove(w, x, y);
            if (!_tooltipFired)
                _hoverStartedMs = _nowMs;
            return;
        }

        if (_hoverWidget is not null)
        {
            var leave = new UiEvent(_hoverWidget.EventId, _hoverWidget, UiEventType.HoverLeave);
            _hoverWidget.OnEvent(in leave);
            if (_tooltipFired)
                TooltipHide?.Invoke(_hoverWidget);
        }
        _hoverWidget = w;
        _hoverStartedMs = _nowMs;
        _tooltipFired = false;
        if (w is not null)
        {
            var screen = w.ScreenPosition;
            var enter = new UiEvent(
                w.EventId,
                w,
                UiEventType.HoverEnter,
                Data1: (int)(x - screen.X),
                Data2: (int)(y - screen.Y));
            w.OnEvent(in enter);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    public void FireEvent(int type, UiElement target, object? payload = null)
    {
        var e = new UiEvent(target.EventId, target, type, Payload: payload);
        target.OnEvent(in e);
    }

    private void UpdateButtonFlag(UiMouseButton b, bool down)
    {
        switch (b)
        {
            case UiMouseButton.Left:   LeftButtonDown = down; break;
            case UiMouseButton.Right:  RightButtonDown = down; break;
            case UiMouseButton.Middle: MiddleButtonDown = down; break;
        }
    }

    private (UiElement? element, float localX, float localY) HitTestTopDown(int x, int y)
    {
        // Modal gets exclusive hit-test.
        if (Modal is not null)
        {
            var mp = Modal.ScreenPosition;
            var mh = Modal.HitTest(x - mp.X, y - mp.Y);
            if (mh is not null) return (mh, x - mp.X, y - mp.Y);
            return (null, 0, 0);
        }

        foreach (var c in ChildrenFrontToBackSnapshot())
        {
            var cp = c.ScreenPosition;
            var hit = c.HitTest(x - cp.X, y - cp.Y);
            if (hit is not null)
                return (hit, x - cp.X, y - cp.Y);
        }
        return (null, 0, 0);
    }

    /// <summary>Public hit-test for tooling (the UI Studio inspector): the topmost element under
    /// (x,y) in root space, honoring modal exclusivity + Z-order. Wraps the private HitTestTopDown.</summary>
    public UiElement? Pick(int x, int y) => HitTestTopDown(x, y).element;

    private static UiElement? FindWindow(UiElement? e)
    {
        while (e is not null)
        {
            if (e.Draggable || e.Resizable) return e;
            e = e.Parent;
        }
        return null;
    }

    private UiElement? FindDragHandleWindow(UiElement? e)
    {
        while (e is not null && !ReferenceEquals(e, this) && !e.WindowMoveHandle)
            e = e.Parent;
        if (e is null || ReferenceEquals(e, this)) return null;
        while (e.Parent is not null && !ReferenceEquals(e.Parent, this))
            e = e.Parent;
        return e;
    }

    /// <summary>Which edges of <paramref name="w"/>'s screen rect the point
    /// (<paramref name="x"/>,<paramref name="y"/>) is within <paramref name="grip"/> px of.
    /// None if the point is outside the grip-expanded box entirely.</summary>
    internal static ResizeEdges HitEdges(UiElement w, int x, int y, int grip)
    {
        float l = w.Left, t = w.Top, r = w.Left + w.Width, b = w.Top + w.Height;
        if (x < l - grip || x > r + grip || y < t - grip || y > b + grip) return ResizeEdges.None;
        var e = ResizeEdges.None;
        if (System.Math.Abs(x - l) <= grip) e |= ResizeEdges.Left;
        if (System.Math.Abs(x - r) <= grip) e |= ResizeEdges.Right;
        if (System.Math.Abs(y - t) <= grip) e |= ResizeEdges.Top;
        if (System.Math.Abs(y - b) <= grip) e |= ResizeEdges.Bottom;
        if (!w.ResizeX) e &= ~(ResizeEdges.Left | ResizeEdges.Right);
        if (!w.ResizeY) e &= ~(ResizeEdges.Top | ResizeEdges.Bottom);
        e &= w.ResizableEdges;
        return e;
    }

    public static (float x, float y, float w, float h) ResizeRect(
        float startX, float startY, float startW, float startH,
        ResizeEdges edges, float dx, float dy, float minW, float minH, float maxW, float maxH)
    {
        float x = startX, y = startY, w = startW, h = startH;
        if ((edges & ResizeEdges.Right) != 0) w = System.Math.Clamp(startW + dx, minW, maxW);
        if ((edges & ResizeEdges.Bottom) != 0) h = System.Math.Clamp(startH + dy, minH, maxH);
        if ((edges & ResizeEdges.Left) != 0) { float nw = System.Math.Clamp(startW - dx, minW, maxW); x = startX + (startW - nw); w = nw; }
        if ((edges & ResizeEdges.Top) != 0) { float nh = System.Math.Clamp(startH - dy, minH, maxH); y = startY + (startH - nh); h = nh; }
        return (x, y, w, h);
    }

    private static bool ContainsAbsolute(UiElement e, int x, int y)
    {
        var sp = e.ScreenPosition;
        return x >= sp.X && x < sp.X + e.Width
            && y >= sp.Y && y < sp.Y + e.Height;
    }

    private void DispatchMouseMove(UiElement target, int x, int y)
    {
        var sp = target.ScreenPosition;
        var e = new UiEvent(target.EventId, target, UiEventType.MouseMove,
                            Data1: (int)(x - sp.X), Data2: (int)(y - sp.Y));
        BubbleEvent(target, in e);
    }

    private bool BubbleEvent(UiElement start, in UiEvent e)
    {
        var w = start;
        while (w is not null)
        {
            if (w.OnEvent(in e)) return true;
            w = w.Parent;
        }
        return false;
    }

    protected override void OnDraw(UiRenderContext ctx)
    {
    }
}
