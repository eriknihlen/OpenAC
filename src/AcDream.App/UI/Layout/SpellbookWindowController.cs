using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AcDream.App.Spells;
using AcDream.Core.Items;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using AcDream.Content;

namespace AcDream.App.UI.Layout;

public enum SpellbookWindowPage { Spells, Components }

public sealed class SpellbookWindowController : IRetainedPanelController
{
    public const uint LayoutId = 0x21000034u;
    public const uint RootId = 0x100002A8u;
    public const uint SpellTabId = 0x100002A9u;
    public const uint ComponentTabId = 0x100002AAu;
    public const uint CloseId = 0x100002ABu;
    public const uint SpellPageId = 0x100002ACu;
    public const uint ComponentPageId = 0x100002ADu;
    public const uint SpellListId = 0x10000295u;
    public const uint SpellScrollbarId = 0x10000296u;
    public const uint DeleteButtonId = 0x100002A5u;
    public const uint ComponentListId = 0x10000464u;
    public const uint ComponentScrollbarId = 0x10000465u;

    private static readonly (uint Id, uint Mask)[] FilterButtons =
    [
        (0x10000298u, 0x0001u), (0x10000299u, 0x0002u),
        (0x1000029Au, 0x0004u), (0x1000029Bu, 0x0008u),
        (0x100005C0u, 0x2000u),
        (0x1000029Cu, 0x0010u), (0x1000029Du, 0x0020u),
        (0x1000029Eu, 0x0040u), (0x1000029Fu, 0x0080u),
        (0x100002A0u, 0x0100u), (0x100002A1u, 0x0200u),
        (0x100002A2u, 0x0400u), (0x1000054Eu, 0x0800u),
    ];

    private readonly Spellbook _spellbook;
    private readonly ClientObjectTable _objects;
    private readonly Func<uint> _playerGuid;
    private readonly IReadOnlyDictionary<uint, SpellComponentDescriptor> _components;
    private readonly SelectionState _selection;
    private readonly Func<uint, uint> _resolveSpellIcon;
    private readonly Func<uint, uint> _resolveComponentIcon;
    private readonly Func<uint, int> _spellLevel;
    private readonly Action<uint> _selectObject;
    private readonly Action<uint> _addFavorite;
    private readonly Action<uint> _sendFilter;
    private readonly Action<uint> _removeSpell;
    private readonly Action<uint>? _examineSpell;
    private readonly Action<string, Action<bool>> _showConfirmation;
    private readonly Action<uint, uint> _setDesiredComponent;
    private readonly Action _close;
    private readonly UiElement _spellPage;
    private readonly UiElement _componentPage;
    private readonly UiElement _spellTab;
    private readonly UiElement _componentTab;
    private readonly UiButton _closeButton;
    private readonly UiButton _deleteButton;
    private readonly UiItemList _spellList;
    private readonly UiItemList _componentList;
    private readonly ComponentBookTemplateFactory _componentTemplates;
    private readonly List<(UiButton Button, uint Mask)> _filters = new();
    private readonly SpellbookRowStyle _rowStyle;
    private readonly UiDatFont? _rowFont;
    private uint? _selectedSpell;
    private uint? _selectedComponent;
    private bool _spellsDirty;
    private bool _componentsDirty;
    private bool _componentEditActive;
    private bool _disposed;

