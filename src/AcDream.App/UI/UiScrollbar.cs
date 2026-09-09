using System;
using System.Numerics;

namespace AcDream.App.UI;

public sealed class UiScrollbar : UiElement
{
    public override bool ReceivesHoverMouseMove => true;

    /// <summary>The scroll model this bar reflects + drives (shared with the transcript).</summary>
    public UiScrollable? Model { get; set; }

    public float ScalarPosition { get; private set; }
    public Action<float>? ScalarChanged { get; set; }
    public Func<float?>? ScalarPositionSource { get; set; }
    public bool Horizontal { get; set; }

    public bool RetailArt { get; set; } = true;

    public Vector4 PlainTrackColor { get; set; } = new(0f, 0f, 0f, 0.6f);
    public Vector4 PlainBorderColor { get; set; } = new(0.46f, 0.37f, 0.16f, 1f);
    public Vector4 PlainNubColor { get; set; } = new(0.72f, 0.62f, 0.34f, 1f);

    public bool IsDragging => _draggingThumb;

    public Action? DragCompleted { get; set; }

    public Func<float?> ScalarFill { get; set; } = () => null;
    public uint ScalarFillSprite { get; set; }
    public uint ScalarRangeSprite { get; set; }
    public float ScalarRangeLeft { get; set; }
    public float ScalarRangeWidth { get; set; } = float.PositiveInfinity;
    public UiLayoutPolicy? ScalarRangeLayoutPolicy { get; set; }
    public bool ScalarFillFromRight { get; set; }

    public void SetScalarPosition(float position)
        => ScalarPosition = Math.Clamp(position, 0f, 1f);

    protected override void OnTick(double deltaSeconds)
    {
        base.OnTick(deltaSeconds);
        if (!_draggingThumb && ScalarPositionSource?.Invoke() is { } value)
            SetScalarPosition(value);
    }

    public string? TooltipText { get; set; }

    /// <inheritdoc />
    public override string? GetTooltipText() =>
        string.IsNullOrWhiteSpace(TooltipText)
            ? base.GetTooltipText()
            : TooltipText;

    /// <summary>RenderSurface id → (GL tex, w, h). 0 id = skip.</summary>
    public Func<uint, (uint tex, int w, int h)>? SpriteResolve { get; set; }

    public uint TrackSprite { get; set; }

    public uint ThumbSprite { get; set; }

    public uint ThumbTopSprite { get; set; }

    public uint ThumbBotSprite { get; set; }

    public uint ThumbRolloverSprite { get; set; }
    public uint ThumbPressedSprite { get; set; }
    public uint ThumbTopRolloverSprite { get; set; }
    public uint ThumbTopPressedSprite { get; set; }
    public uint ThumbBotRolloverSprite { get; set; }
    public uint ThumbBotPressedSprite { get; set; }

    public uint UpSprite { get; set; }

    public uint DownSprite { get; set; }

    /// <summary>Rollover and pressed media for the start/decrement button.</summary>
    public uint UpRolloverSprite { get; set; }
    public uint UpPressedSprite { get; set; }

    /// <summary>Rollover and pressed media for the end/increment button.</summary>
    public uint DownRolloverSprite { get; set; }
    public uint DownPressedSprite { get; set; }

    public float DecrementButtonExtent { get; set; } = 16f;

    /// <summary>Authored extent of the increment button along the scrollbar axis.</summary>
    public float IncrementButtonExtent { get; set; } = 16f;

    public bool HideWhenDisabled { get; set; }

    private const float MinThumb = 8f;

    private const float CapH = 3f;

    private bool _draggingThumb;
    private bool _hoveredThumb;
    private float _dragOffsetY;
    private float _dragOffsetX;
    private EndButton _hoveredButton;
    private EndButton _pressedButton;

    private enum EndButton
    {
        None,
        Decrement,
        Increment,
    }

    public UiScrollbar() { CapturesPointerDrag = true; }

