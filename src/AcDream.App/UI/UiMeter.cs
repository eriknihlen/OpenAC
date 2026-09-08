using System.Numerics;

namespace AcDream.App.UI;

public enum UiMeterLabelAlign : byte { Left = 0, Center = 1, Right = 2 }

internal readonly record struct UiMeterDetailOverlaySpec(
    uint Sprite,
    float X, float Y, float W, float H,
    uint LeftMode, uint TopMode, uint RightMode, uint BottomMode,
    float ParentW, float ParentH);

public sealed class UiMeter : UiElement, IUiDatStateful
{
    private readonly Dictionary<uint, uint> _stateFillSprites = new();
    private readonly Dictionary<uint, (string Text, UiMeterLabelAlign Align)> _stateLabels = new();
    private (string Text, UiMeterLabelAlign Align)? _activeStateLabel;

    private bool _detailConfigured;
    private bool _detailPassToChildren;
    private UiMeterDetailOverlaySpec _detailBack;
    private UiMeterDetailOverlaySpec _detailFront;

    /// <summary>True when this meter absorbed the vitals detail-icon overlays. Exposed for tests.</summary>
    internal bool HasDetailOverlay => _detailConfigured;
    internal uint DetailBackSprite => _detailBack.Sprite;
    internal uint DetailFrontSprite => _detailFront.Sprite;
    /// <summary>The back overlay's authored meter-local rect. Exposed for tests.</summary>
    internal (float X, float Y, float W, float H) DetailBackRect =>
        (_detailBack.X, _detailBack.Y, _detailBack.W, _detailBack.H);
    internal UiMeterDetailOverlaySpec DetailBack => _detailBack;
    internal UiMeterDetailOverlaySpec DetailFront => _detailFront;

    public uint ElementId { get; set; }

    /// <summary>Fill fraction provider; a null result draws an empty bar.</summary>
    public Func<float?> Fill { get; set; } = () => 0f;
    public Func<string?> Label { get; set; } = () => null;
    public Vector4 BarColor { get; set; } = new(1f, 0f, 0f, 1f);
    public Vector4 BgColor { get; set; } = new(0f, 0f, 0f, 0.5f);
    public Vector4 LabelColor { get; set; } = new(1f, 1f, 1f, 1f);

    public bool Outline { get; set; }

    public Vector4 OutlineColor { get; set; } = UiRenderContext.DefaultOutlineColor;

    public UiDatFont? DatFont { get; set; }

    public Func<uint, (uint tex, int w, int h)>? SpriteResolve { get; set; }

    public uint BackLeft { get; set; }
    /// <summary>Empty-track middle (tiled gradient) RenderSurface id.</summary>
    public uint BackTile { get; set; }
    /// <summary>Empty-track right-cap RenderSurface id.</summary>
    public uint BackRight { get; set; }
    public uint FrontLeft { get; set; }
    public uint FrontTile { get; set; }
    public uint FrontRight { get; set; }

    /// <summary>The active numeric DAT state for a stateful fill meter.</summary>
    public uint ActiveRetailStateId { get; private set; }

    internal string? ActiveStateLabel => _activeStateLabel?.Text;

    internal UiMeterLabelAlign? ActiveStateLabelAlign => _activeStateLabel?.Align;

    internal void ConfigureStateFill(uint stateId, uint spriteId)
    {
        if (stateId != 0 && spriteId != 0)
            _stateFillSprites[stateId] = spriteId;
    }

    internal void ConfigureStateLabel(uint stateId, string text, UiMeterLabelAlign align)
    {
        if (stateId != 0 && !string.IsNullOrEmpty(text))
            _stateLabels[stateId] = (text, align);
    }

    internal void ConfigureDetailOverlay(
        in UiMeterDetailOverlaySpec back,
        in UiMeterDetailOverlaySpec front,
        bool passToChildren)
    {
        _detailBack = back;
        _detailFront = front;
        _detailConfigured = back.Sprite != 0 || front.Sprite != 0;
        _detailPassToChildren = passToChildren;
    }

