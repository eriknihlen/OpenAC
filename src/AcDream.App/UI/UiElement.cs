using System;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.App.UI;

[System.Flags]
public enum AnchorEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

public readonly record struct UiCursorMedia(uint File, int HotspotX, int HotspotY)
{
    public bool IsValid => File != 0;
}

public abstract class UiElement
{
    public uint EventId { get; internal set; }

    public uint DatElementId { get; internal set; }

    /// <summary>Human-readable name for debugging / FindByName.</summary>
    public string? Name { get; set; }

    public bool AuthoredInvisible { get; internal set; }

    public bool AuthoredTooltipEnabled { get; internal set; }

    public string? AuthoredTooltipText { get; internal set; }

    public uint AuthoredTooltipRootElementId { get; internal set; }

    public uint AuthoredTooltipLayoutDid { get; internal set; }

    public uint SourceLayoutDid { get; internal set; }

    public Func<uint>? FoundObjectGuidProvider { get; set; }

    public uint AuthoredTooltipTextChildElementId { get; internal set; }

    public float? AuthoredTooltipDelaySeconds { get; internal set; }

    public int? AuthoredResizeMaxWidth { get; internal set; }
    public int? AuthoredResizeMinWidth { get; internal set; }

    public int? AuthoredResizeMaxHeight { get; internal set; }
    public int? AuthoredResizeMinHeight { get; internal set; }

    private readonly Dictionary<string, UiCursorMedia> _stateCursors = new();

    public IReadOnlyDictionary<string, UiCursorMedia> StateCursors => _stateCursors;

    public virtual string ActiveCursorStateName => "";

    public void SetStateCursors(IReadOnlyDictionary<string, UiCursorMedia> cursors)
    {
        _stateCursors.Clear();
        foreach (var kv in cursors)
        {
            if (kv.Value.IsValid)
                _stateCursors[kv.Key] = kv.Value;
        }
    }

    public UiCursorMedia ActiveCursor()
        => CursorForState(ActiveCursorStateName, allowFallback: true);

    public UiCursorMedia CursorForState(string stateName, bool allowFallback = true)
    {
        if (!string.IsNullOrEmpty(stateName)
            && _stateCursors.TryGetValue(stateName, out var named)
            && named.IsValid)
            return named;

        if (!allowFallback)
            return default;

        if (_stateCursors.TryGetValue("", out var direct) && direct.IsValid)
            return direct;
        if (_stateCursors.TryGetValue("Normal", out var normal) && normal.IsValid)
            return normal;

        return default;
    }

    // ── Geometry ────────────────────────────────────────────────────────
    /// <summary>X in the parent's local pixel space.</summary>
    public float Left   { get; set; }
    public float Top    { get; set; }
    public float Width  { get; set; }
    public float Height { get; set; }

    public Vector2 ScreenPosition
    {
        get
        {
            var p = new Vector2(Left, Top);
            var parent = Parent;
            while (parent is not null)
            {
                p += new Vector2(parent.Left, parent.Top);
                parent = parent.Parent;
            }
            return p;
        }
    }

    // ── State flags ─────────────────────────────────────────────────────
    private bool _visible = true;
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;