    public override bool ConsumesDatChildren => true;

    internal bool IsModelDisabled
        => ScalarChanged is null && Model is { HasOverflow: false };

    internal bool IsPresentationVisible => !HideWhenDisabled || !IsModelDisabled;

    protected override bool OnHitTest(float localX, float localY)
        => IsPresentationVisible && base.OnHitTest(localX, localY);

    public static (float y, float h) ThumbRect(UiScrollable m, float trackTop, float trackLen)
    {
        float h = MathF.Max(MinThumb, trackLen * m.ThumbRatio);
        float travel = trackLen - h;
        float y = trackTop + travel * m.PositionRatio;
        return (y, h);
    }

    public static (float x, float width) ScalarFillRect(
        float totalWidth, float fill, bool fromRight)
        => ScalarFillRect(0f, totalWidth, fill, fromRight);

    public static (float x, float width) ScalarFillRect(
        float rangeLeft, float rangeWidth, float fill, bool fromRight)
    {
        float safeWidth = MathF.Max(0f, rangeWidth);
        float visibleWidth = safeWidth * Math.Clamp(fill, 0f, 1f);
        return (fromRight ? rangeLeft + safeWidth - visibleWidth : rangeLeft, visibleWidth);
    }

    protected override void OnDraw(UiRenderContext ctx)
    {
        if (!IsPresentationVisible) return;
        if (!RetailArt)
        {
            DrawPlainScalar(ctx);
            return;
        }
        if (SpriteResolve is not { } resolve) return;
        if (Horizontal)
        {
            if (ScalarChanged is null && Model is { } horizontalModel)
            {
                DrawHorizontalModel(ctx, resolve, horizontalModel);
                return;
            }
            DrawTiled(ctx, resolve, TrackSprite, 0f, 0f, Width, Height);
            (float rangeLeft, float rangeWidth) = ScalarRangeRect();
            if (ScalarRangeSprite != 0)
                DrawTiled(ctx, resolve, ScalarRangeSprite,
                    rangeLeft, 0f, rangeWidth, Height);
            float thumbWidth = ScalarThumbWidth(resolve);
            if (ScalarFill() is float fill && ScalarFillSprite != 0)
            {
                var (fillX, visibleWidth) = ScalarFillRect(
                    Width, fill, ScalarFillFromRight);
                DrawTiledClipped(ctx, resolve, ScalarFillSprite,
                    0f, fillX, visibleWidth, Height);
            }
            float travel = MathF.Max(0f, Width - thumbWidth);
            float x = travel * ScalarPosition;
            DrawSprite(ctx, resolve, ActiveThumbSprite, x, 0f, thumbWidth, Height);
            return;
        }

        if (ScalarChanged is not null)
        {
            DrawVerticalScalar(ctx, resolve);
            return;
        }

        if (Model is not { } m) return;

        DrawTiled(ctx, resolve, TrackSprite, 0f, 0f, Width, Height);

        float decrementExtent = AxisExtent(DecrementButtonExtent, Height);
        float incrementExtent = AxisExtent(IncrementButtonExtent, Height);

        // Decrement/up and increment/down use their authored button heights.
        DrawSprite(ctx, resolve, ActiveStartSprite, 0f, 0f, Width, decrementExtent);
        DrawSprite(ctx, resolve, ActiveEndSprite,
            0f, Height - incrementExtent, Width, incrementExtent);

        {
            float trackTop = decrementExtent;
            float trackLen = MathF.Max(0f, Height - decrementExtent - incrementExtent);
            var (ty, th) = ThumbRect(m, trackTop, trackLen);
            if (ThumbTopSprite != 0 && ThumbBotSprite != 0 && th >= 2f * CapH)
            {
                DrawSprite(ctx, resolve, ActiveThumbTopSprite, 0f, ty, Width, CapH);
                DrawTiled(ctx, resolve, ActiveThumbSprite, 0f, ty + CapH, Width, th - 2f * CapH);
                DrawSprite(ctx, resolve, ActiveThumbBotSprite, 0f, ty + th - CapH, Width, CapH);
            }
            else
            {
                DrawThumbMarker(ctx, resolve, ActiveThumbSprite, 0f, ty, Width, th, vertical: true);
            }
        }
    }

