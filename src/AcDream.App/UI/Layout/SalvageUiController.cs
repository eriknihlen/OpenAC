using AcDream.Core.Items;

namespace AcDream.App.UI.Layout;

public sealed class SalvageUiController : IRetainedPanelController, IItemListDragHandler
{
    public const uint LayoutId = 0x2100000Cu;
    public const uint RootId = 0x10000073u;
    public const uint ItemListId = 0x10000074u;
    public const uint ScrollbarId = 0x10000075u;
    public const uint SalvageButtonId = 0x10000076u;
    public const uint CloseButtonId = 0x10000078u;
    private const uint TradeOverlaySprite = 0x06001DAEu;

    public sealed record Bindings(
        ClientObjectTable Objects,
        Func<uint, bool> IsOwned,
        Func<uint, IReadOnlyList<uint>, bool> SendSalvage,
        Func<bool> AllowMultipleMaterials,
        Func<ItemType, uint, uint, uint, uint, uint> ResolveIcon,
        Action<bool> SetWindowVisible,
        Action<string> Report,
        uint EmptySlotSprite = 0u);

    private readonly Bindings _bindings;
    private readonly UiItemList _list;
    private readonly UiButton _salvageButton;
    private readonly UiButton? _closeButton;
    private readonly List<ClientObject> _items = [];
    private uint _toolId;
    private uint _material;
    private bool _disposed;
    private bool _sending;

    private SalvageUiController(ImportedLayout layout, Bindings bindings)
    {
        _bindings = bindings;
        _list = (UiItemList)layout.FindElement(ItemListId)!;
        _salvageButton = (UiButton)layout.FindElement(SalvageButtonId)!;
        _closeButton = layout.FindElement(CloseButtonId) as UiButton;
        _list.Columns = 1;
        _list.SingleRow = true;
        _list.HorizontalScroll = true;
        _list.CellWidth = 32f;
        _list.CellHeight = 32f;
        _list.FillVisibleEmptySlots = true;
        _list.CellEmptySprite = bindings.EmptySlotSprite;
        _list.EmptySlotFactory = () => new UiItemSlot
        {
            SpriteResolve = _list.SpriteResolve,
            AllowDragSource = false,
        };
        _list.RegisterDragHandler(this);
        if (layout.FindElement(ScrollbarId) is UiScrollbar scrollbar)
        {
            scrollbar.Model = _list.Scroll;
            scrollbar.Horizontal = true;
        }
        _list.ExamineItemRequested = RemoveItem;
        _salvageButton.SuppressSelfToggle = true;
        _salvageButton.OnClick = () => Salvage();
        if (_closeButton is not null) _closeButton.OnClick = Close;
        bindings.Objects.ObjectRemoved += OnObjectRemoved;
        bindings.Objects.ObjectUpdated += OnObjectChanged;
        bindings.Objects.ObjectMoved += OnObjectMoved;
        bindings.Objects.ContainerContentsReplaced += OnContainerContentsReplaced;
        bindings.Objects.Cleared += Close;
        Refresh();
    }

