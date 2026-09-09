using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using AcDream.App.Rendering;
using AcDream.App.UI.Layout;

namespace AcDream.App.UI;

public sealed class UiText : UiElement, IUiDatStateful
{
    public Action? OnClick
    {
        get => _onClick;
        set
        {
            _onClick = value;
            if (value is not null)
                ClickThrough = false;
        }
    }

    private Action? _onClick;
    public override bool HandlesClick
        => OnClick is not null
            || OnCharClick is not null
            || WheelScrollEnabled
            || base.HandlesClick;
    /// <summary>Dat element id for imported UIElement_Text widgets. 0 for synthesized text.</summary>
    public uint ElementId { get; set; }

    public readonly record struct Line(string Text, Vector4 Color);

    public readonly record struct TextRun(string Text, Vector4 Color);

    public readonly record struct Pos(int Line, int Col);

    public Func<Pos, bool>? OnCharClick { get; set; }

    /// <summary>Provider of the lines to show, oldest-first. Polled each frame.</summary>
    public Func<IReadOnlyList<Line>> LinesProvider { get; set; } = static () => Array.Empty<Line>();

    public Func<IReadOnlyList<TextRun>>? RunsProvider { get; set; }

    public Func<int, IReadOnlyList<TextRun>?>? LineRunsProvider { get; set; }

    internal static List<(string Text, float X, Vector4 Color)> LayoutRuns(
        IReadOnlyList<TextRun> runs,
        float startX,
        Func<string, float> measure)
    {
        var placed = new List<(string Text, float X, Vector4 Color)>(runs.Count);
        float penX = startX;
        for (int i = 0; i < runs.Count; i++)
        {
            TextRun run = runs[i];
            if (run.Text.Length != 0)
                placed.Add((run.Text, penX, run.Color));
            penX += measure(run.Text);
        }
        return placed;
    }

