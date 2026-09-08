using System.Numerics;

namespace AcDream.App.UI;

public class UiNineSlicePanel : UiPanel
{
    public readonly record struct Rect(float X, float Y, float W, float H);

    public readonly record struct FrameRects(
        Rect Center, Rect Top, Rect Bottom, Rect Left, Rect Right,
        Rect TL, Rect TR, Rect BL, Rect BR);

    private readonly System.Func<uint, (uint tex, int w, int h)> _resolve;

    public bool DrawCenterFill { get; set; } = true;

    public bool DrawResizeAffordances { get; set; } = true;

    public UiNineSlicePanel(System.Func<uint, (uint, int, int)> resolve)
    {
        _resolve = resolve;
        BackgroundColor = Vector4.Zero; // suppress the base flat-rect fill
        BorderColor = Vector4.Zero;
        Draggable = true;
        Resizable = true;
        Anchors = AnchorEdges.None;
    }

    public static FrameRects ComputeFrameRects(float w, float h, int b)
    {
        float innerW = w - 2 * b;
        float innerH = h - 2 * b;
        return new FrameRects(
            Center: new Rect(b, b, innerW, innerH),
            Top:    new Rect(b, 0, innerW, b),
            Bottom: new Rect(b, h - b, innerW, b),
            Left:   new Rect(0, b, b, innerH),
            Right:  new Rect(w - b, b, b, innerH),
            TL: new Rect(0, 0, b, b),
            TR: new Rect(w - b, 0, b, b),
            BL: new Rect(0, h - b, b, b),
            BR: new Rect(w - b, h - b, b, b));
    }

    protected override void OnDraw(UiRenderContext ctx)
    {
        var r = ComputeFrameRects(Width, Height, RetailChromeSprites.Border);
        if (DrawCenterFill)
            DrawTiled(ctx, RetailChromeSprites.CenterFill, r.Center);
    }

    protected override void OnDrawAfterChildren(UiRenderContext ctx)
    {
        var r = ComputeFrameRects(Width, Height, RetailChromeSprites.Border);
        DrawTiled(ctx, RetailChromeSprites.TopEdge,    r.Top);
        DrawTiled(ctx, RetailChromeSprites.BottomEdge, r.Bottom);
        DrawTiled(ctx, RetailChromeSprites.LeftEdge,   r.Left);
        DrawTiled(ctx, RetailChromeSprites.RightEdge,  r.Right);
        DrawStretched(ctx, RetailChromeSprites.CornerTL, r.TL);
        DrawStretched(ctx, RetailChromeSprites.CornerTR, r.TR);
        DrawStretched(ctx, RetailChromeSprites.CornerBL, r.BL);
        DrawStretched(ctx, RetailChromeSprites.CornerBR, r.BR);

        if (!DrawResizeAffordances)
            return;

        DrawTiled(ctx, RetailChromeSprites.GripTop,    r.Top);
        DrawTiled(ctx, RetailChromeSprites.GripBottom, r.Bottom);
        DrawTiled(ctx, RetailChromeSprites.GripLeft,   r.Left);
        DrawTiled(ctx, RetailChromeSprites.GripRight,  r.Right);
        DrawStretched(ctx, RetailChromeSprites.GripCorner, r.TL);
        DrawStretched(ctx, RetailChromeSprites.GripCorner, r.TR);
        DrawStretched(ctx, RetailChromeSprites.GripCorner, r.BL);
        DrawStretched(ctx, RetailChromeSprites.GripCorner, r.BR);
    }

    private void DrawTiled(UiRenderContext ctx, uint id, Rect d)
    {
        if (d.W <= 0 || d.H <= 0) return;
        var (tex, tw, th) = _resolve(id);
        if (tex == 0 || tw == 0 || th == 0) return;
        ctx.DrawSprite(tex, d.X, d.Y, d.W, d.H, 0, 0, d.W / tw, d.H / th, Vector4.One);
    }

    private void DrawStretched(UiRenderContext ctx, uint id, Rect d)
    {
        if (d.W <= 0 || d.H <= 0) return;
        var (tex, _, _) = _resolve(id);
        if (tex == 0) return;
        ctx.DrawSprite(tex, d.X, d.Y, d.W, d.H, 0, 0, 1, 1, Vector4.One);
    }
}