    public static SalvageUiController? Bind(ImportedLayout layout, Bindings bindings)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(bindings);
        return layout.FindElement(ItemListId) is UiItemList
            && layout.FindElement(SalvageButtonId) is UiButton
                ? new SalvageUiController(layout, bindings) : null;
    }

    public int ItemCount => _items.Count;
    public uint ToolId => _toolId;

    public void HandlePolicyAction(ItemPolicyAction action)
    {
        if (action.Kind == ItemPolicyActionKind.OpenSalvage)
            Open(action.ObjectId);
    }

    public void Open(uint toolId)
    {
        if (_disposed || !IsToolAvailable(toolId)) return;
        ClearItems();
        _toolId = toolId;
        Refresh();
        _bindings.SetWindowVisible(true);
    }

    private bool IsToolAvailable(uint toolId) => toolId != 0u
        && _bindings.IsOwned(toolId)
        && _bindings.Objects.Get(toolId) is { } tool
        && (tool.Type & ItemType.TinkeringTool) != 0;

    public bool CanAdd(uint itemId)
    {
        if (_disposed || _sending || !IsToolAvailable(_toolId)
            || itemId == _toolId || !_bindings.IsOwned(itemId)
            || _items.Any(item => item.ObjectId == itemId)
            || _bindings.Objects.Get(itemId) is not { } item)
            return false;
        return _bindings.Objects.GetContents(itemId).Count > 0
            || SalvageItemPolicy.IsSuitable(item, _material, _bindings.AllowMultipleMaterials());
    }

    public bool AddItem(uint itemId)
    {
        if (!CanAdd(itemId)) return false;
        var visited = new HashSet<uint>();
        AddRecursive(itemId, visited);
        Refresh();
        return true;
    }

    private void AddRecursive(uint itemId, HashSet<uint> visited)
    {
        if (!visited.Add(itemId) || !CanAdd(itemId)) return;
        IReadOnlyList<uint> contents = _bindings.Objects.GetContents(itemId);
        if (contents.Count > 0)
        {
            _bindings.Report($"Adding contents of {_bindings.Objects.Get(itemId)!.GetAppropriateName()}");
            foreach (uint child in contents)
                AddRecursive(child, visited);
            return;
        }
        ClientObject item = _bindings.Objects.Get(itemId)!;
        item.TradeState = 1;
        _items.Add(item);
        if (_items.Count == 1) _material = item.MaterialType ?? 0u;
    }

    public void RemoveItem(uint itemId)
    {
        int index = _items.FindIndex(item => item.ObjectId == itemId);
        if (index < 0) return;
        _items[index].TradeState = 0;
        _items.RemoveAt(index);
        if (_items.Count == 0) _material = 0;
        Refresh();
    }

    public bool Salvage()
    {
        if (_disposed || _sending || _items.Count == 0 || !IsToolAvailable(_toolId)) return false;
        if (_items.Any(item => !_bindings.IsOwned(item.ObjectId)
            || !ReferenceEquals(item, _bindings.Objects.Get(item.ObjectId))
            || !SalvageItemPolicy.IsSuitable(item)))
        {
            _bindings.Report("The list of items you are attempting to salvage is invalid.");
            return false;
        }
        uint[] itemIds = _items.AsEnumerable().Reverse().Select(item => item.ObjectId).ToArray();
        _sending = true;
        try
        {
            foreach (ClientObject item in _items) item.TradeState = 0;
            if (!_bindings.SendSalvage(_toolId, itemIds))
            {
                foreach (ClientObject item in _items) item.TradeState = 1;
                _bindings.Report("You cannot salvage those items right now.");
                return false;
            }
            ClearItems();
            Refresh();
            return true;
        }
        finally { _sending = false; }
    }

    public void Close()
    {
        OnHidden();
        _bindings.SetWindowVisible(false);
    }

    public void OnHidden()
    {
        _toolId = 0;
        ClearItems();
        Refresh();
    }

    private void ClearItems()
    {
        foreach (ClientObject item in _items) item.TradeState = 0;
        _items.Clear();
        _material = 0;
    }

    private void Refresh()
    {
        using (_list.DeferLayout())
        {
            _list.Flush();
            foreach (ClientObject item in _items)
            {
                var slot = new UiItemSlot
                {
                    SlotIndex = _list.GetNumUIItems(),
                    SpriteResolve = _list.SpriteResolve,
                    AllowDragSource = true,
                    SourceKind = ItemDragSource.Inventory,
                    ShowTradeOverlay = true,
                    TradeOverlaySprite = TradeOverlaySprite,
                    TooltipTextResolve = id => _bindings.Objects.Get(id)?.GetTooltipDisplayName(),
                };
                slot.SetItem(item.ObjectId, _bindings.ResolveIcon(
                    item.Type, item.IconId, item.IconUnderlayId, item.IconOverlayId, item.Effects));
                slot.SetStructure(item.Structure, item.MaxStructure);
                _list.AddItem(slot);
            }
        }
        _salvageButton.Enabled = _items.Count > 0;
    }

    private void OnObjectRemoved(ClientObject item)
    {
        if (item.ObjectId == _toolId) Close();
        else RemoveItem(item.ObjectId);
    }

    private void OnObjectChanged(ClientObject item)
    {
        if (item.ObjectId == _toolId && !IsToolAvailable(_toolId)) { Close(); return; }
        if (!_items.Any(staged => staged.ObjectId == item.ObjectId)) return;
        if (!_bindings.IsOwned(item.ObjectId) || !SalvageItemPolicy.IsSuitable(item)
            || !_items.Any(staged => ReferenceEquals(staged, item))) RemoveItem(item.ObjectId);
        else Refresh();
    }

    private void OnObjectMoved(ClientObjectMove move)
    {
        RevalidateOwnership();
    }

    private void OnContainerContentsReplaced(uint containerId) => RevalidateOwnership();

    private void RevalidateOwnership()
    {
        if (_toolId == 0) return;
        if (!IsToolAvailable(_toolId)) { Close(); return; }
        // Moving a container can change ownership of every staged item inside it.
        for (int index = _items.Count - 1; index >= 0; index--)
        {
            ClientObject item = _items[index];
            if (!_bindings.IsOwned(item.ObjectId)
                || !ReferenceEquals(item, _bindings.Objects.Get(item.ObjectId)))
            {
                item.TradeState = 0;
                _items.RemoveAt(index);
            }
        }
        if (_items.Count == 0) _material = 0;
        Refresh();
    }

    public void OnDragLift(UiItemList sourceList, UiItemSlot sourceCell, ItemDragPayload payload)
    {
        if (ReferenceEquals(sourceList, _list)) RemoveItem(payload.ObjId);
    }

    public ItemDragAcceptance OnDragOver(UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload)
        => ReferenceEquals(targetList, _list) && payload.SourceKind == ItemDragSource.Inventory && CanAdd(payload.ObjId)
            ? ItemDragAcceptance.Accept : ItemDragAcceptance.Reject;

    public void HandleDropRelease(UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload)
    {
        if (OnDragOver(targetList, targetCell, payload) == ItemDragAcceptance.Accept) AddItem(payload.ObjId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bindings.Objects.ObjectRemoved -= OnObjectRemoved;
        _bindings.Objects.ObjectUpdated -= OnObjectChanged;
        _bindings.Objects.ObjectMoved -= OnObjectMoved;
        _bindings.Objects.ContainerContentsReplaced -= OnContainerContentsReplaced;
        _bindings.Objects.Cleared -= Close;
        OnHidden();
        _salvageButton.OnClick = null;
        if (_closeButton is not null) _closeButton.OnClick = null;
        _list.ExamineItemRequested = null;
    }
}
