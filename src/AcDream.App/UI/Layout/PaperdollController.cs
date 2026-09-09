using System;
using System.Collections.Generic;
using AcDream.App.UI;
using AcDream.Core.Items;
using AcDream.Core.Selection;

namespace AcDream.App.UI.Layout;

public sealed class PaperdollController : IItemListDragHandler, IRetainedPanelController
{
    public const uint DollViewportId = 0x100001D5u;
    public const uint DollDragMaskId = 0x100001D6u;

    public static readonly uint[] ArmorSlotElementIds =
    {
        0x100005abu, 0x100005acu, 0x100005adu, 0x100005aeu, 0x100005afu,
        0x100005b0u, 0x100005b1u, 0x100005b2u, 0x100005b3u,
    };

    public sealed class PaperdollViewState
    {
        public bool SlotView { get; private set; }          // false = doll-view (default)
        public bool DollVisible    => !SlotView;
        public bool ArmorSlotsVisible => SlotView;
        public void Toggle() => SlotView = !SlotView;
    }

    private readonly ClientObjectTable _objects;
    private readonly Func<uint> _playerGuid;
    private readonly Func<ItemType, uint, uint, uint, uint, uint> _iconIds;
    private readonly Func<ItemType, uint, uint, uint, uint, uint>? _dragIconIds;
    private readonly ItemInteractionController _itemInteraction;
    private readonly bool _ownsItemInteraction;
    private readonly SelectionState _selection;
    private readonly PaperdollClickMap? _clickMap;
    private readonly List<(EquipMask Mask, UiItemList List)> _slots = new();
    private readonly List<(AetheriaUnlockState Bit, UiItemList List)> _aetheriaSlots = new();

    // ── Slots-toggle state ────────────────────────────────────────────────────────────────────────
    private readonly PaperdollViewState _viewState = new();
    private readonly List<UiItemList> _armorSlots = new();
    private UiElement? _dollViewport;
    private UiElement? _dollDragMask;
    private bool _disposed;

    private PaperdollController(
        ImportedLayout layout, ClientObjectTable objects, Func<uint> playerGuid,
        Func<ItemType, uint, uint, uint, uint, uint> iconIds, SelectionState selection,
        ItemInteractionController itemInteraction,
        uint emptySlotSprite, UiDatFont? datFont,
        PaperdollClickMap? clickMap,
        Func<ItemType, uint, uint, uint, uint, uint>? dragIconIds,
        IReadOnlyDictionary<uint, uint>? emptySlotSprites,
        bool ownsItemInteraction)
    {
        _objects = objects; _playerGuid = playerGuid; _iconIds = iconIds;
        _dragIconIds = dragIconIds;
        _itemInteraction = itemInteraction ?? throw new ArgumentNullException(nameof(itemInteraction));
        _ownsItemInteraction = ownsItemInteraction;
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _clickMap = clickMap;

        for (int i = 0; i < PaperdollSlotBackgrounds.Definitions.Length; i++)
        {
            var (element, mask, _, unlockBit) = PaperdollSlotBackgrounds.Definitions[i];
            if (layout.FindElement(element) is not UiItemList list) continue;
            list.RegisterDragHandler(this);
            list.PrimaryItemPressed = PressItem;
            list.ExamineItemRequested = ExamineItem;
            list.Cell.SourceKind  = ItemDragSource.Equipment;
            list.Cell.SlotIndex   = i;              // definition position = equipped drag-payload SourceSlot
            list.Cell.TooltipTextResolve = g => _objects.Get(g)?.GetTooltipDisplayName();
            list.Cell.EmptySprite = emptySlotSprites is not null
                && emptySlotSprites.TryGetValue(element, out uint authoredSprite)
                    ? authoredSprite
                    : emptySlotSprite;
            list.Cell.DoubleClicked = () =>
            {
                if (list.Cell.ItemId != 0)
                    _itemInteraction?.ActivateItem(list.Cell.ItemId);
            };
            _slots.Add((mask, list));
            if (unlockBit != AetheriaUnlockState.None)
                _aetheriaSlots.Add((unlockBit, list));
        }

        _objects.ObjectAdded   += OnObjectChanged;
        _objects.ObjectMoved   += OnObjectMoved;
        _objects.ObjectRemoved += OnObjectChanged;
        _objects.ObjectUpdated += OnObjectChanged;
        _objects.Cleared       += OnObjectsCleared;
        _selection.Changed += OnSelectionChanged;
        _itemInteraction.StateChanged += OnInteractionStateChanged;

        // ── Slots-toggle wiring ───────────────────────────────────────────────────────────────────
        foreach (var id in ArmorSlotElementIds)
            if (layout.FindElement(id) is UiItemList armor) _armorSlots.Add(armor);

        _dollViewport = layout.FindElement(DollViewportId);
        Action<int, int> clickDoll = HandleDollClick;
        if (_dollViewport is UiViewport doll)
            doll.ClickedAt = clickDoll;

        switch (layout.FindElement(DollDragMaskId))
        {
            case UiButton dragMaskButton:
                _dollDragMask = dragMaskButton;
                dragMaskButton.OnClickAt = clickDoll;
                break;
            case UiDatElement dragMaskElement:
                _dollDragMask = dragMaskElement;
                dragMaskElement.ClickThrough = false;
                dragMaskElement.OnClickAt = clickDoll;
                break;
        }

        var slotsBtnEl = layout.FindElement(0x100005BEu);
        if (slotsBtnEl is UiButton slotsBtn)
        {
            slotsBtn.OnClick = () => { _viewState.Toggle(); ApplyView(); };
            if (datFont is not null)
            {
                slotsBtn.Label      = "Slots";
                slotsBtn.LabelFont  = datFont;
                slotsBtn.LabelColor = System.Numerics.Vector4.One;   // white (was gold)
                slotsBtn.LabelAlign = UiButton.LabelAlignment.Left;  // sit at the left, before the slots
            }
        }

        ApplyView();   // initial state = doll-view (armor slots hidden)

        Populate();
    }

