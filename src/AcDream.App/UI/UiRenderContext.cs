using System.Numerics;
using AcDream.App.Rendering;

namespace AcDream.App.UI;

internal readonly record struct UiClipRect(float Left, float Top, float Right, float Bottom)
{
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    public static UiClipRect Intersect(UiClipRect a, UiClipRect b)
        => new(
            MathF.Max(a.Left, b.Left),
            MathF.Max(a.Top, b.Top),
            MathF.Min(a.Right, b.Right),
            MathF.Min(a.Bottom, b.Bottom));

    public static bool TryClipSprite(
        UiClipRect clip,
        ref float x, ref float y, ref float w, ref float h,
        ref float u0, ref float v0, ref float u1, ref float v1)
        => QuadClipper.TryClip(
            clip.Left, clip.Top, clip.Right, clip.Bottom,
            ref x, ref y, ref w, ref h,
            ref u0, ref v0, ref u1, ref v1);
}

public sealed class UiRenderContext
{
    public TextRenderer TextRenderer { get; }
    public BitmapFont? DefaultFont { get; set; }
    public Vector2 ScreenSize { get; }

    private readonly System.Collections.Generic.List<Vector2> _stack = new();
    private Vector2 _current;
    private readonly System.Collections.Generic.List<UiClipRect?> _clipStack = new();
    private UiClipRect? _clip;

    private readonly System.Collections.Generic.List<float> _alphaStack = new();
    private float _alpha = 1f;

    public float AlphaMod => _alpha;

    public void PushAlpha(float a) { _alphaStack.Add(_alpha); _alpha *= a; }

    public void PushAlphaAbsolute(float a) { _alphaStack.Add(_alpha); _alpha = a; }

    public void PopAlpha()
    {
        if (_alphaStack.Count == 0) return;
        _alpha = _alphaStack[^1];
        _alphaStack.RemoveAt(_alphaStack.Count - 1);
    }

    public UiRenderContext(TextRenderer tr, Vector2 screenSize, BitmapFont? defaultFont = null)
    {
        TextRenderer = tr;
        ScreenSize = screenSize;
        DefaultFont = defaultFont;
    }

    public void PushTransform(float dx, float dy)
    {
        _stack.Add(_current);
        _current += new Vector2(dx, dy);
    }

    public void PopTransform()
    {
        if (_stack.Count == 0) return;
        _current = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
    }

    public Vector2 CurrentOrigin => _current;

    /// <summary>Intersect descendant drawing with a local-space viewport.</summary>
    public void PushClip(float x, float y, float w, float h)
    {
        _clipStack.Add(_clip);
        var next = new UiClipRect(
            _current.X + x,
            _current.Y + y,
            _current.X + x + MathF.Max(0f, w),
            _current.Y + y + MathF.Max(0f, h));
        _clip = _clip is { } current
            ? UiClipRect.Intersect(current, next)
            : next;
    }

    public void PopClip()
    {
        if (_clipStack.Count == 0) return;
        _clip = _clipStack[^1];
        _clipStack.RemoveAt(_clipStack.Count - 1);
    }

    internal int ClipStackDepth => _clipStack.Count;

    public bool CurrentClipIsEmpty => _clip is { } c && c.IsEmpty;

    public void PushClipUnbounded()
    {
        _clipStack.Add(_clip);
        _clip = new UiClipRect(0f, 0f, ScreenSize.X, ScreenSize.Y);
    }

    public void BeginOverlayLayer() => TextRenderer.OverlayMode = true;
    public void EndOverlayLayer() => TextRenderer.OverlayMode = false;


    public void DrawRect(float x, float y, float w, float h, Vector4 color) => DrawFill(x, y, w, h, color);

    public void DrawFill(float x, float y, float w, float h, Vector4 color)
    {
        x += _current.X;
        y += _current.Y;
        if (!ClipRect(ref x, ref y, ref w, ref h)) return;
        TextRenderer.DrawFill(x, y, w, h, ApplyAlpha(color));
    }

