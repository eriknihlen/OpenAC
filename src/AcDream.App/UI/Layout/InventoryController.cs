using System;
using System.Numerics;
using AcDream.App.UI;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Selection;
using AcDream.Core.Spells;

namespace AcDream.App.UI.Layout;

public sealed class InventoryController : IItemListDragHandler, IRetainedPanelController
{
    public const uint ContentsGridId    = 0x100001C6u;
    public const uint ContainerListId   = 0x100001CAu;
    public const uint TopContainerId    = 0x100001C9u;
    public const uint BurdenMeterId     = 0x100001D9u;
    public const uint BurdenTextId      = 0x100001D8u;
    public const uint BurdenCaptionId   = 0x100001D7u;  // "Burden"
    public const uint ContentsCaptionId = 0x100001C5u;
    public const uint TitleTextId       = 0x100001D3u;  // "Inventory of <name>"
    public const uint ContentsScrollbarId = 0x100001C7u;
    private const uint BackdropId          = 0x100001D0u;
    private const uint ContentsWindowId    = 0x100001CFu;
    private const uint PaperdollWindowId   = 0x100001CDu;
    private const uint BackpackWindowId    = 0x100001CEu;

    private const int   ContentsColumns = 6;
    private const float ContentsCellPx = 32f;
    private const float BackpackCellPx = 36f;
    private const int   SideBagSlots   = 7;
    private const int   MainPackSlots  = 102;
    private const int   SidePackSlots = 24;

    private readonly ClientObjectTable _objects;
    private readonly Func<uint> _playerGuid;
    private readonly Func<ItemType, uint, uint, uint, uint, uint> _iconIds;
    private readonly Func<ItemType, uint, uint, uint, uint, uint>? _dragIconIds;
    private readonly Func<int?> _strength;
    private readonly Spellbook? _burdenSpellbook;
    private readonly Func<string>? _ownerName;

    private readonly UiItemList? _contentsGrid;
    private readonly UiItemList? _containerList;
    private readonly UiItemList? _topContainer;
    private readonly UiMeter?    _burdenMeter;

    private float _burdenFill;
    private int   _burdenPercent;
    private static readonly Vector4 CaptionColor = new(1f, 1f, 1f, 1f);

    private uint _openContainer;   // 0 = the main pack (the player); else the open side bag's guid
    private readonly SelectionState _selection;
    private readonly Action<uint>? _sendUse;
    private readonly Action<uint, uint, int>? _sendPutItemInContainer;
    private readonly Action<uint, uint, uint, uint>? _sendStackableSplitToContainer;
    private readonly Action<uint, uint, uint>? _sendStackableMerge;
    private readonly Action<uint, uint>? _notifyMergeAttempt;
    private readonly ItemInteractionController? _itemInteraction;
    private readonly StackSplitQuantityState? _stackSplitQuantity;
    private PendingListPlacement? _pendingListPlacement;
    private readonly ShortcutStore? _shortcuts;
    private readonly UiShortcutDigitGraphics? _shortcutDigits;
    private readonly CombatState? _combat;
    private bool _disposed;

    private readonly record struct PendingListPlacement(
        ulong Token,
        uint ItemId,
        uint ContainerId,
        int Placement);

    private const uint EncumbranceValProperty = 5u;
    private const uint EncumbranceAugProperty = 0xE6u;