    internal static (float X, float Y, float W, float H) ComputeDetailOverlayRect(
        in UiMeterDetailOverlaySpec overlay, float meterW, float meterH)
    {
        int parentW = (int)meterW, parentH = (int)meterH;
        int origParentW = (int)overlay.ParentW, origParentH = (int)overlay.ParentH;
        if (origParentW <= 0 || origParentH <= 0
            || (parentW == origParentW && parentH == origParentH))
        {
            return (overlay.X, overlay.Y, overlay.W, overlay.H);
        }

        var originalChild = UiPixelRect.FromPositionAndSize(
            (int)overlay.X, (int)overlay.Y, (int)overlay.W, (int)overlay.H);
        var originalParent = UiPixelRect.FromPositionAndSize(
            0, 0, origParentW, origParentH);
        var currentParent = UiPixelRect.FromPositionAndSize(
            0, 0, parentW, parentH);
        UiPixelRect effective = UiLayoutPolicy.Apply(
            overlay.LeftMode,
            overlay.TopMode,
            overlay.RightMode,
            overlay.BottomMode,
            originalChild,
            originalParent,
            originalChild,
            currentParent);
        return (effective.X0, effective.Y0, effective.Width, effective.Height);
    }

    public bool TrySetRetailState(uint stateId)
    {
        if (_detailConfigured
            && stateId is RetailUiStateIds.HideDetail or RetailUiStateIds.ShowDetail)
        {
            ActiveRetailStateId = stateId;
            if (_detailPassToChildren)
                foreach (UiElement child in Children)
                    if (child is IUiDatStateful stateful)
                        stateful.TrySetRetailState(stateId);
            return true;
        }

        bool hasFill = _stateFillSprites.TryGetValue(stateId, out uint spriteId);
        bool hasLabel = _stateLabels.TryGetValue(stateId, out var caption);
        if (!hasFill && !hasLabel)
            return false;

        ActiveRetailStateId = stateId;
        if (hasFill)
        {
            FrontLeft = 0;
            FrontTile = spriteId;
            FrontRight = 0;
        }
        _activeStateLabel = hasLabel ? caption : null;
        return true;
    }

    public bool Vertical { get; set; }

    public bool FillFromBottom { get; set; } = true;

    public UiMeter() { ClickThrough = true; }

    public override bool ConsumesDatChildren => true;

    private static float AlignX(UiMeterLabelAlign align, float width, float textWidth)
        => align switch
        {
            UiMeterLabelAlign.Left => 0f,
            UiMeterLabelAlign.Right => width - textWidth,
            _ => (width - textWidth) * 0.5f,
        };

    public static (float x, float y, float w, float h) ComputeFillRect(
        float pct, float w, float h)
    {
        if (pct < 0f) pct = 0f;
        if (pct > 1f) pct = 1f;
        return (0f, 0f, w * pct, h);
    }

    public static (float x, float y, float w, float h) ComputeVFillRect(
        float pct, float w, float h, bool fromBottom)
    {
        if (pct < 0f) pct = 0f;
        if (pct > 1f) pct = 1f;
        float fh = h * pct;
        return (0f, fromBottom ? h - fh : 0f, w, fh);
    }

    protected override void OnDraw(UiRenderContext ctx)
    {
        float? pct = Fill();
        float p = pct is float pf ? (pf < 0f ? 0f : pf > 1f ? 1f : pf) : 0f;

        if (SpriteResolve is { } resolve && (BackLeft != 0 || BackTile != 0 || FrontTile != 0))
        {
            if (Vertical)
            {
                DrawVBar(ctx, resolve, BackTile, Height, fromBottom: FillFromBottom, isFill: false);
                if (pct is not null && p > 0f)
                    DrawVBar(ctx, resolve, FrontTile, Height * p, fromBottom: FillFromBottom, isFill: true);
            }
            else
            {
                bool detail = _detailConfigured
                    && ActiveRetailStateId == RetailUiStateIds.ShowDetail;
                DrawHBar(ctx, resolve, BackLeft, BackTile, BackRight, Width);
                if (detail)
                {
                    DrawDetailIcon(
                        ctx, resolve, _detailBack.Sprite,
                        ComputeDetailOverlayRect(in _detailBack, Width, Height),
                        Width);
                }
                if (pct is not null && p > 0f)
                {
                    DrawHBar(ctx, resolve, FrontLeft, FrontTile, FrontRight, Width * p);
                    if (detail)
                    {
                        DrawDetailIcon(
                            ctx, resolve, _detailFront.Sprite,
                            ComputeDetailOverlayRect(in _detailFront, Width, Height),
                            Width * p);
                    }
                }
            }
        }
        else
        {
            ctx.DrawRect(0, 0, Width, Height, BgColor);
            if (pct is not null && p > 0f)
            {
                var (fx, fy, fw, fh) = ComputeFillRect(p, Width, Height);
                if (fw > 0f) ctx.DrawRect(fx, fy, fw, fh, BarColor);
            }
        }

        string? label = Label();
        UiMeterLabelAlign align = UiMeterLabelAlign.Center;
        if (string.IsNullOrEmpty(label) && _activeStateLabel is { } stateLabel)
            (label, align) = stateLabel;
        if (!string.IsNullOrEmpty(label))
        {
            if (DatFont is { } datFont)
            {
                float tw = datFont.MeasureWidth(label);
                float tx = AlignX(align, Width, tw);
                float ty = (Height - datFont.LineHeight) * 0.5f;
                ctx.DrawStringDat(datFont, label, tx, ty, LabelColor, Outline, OutlineColor);
            }
            else if (ctx.DefaultFont is { } font)
            {
                // Fallback: debug bitmap font (no dat font available).
                float tw = font.MeasureWidth(label);
                float tx = AlignX(align, Width, tw);
                float ty = (Height - font.LineHeight) * 0.5f;
                ctx.DrawString(label, tx, ty, LabelColor);
            }
        }
    }

