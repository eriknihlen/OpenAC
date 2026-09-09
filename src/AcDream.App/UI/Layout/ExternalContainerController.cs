using System;
using AcDream.Core.Items;
using AcDream.Core.Selection;

namespace AcDream.App.UI.Layout;

public sealed class ExternalContainerController : IItemListDragHandler, IRetainedPanelController
{
    public const uint LayoutId = 0x21000008u;
    public const uint RootId = 0x10000063u;
    public const uint TopContainerId = 0x10000064u;
    public const uint ContainerListId = 0x10000067u;
    public const uint CloseButtonId = 0x10000068u;
    public const uint ContentsListId = 0x1000006Au;
    public const uint ContentsScrollbarId = 0x1000006Bu;

    private const float ItemCellSize = 32f;
    private const float ContainerCellSize = 36f;
    internal const float DefaultContentWidth = 700f;
    internal const float MinimumContentWidth = 160f;

    private readonly ExternalContainerState _state;
    private readonly ClientObjectTable _objects;
    private readonly SelectionState _selection;
    private readonly ItemInteractionController _itemInteraction;
    private readonly StackSplitQuantityState _stackSplitQuantity;
    private readonly Func<ItemType, uint, uint, uint, uint, uint> _resolveIcon;
    private readonly Func<ItemType, uint, uint, uint, uint, uint> _resolveDragIcon;
    private readonly Action<uint> _sendUse;
    private readonly Action<uint, uint, int> _sendPutItemInContainer;
    private readonly Action<uint, uint, uint, uint> _sendSplitToContainer;
    private readonly Func<uint, bool> _isWithinUseRange;
    private readonly RetailWindowHandle _window;
    private readonly UiItemList _topContainer;
    private readonly UiItemList _containerList;
    private readonly UiItemList _contentsList;

    private uint _openContainer;
    private PendingBackpackPlacement? _pendingPlacement;
    private bool _closeRequested;
    private bool _disposed;

    private ExternalContainerController(
        ImportedLayout layout,
        ExternalContainerState state,
        ClientObjectTable objects,
        SelectionState selection,
        ItemInteractionController itemInteraction,
        StackSplitQuantityState stackSplitQuantity,
        Func<ItemType, uint, uint, uint, uint, uint> resolveIcon,
        Func<ItemType, uint, uint, uint, uint, uint> resolveDragIcon,
        Action<uint> sendUse,
        Action<uint, uint, int> sendPutItemInContainer,
        Action<uint, uint, uint, uint> sendSplitToContainer,
        Func<uint, bool> isWithinUseRange,
        RetailWindowHandle window,
        uint contentsEmptySprite,
        uint containerEmptySprite)
    {
        _state = state;
        _objects = objects;
        _selection = selection;
        _itemInteraction = itemInteraction;
        _stackSplitQuantity = stackSplitQuantity;
        _resolveIcon = resolveIcon;
        _resolveDragIcon = resolveDragIcon;
        _sendUse = sendUse;
        _sendPutItemInContainer = sendPutItemInContainer;
        _sendSplitToContainer = sendSplitToContainer;
        _isWithinUseRange = isWithinUseRange;
        _window = window;

        _topContainer = RequiredList(layout, TopContainerId);
        _containerList = RequiredList(layout, ContainerListId);
        _contentsList = RequiredList(layout, ContentsListId);

        ConfigureList(_topContainer, ContainerCellSize, horizontalScroll: false, containerEmptySprite);
        ConfigureList(_containerList, ContainerCellSize, horizontalScroll: true, containerEmptySprite);
        ConfigureList(_contentsList, ItemCellSize, horizontalScroll: true, contentsEmptySprite);
        _topContainer.PrimaryItemPressed = PressItem;
        _containerList.PrimaryItemPressed = PressItem;
        _contentsList.PrimaryItemPressed = PressItem;
        _topContainer.ExamineItemRequested = ExamineItem;
        _containerList.ExamineItemRequested = ExamineItem;
        _contentsList.ExamineItemRequested = ExamineItem;
        _contentsList.RegisterDragHandler(this);

        if (layout.FindElement(ContentsScrollbarId) is UiScrollbar scrollbar)
        {
            scrollbar.Model = _contentsList.Scroll;
            scrollbar.Horizontal = true;
            scrollbar.SpriteResolve ??= _contentsList.SpriteResolve;
            RetailScrollbarChrome.ApplyHorizontal(scrollbar);
        }

        BindClose(layout, RequestClose);

        _state.Changed += OnExternalContainerChanged;
        _objects.ObjectAdded += OnObjectChanged;
        _objects.ObjectUpdated += OnObjectChanged;
        _objects.ObjectMoved += OnObjectMoved;
        _objects.ObjectRemoved += OnObjectRemoved;
        _objects.ContainerContentsReplaced += OnContentsReplaced;
        _objects.Cleared += OnObjectsCleared;
        _selection.Changed += OnSelectionChanged;
        _itemInteraction.StateChanged += OnInteractionStateChanged;
        _itemInteraction.PendingBackpackPlacementRequested += OnPendingPlacementRequested;
        _itemInteraction.PendingBackpackPlacementCancelled += OnPendingPlacementCancelled;
        _itemInteraction.PendingBackpackPlacementResolved += OnPendingPlacementResolved;
        ClearLists();
    }