    private InventoryController(
        ImportedLayout layout,
        ClientObjectTable objects,
        Func<uint> playerGuid,
        Func<ItemType, uint, uint, uint, uint, uint> iconIds,
        Func<ItemType, uint, uint, uint, uint, uint>? dragIconIds,
        Func<int?> strength,
        SelectionState selection,
        Func<string>? ownerName,
        UiDatFont? datFont,
        uint contentsEmptySprite,
        uint sideBagEmptySprite,
        uint mainPackEmptySprite,
        Action<uint>? sendUse,
        Action<uint, uint, int>? sendPutItemInContainer,
        Action<uint, uint, uint, uint>? sendStackableSplitToContainer,
        Action<uint, uint, uint>? sendStackableMerge,
        Action<uint, uint>? notifyMergeAttempt,
        ItemInteractionController? itemInteraction,
        Action? onClose,
        StackSplitQuantityState? stackSplitQuantity,
        Spellbook? burdenSpellbook,
        ShortcutStore? shortcuts,
        UiShortcutDigitGraphics? shortcutDigits,
        CombatState? combat)
    {
        _objects    = objects;
        _shortcuts  = shortcuts;
        _shortcutDigits = shortcutDigits;
        _combat     = combat;
        _playerGuid = playerGuid;
        _iconIds    = iconIds;
        _dragIconIds = dragIconIds;
        _strength   = strength;
        _burdenSpellbook = burdenSpellbook;
        _ownerName  = ownerName;
        _sendUse            = sendUse;
        _sendPutItemInContainer = sendPutItemInContainer;
        _sendStackableSplitToContainer = sendStackableSplitToContainer;
        _sendStackableMerge = sendStackableMerge;
        _notifyMergeAttempt = notifyMergeAttempt;
        _itemInteraction = itemInteraction;
        _stackSplitQuantity = stackSplitQuantity;
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        if (_itemInteraction is not null)
            _itemInteraction.MergeAttempted += OnMergeAttempted;

        WindowChromeController.BindCloseButton(layout, onClose);

        _contentsGrid  = layout.FindElement(ContentsGridId)  as UiItemList;
        _containerList = layout.FindElement(ContainerListId) as UiItemList;
        _topContainer  = layout.FindElement(TopContainerId)  as UiItemList;

        ConfigureResizeLayout(layout);

        if (_contentsGrid is not null)
        {
            _contentsGrid.Columns    = ContentsColumns;
            _contentsGrid.CellWidth  = ContentsCellPx;
            _contentsGrid.CellHeight = ContentsCellPx;
        }

        if (_contentsGrid is not null
            && layout.FindElement(ContentsScrollbarId) is UiScrollbar bar)
        {
            bar.Model = _contentsGrid.Scroll;
            bar.SpriteResolve ??= _contentsGrid.SpriteResolve;
        }
        if (_containerList is not null)
        {
            _containerList.Columns    = 1;
            _containerList.CellWidth  = BackpackCellPx;
            _containerList.CellHeight = BackpackCellPx;
        }

        if (_contentsGrid  is not null) _contentsGrid.CellEmptySprite  = contentsEmptySprite;
        if (_containerList is not null) _containerList.CellEmptySprite = sideBagEmptySprite;
        if (_topContainer  is not null) _topContainer.CellEmptySprite  = mainPackEmptySprite;

        _contentsGrid?.RegisterDragHandler(this);
        _containerList?.RegisterDragHandler(this);
        _topContainer?.RegisterDragHandler(this);
        if (_contentsGrid is not null)
        {
            _contentsGrid.PrimaryItemPressed = PressItem;
            _contentsGrid.ExamineItemRequested = ExamineItem;
        }
        if (_containerList is not null)
        {
            _containerList.PrimaryItemPressed = PressItem;
            _containerList.ExamineItemRequested = ExamineItem;
        }
        if (_topContainer is not null)
        {
            _topContainer.PrimaryItemPressed = PressSelfItem;
            _topContainer.ExamineItemRequested = ExamineItem;
        }

        _burdenMeter = layout.FindElement(BurdenMeterId) as UiMeter;
        if (_burdenMeter is not null)
        {
            _burdenMeter.Vertical = true;            // 11x58 vertical bar
            _burdenMeter.FillFromBottom = true;
            _burdenMeter.Fill = () => _burdenFill;
        }

        AttachCaption(layout.FindElement(TitleTextId),     () => "Inventory of " + OwnerName(), datFont);
        AttachCaption(layout.FindElement(BurdenCaptionId),   () => "Burden", datFont);
        AttachCaption(layout.FindElement(ContentsCaptionId), () => "Contents of " + OpenContainerName(), datFont);
        AttachCaption(layout.FindElement(BurdenTextId),      () => _burdenPercent + "%", datFont);

        _objects.ObjectAdded   += OnObjectChanged;
        _objects.ObjectMoved   += OnObjectMoved;
        _objects.MoveRequestFailed += OnMoveRequestFailed;
        _objects.ContainerContentsReplaced += OnContainerContentsReplaced;
        _objects.ObjectRemoved += OnObjectRemoved;
        _objects.ObjectUpdated += OnObjectChanged;
        _objects.Cleared       += OnObjectsCleared;
        _selection.Changed += OnSelectionChanged;
        if (_burdenSpellbook is not null)
            _burdenSpellbook.EnchantmentsChanged += RefreshBurden;
        if (_shortcuts is not null)
            _shortcuts.Changed += RestampShortcutNumbers;
        if (_combat is not null)
            _combat.CombatModeChanged += OnCombatModeChanged;
        if (_itemInteraction is not null)
        {
            _itemInteraction.StateChanged += OnInteractionStateChanged;
            _itemInteraction.PendingBackpackPlacementRequested += OnPendingBackpackPlacementRequested;
            _itemInteraction.PendingBackpackPlacementCancelled += OnPendingBackpackPlacementCancelled;
            _itemInteraction.PendingBackpackPlacementResolved += OnPendingBackpackPlacementResolved;
        }

        Populate();
    }

    internal static void ConfigureResizeLayout(ImportedLayout layout)
    {
        static void Set(ImportedLayout imported, uint id, AnchorEdges anchors)
        {
            if (imported.FindElement(id) is not { } element) return;
            element.Anchors = anchors;
            element.CaptureCurrentAnchorBaseline();
        }

        AnchorEdges stretch = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Bottom;
        Set(layout, BackdropId, stretch);
        Set(layout, ContentsWindowId, stretch);
        Set(layout, ContentsGridId, stretch);
        Set(layout, ContentsScrollbarId, stretch);
        Set(layout, PaperdollWindowId, AnchorEdges.Left | AnchorEdges.Top);
        Set(layout, BackpackWindowId, AnchorEdges.Left | AnchorEdges.Top);
    }

    public static InventoryController Bind(
        ImportedLayout layout,
        ClientObjectTable objects,
        Func<uint> playerGuid,
        Func<ItemType, uint, uint, uint, uint, uint> iconIds,
        Func<int?> strength,
        SelectionState selection,
        UiDatFont? datFont,
        Func<string>? ownerName = null,
        uint contentsEmptySprite = 0u,
        uint sideBagEmptySprite  = 0u,
        uint mainPackEmptySprite = 0u,
        Action<uint>? sendUse = null,
        Action<uint, uint, int>? sendPutItemInContainer = null,
        Action<uint, uint, uint, uint>? sendStackableSplitToContainer = null,
        Action<uint, uint, uint>? sendStackableMerge = null,
        Action<uint, uint>? notifyMergeAttempt = null,
        ItemInteractionController? itemInteraction = null,
        Action? onClose = null,
        StackSplitQuantityState? stackSplitQuantity = null,
        Func<ItemType, uint, uint, uint, uint, uint>? dragIconIds = null,
        Spellbook? burdenSpellbook = null,
        ShortcutStore? shortcuts = null,
        UiShortcutDigitGraphics? shortcutDigits = null,
        CombatState? combat = null)
        => new InventoryController(layout, objects, playerGuid, iconIds, dragIconIds, strength, selection,
                                   ownerName, datFont,
                                   contentsEmptySprite, sideBagEmptySprite, mainPackEmptySprite,
                                   sendUse, sendPutItemInContainer,
                                   sendStackableSplitToContainer, sendStackableMerge,
                                   notifyMergeAttempt, itemInteraction,
                                   onClose, stackSplitQuantity, burdenSpellbook,
                                   shortcuts, shortcutDigits, combat);

