using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.App.Spells;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Spells;
using AcDream.Core.Selection;
using AcDream.Runtime.Gameplay;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.UI.Layout;

public sealed class SpellcastingUiController : IRetainedPanelController
{
    public const uint PageId = 0x10000061u;
    public const uint SpellNameId = 0x1000048Bu;
    public const uint EndowmentId = 0x100000B1u;
    public const uint CastButtonId = 0x100000B2u;
    public const uint FavoriteScrollbarId = 0x100000B5u;
    public const uint FavoriteListId = 0x100000B6u;

    private static readonly uint[] TabIds =
    [
        0x100000A3u, 0x100000A4u, 0x100000A5u, 0x100000A6u,
        0x100000A7u, 0x100000A8u, 0x100000A9u, 0x100005C2u,
    ];

    private static readonly uint[] GroupIds =
    [
        0x100000AAu, 0x100000ABu, 0x100000ACu, 0x100000ADu,
        0x100000AEu, 0x100000AFu, 0x100000B0u, 0x100005C3u,
    ];

    private readonly Spellbook _spellbook;
    private readonly RuntimeSpellCastState _casting;
    private readonly SelectionState _selection;
    private readonly ClientObjectTable _objects;
    private readonly Func<uint> _playerGuid;
    private readonly Func<uint, uint> _resolveSpellIcon;
    private readonly Func<ClientObject, uint> _resolveItemDragIcon;
    private readonly Action<uint> _useItem;
    private readonly Action<uint>? _examineSpell;
    private readonly Action<int, int, uint>? _addFavorite;
    private readonly Action<int, uint>? _removeFavorite;
    private readonly UiShortcutDigitGraphics? _shortcutDigits;
    private readonly uint _emptySlotSprite;
    private readonly UiElement[] _tabs;
    private readonly UiElement[] _groups;
    private readonly UiItemList?[] _lists;
    private readonly UiScrollbar?[] _scrollbars;
    private readonly UiButton _cast;
    private readonly UiText? _spellName;
    private readonly UiElement _endowmentHost;
    private readonly UiCatalogSlot _endowmentSlot;
    private int _activeTab;
    private readonly uint?[] _selected = new uint?[8];
    private readonly bool[] _endowmentSelected = new bool[8];
    private readonly float[] _favoriteViewportWidths = new float[8];
    private uint _endowmentItemId;
    private uint _endowmentSpellId;
    private bool _disposed;
    private bool _favoritesDirty;
    private bool _endowmentDirty;
    private bool _favoriteDragActive;