    public static PaperdollController Bind(
        ImportedLayout layout, ClientObjectTable objects, Func<uint> playerGuid,
        Func<ItemType, uint, uint, uint, uint, uint> iconIds, SelectionState selection,
        ItemInteractionController itemInteraction,
        uint emptySlotSprite = 0u, UiDatFont? datFont = null,
        PaperdollClickMap? clickMap = null,
        Func<ItemType, uint, uint, uint, uint, uint>? dragIconIds = null,
        IReadOnlyDictionary<uint, uint>? emptySlotSprites = null,
        bool ownsItemInteraction = false)
        => new PaperdollController(
            layout, objects, playerGuid, iconIds, selection, itemInteraction, emptySlotSprite,
            datFont, clickMap, dragIconIds, emptySlotSprites, ownsItemInteraction);

    private void HandleDollClick(int x, int y)
    {
        EquipMask bodyLocation = _clickMap?.GetBodyLocation(x, y) ?? EquipMask.None;
        uint hitObject = PaperdollSelectionPolicy.GetUpperInventoryObject(
            _objects,
            _playerGuid(),
            bodyLocation);
        if (hitObject == 0)
            return;

        if (_itemInteraction?.OfferSelfPrimaryClick()
            is not null and not ItemPrimaryClickResult.NotActive)
            return;

        _selection.Select(hitObject, SelectionChangeSource.Paperdoll);
    }

    private void OnObjectChanged(ClientObject o)
    {
        if (o.ObjectId == _playerGuid())
            ApplyAetheriaVisibility();
        else if (Concerns(o))
            Populate();
    }
    private void OnObjectMoved(ClientObjectMove move)
    {
        uint player = _playerGuid();
        if ((move.Item is { } item && Concerns(item))
            || move.Previous.ContainerId == player
            || move.Current.ContainerId == player
            || move.Previous.WielderId == player
            || move.Current.WielderId == player)
            Populate();
    }
    private void OnSelectionChanged(SelectionTransition _) => ApplySelectionIndicators();
    private void OnInteractionStateChanged() => Populate();
    private void OnObjectsCleared()
    {
        ApplyAetheriaVisibility();
        Populate();
    }

    private bool Concerns(ClientObject o)
    {
        uint p = _playerGuid();
        return o.WielderId == p || o.ContainerId == p;
    }

    public void Populate()
    {
        uint p = _playerGuid();

        var equipped = new List<ClientObject>();
        foreach (var o in _objects.Objects)
            if (o.CurrentlyEquippedLocation != EquipMask.None && (o.WielderId == p || o.ContainerId == p))
                equipped.Add(o);

        foreach (var (mask, list) in _slots)
        {
            ClientObject? worn = null;
            foreach (var o in equipped)
                if ((o.CurrentlyEquippedLocation & mask) != EquipMask.None) { worn = o; break; }

            if (worn is null) { list.Cell.Clear(); continue; }
            uint tex = _iconIds(worn.Type, worn.IconId, worn.IconUnderlayId, worn.IconOverlayId, worn.Effects);
            uint dragTex = _dragIconIds?.Invoke(
                worn.Type, worn.IconId, worn.IconUnderlayId, worn.IconOverlayId, worn.Effects) ?? 0u;
            list.Cell.SetItem(worn.ObjectId, tex, dragIconTexture: dragTex);
            list.Cell.SetWaitingState(
                _itemInteraction.IsPendingInventorySource(worn.ObjectId));
        }
        ApplyAetheriaVisibility();
        ApplySelectionIndicators();
    }