    public static ExternalContainerController Bind(
        ImportedLayout layout,
        ExternalContainerState state,
        ClientObjectTable objects,
        SelectionState selection,
        ItemInteractionController itemInteraction,
        StackSplitQuantityState stackSplitQuantity,
        Func<ItemType, uint, uint, uint, uint, uint> resolveIcon,
        Func<ItemType, uint, uint, uint, uint, uint> resolveDragIcon,
        Action<uint> sendUse,
        Action<uint, uint, int> sendPutItemInContainer,
        Action<uint, uint, uint, uint> sendSplitToContainer,
        Func<uint, bool> isWithinUseRange,
        RetailWindowHandle window,
        uint contentsEmptySprite = 0u,
        uint containerEmptySprite = 0u)
        => new(
            layout,
            state,
            objects,
            selection,
            itemInteraction,
            stackSplitQuantity,
            resolveIcon,
            resolveDragIcon,
            sendUse,
            sendPutItemInContainer,
            sendSplitToContainer,
            isWithinUseRange,
            window,
            contentsEmptySprite,
            containerEmptySprite);

    internal static RetailWindowFrame.Options CreateWindowOptions(UiElement root)
        => new()
        {
            WindowName = WindowNames.ExternalContainer,
            Chrome = RetailWindowChrome.NineSlice,
            Left = root.Left,
            Top = root.Top,
            ContentWidth = Math.Min(root.Width, DefaultContentWidth),
            ContentHeight = root.Height,
            MinWidth = MinimumContentWidth + 2f * RetailChromeSprites.Border,
            Visible = false,
            Draggable = true,
            Resizable = true,
            ResizeX = true,
            ResizeY = false,
            ResizableEdges = ResizeEdges.Left | ResizeEdges.Right,
            ConstrainDragToParent = true,
            ConstrainResizeToParent = true,
            DrawChromeCenter = false,
        };

    public void Tick()
    {
        uint root = _state.CurrentContainerId;
        if (root != 0u && _window.IsVisible && !_closeRequested && !_isWithinUseRange(root))
            RequestClose();
    }

    public void RequestClose()
    {
        uint root = _state.CurrentContainerId;
        if (root == 0u || _closeRequested)
            return;

        _closeRequested = true;
        _window.Hide();
        ClearLists();
        _sendUse(root);
    }

    public void OnDragLift(UiItemList sourceList, UiItemSlot sourceCell, ItemDragPayload payload)
    {
        if (payload.ObjId != 0u)
            _selection.Select(payload.ObjId, SelectionChangeSource.ExternalContainer);
    }

    public ItemDragAcceptance OnDragOver(
        UiItemList targetList,
        UiItemSlot targetCell,
        ItemDragPayload payload)
    {
        if (payload.SourceKind == ItemDragSource.ShortcutBar)
            return ItemDragAcceptance.None;
        if (!ReferenceEquals(targetList, _contentsList)
            || _openContainer == 0u)
            return ItemDragAcceptance.Reject;
        return EvaluateDrop(payload.ObjId) == InventoryContainerPlacementRejection.None
            ? ItemDragAcceptance.Accept
            : ItemDragAcceptance.Reject;
    }