    private uint ActiveThumb(uint normal, uint rollover, uint pressed)
        => _draggingThumb && pressed != 0u
            ? pressed
            : _hoveredThumb && !_draggingThumb && rollover != 0u
                ? rollover
                : normal;

    private uint ActiveThumbSprite => ActiveThumb(ThumbSprite, ThumbRolloverSprite, ThumbPressedSprite);
    private uint ActiveThumbTopSprite => ActiveThumb(ThumbTopSprite, ThumbTopRolloverSprite, ThumbTopPressedSprite);
    private uint ActiveThumbBotSprite => ActiveThumb(ThumbBotSprite, ThumbBotRolloverSprite, ThumbBotPressedSprite);

    private void DrawThumbMarker(
        UiRenderContext ctx, Func<uint, (uint tex, int w, int h)> resolve,
        uint id, float rectX, float rectY, float rectW, float rectH, bool vertical)
    {
        if (id == 0 || rectW <= 0f || rectH <= 0f) return;
        var (tex, nativeW, nativeH) = resolve(id);
        if (tex == 0 || nativeW == 0 || nativeH == 0) return;

        if (vertical)
        {
            float drawH = MathF.Min(nativeH, rectH);
            float y = rectY + (rectH - drawH) * 0.5f;
            ctx.DrawSprite(tex, rectX, y, rectW, drawH, 0f, 0f, rectW / nativeW, drawH / nativeH, Vector4.One);
        }
        else
        {
            float drawW = MathF.Min(nativeW, rectW);
            float x = rectX + (rectW - drawW) * 0.5f;
            ctx.DrawSprite(tex, x, rectY, drawW, rectH, 0f, 0f, drawW / nativeW, rectH / nativeH, Vector4.One);
        }
    }

    private void DrawHorizontalModel(
        UiRenderContext ctx,
        Func<uint, (uint tex, int w, int h)> resolve,
        UiScrollable model)
    {
        float decrementExtent = AxisExtent(DecrementButtonExtent, Width);
        float incrementExtent = AxisExtent(IncrementButtonExtent, Width);
        DrawTiled(ctx, resolve, TrackSprite, 0f, 0f, Width, Height);
        DrawSprite(ctx, resolve, ActiveStartSprite, 0f, 0f, decrementExtent, Height);
        DrawSprite(ctx, resolve, ActiveEndSprite,
            Width - incrementExtent, 0f, incrementExtent, Height);

        float trackLeft = decrementExtent;
        float trackLength = MathF.Max(0f, Width - decrementExtent - incrementExtent);
        var (tx, tw) = ThumbRect(model, trackLeft, trackLength);
        if (ThumbTopSprite != 0 && ThumbBotSprite != 0 && tw >= 2f * CapH)
        {
            DrawSprite(ctx, resolve, ActiveThumbTopSprite, tx, 0f, CapH, Height);
            DrawTiled(ctx, resolve, ActiveThumbSprite, tx + CapH, 0f, tw - 2f * CapH, Height);
            DrawSprite(ctx, resolve, ActiveThumbBotSprite, tx + tw - CapH, 0f, CapH, Height);
        }
        else
        {
            DrawThumbMarker(ctx, resolve, ActiveThumbSprite, tx, 0f, tw, Height, vertical: false);
        }
    }

    private void DrawVerticalScalar(
        UiRenderContext ctx, Func<uint, (uint tex, int w, int h)> resolve)
    {
        DrawTiled(ctx, resolve, TrackSprite, 0f, 0f, Width, Height);
        float thumbHeight = ScalarThumbExtent(resolve, Height);
        float travel = MathF.Max(0f, Height - thumbHeight);
        float y = travel * ScalarPosition;
        DrawSprite(ctx, resolve, ActiveThumbSprite, 0f, y, Width, thumbHeight);
    }