    public void DrawRectOutline(float x, float y, float w, float h, Vector4 color, float thickness = 1f)
    {
        if (thickness <= 0f || w <= 0f || h <= 0f) return;
        float t = MathF.Min(thickness, MathF.Min(w, h) * 0.5f);
        DrawRect(x, y, w, t, color);
        DrawRect(x, y + h - t, w, t, color);
        DrawRect(x, y + t, t, h - 2f * t, color);
        DrawRect(x + w - t, y + t, t, h - 2f * t, color);
    }

    public void DrawSprite(uint texture, float x, float y, float w, float h,
        float u0, float v0, float u1, float v1, Vector4 tint)
    {
        x += _current.X;
        y += _current.Y;
        DrawSpriteAbsolute(texture, x, y, w, h, u0, v0, u1, v1, tint, applyAlpha: true);
    }

    private void DrawSpriteAbsolute(
        uint texture, float x, float y, float w, float h,
        float u0, float v0, float u1, float v1, Vector4 tint, bool applyAlpha)
    {
        if (_clip is { } clip
            && !UiClipRect.TryClipSprite(
                clip, ref x, ref y, ref w, ref h, ref u0, ref v0, ref u1, ref v1))
            return;
        TextRenderer.DrawSprite(
            texture, x, y, w, h, u0, v0, u1, v1,
            applyAlpha ? ApplyAlpha(tint) : tint);
    }

    private bool ClipRect(ref float x, ref float y, ref float w, ref float h)
    {
        if (_clip is not { } clip)
            return w > 0f && h > 0f;
        float u0 = 0f, v0 = 0f, u1 = 1f, v1 = 1f;
        return UiClipRect.TryClipSprite(
            clip, ref x, ref y, ref w, ref h, ref u0, ref v0, ref u1, ref v1);
    }

    private Vector4 ApplyAlpha(Vector4 c) => _alpha >= 1f ? c : new Vector4(c.X, c.Y, c.Z, c.W * _alpha);

    public void DrawString(string text, float x, float y, Vector4 color, BitmapFont? font = null)
    {
        var f = font ?? DefaultFont;
        if (f is null) return;
        float screenX = _current.X + x;
        float screenY = _current.Y + y;
        Vector4 alphaColor = ApplyAlpha(color);
        if (_clip is { } clip)
        {
            TextRenderer.DrawStringClipped(
                f, text, screenX, screenY, alphaColor,
                clip.Left, clip.Top, clip.Right, clip.Bottom);
            return;
        }
        TextRenderer.DrawString(f, text, screenX, screenY, alphaColor);
    }

    public static readonly Vector4 DefaultOutlineColor = new(0f, 0f, 0f, 1f);

    public static readonly Vector4 StoreOnlyCaptionColor = new(0.5f, 0.5f, 0.5f, 1f);

    public void DrawStringDat(
        UiDatFont font, string text, float x, float y, Vector4 color,
        bool outline = false, Vector4? outlineColor = null)
    {
        if (font is null || string.IsNullOrEmpty(text)) return;

        if (outline)
        {
            DrawStringDatPass(font, text, x, y, outlineColor ?? DefaultOutlineColor, isOutlinePass: true);
        }

        // PASS 1 (or the only pass, when outline is off) — fill, whole string.
        DrawStringDatPass(font, text, x, y, color, isOutlinePass: false);
    }

