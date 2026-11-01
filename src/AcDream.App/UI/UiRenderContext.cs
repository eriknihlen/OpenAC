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
    public Vector2 ScreenSize { get; private set; }

    /// <summary>
    /// The whole window in interface units: what a popup's unbounded clip
    /// opens up to. It is the screen size until the root says the interface
    /// is drawn scaled; at 50% a 1920-pixel window is 3840 units wide.
    /// </summary>
    internal Vector2 CanvasSize { get; private set; }

    /// <summary>Sets <see cref="CanvasSize"/> for the frame being drawn.</summary>
    internal void SetCanvasSize(Vector2 canvasSize) => CanvasSize = canvasSize;

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
        Begin(screenSize, defaultFont);
    }

    /// <summary>
    /// Points the context at a new frame and clears what the last one left.
    /// One context serves every frame instead of one being built per frame:
    /// its three stacks are built empty and grown as the interface nests, so
    /// a context per frame grew them again from nothing every frame -- fifteen
    /// megabytes per thirty seconds of clip-stack arrays alone at an uncapped
    /// frame rate. A newly constructed context goes through this same reset,
    /// which is what makes a reused one start where a fresh one starts.
    /// </summary>
    public void Begin(Vector2 screenSize, BitmapFont? defaultFont)
    {
        ScreenSize = screenSize;
        CanvasSize = screenSize;
        DefaultFont = defaultFont;
        _stack.Clear();
        _current = default;
        _clipStack.Clear();
        _clip = null;
        _alphaStack.Clear();
        _alpha = 1f;
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

    internal int TransformStackDepth => _stack.Count;

    internal int AlphaStackDepth => _alphaStack.Count;

    public bool CurrentClipIsEmpty => _clip is { } c && c.IsEmpty;

    public void PushClipUnbounded()
    {
        _clipStack.Add(_clip);
        _clip = new UiClipRect(0f, 0f, CanvasSize.X, CanvasSize.Y);
    }

    /// <summary>
    /// Deepens the clip stack by one without changing what is clipped, the way
    /// <c>PushTransform(0, 0)</c> and <c>PushAlpha(1)</c> deepen theirs. Only
    /// use is putting the depth back after a drawing callback popped more than
    /// it pushed and ate a level belonging to its caller. What it deepens with
    /// is whatever the over-popping left current -- a wider rectangle, or none
    /// at all -- so this restores the count, not the cropping.
    /// </summary>
    internal void PushClipUnchanged() => _clipStack.Add(_clip);

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

    /// <summary>
    /// A straight segment from (x0, y0) to (x1, y1), <paramref name="thickness"/>
    /// pixels wide, centred on the segment -- half the width falls on each
    /// side of the line through the two points. The ends are square and flush
    /// with the endpoints; there are no caps and no joins, so a chain of
    /// segments leaves a notch on the outside of a sharp corner.
    /// </summary>
    public void DrawLine(
        float x0, float y0, float x1, float y1, Vector4 color, float thickness = 1f)
    {
        if (thickness <= 0f) return;
        float dx = x1 - x0;
        float dy = y1 - y0;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (!(length > 0f)) return;

        // Step sideways off the segment by half the width. Turning a direction
        // a quarter turn gives (-dy, dx), so that is the sideways direction.
        float half = thickness * 0.5f;
        float nx = -dy / length * half;
        float ny = dx / length * half;

        float ax = _current.X + x0;
        float ay = _current.Y + y0;
        float bx = _current.X + x1;
        float by = _current.Y + y1;

        Span<UiQuadVertex> quad =
        [
            new(ax + nx, ay + ny, 0f, 0f),
            new(bx + nx, by + ny, 0f, 0f),
            new(bx - nx, by - ny, 0f, 0f),
            new(ax - nx, ay - ny, 0f, 0f),
        ];
        EmitConvexQuad(UiTextureTableHandle.None, quad, ApplyAlpha(color));
    }

    /// <summary>
    /// Blits a sub-image into the rectangle (x, y, w, h) after scaling and
    /// rotating that rectangle about a pivot.
    ///
    /// <para><paramref name="pivot"/> is measured in pixels from the
    /// rectangle's own top-left corner, so (0, 0) turns the blit about its
    /// top-left and (w / 2, h / 2) about its middle. Each corner is first
    /// pushed away from (or pulled towards) the pivot by
    /// <paramref name="scale"/>, then swung around the pivot by
    /// <paramref name="rotationRadians"/>, measured clockwise on screen
    /// because the y axis points down.</para>
    ///
    /// <para>A rotation of zero and a scale of one produce exactly the
    /// rectangle <see cref="DrawSprite"/> would have drawn.</para>
    /// </summary>
    public void DrawSpriteTransformed(
        uint texture,
        float x, float y, float w, float h,
        float u0, float v0, float u1, float v1,
        Vector4 tint,
        float rotationRadians,
        Vector2 scale,
        Vector2 pivot)
    {
        if (w == 0f || h == 0f || scale.X == 0f || scale.Y == 0f) return;

        float cos = MathF.Cos(rotationRadians);
        float sin = MathF.Sin(rotationRadians);
        float pivotX = _current.X + x + pivot.X;
        float pivotY = _current.Y + y + pivot.Y;
        float left = _current.X + x;
        float top = _current.Y + y;

        Span<UiQuadVertex> quad =
        [
            Corner(left, top, u0, v0),
            Corner(left + w, top, u1, v0),
            Corner(left + w, top + h, u1, v1),
            Corner(left, top + h, u0, v1),
        ];
        EmitConvexQuad(texture, quad, ApplyAlpha(tint));

        UiQuadVertex Corner(float cx, float cy, float u, float v)
        {
            float ox = (cx - pivotX) * scale.X;
            float oy = (cy - pivotY) * scale.Y;
            return new UiQuadVertex(
                pivotX + ox * cos - oy * sin,
                pivotY + ox * sin + oy * cos,
                u,
                v);
        }
    }

    /// <summary>
    /// Trims a four-cornered shape to the clip in force and hands what is left
    /// to the batcher. The upright path has its own clipper that just moves
    /// the rectangle's edges; once a shape is turned, cutting it can add
    /// corners, so it goes through the polygon clipper instead.
    /// </summary>
    private void EmitConvexQuad(uint texture, ReadOnlySpan<UiQuadVertex> quad, Vector4 color)
    {
        if (_clip is not { } clip)
        {
            TextRenderer.DrawConvexPolygon(texture, quad, color);
            return;
        }
        if (clip.IsEmpty) return;

        Span<UiQuadVertex> clipped =
            stackalloc UiQuadVertex[TransformedQuadClipper.MaxClippedVertices];
        int count = TransformedQuadClipper.Clip(
            clip.Left, clip.Top, clip.Right, clip.Bottom, quad, clipped);
        if (count < 3) return;
        TextRenderer.DrawConvexPolygon(texture, clipped[..count], color);
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