    private void DrawPlainScalar(UiRenderContext ctx)
    {
        ctx.DrawFill(0f, 0f, Width, Height, PlainTrackColor);
        ctx.DrawRectOutline(0f, 0f, Width, Height, PlainBorderColor, 1f);

        if (Horizontal)
        {
            float nubWidth = MathF.Min(6f, Width);
            float travel = MathF.Max(0f, Width - nubWidth);
            float x = travel * ScalarPosition;
            ctx.DrawFill(x, 0f, nubWidth, Height, PlainNubColor);
        }
        else
        {
            float nubHeight = MathF.Min(6f, Height);
            float travel = MathF.Max(0f, Height - nubHeight);
            float y = travel * ScalarPosition;
            ctx.DrawFill(0f, y, Width, nubHeight, PlainNubColor);
        }
    }

    /// <summary>Draw a sprite stretched 1:1 to the dest rect.</summary>
    private void DrawSprite(UiRenderContext ctx, Func<uint, (uint tex, int w, int h)> resolve,
        uint id, float x, float y, float w, float h)
    {
        if (id == 0 || w <= 0f || h <= 0f) return;
        var (tex, _, _) = resolve(id);
        if (tex == 0) return;
        ctx.DrawSprite(tex, x, y, w, h, 0f, 0f, 1f, 1f, Vector4.One);
    }

    /// <summary>Draw a sprite TILED to fill the dest rect (UV-repeat at native size on
    /// both axes — the UI texture is GL_REPEAT-wrapped). A native-width axis gives 1:1.</summary>
    private void DrawTiled(UiRenderContext ctx, Func<uint, (uint tex, int w, int h)> resolve,
        uint id, float x, float y, float w, float h)
    {
        if (id == 0 || w <= 0f || h <= 0f) return;
        var (tex, tw, th) = resolve(id);
        if (tex == 0 || tw == 0 || th == 0) return;
        ctx.DrawSprite(tex, x, y, w, h, 0f, 0f, w / tw, h / th, Vector4.One);
    }

    private void DrawTiledClipped(
        UiRenderContext ctx,
        Func<uint, (uint tex, int w, int h)> resolve,
        uint id,
        float rangeLeft,
        float x,
        float w,
        float h)
    {
        if (id == 0 || w <= 0f || h <= 0f) return;
        var (tex, tw, th) = resolve(id);
        if (tex == 0 || tw == 0 || th == 0) return;
        float u0 = (x - rangeLeft) / tw;
        float u1 = u0 + w / tw;
        ctx.DrawSprite(tex, x, 0f, w, h, u0, 0f, u1, h / th, Vector4.One);
    }

    internal (float left, float width) ScalarRangeRect()
    {
        float configuredLeft = ScalarRangeLeft;
        float configuredWidth = ScalarRangeWidth;
        if (ScalarRangeLayoutPolicy is { } policy)
        {
            UiPixelRect currentParent = UiPixelRect.FromPositionAndSize(
                0, 0, (int)Width, (int)Height);
            UiPixelRect range = policy.Apply(policy.OriginalChild, currentParent);
            configuredLeft = range.X0;
            configuredWidth = range.Width;
        }
        float left = Math.Clamp(configuredLeft, 0f, Width);
        float requestedWidth = float.IsPositiveInfinity(configuredWidth)
            ? Width - left
            : MathF.Max(0f, configuredWidth);
        float width = Math.Clamp(requestedWidth, 0f, Width - left);
        return (left, width);
    }

