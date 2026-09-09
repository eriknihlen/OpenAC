using System;
using System.Collections.Generic;

namespace AcDream.App.UI;

public enum UiItemListFlow
{
    RowMajor,

    ColumnMajor,
}

public sealed class UiItemList : UiElement
{
    private readonly List<UiItemSlot> _cells = new();
    private int _layoutDeferralDepth;

    public UiScrollable Scroll { get; }

    public UiItemList(
        Func<uint, (uint tex, int w, int h)>? spriteResolve = null,
        UiScrollable? scroll = null)
    {
        Scroll = scroll ?? new UiScrollable();
        SpriteResolve = spriteResolve;
        AddItem(new UiItemSlot { SpriteResolve = spriteResolve });
    }

    public override bool ConsumesDatChildren => true;

    public Func<uint, (uint tex, int w, int h)>? SpriteResolve { get; set; }

    private IReadOnlyList<uint>? _cooldownSprites;
    public IReadOnlyList<uint>? CooldownSprites
    {
        get => _cooldownSprites;
        set
        {
            _cooldownSprites = value;
            foreach (UiItemSlot cell in _cells)
                cell.CooldownSprites = value;
        }
    }

    private Func<uint, int>? _cooldownStepProvider;
    public Func<uint, int>? CooldownStepProvider
    {
        get => _cooldownStepProvider;
        set
        {
            _cooldownStepProvider = value;
            foreach (UiItemSlot cell in _cells)
                cell.CooldownStepProvider = value;
        }
    }

    public Action<object, int, int>? CatalogDropped { get; set; }

    public Func<uint, bool>? PrimaryItemPressed { get; set; }

    public Action<uint>? ExamineItemRequested { get; set; }

    public Action<uint>? PrimaryCatalogEntryPressed { get; set; }

    public Action<uint>? ExamineCatalogEntryRequested { get; set; }

    private uint _cellEmptySprite;
    public uint CellEmptySprite
    {
        get => _cellEmptySprite;
        set
        {
            _cellEmptySprite = value;
            if (value != 0)
                foreach (var c in _cells) c.EmptySprite = value;
        }
    }

    public IItemListDragHandler? DragHandler { get; private set; }

    public void RegisterDragHandler(IItemListDragHandler handler) => DragHandler = handler;

    public UiItemSlot Cell => _cells.Count > 0
        ? _cells[0]
        : throw new InvalidOperationException("UiItemList has no cells; call AddItem first or use GetItem(index).");

    public int GetNumUIItems() => _cells.Count;

    public int IndexOf(UiItemSlot cell) => _cells.IndexOf(cell);

    public UiItemSlot? GetItem(int index)
        => index >= 0 && index < _cells.Count ? _cells[index] : null;

    public void AddItem(UiItemSlot cell)
    {
        cell.SpriteResolve ??= SpriteResolve;
        cell.CooldownSprites ??= _cooldownSprites;
        cell.CooldownStepProvider ??= _cooldownStepProvider;
        if (_cellEmptySprite != 0) cell.EmptySprite = _cellEmptySprite;
        cell.Anchors = AnchorEdges.None;
        _cells.Add(cell);
        AddChild(cell);
        if (_layoutDeferralDepth == 0)
            LayoutCells();
    }

    public IDisposable DeferLayout()
    {
        _layoutDeferralDepth++;
        return new LayoutDeferral(this);
    }

    public int Columns { get; set; } = 1;

    public UiItemListFlow Flow { get; set; } = UiItemListFlow.RowMajor;

    public float CellWidth { get; set; }
    public float CellHeight { get; set; }

    public bool SingleRow { get; set; }

    public bool HorizontalScroll { get; set; }

    public bool FillVisibleEmptySlots { get; set; }

    public Func<UiItemSlot>? EmptySlotFactory { get; set; }

    public static int RowCount(int cellCount, int columns)
    {
        int cols = columns < 1 ? 1 : columns;
        return (cellCount + cols - 1) / cols;
    }

    internal static (float x, float y) CellOffset(int index, int columns, float cellW, float cellH)
    {
        int col = index % columns, row = index / columns;
        return (col * cellW, row * cellH);
    }

    internal static (float x, float y) CellOffset(
        int index, int columns, int cellCount, UiItemListFlow flow, float cellW, float cellH)
    {
        int cols = columns < 1 ? 1 : columns;
        if (flow == UiItemListFlow.ColumnMajor)
        {
            int rows = Math.Max(1, RowCount(cellCount, cols));
            int col = index / rows, row = index % rows;
            return (col * cellW, row * cellH);
        }
        return CellOffset(index, cols, cellW, cellH);
    }

