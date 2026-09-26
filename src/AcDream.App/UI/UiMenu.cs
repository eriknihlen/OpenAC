using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Silk.NET.Input;

namespace AcDream.App.UI;

public sealed class UiMenu : UiElement
{
    public readonly record struct MenuItem(string Label, object? Payload);

    public IReadOnlyList<MenuItem> Items { get; set; } = System.Array.Empty<MenuItem>();

    /// <summary>Enables a live popup label filter without changing the selected payload.</summary>
    public bool Searchable { get; set; }
    private readonly UiField _searchField = new() { RecordHistory = false, Selectable = true, MaxCharacters = 256 };
    private IReadOnlyList<MenuItem> _filteredItems = Array.Empty<MenuItem>();
    private IReadOnlyList<MenuItem> DisplayItems => Searchable ? _filteredItems : Items;
    internal IReadOnlyList<MenuItem> FilteredItemsForTest => DisplayItems;
    private float SearchHeight => Searchable ? MathF.Max(24f, RowHeight + 6f) : 0f;
    private void FilterItems()
    {
        string[] words = _searchField.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        _filteredItems = Items.Where(item => words.All(word => item.Label.Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
        _hoveredPopupIndex = -1;
        PopupScroll.SetScrollY(0);
    }

    public object? Selected { get; set; }

    public Action<object?>? OnSelect { get; set; }

    public Action? OnOpen { get; set; }

    public Action? BeforeOpen { get; set; }

    /// <summary>Per-payload enabled gate (disabled rows render greyed + are inert). Null ⇒ all enabled.</summary>
    public Func<object?, bool>? EnabledProvider { get; set; }

    public Func<string>? ButtonLabelProvider { get; set; }

    public string? TooltipText { get; set; }

    public Func<string?>? TooltipTextProvider { get; set; }

    /// <inheritdoc />
    public override string? GetTooltipText()
    {
        string? live = TooltipTextProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(live))
            return live;
        return string.IsNullOrWhiteSpace(TooltipText)
            ? base.GetTooltipText()
            : TooltipText;
    }

    public int   RowsPerColumn { get; set; } = 7;
                                                       // ALSO the visible-row window height when Scrollable
    public float RowHeight     { get; set; } = 17f;
    public float ColumnWidth   { get; set; } = 191f;  // dat item template W=191

    public bool Scrollable { get; set; }

    public UiScrollable PopupScroll { get; } = new();

    public float ScrollbarWidth { get; set; } = 16f;
    public float ScrollButtonExtent { get; set; } = 16f;

    public uint ScrollTrackSprite { get; set; }
    public uint ScrollThumbSprite { get; set; }
    public uint ScrollThumbTopSprite { get; set; }
    public uint ScrollThumbBottomSprite { get; set; }
    public uint ScrollUpSprite { get; set; }
    public uint ScrollDownSprite { get; set; }

    public bool PopupScrollbarHideWhenDisabled { get; set; }

    internal bool IsPopupScrollbarPresentationVisible
        => !PopupScrollbarHideWhenDisabled || PopupContentOverflows;

    private bool _draggingPopupThumb;
    private bool _selectingSearch;
    private float _popupThumbDragOffset;

    private int _hoveredPopupIndex = -1;

    internal int HoveredPopupIndexForTest => _hoveredPopupIndex;

    public override bool ReceivesHoverMouseMove => _open && !RetailButtonArt;

    private const int Border = RetailChromeSprites.Border; // 8-piece bevel thickness (5px)
    public float TextIndent { get; set; } = 19f;
    public float ButtonTextIndent { get; set; } = 20f;

    public uint ArrowCapClosedSprite { get; set; }
    public uint ArrowCapOpenSprite { get; set; }
    /// <summary>Authored native size of the arrow-cap overlay (17x19 for vendor's
    /// dropdown) — drawn unstretched, right-anchored to the button's own width, exactly
    /// mirroring the authored element's own X=Width-17,Y=0 placement.</summary>
    public float ArrowCapWidth { get; set; } = 17f;
    public float ArrowCapHeight { get; set; } = 19f;

    public uint CurrentArrowCapSprite => _open ? ArrowCapOpenSprite : ArrowCapClosedSprite;

    public uint CurrentFaceSpriteForTest => _facePressed ? PressedSprite : NormalSprite;

    public UiDatFont? DatFont { get; set; }

    public UiDatFont? ButtonDatFont { get; set; }

    public AcDream.App.Rendering.BitmapFont? Font { get; set; }

    public bool Outline { get; set; }

    public Vector4 OutlineColor { get; set; } = UiRenderContext.DefaultOutlineColor;

    public Func<uint, (uint tex, int w, int h)>? SpriteResolve { get; set; }

    public uint NormalSprite { get; set; }
    public uint PressedSprite { get; set; }
    public uint PopupBgSprite { get; set; }
    public uint ItemNormalSprite { get; set; }
    public uint ItemHighlightSprite { get; set; }

    public bool PopupSizeToContent { get; set; }

    public bool ButtonTextCentered { get; set; }

    public bool ItemTextCentered { get; set; }

    public Vector4 TextColor { get; set; } = new(1f, 0.92f, 0.72f, 1f);
    public Vector4 TextColorAvailable { get; set; } = new(1f, 1f, 1f, 1f);
    public Vector4 TextColorGhosted { get; set; } = new(0.5f, 0.5f, 0.5f, 1f);

    public bool RetailButtonArt { get; set; } = true;

    /// <summary>Uses flat scrollbar rendering in a plain popup even when sprite chrome is available.</summary>
    public bool PlainPopupScrollbar { get; set; }

    public Vector4 PlainBackgroundColor { get; set; } = new(0f, 0f, 0f, 0.92f);
    public Vector4 PlainBorderColor { get; set; } = new(0.46f, 0.37f, 0.16f, 1f);
    public Vector4 PlainOpenBorderColor { get; set; } = new(0.70f, 0.58f, 0.24f, 1f);
    public Vector4 PlainTextColor { get; set; } = new(0.91f, 0.87f, 0.76f, 1f);
    public Vector4 PlainTriangleColor { get; set; } = new(0.91f, 0.87f, 0.76f, 1f);
    public const float PlainPadding = 3f;

    public Vector4 PlainSelectedColor { get; set; } = new(0.28f, 0.23f, 0.08f, 0.95f);
    public Vector4 PlainHoverColor { get; set; } = new(0.40f, 0.33f, 0.14f, 0.95f);

    private bool _open;

    private bool _facePressed;

    public bool IsOpen => _open;

    private void SetOpen(bool value)
    {
        if (_open == value) return;
        // Before _open flips, so a handler that replaces Items is reflected in
        // the very first measure/draw of this opening.
        if (value)
        {
            if (Searchable)
            {
                AcceptsFocus = true;
                IsEditControl = true;
                _searchField.SetText("");
            }
            BeforeOpen?.Invoke();
            if (Items.Count == 0 && !Searchable) return;
            OnOpen?.Invoke();
            if (Searchable) FilterItems();
        }
        if (!value)
        {
            EndSearchSelection();
            _draggingPopupThumb = false;
        }
        _open = value;
        _hoveredPopupIndex = -1;   // stale hover from the last time this popup was open
        if (FindRoot() is not { } root) return;
        if (value)
        {
            root.SetActivePopup(this, () => SetOpen(false));
            if (Searchable)
            {
                _searchField.Keyboard = root.Keyboard;
                root.SetKeyboardFocus(this);
            }
        }
        else
        {
            root.ClearActivePopup(this);
            if (Searchable && root.KeyboardFocus == this) root.SetKeyboardFocus(null);
        }
    }

    private int   ColumnCount => Scrollable
        ? 1
        : System.Math.Max(Searchable ? 1 : 0, (DisplayItems.Count + RowsPerColumn - 1) / System.Math.Max(1, RowsPerColumn));
    private float InteriorW => Scrollable
        ? ColumnWidth + EffectiveScrollbarWidth
        : ColumnCount * ColumnWidth;

    private int EffectiveVisibleRows => Scrollable && PopupSizeToContent
        ? System.Math.Max(1, DisplayItems.Count)
        : RowsPerColumn;

    private bool PopupContentOverflows => DisplayItems.Count > EffectiveVisibleRows;

    private float EffectiveScrollbarWidth
        => IsPopupScrollbarPresentationVisible ? ScrollbarWidth : 0f;

    private float InteriorH => EffectiveVisibleRows * RowHeight;
    private float OuterW => InteriorW + 2 * Border;
    private float OuterH => InteriorH + SearchHeight + 2 * Border;

    public float PopupOuterHeight => OuterH;

    public float PopupOuterWidth => OuterW;

    /// <summary>
    /// Sizes the rows so the open list comes out exactly this wide,
    /// whether or not it has overflowed and brought a scrollbar with it.
    /// Call it after the items change.
    /// </summary>
    public void SizePopupToWidth(float width) =>
        ColumnWidth = System.MathF.Max(
            1f, width - (2 * Border) - EffectiveScrollbarWidth);

    public bool OpenUpward { get; set; } = true;

    private float PopupTop => OpenUpward ? -OuterH : Height;

    public UiMenu()
    {
        CapturesPointerDrag = true;
        _searchField.OnTextChanged = _ => FilterItems();
    }
    protected override void OnTick(double deltaSeconds)
    {
        if (_open && Searchable) _searchField.TickSelfAndChildren(deltaSeconds);
    }
    private void DrawSearch(UiRenderContext ctx)
    {
        if (!Searchable) return;
        _searchField.Left = Border;
        _searchField.Top = PopupTop + Border;
        _searchField.Width = InteriorW;
        _searchField.Height = SearchHeight - 2f;
        _searchField.DatFont = DatFont;
        _searchField.Font = Font;
        _searchField.TextColor = RetailButtonArt ? TextColorAvailable : PlainTextColor;
        _searchField.BackgroundColor = PlainBackgroundColor;
        _searchField.SelectionColor = PlainSelectedColor;
        _searchField.DrawSelfAndChildren(ctx);
        ctx.DrawRectOutline(Border, PopupTop + Border, InteriorW, SearchHeight - 2f, PlainOpenBorderColor, 1f);
        if (_searchField.Text.Length == 0)
            DrawLabel(ctx, "Search...", Border + 7f, PopupTop + Border + (SearchHeight - LineH()) * 0.5f, TextColorGhosted);
        if (DisplayItems.Count == 0)
            DrawLabel(ctx, "No matches", Border + PlainPadding, PopupTop + Border + SearchHeight + 2f, TextColorGhosted);
    }

    public override bool ConsumesDatChildren => true;

    protected override bool ClipsChildren => false;

    protected override bool ExpandsClipForPopup => true;

    protected override void OnDraw(UiRenderContext ctx)
    {
        if (!RetailButtonArt)
        {
            DrawPlainClosedState(ctx);
            return;
        }

        var resolve = SpriteResolve;

        // Button face (3-sliced so it can widen to fit the label) + the active-target label.
        if (resolve is not null)
        {
            var (tex, tw, _) = resolve(_facePressed ? PressedSprite : NormalSprite);
            if (tex != 0 && tw > 0) DrawButtonFace(ctx, tex, tw);
        }
        string caption = ButtonLabelProvider?.Invoke() ?? "";
        UiDatFont? captionFont = ButtonDatFont ?? DatFont;
        float captionW = captionFont?.MeasureWidth(caption)
            ?? Font?.MeasureWidth(caption) ?? caption.Length * 7f;
        float captionLineH = captionFont?.LineHeight ?? Font?.LineHeight ?? 14f;
        float capX = ButtonTextCentered
            ? MathF.Max(0f, (Width - (ArrowCapClosedSprite != 0 ? ArrowCapWidth : 0f) - captionW) * 0.5f)
            : ButtonTextIndent;
        if (captionFont is { } cf)
            ctx.DrawStringDat(cf, caption, capX, (Height - captionLineH) * 0.5f, TextColor, Outline, OutlineColor);
        else
            ctx.DrawString(caption, capX, (Height - captionLineH) * 0.5f, TextColor, Font);

        if (resolve is not null) DrawArrowCap(ctx, resolve);
    }

    private void DrawPlainClosedState(UiRenderContext ctx)
    {
        ctx.DrawFill(0f, 0f, Width, Height, PlainBackgroundColor);
        Vector4 border = (_open || _facePressed) ? PlainOpenBorderColor : PlainBorderColor;
        ctx.DrawRectOutline(0f, 0f, Width, Height, border, 1f);

        string caption = ButtonLabelProvider?.Invoke() ?? "";
        UiDatFont? captionFont = ButtonDatFont ?? DatFont;
        float captionLineH = captionFont?.LineHeight ?? Font?.LineHeight ?? 14f;
        float textY = (Height - captionLineH) * 0.5f;
        if (captionFont is { } cf)
            ctx.DrawStringDat(cf, caption, PlainPadding, textY, PlainTextColor, Outline, OutlineColor);
        else
            ctx.DrawString(caption, PlainPadding, textY, PlainTextColor, Font);

        DrawPlainTriangle(ctx);
    }

    private void DrawPlainTriangle(UiRenderContext ctx)
    {
        const float w = 7f, rightMargin = 6f;
        float x = Width - rightMargin - w;
        float y = (Height - 4f) * 0.5f;
        ctx.DrawFill(x,      y,      w,      1f, PlainTriangleColor);
        ctx.DrawFill(x + 1f, y + 1f, w - 2f, 1f, PlainTriangleColor);
        ctx.DrawFill(x + 2f, y + 2f, w - 4f, 1f, PlainTriangleColor);
        ctx.DrawFill(x + 3f, y + 3f, w - 6f, 1f, PlainTriangleColor);
    }

    private const float FaceCapL = 20f, FaceCapR = 12f;

    private void DrawButtonFace(UiRenderContext ctx, uint tex, float tw)
    {
        float uL = FaceCapL / tw, uR = (tw - FaceCapR) / tw;
        float midDest = Width - FaceCapL - FaceCapR;
        ctx.DrawSprite(tex, 0f,               0f, FaceCapL, Height, 0f, 0f, uL, 1f, Vector4.One); // LED cap
        if (midDest > 0f)
            ctx.DrawSprite(tex, FaceCapL,      0f, midDest,  Height, uL, 0f, uR, 1f, Vector4.One); // gold body (stretched)
        ctx.DrawSprite(tex, Width - FaceCapR, 0f, FaceCapR, Height, uR, 0f, 1f, 1f, Vector4.One); // arrow cap
    }

    private void DrawArrowCap(UiRenderContext ctx, Func<uint, (uint tex, int w, int h)> resolve)
    {
        uint id = _open ? ArrowCapOpenSprite : ArrowCapClosedSprite;
        if (id == 0) return;
        var (tex, tw, th) = resolve(id);
        if (tex == 0 || tw == 0 || th == 0) return;
        float dx = Width - ArrowCapWidth;
        ctx.DrawSprite(tex, dx, 0f, ArrowCapWidth, ArrowCapHeight, 0f, 0f, 1f, 1f, Vector4.One);
    }

    public float NaturalButtonWidth()
    {
        string text = ButtonLabelProvider?.Invoke() ?? "";
        UiDatFont? nf = ButtonDatFont ?? DatFont;
        float textW = nf?.MeasureWidth(text) ?? Font?.MeasureWidth(text) ?? text.Length * 7f;
        return ButtonTextIndent + textW + 4f + FaceCapR;
    }

    protected override void OnDrawOverlay(UiRenderContext ctx)
    {
        if (!_open) return;

        if (!RetailButtonArt)
        {
            ctx.PushAlphaAbsolute(1f);
            try
            {
                if (Scrollable)
                    DrawScrollablePopupPlain(ctx);
                else
                    DrawGridPopupPlain(ctx);
                DrawSearch(ctx);
            }
            finally { ctx.PopAlpha(); }
            return;
        }

        var resolve = SpriteResolve;
        if (resolve is null) return;

        ctx.PushAlphaAbsolute(1f);
        try
        {
            if (Scrollable)
                DrawScrollablePopup(ctx, resolve);
            else
                DrawGridPopup(ctx, resolve);
            DrawSearch(ctx);
        }
        finally { ctx.PopAlpha(); }
    }

    private void DrawGridPopup(UiRenderContext ctx, Func<uint, (uint tex, int w, int h)> resolve)
    {
        float outerTop = PopupTop;                 // G7: direction-aware (see PopupTop's doc)
        float inX = Border, inY = outerTop + Border + SearchHeight; // interior origin (inside the bevel)

        DrawBevel(ctx, resolve, 0f, outerTop, OuterW, OuterH);
        DrawSprite(ctx, resolve, PopupBgSprite, inX, inY, InteriorW, InteriorH);  // panel fill behind rows

        for (int i = 0; i < DisplayItems.Count; i++)
        {
            int col = i / RowsPerColumn, row = i % RowsPerColumn;
            float x = inX + col * ColumnWidth, y = inY + row * RowHeight;
            bool selected = Equals(DisplayItems[i].Payload, Selected) || (Searchable && i == _hoveredPopupIndex);
            DrawSprite(ctx, resolve, selected ? ItemHighlightSprite : ItemNormalSprite, x, y, ColumnWidth, RowHeight);
        }

        float textY = (RowHeight - LineH()) * 0.5f;
        for (int i = 0; i < DisplayItems.Count; i++)
        {
            int col = i / RowsPerColumn, row = i % RowsPerColumn;
            // Items grey out when unavailable; when EnabledProvider is null all items are enabled.
            bool avail = EnabledProvider?.Invoke(DisplayItems[i].Payload) ?? true;
            DrawLabel(ctx, DisplayItems[i].Label, inX + col * ColumnWidth + ItemTextX(DisplayItems[i].Label),
                      inY + row * RowHeight + textY,
                      avail ? TextColorAvailable : TextColorGhosted);
        }
    }

    private float ItemTextX(string label) => ItemTextCentered
        ? MathF.Max(0f, (ColumnWidth - MeasureText(label)) * 0.5f)
        : TextIndent;

    private float MeasureText(string s)
        => DatFont?.MeasureWidth(s) ?? Font?.MeasureWidth(s) ?? s.Length * 7f;

    private void DrawScrollablePopup(UiRenderContext ctx, Func<uint, (uint tex, int w, int h)> resolve)
    {
        ConfigurePopupScroll();

        float outerTop = PopupTop;                 // G7: direction-aware (see PopupTop's doc)
        float inX = Border, inY = outerTop + Border + SearchHeight;

        DrawBevel(ctx, resolve, 0f, outerTop, OuterW, OuterH);
        DrawSprite(ctx, resolve, PopupBgSprite, inX, inY, ColumnWidth, InteriorH);

        int start = VisibleTopRow;
        int count = System.Math.Min(EffectiveVisibleRows, DisplayItems.Count - start);
        float textY = (RowHeight - LineH()) * 0.5f;
        for (int i = 0; i < count; i++)
        {
            int idx = start + i;
            float y = inY + i * RowHeight;
            bool selected = Equals(DisplayItems[idx].Payload, Selected) || (Searchable && idx == _hoveredPopupIndex);
            DrawSprite(ctx, resolve, selected ? ItemHighlightSprite : ItemNormalSprite, inX, y, ColumnWidth, RowHeight);
        }
        for (int i = 0; i < count; i++)
        {
            int idx = start + i;
            float y = inY + i * RowHeight;
            bool avail = EnabledProvider?.Invoke(DisplayItems[idx].Payload) ?? true;
            DrawLabel(ctx, DisplayItems[idx].Label, inX + ItemTextX(DisplayItems[idx].Label), y + textY,
                      avail ? TextColorAvailable : TextColorGhosted);
        }

        DrawPopupScrollbar(ctx, resolve, inX + ColumnWidth, inY);
    }

    private void ConfigurePopupScroll()
    {
        int lineHeight = System.Math.Max(1, (int)MathF.Round(RowHeight));
        PopupScroll.LineHeight = lineHeight;
        PopupScroll.SetExtents(DisplayItems.Count * lineHeight, EffectiveVisibleRows * lineHeight);
    }

    private int VisibleTopRow
    {
        get
        {
            int lineHeight = System.Math.Max(1, (int)MathF.Round(RowHeight));
            int maxStart = System.Math.Max(0, DisplayItems.Count - EffectiveVisibleRows);
            int row = (int)MathF.Round((float)PopupScroll.ScrollY / lineHeight);
            return System.Math.Clamp(row, 0, maxStart);
        }
    }

    private void DrawPopupScrollbar(
        UiRenderContext ctx, Func<uint, (uint tex, int w, int h)> resolve, float x, float y)
    {
        if (!IsPopupScrollbarPresentationVisible) return;

        DrawSprite(ctx, resolve, ScrollTrackSprite, x, y, ScrollbarWidth, InteriorH);

        float decExtent = System.Math.Clamp(ScrollButtonExtent, 0f, InteriorH);
        float incExtent = System.Math.Clamp(ScrollButtonExtent, 0f, InteriorH - decExtent);
        DrawSprite(ctx, resolve, ScrollUpSprite, x, y, ScrollbarWidth, decExtent);
        DrawSprite(ctx, resolve, ScrollDownSprite, x, y + InteriorH - incExtent, ScrollbarWidth, incExtent);

        if (!PopupScroll.HasOverflow) return;

        float trackTop = decExtent;
        float trackLen = MathF.Max(0f, InteriorH - decExtent - incExtent);
        var (ty, th) = UiScrollbar.ThumbRect(PopupScroll, trackTop, trackLen);
        const float capH = 3f;
        if (ScrollThumbTopSprite != 0 && ScrollThumbBottomSprite != 0 && th >= 2f * capH)
        {
            DrawSprite(ctx, resolve, ScrollThumbTopSprite, x, y + ty, ScrollbarWidth, capH);
            DrawSprite(ctx, resolve, ScrollThumbSprite, x, y + ty + capH, ScrollbarWidth, th - 2f * capH);
            DrawSprite(ctx, resolve, ScrollThumbBottomSprite, x, y + ty + th - capH, ScrollbarWidth, capH);
        }
        else
        {
            DrawSprite(ctx, resolve, ScrollThumbSprite, x, y + ty, ScrollbarWidth, th);
        }
    }


    private void DrawGridPopupPlain(UiRenderContext ctx)
    {
        float outerTop = PopupTop;
        float inX = Border, inY = outerTop + Border + SearchHeight;

        ctx.DrawFill(0f, outerTop, OuterW, OuterH, PlainBackgroundColor);
        ctx.DrawRectOutline(0f, outerTop, OuterW, OuterH, PlainBorderColor, 1f);

        for (int i = 0; i < DisplayItems.Count; i++)
        {
            int col = i / RowsPerColumn, row = i % RowsPerColumn;
            float x = inX + col * ColumnWidth, y = inY + row * RowHeight;
            bool selected = Equals(DisplayItems[i].Payload, Selected) || (Searchable && i == _hoveredPopupIndex);
            if (selected)
                ctx.DrawFill(x, y, ColumnWidth, RowHeight, PlainSelectedColor);
            else if (i == _hoveredPopupIndex)
                ctx.DrawFill(x, y, ColumnWidth, RowHeight, PlainHoverColor);
        }

        float textY = (RowHeight - LineH()) * 0.5f;
        for (int i = 0; i < DisplayItems.Count; i++)
        {
            int col = i / RowsPerColumn, row = i % RowsPerColumn;
            bool avail = EnabledProvider?.Invoke(DisplayItems[i].Payload) ?? true;
            DrawLabel(ctx, DisplayItems[i].Label, inX + col * ColumnWidth + PlainPadding,
                      inY + row * RowHeight + textY,
                      avail ? PlainTextColor : TextColorGhosted);
        }
    }

    private void DrawScrollablePopupPlain(UiRenderContext ctx)
    {
        ConfigurePopupScroll();

        float outerTop = PopupTop;
        float inX = Border, inY = outerTop + Border + SearchHeight;

        ctx.DrawFill(0f, outerTop, OuterW, OuterH, PlainBackgroundColor);
        ctx.DrawRectOutline(0f, outerTop, OuterW, OuterH, PlainBorderColor, 1f);

        int start = VisibleTopRow;
        int count = System.Math.Min(EffectiveVisibleRows, DisplayItems.Count - start);
        float textY = (RowHeight - LineH()) * 0.5f;
        for (int i = 0; i < count; i++)
        {
            int idx = start + i;
            float y = inY + i * RowHeight;
            bool selected = Equals(DisplayItems[idx].Payload, Selected) || (Searchable && idx == _hoveredPopupIndex);
            if (selected)
                ctx.DrawFill(inX, y, ColumnWidth, RowHeight, PlainSelectedColor);
            else if (idx == _hoveredPopupIndex)
                ctx.DrawFill(inX, y, ColumnWidth, RowHeight, PlainHoverColor);
        }
        for (int i = 0; i < count; i++)
        {
            int idx = start + i;
            bool avail = EnabledProvider?.Invoke(DisplayItems[idx].Payload) ?? true;
            DrawLabel(ctx, DisplayItems[idx].Label, inX + PlainPadding, inY + i * RowHeight + textY,
                      avail ? PlainTextColor : TextColorGhosted);
        }

        if (!PlainPopupScrollbar && SpriteResolve is { } resolve)
            DrawPopupScrollbar(ctx, resolve, inX + ColumnWidth, inY);
        else
            DrawPopupScrollbarPlain(ctx, inX + ColumnWidth, inY);
    }

    private void DrawPopupScrollbarPlain(UiRenderContext ctx, float x, float y)
    {
        if (!IsPopupScrollbarPresentationVisible) return;

        ctx.DrawFill(x, y, ScrollbarWidth, InteriorH, PlainBackgroundColor);
        ctx.DrawRectOutline(x, y, ScrollbarWidth, InteriorH, PlainBorderColor, 1f);

        if (!PopupScroll.HasOverflow) return;

        float decExtent = System.Math.Clamp(ScrollButtonExtent, 0f, InteriorH);
        float incExtent = System.Math.Clamp(ScrollButtonExtent, 0f, InteriorH - decExtent);
        float trackTop = decExtent;
        float trackLen = MathF.Max(0f, InteriorH - decExtent - incExtent);
        var (ty, th) = UiScrollbar.ThumbRect(PopupScroll, trackTop, trackLen);
        ctx.DrawFill(x + 1f, y + ty, MathF.Max(0f, ScrollbarWidth - 2f), th, PlainBorderColor);
    }

    private void UpdatePlainPopupHover(float lx, float ly)
    {
        float ix = lx - Border, iy = ly - (PopupTop + Border + SearchHeight);
        _hoveredPopupIndex = Scrollable ? HoveredScrollableIndex(ix, iy) : HoveredGridIndex(ix, iy);
    }

    private int HoveredGridIndex(float ix, float iy)
    {
        if (ix < 0 || ix >= InteriorW || iy < 0 || iy >= InteriorH) return -1;
        int col = (int)(ix / ColumnWidth);
        int row = (int)(iy / RowHeight);
        int idx = col * RowsPerColumn + row;
        return row >= 0 && row < RowsPerColumn && idx >= 0 && idx < DisplayItems.Count ? idx : -1;
    }

    private int HoveredScrollableIndex(float ix, float iy)
    {
        if (ix < 0 || ix >= ColumnWidth || iy < 0 || iy >= InteriorH) return -1;
        int row = (int)(iy / RowHeight);
        int idx = VisibleTopRow + row;
        return row >= 0 && row < EffectiveVisibleRows && idx >= 0 && idx < DisplayItems.Count ? idx : -1;
    }

    private void DrawBevel(UiRenderContext ctx, Func<uint, (uint tex, int w, int h)> resolve,
        float x, float y, float w, float h)
    {
        var r = UiNineSlicePanel.ComputeFrameRects(w, h, Border);
        void P(uint id, in UiNineSlicePanel.Rect d) => DrawSprite(ctx, resolve, id, x + d.X, y + d.Y, d.W, d.H);
        P(RetailChromeSprites.CenterFill, r.Center);
        P(RetailChromeSprites.TopEdge,    r.Top);
        P(RetailChromeSprites.BottomEdge, r.Bottom);
        P(RetailChromeSprites.LeftEdge,   r.Left);
        P(RetailChromeSprites.RightEdge,  r.Right);
        P(RetailChromeSprites.CornerTL,   r.TL);
        P(RetailChromeSprites.CornerTR,   r.TR);
        P(RetailChromeSprites.CornerBL,   r.BL);
        P(RetailChromeSprites.CornerBR,   r.BR);
    }

    private float LineH() => DatFont?.LineHeight ?? Font?.LineHeight ?? 14f;

    private void DrawSprite(UiRenderContext ctx, Func<uint, (uint tex, int w, int h)> resolve,
        uint id, float x, float y, float w, float h)
    {
        if (id == 0) return;
        var (tex, tw, th) = resolve(id);
        if (tex == 0 || tw == 0 || th == 0) return;
        // Tile at native size (the panel fill is 191×2; rows are 191×17 = 1:1).
        ctx.DrawSprite(tex, x, y, w, h, 0f, 0f, w / tw, h / th, Vector4.One);
    }

    private void DrawLabel(UiRenderContext ctx, string s, float x, float y, Vector4 color)
    {
        if (DatFont is { } df) ctx.DrawStringDat(df, s, x, y, color, Outline, OutlineColor);
        else ctx.DrawString(s, x, y, color, Font);
    }

    protected override bool OnHitTest(float lx, float ly)
    {
        if (!_open) return base.OnHitTest(lx, ly);
        if (lx < 0 || lx >= OuterW) return false;
        // G7: the union of the button itself + the popup, whichever side it opens on.
        return OpenUpward ? (ly >= -OuterH && ly < Height) : (ly >= 0 && ly < Height + OuterH);
    }

    public override bool OnEvent(in UiEvent e)
    {
        if (Searchable && e.Type is UiEventType.FocusGained or UiEventType.FocusLost)
        {
            if (e.Type == UiEventType.FocusLost) EndSearchSelection();
            return _searchField.OnEvent(e);
        }
        if (e.Type == UiEventType.CaptureChanged)
        {
            EndSearchSelection();
            _draggingPopupThumb = false;
        }
        if (_selectingSearch && e.Type is UiEventType.MouseMove or UiEventType.MouseUp)
        {
            var searchPointer = new UiEvent(0, _searchField, e.Type, e.Data0,
                (int)(e.Data1 - Border), (int)(e.Data2 - PopupTop - Border));
            _searchField.OnEvent(searchPointer);
            if (e.Type == UiEventType.MouseUp) _selectingSearch = false;
            return true;
        }
        if (Searchable && _open && e.Type is UiEventType.KeyDown or UiEventType.KeyUp or UiEventType.Char)
        {
            if (e.Type == UiEventType.KeyDown)
            {
                var key = (Key)e.Data0;
                if (key == Key.Escape) { SetOpen(false); return true; }
                if (key is Key.Enter or Key.KeypadEnter)
                {
                    int index = _hoveredPopupIndex >= 0 ? _hoveredPopupIndex : 0;
                    if (index < DisplayItems.Count && (EnabledProvider?.Invoke(DisplayItems[index].Payload) ?? true))
                    {
                        OnSelect?.Invoke(DisplayItems[index].Payload);
                        SetOpen(false);
                    }
                    return true;
                }
                if (key is Key.Down or Key.Up)
                {
                    int next = _hoveredPopupIndex;
                    do { next += key == Key.Down ? 1 : -1; }
                    while (next >= 0 && next < DisplayItems.Count && !(EnabledProvider?.Invoke(DisplayItems[next].Payload) ?? true));
                    if (next >= 0 && next < DisplayItems.Count)
                    {
                        _hoveredPopupIndex = next;
                        ConfigurePopupScroll();
                        if (next < VisibleTopRow) PopupScroll.SetScrollY((int)(next * RowHeight));
                        else if (next >= VisibleTopRow + EffectiveVisibleRows) PopupScroll.SetScrollY((int)((next - EffectiveVisibleRows + 1) * RowHeight));
                    }
                    return true;
                }
            }
            _searchField.OnEvent(e);
            return true;
        }
        if (Scrollable && _open)
        {
            if (e.Type == UiEventType.MouseMove && _draggingPopupThumb)
            {
                DragPopupThumb(e.Data2);
                return true;
            }
            if (e.Type == UiEventType.MouseUp && _draggingPopupThumb)
            {
                _draggingPopupThumb = false;
                return true;
            }
            if (e.Type == UiEventType.Scroll)
            {
                ConfigurePopupScroll();
                PopupScroll.ScrollByLines(-e.Data0);
                return true;
            }
        }

        if (!RetailButtonArt && _open && e.Type == UiEventType.MouseMove)
        {
            UpdatePlainPopupHover(e.Data1, e.Data2);
            return true;
        }

        if (e.Type is UiEventType.MouseUp
            or UiEventType.HoverLeave
            or UiEventType.CaptureChanged)
        {
            _facePressed = false;   // the momentary face flick ends here
            if (e.Type == UiEventType.HoverLeave)
                _hoveredPopupIndex = -1;
            return false;
        }

        if (e.Type != UiEventType.MouseDown) return false;

        float lx = e.Data1, ly = e.Data2;
        bool clickedInPopup = OpenUpward ? ly < 0 : ly >= Height;
        if (_open && clickedInPopup)
        {
            float ix = lx - Border, iy = ly - (PopupTop + Border + SearchHeight);
            if (Searchable && iy < 0 && iy >= -SearchHeight)
            {
                FindRoot()?.SetKeyboardFocus(this);
                _selectingSearch = true;
                _hoveredPopupIndex = -1;
                var searchClick = new UiEvent(0, _searchField, e.Type, e.Data0, (int)ix, (int)(iy + SearchHeight));
                _searchField.OnEvent(searchClick);
                return true;
            }
            if (Scrollable)
                return HandleScrollablePopupMouseDown(ix, iy);

            if (ix >= 0 && ix < InteriorW && iy >= 0 && iy < InteriorH)
            {
                int col = (int)(ix / ColumnWidth);
                int row = (int)(iy / RowHeight);
                int idx = col * RowsPerColumn + row;
                // Only pick enabled items.
                if (row >= 0 && row < RowsPerColumn && idx >= 0 && idx < DisplayItems.Count
                    && (EnabledProvider?.Invoke(DisplayItems[idx].Payload) ?? true))
                {
                    OnSelect?.Invoke(DisplayItems[idx].Payload);
                }
            }
            SetOpen(false);
            return true;
        }

        _facePressed = true;                       // momentary press flick
        SetOpen(!_open);
        return true;
    }

    private void EndSearchSelection()
    {
        if (!_selectingSearch) return;
        _selectingSearch = false;
        _searchField.OnEvent(new UiEvent(0, _searchField, UiEventType.MouseUp));
    }

    private bool HandleScrollablePopupMouseDown(float ix, float iy)
    {
        if (ix >= 0 && ix < ColumnWidth && iy >= 0 && iy < InteriorH)
        {
            int row = (int)(iy / RowHeight);
            int idx = VisibleTopRow + row;
            if (row >= 0 && row < EffectiveVisibleRows && idx >= 0 && idx < DisplayItems.Count
                && (EnabledProvider?.Invoke(DisplayItems[idx].Payload) ?? true))
            {
                OnSelect?.Invoke(DisplayItems[idx].Payload);
            }
            SetOpen(false);
            return true;
        }

        float scrollbarX = ColumnWidth;
        if (IsPopupScrollbarPresentationVisible
            && ix >= scrollbarX && ix < scrollbarX + ScrollbarWidth
            && iy >= 0 && iy < InteriorH)
        {
            ConfigurePopupScroll();
            float decExtent = System.Math.Clamp(ScrollButtonExtent, 0f, InteriorH);
            float incExtent = System.Math.Clamp(ScrollButtonExtent, 0f, InteriorH - decExtent);

            if (iy < decExtent) { PopupScroll.ScrollByLines(-1); return true; }
            if (iy >= InteriorH - incExtent) { PopupScroll.ScrollByLines(1); return true; }

            float trackTop = decExtent;
            float trackLen = MathF.Max(0f, InteriorH - decExtent - incExtent);
            var (ty, th) = UiScrollbar.ThumbRect(PopupScroll, trackTop, trackLen);
            if (iy >= ty && iy <= ty + th)
            {
                _draggingPopupThumb = true;
                _popupThumbDragOffset = iy - ty;
            }
            else
            {
                PopupScroll.ScrollByPage(iy < ty ? -1 : 1);
            }
            return true;
        }

        SetOpen(false);
        return true;
    }

    private void DragPopupThumb(float ly)
    {
        float iy = ly - (PopupTop + Border + SearchHeight);         // G7: direction-aware
        ConfigurePopupScroll();
        float decExtent = System.Math.Clamp(ScrollButtonExtent, 0f, InteriorH);
        float incExtent = System.Math.Clamp(ScrollButtonExtent, 0f, InteriorH - decExtent);
        float trackTop = decExtent;
        float trackLen = MathF.Max(0f, InteriorH - decExtent - incExtent);
        var (_, thumbH) = UiScrollbar.ThumbRect(PopupScroll, trackTop, trackLen);
        float travel = MathF.Max(1f, trackLen - thumbH);
        float ratio = (iy - _popupThumbDragOffset - trackTop) / travel;
        PopupScroll.SetPositionRatio(ratio);
    }
}
