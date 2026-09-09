using System;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.App.UI;

public sealed class UiField : UiElement
{
    private readonly record struct WrappedLine(int Start, int Length, string Text);

    public uint ElementId { get; set; }
    public UiDatFont? DatFont { get; set; }
    public AcDream.App.Rendering.BitmapFont? Font { get; set; }
    public Vector4 TextColor { get; set; } = new(1f, 1f, 1f, 1f);

    public bool Outline { get; set; }

    public Vector4 OutlineColor { get; set; } = UiRenderContext.DefaultOutlineColor;

    public Vector4 BackgroundColor { get; set; } = new(0f, 0f, 0f, 0f);
    /// <summary>Selected-span highlight (translucent blue, behind the text).</summary>
    public Vector4 SelectionColor { get; set; } = new(0.25f, 0.45f, 0.85f, 0.5f);
    public float Padding { get; set; } = 4f;
    public int MaxCharacters { get; set; } = 0xFFFF;
    public bool OneLine { get; set; } = true;
    public bool Selectable { get; set; }
    public bool Centered { get; set; }
    public bool RightAligned { get; set; }
    public bool ClearOnSubmit { get; set; } = true;
    public bool RecordHistory { get; set; } = true;
    public Func<char, bool>? CharacterFilter { get; set; }
    public bool SelectAllOnFocus { get; set; }
    public UiScrollable Scroll { get; } = new();
    public Action? OnReadOnlyClick { get; set; }

    private bool _editable = true;

    public bool Editable
    {
        get => _editable;
        set
        {
            if (_editable == value) return;
            _editable = value;
            AcceptsFocus = value;
            IsEditControl = value;
            if (!value && _focused)
                FindRoot()?.SetKeyboardFocus(null);
            if (!value)
                _repeatKey = null;
        }
    }

    public Silk.NET.Input.IKeyboard? Keyboard { get; set; }

    public Func<uint, (uint tex, int w, int h)>? SpriteResolve { get; set; }
    /// <summary>Unfocused/default state sprite imported from the DAT.</summary>
    public uint BackgroundSprite { get; set; }
    public uint FocusRailLeftSprite { get; set; }
    public float FocusRailLeftWidth { get; set; } = 1f;
    public uint FocusRailRightSprite { get; set; }
    public float FocusRailRightWidth { get; set; } = 1f;

    public uint FocusFieldSprite { get; set; }

    public Action<string>? OnSubmit { get; set; }
    public Action? OnFocusGained { get; set; }
    public Action<string>? OnFocusLost { get; set; }
    public Action<string>? OnTextChanged { get; set; }

    private string _textValue = "";

    private string _text
    {
        get => _textValue;
        set
        {
            if (string.Equals(_textValue, value, StringComparison.Ordinal))
                return;
            _textValue = value;
            _textVersion++;
            OnTextChanged?.Invoke(value);
        }
    }

    private int _caret;
    private int? _selAnchor;   // selection fixed end (null = no selection); span = [min,max] with _caret
    public string Text => _text;
    public bool IsFocused => _focused;
    public int CaretPos => _caret;

    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    public int HistoryCount => _history.Count;

    private bool _focused;
    private bool _selecting;   // mouse drag in progress
    private bool _preserveFocusSelectionOnMouseDown;
    private float _scrollX;
    private IReadOnlyList<WrappedLine> _wrappedLines = Array.Empty<WrappedLine>();
    private float _wrappedLineHeight = 14f;
    private int _textVersion;
    private int _wrappedVersion = -1;
    private float _wrappedWidth;
    private bool _suppressNextNewlineChar;

    // Held-key auto-repeat (Silk delivers one KeyDown per physical press).
    private Silk.NET.Input.Key? _repeatKey;
    private double _repeatTimer;
    private const double RepeatDelay = 0.40;   // s before the first repeat
    private const double RepeatRate  = 0.04;   // s between repeats (~25/s)

    public UiField()
    {
        AcceptsFocus = true;
        IsEditControl = true;
        CapturesPointerDrag = true;   // interior drag selects, doesn't move the window
    }

    public override bool ConsumesDatChildren => true;

    // ── Editing primitives ──────────────────────────────────────────────

    public Func<string, string?>? TextReplacer { get; set; }