    internal void LayoutCells()
    {
        if (CellWidth <= 0f)
        {
            if (_cells.Count > 0)
            {
                var c = _cells[0];
                c.Left = 0; c.Top = 0; c.Width = Width; c.Height = Height; c.Visible = true;
            }
            return;
        }

        UpdateEmptySlots();

        int cols = SingleRow
            ? Math.Max(1, _cells.Count)
            : Columns < 1 ? 1 : Columns;
        int cellH = (int)MathF.Round(CellHeight);

        if (SingleRow && HorizontalScroll)
        {
            int cellW = Math.Max(1, (int)MathF.Round(CellWidth));
            Scroll.LineHeight = cellW;
            Scroll.ContentHeight = _cells.Count * cellW;
            Scroll.ViewHeight = (int)MathF.Floor(Width);
            Scroll.SetScrollY(Scroll.ScrollY);
            float scrollX = Scroll.ScrollY;

            for (int i = 0; i < _cells.Count; i++)
            {
                float left = i * CellWidth - scrollX;
                UiItemSlot cell = _cells[i];
                cell.Left = left;
                cell.Top = 0f;
                cell.Width = CellWidth;
                cell.Height = CellHeight;
                cell.Visible = left < Width && left + CellWidth > 0f;
            }
            return;
        }

        Scroll.LineHeight = cellH > 0 ? cellH : 1;
        Scroll.ContentHeight = RowCount(_cells.Count, cols) * cellH;
        Scroll.ViewHeight = (int)MathF.Floor(Height);
        Scroll.SetScrollY(Scroll.ScrollY);
        float scrollY = Scroll.ScrollY;

        for (int i = 0; i < _cells.Count; i++)
        {
            var (x, baseY) = CellOffset(i, cols, _cells.Count, Flow, CellWidth, CellHeight);
            float top = baseY - scrollY;
            var cell = _cells[i];
            cell.Left = x; cell.Top = top; cell.Width = CellWidth; cell.Height = CellHeight;
            cell.Visible = top < Height && top + CellHeight > 0f;
        }
    }

    private void UpdateEmptySlots()
    {
        if (!FillVisibleEmptySlots || !SingleRow || CellWidth <= 0f)
            return;

        int visibleCellCount = Math.Max(0, (int)MathF.Floor(Width / CellWidth));
        while (_cells.Count < visibleCellCount)
        {
            UiItemSlot cell = EmptySlotFactory?.Invoke() ?? new UiItemSlot();
            cell.SpriteResolve ??= SpriteResolve;
            cell.CooldownSprites ??= _cooldownSprites;
            cell.CooldownStepProvider ??= _cooldownStepProvider;
            if (_cellEmptySprite != 0) cell.EmptySprite = _cellEmptySprite;
            cell.Anchors = AnchorEdges.None;
            _cells.Add(cell);
            AddChild(cell);
        }

        while (_cells.Count > visibleCellCount && _cells[^1].IsEmptySlot)
        {
            UiItemSlot cell = _cells[^1];
            _cells.RemoveAt(_cells.Count - 1);
            RemoveChild(cell);
        }
    }


    public void Flush()
    {
        foreach (var c in _cells) RemoveChild(c);
        _cells.Clear();
    }

    public override bool OnEvent(in UiEvent e)
    {
        if (CatalogDropped is not null && e.Payload is not null)
        {
            if (e.Type is UiEventType.DragEnter or UiEventType.DragOver)
                return true;
            if (e.Type == UiEventType.DropReleased)
            {
                CatalogDropped(e.Payload, e.Data1, e.Data2);
                return true;
            }
        }
        if (e.Type == UiEventType.Scroll && CellWidth > 0f)
        {
            // Mirror UiText: Silk +Y wheel = up/older = decrease ScrollY; negate Data0.
            Scroll.ScrollByLines(-e.Data0);
            return true;
        }
        return base.OnEvent(e);
    }

    public void ScrollItemIntoView(int index)
    {
        if (CellWidth <= 0f || index < 0 || index >= _cells.Count) return;
        if (SingleRow && HorizontalScroll)
        {
            float left = index * CellWidth;
            float right = left + CellWidth;
            if (left < Scroll.ScrollY)
                Scroll.SetScrollY((int)MathF.Floor(left));
            else if (right > Scroll.ScrollY + Width)
                Scroll.SetScrollY((int)MathF.Ceiling(right - Width));
            return;
        }
        int columns = Math.Max(1, Columns);
        int row = Flow == UiItemListFlow.ColumnMajor
            ? index % Math.Max(1, RowCount(_cells.Count, columns))
            : index / columns;
        float top = row * CellHeight;
        float bottom = top + CellHeight;
        if (top < Scroll.ScrollY) Scroll.SetScrollY((int)MathF.Floor(top));
        else if (bottom > Scroll.ScrollY + Height)
            Scroll.SetScrollY((int)MathF.Ceiling(bottom - Height));
    }

    protected override void OnDraw(UiRenderContext ctx)
    {
        LayoutCells();
    }

    private void EndLayoutDeferral()
    {
        if (_layoutDeferralDepth <= 0)
            return;
        _layoutDeferralDepth--;
        if (_layoutDeferralDepth == 0)
            LayoutCells();
    }

    private sealed class LayoutDeferral : IDisposable
    {
        private UiItemList? _owner;

        public LayoutDeferral(UiItemList owner) => _owner = owner;

        public void Dispose()
        {
            UiItemList? owner = _owner;
            _owner = null;
            owner?.EndLayoutDeferral();
        }
    }
}