    private SpellcastingUiController(
        ImportedLayout layout,
        Spellbook spellbook,
        RuntimeSpellCastState casting,
        ClientObjectTable objects,
        Func<uint> playerGuid,
        Func<uint, uint> resolveSpellIcon,
        Func<ClientObject, uint> resolveItemDragIcon,
        Action<uint> useItem,
        Action<uint>? examineSpell,
        SelectionState selection,
        Action<int, int, uint>? addFavorite,
        Action<int, uint>? removeFavorite,
        UiElement[] tabs,
        UiElement[] groups,
        UiItemList?[] lists,
        UiScrollbar?[] scrollbars,
        UiButton cast,
        UiElement endowmentHost,
        UiShortcutDigitGraphics? shortcutDigits,
        uint emptySlotSprite)
    {
        _spellbook = spellbook;
        _casting = casting;
        _selection = selection;
        _objects = objects;
        _playerGuid = playerGuid;
        _resolveSpellIcon = resolveSpellIcon;
        _resolveItemDragIcon = resolveItemDragIcon;
        _useItem = useItem;
        _examineSpell = examineSpell;
        _addFavorite = addFavorite;
        _removeFavorite = removeFavorite;
        _shortcutDigits = shortcutDigits;
        _emptySlotSprite = emptySlotSprite;
        _tabs = tabs;
        _groups = groups;
        _lists = lists;
        _scrollbars = scrollbars;
        _cast = cast;
        _spellName = layout.FindElement(SpellNameId) as UiText;
        _endowmentHost = endowmentHost;
        foreach (UiElement child in _endowmentHost.Children) child.Visible = false;
        _endowmentSlot = new UiCatalogSlot
        {
            Left = 0f,
            Top = 0f,
            Width = endowmentHost.Width,
            Height = endowmentHost.Height,
            SpriteResolve = lists.FirstOrDefault(list => list is not null)?.SpriteResolve,
        };
        _endowmentSlot.Clicked = SelectEndowment;
        _endowmentSlot.DoubleClicked = () => { SelectEndowment(); CastSelected(); };
        _endowmentHost.AddChild(_endowmentSlot);

        for (int i = 0; i < _lists.Length; i++)
        {
            if (_lists[i] is not { } list)
                continue;
            list.PrimaryCatalogEntryPressed = SelectSpell;
            list.ExamineCatalogEntryRequested = _examineSpell;
            if (_scrollbars[i] is not { } scrollbar)
                continue;
            list.HorizontalScroll = true;
            scrollbar.Horizontal = true;
            scrollbar.Model = list.Scroll;
            scrollbar.SpriteResolve ??= list.SpriteResolve;
        }

        for (int i = 0; i < tabs.Length; i++)
        {
            int index = i;
            SetClick(tabs[i], () => SelectTab(index));
        }
        _cast.OnClick = CastSelected;
        ConfigureSpellName();
        _spellbook.SpellbookChanged += OnSpellbookChanged;
        _selection.Changed += OnSelectionChanged;
        _objects.ObjectAdded += OnObjectChanged;
        _objects.ObjectUpdated += OnObjectChanged;
        _objects.ObjectRemoved += OnObjectChanged;
        _objects.Cleared += OnObjectsCleared;
        UpdateEndowment();
        SelectTab(0);
        Rebuild();
        for (int tab = 0; tab < _lists.Length; tab++)
            _favoriteViewportWidths[tab] = _lists[tab]?.Width ?? 0f;
    }

    public int ActiveTab => _activeTab;

    public static SpellcastingUiController? Bind(
        ImportedLayout layout,
        Spellbook spellbook,
        RuntimeSpellCastState casting,
        ClientObjectTable objects,
        Func<uint> playerGuid,
        Func<uint, uint> resolveSpellIcon,
        Func<ClientObject, uint> resolveItemDragIcon,
        Action<uint> useItem,
        SelectionState selection,
        Action<int, int, uint>? addFavorite,
        Action<int, uint>? removeFavorite,
        UiShortcutDigitGraphics? shortcutDigits = null,
        uint emptySlotSprite = 0u,
        Action<uint>? examineSpell = null)
    {
        if (layout.FindElement(CastButtonId) is not UiButton cast
            || layout.FindElement(EndowmentId) is not { } endowmentHost)
            return null;

        var tabs = new UiElement[8];
        var groups = new UiElement[8];
        var lists = new UiItemList?[8];
        var scrollbars = new UiScrollbar?[8];
        for (int i = 0; i < 8; i++)
        {
            if (layout.FindElement(TabIds[i]) is not { } tab
                || layout.FindElement(GroupIds[i]) is not { } group)
                return null;
            tabs[i] = tab;
            groups[i] = group;
            lists[i] = Descendants(group).OfType<UiItemList>().FirstOrDefault();
            scrollbars[i] = Descendants(group).OfType<UiScrollbar>().FirstOrDefault(
                scrollbar => scrollbar.DatElementId == FavoriteScrollbarId);
        }

        return new SpellcastingUiController(
            layout, spellbook, casting, objects, playerGuid, resolveSpellIcon,
            resolveItemDragIcon, useItem, examineSpell, selection,
            addFavorite, removeFavorite,
            tabs, groups, lists, scrollbars, cast, endowmentHost,
            shortcutDigits, emptySlotSprite);
    }