    public override bool OnEvent(in UiEvent e)
    {
        if (e.Type == UiEventType.CaptureChanged)
        {
            bool wasDragging = _draggingThumb;
            _draggingThumb = false;
            _pressedButton = EndButton.None;
            if (wasDragging) DragCompleted?.Invoke();
            return false;
        }

        if (!IsPresentationVisible)
        {
            _draggingThumb = false;
            _hoveredThumb = false;
            _hoveredButton = EndButton.None;
            _pressedButton = EndButton.None;
            return false;
        }

        if (e.Type == UiEventType.HoverEnter)
        {
            _hoveredButton = ButtonAt(e.Data1, e.Data2);
            _hoveredThumb = ThumbAt(e.Data1, e.Data2);
            return true;
        }
        if (e.Type == UiEventType.HoverLeave)
        {
            _hoveredButton = EndButton.None;
            _hoveredThumb = false;
            return true;
        }
        if (e.Type == UiEventType.MouseMove)
        {
            _hoveredButton = ButtonAt(e.Data1, e.Data2);
            _hoveredThumb = ThumbAt(e.Data1, e.Data2);
        }

        if (ScalarChanged is not null)
            return Horizontal ? OnScalarEvent(e) : OnVerticalScalarEvent(e);

        if (Horizontal && Model is not null)
            return OnHorizontalModelEvent(e);

        if (Model is not { } m) return false;

        switch (e.Type)
        {
            case UiEventType.MouseDown:
            {
                float ly = e.Data2;
                _pressedButton = ButtonAt(e.Data1, e.Data2);
                float decrementExtent = AxisExtent(DecrementButtonExtent, Height);
                float incrementExtent = AxisExtent(IncrementButtonExtent, Height);

                // Up-button region: authored top rows.
                if (ly < decrementExtent) { m.ScrollByLines(-1); return true; }

                // Down-button region: authored bottom rows.
                if (ly >= Height - incrementExtent) { m.ScrollByLines(1); return true; }

                // Track interior: start a thumb drag or page-scroll.
                float trackTop = decrementExtent;
                float trackLen = MathF.Max(0f, Height - decrementExtent - incrementExtent);
                var (ty, th) = ThumbRect(m, trackTop, trackLen);

                if (ly >= ty && ly <= ty + th)
                {
                    _draggingThumb = true;
                    _dragOffsetY = ly - ty;
                }
                else
                {
                    m.ScrollByPage(ly < ty ? -1 : 1);
                }
                return true;
            }

            case UiEventType.MouseMove when _draggingThumb:
            {
                float trackTop = AxisExtent(DecrementButtonExtent, Height);
                float trackLen = MathF.Max(
                    0f,
                    Height
                    - AxisExtent(DecrementButtonExtent, Height)
                    - AxisExtent(IncrementButtonExtent, Height));
                float thumbH = MathF.Max(MinThumb, trackLen * m.ThumbRatio);
                float travel = MathF.Max(1f, trackLen - thumbH);
                float newRatio = ((float)e.Data2 - _dragOffsetY - trackTop) / travel;
                m.SetPositionRatio(newRatio);
                return true;
            }

            case UiEventType.MouseUp:
            {
                bool wasDragging = _draggingThumb;
                _draggingThumb = false;
                _pressedButton = EndButton.None;
                if (wasDragging) DragCompleted?.Invoke();
                return true;
            }
        }

        return false;
    }

