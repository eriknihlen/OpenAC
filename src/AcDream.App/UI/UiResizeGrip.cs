using System;
using System.Numerics;
using AcDream.App.UI.Layout;

namespace AcDream.App.UI;

public sealed class UiResizeGrip : UiElement
{
    private readonly ElementInfo? _info;
    private readonly Func<uint, (uint tex, int w, int h)>? _resolve;

    /// <summary>Synthetic grip with no authored media — used by unit tests that
    /// exercise only the resize-drag hit-testing/edge behavior.</summary>
    public UiResizeGrip()
    {
    }

    public UiResizeGrip(ElementInfo info, Func<uint, (uint tex, int w, int h)> resolve)
    {
        _info = info;
        _resolve = resolve;
    }

    public uint SpriteFile => _info is not null && _info.StateMedia.TryGetValue("", out var media)
        ? media.File
        : 0u;

    protected override void OnDraw(UiRenderContext ctx)
    {
        if (_resolve is null) return;
        uint file = SpriteFile;
        if (file == 0u) return;
        var (tex, tw, th) = _resolve(file);
        if (tex == 0 || tw == 0 || th == 0) return;
        ctx.DrawSprite(tex, 0, 0, Width, Height, 0, 0, Width / tw, Height / th, Vector4.One);
    }

    public enum Border
    {
        None = 0,
        UpperLeft = 1,
        Top = 2,
        UpperRight = 3,
        Right = 4,
        LowerRight = 5,
        Bottom = 6,
        LowerLeft = 7,
        Left = 8,
    }

    public Border BorderLocation { get; set; }

    public static Border DecodeBorderLocation(bool bottom, bool left, bool right, bool top)
    {
        if (right) return top ? Border.UpperRight : bottom ? Border.LowerRight : Border.Right;
        if (left) return top ? Border.UpperLeft : bottom ? Border.LowerLeft : Border.Left;
        if (top) return Border.Top;
        if (bottom) return Border.Bottom;
        return Border.None;
    }

    public ResizeEdges Edges => BorderLocation switch
    {
        Border.UpperLeft => ResizeEdges.Left | ResizeEdges.Top,
        Border.Top => ResizeEdges.Top,
        Border.UpperRight => ResizeEdges.Right | ResizeEdges.Top,
        Border.Right => ResizeEdges.Right,
        Border.LowerRight => ResizeEdges.Right | ResizeEdges.Bottom,
        Border.Bottom => ResizeEdges.Bottom,
        Border.LowerLeft => ResizeEdges.Left | ResizeEdges.Bottom,
        Border.Left => ResizeEdges.Left,
        _ => ResizeEdges.None,
    };
}