    public void AddFavorite(uint spellId)
    {
        IReadOnlyList<uint> spells = _spellbook.GetFavorites(_activeTab);
        if (spells.Contains(spellId))
        {
            SelectSpell(spellId);
            return;
        }
        int position = spells.Count;
        _addFavorite?.Invoke(_activeTab, position, spellId);
        SelectSpell(spellId);
    }

    public bool Handle(InputAction action)
    {
        if (TryMapSpellShortcut(action, out int index))
        {
            IReadOnlyList<uint> spells = _spellbook.GetFavorites(_activeTab);
            if (index < spells.Count)
            {
                SelectSpell(spells[index]);
                CastSelected();
            }
            return true;
        }

        switch (action)
        {
            case InputAction.CombatPrevSpellTab: SelectTab((_activeTab + 7) % 8); return true;
            case InputAction.CombatNextSpellTab: SelectTab((_activeTab + 1) % 8); return true;
            case InputAction.CombatFirstSpellTab: SelectTab(0); return true;
            case InputAction.CombatLastSpellTab: SelectTab(7); return true;
            case InputAction.CombatPrevSpell: MoveSelection(-1, false); return true;
            case InputAction.CombatNextSpell: MoveSelection(1, false); return true;
            case InputAction.CombatFirstSpell: MoveSelection(0, true); return true;
            case InputAction.CombatLastSpell: MoveSelection(-1, true); return true;
            case InputAction.CombatCastCurrentSpell: CastSelected(); return true;
            default: return false;
        }
    }

    internal static bool TryMapSpellShortcut(
        InputAction action,
        out int index)
    {
        if (action is >= InputAction.UseSpellSlot_1
            and <= InputAction.UseSpellSlot_9)
        {
            index = (int)action - (int)InputAction.UseSpellSlot_1;
            return true;
        }

        index = action switch
        {
            InputAction.UseSpellSlot_10 => 9,
            InputAction.UseSpellSlot_11 => 10,
            InputAction.UseSpellSlot_12 => 11,
            _ => -1,
        };
        return index >= 0;
    }

    private void SelectTab(int tab)
    {
        _activeTab = Math.Clamp(tab, 0, 7);
        for (int i = 0; i < 8; i++)
        {
            SetSelected(_tabs[i], i == _activeTab);
            _groups[i].Visible = i == _activeTab;
        }
        IReadOnlyList<uint> favorites = _spellbook.GetFavorites(_activeTab);
        if (_endowmentSelected[_activeTab] && _endowmentItemId != 0u)
            _selected[_activeTab] = null;
        else if (_selected[_activeTab] is not uint selected || !favorites.Contains(selected))
        {
            _selected[_activeTab] = favorites.Count == 0 ? null : favorites[0];
            _endowmentSelected[_activeTab] = favorites.Count == 0 && _endowmentItemId != 0u;
        }
        SyncSelection();
        UpdateCastAvailability();
    }

    private void MoveSelection(int delta, bool edge)
    {
        IReadOnlyList<uint> spells = _spellbook.GetFavorites(_activeTab);
        int count = spells.Count + (_endowmentItemId != 0u ? 1 : 0);
        if (count == 0) return;
        int current = _endowmentSelected[_activeTab]
            ? 0
            : (_selected[_activeTab] is uint id ? spells.IndexOf(id) : -1)
                + (_endowmentItemId != 0u ? 1 : 0);
        int next = edge ? (delta < 0 ? count - 1 : 0) : (current + delta + count) % count;
        if (_endowmentItemId != 0u && next == 0) SelectEndowment();
        else SelectSpell(spells[next - (_endowmentItemId != 0u ? 1 : 0)]);
    }

    private void SelectSpell(uint spellId)
    {
        _endowmentSelected[_activeTab] = false;
        _selected[_activeTab] = spellId;
        SyncSelection();
        ScrollSelectedSpellIntoView(_activeTab, spellId);
        UpdateCastAvailability();
    }