    private void OnObjectChanged(ClientObject o)
    {
        if (Concerns(o) || _pendingListPlacement?.ItemId == o.ObjectId)
            Populate();
    }
    private void OnObjectRemoved(ClientObject o)
    {
        bool fallbackResolved = _itemInteraction is null
            && _pendingListPlacement?.ItemId == o.ObjectId;
        if (fallbackResolved)
            _pendingListPlacement = null;
        if (_selection.SelectedObjectId == o.ObjectId)
        {
            _selection.Clear(
                SelectionChangeSource.System,
                SelectionChangeReason.SelectedObjectRemoved);
        }
        if (fallbackResolved || Concerns(o)) Populate();
    }
    private void OnObjectMoved(ClientObjectMove move)
    {
        bool fallbackResolved = _itemInteraction is null
            && _pendingListPlacement?.ItemId == move.ItemId;
        if (fallbackResolved)
            _pendingListPlacement = null;
        uint player = _playerGuid();
        if (fallbackResolved
            || (move.Item is { } item && Concerns(item))
            || move.Previous.ContainerId == player
            || move.Current.ContainerId == player
            || move.Previous.WielderId == player
            || move.Current.WielderId == player)
            Populate();
    }
    private void OnContainerContentsReplaced(uint containerId)
    {
        if (containerId == EffectiveOpen() || containerId == _playerGuid())
            Populate();
    }
    // A transaction only changes which cells are waiting on it.
    private void OnInteractionStateChanged() => ApplyTransactionStates();

    private void ApplyTransactionStates()
    {
        PendingListPlacement? pending = _pendingListPlacement;
        ApplyWaitingStates(_containerList, _playerGuid(), pending);
        ApplyWaitingStates(_contentsGrid, EffectiveOpen(), pending);
        if (_topContainer?.GetItem(0) is { ItemId: not 0u } main)
            main.SetWaitingState(IsWaitingSource(main.ItemId));
        ApplyIndicators();
    }

    private void ApplyWaitingStates(
        UiItemList? list,
        uint containerId,
        PendingListPlacement? pending)
    {
        if (list is null) return;
        for (int i = 0; i < list.GetNumUIItems(); i++)
        {
            if (list.GetItem(i) is not { ItemId: not 0u } cell) continue;
            cell.SetWaitingState(
                IsWaitingSource(cell.ItemId)
                || (pending is { } projection
                    && projection.ContainerId == containerId
                    && projection.ItemId == cell.ItemId));
        }
    }
    private void OnPendingBackpackPlacementRequested(PendingBackpackPlacement pending)
    {
        if (_pendingListPlacement is not null
            || pending.ItemId == 0u
            || pending.ContainerId == 0u
            || _objects.Get(pending.ItemId) is null)
        {
            return;
        }

        _pendingListPlacement = new PendingListPlacement(
            pending.Token,
            pending.ItemId,
            pending.ContainerId,
            pending.Placement);
        Populate();
    }
    private void OnPendingBackpackPlacementCancelled(PendingBackpackPlacement pending)
    {
        if (_pendingListPlacement is not { } projection
            || projection.Token != pending.Token)
            return;
        _pendingListPlacement = null;
        Populate();
    }
    private void OnPendingBackpackPlacementResolved(PendingBackpackPlacement pending)
    {
        if (_pendingListPlacement is not { } projection
            || projection.Token != pending.Token)
            return;
        _pendingListPlacement = null;
        Populate();
    }
    private void OnSelectionChanged(SelectionTransition _) => ApplyIndicators();
    private void OnMoveRequestFailed(MoveRequestFailure failure)
    {
        if (_itemInteraction is not null
            || _pendingListPlacement?.ItemId != failure.ItemId)
            return;
        _pendingListPlacement = null;
        Populate();
    }
    private void OnObjectsCleared()
    {
        _pendingListPlacement = null;
        Populate();
    }

    /// <summary>True if the object is in (or wielded by) the player — i.e. a rebuild is warranted.</summary>
    private bool Concerns(ClientObject o)
    {
        uint p = _playerGuid();
        return o.ObjectId == p                          // the player object IS the burden source
            || o.ContainerId == p || o.WielderId == p
            || o.ContainerId == EffectiveOpen()
            || (o.ContainerId != 0 && _objects.Get(o.ContainerId)?.ContainerId == p);  // 2-deep
    }

