using System;
using System.Collections.Generic;
using AcDream.App.UI;
using AcDream.Core.Items;
using AcDream.Core.Selection;
using AcDream.Automation.Items;

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
    private readonly IPaperdollFigureLighting? _figureLighting;
    private readonly Func<ClientObject, string> _resolveAppropriateName;
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
        bool ownsItemInteraction,
        IPaperdollFigureLighting? figureLighting,
        Func<ClientObject, string> resolveAppropriateName)
    {
        ArgumentNullException.ThrowIfNull(resolveAppropriateName);
        _resolveAppropriateName = resolveAppropriateName;
        _objects = objects; _playerGuid = playerGuid; _iconIds = iconIds;
        _dragIconIds = dragIconIds;
        _itemInteraction = itemInteraction ?? throw new ArgumentNullException(nameof(itemInteraction));
        _ownsItemInteraction = ownsItemInteraction;
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _clickMap = clickMap;
        _figureLighting = figureLighting;

        for (int i = 0; i < PaperdollSlotBackgrounds.Definitions.Length; i++)
        {
            var (element, mask, _, unlockBit) = PaperdollSlotBackgrounds.Definitions[i];
            if (layout.FindElement(element) is not UiItemList list) continue;
            list.RegisterDragHandler(this);
            list.PrimaryItemPressed = PressItem;
            list.ExamineItemRequested = ExamineItem;
            list.Cell.SourceKind  = ItemDragSource.Equipment;
            list.Cell.SlotIndex   = i;              // definition position = equipped drag-payload SourceSlot
            list.Cell.TooltipTextResolve = g => ItemTooltipCaption.Resolve(
                _objects, g, _resolveAppropriateName);
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
        if (_dollViewport is UiViewport doll)
            doll.ClickedAt = HandleDollClick;

        // The mask over the rendered figure is authored as a hit region with no
        // face of its own, and which widget class it imports as is an authoring
        // detail that has changed under us before. Bind the behaviour to the
        // element, not to a class: the region carries the whole pointer surface.
        _dollDragMask = layout.FindElement(DollDragMaskId);
        if (_dollDragMask is not null)
        {
            _dollDragMask.ClickThrough = false;
            _dollDragMask.PointerRegion = new UiPointerRegion
            {
                Clicked = HandleDollClick,
                RightClicked = HandleDollRightClick,
                DragPayloadAt = BuildDollDragPayload,
                DragGhostAt = BuildDollDragGhost,
                DragOverAt = HandleDollDragOver,
                DragLeft = ClearDollDragAcceptance,
                DropReleasedAt = HandleDollDrop,
            };
        }
        Console.WriteLine(
            $"[UI] paperdoll doll mask 0x{DollDragMaskId:X8} built as "
            + $"{_dollDragMask?.GetType().Name ?? "nothing"} "
            + $"{(int?)_dollDragMask?.Width ?? 0}x{(int?)_dollDragMask?.Height ?? 0}, click map "
            + (_clickMap is null ? "missing." : $"{_clickMap.Width}x{_clickMap.Height}."));

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
        /// <summary>Composes an item's displayed name, material prefix
        /// included. Required: without it a cell would quietly caption the
        /// plain name and disagree with the selection caption.</summary>
        Func<ClientObject, string> resolveAppropriateName,
        uint emptySlotSprite = 0u, UiDatFont? datFont = null,
        PaperdollClickMap? clickMap = null,
        Func<ItemType, uint, uint, uint, uint, uint>? dragIconIds = null,
        IReadOnlyDictionary<uint, uint>? emptySlotSprites = null,
        bool ownsItemInteraction = false,
        IPaperdollFigureLighting? figureLighting = null)
        => new PaperdollController(
            layout, objects, playerGuid, iconIds, selection, itemInteraction, emptySlotSprite,
            datFont, clickMap, dragIconIds, emptySlotSprites, ownsItemInteraction, figureLighting,
            resolveAppropriateName);

    private const int DollDragGhostSize = 32;

    /// <summary>Whether a drag now over the doll would be worn on release. The
    /// authored accept/reject overlay art is not imported yet, so this is state
    /// the panel keeps rather than paints.</summary>
    public ItemDragAcceptance DollDragAcceptance { get; private set; }

    /// <summary>The doll hit test: an element-local point picks a colour out of
    /// the click map, the colour is a body location, and the body location picks
    /// the outermost item worn there. The map is sampled one pixel per pixel with
    /// no scaling, and an unmapped point yields no body location and no object.
    /// A mapped but bare location yields the player, who wears nothing there.</summary>
    private uint DollObjectUnderPoint(int x, int y, out EquipMask bodyLocation)
    {
        bodyLocation = _clickMap?.GetBodyLocation(x, y) ?? EquipMask.None;
        return bodyLocation == EquipMask.None
            ? 0u
            : PaperdollSelectionPolicy.GetUpperInventoryObject(_objects, _playerGuid(), bodyLocation);
    }

    /// <summary>The worn item under an element-local point, or 0 for an unmapped
    /// point or a bare body location (where the hit test answers with the player).</summary>
    private uint DollItemUnderPoint(int x, int y, out EquipMask bodyLocation)
    {
        uint hit = DollObjectUnderPoint(x, y, out bodyLocation);
        return hit == _playerGuid() ? 0u : hit;
    }

    private void HandleDollClick(int x, int y)
    {
        uint hitObject = DollObjectUnderPoint(x, y, out _);
        if (hitObject == 0)
            return;

        if (_itemInteraction?.OfferSelfPrimaryClick()
            is not null and not ItemPrimaryClickResult.NotActive)
            return;

        _selection.Select(hitObject, SelectionChangeSource.Paperdoll);
    }

    /// <summary>Right click selects what the hit test found and then examines it,
    /// the same pair the equipped-slot grid runs on its own right click.</summary>
    private void HandleDollRightClick(int x, int y)
    {
        uint hitObject = DollObjectUnderPoint(x, y, out _);
        if (hitObject == 0)
            return;
        ExamineItem(hitObject);
    }

    /// <summary>Lifting from the doll carries the same payload the matching
    /// equipped slot would: the worn item, an equipment source, and the slot the
    /// resolved body location belongs to. A bare or unmapped region lifts nothing.</summary>
    private object? BuildDollDragPayload(int x, int y)
    {
        uint itemId = DollItemUnderPoint(x, y, out EquipMask bodyLocation);
        return itemId == 0
            ? null
            : new ItemDragPayload(
                itemId,
                ItemDragSource.Equipment,
                DollSlotIndex(bodyLocation),
                SourceCell: null);
    }

    private (uint tex, int w, int h)? BuildDollDragGhost(int x, int y)
    {
        uint itemId = DollItemUnderPoint(x, y, out _);
        if (itemId == 0 || _objects.Get(itemId) is not { } item)
            return null;
        uint dragTex = _dragIconIds?.Invoke(
            item.Type, item.IconId, item.IconUnderlayId, item.IconOverlayId, item.Effects) ?? 0u;
        if (dragTex != 0u)
            return (dragTex, DollDragGhostSize, DollDragGhostSize);
        uint tex = _iconIds(item.Type, item.IconId, item.IconUnderlayId, item.IconOverlayId, item.Effects);
        return tex != 0u ? (tex, DollDragGhostSize, DollDragGhostSize) : null;
    }

    /// <summary>Definition index of the equipped slot that covers a body location,
    /// so a doll lift reports the same source slot the slot grid would.</summary>
    private static int DollSlotIndex(EquipMask bodyLocation)
    {
        for (int i = 0; i < PaperdollSlotBackgrounds.Definitions.Length; i++)
            if ((PaperdollSlotBackgrounds.Definitions[i].Mask & bodyLocation) != EquipMask.None)
                return i;
        return -1;
    }

    private void ClearDollDragAcceptance() => DollDragAcceptance = ItemDragAcceptance.None;

    /// <summary>The doll takes a drop anywhere on its body, so acceptance ignores
    /// which region the pointer is over. It is a preview only: it asks whether the
    /// item is wearable at all, not the full legality the wield transaction runs
    /// (which does weigh what is already worn against the item's priority), so a
    /// drag can read as accepted and still be turned away on release.</summary>
    private void HandleDollDragOver(object? payload, int x, int y)
        => DollDragAcceptance = payload is not ItemDragPayload drag
            || drag.SourceKind == ItemDragSource.ShortcutBar
                ? ItemDragAcceptance.None
                : _objects.Get(drag.ObjId) is { } item && ItemEquipRules.IsAutoWearItem(item)
                    ? ItemDragAcceptance.Accept
                    : ItemDragAcceptance.Reject;

    /// <summary>A drop on the doll wears a wearable item without naming a body
    /// location: the whole of its valid locations goes out and the server decides
    /// where it lands. An item that is not wearable at all is refused.</summary>
    private void HandleDollDrop(object? payload, int x, int y)
    {
        ClearDollDragAcceptance();
        if (payload is not ItemDragPayload drag || drag.SourceKind == ItemDragSource.ShortcutBar)
            return;
        if (_objects.Get(drag.ObjId) is not { } item || !ItemEquipRules.IsAutoWearItem(item))
            return;
        _itemInteraction.WieldFromPaperdoll(drag.ObjId, item.ValidLocations);
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
    private void OnSelectionChanged(SelectionTransition _)
    {
        ApplySelectionIndicators();
        FlashSelectedFigureParts();
    }

    /// <summary>Selecting an item flashes the parts of the figure it covers -
    /// any selection, not just one made on the doll, and nothing at all for an
    /// item the figure does not wear.</summary>
    private void FlashSelectedFigureParts()
    {
        if (_figureLighting is null)
            return;
        PaperdollFigureParts.FigureFlash flash = PaperdollFigureParts.Resolve(
            _objects, _playerGuid(), _selection.SelectedObjectId ?? 0u);
        if (flash.WholeFigure)
            _figureLighting.FlashWholeFigure();
        else if (flash.PartMask != 0u)
            _figureLighting.FlashParts(flash.PartMask);
    }
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
        if (_dollDragMask is not null)
            _dollDragMask.PointerRegion = null;
        if (_ownsItemInteraction)
            _itemInteraction.Dispose();
    }
}
