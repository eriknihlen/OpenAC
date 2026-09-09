using System;

namespace AcDream.App.UI.Layout;

public enum RetailWindowChrome
{
    Imported,

    NineSlice,

    /// <summary>Use the shared frame variant with the toolbar's two height stops.</summary>
    CollapsibleNineSlice,
}

public static class RetailWindowFrame
{
    public sealed record Options
    {
        public required string WindowName { get; init; }
        public RetailWindowChrome Chrome { get; init; } = RetailWindowChrome.NineSlice;

        /// <summary>Initial outer-frame position in screen pixels.</summary>
        public float Left { get; init; }
        public float Top { get; init; }

        public float? ContentWidth { get; init; }
        public float? ContentHeight { get; init; }
        public bool RebaseContentLayout { get; init; }

        public ElementInfo? DatConstraintSource { get; init; }

        public bool DatConstraintSourceIsOuterFrame { get; init; }

        public float? MinWidth { get; init; }
        public float? MinHeight { get; init; }
        public float? MaxWidth { get; init; }
        public float? MaxHeight { get; init; }

        public bool Draggable { get; init; } = true;
        public bool Resizable { get; init; } = true;
        public bool ResizeX { get; init; } = true;
        public bool ResizeY { get; init; } = true;
        public ResizeEdges ResizableEdges { get; init; } =
            ResizeEdges.Left | ResizeEdges.Right | ResizeEdges.Top | ResizeEdges.Bottom;
        public bool ConstrainDragToParent { get; init; }
        public bool ConstrainResizeToParent { get; init; }

        public float Opacity { get; init; } = 1f;
        public bool DrawChromeCenter { get; init; } = true;
        public bool Visible { get; init; } = true;

        public int AuthoredGeometryRevision { get; init; }

        public AnchorEdges OuterAnchors { get; init; } = AnchorEdges.None;

        public AnchorEdges ContentAnchors { get; init; } =
            AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Right | AnchorEdges.Bottom;
        public bool? ContentClickThrough { get; init; }
        public IRetainedPanelController? Controller { get; init; }
        public IRetainedWindowStateController? StateController { get; init; }
    }

    public static RetailWindowHandle Mount(
        UiRoot root,
        UiElement content,
        Func<uint, (uint handle, int w, int h)> resolveChrome,
        Options options)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(resolveChrome);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WindowName);

        float contentWidth = options.ContentWidth ?? content.Width;
        float contentHeight = options.ContentHeight ?? content.Height;
        bool wrapped = options.Chrome != RetailWindowChrome.Imported;
        int inset = wrapped ? 2 * RetailChromeSprites.Border : 0;
        float outerWidth = contentWidth + inset;
        float outerHeight = contentHeight + inset;

        UiElement outerFrame;
        if (wrapped)
        {
            outerFrame = options.Chrome switch
            {
                RetailWindowChrome.NineSlice => new UiNineSlicePanel(resolveChrome),
                RetailWindowChrome.CollapsibleNineSlice => new UiCollapsibleFrame(resolveChrome),
                _ => throw new ArgumentOutOfRangeException(nameof(options.Chrome)),
            };

            if (outerFrame is UiNineSlicePanel nineSlice)
                nineSlice.DrawCenterFill = options.DrawChromeCenter;

            const int border = RetailChromeSprites.Border;
            content.Left = border;
            content.Top = border;
            content.Width = contentWidth;
            content.Height = contentHeight;
            content.Anchors = options.ContentAnchors;
            content.Draggable = false;
            content.Resizable = false;
            if (options.ContentClickThrough is { } clickThrough)
                content.ClickThrough = clickThrough;
            if (options.RebaseContentLayout)
                content.RebaseChildLayoutBaselines();
            outerFrame.AddChild(content);
        }
        else
        {
            outerFrame = content;
            content.Width = contentWidth;
            content.Height = contentHeight;
            content.Anchors = AnchorEdges.None;
            if (options.ContentClickThrough is { } clickThrough)
                content.ClickThrough = clickThrough;
            if (options.RebaseContentLayout)
                content.RebaseChildLayoutBaselines();
        }

        outerFrame.Left = options.Left;
        outerFrame.Top = options.Top;
        outerFrame.Width = outerWidth;
        outerFrame.Height = outerHeight;
        outerFrame.Anchors = options.OuterAnchors;
        outerFrame.Draggable = options.Draggable;
        outerFrame.Resizable = options.Resizable;
        outerFrame.ResizeX = options.ResizeX;
        outerFrame.ResizeY = options.ResizeY;
        outerFrame.ResizableEdges = options.ResizableEdges;
        outerFrame.ConstrainDragToParent = options.ConstrainDragToParent;
        outerFrame.ConstrainResizeToParent = options.ConstrainResizeToParent;
        outerFrame.Opacity = Math.Clamp(options.Opacity, 0f, 1f);
        outerFrame.Visible = options.Visible;

        int constraintInset = options.DatConstraintSourceIsOuterFrame ? 0 : inset;
        outerFrame.MinWidth = ResolveConstraint(
            options.MinWidth, options.DatConstraintSource, 0x3Fu, outerWidth, constraintInset);
        outerFrame.MinHeight = ResolveConstraint(
            options.MinHeight, options.DatConstraintSource, 0x3Eu, outerHeight, constraintInset);
        outerFrame.MaxWidth = Math.Max(
            outerFrame.MinWidth,
            ResolveConstraint(options.MaxWidth, options.DatConstraintSource, 0x3Du, float.MaxValue, constraintInset));
        outerFrame.MaxHeight = Math.Max(
            outerFrame.MinHeight,
            ResolveConstraint(options.MaxHeight, options.DatConstraintSource, 0x3Cu, float.MaxValue, constraintInset));

        if (outerWidth < outerFrame.MinWidth || outerWidth > outerFrame.MaxWidth)
            throw new InvalidOperationException(
                $"RetailWindowFrame.Mount(\"{options.WindowName}\"): mounted outer width " +
                $"{outerWidth} is outside its own clamp [{outerFrame.MinWidth}, {outerFrame.MaxWidth}] " +
                "— the window would open already violating its authored resize bounds.");
        if (outerHeight < outerFrame.MinHeight || outerHeight > outerFrame.MaxHeight)
            throw new InvalidOperationException(
                $"RetailWindowFrame.Mount(\"{options.WindowName}\"): mounted outer height " +
                $"{outerHeight} is outside its own clamp [{outerFrame.MinHeight}, {outerFrame.MaxHeight}] " +
                "— the window would open already violating its authored resize bounds.");

        if (wrapped)
            content.ApplyAnchor(outerWidth, outerHeight);

        root.AddChild(outerFrame);
        return root.RegisterWindow(
            options.WindowName,
            outerFrame,
            content,
            options.Controller,
            options.StateController,
            options.AuthoredGeometryRevision);
    }

    private static float ResolveConstraint(
        float? explicitValue,
        ElementInfo? datSource,
        uint propertyId,
        float fallback,
        int chromeInset)
    {
        if (explicitValue is { } value)
            return value;
        if (datSource is not null
            && datSource.TryGetEffectiveInteger(propertyId, out int datValue)
            && datValue >= 0)
            return datValue + chromeInset;
        return fallback;
    }
}
