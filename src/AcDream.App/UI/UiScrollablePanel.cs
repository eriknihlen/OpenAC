using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AcDream.App.UI;

public sealed class UiScrollablePanel : UiPanel
{
    /// <inheritdoc/>
    protected override bool ClipsChildren => true;

    private readonly Dictionary<UiElement, float> _baseTops = new(ReferenceEqualityComparer.Instance);

    public UiScrollable Scroll { get; } = new();

    public int LineHeight { get; set; } = 16;

    public int ContentHeight { get; private set; }

    public UiScrollablePanel()
    {
        BackgroundColor = Vector4.Zero;
        BorderColor = Vector4.Zero;
    }

    public override void AddChild(UiElement child)
    {
        base.AddChild(child);
        Track(child);
    }

    public override bool RemoveChild(UiElement child)
    {
        bool removed = base.RemoveChild(child);
        if (removed)
        {
            _baseTops.Remove(child);
            RecomputeContentHeight();
        }
        return removed;
    }

    public void ClearContent()
    {
        foreach (var child in ChildrenBackToFrontSnapshot())
            RemoveChild(child);
        _baseTops.Clear();
        ContentHeight = 0;
        Scroll.SetScrollY(0);
    }

    internal void LayoutScrollableChildren()
    {
        Scroll.LineHeight = Math.Max(1, LineHeight);
        Scroll.ContentHeight = ContentHeight;
        Scroll.ViewHeight = Math.Max(0, (int)MathF.Floor(Height));
        Scroll.SetScrollY(Scroll.ScrollY);

        foreach (var child in Children)
        {
            if (!_baseTops.TryGetValue(child, out float baseTop))
                continue;

            float top = baseTop - Scroll.ScrollY;
            child.Top = top;
            child.Visible = top + child.Height > 0.5f && top < Height - 0.5f;
        }
    }

    public override bool OnEvent(in UiEvent e)
    {
        if (e.Type == UiEventType.Scroll)
        {
            Scroll.ScrollByLines(-e.Data0);
            LayoutScrollableChildren();
            return true;
        }
        return base.OnEvent(e);
    }

    protected override void OnDraw(UiRenderContext ctx)
    {
        LayoutScrollableChildren();
        base.OnDraw(ctx);
    }

    private void Track(UiElement child)
    {
        child.Anchors = AnchorEdges.None;
        _baseTops[child] = child.Top;
        RecomputeContentHeight();
    }

    private void RecomputeContentHeight()
    {
        float bottom = 0f;
        foreach (var child in Children)
        {
            if (!_baseTops.TryGetValue(child, out float top))
                continue;
            bottom = MathF.Max(bottom, top + child.Height);
        }
        ContentHeight = (int)MathF.Ceiling(bottom);
        Scroll.SetScrollY(Scroll.ScrollY);
    }
}