    private SpellbookWindowController(
        ImportedLayout layout,
        Spellbook spellbook,
        ClientObjectTable objects,
        Func<uint> playerGuid,
        IReadOnlyDictionary<uint, SpellComponentDescriptor> components,
        SelectionState selection,
        Func<uint, uint> resolveSpellIcon,
        Func<uint, uint> resolveComponentIcon,
        Func<uint, int> spellLevel,
        Action<uint> selectObject,
        Action<uint> addFavorite,
        Action<uint> sendFilter,
        Action<uint> removeSpell,
        Action<uint>? examineSpell,
        Action<string, Action<bool>> showConfirmation,
        Action<uint, uint> setDesiredComponent,
        Action close,
        UiElement spellPage,
        UiElement componentPage,
        UiElement spellTab,
        UiElement componentTab,
        UiButton closeButton,
        UiButton deleteButton,
        UiItemList spellList,
        UiItemList componentList,
        ComponentBookTemplateFactory componentTemplates,
        SpellbookRowStyle rowStyle,
        UiDatFont? rowFont)
    {
        _spellbook = spellbook;
        _objects = objects;
        _playerGuid = playerGuid;
        _components = components;
        _selection = selection;
        _resolveSpellIcon = resolveSpellIcon;
        _resolveComponentIcon = resolveComponentIcon;
        _spellLevel = spellLevel;
        _selectObject = selectObject;
        _addFavorite = addFavorite;
        _sendFilter = sendFilter;
        _removeSpell = removeSpell;
        _examineSpell = examineSpell;
        _showConfirmation = showConfirmation;
        _setDesiredComponent = setDesiredComponent;
        _close = close;
        _spellPage = spellPage;
        _componentPage = componentPage;
        _spellTab = spellTab;
        _componentTab = componentTab;
        _closeButton = closeButton;
        _deleteButton = deleteButton;
        _spellList = spellList;
        _componentList = componentList;
        _componentTemplates = componentTemplates;
        _rowStyle = rowStyle;
        _rowFont = rowFont;

        RetailTabBinding.SetClick(spellTab, () => ShowPage(SpellbookWindowPage.Spells));
        RetailTabBinding.SetClick(componentTab, () => ShowPage(SpellbookWindowPage.Components));
        closeButton.OnClick = close;
        deleteButton.OnClick = RequestDeleteSelected;
        foreach ((uint id, uint mask) in FilterButtons)
        {
            if (layout.FindElement(id) is not UiButton button) continue;
            uint filterMask = mask;
            button.OnClick = () => ToggleFilter(filterMask);
            _filters.Add((button, mask));
        }

        ConfigureSpellList(layout);
        ConfigureComponentList(layout);
        _spellbook.SpellbookChanged += OnSpellbookChanged;
        _spellbook.DesiredComponentsChanged += OnDesiredComponentsChanged;
        _objects.ObjectAdded += OnObjectChanged;
        _objects.ObjectUpdated += OnObjectChanged;
        _objects.ObjectRemoved += OnObjectRemoved;
        _objects.ObjectMoved += OnObjectMoved;
        _objects.ContainerContentsReplaced += OnContainerContentsReplaced;
        _selection.Changed += OnSelectionChanged;
        ShowPage(SpellbookWindowPage.Spells);
        RebuildAll();
    }

    public SpellbookWindowPage CurrentPage { get; private set; }

    public static SpellbookWindowController? Bind(
        ImportedLayout layout,
        Spellbook spellbook,
        ClientObjectTable objects,
        Func<uint> playerGuid,
        IReadOnlyDictionary<uint, SpellComponentDescriptor> components,
        SelectionState selection,
        Func<uint, uint> resolveSpellIcon,
        Func<uint, uint> resolveComponentIcon,
        Func<uint, int> spellLevel,
        Action<uint> selectObject,
        Action<uint> addFavorite,
        Action<uint> sendFilter,
        Action<uint> removeSpell,
        Action<uint>? examineSpell,
        Action<string, Action<bool>> showConfirmation,
        Action<uint, uint> setDesiredComponent,
        Action close,
        ComponentBookTemplateFactory componentTemplates,
        SpellbookRowStyle rowStyle,
        UiDatFont? rowFont)
    {
        if (layout.FindElement(SpellPageId) is not { } spellPage
            || layout.FindElement(ComponentPageId) is not { } componentPage
            || layout.FindElement(SpellTabId) is not { } spellTab
            || layout.FindElement(ComponentTabId) is not { } componentTab
            || layout.FindElement(CloseId) is not UiButton closeButton
            || layout.FindElement(DeleteButtonId) is not UiButton deleteButton
            || layout.FindElement(SpellListId) is not UiItemList spellList)
            return null;

        UiElement? componentHost = layout.FindElement(ComponentListId);
        if (componentHost is null) return null;
        UiItemList componentList;
        if (componentHost is UiItemList itemList)
            componentList = itemList;
        else
        {
            componentList = new UiItemList(spellList.SpriteResolve)
            {
                Width = componentHost.Width,
                Height = componentHost.Height,
                Anchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Right | AnchorEdges.Bottom,
            };
            componentHost.AddChild(componentList);
            componentList.CaptureCurrentAnchorBaseline();
        }

        return new SpellbookWindowController(
            layout, spellbook, objects, playerGuid, components, selection,
            resolveSpellIcon, resolveComponentIcon,
            spellLevel, selectObject,
            addFavorite, sendFilter, removeSpell, examineSpell, showConfirmation,
            setDesiredComponent, close,
            spellPage, componentPage, spellTab, componentTab, closeButton,
            deleteButton, spellList, componentList, componentTemplates,
            rowStyle, rowFont);
    }