    public void Populate()
    {
        using IDisposable? contentsLayout = _contentsGrid?.DeferLayout();
        using IDisposable? containersLayout = _containerList?.DeferLayout();

        uint p = _playerGuid();
        uint open = EffectiveOpen();

        var visibleBags = new List<uint>();
        foreach (var guid in _objects.GetContents(p))
        {
            var item = _objects.Get(guid);
            if (item is null || item.CurrentlyEquippedLocation != EquipMask.None) continue;
            bool isBag = IsBag(item);
            if (isBag) visibleBags.Add(guid);
        }

        PendingListPlacement? pending = _pendingListPlacement;
        if (pending is { } bagProjection
            && bagProjection.ContainerId == p
            && _objects.Get(bagProjection.ItemId) is { } pendingBag
            && IsBag(pendingBag))
        {
            visibleBags.Remove(bagProjection.ItemId);
            int index = Math.Clamp(bagProjection.Placement, 0, visibleBags.Count);
            visibleBags.Insert(index, bagProjection.ItemId);
        }

        var visibleContents = new List<uint>();
        foreach (var guid in _objects.GetContents(open))
        {
            var item = _objects.Get(guid);
            if (item is null || item.CurrentlyEquippedLocation != EquipMask.None) continue;
            bool isBag = IsBag(item);
            if (!isBag) visibleContents.Add(guid);
        }

        if (pending is { } projection
            && projection.ContainerId == open
            && _objects.Get(projection.ItemId) is { } pendingItem
            && !IsBag(pendingItem))
        {
            visibleContents.Remove(projection.ItemId);
            int index = Math.Clamp(projection.Placement, 0, visibleContents.Count);
            visibleContents.Insert(index, projection.ItemId);
        }

        // Flushing cells cancels a press in progress: UiRoot releases capture when the
        // captured element leaves the tree.
        if (TryRefreshCellsInPlace(visibleBags, visibleContents, pending, p, open))
        {
            ApplyIndicators();
            RefreshBurden();
            return;
        }

        _containerList?.Flush();
        _contentsGrid?.Flush();

        foreach (uint guid in visibleBags)
        {
            AddCell(
                _containerList,
                guid,
                isContainer: true,
                IsWaiting(guid, p, pending));
        }

        foreach (uint guid in visibleContents)
        {
            AddCell(
                _contentsGrid,
                guid,
                isContainer: false,
                IsWaiting(guid, open, pending));
        }

        if (_contentsGrid is not null)
        {
            int cap = _objects.Get(open)?.ItemsCapacity ?? 0;
            int slots = cap > 0 ? cap : (open == p ? MainPackSlots : SidePackSlots);
            while (_contentsGrid.GetNumUIItems() < slots) AddEmptyCell(_contentsGrid);
        }

        if (_containerList is not null)
        {
            int capacity = _objects.Get(p)?.ContainersCapacity ?? 0;
            int bags = _containerList.GetNumUIItems();
            int slots = capacity > 0 ? capacity : SideBagSlots;
            slots = Math.Max(slots, bags);
            slots = Math.Min(slots, SideBagSlots);
            while (_containerList.GetNumUIItems() < slots) AddEmptyCell(_containerList);
        }

        if (_topContainer is not null)
        {
            const uint PlayerPackBaseIcon = 0x0600127Eu;
            _topContainer.Flush();
            var main = new UiItemSlot
            {
                SpriteResolve = _topContainer.SpriteResolve,
                TooltipTextResolve = g => _objects.Get(g)?.GetTooltipDisplayName(),
            };
            main.SetItem(
                p,
                _iconIds(ItemType.Container, PlayerPackBaseIcon, 0u, 0u, 0u),
                dragIconTexture: _dragIconIds?.Invoke(
                    ItemType.Container, PlayerPackBaseIcon, 0u, 0u, 0u) ?? 0u);
            main.DragAcceptSprite = 0x060011F7u; main.DragRejectSprite = 0x060011F8u;
            main.SetWaitingState(IsWaitingSource(p));
            main.Clicked = () => OpenContainer(p);
            SetCapacityBar(main, p);               // main-pack fullness (items / ItemsCapacity)
            _topContainer.AddItem(main);
        }

        ApplyIndicators();
        RefreshBurden();
    }

    private static bool IsBag(ClientObject item) =>
        item.ContainerTypeHint != 0u
        || item.Type.HasFlag(ItemType.Container)
        || item.ItemsCapacity > 0;

    private int CountBags(uint containerId)
    {
        int count = 0;
        foreach (uint guid in _objects.GetContents(containerId))
        {
            if (_objects.Get(guid) is { } item && IsBag(item))
                count++;
        }
        return count;
    }

    private int CountLooseContents(uint containerId)
    {
        int count = 0;
        foreach (uint guid in _objects.GetContents(containerId))
        {
            if (_objects.Get(guid) is { } item
                && item.CurrentlyEquippedLocation == EquipMask.None
                && !IsBag(item))
            {
                count++;
            }
        }
        return count;
    }

    private uint EffectiveOpen() => _openContainer != 0 ? _openContainer : _playerGuid();

    public uint CurrentOpenContainerId => EffectiveOpen();

    private bool IsWaiting(uint guid, uint containerId, PendingListPlacement? pending)
        => IsWaitingSource(guid)
            || (pending is { } projection
                && projection.ContainerId == containerId
                && projection.ItemId == guid);

    /// <summary>False when anything structural moved, which needs the rebuild.</summary>
    private bool TryRefreshCellsInPlace(
        IReadOnlyList<uint> visibleBags,
        IReadOnlyList<uint> visibleContents,
        PendingListPlacement? pending,
        uint player,
        uint open)
    {
        if (!MatchesCells(_containerList, visibleBags, ContainerSlotTarget(player, visibleBags.Count))
            || !MatchesCells(_contentsGrid, visibleContents, ContentsSlotTarget(open, player)))
        {
            return false;
        }

        RefreshCells(_containerList, visibleBags, isContainer: true, player, pending);
        RefreshCells(_contentsGrid, visibleContents, isContainer: false, open, pending);
        RefreshTopContainer(player);
        return true;
    }

    private static bool MatchesCells(UiItemList? list, IReadOnlyList<uint> guids, int slotTarget)
    {
        if (list is null) return guids.Count == 0;
        if (list.GetNumUIItems() != slotTarget) return false;
        for (int i = 0; i < slotTarget; i++)
        {
            uint expected = i < guids.Count ? guids[i] : 0u;
            if (list.GetItem(i) is not { } cell || cell.ItemId != expected) return false;
        }
        return true;
    }

