using System;
using System.Collections.Generic;

namespace AcDream.App.UI;

public sealed class UiCollapsibleFrame : UiNineSlicePanel, IRetainedWindowStateController
{
    public UiCollapsibleFrame(Func<uint, (uint, int, int)> resolve) : base(resolve) { }

    public float CollapsedHeight { get; set; }
    public float ExpandedHeight  { get; set; }
    public IReadOnlyList<UiElement> SecondRow { get; set; } = Array.Empty<UiElement>();

    /// <summary>True when the frame is at (or nearer) the expanded stop.</summary>
    public bool IsExpanded => Height >= (CollapsedHeight + ExpandedHeight) * 0.5f;

    protected override void OnTick(double deltaSeconds)
    {
        base.OnTick(deltaSeconds);
        if (ExpandedHeight <= CollapsedHeight) return;
        bool expanded = IsExpanded;
        Height = expanded ? ExpandedHeight : CollapsedHeight;   // snap the dragged height to a stop
        for (int i = 0; i < SecondRow.Count; i++) SecondRow[i].Visible = expanded;
    }

    /// <summary>Test hook — OnTick is protected. Drives one snap+visibility reconcile.</summary>
    internal void TickForTest(double dt) => OnTick(dt);

    public RetainedWindowState CaptureWindowState()
        => new(Collapsed: !IsExpanded);

    public void RestoreWindowState(RetainedWindowState state)
    {
        if (ExpandedHeight <= CollapsedHeight) return;
        Height = state.Collapsed ? CollapsedHeight : ExpandedHeight;
        for (int i = 0; i < SecondRow.Count; i++)
            SecondRow[i].Visible = !state.Collapsed;
    }
}