    public void InsertChar(char c)
    {
        if (!Editable) return;
        if (!OneLine && (c == '\r' || c == '\n'))
            c = '\n';
        else if (c < 0x20 || c == 0x7F)
            return;
        if (CharacterFilter is not null && !CharacterFilter(c)) return;
        DeleteSelection();
        if (_text.Length >= MaxCharacters) return;
        _text = _text.Insert(_caret, c.ToString());
        _caret++;
        _historyIndex = -1;

        if (c == ' '
            && TextReplacer is { } replace
            && _caret == _text.Length
            && replace(_text) is { } replacement)
        {
            SetText(replacement);
        }
    }

    public void Backspace()
    {
        if (!Editable) return;
        if (DeleteSelection()) return;
        if (_caret == 0) return;
        _text = _text.Remove(_caret - 1, 1);
        _caret--;
    }

    public void DeleteForward()
    {
        if (!Editable) return;
        if (DeleteSelection()) return;
        if (_caret >= _text.Length) return;
        _text = _text.Remove(_caret, 1);
    }

    private void MoveCaretTo(int target, bool shift)
    {
        target = Math.Clamp(target, 0, _text.Length);
        if (shift) _selAnchor ??= _caret;
        else _selAnchor = null;
        _caret = target;
        _historyIndex = -1;
    }

    public void MoveCaret(int delta) => MoveCaretTo(_caret + delta, false);

    private void MoveCaret(int delta, bool shift) => MoveCaretTo(_caret + delta, shift);

    // ── Selection ────────────────────────────────────────────────────────

    private (int lo, int hi) SelSpan()
    {
        if (_selAnchor is not { } a || a == _caret) return (_caret, _caret);
        return (Math.Min(a, _caret), Math.Max(a, _caret));
    }

    private bool HasSelection => _selAnchor is { } a && a != _caret;

    private string SelectedText()
    {
        var (lo, hi) = SelSpan();
        return hi > lo ? _text.Substring(lo, hi - lo) : "";
    }

    /// <summary>Remove the selected span (if any). Returns true if it removed anything.</summary>
    private bool DeleteSelection()
    {
        if (!HasSelection) { _selAnchor = null; return false; }
        var (lo, hi) = SelSpan();
        _text = _text.Remove(lo, hi - lo);
        _caret = lo;
        _selAnchor = null;
        return true;
    }

    public void SelectAllText()
    {
        if (_text.Length == 0) { _selAnchor = null; return; }
        _selAnchor = 0;
        _caret = _text.Length;
    }

    public void SetText(string? text)
    {
        _text = text ?? "";
        if (_text.Length > MaxCharacters)
            _text = _text[..MaxCharacters];
        _caret = _text.Length;
        _selAnchor = null;
        _historyIndex = -1;
    }

    private void CopySelection()
    {
        var s = SelectedText();
        if (s.Length > 0 && Keyboard is not null) Keyboard.ClipboardText = s;
    }

    private void CutSelection()
    {
        if (!Editable) return;
        if (!HasSelection) return;
        CopySelection();
        DeleteSelection();
        _historyIndex = -1;
    }

    private void Paste()
    {
        if (!Editable) return;
        if (Keyboard is null) return;
        string clip = Keyboard.ClipboardText ?? "";
        if (clip.Length == 0) return;

        var sb = new System.Text.StringBuilder(clip.Length);
        bool previousCr = false;
        foreach (char ch in clip)
        {
            if (!OneLine && (ch == '\r' || ch == '\n'))
            {
                if (ch == '\n' && previousCr)
                {
                    previousCr = false;
                    continue;
                }
                sb.Append('\n');
                previousCr = ch == '\r';
                continue;
            }
            previousCr = false;
            if (ch >= 0x20 && ch != 0x7F
                && (CharacterFilter is null || CharacterFilter(ch)))
                sb.Append(ch);
        }
        if (sb.Length == 0) return;

        DeleteSelection();
        int room = MaxCharacters - _text.Length;
        if (room <= 0) return;
        string ins = sb.Length > room ? sb.ToString(0, room) : sb.ToString();
        _text = _text.Insert(_caret, ins);
        _caret += ins.Length;
        _historyIndex = -1;
    }

    // ── Submit + history ─────────────────────────────────────────────────

    public void Submit()
    {
        if (!Editable) return;
        var t = _text;
        if (t.Trim().Length == 0)
        {
            if (ClearOnSubmit) Clear();
            return;
        }
        OnSubmit?.Invoke(t);
        if (RecordHistory) PushHistory(t);
        if (ClearOnSubmit) Clear();
    }