    public void HandleDropRelease(
        UiItemList targetList,
        UiItemSlot targetCell,
        ItemDragPayload payload)
    {
        InventoryContainerPlacementRejection legality = EvaluateDrop(payload.ObjId);
        if (legality != InventoryContainerPlacementRejection.None)
        {
            if (InventoryContainerPlacementPolicy.ComposeClientLocal(
                    legality,
                    _objects.Get(payload.ObjId),
                    _objects.Get(_openContainer),
                    playerId: 0u) is { } refusal)
            {
                _itemInteraction.ReportClientLocal(refusal);
            }
            return;
        }
        if (!_itemInteraction.EnsureInventoryRequestReady())
            return;
        if (_objects.Get(payload.ObjId) is not { } item)
            return;

        uint fullStack = (uint)Math.Max(1, item.StackSize);
        uint amount = _stackSplitQuantity.GetObjectSplitSize(
            item.ObjectId,
            _selection.SelectedObjectId ?? 0u,
            fullStack);
        int placement = targetCell.ItemId != 0u
            ? Math.Max(0, targetCell.SlotIndex)
            : _objects.GetContents(_openContainer).Count;

        InventoryRequestKind kind = amount < fullStack
            ? InventoryRequestKind.SplitToContainer
            : InventoryRequestKind.PutInContainer;
        if (amount < fullStack)
        {
            _itemInteraction.TryDispatchInventoryRequest(
                kind,
                item.ObjectId,
                () =>
                {
                    _sendSplitToContainer(item.ObjectId, _openContainer, (uint)placement, amount);
                    return true;
                });
        }
        else
        {
            _itemInteraction.TryDispatchPendingBackpackPlacement(
                item.ObjectId,
                _openContainer,
                placement,
                kind,
                () =>
                {
                    _sendPutItemInContainer(item.ObjectId, _openContainer, placement);
                    return true;
                });
        }
    }

    private void OnExternalContainerChanged(ExternalContainerTransition transition)
    {
        if (transition.Kind == ExternalContainerTransitionKind.ReplacementRequested)
        {
            _openContainer = 0u;
            _closeRequested = false;
            _window.Hide();
            ClearLists();
            return;
        }

        if (transition.ContainerId == 0u)
        {
            _openContainer = 0u;
            _closeRequested = false;
            _window.Hide();
            ClearLists();
            return;
        }

        _openContainer = transition.ContainerId;
        _closeRequested = false;
        ScrollToHome();
        Populate();
        _window.Show();
    }

    private void Populate()
    {
        uint root = _state.CurrentContainerId;
        if (root == 0u)
        {
            ClearLists();
            return;
        }
        if (_openContainer == 0u)
            _openContainer = root;

        using IDisposable topLayout = _topContainer.DeferLayout();
        using IDisposable containerLayout = _containerList.DeferLayout();
        using IDisposable contentsLayout = _contentsList.DeferLayout();
        _topContainer.Flush();
        _containerList.Flush();
        _contentsList.Flush();

        AddRootCell(root);
        foreach (uint guid in _objects.GetContents(root))
        {
            if (IsContainer(_objects.Get(guid)))
                AddContainerCell(guid);
        }

        var visibleContents = new List<uint>();
        foreach (uint guid in _objects.GetContents(_openContainer))
        {
            if (!IsContainer(_objects.Get(guid)))
                visibleContents.Add(guid);
        }
        if (_pendingPlacement is { } pending
            && pending.ContainerId == _openContainer
            && _objects.Get(pending.ItemId) is { } pendingItem
            && !IsContainer(pendingItem))
        {
            visibleContents.Remove(pending.ItemId);
            visibleContents.Insert(
                Math.Clamp(pending.Placement, 0, visibleContents.Count),
                pending.ItemId);
        }
        foreach (uint guid in visibleContents)
        {
            bool waiting = _itemInteraction.IsPendingInventorySource(guid)
                || _pendingPlacement is { } projection
                    && projection.ContainerId == _openContainer
                    && projection.ItemId == guid;
            AddContentsCell(guid, waiting);
        }
        ApplyIndicators();
    }

    private void AddRootCell(uint guid)
    {
        UiItemSlot cell = CreateCell(_topContainer, guid, ItemDragSource.Ground);
        cell.DoubleClicked = RequestClose;
        cell.IsOpenContainer = _openContainer == guid;
        SetCapacity(cell, guid);
        _topContainer.AddItem(cell);
    }

    private void AddContainerCell(uint guid)
    {
        UiItemSlot cell = CreateCell(_containerList, guid, ItemDragSource.Ground);
        SetCapacity(cell, guid);
        _containerList.AddItem(cell);
    }

    private void AddContentsCell(uint guid, bool waiting = false)
    {
        UiItemSlot cell = CreateCell(_contentsList, guid, ItemDragSource.Ground);
        cell.DoubleClicked = () => _itemInteraction.ActivateItem(guid);
        cell.SetWaitingState(waiting);
        cell.DragAcceptSprite = 0x060011F9u;
        cell.DragRejectSprite = 0x060011F8u;
        _contentsList.AddItem(cell);
    }