    private void RefreshCells(
        UiItemList? list,
        IReadOnlyList<uint> guids,
        bool isContainer,
        uint containerId,
        PendingListPlacement? pending)
    {
        if (list is null) return;
        for (int i = 0; i < guids.Count; i++)
        {
            if (list.GetItem(i) is not { } cell) continue;
            uint guid = guids[i];
            ClientObject? item = _objects.Get(guid);
            uint tex = item is null
                ? 0u
                : _iconIds(item.Type, item.IconId, item.IconUnderlayId, item.IconOverlayId, item.Effects);
            uint dragTex = item is null
                ? 0u
                : _dragIconIds?.Invoke(
                    item.Type, item.IconId, item.IconUnderlayId, item.IconOverlayId, item.Effects) ?? 0u;
            cell.SetItem(guid, tex, dragIconTexture: dragTex);
            SetStructureBar(cell, item);
            ApplyMarkers(cell, item);
            cell.SetWaitingState(IsWaiting(guid, containerId, pending));
            if (isContainer)
                SetCapacityBar(cell, guid);
        }
    }

    // The sale and trade markers and the shortcut number belong to the
    // object, so every cell showing it carries them.
    private void ApplyMarkers(UiItemSlot cell, ClientObject? item)
    {
        cell.ShowSellOverlay = item is { SellState: not 0 };
        cell.SellOverlaySprite = ItemCellOverlaySprites.Sell;
        cell.ShowTradeOverlay = item is { TradeState: not 0 };
        cell.TradeOverlaySprite = ItemCellOverlaySprites.Trade;
        ApplyShortcutNumber(cell, item?.ObjectId ?? 0u);
    }

    private void ApplyShortcutNumber(UiItemSlot cell, uint guid)
    {
        int slot = ShortcutSlotOf(guid);
        if (slot < 0)
        {
            cell.ClearShortcutNum();
            return;
        }
        cell.RegularDigits = _shortcutDigits?.RegularDigits;
        cell.GhostedDigits = _shortcutDigits?.GhostedDigits;
        cell.EmptyDigits = _shortcutDigits?.EmptyDigits;
        cell.SetShortcutNum(slot, _combat?.CurrentMode == CombatMode.Magic);
    }

    /// <summary>The numbered (top-row) shortcut slot holding this object, or -1.</summary>
    private int ShortcutSlotOf(uint guid)
    {
        if (_shortcuts is null || guid == 0u)
            return -1;
        for (int slot = 0; slot < NumberedShortcutSlots; slot++)
            if (_shortcuts.Get(slot) == guid)
                return slot;
        return -1;
    }

    private const int NumberedShortcutSlots = 9;

    private void OnCombatModeChanged(CombatMode mode) => RestampShortcutNumbers();

    private void RestampShortcutNumbers()
    {
        RestampShortcutNumbers(_contentsGrid);
        RestampShortcutNumbers(_containerList);
        RestampShortcutNumbers(_topContainer);
    }

    private void RestampShortcutNumbers(UiItemList? list)
    {
        if (list is null) return;
        for (int i = 0; i < list.GetNumUIItems(); i++)
        {
            if (list.GetItem(i) is { ItemId: not 0u } cell)
                ApplyShortcutNumber(cell, cell.ItemId);
        }
    }

    private void RefreshTopContainer(uint player)
    {
        if (_topContainer?.GetItem(0) is not { } main) return;
        main.SetWaitingState(IsWaitingSource(player));
        SetCapacityBar(main, player);
    }

    // Never from the cells on screen: a capacity change has to reach the rebuild.
    private int ContainerSlotTarget(uint player, int bagCount)
    {
        if (_containerList is null) return 0;
        int capacity = _objects.Get(player)?.ContainersCapacity ?? 0;
        int slots = capacity > 0 ? capacity : SideBagSlots;
        slots = Math.Max(slots, bagCount);
        return Math.Min(slots, SideBagSlots);
    }

    private int ContentsSlotTarget(uint open, uint player)
    {
        if (_contentsGrid is null) return 0;
        int cap = _objects.Get(open)?.ItemsCapacity ?? 0;
        return cap > 0 ? cap : (open == player ? MainPackSlots : SidePackSlots);
    }

    private void AddCell(UiItemList? list, uint guid, bool isContainer, bool waiting = false)
    {
        if (list is null) return;
        var item = _objects.Get(guid);
        uint tex = item is null ? 0u
            : _iconIds(item.Type, item.IconId, item.IconUnderlayId, item.IconOverlayId, item.Effects);
        uint dragTex = item is null ? 0u
            : _dragIconIds?.Invoke(
                item.Type, item.IconId, item.IconUnderlayId, item.IconOverlayId, item.Effects) ?? 0u;
        var cell = new UiItemSlot
        {
            SpriteResolve = list.SpriteResolve,
            TooltipTextResolve = g => _objects.Get(g)?.GetTooltipDisplayName(),
        };
        cell.SetItem(guid, tex, dragIconTexture: dragTex);
        SetStructureBar(cell, item);
        ApplyMarkers(cell, item);
        cell.SetWaitingState(waiting);
        cell.SlotIndex = list.GetNumUIItems();                 // index it will occupy (== its slot in a packed list)
        ConfigureDropFeedback(list, cell);
        if (isContainer)
        {
            cell.Clicked = () => OpenContainer(guid);
            SetCapacityBar(cell, guid);
        }
        else
        {
            cell.DoubleClicked = () => _itemInteraction?.ActivateItem(guid);
        }
        list.AddItem(cell);
    }

    private bool PressItem(uint guid)
    {
        if (_itemInteraction?.OfferPrimaryClick(guid)
            is not null and not ItemPrimaryClickResult.NotActive)
            return true;
        if (_objects.Get(guid) is { } item && IsBag(item))
            OpenContainer(guid);
        else
            SelectItem(guid);
        return false;
    }

    private bool PressSelfItem(uint guid)
    {
        if (_itemInteraction?.OfferSelfPrimaryClick()
            is not null and not ItemPrimaryClickResult.NotActive)
            return true;
        OpenContainer(guid);
        return false;
    }

