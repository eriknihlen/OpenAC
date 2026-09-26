using System.Numerics;

namespace AcDream.App.UI;

/// <summary>A wrapped transcript that follows new entries only while the reader is at its end.</summary>
public sealed class UiMarkupLog : UiPanel
{
    private readonly record struct Row(long Entry, int Offset, string Text);
    private readonly List<Row> _rows = [];
    private readonly UiScrollbar _bar;
    private IReadOnlyList<string>? _snapshot;
    private int _first;
    private float _width = -1, _height = -1;
    private UiDatFont? _font;
    private int _lineHeight = 18;
    private bool _followEnd = true, _updating;
    public Func<IReadOnlyList<string>> ItemsSource { get; set; } = static () => Array.Empty<string>();
    public Func<int> FirstIndexSource { get; set; } = static () => 0;
    public UiDatFont? DatFont { get; set; }
    public UiScrollable Scroll { get; } = new();
    public bool FollowingEnd => _followEnd;
    public Vector4 TextColor { get; set; } = new(.91f, .87f, .76f, 1f);
    public PluginUiPalette? ThemePalette { get; set; }
    internal long? FirstVisibleEntry => _rows.Count == 0 ? null : _rows[Math.Min(_rows.Count - 1, Scroll.ScrollY / _lineHeight)].Entry;
    internal string? FirstVisibleText => _rows.Count == 0 ? null : _rows[Math.Min(_rows.Count - 1, Scroll.ScrollY / _lineHeight)].Text;

    public UiMarkupLog(Func<uint, (uint, int, int)> resolve)
    {
        BackgroundColor = new(0, 0, 0, .92f);
        BorderColor = new(.46f, .37f, .16f, 1);
        _bar = new UiScrollbar { Model = Scroll, Width = 16, SpriteResolve = resolve, Anchors = AnchorEdges.None };
        RetailScrollbarChrome.ApplyVertical(_bar);
        AddChild(_bar);
        Scroll.PositionChanged += () => { if (!_updating) _followEnd = Scroll.AtEnd; };
    }

    protected override void OnTick(double dt) { base.OnTick(dt); Refresh(); }

    internal void Refresh()
    {
        var items = ItemsSource();
        int first = FirstIndexSource();
        if (ReferenceEquals(items, _snapshot) && first == _first && Width == _width
            && Height == _height && ReferenceEquals(DatFont, _font)) return;
        Row? anchor = _rows.Count == 0 ? null : _rows[Math.Min(_rows.Count - 1, Scroll.ScrollY / _lineHeight)];
        int remainder = Scroll.ScrollY % _lineHeight;
        _snapshot = items; _first = first; _width = Width; _height = Height; _font = DatFont;
        _lineHeight = Math.Max(14, (int)MathF.Ceiling(DatFont?.LineHeight ?? 14) + 2);
        float available = Math.Max(1, Width - 26);
        _rows.Clear();
        for (int i = 0; i < items.Count; i++) Wrap((long)first + i, items[i] ?? string.Empty, available);
        _updating = true;
        try
        {
            Scroll.LineHeight = _lineHeight;
            Scroll.SetExtents(_rows.Count * _lineHeight, Math.Max(0, (int)Height - 8));
            if (_followEnd) Scroll.ScrollToEnd();
            else if (anchor is { } old)
            {
                int index = _rows.FindLastIndex(r => r.Entry == old.Entry && r.Offset <= old.Offset);
                Scroll.SetScrollY(index < 0 ? 0 : index * _lineHeight + Math.Min(remainder, _lineHeight - 1));
            }
        }
        finally { _updating = false; }
        _bar.Left = Math.Max(0, Width - 17); _bar.Top = 1;
        _bar.Height = Math.Max(0, Height - 2);
        _bar.Visible = Scroll.HasOverflow;
    }

    private void Wrap(long entry, string text, float available)
    {
        if (text.Length == 0) { _rows.Add(new(entry, 0, "")); return; }
        int start = 0;
        while (start < text.Length)
        {
            int end = start, space = -1;
            float width = 0;
            while (end < text.Length && text[end] != '\n')
            {
                char c = text[end];
                float advance = DatFont is { } font
                    ? font.TryGetGlyph(c, out var glyph) ? UiDatFont.GlyphAdvance(glyph) : 0
                    : 7;
                if (end > start && width + advance > available) break;
                width += advance;
                if (char.IsWhiteSpace(c)) space = end;
                end++;
            }
            bool newline = end < text.Length && text[end] == '\n';
            if (!newline && end < text.Length && space > start) end = space;
            _rows.Add(new(entry, start, text[start..end].TrimEnd('\r')));
            start = end;
            if (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        }
    }

    protected override void OnDraw(UiRenderContext ctx)
    {
        Refresh();
        var p = ThemePalette;
        ctx.DrawFill(0, 0, Width, Height, p?.Field ?? BackgroundColor);
        ctx.DrawRectOutline(0, 0, Width, Height, p?.Border ?? BorderColor, 1);
        _bar.RetailArt = p is null;
        _bar.PlainTrackColor = p?.Field ?? BackgroundColor;
        _bar.PlainBorderColor = p?.Border ?? BorderColor;
        _bar.PlainNubColor = p?.Muted ?? TextColor;
        ctx.PushClip(4, 4, Math.Max(0, Width - 25), Math.Max(0, Height - 8));
        try
        {
            int top = Scroll.ScrollY / _lineHeight;
            for (int i = top; i < _rows.Count; i++)
            {
                float y = 4 + i * _lineHeight - Scroll.ScrollY;
                if (y >= Height - 4) break;
                if (DatFont is { } font) ctx.DrawStringDat(font, _rows[i].Text, 4, y, p?.Text ?? TextColor, p is null);
                else ctx.DrawString(_rows[i].Text, 4, y, p?.Text ?? TextColor);
            }
        }
        finally { ctx.PopClip(); }
    }

    public override bool OnEvent(in UiEvent e)
    {
        if (e.Type != UiEventType.Scroll) return base.OnEvent(e);
        Refresh();
        Scroll.ScrollByLines(-e.Data0 * 3);
        return true;
    }
}