    public void DrawStringDatPass(
        UiDatFont font, string text, float x, float y, Vector4 tint, bool isOutlinePass)
    {
        if (font is null || string.IsNullOrEmpty(text)) return;

        float originX = _current.X + x;
        float originY = _current.Y + y;

        float baseY = System.MathF.Floor(originY + 0.5f);

        float pen = originX;
        for (int i = 0; i < text.Length; i++)
        {
            if (!font.TryGetGlyph(text[i], out var g))
                continue;

            // Horizontal: snap each glyph's dest X to a whole pixel (the pen keeps its
            // true fractional advance). Vertical: integer baseline + integer per-glyph
            // offset — never an independent per-glyph round (see baseY's note above).
            // Half-up for the same anti-vibration reason as baseY.
            float gx = System.MathF.Floor(pen + g.HorizontalOffsetBefore + 0.5f);
            float gy = baseY + g.VerticalOffsetBefore;
            float gw = g.Width;
            float gh = g.Height;

            if (gw > 0f && gh > 0f)
            {
                if (isOutlinePass)
                    DrawOutlineGlyph(font, g, gx, gy, gw, gh, tint);
                else
                    DrawFillGlyph(font, g, gx, gy, gw, gh, tint);
            }

            pen += UiDatFont.GlyphAdvance(g);
        }
    }

    private void DrawFillGlyph(
        UiDatFont font, DatReaderWriter.Types.FontCharDesc g,
        float gx, float gy, float gw, float gh, Vector4 tint)
    {
        var (fu0, fv0, fu1, fv1) = AtlasUv(
            g.OffsetX, g.OffsetY, g.Width, g.Height,
            font.ForegroundWidth, font.ForegroundHeight);
        DrawSpriteAbsolute(font.ForegroundTexture, gx, gy, gw, gh, fu0, fv0, fu1, fv1, tint, applyAlpha: true);
    }

    private void DrawOutlineGlyph(
        UiDatFont font, DatReaderWriter.Types.FontCharDesc g,
        float gx, float gy, float gw, float gh, Vector4 tint)
    {
        if (font.BackgroundTexture != 0)
        {
            int bx = font.BorderX, by = font.BorderY;
            float ix = gx - bx;
            float iy = gy - by;
            float iw = gw + 2f * bx;
            float ih = gh + 2f * by;

            float srcX = g.OffsetX - bx, srcY = g.OffsetY - by;
            float srcW = iw, srcH = ih;
            float destX = ix, destY = iy, destW = iw, destH = ih;
            if (srcX < 0f) { destX -= srcX; destW += srcX; srcW += srcX; srcX = 0f; }
            if (srcY < 0f) { destY -= srcY; destH += srcY; srcH += srcY; srcY = 0f; }
            if (srcX + srcW > font.BackgroundWidth)
            {
                float over = srcX + srcW - font.BackgroundWidth;
                srcW -= over; destW -= over;
            }
            if (srcY + srcH > font.BackgroundHeight)
            {
                float over = srcY + srcH - font.BackgroundHeight;
                srcH -= over; destH -= over;
            }
            if (srcW <= 0f || srcH <= 0f) return;

            var (bu0, bv0, bu1, bv1) = AtlasUv(
                (int)srcX, (int)srcY, (int)srcW, (int)srcH,
                font.BackgroundWidth, font.BackgroundHeight);
            DrawSpriteAbsolute(font.BackgroundTexture, destX, destY, destW, destH, bu0, bv0, bu1, bv1, tint, applyAlpha: true);
        }
        else
        {
            var (fu0, fv0, fu1, fv1) = AtlasUv(
                g.OffsetX, g.OffsetY, g.Width, g.Height,
                font.ForegroundWidth, font.ForegroundHeight);
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    DrawSpriteAbsolute(
                        font.ForegroundTexture, gx + dx, gy + dy, gw, gh,
                        fu0, fv0, fu1, fv1, tint, applyAlpha: true);
                }
            }
        }
    }

    private static (float u0, float v0, float u1, float v1) AtlasUv(
        int offsetX, int offsetY, int width, int height, int atlasW, int atlasH)
    {
        if (atlasW <= 0 || atlasH <= 0) return (0f, 0f, 0f, 0f);
        float u0 = offsetX / (float)atlasW;
        float v0 = offsetY / (float)atlasH;
        float u1 = (offsetX + width) / (float)atlasW;
        float v1 = (offsetY + height) / (float)atlasH;
        return (u0, v0, u1, v1);
    }
}