    private void AddEmptyCell(UiItemList list)
    {
        var cell = new UiItemSlot { SpriteResolve = list.SpriteResolve, SlotIndex = list.GetNumUIItems() };
        ConfigureDropFeedback(list, cell);
        list.AddItem(cell);
    }

    private void ConfigureDropFeedback(UiItemList list, UiItemSlot cell)
    {
        cell.DragAcceptSprite = ReferenceEquals(list, _contentsGrid)
            ? 0x060011F9u
            : 0x060011F7u;
        cell.DragRejectSprite = 0x060011F8u;
    }

    private void SetCapacityBar(UiItemSlot cell, uint containerGuid)
    {
        int cap = _objects.Get(containerGuid)?.ItemsCapacity ?? 0;
        if (cap <= 0) { cell.CapacityFill = -1f; return; }
        int n = CountLooseContents(containerGuid);
        cell.CapacityFill = Math.Clamp(n / (float)cap, 0f, 1f);
    }

    private static void SetStructureBar(UiItemSlot cell, ClientObject? item)
    {
        cell.SetStructure(item?.Structure ?? 0, item?.MaxStructure ?? 0);
    }

    public void OnDragLift(UiItemList sourceList, UiItemSlot sourceCell, ItemDragPayload payload)
    {
        if (payload.ObjId != 0 && _selection.SelectedObjectId != payload.ObjId)
            _selection.Select(payload.ObjId, SelectionChangeSource.Inventory);
    }

    public ItemDragAcceptance OnDragOver(UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload)
    {
        if (payload.SourceKind == ItemDragSource.ShortcutBar)
            return ItemDragAcceptance.None;
        InventoryContainerPlacementRejection legality = EvaluateDrop(
            targetList, targetCell, payload.ObjId, out _, out uint destination);
        if (IsCapacityRejection(legality)
            && ResolveFallthroughContainer(payload.ObjId, destination, out _) != 0u)
        {
            legality = InventoryContainerPlacementRejection.None;
        }
        return legality == InventoryContainerPlacementRejection.None
            ? ItemDragAcceptance.Accept
            : ItemDragAcceptance.Reject;
    }

    private static bool IsCapacityRejection(InventoryContainerPlacementRejection rejection) =>
        rejection is InventoryContainerPlacementRejection.ItemCapacityFull
            or InventoryContainerPlacementRejection.ContainerCapacityFull;

    /// <summary>
    /// When the pack the player named has no room, the pack the item goes
    /// to instead: the main pack, then the side packs in order (0 when none
    /// has room, with the capacity rejection to report against the player).
    /// </summary>
    private uint ResolveFallthroughContainer(
        uint itemId,
        uint namedContainer,
        out InventoryContainerPlacementRejection refusal)
    {
        uint root = _playerGuid();
        return InventoryPlacementSearch.ChooseContainer(
            _objects, itemId, root, namedContainer, root, out refusal);
    }

    public void HandleDropRelease(UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload)
    {
        if (payload.SourceKind == ItemDragSource.ShortcutBar)
            return;

        uint item = payload.ObjId;
        if (item == 0) return;

        InventoryContainerPlacementRejection legality = EvaluateDrop(
            targetList,
            targetCell,
            item,
            out _,
            out uint legalityDestination);
        uint fallthrough = 0u;
        if (IsCapacityRejection(legality))
        {
            fallthrough = ResolveFallthroughContainer(
                item, legalityDestination, out InventoryContainerPlacementRejection noRoom);
            if (fallthrough == 0u)
            {
                // Nothing in the inventory has room: the notice names the
                // inventory as a whole, not the pack that was dropped on.
                if (InventoryContainerPlacementPolicy.ComposeClientLocal(
                        noRoom,
                        _objects.Get(item),
                        _objects.Get(_playerGuid()),
                        _playerGuid()) is { } fullNotice)
                {
                    _itemInteraction?.ReportClientLocal(fullNotice);
                }
                return;
            }
            legality = InventoryContainerPlacementPolicy.Evaluate(
                _objects, item, fallthrough, _playerGuid());
            legalityDestination = fallthrough;
        }
        if (legality != InventoryContainerPlacementRejection.None)
        {
            if (InventoryContainerPlacementPolicy.ComposeClientLocal(
                    legality,
                    _objects.Get(item),
                    _objects.Get(legalityDestination),
                    _playerGuid()) is { } refusal)
            {
                _itemInteraction?.ReportClientLocal(refusal);
            }
            return;
        }

        if (targetList == _contentsGrid
            && _pendingListPlacement is { } localPending
            && localPending.ContainerId == EffectiveOpen())
        {
            _itemInteraction?.ReportPendingBackpackPlacementConflict();
            return;
        }
        if (_itemInteraction is not null
            && !_itemInteraction.EnsureInventoryRequestReady())
        {
            return;
        }

        if (targetList == _contentsGrid
            && targetCell.ItemId != 0
            && TryMergeStacks(item, targetCell.ItemId))
            return;

        bool sourceIsBag = _objects.Get(item) is { } dragged && IsBag(dragged);
        uint container; int placement;
        if (targetList == _contentsGrid)
        {
            container = EffectiveOpen();
            placement = targetCell.ItemId != 0
                ? targetCell.SlotIndex
                : CountLooseContents(container);                                              // first empty = append after visible loose items
        }
        else if (targetList == _containerList || targetList == _topContainer)
        {
            if (sourceIsBag)
            {
                container = _playerGuid();
                if (container == 0u) return;
                placement = Math.Max(0, targetCell.SlotIndex);
            }
            else
            {
                if (targetCell.ItemId == 0 || targetCell.ItemId == item) return;
                container = targetCell.ItemId;                                                  // the bag / main pack
                if (IsContainerFull(container)) return;                                         // red already shown
                placement = _objects.GetContents(container).Count;                              // append into it
            }
        }
        else return;

        if (fallthrough != 0u)
        {
            // The named pack was full; append into the pack that has room
            // (a pack joins the player's pack list, an item its loose items).
            container = fallthrough;
            placement = container != _playerGuid()
                ? _objects.GetContents(container).Count
                : sourceIsBag
                    ? CountBags(container)
                    : CountLooseContents(container);
        }

        if (container == item) return;                                                         // never into itself

        if (_objects.Get(item) is { } source)
        {
            uint fullStack = (uint)Math.Max(source.StackSize, 1);
            uint splitSize = _stackSplitQuantity?.GetObjectSplitSize(
                item, _selection.SelectedObjectId ?? 0u, fullStack) ?? fullStack;
            if (splitSize < fullStack)
            {
                if (_itemInteraction is not null)
                {
                    _itemInteraction.TrySplitToContainer(
                        item,
                        container,
                        (uint)placement,
                        splitSize);
                }
                else
                {
                    DispatchInventoryRequest(
                        InventoryRequestKind.SplitToContainer,
                        item,
                        () =>
                        {
                            if (_sendStackableSplitToContainer is null)
                                return false;
                            _sendStackableSplitToContainer(
                                item,
                                container,
                                (uint)placement,
                                splitSize);
                            return true;
                        });
                }
                return;
            }
        }

        if (_itemInteraction is not null)
        {
            InventoryRequestKind kind = payload.SourceKind == ItemDragSource.Ground
                ? InventoryRequestKind.Pickup
                : InventoryRequestKind.PutInContainer;
            if (!_itemInteraction.TryDispatchPendingBackpackPlacement(
                    item,
                    container,
                    placement,
                    kind,
                    () =>
                    {
                        if (_sendPutItemInContainer is null)
                            return false;
                        _sendPutItemInContainer(item, container, placement);
                        return true;
                    }))
            {
                return;
            }
            return;
        }

        if (_pendingListPlacement is not null)
            return;
        _pendingListPlacement = new PendingListPlacement(0u, item, container, placement);
        Populate();
        _sendPutItemInContainer?.Invoke(item, container, placement);
    }

