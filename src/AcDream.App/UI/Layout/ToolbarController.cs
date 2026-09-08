using System;
using System.Collections.Generic;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Selection;

namespace AcDream.App.UI.Layout;

public sealed class ToolbarController : IItemListDragHandler, IRetainedPanelController
{
    private static readonly uint[] SlotIds =
    {
        0x100001A7, 0x100001A8, 0x100001A9, 0x100001AA, 0x100001AB,
        0x100001AC, 0x100001AD, 0x100001AE, 0x100001AF,
        0x100006B7, 0x100006B8, 0x100006B9, 0x100006BA, 0x100006BB,
        0x100006BC, 0x100006BD, 0x100006BE, 0x100006BF,
    };


    private static readonly uint[] CombatIndicatorIds =
        { 0x10000192u, 0x10000193u, 0x10000194u, 0x10000195u };

    private const uint PanelIdAttribute = 0x10000029u;
    private const uint UseButtonId = 0x1000019Du;
    private const uint ExamineButtonId = 0x100001A5u;
    private const uint AmmoIndicatorId = 0x10000194u;
    private const uint InventoryButtonId = 0x100001B1u;
    private static readonly uint[] PanelButtonIds =
        { 0x10000197u, 0x10000198u, 0x10000199u, 0x1000055Au, 0x1000019Au, 0x1000019Bu, InventoryButtonId };

    private readonly UiItemList?[] _slots = new UiItemList?[SlotIds.Length];
    private readonly UiElement?[] _combatIndicators = new UiElement?[CombatIndicatorIds.Length];
    private readonly UiButton? _inventoryButton;
    private readonly UiButton? _useButton;
    private readonly UiButton? _examineButton;
    private readonly UiButton? _ammoIndicator;
    private readonly List<(uint PanelId, UiButton Button)> _panelButtons = new();
    private readonly ClientObjectTable _repo;
    private readonly CombatState? _combatState;
    private readonly ShortcutStore _store;
    private readonly Func<ItemType, uint, uint, uint, uint, uint> _iconIds;  // (itemType, icon, underlay, overlay, effects) → GL tex
    private readonly Func<ItemType, uint, uint, uint, uint, uint>? _dragIconIds;
    private readonly Action<uint> _useItem;                   // guid → fire UseObject
    private readonly Action<ShortcutEntry>? _sendAddShortcut;
    private readonly Action<uint>? _sendRemoveShortcut;      // (index)
    private readonly ItemInteractionController? _itemInteraction;
    private readonly Action<uint>? _selectItem;
    private readonly Func<uint> _selectedObjectId;
    private readonly SelectionState? _selection;
    private readonly Func<uint>? _playerGuid;
    private readonly Action<uint, uint, int>? _sendPutItemInContainer;
    private uint _ammoObjectId;
    private bool _disposed;

    private uint[]? _regularDigits;
    private uint[]? _ghostedDigits;
    private uint[]? _emptyDigits;
    private bool _shortcutsGhosted;