    private bool OnHorizontalModelEvent(in UiEvent e)
    {
        UiScrollable m = Model!;
        switch (e.Type)
        {
            case UiEventType.MouseDown:
            {
                float x = e.Data1;
                _pressedButton = ButtonAt(e.Data1, e.Data2);
                float decrementExtent = AxisExtent(DecrementButtonExtent, Width);
                float incrementExtent = AxisExtent(IncrementButtonExtent, Width);
                if (x < decrementExtent) { m.ScrollByLines(-1); return true; }
                if (x >= Width - incrementExtent) { m.ScrollByLines(1); return true; }

                float trackLeft = decrementExtent;
                float trackLength = MathF.Max(0f, Width - decrementExtent - incrementExtent);
                var (tx, tw) = ThumbRect(m, trackLeft, trackLength);
                if (x >= tx && x <= tx + tw)
                {
                    _draggingThumb = true;
                    _dragOffsetX = x - tx;
                }
                else
                {
                    m.ScrollByPage(x < tx ? -1 : 1);
                }
                return true;
            }

            case UiEventType.MouseMove when _draggingThumb:
            {
                float trackLeft = AxisExtent(DecrementButtonExtent, Width);
                float trackLength = MathF.Max(
                    0f,
                    Width
                    - AxisExtent(DecrementButtonExtent, Width)
                    - AxisExtent(IncrementButtonExtent, Width));
                float thumbWidth = MathF.Max(MinThumb, trackLength * m.ThumbRatio);
                float travel = MathF.Max(1f, trackLength - thumbWidth);
                float ratio = ((float)e.Data1 - _dragOffsetX - trackLeft) / travel;
                m.SetPositionRatio(ratio);
                return true;
            }

            case UiEventType.MouseUp:
            {
                bool wasDragging = _draggingThumb;
                _draggingThumb = false;
                _pressedButton = EndButton.None;
                if (wasDragging) DragCompleted?.Invoke();
                return true;
            }
        }
        return false;
    }

    private bool OnScalarEvent(in UiEvent e)
    {
        switch (e.Type)
        {
            case UiEventType.MouseDown:
            {
                float thumbWidth = ScalarThumbWidth(SpriteResolve);
                float travel = MathF.Max(1f, Width - thumbWidth);
                float thumbX = travel * ScalarPosition;
                float x = e.Data1;
                _draggingThumb = true;
                if (x >= thumbX && x <= thumbX + thumbWidth)
                {
                    _dragOffsetX = x - thumbX;
                }
                else
                {
                    _dragOffsetX = thumbWidth * 0.5f;
                    ChangeScalarPosition((x - _dragOffsetX) / travel);
                }
                return true;
            }

            case UiEventType.MouseMove when _draggingThumb:
            {
                float thumbWidth = ScalarThumbWidth(SpriteResolve);
                float travel = MathF.Max(1f, Width - thumbWidth);
                ChangeScalarPosition(((float)e.Data1 - _dragOffsetX) / travel);
                return true;
            }

            case UiEventType.MouseUp:
            {
                bool wasDragging = _draggingThumb;
                _draggingThumb = false;
                _pressedButton = EndButton.None;
                if (wasDragging) DragCompleted?.Invoke();
                return true;
            }
        }

        return false;
    }

    private bool OnVerticalScalarEvent(in UiEvent e)
    {
        switch (e.Type)
        {
            case UiEventType.MouseDown:
            {
                float thumbHeight = ScalarThumbExtent(SpriteResolve, Height);
                float travel = MathF.Max(1f, Height - thumbHeight);
                float thumbY = travel * ScalarPosition;
                float y = e.Data2;
                _draggingThumb = true;
                if (y >= thumbY && y <= thumbY + thumbHeight)
                {
                    _dragOffsetY = y - thumbY;
                }
                else
                {
                    _dragOffsetY = thumbHeight * 0.5f;
                    ChangeScalarPosition((y - _dragOffsetY) / travel);
                }
                return true;
            }

            case UiEventType.MouseMove when _draggingThumb:
            {
                float thumbHeight = ScalarThumbExtent(SpriteResolve, Height);
                float travel = MathF.Max(1f, Height - thumbHeight);
                ChangeScalarPosition(((float)e.Data2 - _dragOffsetY) / travel);
                return true;
            }

            case UiEventType.MouseUp:
            {
                bool wasDragging = _draggingThumb;
                _draggingThumb = false;
                _pressedButton = EndButton.None;
                if (wasDragging) DragCompleted?.Invoke();
                return true;
            }
        }

        return false;
    }

    private float ScalarThumbWidth(Func<uint, (uint tex, int w, int h)>? resolve) =>
        ScalarThumbExtent(resolve, Width);