    private void ScrollSelectedSpellIntoView(int tab, uint spellId)
    {
        UiItemList? list = _lists[tab];
        if (list is null) return;
        for (int i = 0; i < list.GetNumUIItems(); i++)
        {
            if (list.GetItem(i) is UiCatalogSlot { EntryId: var entryId }
                && entryId == spellId)
            {
                list.ScrollItemIntoView(i);
                return;
            }
        }
    }

    private void CastSelected()
    {
        if (_endowmentSelected[_activeTab] && _endowmentItemId != 0u)
            _useItem(_endowmentItemId);
        else if (_selected[_activeTab] is uint spellId)
            _casting.Cast(spellId);
    }

    private void SelectEndowment()
    {
        if (_endowmentItemId == 0u) return;
        _endowmentSelected[_activeTab] = true;
        _selected[_activeTab] = null;
        SyncSelection();
        UpdateCastAvailability();
    }

    private void Rebuild()
    {
        for (int tab = 0; tab < 8; tab++)
        {
            UiItemList? list = _lists[tab];
            if (list is null) continue;
            IReadOnlyList<uint> favorites = _spellbook.GetFavorites(tab);
            int targetTab = tab;
            list.CatalogDropped = (payload, x, _) =>
            {
                if (payload is SpellbookShortcutDragPayload shortcut)
                    DropSpellbookShortcut(
                        shortcut, targetTab, DropPosition(list, x, favorites.Count));
            };
            using (list.DeferLayout())
            {
                list.Flush();
                list.SingleRow = true;
                list.HorizontalScroll = true;
                list.CellWidth = 32f;
                list.CellHeight = 32f;
                list.CellEmptySprite = _emptySlotSprite;
                list.EmptySlotFactory = () => CreateEmptyFavoriteSlot(list, targetTab);
                list.FillVisibleEmptySlots = true;
                foreach (uint spellId in favorites)
                {
                    uint id = spellId;
                    int position = list.GetNumUIItems();
                    _spellbook.TryGetMetadata(id, out SpellMetadata? metadata);
                    UiCatalogSlot? slot = null;
                    slot = new UiCatalogSlot
                    {
                        EntryId = id,
                        CatalogIconTexture = metadata is null ? 0u : _resolveSpellIcon(id),
                        Label = metadata?.Name ?? $"Spell {id}",
                        SpriteResolve = list.SpriteResolve,
                        CatalogDragPayload = new SpellFavoriteDragPayload(tab, position, id),
                        DragBegan = payload => BeginFavoriteDrag((SpellFavoriteDragPayload)payload),
                        DragEnded = payload => EndFavoriteDrag((SpellFavoriteDragPayload)payload),
                        DragOverAcceptance = FavoriteDragOverAcceptance,
                        Dropped = payload =>
                        {
                            if (payload is SpellFavoriteDragPayload favorite)
                                DropFavorite(favorite, targetTab, list, slot!);
                            else if (payload is SpellbookShortcutDragPayload shortcut)
                                DropSpellbookShortcut(shortcut, targetTab, position);
                        },
                    };
                    slot.DoubleClicked = CastSelected;
                    list.AddItem(slot);
                }
            }
            StampShortcutOverlays(list);
        }
        SelectTab(_activeTab);
    }

    private UiCatalogSlot CreateEmptyFavoriteSlot(UiItemList list, int targetTab)
    {
        int shortcutIndex = list.GetNumUIItems();
        UiCatalogSlot? slot = null;
        slot = new UiCatalogSlot
        {
            SpriteResolve = list.SpriteResolve,
            DragOverAcceptance = FavoriteDragOverAcceptance,
            Dropped = payload =>
            {
                if (payload is SpellFavoriteDragPayload favorite)
                    DropFavorite(favorite, targetTab, list, slot!);
                else if (payload is SpellbookShortcutDragPayload shortcut)
                {
                    int position = Math.Max(0, list.IndexOf(slot!));
                    position = Math.Min(position, _spellbook.GetFavorites(targetTab).Count);
                    DropSpellbookShortcut(shortcut, targetTab, position);
                }
            },
        };
        ConfigureShortcutOverlay(slot, shortcutIndex);
        return slot;
    }