    private ToolbarController(
        ImportedLayout layout,
        ClientObjectTable repo,
        ShortcutStore shortcuts,
        Func<ItemType, uint, uint, uint, uint, uint> iconIds,
        Action<uint> useItem,
        CombatState? combatState,
        uint[]? regularDigits,
        uint[]? ghostedDigits,
        uint[]? emptyDigits,
        ItemInteractionController? itemInteraction = null,
        Action<ShortcutEntry>? sendAddShortcut = null,
        Action<uint>? sendRemoveShortcut = null,
        Action? toggleCombat = null,
        Action<uint>? selectItem = null,
        Func<uint>? selectedObjectId = null,
        SelectionState? selection = null,
        Func<uint>? playerGuid = null,
        Action<uint, uint, int>? sendPutItemInContainer = null,
        UiDatFont? ammoFont = null,
        Func<ItemType, uint, uint, uint, uint, uint>? dragIconIds = null)
    {
        _repo = repo;
        _combatState = combatState;
        _store = shortcuts ?? throw new ArgumentNullException(nameof(shortcuts));
        _iconIds = iconIds;
        _dragIconIds = dragIconIds;
        _useItem = useItem;
        _regularDigits = regularDigits;
        _ghostedDigits = ghostedDigits;
        _emptyDigits = emptyDigits;
        _itemInteraction = itemInteraction;
        _sendAddShortcut    = sendAddShortcut;
        _sendRemoveShortcut = sendRemoveShortcut;
        _selectItem = selectItem;
        _selectedObjectId = selectedObjectId ?? (() => 0u);
        _selection = selection;
        _playerGuid = playerGuid;
        _sendPutItemInContainer = sendPutItemInContainer;

        for (int i = 0; i < SlotIds.Length; i++)
        {
            _slots[i] = layout.FindElement(SlotIds[i]) as UiItemList;
            if (_slots[i] is { } list)
            {
                WireClick(list);
                list.PrimaryItemPressed = PressItem;
                list.ExamineItemRequested = ExamineItem;
                list.RegisterDragHandler(this);
                list.Cell.SlotIndex  = i;
                list.Cell.SourceKind = ItemDragSource.ShortcutBar;
                list.Cell.DragAcceptSprite = 0x060011FAu;
                list.Cell.TooltipTextResolve = g => _repo.Get(g)?.GetTooltipDisplayName();
            }
        }

        for (int i = 0; i < CombatIndicatorIds.Length; i++)
        {
            _combatIndicators[i] = layout.FindElement(CombatIndicatorIds[i]);
            if (_combatIndicators[i] is UiButton button)
                button.OnClick = toggleCombat;
        }

        _ammoIndicator = layout.FindElement(AmmoIndicatorId) as UiButton;
        if (_ammoIndicator is not null)
        {
            _ammoIndicator.LabelFont = ammoFont;
            _ammoIndicator.LabelColor = System.Numerics.Vector4.One;
        }

        foreach (uint elementId in PanelButtonIds)
        {
            if (layout.FindElement(elementId) is UiButton panelButton
                && panelButton.TryGetEnumAttribute(PanelIdAttribute, out uint panelId))
                _panelButtons.Add((panelId, panelButton));
        }

        _inventoryButton = layout.FindElement(InventoryButtonId) as UiButton;
        if (_inventoryButton is not null)
        {
            _inventoryButton.FoundObjectGuidProvider = () =>
                _playerGuid?.Invoke() ?? _itemInteraction?.PlayerGuid ?? 0u;
            _inventoryButton.ItemDragAcceptSprite = 0x060011F7u;
            _inventoryButton.OnItemDragOver = InventoryButtonDragOver;
            _inventoryButton.OnItemDrop = HandleInventoryButtonDrop;
        }

        _useButton = layout.FindElement(UseButtonId) as UiButton;
        _examineButton = layout.FindElement(ExamineButtonId) as UiButton;
        if (_useButton is not null)
            _useButton.OnClick = () => _itemInteraction?.UseSelectedOrEnterMode(_selectedObjectId());
        if (_examineButton is not null)
            _examineButton.OnClick = () => _itemInteraction?.ExamineSelectedOrEnterMode(_selectedObjectId());

        SetCombatMode(CombatMode.NonCombat);

        if (_combatState is not null)
            _combatState.CombatModeChanged += SetCombatMode;
        if (_selection is not null)
            _selection.Changed += OnSelectionChanged;

        repo.ObjectAdded   += OnRepositoryObjectChanged;
        repo.ObjectUpdated += OnRepositoryObjectChanged;
        repo.ObjectRemoved += OnRepositoryObjectChanged;
        repo.ObjectMoved   += OnRepositoryObjectMoved;
        repo.Cleared       += OnRepositoryCleared;
        _store.Changed     += Populate;
        RefreshAmmo();
        RefreshUseButton();
    }

    private void OnRepositoryObjectChanged(ClientObject obj)
    {
        if (IsAmmoRelated(obj))
            RefreshAmmo();
        if (IsShortcutGuid(obj.ObjectId))
            Populate();
        if (obj.ObjectId == _selectedObjectId())
            RefreshUseButton();
    }

    private void OnRepositoryObjectMoved(ClientObjectMove move)
    {
        uint player = _playerGuid?.Invoke() ?? 0u;
        if (move.ItemId == _ammoObjectId
            || (player != 0
                && (move.Previous.ContainerId == player
                    || move.Current.ContainerId == player
                    || move.Previous.WielderId == player
                    || move.Current.WielderId == player)))
            RefreshAmmo();
        if (IsShortcutGuid(move.ItemId))
            Populate();
    }

    private void OnRepositoryCleared()
    {
        RefreshAmmo();
        Populate();
        RefreshUseButton();
    }

    private void OnSelectionChanged(SelectionTransition _)
        => RefreshUseButton();