    private void Clear() { _text = ""; _caret = 0; _selAnchor = null; _historyIndex = -1; }

    private void PushHistory(string t)
    {
        _history.Add(t);
        if (_history.Count > 100) _history.RemoveAt(0);
        _historyIndex = -1;
    }

    public void HistoryPrev()
    {
        if (_history.Count == 0) return;
        _historyIndex = _historyIndex < 0 ? _history.Count - 1 : Math.Max(0, _historyIndex - 1);
        SetTextFromHistory();
    }

    public void HistoryNext()
    {
        if (_historyIndex < 0) return;
        _historyIndex++;
        if (_historyIndex >= _history.Count) { _historyIndex = -1; Clear(); return; }
        SetTextFromHistory();
    }

    private void SetTextFromHistory()
    {
        _text = _history[_historyIndex];
        _caret = _text.Length;
        _selAnchor = null;
    }

    // ── Geometry ─────────────────────────────────────────────────────────

    private float MeasureTo(int i)
    {
        if (i <= 0) return 0f;
        string s = _text.Substring(0, Math.Min(i, _text.Length));
        return DatFont is { } df ? df.MeasureWidth(s)
             : Font is { } bf ? bf.MeasureWidth(s) : 0f;
    }

    public float CaretPixelX() => MeasureTo(_caret);