    private void StampShortcutOverlays(UiItemList list)
    {
        for (int i = 0; i < list.GetNumUIItems(); i++)
        {
            UiItemSlot? slot = list.GetItem(i);
            if (slot is null) continue;
            ConfigureShortcutOverlay(slot, i);
        }
    }

    private void ConfigureShortcutOverlay(UiItemSlot slot, int index)
    {
        slot.RegularDigits = _shortcutDigits?.RegularDigits;
        slot.GhostedDigits = _shortcutDigits?.GhostedDigits;
        slot.EmptyDigits = _shortcutDigits?.EmptyDigits;
        if (index < 9)
            slot.SetShortcutNum(index, ghosted: false);
        else
            slot.ClearShortcutNum();
    }

    private void BeginFavoriteDrag(SpellFavoriteDragPayload payload)
    {
        _favoriteDragActive = true;
        _removeFavorite?.Invoke(payload.SourceTab, payload.SpellId);
    }

    private void EndFavoriteDrag(SpellFavoriteDragPayload payload)
    {
        _favoriteDragActive = false;
    }

    internal static ItemDragAcceptance FavoriteDragOverAcceptance(object payload)
        => payload is SpellFavoriteDragPayload or SpellbookShortcutDragPayload
            ? ItemDragAcceptance.Accept
            : ItemDragAcceptance.None;

    internal int FavoriteDropIndex(
        SpellFavoriteDragPayload payload, int targetTab, UiItemList list, UiItemSlot cell)
    {
        int index = Math.Max(0, list.IndexOf(cell));
        if (cell.IsEmptySlot)
            return Math.Min(index, _spellbook.GetFavorites(targetTab).Count);
        if (payload.SourceTab == targetTab && payload.SourcePosition < index)
            index -= 1;
        return index;
    }

    private void DropFavorite(
        SpellFavoriteDragPayload payload, int targetTab, UiItemList list, UiItemSlot cell)
    {
        int targetPosition = FavoriteDropIndex(payload, targetTab, list, cell);
        _addFavorite?.Invoke(targetTab, targetPosition, payload.SpellId);
        _selected[targetTab] = payload.SpellId;
    }

    private void DropSpellbookShortcut(
        SpellbookShortcutDragPayload payload,
        int targetTab,
        int targetPosition)
    {
        _addFavorite?.Invoke(targetTab, targetPosition, payload.SpellId);
        _selected[targetTab] = payload.SpellId;
    }

    private static int DropPosition(UiItemList list, int localX, int favoriteCount)
    {
        int position = list.CellWidth <= 0f
            ? favoriteCount
            : (int)MathF.Floor(MathF.Max(0f, localX) / list.CellWidth);
        return Math.Clamp(position, 0, favoriteCount);
    }

    private void OnSpellbookChanged() => _favoritesDirty = true;

    private void OnObjectChanged(ClientObject _) => _endowmentDirty = true;

    private void OnObjectsCleared() => _endowmentDirty = true;

    public void Tick()
    {
        if (_endowmentDirty)
        {
            _endowmentDirty = false;
            UpdateEndowment();
            SelectTab(_activeTab);
        }
        if (_favoritesDirty && !_favoriteDragActive)
        {
            _favoritesDirty = false;
            Rebuild();
        }
        for (int tab = 0; tab < _lists.Length; tab++)
        {
            if (_lists[tab] is not { } list || _favoriteViewportWidths[tab] == list.Width)
                continue;
            _favoriteViewportWidths[tab] = list.Width;
            list.LayoutCells();
            if (!_endowmentSelected[tab] && _selected[tab] is uint selected)
            {
                ScrollSelectedSpellIntoView(tab, selected);
                list.LayoutCells();
            }
        }
    }