    public void ShowPage(SpellbookWindowPage page)
    {
        CurrentPage = page;
        _spellPage.Visible = page == SpellbookWindowPage.Spells;
        _componentPage.Visible = page == SpellbookWindowPage.Components;
        RetailTabBinding.SetOpen(_spellTab, page == SpellbookWindowPage.Spells);
        RetailTabBinding.SetOpen(_componentTab, page == SpellbookWindowPage.Components);
    }

    private void ConfigureSpellList(ImportedLayout layout)
    {
        _spellList.ExamineCatalogEntryRequested = _examineSpell;
        _spellList.Columns = 1;
        _spellList.CellWidth = Math.Max(1f, _spellList.Width);
        _spellList.CellHeight = _rowStyle.Height;
        if (layout.FindElement(SpellScrollbarId) is UiScrollbar scrollbar)
            scrollbar.Model = _spellList.Scroll;
    }

    private void ConfigureComponentList(ImportedLayout layout)
    {
        _componentList.Columns = 1;
        _componentList.CellWidth = Math.Max(1f, _componentList.Width);
        _componentList.CellHeight = 32f;
        if (layout.FindElement(ComponentScrollbarId) is UiScrollbar scrollbar)
            scrollbar.Model = _componentList.Scroll;
    }

    private void ToggleFilter(uint mask)
    {
        uint filters = _spellbook.SpellbookFilters ^ mask;
        _sendFilter(filters);
        _spellList.Scroll.SetScrollY(0);
    }

    private void RebuildAll()
    {
        RebuildSpells();
        RebuildComponents();
        _spellsDirty = false;
        _componentsDirty = false;
    }

    private void RebuildSpells()
    {
        uint effective = _spellbook.SpellbookFilters;
        foreach ((UiButton button, uint mask) in _filters)
            button.Selected = (effective & mask) != 0;

        SpellMetadata[] spells = _spellbook.LearnedSpells
            .Select(id => _spellbook.TryGetMetadata(id, out SpellMetadata metadata) ? metadata : null)
            .Where(metadata => metadata is not null && IsVisible(metadata, effective))
            .OrderBy(metadata => metadata!.SortKey)
            .Cast<SpellMetadata>()
            .ToArray();

        using (_spellList.DeferLayout())
        {
            _spellList.Flush();
            foreach (SpellMetadata metadata in spells)
            {
                uint spellId = metadata.SpellId;
                var slot = new UiCatalogSlot
                {
                    EntryId = spellId,
                    CatalogIconTexture = _resolveSpellIcon(spellId),
                    Label = metadata.Name,
                    ShowLabel = true,
                    BackgroundSprite = _rowStyle.BackgroundSprite,
                    SelectedSprite = _rowStyle.SelectedSprite,
                    SelectionBehindContent = true,
                    LabelFont = _rowFont,
                    LabelColor = _rowStyle.LabelColor,
                    IconLeft = _rowStyle.IconLeft,
                    IconTop = _rowStyle.IconTop,
                    IconWidth = _rowStyle.IconWidth,
                    IconHeight = _rowStyle.IconHeight,
                    LabelLeft = _rowStyle.LabelLeft,
                    LabelWidth = _rowStyle.LabelWidth,
                    SpriteResolve = _spellList.SpriteResolve,
                    CatalogDragPayload = new SpellbookShortcutDragPayload(spellId),
                    DragBegan = _ => SelectSpell(spellId),
                };
                slot.Clicked = () => SelectSpell(spellId);
                slot.DoubleClicked = () => { SelectSpell(spellId); _addFavorite(spellId); };
                _spellList.AddItem(slot);
            }
        }
        SyncSpellSelection();
    }