            UiRoot? root = FindRoot();
            root?.OnElementVisibilityChanging(this, value);
            _visible = value;
            root?.OnElementVisibilityChanged(this, value);
        }
    }
    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            OnEnabledChanged();
        }
    }

    public bool ClickThrough    { get; set; }

    /// <summary>
    /// Optional live visibility reader, evaluated once per tick. Markup
    /// <c>visible="{Binding}"</c> uses this so a panel can show and hide itself
    /// from its binding object's state without the owner touching UI objects.
    /// </summary>
    public Func<bool>? VisibleSource { get; set; }

    public Func<bool>? EnabledSource { get; set; }

    public bool AcceptsFocus    { get; set; }

    /// <summary>
    /// True if this is a text-entry (edit box); used by focus routing
    /// to suppress global hotkeys while typing.
    /// </summary>
    public bool IsEditControl   { get; set; }

    private int _zOrder;

    /// <summary>Painter's-algorithm z-order within siblings. Higher = on top.</summary>
    public int ZOrder
    {
        get => _zOrder;
        set
        {
            if (_zOrder == value) return;
            _zOrder = value;
            Parent?.InvalidateChildOrder();
        }
    }

    public float Opacity        { get; set; } = 1f;

    public bool Draggable { get; set; }

    public bool WindowMoveHandle { get; set; }

    public bool ConstrainDragToParent { get; set; }

    public bool ConstrainResizeToParent { get; set; }

    public bool Resizable { get; set; }

    public bool CapturesPointerDrag { get; set; }

    public virtual bool IsDragSource => false;

    public virtual bool HandlesClick => false;

    public virtual bool ReceivesHoverMouseMove => false;

    /// <summary>Minimum size enforced while resizing.</summary>
    public float MinWidth { get; set; } = 40f;
    public float MinHeight { get; set; } = 40f;

    /// <summary>Maximum size enforced while resizing (default unbounded).</summary>
    public float MaxWidth { get; set; } = float.MaxValue;
    public float MaxHeight { get; set; } = float.MaxValue;

    public bool ResizeX { get; set; } = true;
    public bool ResizeY { get; set; } = true;

    public ResizeEdges ResizableEdges { get; set; } =
        ResizeEdges.Left | ResizeEdges.Right | ResizeEdges.Top | ResizeEdges.Bottom;

    /// <summary>Edges this element anchors to in its parent. Default Left|Top
    /// (pinned top-left, fixed size — no reflow). Left|Right stretches width.</summary>
    private AnchorEdges _anchors = AnchorEdges.Left | AnchorEdges.Top;

    public AnchorEdges Anchors
    {
        get => _anchors;
        set
        {
            _anchors = value;
            LayoutPolicy = null;
            _anchorCaptured = false;
        }
    }

    public UiLayoutPolicy? LayoutPolicy { get; set; }

    // ── Tree structure ──────────────────────────────────────────────────
    public UiElement? Parent { get; private set; }

    private readonly List<UiElement> _children = new();
    private UiElement[]? _childrenBackToFront;
    private UiElement[]? _childrenFrontToBack;
    public IReadOnlyList<UiElement> Children => _children;

    public virtual void AddChild(UiElement child)
    {
        if (child.Parent is not null) child.Parent.RemoveChild(child);
        child.Parent = this;
        _children.Add(child);
        InvalidateChildOrder();
    }

    public static UiElement? FindDescendant(UiElement root, uint datElementId)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.DatElementId == datElementId) return root;
        foreach (UiElement child in root.Children)
        {
            UiElement? found = FindDescendant(child, datElementId);
            if (found is not null) return found;
        }
        return null;
    }

    public virtual bool RemoveChild(UiElement child)
    {
        if (!_children.Contains(child)) return false;
        FindRoot()?.OnSubtreeRemoving(child);
        _children.Remove(child);
        child.Parent = null;
        InvalidateChildOrder();
        return true;
    }

    internal UiElement[] ChildrenBackToFrontSnapshot()
    {
        if (_childrenBackToFront is not null)
            return _childrenBackToFront;

        _childrenBackToFront = _children.ToArray();
        Array.Sort(
            _childrenBackToFront,
            static (a, b) => a.ZOrder.CompareTo(b.ZOrder));
        return _childrenBackToFront;
    }

    internal UiElement[] ChildrenFrontToBackSnapshot()
    {
        if (_childrenFrontToBack is not null)
            return _childrenFrontToBack;

        UiElement[] backToFront = ChildrenBackToFrontSnapshot();
        _childrenFrontToBack = new UiElement[backToFront.Length];
        for (int source = backToFront.Length - 1, destination = 0;
             source >= 0;
             source--, destination++)
        {
            _childrenFrontToBack[destination] = backToFront[source];
        }

        return _childrenFrontToBack;
    }

    private void InvalidateChildOrder()
    {
        _childrenBackToFront = null;
        _childrenFrontToBack = null;
    }

    public virtual bool ConsumesDatChildren => false;

    // ── Virtual overrides ───────────────────────────────────────────────

    protected virtual void OnDraw(UiRenderContext ctx) { }

    protected virtual void OnDrawAfterChildren(UiRenderContext ctx) { }

    protected virtual void OnDrawOverlay(UiRenderContext ctx) { }

    protected virtual bool ClipsChildren => true;

    protected virtual bool ExpandsClipForPopup => false;

    protected virtual void OnTick(double deltaSeconds) { }

    protected virtual void OnEnabledChanged() { }

    protected virtual bool OnHitTest(float localX, float localY)
        => localX >= 0f && localX < Width && localY >= 0f && localY < Height;

    public virtual bool OnEvent(in UiEvent e) => false;

    public virtual object? GetDragPayload() => null;

    public virtual (uint tex, int w, int h)? GetDragGhost() => null;

    internal virtual void SetDragSourceActive(bool active, object? payload) { }

    public Func<string?>? RuntimeTooltipTextSource { get; set; }

    public virtual string? GetTooltipText()
    {
        string? text = RuntimeTooltipTextSource?.Invoke();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }


    internal void DrawSelfAndChildren(UiRenderContext ctx)
    {
        if (!Visible) return;

        ctx.PushTransform(Left, Top);
        ctx.PushAlpha(Opacity);
        bool clipsChildren = ClipsChildren;
        if (clipsChildren)
            ctx.PushClip(0f, 0f, Width, Height);
        try
        {
            if (!ctx.CurrentClipIsEmpty)
            {
                OnDraw(ctx);

                for (int i = 0; i < _children.Count; i++)
                    _children[i].ApplyAnchor(Width, Height);

                if (_children.Count > 0)
                {
                    UiElement[] ordered = ChildrenBackToFrontSnapshot();
                    for (int i = 0; i < ordered.Length; i++)
                        ordered[i].DrawSelfAndChildren(ctx);
                }

                OnDrawAfterChildren(ctx);
            }
        }
        finally
        {
            if (clipsChildren)
                ctx.PopClip();
            ctx.PopAlpha();
            ctx.PopTransform();
        }
    }

    internal void DrawOverlays(UiRenderContext ctx)
    {
        if (!Visible) return;
        ctx.PushTransform(Left, Top);
        ctx.PushAlpha(Opacity);
        try
        {
            if (ExpandsClipForPopup)
            {
                ctx.PushClipUnbounded();
                try { OnDrawOverlay(ctx); }
                finally { ctx.PopClip(); }
            }
            else
            {
                OnDrawOverlay(ctx);
            }
            if (_children.Count > 0)
            {
                bool clipsChildren = ClipsChildren;
                if (clipsChildren)
                    ctx.PushClip(0f, 0f, Width, Height);
                try
                {
                    UiElement[] ordered = ChildrenBackToFrontSnapshot();
                    for (int i = 0; i < ordered.Length; i++)
                        ordered[i].DrawOverlays(ctx);
                }
                finally
                {
                    if (clipsChildren)
                        ctx.PopClip();
                }
            }
        }
        finally
        {
            ctx.PopAlpha();
            ctx.PopTransform();
        }
    }

    internal void TickSelfAndChildren(double dt)
    {
        if (VisibleSource is { } visibility)
            Visible = visibility();
        if (!Visible) return;
        if (EnabledSource is { } enabled)
            Enabled = enabled();
        OnTick(dt);
        for (int i = 0; i < _children.Count; i++)
            _children[i].TickSelfAndChildren(dt);
    }

    internal UiElement? HitTest(float localX, float localY)
    {
        if (!Visible || !Enabled) return null;
        if (ClipsChildren
            && (localX < 0f || localX >= Width || localY < 0f || localY >= Height))
            return null;

        if (_children.Count > 0)
        {
            UiElement[] ordered = ChildrenFrontToBackSnapshot();
            for (int i = 0; i < ordered.Length; i++)
            {
                var c = ordered[i];
                var childHit = c.HitTest(localX - c.Left, localY - c.Top);
                if (childHit is not null) return childHit;
            }
        }

        if (ClickThrough) return null;
        return OnHitTest(localX, localY) ? this : null;
    }

    // ── Anchor layout ────────────────────────────────────────────────────

    private bool _anchorCaptured;
    private float _amL, _amT, _amR, _amB, _aw0, _ah0;

    internal void ApplyAnchor(float parentW, float parentH)
    {
        if (LayoutPolicy is not null)
        {
            var current = UiPixelRect.FromPositionAndSize(
                (int)Left,
                (int)Top,
                (int)Width,
                (int)Height);
            var parent = UiPixelRect.FromPositionAndSize(0, 0, (int)parentW, (int)parentH);
            var next = LayoutPolicy.Apply(current, parent);
            Left = next.X0;
            Top = next.Y0;
            Width = next.Width;
            Height = next.Height;
            return;
        }

        if (Anchors == AnchorEdges.None) return;
        if (!_anchorCaptured)
        {
            _amL = Left; _amT = Top;
            _amR = parentW - (Left + Width);
            _amB = parentH - (Top + Height);
            _aw0 = Width; _ah0 = Height;
            _anchorCaptured = true;
        }
        var (x, y, w, h) = ComputeAnchoredRect(Anchors, _amL, _amT, _amR, _amB, _aw0, _ah0, parentW, parentH);
        Left = x; Top = y; Width = w; Height = h;
    }

    internal void ResetAnchorCapture()
    {
        _anchorCaptured = false;
        if (LayoutPolicy is null || Parent is null) return;

        LayoutPolicy.Rebase(
            UiPixelRect.FromPositionAndSize(
                (int)Left,
                (int)Top,
                (int)Width,
                (int)Height),
            UiPixelRect.FromPositionAndSize(
                0,
                0,
                (int)Parent.Width,
                (int)Parent.Height));
    }

    internal void CaptureCurrentAnchorBaseline()
    {
        if (Parent is null || Anchors == AnchorEdges.None) return;
        _anchorCaptured = false;
        ApplyAnchor(Parent.Width, Parent.Height);
    }

    internal void RebaseChildLayoutBaselines()
    {
        foreach (var child in _children)
            child.ResetAnchorCapture();
    }

    internal UiRoot? FindRoot()
    {
        UiElement e = this;
        while (e.Parent is not null) e = e.Parent;
        return e as UiRoot;
    }

    public static (float x, float y, float w, float h) ComputeAnchoredRect(
        AnchorEdges a, float mL, float mT, float mR, float mB,
        float w0, float h0, float parentW, float parentH)
    {
        bool l = (a & AnchorEdges.Left) != 0, r = (a & AnchorEdges.Right) != 0;
        float x, w;
        if (l && r) { x = mL; w = parentW - mR - mL; }
        else if (r) { w = w0; x = parentW - mR - w0; }
        else { x = mL; w = w0; }

        bool t = (a & AnchorEdges.Top) != 0, b = (a & AnchorEdges.Bottom) != 0;
        float y, h;
        if (t && b) { y = mT; h = parentH - mB - mT; }
        else if (b) { h = h0; y = parentH - mB - h0; }
        else { y = mT; h = h0; }

        if (w < 0) w = 0;
        if (h < 0) h = 0;
        return (x, y, w, h);
    }
}