    private void SyncSelection()
    {
        for (int tab = 0; tab < 8; tab++)
        {
            UiItemList? list = _lists[tab];
            if (list is null) continue;
            for (int i = 0; i < list.GetNumUIItems(); i++)
                if (list.GetItem(i) is UiCatalogSlot slot)
                    slot.Selected = slot.EntryId == _selected[tab];
        }
        _endowmentSlot.Selected = _endowmentItemId != 0u
            && _endowmentSelected[_activeTab];
    }

    private void OnSelectionChanged(SelectionTransition _) => UpdateCastAvailability();

    private void UpdateCastAvailability()
    {
        if (_endowmentSelected[_activeTab] && _endowmentItemId != 0u)
        {
            (bool enabled, string? tooltip) = ComputeEndowmentCastState();
            _cast.Enabled = enabled;
            _cast.TooltipText = tooltip;
            return;
        }

        if (_selected[_activeTab] is uint spellId)
        {
            (bool enabled, string? tooltip) = ComputeSpellCastState(spellId);
            _cast.Enabled = enabled;
            _cast.TooltipText = tooltip;
            return;
        }

        _cast.Enabled = false;
        bool anyFavorites = false;
        for (int tab = 0; tab < 8 && !anyFavorites; tab++)
            anyFavorites = _spellbook.GetFavorites(tab).Count > 0;
        _cast.TooltipText = anyFavorites
            ? "Select a spell to cast"
            : "You have no spells ready to cast";
    }

    private (bool enabled, string? tooltip) ComputeEndowmentCastState()
    {
        ClientObject? endowment = _objects.Get(_endowmentItemId);
        if (endowment is null)
            return (false, null);

        string composedName = ComposeEndowmentName(endowment);
        if (ItemUseability.AllowsSelfTarget(endowment.Useability ?? 0u))
            return (true, $"USE the {composedName}");

        uint? targetId = _selection.SelectedObjectId;
        if (targetId is null or 0u)
            return (false, $"You must select a target for the {composedName}");

        string targetName = _objects.Get(targetId.Value)?.GetAppropriateName() ?? composedName;
        return (true, $"USE the {composedName} on {targetName}");
    }

    private string ComposeEndowmentName(ClientObject endowment)
    {
        string itemName = endowment.GetAppropriateName();
        return _spellbook.TryGetMetadata(_endowmentSpellId, out SpellMetadata spellMetadata)
            ? $"{itemName} ({spellMetadata.Name})"
            : itemName;
    }

    private (bool enabled, string? tooltip) ComputeSpellCastState(uint spellId)
    {
        if (!_spellbook.TryGetMetadata(spellId, out SpellMetadata metadata))
            return (false, null);

        string spellName = metadata.Name;
        SpellCastGate gate = _casting.EvaluateCastGate(spellId);
        switch (gate)
        {
            case SpellCastGate.NoTargetNeeded:
                return (true, $"CAST {spellName}");
            case SpellCastGate.TargetCompatible:
            {
                uint? targetId = _selection.SelectedObjectId;
                string? targetName = targetId is uint id and not 0u
                    ? _objects.Get(id)?.GetAppropriateName()
                    : null;
                return (true, targetName is null
                    ? $"CAST {spellName}"
                    : $"CAST {spellName} on {targetName}");
            }
            case SpellCastGate.TargetIncompatible:
                return (false, $"You must select an appropriate target for {spellName}");
            case SpellCastGate.NoTargetSelected:
                return (false, $"You must select a target for {spellName}");
            default:
                return (false, null);
        }
    }

    private void ConfigureSpellName()
    {
        if (_spellName is null) return;
        _spellName.OneLine = true;
        _spellName.Centered = true;
        _spellName.Padding = 0;
        _spellName.LinesProvider = () =>
        {
            uint? chosenSpell = _endowmentSelected[_activeTab]
                ? (_endowmentSpellId == 0u ? null : _endowmentSpellId)
                : _selected[_activeTab];
            string name = chosenSpell is uint spellId
                && _spellbook.TryGetMetadata(spellId, out SpellMetadata metadata)
                    ? metadata.Name : string.Empty;
            return [new UiText.Line(name, _spellName.DefaultColor)];
        };
    }