    private UiItemSlot CreateCell(UiItemList owner, uint guid, ItemDragSource source)
    {
        ClientObject? item = _objects.Get(guid);
        uint icon = item is null ? 0u : _resolveIcon(
            item.Type, item.IconId, item.IconUnderlayId, item.IconOverlayId, item.Effects);
        uint dragIcon = item is null ? 0u : _resolveDragIcon(
            item.Type, item.IconId, item.IconUnderlayId, item.IconOverlayId, item.Effects);
        var cell = new UiItemSlot
        {
            SpriteResolve = owner.SpriteResolve,
            SlotIndex = owner.GetNumUIItems(),
            SourceKind = source,
            TooltipTextResolve = g => _objects.Get(g)?.GetTooltipDisplayName(),
        };
        cell.SetItem(guid, icon, dragIconTexture: dragIcon);
        return cell;
    }

    private void OpenNestedContainer(uint guid)
    {
        Select(guid);
        _openContainer = guid;
        _contentsList.Scroll.SetScrollY(0);
        Populate();
    }

    private void Select(uint guid)
        => _selection.Select(guid, SelectionChangeSource.ExternalContainer);

    private void ExamineItem(uint guid)
    {
        Select(guid);
        _itemInteraction.ExamineSelectedOrEnterMode(guid);
    }

    private bool PressItem(uint guid)
    {
        if (_itemInteraction.OfferPrimaryClick(guid) != ItemPrimaryClickResult.NotActive)
            return true;
        if (IsContainer(_objects.Get(guid)) && guid != _state.CurrentContainerId)
            OpenNestedContainer(guid);
        else
            Select(guid);
        return false;
    }

    private void ApplyIndicators()
    {
        ApplyIndicators(_topContainer);
        ApplyIndicators(_containerList);
        ApplyIndicators(_contentsList);
    }

    private void ApplyIndicators(UiItemList list)
    {
        for (int i = 0; i < list.GetNumUIItems(); i++)
        {
            if (list.GetItem(i) is not { } cell) continue;
            bool pendingSource = _itemInteraction.IsPendingSource(cell.ItemId);
            cell.Selected = cell.ItemId != 0u
                && cell.ItemId == _selection.SelectedObjectId
                && !pendingSource
                && !_itemInteraction.IsPendingInventorySource(cell.ItemId);
            cell.IsOpenContainer = cell.ItemId != 0u && cell.ItemId == _openContainer;
        }
    }

    private void SetCapacity(UiItemSlot cell, uint containerId)
    {
        int capacity = _objects.Get(containerId)?.ItemsCapacity ?? 0;
        cell.CapacityFill = capacity <= 0
            ? -1f
            : Math.Clamp(_objects.GetContents(containerId).Count / (float)capacity, 0f, 1f);
    }

    private void ClearLists()
    {
        _topContainer.Flush();
        _containerList.Flush();
        _contentsList.Flush();
        ScrollToHome();
    }

    private void ScrollToHome()
    {
        _topContainer.Scroll.SetScrollY(0);
        _containerList.Scroll.SetScrollY(0);
        _contentsList.Scroll.SetScrollY(0);
    }

    private void OnObjectChanged(ClientObject item)
    {
        if (_window.IsVisible && Concerns(item))
            Populate();
    }

    private void OnObjectMoved(ClientObjectMove move)
    {
        if (!_window.IsVisible) return;
        uint root = _state.CurrentContainerId;
        if ((move.Item is { } item && Concerns(item))
            || move.Previous.ContainerId == root
            || move.Current.ContainerId == root
            || move.Previous.ContainerId == _openContainer
            || move.Current.ContainerId == _openContainer)
        {
            Populate();
        }
    }
    private void OnContentsReplaced(uint containerId)
    {
        if (_window.IsVisible
            && (containerId == _state.CurrentContainerId
                || containerId == _openContainer
                || ProjectionContains(_state.CurrentContainerId, containerId)))
            Populate();
    }

    private void OnObjectRemoved(ClientObject item)
    {
        if (_selection.SelectedObjectId == item.ObjectId)
        {
            _selection.Clear(
                SelectionChangeSource.System,
                SelectionChangeReason.SelectedObjectRemoved);
        }

        if (item.ObjectId == _state.CurrentContainerId)
            _state.ApplyClose(item.ObjectId);
        else if (_window.IsVisible && Concerns(item))
            Populate();
    }

    private void OnObjectsCleared()
    {
        _openContainer = 0u;
        _closeRequested = false;
        _window.Hide();
        ClearLists();
    }

    private void OnSelectionChanged(SelectionTransition _) => ApplyIndicators();
    private void OnInteractionStateChanged()
    {
        if (_window.IsVisible)
            Populate();
        else
            ApplyIndicators();
    }