    internal static bool RunsMatchLine(IReadOnlyList<TextRun> runs, string line)
    {
        int at = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            string text = runs[i].Text;
            if (at + text.Length > line.Length
                || string.CompareOrdinal(line, at, text, 0, text.Length) != 0)
            {
                return false;
            }
            at += text.Length;
        }
        return at == line.Length;
    }

    public BitmapFont? Font { get; set; }

    public UiDatFont? DatFont { get; set; }

    public Silk.NET.Input.IKeyboard? Keyboard { get; set; }

    public Vector4 DefaultColor { get; set; } = Vector4.One;

    public Vector4? TagColor { get; set; }

    public IReadOnlyList<Vector4> FontColorPalette { get; set; }
        = Array.Empty<Vector4>();

    public Vector4 BackgroundColor { get; set; } = new(0f, 0f, 0f, 0f);

    public bool Outline { get; set; }

    public Vector4 OutlineColor { get; set; } = UiRenderContext.DefaultOutlineColor;

    /// <summary>Optional dat state-sprite background (the element's own media), drawn
    /// UNDER the text. Set by DatWidgetFactory.BuildText from the ElementInfo. 0 = none.</summary>
    public uint BackgroundSprite { get; set; }

    public Func<uint, (uint tex, int w, int h)>? SpriteResolve { get; set; }

    public Vector4 SelectionColor { get; set; } = new(0.25f, 0.45f, 0.85f, 0.5f);

    public float Padding { get; set; }

    public float MarginLeft { get; set; }
    public float MarginRight { get; set; }
    public float MarginTop { get; set; }
    public float MarginBottom { get; set; }

    public bool OneLine { get; set; }

    private bool _selectable;

    public bool Selectable
    {
        get => _selectable;
        set
        {
            if (_selectable == value) return;
            _selectable = value;
            ClickThrough = !value;
            AcceptsFocus = value;
            IsEditControl = value;
            CapturesPointerDrag = value;
            if (!value)
            {
                _selecting = false;
                _selAnchor = null;
                _selCaret = null;
            }
        }
    }

    public bool Centered { get; set; }

    public bool RightAligned { get; set; }

    public VJustify VerticalJustify { get; set; } = VJustify.Center;

    public bool HonorVerticalJustification { get; set; }

    /// <summary>The scroll model — also read by the linked UiScrollbar.</summary>
    public UiScrollable Scroll { get; } = new();

    public bool PreserveEndOnLayout { get; set; } = true;

    public bool WheelScrollEnabled { get; set; }

    private const float WheelLines = 1f;

    private IReadOnlyList<Line> _lastLines = Array.Empty<Line>();
    private BitmapFont? _lastFont;
    private UiDatFont? _lastDatFont;
    private float _lastLineHeight = 16f;
    private float _lastBaseY;          // top Y of line 0 in local space
    private float _lastPadding;

    private ElementInfo? _datInfo;
    private uint _activeRetailStateId = UiStateInfo.DirectStateId;
    private string _activeDatStateName = "";
    private bool _drawTextAfterChildren;
    private bool _honorDatVerticalJustification;

    // ── Selection state ──────────────────────────────────────────────────
    private Pos? _selAnchor;   // where the drag started
    private Pos? _selCaret;
    private bool _selecting;

    public UiText()
    {
        ClickThrough = true;
        AcceptsFocus = false;
        IsEditControl = false;
        CapturesPointerDrag = false;
    }

    public override bool ConsumesDatChildren
    {
        get
        {
            if (_datInfo is null) return true;
            foreach (UiStateInfo state in _datInfo.States.Values)
                if (state.PassToChildren)
                    return false;
            return true;
        }
    }

    public uint ActiveRetailStateId => _activeRetailStateId;

    public override string ActiveCursorStateName => _activeDatStateName;

    private Dictionary<uint, string>? _authoredStateStrings;

    internal void SetAuthoredStateStrings(Dictionary<uint, string> strings)
        => _authoredStateStrings = strings;

    internal void ConfigureDatState(ElementInfo info)
    {
        _datInfo = info;
        _honorDatVerticalJustification = true;
        _drawTextAfterChildren = false;
        foreach (UiStateInfo state in info.States.Values)
        {
            if (!state.PassToChildren) continue;
            _drawTextAfterChildren = true;
            break;
        }
        _activeRetailStateId = info.EffectiveDefaultStateId();
        ApplyDatState(_activeRetailStateId, propagate: false);
    }

    internal bool DrawTextAfterChildren => _drawTextAfterChildren;

    public bool TrySetRetailState(uint stateId)
        => ApplyDatState(stateId, propagate: true);

    private bool ApplyDatState(uint stateId, bool propagate)
    {
        if (_datInfo is null) return false;

        UiStateInfo? state = null;
        string stateName;
        if (stateId == UiStateInfo.DirectStateId)
        {
            if (!_datInfo.States.TryGetValue(stateId, out state)
                && !_datInfo.StateMedia.ContainsKey(""))
                return false;
            stateName = "";
        }
        else if (_datInfo.States.TryGetValue(stateId, out state))
        {
            stateName = state.Name;
        }
        else
        {
            stateName = UiButtonStateMachine.StateName(stateId);
            if (string.IsNullOrEmpty(stateName))
                stateName = RetailUiStateIds.StateName(stateId);
            if (string.IsNullOrEmpty(stateName)
                || !_datInfo.StateMedia.ContainsKey(stateName))
                return false;
        }

        _activeRetailStateId = stateId;
        _activeDatStateName = stateName;
        BackgroundSprite = _datInfo.StateMedia.TryGetValue(stateName, out var media)
            ? media.File
            : _datInfo.StateMedia.TryGetValue("", out var direct) ? direct.File : 0u;

        if (_datInfo.TryGetEffectiveProperty(0x1Bu, out UiPropertyValue color, stateId)
            && TryColor(color, out Vector4 resolvedColor))
            DefaultColor = resolvedColor;

        if (state is not null
            && state.Properties.Values.TryGetValue(0x3Bu, out var invisibleProp)
            && invisibleProp.Kind == UiPropertyKind.Bool)
            Visible = !invisibleProp.BoolValue;

        if (_authoredStateStrings is { } stateStrings
            && stateStrings.TryGetValue(stateId, out string? authoredLine))
        {
            LinesProvider = () => [new Line(authoredLine, DefaultColor)];
        }

        if (propagate && state?.PassToChildren == true)
        {
            foreach (UiElement child in Children)
                if (child is IUiDatStateful stateful)
                    stateful.TrySetRetailState(stateId);
        }

        return true;
    }

    private static bool TryColor(UiPropertyValue property, out Vector4 color)
    {
        UiPropertyValue? value = property.Kind == UiPropertyKind.Color
            ? property
            : property.Kind == UiPropertyKind.Array
                && property.ArrayValue.Count > 0
                && property.ArrayValue[0].Kind == UiPropertyKind.Color
                    ? property.ArrayValue[0]
                    : null;
        if (value is null)
        {
            color = default;
            return false;
        }

        UiColorValue c = value.ColorValue;
        float alpha = c.Alpha == 0 ? 1f : c.Alpha / 255f;
        color = new Vector4(c.Red / 255f, c.Green / 255f, c.Blue / 255f, alpha);
        return true;
    }

    public static float ClampScroll(float scroll, float contentHeight, float viewHeight)
    {
        float max = Math.Max(0f, contentHeight - viewHeight);
        if (scroll < 0f) return 0f;
        return scroll > max ? max : scroll;
    }

    protected override void OnDraw(UiRenderContext ctx)
    {
        // Optional dat state-sprite background drawn UNDER everything else.
        if (BackgroundSprite != 0 && SpriteResolve is { } sr)
        {
            var (tex, tw, th) = sr(BackgroundSprite);
            if (tex != 0 && tw != 0 && th != 0)
                ctx.DrawSprite(tex, 0, 0, Width, Height, 0, 0, Width / tw, Height / th, Vector4.One);
        }

        // Background must draw UNDER the transcript text. DrawStringDat emits into the
        // sprite bucket which flushes BEFORE rects, so a DrawRect background would wash
        // over the text. DrawFill routes the background through the sprite bucket too,
        // submitted first → text on top.
        ctx.DrawFill(0, 0, Width, Height, BackgroundColor);

        if (!_drawTextAfterChildren)
            DrawText(ctx);
    }

    protected override void OnDrawAfterChildren(UiRenderContext ctx)
    {
        if (_drawTextAfterChildren)
            DrawText(ctx);
    }

    private void DrawText(UiRenderContext ctx)
    {
        DrawClippedText(ctx);
    }

    private void DrawClippedText(UiRenderContext ctx)
    {
        if (OneLine && RunsProvider is { } runsProvider)
        {
            DrawSingleLineRuns(ctx, runsProvider());
            return;
        }

        if (OneLine && Centered)
        {
            var cLines = LinesProvider();
            if (cLines.Count == 0) return;
            var line0 = cLines[0];
            if (DatFont is { } cdf)
            {
                float cx = (Width - cdf.MeasureWidth(line0.Text)) * 0.5f;
                float cy = VOffset(Height, cdf.LineHeight, Padding, VerticalJustify);
                ctx.DrawStringDat(cdf, line0.Text, cx, cy, line0.Color, Outline, OutlineColor);
            }
            else if ((Font ?? ctx.DefaultFont) is { } cbf)
            {
                float cx = (Width - cbf.MeasureWidth(line0.Text)) * 0.5f;
                float cy = VOffset(Height, cbf.LineHeight, Padding, VerticalJustify);
                ctx.DrawString(line0.Text, cx, cy, line0.Color, cbf);
            }
            return;
        }

        // Static right-aligned single-line mode: draw the first line flush with the right
        // edge, vertical position per VerticalJustify, then skip the scroll/selection machinery.
        if (OneLine && RightAligned)
        {
            var rLines = LinesProvider();
            if (rLines.Count == 0) return;
            var line0 = rLines[0];
            if (DatFont is { } rdf)
            {
                float rx = Width - rdf.MeasureWidth(line0.Text) - Padding;
                float ry = VOffset(Height, rdf.LineHeight, Padding, VerticalJustify);
                ctx.DrawStringDat(rdf, line0.Text, rx, ry, line0.Color, Outline, OutlineColor);
            }
            else if ((Font ?? ctx.DefaultFont) is { } rbf)
            {
                float rx = Width - rbf.MeasureWidth(line0.Text) - Padding;
                float ry = VOffset(Height, rbf.LineHeight, Padding, VerticalJustify);
                ctx.DrawString(line0.Text, rx, ry, line0.Color, rbf);
            }
            return;
        }

        if (OneLine)
        {
            var singleLines = LinesProvider();
            if (singleLines.Count == 0) return;
            var line0 = singleLines[0];
            if (DatFont is { } datSingle)
            {
                float y = VOffset(Height, datSingle.LineHeight, Padding, VerticalJustify);
                ctx.DrawStringDat(datSingle, line0.Text, Padding, y, line0.Color, Outline, OutlineColor);
            }
            else if ((Font ?? ctx.DefaultFont) is { } bitmapSingle)
            {
                float y = VOffset(Height, bitmapSingle.LineHeight, Padding, VerticalJustify);
                ctx.DrawString(line0.Text, Padding, y, line0.Color, bitmapSingle);
            }
            return;
        }

        var datFont = DatFont;
        var bitmapFont = datFont is null ? (Font ?? ctx.DefaultFont) : null;
        if (datFont is null && bitmapFont is null) return;

        var lines = LinesProvider();

        _lastLines = lines;
        _lastDatFont = datFont;
        _lastFont = bitmapFont;
        _lastLineHeight = datFont is not null ? datFont.LineHeight : bitmapFont!.LineHeight;
        _lastPadding = Padding;

        if (lines.Count == 0) return;

        float lh = _lastLineHeight;
        float top = Padding + MarginTop, bottom = Height - Padding - MarginBottom;
        float innerH = bottom - top;
        float contentH = lines.Count * lh;

        Scroll.LineHeight = (int)MathF.Round(lh);
        Scroll.SetExtents(
            (int)MathF.Ceiling(contentH),
            (int)MathF.Floor(innerH),
            preserveEnd: PreserveEndOnLayout);

        float baseY = ContentBaseY(
            top,
            bottom,
            contentH,
            Scroll.MaxScroll,
            Scroll.ScrollY,
            VerticalJustify,
            _honorDatVerticalJustification || HonorVerticalJustification);
        _lastBaseY = baseY;

        // Normalised selection span (start <= end), if any.
        bool hasSel = TryGetOrderedSelection(out Pos selStart, out Pos selEnd);

        List<(string Text, float X, float Y, Vector4 Color)>? datLines = null;

        for (int i = 0; i < lines.Count; i++)
        {
            float y = baseY + i * lh;
            if (!LineIntersectsViewport(y, lh, top, bottom)) continue;

            string text = lines[i].Text;
            float lineX = HorizontalOffset(text, datFont, bitmapFont);

            if (hasSel && i >= selStart.Line && i <= selEnd.Line)
            {
                int c0 = i == selStart.Line ? selStart.Col : 0;
                int c1 = i == selEnd.Line ? selEnd.Col : text.Length;
                c0 = Math.Clamp(c0, 0, text.Length);
                c1 = Math.Clamp(c1, 0, text.Length);
                if (c1 > c0)
                {
                    float hx, hw;
                    if (datFont is not null)
                    {
                        hx = lineX + datFont.MeasureWidth(text.Substring(0, c0));
                        hw = datFont.MeasureWidth(text.Substring(c0, c1 - c0));
                    }
                    else
                    {
                        hx = lineX + bitmapFont!.MeasureWidth(text.Substring(0, c0));
                        hw = bitmapFont.MeasureWidth(text.Substring(c0, c1 - c0));
                    }
                    // Highlight sits BEHIND the line's text → sprite bucket, submitted
                    // before this line's text (still true: this happens before either
                    // pass below runs for ANY line).
                    ctx.DrawFill(hx, y, hw, lh, SelectionColor);
                }
            }

            IReadOnlyList<TextRun>? runs = LineRunsProvider?.Invoke(i);
            if (runs is { Count: > 0 } && !RunsMatchLine(runs, text))
                runs = null;   // never draw text that disagrees with what we select

            if (datFont is not null)
            {
                datLines ??= new();
                if (runs is { Count: > 0 })
                {
                    foreach (var placed in LayoutRuns(runs, lineX, datFont.MeasureWidth))
                        datLines.Add((placed.Text, placed.X, y, placed.Color));
                }
                else
                {
                    datLines.Add((text, lineX, y, lines[i].Color));
                }
            }
            else if (runs is { Count: > 0 })
            {
                foreach (var placed in LayoutRuns(runs, lineX, bitmapFont!.MeasureWidth))
                    ctx.DrawString(placed.Text, placed.X, y, placed.Color, bitmapFont);
            }
            else
            {
                ctx.DrawString(text, lineX, y, lines[i].Color, bitmapFont);
            }
        }

        if (datLines is not null)
        {
            // Outline-OFF stays a single fill-only pass per line — byte-identical to
            // the pre-S1 per-line DrawStringDat(outline:false) submission order.
            if (Outline)
                foreach (var line in datLines)
                    ctx.DrawStringDatPass(datFont!, line.Text, line.X, line.Y, OutlineColor, isOutlinePass: true);
            foreach (var line in datLines)
                ctx.DrawStringDatPass(datFont!, line.Text, line.X, line.Y, line.Color, isOutlinePass: false);
        }
    }

    private void DrawSingleLineRuns(
        UiRenderContext ctx,
        IReadOnlyList<TextRun> runs)
    {
        if (runs.Count == 0) return;

        UiDatFont? datFont = DatFont;
        BitmapFont? bitmapFont = datFont is null
            ? Font ?? ctx.DefaultFont
            : null;
        if (datFont is null && bitmapFont is null) return;

        float totalWidth = 0f;
        foreach (TextRun run in runs)
        {
            totalWidth += datFont is not null
                ? datFont.MeasureWidth(run.Text)
                : bitmapFont!.MeasureWidth(run.Text);
        }

        float x = Centered
            ? Math.Max(Padding, (Width - totalWidth) * 0.5f)
            : RightAligned
                ? Math.Max(Padding, Width - Padding - totalWidth)
                : Padding;
        float lineHeight = datFont?.LineHeight ?? bitmapFont!.LineHeight;
        float y = VOffset(
            Height,
            lineHeight,
            Padding,
            VerticalJustify);

        if (datFont is not null)
        {
            var runGeometry = new List<(string Text, float X, Vector4 Color)>();
            float penX = x;
            foreach (TextRun run in runs)
            {
                if (run.Text.Length == 0) continue;
                runGeometry.Add((run.Text, penX, run.Color));
                penX += datFont.MeasureWidth(run.Text);
            }

            if (Outline)
                foreach (var run in runGeometry)
                    ctx.DrawStringDatPass(datFont, run.Text, run.X, y, OutlineColor, isOutlinePass: true);
            foreach (var run in runGeometry)
                ctx.DrawStringDatPass(datFont, run.Text, run.X, y, run.Color, isOutlinePass: false);
        }
        else
        {
            foreach (TextRun run in runs)
            {
                if (run.Text.Length == 0) continue;
                ctx.DrawString(run.Text, x, y, run.Color, bitmapFont);
                x += bitmapFont!.MeasureWidth(run.Text);
            }
        }
    }

    internal static bool LineIntersectsViewport(
        float lineTop,
        float lineHeight,
        float viewportTop,
        float viewportBottom)
        => lineHeight > 0f
            && lineTop < viewportBottom
            && lineTop + lineHeight > viewportTop;

    private float HorizontalOffset(string text, UiDatFont? datFont, BitmapFont? bitmapFont)
    {
        float width = datFont is not null
            ? datFont.MeasureWidth(text)
            : bitmapFont?.MeasureWidth(text) ?? 0f;
        return ContentOffsetX(Width, Padding, MarginLeft, MarginRight, width, Centered, RightAligned);
    }

    public static float ContentOffsetX(
        float elementWidth,
        float padding,
        float marginLeft,
        float marginRight,
        float lineWidth,
        bool centered,
        bool rightAligned)
    {
        float contentLeft = padding + marginLeft;
        float contentRight = elementWidth - padding - marginRight;
        if (centered)
            return Math.Max(contentLeft, contentLeft + (contentRight - contentLeft - lineWidth) * 0.5f);
        if (rightAligned)
            return Math.Max(contentLeft, contentRight - lineWidth);
        return contentLeft;
    }

    public override bool OnEvent(in UiEvent e)
    {
        if (e.Type == UiEventType.Click && OnCharClick is not null)
        {
            if (OnCharClick(HitChar(e.Data1, e.Data2)))
                return true;
        }

        if (e.Type == UiEventType.Click && OnClick is not null)
        {
            OnClick();
            return true;
        }
        switch (e.Type)
        {
            case UiEventType.Scroll:
            {
                if (!Selectable && !WheelScrollEnabled) return false;
                // Silk wheel +Y = scroll up = reveal older = toward the TOP = decrease ScrollY.
                // ScrollByLines sign: +down/newer, -up/older.
                // e.Data0 > 0 → wheel up → want older → ScrollByLines with negative lines.
                Scroll.ScrollByLines((int)(-e.Data0 * WheelLines));
                return true;
            }

            case UiEventType.MouseDown:
            {
                if (!Selectable) return false;
                var p = HitChar(e.Data1, e.Data2);
                _selAnchor = p;
                _selCaret = p;
                _selecting = true;
                return true;
            }

            case UiEventType.MouseMove:
            {
                if (!Selectable) return false;
                if (_selecting)
                {
                    _selCaret = HitChar(e.Data1, e.Data2);
                    return true;
                }
                return false;
            }

            case UiEventType.MouseUp:
            {
                if (!Selectable) return false;
                _selecting = false;
                return true;
            }

            case UiEventType.KeyDown:
            {
                if (!Selectable) return false;
                var key = (Silk.NET.Input.Key)e.Data0;
                bool ctrl = Keyboard is not null
                    && (Keyboard.IsKeyPressed(Silk.NET.Input.Key.ControlLeft)
                        || Keyboard.IsKeyPressed(Silk.NET.Input.Key.ControlRight));
                if (ctrl && key == Silk.NET.Input.Key.C)
                {
                    if (Keyboard is not null)
                    {
                        string sel = SelectedText();
                        if (sel.Length > 0) Keyboard.ClipboardText = sel;
                    }
                    return true;
                }
                if (ctrl && key == Silk.NET.Input.Key.A)
                {
                    SelectAll();
                    return true;
                }
                return false;
            }
        }
        return false;
    }

    // ── Selection helpers ────────────────────────────────────────────────

    private void SelectAll()
    {
        var lines = _lastLines;
        if (lines.Count == 0)
        {
            _selAnchor = _selCaret = null;
            return;
        }
        int last = lines.Count - 1;
        _selAnchor = new Pos(0, 0);
        _selCaret = new Pos(last, lines[last].Text.Length);
    }

    private bool TryGetOrderedSelection(out Pos start, out Pos end)
    {
        start = default; end = default;
        if (_selAnchor is not { } a || _selCaret is not { } c) return false;
        (start, end) = Order(a, c);
        return !(start.Line == end.Line && start.Col == end.Col);
    }

    public string SelectedText()
    {
        if (!TryGetOrderedSelection(out var start, out var end)) return string.Empty;
        return SelectedText(_lastLines, start, end);
    }

    // ── Pure, testable logic (no GL / no font texture) ───────────────────

    public static float VOffset(float height, float lineHeight, float padding, VJustify vj)
        => vj switch
        {
            VJustify.Top    => padding,
            VJustify.Bottom => height - lineHeight - padding,
            _               => (height - lineHeight) * 0.5f,
        };

    public static float ContentBaseY(
        float top,
        float bottom,
        float contentHeight,
        float maxScroll,
        float scrollY,
        VJustify justification,
        bool honorJustification)
    {
        float viewHeight = Math.Max(0f, bottom - top);
        if (!honorJustification || contentHeight > viewHeight)
            return bottom - contentHeight + (maxScroll - scrollY);

        return justification switch
        {
            VJustify.Top => top,
            VJustify.Bottom => bottom - contentHeight,
            _ => top + (viewHeight - contentHeight) * 0.5f,
        };
    }

    public static (Pos start, Pos end) Order(Pos a, Pos b)
    {
        if (a.Line < b.Line || (a.Line == b.Line && a.Col <= b.Col)) return (a, b);
        return (b, a);
    }

    /// <summary>
    /// Assemble the selected substring spanning <paramref name="start"/> ..
    /// <paramref name="end"/> (inclusive of start.Col, exclusive of end.Col) from
    /// <paramref name="lines"/>. Multi-line selections are joined with "\n":
    /// the first line from start.Col to its end, whole middle lines, and the last
    /// line up to end.Col. Pure — unit-testable without GL.
    /// </summary>
    public static string SelectedText(IReadOnlyList<Line> lines, Pos start, Pos end)
    {
        if (lines.Count == 0) return string.Empty;
        (start, end) = Order(start, end);

        int sl = Math.Clamp(start.Line, 0, lines.Count - 1);
        int el = Math.Clamp(end.Line, 0, lines.Count - 1);

        if (sl == el)
        {
            string t = lines[sl].Text;
            int c0 = Math.Clamp(start.Col, 0, t.Length);
            int c1 = Math.Clamp(end.Col, 0, t.Length);
            if (c1 <= c0) return string.Empty;
            return t.Substring(c0, c1 - c0);
        }

        var sb = new StringBuilder();

        // First line: from start.Col to its end.
        {
            string t = lines[sl].Text;
            int c0 = Math.Clamp(start.Col, 0, t.Length);
            sb.Append(t.AsSpan(c0));
        }

        // Whole middle lines.
        for (int i = sl + 1; i < el; i++)
        {
            sb.Append('\n');
            sb.Append(lines[i].Text);
        }

        // Last line: up to end.Col.
        {
            sb.Append('\n');
            string t = lines[el].Text;
            int c1 = Math.Clamp(end.Col, 0, t.Length);
            sb.Append(t.AsSpan(0, c1));
        }

        return sb.ToString();
    }

    private Pos HitChar(float localX, float localY)
    {
        var lines = _lastLines;
        if (lines.Count == 0) return new Pos(0, 0);

        float lh = _lastLineHeight <= 0f ? 16f : _lastLineHeight;
        int line = (int)MathF.Floor((localY - _lastBaseY) / lh);
        line = Math.Clamp(line, 0, lines.Count - 1);

        string text = lines[line].Text;
        float lineX = HorizontalOffset(text, _lastDatFont, _lastFont);
        int col = _lastDatFont is { } df
            ? CharIndexAt(text, ch => df.TryGetGlyph(ch, out var g) ? UiDatFont.GlyphAdvance(g) : 0f,
                          localX - lineX)
            : (_lastFont is { } bf
                ? CharIndexAt(text, ch => bf.TryGetGlyph(ch, out var bg) ? bg.Advance : 0f,
                              localX - lineX)
                : 0);
        return new Pos(line, col);
    }

    /// <summary>Word-wrap text to a measured pixel width, preserving explicit newlines.</summary>
    public static IReadOnlyList<string> WrapWords(
        string text,
        Func<string, float> measureWidth,
        float maximumWidth)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measureWidth);
        if (maximumWidth <= 0f) throw new ArgumentOutOfRangeException(nameof(maximumWidth));

        var result = new List<string>();
        string[] paragraphs = text.Replace("\r", string.Empty).Split('\n');
        foreach (string paragraph in paragraphs)
        {
            if (paragraph.Length == 0)
            {
                result.Add(string.Empty);
                continue;
            }

            string[] words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var line = new StringBuilder();
            foreach (string word in words)
            {
                string candidate = line.Length == 0 ? word : $"{line} {word}";
                if (measureWidth(candidate) <= maximumWidth)
                {
                    if (line.Length != 0) line.Append(' ');
                    line.Append(word);
                    continue;
                }

                if (line.Length != 0 && measureWidth(word) <= maximumWidth)
                {
                    result.Add(line.ToString());
                    line.Clear();
                    line.Append(word);
                    continue;
                }

                for (int i = 0; i < word.Length; i++)
                {
                    string prefix = i == 0 && line.Length != 0 ? " " : string.Empty;
                    if (line.Length != 0
                        && measureWidth(line + prefix + word[i]) > maximumWidth)
                    {
                        result.Add(line.ToString());
                        line.Clear();
                        prefix = string.Empty;
                    }
                    line.Append(prefix).Append(word[i]);
                }
            }

            result.Add(line.ToString());
        }

        return result;
    }

    public static int CharIndexAt(string text, Func<char, float> advanceOf, float x)
    {
        if (string.IsNullOrEmpty(text) || x <= 0f) return 0;

        float cursor = 0f;
        for (int i = 0; i < text.Length; i++)
        {
            float adv = advanceOf(text[i]);
            float mid = cursor + adv * 0.5f;
            if (x < mid) return i;
            cursor += adv;
        }
        return text.Length;
    }
}