    private void DrawHBar(
        UiRenderContext ctx, Func<uint, (uint tex, int w, int h)> resolve,
        uint leftId, uint midId, uint rightId, float clipW)
    {
        if (clipW <= 0f) return;
        float w = Width, h = Height;
        var (lt, lw, _) = leftId  != 0 ? resolve(leftId)  : (0u, 0, 0);
        var (mt, mw, _) = midId   != 0 ? resolve(midId)   : (0u, 0, 0);
        var (rt, rw, _) = rightId != 0 ? resolve(rightId) : (0u, 0, 0);

        float capL = lt != 0 ? MathF.Min(lw, w) : 0f;
        float capR = rt != 0 ? MathF.Min(rw, w - capL) : 0f;
        float midW = w - capL - capR;

        DrawPiece(ctx, lt, 0f,       capL, lw, h, clipW);
        DrawPiece(ctx, mt, capL,     midW, mw, h, clipW);
        DrawPiece(ctx, rt, w - capR, capR, rw, h, clipW);
    }

    private void DrawVBar(UiRenderContext ctx, Func<uint, (uint tex, int w, int h)> resolve,
        uint tileId, float visibleH, bool fromBottom, bool isFill)
    {
        if (tileId == 0 || visibleH <= 0f) return;
        var (tex, _, _) = resolve(tileId);
        if (tex == 0) return;
        float w = Width, h = Height;
        if (visibleH > h) visibleH = h;
        float frac = h > 0f ? visibleH / h : 0f;
        // Bottom-up fill: bottom of the rect AND bottom of the sprite (v in [1-frac, 1]).
        // Top-down / track: top of the rect AND top of the sprite (v in [0, frac]).
        float y  = isFill && fromBottom ? h - visibleH : 0f;
        float v0 = isFill && fromBottom ? 1f - frac : 0f;
        float v1 = isFill && fromBottom ? 1f : frac;
        ctx.DrawSprite(tex, 0f, y, w, visibleH, 0f, v0, 1f, v1, System.Numerics.Vector4.One);
    }

    private void DrawDetailIcon(
        UiRenderContext ctx, Func<uint, (uint tex, int w, int h)> resolve,
        uint spriteId, (float X, float Y, float W, float H) rect, float clipW)
    {
        if (spriteId == 0 || rect.W <= 0f || rect.H <= 0f) return;
        var (tex, _, _) = resolve(spriteId);
        if (tex == 0) return;
        float visibleW = MathF.Min(rect.W, clipW - rect.X);
        if (visibleW <= 0f) return;
        float h = MathF.Min(rect.H, Height - rect.Y);
        if (h <= 0f) return;
        float u1 = visibleW / rect.W;
        ctx.DrawSprite(tex, rect.X, rect.Y, visibleW, h, 0f, 0f, u1, 1f, Vector4.One);
    }

    private static void DrawPiece(
        UiRenderContext ctx, uint tex, float pieceX, float pieceW, float nativeW, float h, float clipW)
    {
        if (tex == 0 || pieceW <= 0f || nativeW <= 0f) return;
        float visibleW = MathF.Min(pieceW, clipW - pieceX);
        if (visibleW <= 0f) return;
        float u1 = visibleW / nativeW;
        ctx.DrawSprite(tex, pieceX, 0f, visibleW, h, 0f, 0f, u1, 1f, Vector4.One);
    }
}