    private void RebuildComponents()
    {
        uint player = _playerGuid();
        var packs = _objects.Objects
            .Where(item => (item.IsComponentPack || _components.ContainsKey(item.WeenieClassId))
                && IsOwnedByPlayer(item, player))
            .GroupBy(item => item.WeenieClassId)
            .Select(group => new
            {
                ComponentId = group.Key,
                First = group.First(),
                Quantity = group.Sum(item => Math.Max(1, item.StackSize)),
            })
            .OrderBy(group => _components.TryGetValue(group.ComponentId, out var descriptor) ? descriptor.Category : uint.MaxValue)
            .ThenBy(group => group.First.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        float width = Math.Max(96f, _componentList.Width);
        _componentList.CellWidth = width;
        using (_componentList.DeferLayout())
        {
            _componentList.Flush();
            uint? category = null;
            foreach (var packGroup in packs)
            {
                ClientObject pack = packGroup.First;
                uint componentId = packGroup.ComponentId;
                _components.TryGetValue(componentId, out SpellComponentDescriptor? descriptor);
                uint nextCategory = descriptor?.Category ?? 8u;
                if (category != nextCategory)
                {
                    category = nextCategory;
                    UiTemplateListSlot categoryRow =
                        _componentTemplates.CreateCategoryRow(nextCategory);
                    categoryRow.Clicked = () =>
                    {
                        _selectedComponent = null;
                        SyncComponentSelection();
                        _selectObject(0u);
                    };
                    _componentList.AddItem(categoryRow);
                }
                uint desired = _spellbook.DesiredComponents.TryGetValue(componentId, out uint amount) ? amount : 0u;
                string componentName = string.IsNullOrWhiteSpace(pack.Name)
                    ? descriptor?.Name ?? $"Component {componentId}"
                    : pack.Name;
                ComponentBookTemplateFactory.ComponentRow row =
                    _componentTemplates.CreateComponentRow(
                        componentId,
                        _resolveComponentIcon(
                            pack.IconId != 0 ? pack.IconId : descriptor?.IconId ?? 0u),
                        componentName,
                        packGroup.Quantity,
                        desired);
                UiField desiredField = row.DesiredField;
                desiredField.OnFocusGained = () => _componentEditActive = true;
                uint committed = desired;
                void Commit(string value)
                {
                    if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed)
                        || parsed > 5000u)
                    {
                        desiredField.SetText(committed.ToString(CultureInfo.InvariantCulture));
                        return;
                    }
                    if (parsed == committed) return;
                    committed = parsed;
                    _setDesiredComponent(componentId, parsed);
                }
                desiredField.OnSubmit = Commit;
                desiredField.OnFocusLost = value =>
                {
                    Commit(value);
                    _componentEditActive = false;
                };
                row.Slot.Clicked = () =>
                {
                    _selectedComponent = componentId;
                    SyncComponentSelection();
                    _selectObject(pack.ObjectId);
                };
                _componentList.AddItem(row.Slot);
            }
        }
        SyncComponentSelectionFromWorld();
    }

    private bool IsOwnedByPlayer(ClientObject item, uint playerGuid)
    {
        if (playerGuid == 0) return item.ContainerId != 0 || item.WielderId != 0;
        ClientObject current = item;
        for (int depth = 0; depth < 16; depth++)
        {
            if (current.WielderId == playerGuid || current.ContainerId == playerGuid) return true;
            if (current.ContainerId == 0 || _objects.Get(current.ContainerId) is not { } parent) return false;
            current = parent;
        }
        return false;
    }

    private bool IsVisible(SpellMetadata metadata, uint filters)
    {
        uint school = metadata.SchoolId switch
        {
            MagicSchool.CreatureEnchantment => 0x0001u,
            MagicSchool.ItemEnchantment => 0x0002u,
            MagicSchool.LifeMagic => 0x0004u,
            MagicSchool.WarMagic => 0x0008u,
            MagicSchool.VoidMagic => 0x2000u,
            _ => 0u,
        };
        int spellLevel = _spellLevel(metadata.SpellId);
        if (spellLevel is < 1 or > 8) return false;
        uint level = 0x10u << (spellLevel - 1);
        return school != 0 && (filters & school) != 0 && (filters & level) != 0;
    }

    private void SelectSpell(uint spellId)
    {
        _selectedSpell = spellId;
        SyncSpellSelection();
        for (int i = 0; i < _spellList.GetNumUIItems(); i++)
            if (_spellList.GetItem(i) is UiCatalogSlot slot && slot.EntryId == spellId)
            {
                _spellList.ScrollItemIntoView(i);
                break;
            }
    }

    private void RequestDeleteSelected()
    {
        if (_selectedSpell is not uint spellId
            || !_spellbook.TryGetMetadata(spellId, out SpellMetadata metadata))
            return;

        string message = $"Are you sure you want to remove {metadata.Name} from your spellbook? "
            + "You will no longer be able to cast this spell unless you learn it again!";
        _showConfirmation(message, accepted =>
        {
            if (accepted) _removeSpell(spellId);
        });
    }

    private void SyncSpellSelection()
    {
        for (int i = 0; i < _spellList.GetNumUIItems(); i++)
            if (_spellList.GetItem(i) is UiCatalogSlot slot)
                slot.Selected = slot.EntryId == _selectedSpell;
    }

    private void SyncComponentSelection()
    {
        for (int i = 0; i < _componentList.GetNumUIItems(); i++)
            if (_componentList.GetItem(i) is UiTemplateListSlot slot)
                slot.SetSelected(slot.EntryId == _selectedComponent);
    }

    private void SyncComponentSelectionFromWorld()
    {
        _selectedComponent = _selection.SelectedObjectId is uint selected
            && _objects.Get(selected) is { } item
            && IsComponent(item)
                ? item.WeenieClassId
                : null;
        SyncComponentSelection();
    }

    public void Tick()
    {
        if (_spellsDirty)
        {
            _spellsDirty = false;
            RebuildSpells();
        }
        if (_componentsDirty && !_componentEditActive)
        {
            _componentsDirty = false;
            RebuildComponents();
        }
    }

    private void OnSpellbookChanged() => _spellsDirty = true;
    private void OnDesiredComponentsChanged() => _componentsDirty = true;
    private void OnObjectChanged(ClientObject item)
    {
        if (IsComponent(item)) _componentsDirty = true;
    }
    private void OnObjectRemoved(ClientObject item)
    {
        if (IsComponent(item)) _componentsDirty = true;
    }
    private void OnObjectMoved(ClientObjectMove _) => _componentsDirty = true;
    private void OnContainerContentsReplaced(uint _) => _componentsDirty = true;
    private void OnSelectionChanged(SelectionTransition _) => SyncComponentSelectionFromWorld();
    private bool IsComponent(ClientObject item)
        => item.IsComponentPack || _components.ContainsKey(item.WeenieClassId);
    public void OnShown() => RebuildAll();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _spellbook.SpellbookChanged -= OnSpellbookChanged;
        _spellbook.DesiredComponentsChanged -= OnDesiredComponentsChanged;
        _objects.ObjectAdded -= OnObjectChanged;
        _objects.ObjectUpdated -= OnObjectChanged;
        _objects.ObjectRemoved -= OnObjectRemoved;
        _objects.ObjectMoved -= OnObjectMoved;
        _objects.ContainerContentsReplaced -= OnContainerContentsReplaced;
        _selection.Changed -= OnSelectionChanged;
        RetailTabBinding.SetClick(_spellTab, null);
        RetailTabBinding.SetClick(_componentTab, null);
        _closeButton.OnClick = null;
        _deleteButton.OnClick = null;
        foreach ((UiButton button, _) in _filters) button.OnClick = null;
    }

}