    private int HitCharX(float localX)
    {
        float target = localX - Padding - TextAlignmentOffset() + _scrollX;
        if (target <= 0f) return 0;
        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i <= _text.Length; i++)
        {
            float d = MathF.Abs(MeasureTo(i) - target);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    // ── Draw ─────────────────────────────────────────────────────────────

    protected override void OnDraw(UiRenderContext ctx)
    {
        bool lit = _focused && SpriteResolve is not null && FocusFieldSprite != 0;
        if (lit)
        {
            var (tex, tw, th) = SpriteResolve!(FocusFieldSprite);
            if (tex != 0 && tw > 0) ctx.DrawSprite(tex, 0, 0, Width, Height, 0f, 0f, 1f, 1f, Vector4.One);
            else lit = false;
        }
        if (_focused && SpriteResolve is not null)
        {
            if (FocusRailLeftSprite != 0)
            {
                var (tex, tw, _) = SpriteResolve(FocusRailLeftSprite);
                if (tex != 0 && tw > 0)
                    ctx.DrawSprite(tex, 0, 0, FocusRailLeftWidth, Height, 0f, 0f, 1f, 1f, Vector4.One);
            }
            if (FocusRailRightSprite != 0)
            {
                var (tex, tw, _) = SpriteResolve(FocusRailRightSprite);
                if (tex != 0 && tw > 0)
                    ctx.DrawSprite(
                        tex, Width - FocusRailRightWidth, 0,
                        FocusRailRightWidth, Height, 0f, 0f, 1f, 1f, Vector4.One);
            }
        }
        if (!lit && SpriteResolve is not null && BackgroundSprite != 0)
        {
            var (tex, tw, th) = SpriteResolve(BackgroundSprite);
            if (tex != 0 && tw > 0 && th > 0)
            {
                ctx.DrawSprite(tex, 0, 0, Width, Height, 0f, 0f,
                    Width / tw, Height / th, Vector4.One);
                lit = true;
            }
        }
        if (!lit) ctx.DrawFill(0, 0, Width, Height, BackgroundColor);

        if (!OneLine)
        {
            DrawMultiLine(ctx);
            return;
        }

        float lh = DatFont?.LineHeight ?? Font?.LineHeight ?? 14f;
        float ty = (Height - lh) * 0.5f;
        float visibleW = MathF.Max(1f, Width - 2f * Padding);

        float caretX = MeasureTo(_caret);
        float fullW = MeasureTo(_text.Length);
        if (caretX - _scrollX > visibleW) _scrollX = caretX - visibleW;
        if (caretX < _scrollX) _scrollX = caretX;
        _scrollX = Math.Clamp(_scrollX, 0f, MathF.Max(0f, fullW - visibleW));
        float alignX = TextAlignmentOffset();

        int start = 0;
        while (start < _text.Length && MeasureTo(start + 1) <= _scrollX) start++;
        int end = start;
        while (end < _text.Length && MeasureTo(end + 1) - _scrollX <= visibleW) end++;

        if (HasSelection)
        {
            var (lo, hi) = SelSpan();
            float h0 = MathF.Max(alignX + MeasureTo(lo) - _scrollX, 0f);
            float h1 = MathF.Min(alignX + MeasureTo(hi) - _scrollX, visibleW);
            if (h1 > h0) ctx.DrawFill(Padding + h0, ty, h1 - h0, lh, SelectionColor);
        }

        if (end > start)
        {
            string vis = _text.Substring(start, end - start);
            float vx = Padding + alignX + (MeasureTo(start) - _scrollX);
            if (DatFont is { } df2) ctx.DrawStringDat(df2, vis, vx, ty, TextColor, Outline, OutlineColor);
            else ctx.DrawString(vis, vx, ty, TextColor, Font);
        }

        if (_focused)
        {
            float cx = Padding + alignX + (caretX - _scrollX);
            if (cx >= Padding - 1f && cx <= Width - Padding + 1f)
                ctx.DrawFill(cx, ty, 1f, lh, TextColor);
        }
    }

    private float TextAlignmentOffset()
    {
        float visibleW = MathF.Max(1f, Width - 2f * Padding);
        float spare = MathF.Max(0f, visibleW - MeasureTo(_text.Length));
        if (RightAligned) return spare;
        if (Centered) return spare * 0.5f;
        return 0f;
    }

    private void DrawMultiLine(UiRenderContext ctx)
    {
        float lineHeight = DatFont?.LineHeight ?? Font?.LineHeight ?? 14f;
        float visibleWidth = MathF.Max(1f, Width - (2f * Padding));
        float visibleHeight = MathF.Max(1f, Height - (2f * Padding));
        IReadOnlyList<WrappedLine> lines = BuildWrappedLines(visibleWidth);
        _wrappedLines = lines;
        _wrappedVersion = _textVersion;
        _wrappedWidth = visibleWidth;
        _wrappedLineHeight = lineHeight;

        Scroll.LineHeight = Math.Max(1, (int)MathF.Round(lineHeight));
        Scroll.SetExtents(
            Math.Max(1, (int)MathF.Ceiling(lines.Count * lineHeight)),
            Math.Max(1, (int)MathF.Floor(visibleHeight)),
            preserveEnd: false);

        int caretLine = FindCaretLine(lines, _caret);
        if (_focused)
        {
            float caretTop = caretLine * lineHeight;
            float caretBottom = caretTop + lineHeight;
            if (caretTop < Scroll.ScrollY)
                Scroll.SetScrollY((int)MathF.Floor(caretTop));
            else if (caretBottom > Scroll.ScrollY + visibleHeight)
                Scroll.SetScrollY((int)MathF.Ceiling(caretBottom - visibleHeight));
        }

        var (selectionLow, selectionHigh) = SelSpan();
        for (int i = 0; i < lines.Count; i++)
        {
            WrappedLine line = lines[i];
            float y = Padding + (i * lineHeight) - Scroll.ScrollY;
            if (y + lineHeight <= Padding || y >= Height - Padding)
                continue;

            int lineEnd = line.Start + line.Length;
            int highlightLow = Math.Max(selectionLow, line.Start);
            int highlightHigh = Math.Min(selectionHigh, lineEnd);
            if (HasSelection && highlightHigh > highlightLow)
            {
                float x0 = Padding + MeasureRange(
                    line.Start,
                    highlightLow - line.Start);
                float x1 = Padding + MeasureRange(
                    line.Start,
                    highlightHigh - line.Start);
                ctx.DrawFill(
                    x0,
                    y,
                    MathF.Max(0f, x1 - x0),
                    lineHeight,
                    SelectionColor);
            }

            if (DatFont is { } dat)
                ctx.DrawStringDat(dat, line.Text, Padding, y, TextColor, Outline, OutlineColor);
            else if (Font is { } bitmap)
                ctx.DrawString(line.Text, Padding, y, TextColor, bitmap);
        }

        if (_focused && lines.Count > 0)
        {
            WrappedLine line = lines[caretLine];
            int lineColumn = Math.Clamp(
                _caret - line.Start,
                0,
                line.Length);
            float x = Padding + MeasureRange(line.Start, lineColumn);
            float y = Padding + (caretLine * lineHeight) - Scroll.ScrollY;
            ctx.DrawFill(x, y, 1f, lineHeight, TextColor);
        }
    }

    private IReadOnlyList<WrappedLine> BuildWrappedLines(float maximumWidth)
    {
        var lines = new List<WrappedLine>();
        if (_text.Length == 0)
        {
            lines.Add(new WrappedLine(0, 0, string.Empty));
            return lines;
        }

        int start = 0;
        while (start < _text.Length)
        {
            if (_text[start] == '\n')
            {
                lines.Add(new WrappedLine(start, 0, string.Empty));
                start++;
                continue;
            }

            int paragraphEnd = _text.IndexOf('\n', start);
            if (paragraphEnd < 0)
                paragraphEnd = _text.Length;
            int end = start;
            int lastWhitespaceEnd = -1;
            while (end < paragraphEnd)
            {
                int candidateEnd = end + 1;
                if (MeasureRange(start, candidateEnd - start) > maximumWidth
                    && end > start)
                    break;
                end = candidateEnd;
                if (char.IsWhiteSpace(_text[end - 1]))
                    lastWhitespaceEnd = end;
            }

            if (end < paragraphEnd && lastWhitespaceEnd > start)
                end = lastWhitespaceEnd;
            if (end == start)
                end++;

            int length = end - start;
            lines.Add(new WrappedLine(
                start,
                length,
                _text.Substring(start, length)));
            start = end;
            if (start == paragraphEnd && start < _text.Length)
                start++;
        }

        if (_text.EndsWith('\n'))
            lines.Add(new WrappedLine(_text.Length, 0, string.Empty));
        return lines;
    }

    private int FindCaretLine(IReadOnlyList<WrappedLine> lines, int caret)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            WrappedLine line = lines[i];
            int end = line.Start + line.Length;
            if (caret < end || caret == end && i == lines.Count - 1)
                return i;
            if (caret == end && i + 1 < lines.Count
                && lines[i + 1].Start > caret)
                return i;
        }
        return Math.Max(0, lines.Count - 1);
    }

    private float MeasureRange(int start, int length)
    {
        if (length <= 0) return 0f;
        string value = _text.Substring(start, length);
        return DatFont?.MeasureWidth(value)
               ?? Font?.MeasureWidth(value)
               ?? value.Length * 8f;
    }

    internal void EnsureWrappedLinesCurrent()
    {
        if (_wrappedVersion == _textVersion && _wrappedLines.Count > 0)
            return;

        float width = _wrappedWidth > 0f
            ? _wrappedWidth
            : MathF.Max(1f, Width - (2f * Padding));
        _wrappedLines = BuildWrappedLines(width);
        _wrappedVersion = _textVersion;
    }

    private int HitChar(float localX, float localY)
    {
        if (OneLine)
            return HitCharX(localX);
        EnsureWrappedLinesCurrent();
        if (_wrappedLines.Count == 0)
            return _text.Length;

        int lineIndex = Math.Clamp(
            (int)MathF.Floor(
                (localY - Padding + Scroll.ScrollY)
                / MathF.Max(1f, _wrappedLineHeight)),
            0,
            _wrappedLines.Count - 1);
        WrappedLine line = _wrappedLines[lineIndex];
        float target = MathF.Max(0f, localX - Padding);
        int best = 0;
        float bestDistance = float.MaxValue;
        for (int col = 0; col <= line.Length; col++)
        {
            float distance = MathF.Abs(
                MeasureRange(line.Start, col) - target);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = col;
        }
        return line.Start + best;
    }

    // ── Auto-repeat ──────────────────────────────────────────────────────

    protected override void OnTick(double deltaSeconds)
    {
        if (!Editable || _repeatKey is not { } k) return;
        _repeatTimer -= deltaSeconds;
        if (_repeatTimer > 0) return;
        _repeatTimer = RepeatRate;
        bool shift = ShiftHeld();
        switch (k)
        {
            case Silk.NET.Input.Key.Backspace: Backspace(); break;
            case Silk.NET.Input.Key.Delete:    DeleteForward(); break;
            case Silk.NET.Input.Key.Left:      MoveCaret(-1, shift); break;
            case Silk.NET.Input.Key.Right:     MoveCaret(1, shift); break;
            default: _repeatKey = null; break;
        }
    }

    private void StartRepeat(Silk.NET.Input.Key k) { _repeatKey = k; _repeatTimer = RepeatDelay; }

    private bool CtrlHeld() => Keyboard is not null
        && (Keyboard.IsKeyPressed(Silk.NET.Input.Key.ControlLeft)
            || Keyboard.IsKeyPressed(Silk.NET.Input.Key.ControlRight));

    private bool ShiftHeld() => Keyboard is not null
        && (Keyboard.IsKeyPressed(Silk.NET.Input.Key.ShiftLeft)
            || Keyboard.IsKeyPressed(Silk.NET.Input.Key.ShiftRight));

    // ── Events ───────────────────────────────────────────────────────────

    public override bool OnEvent(in UiEvent e)
    {
        switch (e.Type)
        {
            case UiEventType.FocusGained:
                _focused = true;
                if (SelectAllOnFocus)
                {
                    SelectAllText();
                    _preserveFocusSelectionOnMouseDown = true;
                }
                OnFocusGained?.Invoke();
                return true;
            case UiEventType.FocusLost:
                OnFocusLost?.Invoke(_text);
                _focused = false; _historyIndex = -1;
                _selAnchor = null; _selecting = false; _repeatKey = null;
                _preserveFocusSelectionOnMouseDown = false;
                return true;

            case UiEventType.Char:
            {
                char value = (char)e.Data0;
                if (_suppressNextNewlineChar
                    && (value == '\r' || value == '\n'))
                {
                    _suppressNextNewlineChar = false;
                    return true;
                }
                _suppressNextNewlineChar = false;
                InsertChar(value);
                return true;
            }

            case UiEventType.MouseDown:
                if (_preserveFocusSelectionOnMouseDown)
                {
                    _preserveFocusSelectionOnMouseDown = false;
                    _selecting = false;
                    return true;
                }
                _caret = HitChar(e.Data1, e.Data2);
                _selAnchor = Selectable ? _caret : null;
                _selecting = Selectable;
                return true;
            case UiEventType.MouseMove:
                if (Selectable && _selecting)
                    _caret = HitChar(e.Data1, e.Data2);
                return true;
            case UiEventType.MouseUp:
                _selecting = false;
                return true;

            case UiEventType.KeyUp:
                if ((Silk.NET.Input.Key)e.Data0 == _repeatKey) _repeatKey = null;
                return true;

            case UiEventType.KeyDown:
            {
                if (!Editable)
                    return true;
                var key = (Silk.NET.Input.Key)e.Data0;
                if (CtrlHeld())
                {
                    switch (key)
                    {
                        case Silk.NET.Input.Key.A when Selectable: SelectAllText(); return true;
                        case Silk.NET.Input.Key.C when Selectable: CopySelection(); return true;
                        case Silk.NET.Input.Key.X when Selectable: CutSelection(); return true;
                        case Silk.NET.Input.Key.V: Paste();         return true;
                    }
                    return true;
                }

                bool shift = Selectable && ShiftHeld();
                switch (key)
                {
                    case Silk.NET.Input.Key.Escape:
                        FindRoot()?.SetKeyboardFocus(null);
                        return true;

                    case Silk.NET.Input.Key.Enter:
                    case Silk.NET.Input.Key.KeypadEnter:
                        if (!OneLine)
                        {
                            InsertChar('\n');
                            _suppressNextNewlineChar = true;
                            return true;
                        }
                        Submit();
                        FindRoot()?.SetKeyboardFocus(null);   // exit write mode after sending
                        return true;
                    case Silk.NET.Input.Key.Backspace: Backspace();        StartRepeat(key); return true;
                    case Silk.NET.Input.Key.Delete:    DeleteForward();    StartRepeat(key); return true;
                    case Silk.NET.Input.Key.Left:      MoveCaret(-1, shift); StartRepeat(key); return true;
                    case Silk.NET.Input.Key.Right:     MoveCaret(1, shift);  StartRepeat(key); return true;
                    case Silk.NET.Input.Key.Home:      MoveCaretTo(0, shift); return true;
                    case Silk.NET.Input.Key.End:       MoveCaretTo(_text.Length, shift); return true;
                    case Silk.NET.Input.Key.Up:        HistoryPrev(); return true;
                    case Silk.NET.Input.Key.Down:      HistoryNext(); return true;
                }
                return false;
            }
            case UiEventType.Scroll:
                if (!OneLine)
                {
                    Scroll.ScrollByLines(-Math.Sign(e.Data0));
                    return true;
                }
                return false;
            case UiEventType.Click:
                if (!Editable)
                {
                    OnReadOnlyClick?.Invoke();
                    return true;
                }
                return false;
        }
        return false;
    }
}