    private void RefreshUseButton()
    {
        if (_useButton is null)
            return;

        _useButton.Enabled =
            _itemInteraction?.IsToolbarUseEnabled(_selectedObjectId()) == true;
    }

    private bool IsAmmoRelated(ClientObject obj)
    {
        uint player = _playerGuid?.Invoke() ?? 0u;
        return obj.ObjectId == _ammoObjectId
            || (player != 0 && obj.ObjectId == player)
            || (player != 0
                && (obj.WielderId == player || obj.ContainerId == player)
                && (obj.CurrentlyEquippedLocation
                    & (EquipMask.MissileWeapon | EquipMask.MissileAmmo)) != 0);
    }

    private void RefreshAmmo()
    {
        uint player = _playerGuid?.Invoke() ?? 0u;
        IReadOnlyList<ClientObject> placements = player != 0
            ? _repo.GetEquippedBy(player)
            : Array.Empty<ClientObject>();

        ToolbarAmmoPolicy.Result result = ToolbarAmmoPolicy.Resolve(placements);
        _ammoObjectId = result.ObjectId;
        if (_ammoIndicator is not null)
        {
            _ammoIndicator.Label = result.IsVisible
                ? result.DisplayCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
        }
    }

    private bool IsShortcutGuid(uint guid)
    {
        for (int s = 0; s < ShortcutStore.SlotCount; s++)
            if (_store.Get(s) == guid) return true;
        return false;
    }

    public static ToolbarController Bind(
        ImportedLayout layout,
        ClientObjectTable repo,
        ShortcutStore shortcuts,
        Func<ItemType, uint, uint, uint, uint, uint> iconIds,
        Action<uint> useItem,
        CombatState? combatState = null,
        uint[]? regularDigits = null,
        uint[]? ghostedDigits = null,
        uint[]? emptyDigits = null,
        ItemInteractionController? itemInteraction = null,
        Action<ShortcutEntry>? sendAddShortcut = null,
        Action<uint>? sendRemoveShortcut = null,
        Action? toggleCombat = null,
        Action<uint>? selectItem = null,
        Func<uint>? selectedObjectId = null,
        SelectionState? selection = null,
        Func<uint>? playerGuid = null,
        Action<uint, uint, int>? sendPutItemInContainer = null,
        UiDatFont? ammoFont = null,
        Func<ItemType, uint, uint, uint, uint, uint>? dragIconIds = null)
    {
        var c = new ToolbarController(layout, repo, shortcuts, iconIds, useItem, combatState,
                                      regularDigits, ghostedDigits, emptyDigits, itemInteraction,
                                      sendAddShortcut, sendRemoveShortcut, toggleCombat, selectItem,
                                      selectedObjectId, selection, playerGuid, sendPutItemInContainer, ammoFont,
                                      dragIconIds);
        c.Populate();
        return c;
    }

    public void BindPanelButtons(Func<uint, bool> isAvailable, Action<uint> togglePanel)
    {
        ArgumentNullException.ThrowIfNull(isAvailable);
        ArgumentNullException.ThrowIfNull(togglePanel);

        foreach (var (panelId, button) in _panelButtons)
        {
            bool available = isAvailable(panelId);
            button.Enabled = available;
            button.OnClick = !available ? null : () =>
            {
                if (ReferenceEquals(button, _inventoryButton)
                    && _itemInteraction?.OfferSelfPrimaryClick()
                        is not null and not ItemPrimaryClickResult.NotActive)
                    return;
                togglePanel(panelId);
            };
        }
    }

    public void SetPanelOpen(uint panelId, bool open)
    {
        foreach (var entry in _panelButtons)
        {
            if (entry.PanelId != panelId || !entry.Button.Enabled) continue;
            entry.Button.TrySetRetailState(open
                ? UiButtonStateMachine.Highlight
                : UiButtonStateMachine.Normal);
            return;
        }
    }

    public void Populate()
    {
        foreach (var list in _slots) list?.Cell.Clear();

        for (int slot = 0; slot < _slots.Length; slot++)
        {
            ShortcutEntry? entry = _store.GetEntry(slot);
            uint guid = entry?.ObjectId ?? 0u;
            if (guid == 0) continue;
            var list = _slots[slot];
            if (list is null) continue;
            var item = _repo.Get(guid);
            if (item is null) continue;
            uint tex = _iconIds(item.Type, item.IconId, item.IconUnderlayId, item.IconOverlayId, item.Effects);
            uint dragTex = _dragIconIds?.Invoke(
                item.Type, item.IconId, item.IconUnderlayId, item.IconOverlayId, item.Effects) ?? 0u;
            list.Cell.SetItem(guid, tex, entry, dragTex);
        }

        RestampShortcutNumbers();
    }