    private void ApplyAetheriaVisibility()
    {
        AetheriaUnlockState unlocked = AetheriaUnlocks.Read(_objects.Get(_playerGuid()));
        foreach (var (bit, list) in _aetheriaSlots)
            list.Visible = (unlocked & bit) != AetheriaUnlockState.None;
    }

    private void ApplySelectionIndicators()
    {
        foreach (var (_, list) in _slots)
        {
            list.Cell.Selected = list.Cell.ItemId != 0
                && list.Cell.ItemId == _selection.SelectedObjectId
                && !_itemInteraction.IsPendingInventorySource(list.Cell.ItemId);
        }
    }

    private EquipMask MaskFor(UiItemList list)
    {
        foreach (var (mask, l) in _slots) if (ReferenceEquals(l, list)) return mask;
        return EquipMask.None;
    }

    public void OnDragLift(UiItemList sourceList, UiItemSlot sourceCell, ItemDragPayload payload)
    {
        if (payload.ObjId != 0 && _selection.SelectedObjectId != payload.ObjId)
            _selection.Select(payload.ObjId, SelectionChangeSource.Paperdoll);
    }

    public ItemDragAcceptance OnDragOver(UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload)
    {
        if (payload.SourceKind == ItemDragSource.ShortcutBar)
            return ItemDragAcceptance.None;
        var item = _objects.Get(payload.ObjId);
        if (item is null)
            return ItemDragAcceptance.Reject;
        return (item.ValidLocations & MaskFor(targetList)) != EquipMask.None
            ? ItemDragAcceptance.Accept
            : ItemDragAcceptance.Reject;
    }

    public void HandleDropRelease(UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload)
    {
        if (payload.SourceKind == ItemDragSource.ShortcutBar)
            return;
        var item = _objects.Get(payload.ObjId);
        if (item is null) return;
        EquipMask wieldMask = ItemEquipRules.ResolvePaperdollDropWieldMask(item, MaskFor(targetList));
        if (wieldMask == EquipMask.None) return;       // not wieldable here (defensive; OnDragOver already rejected)
        _itemInteraction.WieldFromPaperdoll(payload.ObjId, wieldMask);
    }

    private void ApplyView()
    {
        if (_dollViewport is not null) _dollViewport.Visible = _viewState.DollVisible;
        if (_dollDragMask is not null) _dollDragMask.Visible = _viewState.DollVisible;
        foreach (var a in _armorSlots) a.Visible = _viewState.ArmorSlotsVisible;
    }

    private void ExamineItem(uint itemId)
    {
        _selection.Select(itemId, SelectionChangeSource.Paperdoll);
        _itemInteraction.ExamineSelectedOrEnterMode(itemId);
    }

    private bool PressItem(uint itemId)
    {
        if (_itemInteraction.OfferPrimaryClick(itemId) != ItemPrimaryClickResult.NotActive)
            return true;
        _selection.Select(itemId, SelectionChangeSource.Paperdoll);
        return false;
    }

    /// <summary>Detach event handlers (idempotent).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _objects.ObjectAdded   -= OnObjectChanged;
        _objects.ObjectMoved   -= OnObjectMoved;
        _objects.ObjectRemoved -= OnObjectChanged;
        _objects.ObjectUpdated -= OnObjectChanged;
        _objects.Cleared       -= OnObjectsCleared;
        _selection.Changed -= OnSelectionChanged;
        _itemInteraction.StateChanged -= OnInteractionStateChanged;
        foreach (var (_, list) in _slots)
        {
            list.PrimaryItemPressed = null;
            list.ExamineItemRequested = null;
        }
        if (_dollViewport is UiViewport doll)
            doll.ClickedAt = null;
        switch (_dollDragMask)
        {
            case UiButton button:
                button.OnClickAt = null;
                break;
            case UiDatElement element:
                element.OnClickAt = null;
                break;
        }
        if (_ownsItemInteraction)
            _itemInteraction.Dispose();
    }
}