    private bool TryMergeStacks(uint sourceId, uint targetId)
    {
        if (_sendStackableMerge is null
            || _objects.Get(sourceId) is not { } source
            || _objects.Get(targetId) is not { } target)
            return false;

        int requestedAmount = _selection.SelectedObjectId == sourceId
            && _stackSplitQuantity is not null
                ? (int)Math.Min(_stackSplitQuantity.Value, int.MaxValue)
                : Math.Max(1, source.StackSize);
        StackMergePlan? plan = StackMergePlanner.Plan(
            new StackMergeItem(
                source.ObjectId,
                source.WeenieClassId,
                source.StackSize,
                source.StackSizeMax,
                source.TradeState),
            new StackMergeItem(
                target.ObjectId,
                target.WeenieClassId,
                target.StackSize,
                target.StackSizeMax,
                target.TradeState),
            _itemInteraction?.CanMakeInventoryRequest ?? true,
            requestedAmount);
        if (plan is not { } merge)
            return false;

        return DispatchInventoryRequest(
            InventoryRequestKind.Merge,
            merge.SourceObjectId,
            () =>
            {
                _sendStackableMerge(
                    merge.SourceObjectId,
                    merge.TargetObjectId,
                    merge.Amount);
                _notifyMergeAttempt?.Invoke(merge.SourceObjectId, merge.TargetObjectId);
                _selection.Select(merge.TargetObjectId, SelectionChangeSource.Inventory);
                return true;
            });
    }

    private bool DispatchInventoryRequest(
        InventoryRequestKind kind,
        uint itemId,
        Func<bool> dispatch)
        => _itemInteraction?.TryDispatchInventoryRequest(kind, itemId, dispatch)
            ?? dispatch();

    private bool IsContainerFull(uint container)
    {
        int cap = _objects.Get(container)?.ItemsCapacity ?? 0;
        if (cap <= 0) return false;
        return CountLooseContents(container) >= cap;
    }

    private void OnMergeAttempted(uint sourceId, uint targetId)
    {
        _notifyMergeAttempt?.Invoke(sourceId, targetId);
        _selection.Select(targetId, SelectionChangeSource.Inventory);
    }

    private InventoryContainerPlacementRejection EvaluateDrop(
        UiItemList targetList,
        UiItemSlot targetCell,
        uint itemId,
        out bool sourceIsBag,
        out uint destinationId)
    {
        sourceIsBag = _objects.Get(itemId) is { } source && IsBag(source);
        destinationId = 0u;
        if (itemId == 0u)
            return InventoryContainerPlacementRejection.InvalidItem;

        if (ReferenceEquals(targetList, _contentsGrid))
        {
            destinationId = EffectiveOpen();
            if (sourceIsBag)
                return InventoryContainerPlacementRejection.ContainerCapacityFull;
        }
        else if (ReferenceEquals(targetList, _containerList)
            || ReferenceEquals(targetList, _topContainer))
        {
            destinationId = sourceIsBag ? _playerGuid() : targetCell.ItemId;
            if (!sourceIsBag && (targetCell.ItemId == 0u || targetCell.ItemId == itemId))
                return InventoryContainerPlacementRejection.InvalidDestination;
        }
        else
        {
            return InventoryContainerPlacementRejection.InvalidDestination;
        }

        return InventoryContainerPlacementPolicy.Evaluate(
            _objects,
            itemId,
            destinationId,
            _playerGuid());
    }

    private bool IsWaitingSource(uint itemGuid)
        => _itemInteraction?.IsPendingInventorySource(itemGuid) == true;

    private void SelectItem(uint guid)
    {
        if (guid == 0) return;
        _selection.Select(guid, SelectionChangeSource.Inventory);
    }