    public void SetCombatMode(CombatMode mode)
    {
        bool[] show =
        {
            mode == CombatMode.NonCombat,
            mode == CombatMode.Melee,
            mode == CombatMode.Missile,
            mode == CombatMode.Magic,
        };

        for (int i = 0; i < _combatIndicators.Length; i++)
        {
            if (_combatIndicators[i] is { } e)
                e.Visible = show[i];
        }

        _shortcutsGhosted = mode == CombatMode.Magic;
        RestampShortcutNumbers();
    }

    private void RestampShortcutNumbers()
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            var cell = _slots[i]?.Cell;
            if (cell is null) continue;
            cell.RegularDigits = _regularDigits;
            cell.GhostedDigits = _ghostedDigits;
            cell.EmptyDigits = _emptyDigits;
            if (i < 9)
                cell.SetShortcutNum(i, _shortcutsGhosted); // top row: slot labels 1–9 always shown
            else
                cell.ClearShortcutNum();          // bottom row: no slot labels
        }
    }

    private void WireClick(UiItemList list)
    {
        list.Cell.Clicked = () =>
        {
            if (list.Cell.ItemId != 0)
            {
                if (_itemInteraction is not null)
                    _itemInteraction.ActivateItem(list.Cell.ItemId);
                else
                    _useItem(list.Cell.ItemId);
            }
        };
    }

    private bool PressItem(uint itemId)
    {
        if (_itemInteraction?.OfferPrimaryClick(itemId)
            is not null and not ItemPrimaryClickResult.NotActive)
            return true;

        if (_selectItem is not null)
            _selectItem(itemId);
        else
            _selection?.Select(itemId, SelectionChangeSource.Toolbar);
        return false;
    }

    private void ExamineItem(uint itemId)
    {
        if (_selectItem is not null)
            _selectItem(itemId);
        else
            _selection?.Select(itemId, SelectionChangeSource.Toolbar);
        _itemInteraction?.ExamineSelectedOrEnterMode(itemId);
    }

    private static ItemDragAcceptance InventoryButtonDragOver(ItemDragPayload payload)
        => payload.ObjId != 0 && payload.SourceKind != ItemDragSource.ShortcutBar
            ? ItemDragAcceptance.Accept
            : ItemDragAcceptance.None;

    private void HandleInventoryButtonDrop(ItemDragPayload payload)
    {
        if (payload.ObjId == 0 || payload.SourceKind == ItemDragSource.ShortcutBar)
            return;
        uint player = _playerGuid?.Invoke() ?? 0u;
        if (player == 0 || _repo.Get(payload.ObjId) is null)
            return;
        if (_itemInteraction is not null)
        {
            Func<bool> dispatch = () =>
            {
                if (_sendPutItemInContainer is null)
                    return false;
                _sendPutItemInContainer(payload.ObjId, player, 0);
                return true;
            };
            if (_itemInteraction.IsOwnedByPlayer(payload.ObjId))
                _itemInteraction.TryDispatchPendingBackpackPlacement(
                    payload.ObjId,
                    player,
                    0,
                    InventoryRequestKind.PutInContainer,
                    dispatch);
            else
                _itemInteraction.TryDispatchInventoryRequest(
                    InventoryRequestKind.Pickup,
                    payload.ObjId,
                    dispatch);
            return;
        }
        _sendPutItemInContainer?.Invoke(payload.ObjId, player, 0);
    }

    public bool UseShortcut(int slot, bool use)
    {
        if ((uint)slot >= _slots.Length || _slots[slot]?.Cell is not { } cell)
            return false;

        uint itemId = cell.ItemId;
        if (_itemInteraction?.IsTargetModeActive == true)
        {
            if (itemId != 0)
                _itemInteraction.OfferPrimaryClick(itemId);
            else
                _itemInteraction.CancelTargetMode();
            return true;
        }

        if (itemId == 0)
            return false;

        if (use)
        {
            if (_itemInteraction is not null)
                _itemInteraction.ActivateItem(itemId);
            else
                _useItem(itemId);
        }
        else
        {
            _selectItem?.Invoke(itemId);
        }

        return true;
    }

    public bool CreateShortcutToItem(uint itemId)
    {
        if (itemId == 0 || _repo.Get(itemId) is not { } item)
            return false;

        var flags = (PublicWeenieFlags)(item.PublicWeenieBitfield ?? 0u);
        bool isPlayer = itemId == _itemInteraction?.PlayerGuid
            || (flags & PublicWeenieFlags.Player) != 0;
        if ((flags & PublicWeenieFlags.Stuck) != 0 && !isPlayer)
            return false;
        if ((item.Type & ItemType.Creature) != 0 && !isPlayer)
            return false;
        if (_itemInteraction?.IsOwnedByPlayer(itemId) != true)
            return false;

        for (int slot = 0; slot < _slots.Length; slot++)
            if (_store.Get(slot) == itemId)
                return false;

        int empty = -1;
        for (int slot = 0; slot < _slots.Length; slot++)
        {
            if (_store.IsEmpty(slot))
            {
                empty = slot;
                break;
            }
        }
        if (empty < 0)
            return false;

        var entry = new ShortcutEntry(empty, itemId, 0u);
        _sendAddShortcut?.Invoke(entry);
        _store.Set(entry);
        return true;
    }


    /// <inheritdoc/>
    public void OnDragLift(UiItemList sourceList, UiItemSlot sourceCell, ItemDragPayload payload)
    {
        if (payload.ObjId != 0 && _selectedObjectId() != payload.ObjId)
            _selectItem?.Invoke(payload.ObjId);

        _sendRemoveShortcut?.Invoke((uint)payload.SourceSlot);
        _store.Remove(payload.SourceSlot);
    }

    /// <inheritdoc/>
    public ItemDragAcceptance OnDragOver(UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload)
        => payload.ObjId != 0 ? ItemDragAcceptance.Accept : ItemDragAcceptance.None;

    /// <inheritdoc/>
    public void HandleDropRelease(UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload)
    {
        ShortcutDropSource source = payload.SourceKind == ItemDragSource.ShortcutBar
            ? ShortcutDropSource.ShortcutAlias
            : ShortcutDropSource.FreshItem;
        ShortcutEntry dragged = payload.Shortcut
            ?? new ShortcutEntry(payload.SourceSlot, payload.ObjId, 0u);
        ShortcutMutation[] plan = ShortcutDropPlanner.PlanDrop(
            _store.Snapshot(), source, payload.SourceSlot, targetCell.SlotIndex, dragged);

        ApplyShortcutPlan(plan);
    }

    public void ReplaceFullyMergedShortcut(uint oldObjectId, uint newObjectId)
    {
        ShortcutMutation[] plan = ShortcutDropPlanner.PlanFullStackMerge(
            _store.Snapshot(), oldObjectId, newObjectId);
        ApplyShortcutPlan(plan);
    }

    private void ApplyShortcutPlan(IReadOnlyList<ShortcutMutation> plan)
    {
        foreach (var mutation in plan)
        {
            if (mutation.Kind == ShortcutMutationKind.Remove)
            {
                _sendRemoveShortcut?.Invoke((uint)mutation.Slot);
                _store.Remove(mutation.Slot);
            }
            else if (mutation.Entry is { } entry)
            {
                _sendAddShortcut?.Invoke(entry);
                _store.Set(entry);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (_, button) in _panelButtons)
            button.OnClick = null;
        foreach (var indicator in _combatIndicators)
            if (indicator is UiButton button)
                button.OnClick = null;
        if (_useButton is not null)
            _useButton.OnClick = null;
        if (_examineButton is not null)
            _examineButton.OnClick = null;
        if (_inventoryButton is not null)
        {
            _inventoryButton.OnItemDragOver = null;
            _inventoryButton.OnItemDrop = null;
        }

        if (_combatState is not null)
            _combatState.CombatModeChanged -= SetCombatMode;
        if (_selection is not null)
            _selection.Changed -= OnSelectionChanged;
        foreach (UiItemList? list in _slots)
            if (list is not null)
            {
                list.PrimaryItemPressed = null;
                list.ExamineItemRequested = null;
            }
        _repo.ObjectAdded   -= OnRepositoryObjectChanged;
        _repo.ObjectUpdated -= OnRepositoryObjectChanged;
        _repo.ObjectRemoved -= OnRepositoryObjectChanged;
        _repo.ObjectMoved   -= OnRepositoryObjectMoved;
        _repo.Cleared       -= OnRepositoryCleared;
        _store.Changed      -= Populate;
    }
}