    private void UpdateEndowment()
    {
        ClientObject? endowment = _objects.GetEquippedBy(_playerGuid())
            .FirstOrDefault(item =>
                (item.CurrentlyEquippedLocation & EquipMask.Held) != 0
                && (item.Type & ItemType.Caster) != 0
                && item.SpellId.GetValueOrDefault() != 0u);

        _endowmentItemId = endowment?.ObjectId ?? 0u;
        _endowmentSpellId = endowment?.SpellId ?? 0u;
        _endowmentHost.Visible = endowment is not null;
        _endowmentSlot.EntryId = _endowmentItemId;
        _endowmentSlot.CatalogIconTexture = _endowmentSpellId == 0u
            ? 0u : _resolveSpellIcon(_endowmentSpellId);
        _endowmentSlot.CatalogOverlayTexture = endowment is null
            ? 0u : _resolveItemDragIcon(endowment);
        string spellName = _spellbook.TryGetMetadata(_endowmentSpellId, out SpellMetadata metadata)
            ? metadata.Name : $"Spell {_endowmentSpellId}";
        _endowmentSlot.Label = endowment is null
            ? string.Empty : $"{endowment.GetAppropriateName()} ({spellName})";

        for (int tab = 0; tab < 8; tab++)
        {
            if (_endowmentItemId != 0u && _selected[tab] is null)
                _endowmentSelected[tab] = true;
            else if (_endowmentItemId == 0u && _endowmentSelected[tab])
                _endowmentSelected[tab] = false;
        }
    }

    private static IEnumerable<UiElement> Descendants(UiElement root)
    {
        foreach (UiElement child in root.Children)
        {
            yield return child;
            foreach (UiElement nested in Descendants(child)) yield return nested;
        }
    }

    public void OnShown() => SyncSelection();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _spellbook.SpellbookChanged -= OnSpellbookChanged;
        _selection.Changed -= OnSelectionChanged;
        _objects.ObjectAdded -= OnObjectChanged;
        _objects.ObjectUpdated -= OnObjectChanged;
        _objects.ObjectRemoved -= OnObjectChanged;
        _objects.Cleared -= OnObjectsCleared;
        foreach (UiElement tab in _tabs) SetClick(tab, null);
        foreach (UiItemList? list in _lists)
        {
            if (list is null) continue;
            list.PrimaryCatalogEntryPressed = null;
            list.ExamineCatalogEntryRequested = null;
        }
        _cast.OnClick = null;
    }

    private static void SetClick(UiElement element, Action? action)
    {
        element.ClickThrough = action is null;
        switch (element)
        {
            case UiButton button: button.OnClick = action; break;
            case UiText text: text.OnClick = action; break;
            case UiDatElement dat: dat.OnClick = action; break;
        }
    }

    private static void SetSelected(UiElement element, bool selected)
    {
        if (element is IUiDatStateful stateful
            && stateful.TrySetRetailState(selected ? RetailUiStateIds.Open : RetailUiStateIds.Closed))
            return;
        if (element is UiButton button)
            button.Selected = selected;
        else if (element is UiText text)
            text.DefaultColor = selected
                ? new System.Numerics.Vector4(1f, 1f, 1f, 1f)
                : new System.Numerics.Vector4(0.65f, 0.65f, 0.65f, 1f);
    }
}

public sealed record SpellFavoriteDragPayload(int SourceTab, int SourcePosition, uint SpellId);

public sealed record SpellbookShortcutDragPayload(uint SpellId);

internal static class FavoriteListExtensions
{
    public static int IndexOf(this IReadOnlyList<uint> values, uint value)
    {
        for (int i = 0; i < values.Count; i++) if (values[i] == value) return i;
        return -1;
    }
}