    private float ScalarThumbExtent(
        Func<uint, (uint tex, int w, int h)>? resolve, float axisLength)
    {
        if (resolve is not null && ThumbSprite != 0)
        {
            var (_, width, height) = resolve(ThumbSprite);
            int native = Horizontal ? width : height;
            if (native > 0) return MathF.Min(native, axisLength);
        }
        return MathF.Min(16f, axisLength);
    }

    private void ChangeScalarPosition(float position)
    {
        SetScalarPosition(position);
        ScalarChanged?.Invoke(ScalarPosition);
    }

    private bool ThumbAt(float x, float y)
    {
        if (x < 0f || x >= Width || y < 0f || y >= Height)
            return false;

        if (ScalarChanged is not null)
        {
            if (Horizontal)
            {
                float thumbWidth = ScalarThumbWidth(SpriteResolve);
                float thumbX = MathF.Max(0f, Width - thumbWidth) * ScalarPosition;
                return x >= thumbX && x <= thumbX + thumbWidth;
            }
            float thumbHeight = ScalarThumbExtent(SpriteResolve, Height);
            float thumbY = MathF.Max(0f, Height - thumbHeight) * ScalarPosition;
            return y >= thumbY && y <= thumbY + thumbHeight;
        }

        if (Model is not { } m) return false;

        if (Horizontal)
        {
            float trackLeft = AxisExtent(DecrementButtonExtent, Width);
            float trackLength = MathF.Max(
                0f,
                Width
                - AxisExtent(DecrementButtonExtent, Width)
                - AxisExtent(IncrementButtonExtent, Width));
            var (tx, tw) = ThumbRect(m, trackLeft, trackLength);
            return x >= tx && x <= tx + tw;
        }

        float trackTop = AxisExtent(DecrementButtonExtent, Height);
        float trackLen = MathF.Max(
            0f,
            Height
            - AxisExtent(DecrementButtonExtent, Height)
            - AxisExtent(IncrementButtonExtent, Height));
        var (ty, th) = ThumbRect(m, trackTop, trackLen);
        return y >= ty && y <= ty + th;
    }

    private EndButton ButtonAt(float x, float y)
    {
        if (x < 0f || x >= Width || y < 0f || y >= Height)
            return EndButton.None;

        if (Horizontal)
        {
            if (x < AxisExtent(DecrementButtonExtent, Width))
                return EndButton.Decrement;
            if (x >= Width - AxisExtent(IncrementButtonExtent, Width))
                return EndButton.Increment;
            return EndButton.None;
        }

        if (y < AxisExtent(DecrementButtonExtent, Height))
            return EndButton.Decrement;
        if (y >= Height - AxisExtent(IncrementButtonExtent, Height))
            return EndButton.Increment;
        return EndButton.None;
    }

    private uint ActiveStartSprite
        => _pressedButton == EndButton.Decrement
            && _hoveredButton == EndButton.Decrement
            && UpPressedSprite != 0u
            ? UpPressedSprite
            : _hoveredButton == EndButton.Decrement && UpRolloverSprite != 0u
                ? UpRolloverSprite
                : UpSprite;

    private uint ActiveEndSprite
        => _pressedButton == EndButton.Increment
            && _hoveredButton == EndButton.Increment
            && DownPressedSprite != 0u
            ? DownPressedSprite
            : _hoveredButton == EndButton.Increment && DownRolloverSprite != 0u
                ? DownRolloverSprite
                : DownSprite;

    internal uint ActiveStartSpriteForTest => ActiveStartSprite;
    internal uint ActiveEndSpriteForTest => ActiveEndSprite;
    internal uint ActiveThumbSpriteForTest => ActiveThumbSprite;
    internal uint ActiveThumbTopSpriteForTest => ActiveThumbTopSprite;
    internal uint ActiveThumbBotSpriteForTest => ActiveThumbBotSprite;

    private static float AxisExtent(float authoredExtent, float axisLength)
        => Math.Clamp(authoredExtent, 0f, MathF.Max(0f, axisLength));
}