    private void OnPendingPlacementRequested(PendingBackpackPlacement pending)
    {
        if (pending.ContainerId != _openContainer)
            return;
        _pendingPlacement = pending;
        if (_window.IsVisible)
            Populate();
    }

    private void OnPendingPlacementCancelled(PendingBackpackPlacement pending)
        => ResolvePendingPlacement(pending);

    private void OnPendingPlacementResolved(PendingBackpackPlacement pending)
        => ResolvePendingPlacement(pending);

    private void ResolvePendingPlacement(PendingBackpackPlacement pending)
    {
        if (_pendingPlacement is not { } current || current.Token != pending.Token)
            return;
        _pendingPlacement = null;
        if (_window.IsVisible)
            Populate();
    }

    private InventoryContainerPlacementRejection EvaluateDrop(uint itemId)
    {
        if (_objects.Get(itemId) is { } source && IsContainer(source))
            return InventoryContainerPlacementRejection.ContainerCapacityFull;
        return InventoryContainerPlacementPolicy.Evaluate(
            _objects,
            itemId,
            _openContainer,
            playerId: 0u);
    }

    private static bool IsContainer(ClientObject? item)
        => item is not null
            && (item.ContainerTypeHint != 0u
                || item.Type.HasFlag(ItemType.Container)
                || item.ItemsCapacity > 0);

    private bool Concerns(ClientObject item)
    {
        uint root = _state.CurrentContainerId;
        return item.ObjectId == root
            || item.ObjectId == _openContainer
            || item.ContainerId == root
            || item.ContainerId == _openContainer
            || ProjectionContains(root, item.ObjectId)
            || ProjectionContains(_openContainer, item.ObjectId);
    }

    private bool ProjectionContains(uint containerId, uint itemId)
    {
        if (containerId == 0u || itemId == 0u) return false;
        foreach (uint candidate in _objects.GetContents(containerId))
        {
            if (candidate == itemId) return true;
        }
        return false;
    }

    private static void ConfigureList(
        UiItemList list,
        float cellSize,
        bool horizontalScroll,
        uint emptySprite)
    {
        list.Columns = 1;
        list.CellWidth = cellSize;
        list.CellHeight = cellSize;
        list.SingleRow = true;
        list.HorizontalScroll = horizontalScroll;
        list.FillVisibleEmptySlots = true;
        if (emptySprite != 0u)
            list.CellEmptySprite = emptySprite;
        list.EmptySlotFactory = () => new UiItemSlot
        {
            SpriteResolve = list.SpriteResolve,
            SourceKind = ItemDragSource.Ground,
            DragAcceptSprite = 0x060011F9u,
            DragRejectSprite = 0x060011F8u,
        };
    }

    private static UiItemList RequiredList(ImportedLayout layout, uint id)
        => layout.FindElement(id) as UiItemList
            ?? throw new InvalidOperationException(
                $"External-container LayoutDesc is missing ItemList 0x{id:X8}.");

    private static void BindClose(ImportedLayout layout, Action close)
    {
        switch (layout.FindElement(CloseButtonId))
        {
            case UiButton button:
                button.OnClick = close;
                break;
            case UiDatElement element:
                element.ClickThrough = false;
                element.OnClick = close;
                break;
            default:
                throw new InvalidOperationException(
                    "External-container LayoutDesc is missing its close button.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.Changed -= OnExternalContainerChanged;
        _objects.ObjectAdded -= OnObjectChanged;
        _objects.ObjectUpdated -= OnObjectChanged;
        _objects.ObjectMoved -= OnObjectMoved;
        _objects.ObjectRemoved -= OnObjectRemoved;
        _objects.ContainerContentsReplaced -= OnContentsReplaced;
        _objects.Cleared -= OnObjectsCleared;
        _selection.Changed -= OnSelectionChanged;
        _itemInteraction.StateChanged -= OnInteractionStateChanged;
        _itemInteraction.PendingBackpackPlacementRequested -= OnPendingPlacementRequested;
        _itemInteraction.PendingBackpackPlacementCancelled -= OnPendingPlacementCancelled;
        _itemInteraction.PendingBackpackPlacementResolved -= OnPendingPlacementResolved;
        _topContainer.PrimaryItemPressed = null;
        _containerList.PrimaryItemPressed = null;
        _contentsList.PrimaryItemPressed = null;
        _topContainer.ExamineItemRequested = null;
        _containerList.ExamineItemRequested = null;
        _contentsList.ExamineItemRequested = null;
    }
}
