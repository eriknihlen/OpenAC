using System;
using System.Collections.Generic;
using AcDream.App.UI.Layout;

namespace AcDream.App.UI;

public sealed class UiTemplateListBox : UiDatElement
{
    public const uint RetailTypeId = 5u;

    private const int DefaultLineHeight = 16;

    private UiScrollablePanel? _viewport;
    private int _pendingLineHeight = DefaultLineHeight;

    /// <summary>The authored row-template list (dat property 0x64), in authored array order.</summary>
    public IReadOnlyList<UiTemplateListEntry> Templates { get; }

    public uint ScrollbarElementId { get; }

    public UiScrollable Scroll => Viewport.Scroll;

    public int ContentHeight => _viewport?.ContentHeight ?? 0;

    public int LineHeight
    {
        get => _viewport?.LineHeight ?? _pendingLineHeight;
        set
        {
            _pendingLineHeight = value;
            if (_viewport is not null) _viewport.LineHeight = value;
        }
    }

    public Func<uint, uint, UiElement?>? TemplateResolver { get; set; }

    public UiTemplateListBox(
        ElementInfo info,
        Func<uint, (uint tex, int w, int h)> resolve,
        IReadOnlyList<UiTemplateListEntry> templates,
        uint scrollbarElementId)
        : base(info, resolve)
    {
        Templates = templates;
        ScrollbarElementId = scrollbarElementId;
    }

    public override bool ConsumesDatChildren => true;

    internal UiScrollablePanel? ViewportForTest => _viewport;

    private UiScrollablePanel Viewport
    {
        get
        {
            if (_viewport is null)
            {
                _viewport = new UiScrollablePanel
                {
                    Anchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Right | AnchorEdges.Bottom,
                    LineHeight = _pendingLineHeight,
                    Width = Width,
                    Height = Height,
                };
                base.AddChild(_viewport);

                _viewport.CaptureCurrentAnchorBaseline();
            }
            return _viewport;
        }
    }

    public UiElement? AddItemFromTemplateList(int index)
    {
        if (index < 0 || index >= Templates.Count) return null;
        Func<uint, uint, UiElement?>? resolver = TemplateResolver;
        if (resolver is null) return null;

        UiTemplateListEntry entry = Templates[index];
        UiElement? row = resolver(entry.TemplateLayoutId, entry.TemplateElementId);
        if (row is null) return null;

        return AddPrebuiltRow(row);
    }

    public UiElement AddPrebuiltRow(UiElement row)
    {
        ArgumentNullException.ThrowIfNull(row);
        UiScrollablePanel viewport = Viewport;
        row.Left = 0f;
        row.Top = viewport.ContentHeight;
        viewport.AddChild(row);
        return row;
    }

    public void RemoveTail(int retainedItemCount)
    {
        int count = _viewport?.Children.Count ?? 0;
        if (retainedItemCount < 0 || retainedItemCount > count)
            throw new ArgumentOutOfRangeException(nameof(retainedItemCount));
        if (_viewport is null || retainedItemCount == count)
            return;

        for (int i = count - 1; i >= retainedItemCount; i--)
            _viewport.RemoveChild(_viewport.Children[i]);
    }

    public int ItemCount => _viewport?.Children.Count ?? 0;

    public void Flush() => _viewport?.ClearContent();

    public void FlushPreservingScroll()
    {
        if (_viewport is null) return;
        int savedScrollY = _viewport.Scroll.ScrollY;
        _viewport.ClearContent();
        _viewport.Scroll.SetScrollY(savedScrollY);
    }
}