    private void ExamineItem(uint guid)
    {
        SelectItem(guid);
        _itemInteraction?.ExamineSelectedOrEnterMode(guid);
    }

    private void OpenContainer(uint guid)
    {
        if (guid == 0) return;
        _selection.Select(guid, SelectionChangeSource.Inventory);
        uint open = EffectiveOpen();
        if (guid == open) { ApplyIndicators(); return; }   // already open — just move the square

        uint p = _playerGuid();
        _openContainer = guid;
        if (guid != p)
        {
            if (_itemInteraction is not null)
                _itemInteraction.ActivateItem(guid);
            else
                _sendUse?.Invoke(guid);             // open the side bag (ViewContents will land)
        }
        Populate();
    }

    private void ApplyIndicators()
    {
        SetIndicators(_contentsGrid);
        SetIndicators(_containerList);
        SetIndicators(_topContainer);
    }

    private void SetIndicators(UiItemList? list)
    {
        if (list is null) return;
        uint open = EffectiveOpen();
        for (int i = 0; i < list.GetNumUIItems(); i++)
        {
            var cell = list.GetItem(i);
            if (cell is null) continue;
            bool pendingTargetSource = _itemInteraction?.IsPendingSource(cell.ItemId) == true
                || IsWaitingSource(cell.ItemId);
            cell.Selected        = cell.ItemId != 0
                && cell.ItemId == _selection.SelectedObjectId
                && !pendingTargetSource;
            cell.IsOpenContainer = cell.ItemId != 0 && cell.ItemId == open;
        }
    }

    private string OpenContainerName()
    {
        uint open = EffectiveOpen();
        return open == _playerGuid() ? "Backpack" : (_objects.Get(open)?.Name ?? "Backpack");
    }

    private string OwnerName()
    {
        var name = _ownerName?.Invoke();
        return string.IsNullOrWhiteSpace(name) ? "Player" : name;
    }

    private void AttachCaption(UiElement? host, Func<string> text, UiDatFont? datFont)
    {
        if (host is null) return;

        if (host is UiText t)
        {
            t.Centered = true;
            t.OneLine = true;
            t.DatFont = datFont;
            t.ClickThrough = true;
            t.AcceptsFocus = false;
            t.IsEditControl = false;
            t.CapturesPointerDrag = false;
            t.LinesProvider = () =>
            {
                var s = text();
                return string.IsNullOrEmpty(s)
                    ? Array.Empty<UiText.Line>()
                    : new[] { new UiText.Line(s, CaptionColor) };
            };
            return;
        }

        var label = new UiText
        {
            Left = 0f, Top = 0f, Width = host.Width, Height = host.Height,
            Anchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Right,
            Centered = true, OneLine = true, DatFont = datFont, ClickThrough = true,
            AcceptsFocus = false, IsEditControl = false, CapturesPointerDrag = false,
            LinesProvider = () =>
            {
                var s = text();
                return string.IsNullOrEmpty(s)
                    ? Array.Empty<UiText.Line>()
                    : new[] { new UiText.Line(s, CaptionColor) };
            },
        };
        host.AddChild(label);
    }

    private void RefreshBurden()
    {
        uint p = _playerGuid();
        int str = _strength() ?? 10;                       // InqAttribute default 0xa
        int aug = _objects.Get(p)?.Properties.GetInt(EncumbranceAugProperty) ?? 0;
        int capacity = BurdenMath.EncumbranceCapacity(str, aug);

        int? wire = _objects.Get(p)?.Properties.Ints.TryGetValue(EncumbranceValProperty, out var ev) == true
            ? ev : (int?)null;
        int burden = wire ?? _objects.SumCarriedBurden(p);

        float load = BurdenMath.LoadRatio(capacity, burden);
        _burdenFill    = BurdenMath.LoadToFill(load);
        _burdenPercent = BurdenMath.LoadToPercent(load);
    }

    /// <summary>Detach event handlers (idempotent).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _objects.ObjectAdded   -= OnObjectChanged;
        _objects.ObjectMoved   -= OnObjectMoved;
        _objects.MoveRequestFailed -= OnMoveRequestFailed;
        _objects.ContainerContentsReplaced -= OnContainerContentsReplaced;
        _objects.ObjectRemoved -= OnObjectRemoved;
        _objects.ObjectUpdated -= OnObjectChanged;
        _objects.Cleared       -= OnObjectsCleared;
        _selection.Changed -= OnSelectionChanged;
        if (_burdenSpellbook is not null)
            _burdenSpellbook.EnchantmentsChanged -= RefreshBurden;
        if (_shortcuts is not null)
            _shortcuts.Changed -= RestampShortcutNumbers;
        if (_combat is not null)
            _combat.CombatModeChanged -= OnCombatModeChanged;
        if (_contentsGrid is not null)
        {
            _contentsGrid.PrimaryItemPressed = null;
            _contentsGrid.ExamineItemRequested = null;
        }
        if (_containerList is not null)
        {
            _containerList.PrimaryItemPressed = null;
            _containerList.ExamineItemRequested = null;
        }
        if (_topContainer is not null)
        {
            _topContainer.PrimaryItemPressed = null;
            _topContainer.ExamineItemRequested = null;
        }
        if (_itemInteraction is not null)
        {
            _itemInteraction.MergeAttempted -= OnMergeAttempted;
            _itemInteraction.StateChanged -= OnInteractionStateChanged;
            _itemInteraction.PendingBackpackPlacementRequested -= OnPendingBackpackPlacementRequested;
            _itemInteraction.PendingBackpackPlacementCancelled -= OnPendingBackpackPlacementCancelled;
            _itemInteraction.PendingBackpackPlacementResolved -= OnPendingBackpackPlacementResolved;
        }
    }
}
