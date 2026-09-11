using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AcDream.App.Rendering;
using AcDream.Core.Items;
using AcDream.Core.Properties;
using AcDream.Core.Selection;

namespace AcDream.App.UI.Layout;

public sealed class VendorUiController : IRetainedPanelController, IItemListDragHandler
{
    public const uint LayoutId = 0x21000012u;
    public const uint RootId = 0x100000B7u;
    public const uint CloseId = 0x100000D6u;
    public const uint PanelGroupId = 0x100000B8u;

    public const uint ItemsTabId = 0x100000B9u;
    public const uint BuyingTabId = 0x100000BAu;
    public const uint SellingTabId = 0x100000BBu;

    public const uint ItemsPageId = 0x100000BCu;
    public const uint ItemListId = 0x100000BDu;
    public const uint ItemScrollbarId = 0x100000BEu;
    public const uint TypeFilterMenuId = 0x100000BFu;
    public const uint ItemNameTextId = 0x100000C0u;
    public const uint ItemCostTextId = 0x100000C1u;
    public const uint BuyButtonId = 0x100000C2u;
    public const uint AddButtonId = 0x100000C3u;

    public const uint BuyingPageId = 0x100000C4u;
    public const uint SellingPageId = 0x100000CDu;

    public const uint BuyingListId = 0x100000C5u;
    public const uint BuyingScrollbarId = 0x100000C6u;
    public const uint SellingListId = 0x100000CEu;
    public const uint SellingScrollbarId = 0x100000CFu;

    public const uint BuyingListTextId = 0x100000C7u;
    public const uint BuyingPurseTextId = 0x100000C8u;
    public const uint SellingListTextId = 0x100000D0u;
    public const uint SellingPurseTextId = 0x100000D1u;

    public const uint BuyItemButtonId = 0x100000C9u;
    public const uint BuyAllButtonId = 0x100000CAu;
    public const uint BuyClearItemButtonId = 0x100000CBu;
    public const uint BuyClearListButtonId = 0x100000CCu;

    public const uint SellItemButtonId = 0x100000D2u;
    public const uint SellAllButtonId = 0x100000D3u;
    public const uint SellClearItemButtonId = 0x100000D4u;
    public const uint SellClearListButtonId = 0x100000D5u;

    private const int TypeMenuRowsPerColumn = 6;
    private const float TypeMenuRowHeight = 18f;
    private const float TypeMenuColumnWidth = 100f;
    private const uint TypeMenuItemNormalSprite = 0x060012B3u;
    private const uint TypeMenuItemHighlightSprite = 0x060012B4u;
    private const uint TypeMenuNormalSprite = 0x060012B3u;
    private const uint TypeMenuPressedSprite = 0x060012B4u;
    private const uint TypeMenuArrowCapClosedSprite = 0x060012B1u;
    private const uint TypeMenuArrowCapOpenSprite = 0x060012B2u;

    private const float TypeMenuScrollbarWidth = 16f;
    private const float TypeMenuScrollButtonExtent = 16f;
    private const uint TypeMenuScrollTrackSprite = 0x06004C5Fu;
    private const uint TypeMenuScrollThumbTopSprite = 0x06004C60u;
    private const uint TypeMenuScrollThumbSprite = 0x06004C63u;
    private const uint TypeMenuScrollThumbBottomSprite = 0x06004C66u;
    private const uint TypeMenuScrollUpSprite = RetailScrollbarChrome.UpNormal;
    private const uint TypeMenuScrollDownSprite = RetailScrollbarChrome.DownNormal;

    private static readonly (string Label, ItemType Mask)[] CategoryFilters =
    [
        ("Armor", ItemType.Armor),                                                       // 0x2
        ("Books, Paper", ItemType.Writable),                                              // 0x2000
        ("Clothing", ItemType.Clothing),                                                  // 0x4
        ("Containers", ItemType.Container),                                               // 0x200
        ("Food", ItemType.Food),                                                          // 0x20
        ("Gems", ItemType.Gem),                                                           // 0x800
        ("Jewelry", ItemType.Jewelry),                                                    // 0x8
        ("Keys, Tools", ItemType.TinkeringTool | ItemType.Key),
        ("Miscellaneous", ItemType.Useless | ItemType.Misc | ItemType.Creature),          // 0x490
        ("Services", ItemType.Service),
        ("Spell Components", ItemType.SpellComponents),                                   // 0x1000
        ("Trade Notes", ItemType.PromissoryNote),
        ("Weapons", ItemType.Weapon),                                                     // 0x101
        ("Mana Stones", ItemType.ManaStone),
        ("Magic Items", ItemType.Caster),                                                 // 0x8000
        ("Alchemical Items", ItemType.CraftAlchemyIntermediate | ItemType.CraftAlchemyBase),
        ("Cooking Items", ItemType.CraftCookingBase),
        ("Fletching Items", ItemType.CraftFletchingIntermediate | ItemType.CraftFletchingBase),
    ];

    private readonly VendorState _vendor;
    private readonly RetailWindowHandle _window;
    private readonly Func<ItemType, uint, uint, uint, uint, uint> _resolveIcon;
    private readonly ClientObjectTable _objects;
    private readonly Func<uint> _playerGuid;
    private readonly ItemInteractionController _itemInteraction;
    private readonly SelectionState _selection;
    private readonly StackSplitQuantityState _splitQuantity;
    private readonly UiElement _itemsPage;
    private readonly UiElement _buyingPage;
    private readonly UiElement _sellingPage;
    private readonly UiElement _itemsTab;
    private readonly UiElement _buyingTab;
    private readonly UiElement _sellingTab;
    private readonly UiItemList _itemList;
    private readonly UiItemList? _buyingList;
    private readonly UiItemList? _sellingList;
    private readonly UiMenu _typeMenu;
    private readonly UiText _itemNameText;
    private readonly UiText _itemCostText;
    private readonly UiText? _buyListText;
    private readonly UiText? _buyPurseText;
    private readonly UiText? _sellListText;
    private readonly UiText? _sellPurseText;
    private readonly UiButton? _close;
    private readonly UiButton? _buyButton;
    private readonly UiButton? _addButton;
    private readonly UiButton? _buyItemButton;
    private readonly UiButton? _buyAllButton;
    private readonly UiButton? _buyClearItemButton;
    private readonly UiButton? _buyClearListButton;
    private readonly UiButton? _sellItemButton;
    private readonly UiButton? _sellAllButton;
    private readonly UiButton? _sellClearItemButton;
    private readonly UiButton? _sellClearListButton;
    private readonly VendorStagingList _buyStaging = new();
    private readonly VendorStagingList _sellStaging = new();
    private readonly RetailDialogFactory? _dialogs;
    private readonly Action<string>? _systemMessage;

    private readonly List<(string Label, ItemType Mask)> _presentCategories = new();
    private int _selectedCategoryIndex = -1;
    private bool _buyEnabledBySelection;
    private uint _closeConfirmContext;
    private int _lastAlternateCurrencyPurchase;
    private bool _alternateCurrencyInventoryObserved;
    private PendingVendorSplit? _pendingVendorSplit;
    private readonly DragOverGlobalTimeSink _dragOverSink;
    private bool _disposed;

    private readonly record struct PendingVendorSplit(
        uint SourceGuid,
        uint WeenieClassId,
        int Quantity);

    private VendorUiController(
        VendorState vendor,
        RetailWindowHandle window,
        Func<ItemType, uint, uint, uint, uint, uint> resolveIcon,
        ClientObjectTable objects,
        Func<uint> playerGuid,
        ItemInteractionController itemInteraction,
        SelectionState selection,
        StackSplitQuantityState splitQuantity,
        UiElement itemsPage,
        UiElement buyingPage,
        UiElement sellingPage,
        UiElement itemsTab,
        UiElement buyingTab,
        UiElement sellingTab,
        UiItemList itemList,
        UiScrollbar? itemScrollbar,
        UiItemList? buyingList,
        UiScrollbar? buyingScrollbar,
        UiItemList? sellingList,
        UiScrollbar? sellingScrollbar,
        UiMenu typeMenu,
        UiText itemNameText,
        UiText itemCostText,
        UiText? buyListText,
        UiText? buyPurseText,
        UiText? sellListText,
        UiText? sellPurseText,
        UiButton? close,
        UiButton? buyButton,
        UiButton? addButton,
        UiButton? buyItemButton,
        UiButton? buyAllButton,
        UiButton? buyClearItemButton,
        UiButton? buyClearListButton,
        UiButton? sellItemButton,
        UiButton? sellAllButton,
        UiButton? sellClearItemButton,
        UiButton? sellClearListButton,
        RetailDialogFactory? dialogs,
        Action<string>? systemMessage,
        UiDatFont? datFont,
        BitmapFont? debugFont,
        Func<uint, (uint tex, int w, int h)> resolveSprite,
        uint emptySlotSprite,
        uint buyingEmptySlotSprite,
        uint sellingEmptySlotSprite)
    {
        _vendor = vendor;
        _window = window;
        _resolveIcon = resolveIcon;
        _objects = objects;
        _playerGuid = playerGuid;
        _itemInteraction = itemInteraction;
        _selection = selection;
        _splitQuantity = splitQuantity;
        _itemsPage = itemsPage;
        _buyingPage = buyingPage;
        _sellingPage = sellingPage;
        _itemsTab = itemsTab;
        _buyingTab = buyingTab;
        _sellingTab = sellingTab;
        _itemList = itemList;
        _buyingList = buyingList;
        _sellingList = sellingList;
        _typeMenu = typeMenu;
        _itemNameText = itemNameText;
        _itemCostText = itemCostText;
        _buyListText = buyListText;
        _buyPurseText = buyPurseText;
        _sellListText = sellListText;
        _sellPurseText = sellPurseText;
        _close = close;
        _buyButton = buyButton;
        _addButton = addButton;
        _buyItemButton = buyItemButton;
        _buyAllButton = buyAllButton;
        _buyClearItemButton = buyClearItemButton;
        _buyClearListButton = buyClearListButton;
        _sellItemButton = sellItemButton;
        _sellAllButton = sellAllButton;
        _sellClearItemButton = sellClearItemButton;
        _sellClearListButton = sellClearListButton;
        _dialogs = dialogs;
        _systemMessage = systemMessage;

        _itemList.Columns = 1;
        _itemList.SingleRow = true;
        _itemList.HorizontalScroll = true;
        _itemList.CellWidth = 32f;
        _itemList.CellHeight = 32f;
        _itemList.FillVisibleEmptySlots = true;
        if (emptySlotSprite != 0u)
            _itemList.CellEmptySprite = emptySlotSprite;
        _itemList.EmptySlotFactory = () => new UiItemSlot
        {
            SpriteResolve = _itemList.SpriteResolve,
            AllowDragSource = false,
        };
        _itemList.ExamineItemRequested = ExamineItem;
        _itemList.PrimaryItemPressed = PressVendorItem;
        if (itemScrollbar is not null)
        {
            itemScrollbar.Model = _itemList.Scroll;
            itemScrollbar.Horizontal = true;
        }

        ConfigureEmptyStrip(_buyingList, buyingEmptySlotSprite);
        if (buyingScrollbar is not null && _buyingList is not null)
        {
            buyingScrollbar.Model = _buyingList.Scroll;
            buyingScrollbar.Horizontal = true;
        }
        ConfigureEmptyStrip(_sellingList, sellingEmptySlotSprite);
        if (sellingScrollbar is not null && _sellingList is not null)
        {
            sellingScrollbar.Model = _sellingList.Scroll;
            sellingScrollbar.Horizontal = true;
        }
        _sellingList?.RegisterDragHandler(this);
        if (_buyingList is not null)
        {
            _buyingList.PrimaryItemPressed = PressVendorItem;
            _buyingList.ExamineItemRequested = ExamineItem;
        }
        if (_sellingList is not null)
        {
            _sellingList.PrimaryItemPressed = PressVendorItem;
            _sellingList.ExamineItemRequested = ExamineItem;
        }

        _dragOverSink = new DragOverGlobalTimeSink(PollDragOver);
        _window.ContentRoot.AddChild(_dragOverSink);

        _typeMenu.SpriteResolve = resolveSprite;
        _typeMenu.DatFont = datFont;
        _typeMenu.Font = debugFont;
        _typeMenu.NormalSprite = TypeMenuNormalSprite;
        _typeMenu.PressedSprite = TypeMenuPressedSprite;
        _typeMenu.ItemNormalSprite = TypeMenuItemNormalSprite;
        _typeMenu.ItemHighlightSprite = TypeMenuItemHighlightSprite;
        _typeMenu.RowsPerColumn = TypeMenuRowsPerColumn;
        _typeMenu.RowHeight = TypeMenuRowHeight;
        _typeMenu.ColumnWidth = TypeMenuColumnWidth;
        _typeMenu.Scrollable = true;
        _typeMenu.PopupSizeToContent = true;
        _typeMenu.PopupScrollbarHideWhenDisabled = true;
        _typeMenu.ScrollbarWidth = TypeMenuScrollbarWidth;
        _typeMenu.ScrollButtonExtent = TypeMenuScrollButtonExtent;
        _typeMenu.ScrollTrackSprite = TypeMenuScrollTrackSprite;
        _typeMenu.ScrollThumbTopSprite = TypeMenuScrollThumbTopSprite;
        _typeMenu.ScrollThumbSprite = TypeMenuScrollThumbSprite;
        _typeMenu.ScrollThumbBottomSprite = TypeMenuScrollThumbBottomSprite;
        _typeMenu.ScrollUpSprite = TypeMenuScrollUpSprite;
        _typeMenu.ScrollDownSprite = TypeMenuScrollDownSprite;
        _typeMenu.ArrowCapClosedSprite = TypeMenuArrowCapClosedSprite;
        _typeMenu.ArrowCapOpenSprite = TypeMenuArrowCapOpenSprite;
        _typeMenu.OpenUpward = false;
        _typeMenu.TextIndent = 0f;
        _typeMenu.ButtonTextIndent = 0f;
        _typeMenu.OnSelect = payload =>
        {
            if (payload is uint mask) SelectCategory(mask);
        };
        _typeMenu.ButtonLabelProvider = () =>
            _selectedCategoryIndex >= 0 && _selectedCategoryIndex < _presentCategories.Count
                ? _presentCategories[_selectedCategoryIndex].Label
                : string.Empty;

        RetailTabBinding.SetClick(_itemsTab, () => ShowTab(VendorPanelTab.Items));
        RetailTabBinding.SetClick(_buyingTab, () => ShowTab(VendorPanelTab.Buying));
        RetailTabBinding.SetClick(_sellingTab, () => ShowTab(VendorPanelTab.Selling));
        if (_close is not null)
            _close.OnClick = CloseButtonPressed;
        if (_buyButton is not null)
            _buyButton.OnClick = BuySelectedItem;
        if (_addButton is not null)
            _addButton.OnClick = AddSelectedToBuyList;
        if (_buyItemButton is not null)
            _buyItemButton.OnClick = BuyItemButtonPressed;
        if (_buyAllButton is not null)
            _buyAllButton.OnClick = BuyAllButtonPressed;
        if (_buyClearItemButton is not null)
            _buyClearItemButton.OnClick = BuyClearItemButtonPressed;
        if (_buyClearListButton is not null)
            _buyClearListButton.OnClick = () => _buyStaging.Clear();
        if (_sellItemButton is not null)
            _sellItemButton.OnClick = SellItemButtonPressed;
        if (_sellAllButton is not null)
            _sellAllButton.OnClick = SellAllButtonPressed;
        if (_sellClearItemButton is not null)
            _sellClearItemButton.OnClick = SellClearItemButtonPressed;
        if (_sellClearListButton is not null)
            _sellClearListButton.OnClick = () => _sellStaging.Clear();

        _buyStaging.Changed += RebuildBuyingList;
        _buyStaging.Changed += RefreshItemsTabAvailability;
        _sellStaging.Changed += RebuildSellingList;
        _buyStaging.Changed += UpdateBuyTransactionText;
        _sellStaging.Changed += UpdateSellTransactionText;
        _objects.ObjectAdded += OnObjectAdded;
        _objects.ObjectUpdated += OnObjectMoneyChanged;
        _objects.StackSizeUpdated += OnStackSizeUpdated;
        _objects.ObjectMoved += OnObjectMoved;

        ShowTab(VendorPanelTab.Items);
        ClearContent();

        _vendor.Changed += OnVendorChanged;
        _selection.Changed += OnSelectionTransition;
        _objects.ObjectRemoved += OnObjectRemoved;
        _itemInteraction.RuntimeTransactions.Inventory.RequestFailed += OnInventoryRequestFailed;
        _itemInteraction.StateChanged += OnInteractionStateChanged;
        _splitQuantity.Changed += OnSplitQuantityChanged;
    }

    public static VendorUiController? Bind(
        ImportedLayout layout,
        VendorState vendor,
        RetailWindowHandle window,
        Func<ItemType, uint, uint, uint, uint, uint> resolveIcon,
        ClientObjectTable objects,
        Func<uint> playerGuid,
        ItemInteractionController itemInteraction,
        SelectionState selection,
        StackSplitQuantityState splitQuantity,
        UiDatFont? datFont,
        BitmapFont? debugFont,
        Func<uint, (uint tex, int w, int h)> resolveSprite,
        uint emptySlotSprite = 0u,
        uint buyingEmptySlotSprite = 0u,
        uint sellingEmptySlotSprite = 0u,
        RetailDialogFactory? dialogs = null,
        Action<string>? systemMessage = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(vendor);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(resolveIcon);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(playerGuid);
        ArgumentNullException.ThrowIfNull(itemInteraction);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(splitQuantity);
        ArgumentNullException.ThrowIfNull(resolveSprite);

        if (layout.FindElement(ItemsPageId) is not { } itemsPage
            || layout.FindElement(BuyingPageId) is not { } buyingPage
            || layout.FindElement(SellingPageId) is not { } sellingPage
            || layout.FindElement(ItemsTabId) is not { } itemsTab
            || layout.FindElement(BuyingTabId) is not { } buyingTab
            || layout.FindElement(SellingTabId) is not { } sellingTab
            || layout.FindElement(ItemListId) is not UiItemList itemList
            || layout.FindElement(TypeFilterMenuId) is not UiMenu typeMenu
            || layout.FindElement(ItemNameTextId) is not UiText itemNameText
            || layout.FindElement(ItemCostTextId) is not UiText itemCostText)
        {
            return null;
        }

        UiText? buyListText = layout.FindElement(BuyingListTextId) as UiText;
        UiText? buyPurseText = layout.FindElement(BuyingPurseTextId) as UiText;
        UiText? sellListText = layout.FindElement(SellingListTextId) as UiText;
        UiText? sellPurseText = layout.FindElement(SellingPurseTextId) as UiText;

        UiButton? close = layout.FindElement(CloseId) as UiButton;
        UiScrollbar? itemScrollbar = layout.FindElement(ItemScrollbarId) as UiScrollbar;
        UiButton? buyButton = layout.FindElement(BuyButtonId) as UiButton;
        UiButton? addButton = layout.FindElement(AddButtonId) as UiButton;
        UiItemList? buyingList = layout.FindElement(BuyingListId) as UiItemList;
        UiScrollbar? buyingScrollbar = layout.FindElement(BuyingScrollbarId) as UiScrollbar;
        UiItemList? sellingList = layout.FindElement(SellingListId) as UiItemList;
        UiScrollbar? sellingScrollbar = layout.FindElement(SellingScrollbarId) as UiScrollbar;
        UiButton? buyItemButton = layout.FindElement(BuyItemButtonId) as UiButton;
        UiButton? buyAllButton = layout.FindElement(BuyAllButtonId) as UiButton;
        UiButton? buyClearItemButton = layout.FindElement(BuyClearItemButtonId) as UiButton;
        UiButton? buyClearListButton = layout.FindElement(BuyClearListButtonId) as UiButton;
        UiButton? sellItemButton = layout.FindElement(SellItemButtonId) as UiButton;
        UiButton? sellAllButton = layout.FindElement(SellAllButtonId) as UiButton;
        UiButton? sellClearItemButton = layout.FindElement(SellClearItemButtonId) as UiButton;
        UiButton? sellClearListButton = layout.FindElement(SellClearListButtonId) as UiButton;

        return new VendorUiController(
            vendor,
            window,
            resolveIcon,
            objects,
            playerGuid,
            itemInteraction,
            selection,
            splitQuantity,
            itemsPage,
            buyingPage,
            sellingPage,
            itemsTab,
            buyingTab,
            sellingTab,
            itemList,
            itemScrollbar,
            buyingList,
            buyingScrollbar,
            sellingList,
            sellingScrollbar,
            typeMenu,
            itemNameText,
            itemCostText,
            buyListText,
            buyPurseText,
            sellListText,
            sellPurseText,
            close,
            buyButton,
            addButton,
            buyItemButton,
            buyAllButton,
            buyClearItemButton,
            buyClearListButton,
            sellItemButton,
            sellAllButton,
            sellClearItemButton,
            sellClearListButton,
            dialogs,
            systemMessage,
            datFont,
            debugFont,
            resolveSprite,
            emptySlotSprite,
            buyingEmptySlotSprite,
            sellingEmptySlotSprite);
    }

    private enum VendorPanelTab { Items, Buying, Selling }

    private sealed class DragOverGlobalTimeSink : UiElement, IUiGlobalTimeListener
    {
        private readonly Action _onGlobalUiTime;
        public DragOverGlobalTimeSink(Action onGlobalUiTime) => _onGlobalUiTime = onGlobalUiTime;
        public void OnGlobalUiTime(double nowSeconds) => _onGlobalUiTime();
    }

    private void ShowTab(VendorPanelTab tab)
    {
        if (_lastAlternateCurrencyPurchase != 0)
        {
            _lastAlternateCurrencyPurchase = 0;
            RefreshMoneyText();
        }
        _itemsPage.Visible = tab == VendorPanelTab.Items;
        _buyingPage.Visible = tab == VendorPanelTab.Buying;
        _sellingPage.Visible = tab == VendorPanelTab.Selling;
        RetailTabBinding.SetOpen(_itemsTab, tab == VendorPanelTab.Items);
        RetailTabBinding.SetOpen(_buyingTab, tab == VendorPanelTab.Buying);
        RetailTabBinding.SetOpen(_sellingTab, tab == VendorPanelTab.Selling);
    }

    private void OnVendorChanged(VendorTransition transition)
    {
        switch (transition.Kind)
        {
            case VendorStateTransitionKind.Opened:
                _buyStaging.Clear();
                _sellStaging.Clear();
                _pendingVendorSplit = null;
                ResetAlternateCurrencyTracking();
                RefreshMoneyText();
                _selectedCategoryIndex = -1;
                ShowTab(VendorPanelTab.Items);
                RebuildCategories();
                _window.Show();
                break;
            case VendorStateTransitionKind.Refreshed:
                ResetAlternateCurrencyTracking();
                RefreshMoneyText();
                ShowTab(VendorPanelTab.Items);
                RebuildCategories();
                _window.Show();
                break;
            case VendorStateTransitionKind.Closed:
            case VendorStateTransitionKind.Reset:
                _buyStaging.Clear();
                _sellStaging.Clear();
                _pendingVendorSplit = null;
                ResetAlternateCurrencyTracking();
                ClearContent();
                ShowTab(VendorPanelTab.Items);
                _window.Hide();
                DismissCloseConfirmationIfOpen();
                break;
        }
    }

    private void RebuildCategories()
    {
        IReadOnlyList<VendorShopItem> items = _vendor.Items;

        _presentCategories.Clear();
        foreach ((string label, ItemType mask) in CategoryFilters)
        {
            uint maskValue = (uint)mask;
            bool present = false;
            for (int i = 0; i < items.Count; i++)
            {
                if (((items[i].ItemType ?? 0u) & maskValue) != 0u)
                {
                    present = true;
                    break;
                }
            }
            if (present) _presentCategories.Add((label, mask));
        }

        int selected = _selectedCategoryIndex;
        if (selected >= _presentCategories.Count - 1)
            selected = _presentCategories.Count - 1;
        if (selected < 0)
            selected = 0;
        _selectedCategoryIndex = selected;

        _typeMenu.Items = _presentCategories
            .Select(entry => new UiMenu.MenuItem(entry.Label, (object)(uint)entry.Mask))
            .ToArray();
        _typeMenu.Selected = _selectedCategoryIndex >= 0 && _selectedCategoryIndex < _presentCategories.Count
            ? (object)(uint)_presentCategories[_selectedCategoryIndex].Mask
            : null;

        RebuildItemList();
    }

    private void SelectCategory(uint mask)
    {
        int index = _presentCategories.FindIndex(entry => (uint)entry.Mask == mask);
        if (index < 0 || index == _selectedCategoryIndex) return;

        _selectedCategoryIndex = index;
        _typeMenu.Selected = (object)mask;
        RebuildItemList();
    }

    private void RebuildItemList() => RebuildItemList(reselectFirst: true);

    private void RebuildItemList(bool reselectFirst)
    {
        ItemType activeMask = _selectedCategoryIndex >= 0 && _selectedCategoryIndex < _presentCategories.Count
            ? _presentCategories[_selectedCategoryIndex].Mask
            : default;
        uint maskValue = (uint)activeMask;

        IReadOnlyList<VendorShopItem> items = _vendor.Items;
        uint? selectedGuid = _selection.SelectedObjectId;
        VendorShopItem? firstItem = null;
        bool selectedStillVisible = false;

        using (_itemList.DeferLayout())
        {
            _itemList.Flush();
            if (maskValue != 0u)
            {
                foreach (VendorShopItem item in items)
                {
                    if (((item.ItemType ?? 0u) & maskValue) == 0u) continue;
                    if (AvailableShopQuantity(item) <= 0) continue;

                    firstItem ??= item;
                    if (item.ItemGuid == selectedGuid) selectedStillVisible = true;

                    uint icon = _resolveIcon(
                        (ItemType)(item.ItemType ?? 0u),
                        item.IconId,
                        item.IconUnderlayId,
                        item.IconOverlayId,
                        item.Effects);
                    var cell = new UiItemSlot
                    {
                        SpriteResolve = _itemList.SpriteResolve,
                        SlotIndex = _itemList.GetNumUIItems(),
                        AllowDragSource = false,
                        TooltipTextResolve = g => _objects.Get(g)?.GetTooltipDisplayName(),
                    };
                    cell.SetItem(item.ItemGuid, icon);
                    cell.Selected = item.ItemGuid == selectedGuid;
                    VendorShopItem captured = item;
                    cell.Clicked = () =>
                        _selection.Select(captured.ItemGuid, SelectionChangeSource.Vendor);
                    cell.DoubleClicked = () =>
                    {
                        _selection.Select(captured.ItemGuid, SelectionChangeSource.Vendor);
                        BuySelectedItem();
                    };
                    _itemList.AddItem(cell);
                }
            }
        }

        if (reselectFirst)
        {
            if (firstItem is { } first)
                _selection.Select(first.ItemGuid, SelectionChangeSource.Vendor);
            else
                _selection.Clear(SelectionChangeSource.Vendor);

            _itemList.Scroll.SetScrollY(0);
        }
        else if (selectedGuid is not null && !selectedStillVisible)
        {
            _selection.Clear(
                SelectionChangeSource.Vendor,
                SelectionChangeReason.SelectedObjectRemoved);
        }
    }

    private int AvailableShopQuantity(VendorShopItem item)
    {
        if (item.StackSize < 0)
            return int.MaxValue;
        int staged = _buyStaging.TryGet(item.ItemGuid, out VendorStagingEntry entry) ? entry.Quantity : 0;
        return item.StackSize - staged;
    }

    private void RefreshItemsTabAvailability() => RebuildItemList(reselectFirst: false);

    private void ApplyItemDisplay(VendorShopItem item)
    {
        for (int i = 0; i < _itemList.GetNumUIItems(); i++)
        {
            if (_itemList.GetItem(i) is { } cell)
                cell.Selected = cell.ItemId == item.ItemGuid;
        }

        int quantity = (int)ResolveBuyQuantity(item);

        string baseName = quantity <= 1
            ? item.Name ?? string.Empty
            : (string.IsNullOrEmpty(item.PluralName) ? item.Name : item.PluralName) ?? string.Empty;
        string nameText = quantity > 1 ? $"{quantity} {baseName}" : baseName;
        SetPlainText(_itemNameText, nameText);

        VendorShopProfile profile = _vendor.Profile;
        int price = ComputeShopItemPrice(item, quantity);
        SetPlainText(_itemCostText, BuildCostText(profile, quantity, price));

        SetActionButtonsEnabled(true);
    }

    private int ComputeShopItemPrice(VendorShopItem item, int quantity)
    {
        int rawValue = item.Value ?? 0;
        int perUnit = VendorPricing.PerUnitValue(rawValue, item.DescStackSize);
        return VendorPricing.SellPrice(
            perUnit,
            item.ItemType ?? 0u,
            _vendor.Profile.SellPrice,
            quantity);
    }

    private void ExamineItem(uint guid)
    {
        _selection.Select(guid, SelectionChangeSource.Vendor);
        _itemInteraction.ExamineSelectedOrEnterMode(guid);
    }

    private bool PressVendorItem(uint guid)
    {
        if (guid != 0u)
            _selection.Select(guid, SelectionChangeSource.Vendor);
        return false;
    }

    private void OnSelectionTransition(SelectionTransition transition)
    {
        _ = transition;
        RefreshSelectionDisplay();
        RefreshStagingSelectionHighlight();
    }

    private void RefreshStagingSelectionHighlight()
    {
        uint? selected = _selection.SelectedObjectId;
        SetHighlight(_buyingList, selected);
        SetHighlight(_sellingList, selected);

        static void SetHighlight(UiItemList? list, uint? selectedGuid)
        {
            if (list is null) return;
            for (int i = 0; i < list.GetNumUIItems(); i++)
            {
                if (list.GetItem(i) is { } cell)
                    cell.Selected = cell.ItemId == selectedGuid;
            }
        }
    }

    private void OnSplitQuantityChanged() => RefreshSelectionDisplay();

    private void RefreshSelectionDisplay()
    {
        uint? selected = _selection.SelectedObjectId;
        if (selected is { } guid)
        {
            foreach (VendorShopItem item in _vendor.Items)
            {
                if (item.ItemGuid == guid)
                {
                    ApplyItemDisplay(item);
                    return;
                }
            }
        }
        ClearSelectionDisplay();
    }

    private void OnObjectRemoved(ClientObject item)
    {
        if (IsCurrentAlternateCurrency(item))
        {
            _alternateCurrencyInventoryObserved = true;
            _lastAlternateCurrencyPurchase = 0;
            RefreshMoneyText();
        }
        if (_pendingVendorSplit is { } split && split.SourceGuid == item.ObjectId)
            _pendingVendorSplit = null;

        if (_selection.SelectedObjectId == item.ObjectId)
        {
            _selection.Clear(
                SelectionChangeSource.Vendor,
                SelectionChangeReason.SelectedObjectRemoved);
        }

        _sellStaging.Remove(item.ObjectId, -1);

        if (_buyStaging.Remove(item.ObjectId, -1))
        {
            string name = string.IsNullOrWhiteSpace(item.Name) ? "that item" : item.Name;
            _systemMessage?.Invoke($"Removing {name} from shopping list");
        }
    }

    private uint ResolveBuyQuantity(VendorShopItem item)
    {
        uint stackSize = (uint)VendorSplitPolicy.ResolveAuthoredStackSize(item.DescStackSize, item.MaxStackSize);
        if (stackSize <= 1u)
            return 1u;

        uint selected = _selection.SelectedObjectId ?? item.ItemGuid;
        return _splitQuantity.GetObjectSplitSize(item.ItemGuid, selected, stackSize);
    }

    private string BuildCostText(VendorShopProfile profile, int quantity, int price)
    {
        if (profile.AlternateCurrencyWcid != 0u)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "This item costs {0} {1}. You have {2} {1}.",
                price,
                profile.AlternateCurrencyPluralName,
                ResolveAlternateCurrencyAmount(profile));
        }

        int playerTotal = _objects.Get(_playerGuid())?.Properties.GetInt((uint)PropertyInt.CoinValue) ?? 0;
        string verb = quantity <= 1 ? "costs" : "cost";
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1}p (you have {2}p)",
            verb,
            price.ToString("N0", CultureInfo.InvariantCulture),
            playerTotal.ToString("N0", CultureInfo.InvariantCulture));
    }

    private void ClearSelectionDisplay()
    {
        for (int i = 0; i < _itemList.GetNumUIItems(); i++)
        {
            if (_itemList.GetItem(i) is { } cell)
                cell.Selected = false;
        }
        SetPlainText(_itemNameText, string.Empty);
        SetPlainText(_itemCostText, string.Empty);
        SetActionButtonsEnabled(false);
    }

    private void SetActionButtonsEnabled(bool enabled)
    {
        _buyEnabledBySelection = enabled;
        RecomputeBuyButtonEnabled();
    }

    private void RecomputeBuyButtonEnabled()
    {
        if (_buyButton is not null)
            _buyButton.Enabled = _buyEnabledBySelection && _itemInteraction.CanMakeInventoryRequest;
        if (_addButton is not null)
            _addButton.Enabled = _buyEnabledBySelection;
    }

    private void OnInteractionStateChanged() => RecomputeBuyButtonEnabled();

    private void BuySelectedItem()
    {
        if (_selection.SelectedObjectId is not { } guid)
            return;

        VendorShopItem? selected = null;
        foreach (VendorShopItem item in _vendor.Items)
        {
            if (item.ItemGuid == guid)
            {
                selected = item;
                break;
            }
        }
        if (selected is not { } shopItem)
            return;

        uint quantity = ResolveBuyQuantity(shopItem);
        VendorShopProfile profile = _vendor.Profile;
        if (_itemInteraction.TryBuy(
                _vendor.VendorId,
                shopItem.ItemGuid,
                (int)quantity,
                profile.AlternateCurrencyWcid))
        {
            RecordAlternateCurrencyPurchase(
                profile,
                ComputeShopItemPrice(shopItem, (int)quantity));
        }
    }

    private bool TryFindShopItem(uint guid, out VendorShopItem shopItem)
    {
        foreach (VendorShopItem item in _vendor.Items)
        {
            if (item.ItemGuid == guid)
            {
                shopItem = item;
                return true;
            }
        }
        shopItem = default;
        return false;
    }

    private void AddSelectedToBuyList()
    {
        if (_selection.SelectedObjectId is not { } guid || !TryFindShopItem(guid, out VendorShopItem shopItem))
            return;

        uint quantity = ResolveBuyQuantity(shopItem);
        if (_buyStaging.Add(shopItem.ItemGuid, (int)quantity) == VendorStagingAddOutcome.Capped)
            _systemMessage?.Invoke(VendorStagingList.TooMuchMessage);
    }

    private static int BuyStagingRemovalAmount(VendorShopItem item) =>
        (item.MaxStackSize ?? 1) > 1 ? -1 : 1;

    private void BuyItemButtonPressed()
    {
        if (_selection.SelectedObjectId is not { } guid || !TryFindShopItem(guid, out VendorShopItem shopItem))
            return;

        uint quantity = ResolveBuyQuantity(shopItem);
        if (_itemInteraction.TryBuy(
                _vendor.VendorId,
                shopItem.ItemGuid,
                (int)quantity,
                _vendor.Profile.AlternateCurrencyWcid))
        {
            RecordAlternateCurrencyPurchase(
                _vendor.Profile,
                ComputeShopItemPrice(shopItem, (int)quantity));
            _buyStaging.Remove(shopItem.ItemGuid, BuyStagingRemovalAmount(shopItem));
        }
    }

    private const string NotEnoughMoneyMessage = "You don't have enough money";

    private const string NotEnoughRoomMessage = "You must empty some slots in your backpack first";

    private void BuyAllButtonPressed()
    {
        if (_buyStaging.IsEmpty)
            return;

        var items = new List<(int Amount, uint ItemGuid)>(_buyStaging.Entries.Count);
        foreach (VendorStagingEntry entry in _buyStaging.Entries)
            items.Add((entry.Quantity, entry.ItemGuid));

        VendorShopProfile profile = _vendor.Profile;
        int transactionValue = ComputeBuyTransactionValue();

        if (profile.AlternateCurrencyWcid == 0u)
        {
            int playerTotal = _objects.Get(_playerGuid())?.Properties.GetInt((uint)PropertyInt.CoinValue) ?? 0;
            if (transactionValue > playerTotal)
            {
                _systemMessage?.Invoke(NotEnoughMoneyMessage);
                return;
            }
        }
        else if (transactionValue > ResolveAlternateCurrencyAmount(profile))
        {
            _systemMessage?.Invoke(NotEnoughMoneyMessage);
            return;
        }

        (int itemSlotsNeeded, int containerSlotsNeeded) = ComputeBuySlotsNeeded(items);
        ClientObject? player = _objects.Get(_playerGuid());
        (int itemsUsed, int containersUsed) = CountPlayerContents();

        int freeContainerSlots = (player?.ContainersCapacity ?? 0) - containersUsed;
        if (containerSlotsNeeded > freeContainerSlots)
        {
            _systemMessage?.Invoke(NotEnoughRoomMessage);
            return;
        }
        int freeItemSlots = (player?.ItemsCapacity ?? 0) - itemsUsed;
        if (itemSlotsNeeded > freeItemSlots)
        {
            _systemMessage?.Invoke(NotEnoughRoomMessage);
            return;
        }

        if (_itemInteraction.TryBuyAll(_vendor.VendorId, items, profile.AlternateCurrencyWcid))
        {
            RecordAlternateCurrencyPurchase(profile, transactionValue);
            _buyStaging.Clear();
        }
    }

    private int ComputeBuyTransactionValue()
    {
        VendorShopProfile profile = _vendor.Profile;
        int total = 0;
        foreach (VendorStagingEntry entry in _buyStaging.Entries)
        {
            if (!TryFindShopItem(entry.ItemGuid, out VendorShopItem item))
                continue;
            int perUnit = VendorPricing.PerUnitValue(item.Value ?? 0, item.DescStackSize);
            total += VendorPricing.SellPrice(perUnit, item.ItemType ?? 0u, profile.SellPrice, entry.Quantity);
        }
        return total;
    }

    private int ComputeSellTransactionValue()
    {
        VendorShopProfile profile = _vendor.Profile;
        int total = 0;
        foreach (VendorStagingEntry entry in _sellStaging.Entries)
        {
            if (_objects.Get(entry.ItemGuid) is not { } item)
                continue;
            int perUnit = VendorPricing.PerUnitValue(item.Value, item.StackSize);
            total += VendorPricing.BuyPrice(perUnit, (uint)item.Type, profile.BuyPrice, entry.Quantity);
        }
        return total;
    }

    private static string BuildTransactionListText(
        string verb, int count, int totalValue, VendorShopProfile profile)
    {
        string noun = count == 1 ? "item" : "items";
        if (profile.AlternateCurrencyWcid != 0u)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} {2} worth {3} {4}",
                verb,
                count,
                noun,
                totalValue,
                profile.AlternateCurrencyPluralName);
        }
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} {2} worth {3}p",
            verb,
            count,
            noun,
            totalValue.ToString("N0", CultureInfo.InvariantCulture));
    }

    private string BuildPurseText(VendorShopProfile profile)
    {
        if (profile.AlternateCurrencyWcid != 0u)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "You have {0} {1}.",
                ResolveAlternateCurrencyAmount(profile),
                profile.AlternateCurrencyPluralName);
        }
        int playerTotal = _objects.Get(_playerGuid())?.Properties.GetInt((uint)PropertyInt.CoinValue) ?? 0;
        return string.Format(
            CultureInfo.InvariantCulture,
            "You have {0}p",
            playerTotal.ToString("N0", CultureInfo.InvariantCulture));
    }

    private void UpdateBuyTransactionText()
    {
        if (_buyListText is null && _buyPurseText is null)
            return;

        VendorShopProfile profile = _vendor.Profile;
        int count = _buyStaging.Entries.Sum(e => e.Quantity);
        int totalValue = ComputeBuyTransactionValue();
        if (_buyListText is not null)
            SetPlainText(_buyListText, BuildTransactionListText("Buying", count, totalValue, profile));
        if (_buyPurseText is not null)
            SetPlainText(_buyPurseText, BuildPurseText(profile));
    }

    private void UpdateSellTransactionText()
    {
        if (_sellListText is null && _sellPurseText is null)
            return;

        VendorShopProfile profile = _vendor.Profile;
        int count = _sellStaging.Entries.Sum(e => e.Quantity);
        int totalValue = ComputeSellTransactionValue();
        if (_sellListText is not null)
            SetPlainText(_sellListText, BuildTransactionListText("Selling", count, totalValue, profile));
        if (_sellPurseText is not null)
            SetPlainText(_sellPurseText, BuildPurseText(profile));
    }

    private void OnObjectMoneyChanged(ClientObject updated)
    {
        TryResolvePendingVendorSplit(updated);
        if (updated.ObjectId != _playerGuid())
            return;
        RefreshMoneyText();
    }

    private void OnObjectAdded(ClientObject item)
    {
        TryResolvePendingVendorSplit(item);
        if (IsCurrentAlternateCurrency(item)
            && _objects.IsOwnedByObject(item.ObjectId, _playerGuid()))
        {
            ReconcileAlternateCurrencyInventory();
        }
    }

    private void OnStackSizeUpdated(ClientObject item)
    {
        if (IsCurrentAlternateCurrency(item)
            && _objects.IsOwnedByObject(item.ObjectId, _playerGuid()))
        {
            ReconcileAlternateCurrencyInventory();
        }
    }

    private void OnObjectMoved(ClientObjectMove move)
    {
        if (move.Item is not { } item)
            return;

        TryResolvePendingVendorSplit(item);
        if (!IsCurrentAlternateCurrency(item))
            return;

        ReconcileAlternateCurrencyInventory();
    }

    private void ReconcileAlternateCurrencyInventory()
    {
        _alternateCurrencyInventoryObserved = true;
        _lastAlternateCurrencyPurchase = 0;
        RefreshMoneyText();
    }

    private void RefreshMoneyText()
    {
        UpdateBuyTransactionText();
        UpdateSellTransactionText();
        RefreshSelectionDisplay();
    }

    private bool IsCurrentAlternateCurrency(ClientObject item)
    {
        uint wcid = _vendor.Profile.AlternateCurrencyWcid;
        return wcid != 0u && item.WeenieClassId == wcid;
    }

    private int ResolveAlternateCurrencyAmount(VendorShopProfile profile)
    {
        if (profile.AlternateCurrencyWcid == 0u)
            return 0;

        long live = 0;
        bool found = false;
        foreach (ClientObject item in _objects.Objects)
        {
            if (item.WeenieClassId != profile.AlternateCurrencyWcid
                || !_objects.IsOwnedByObject(item.ObjectId, _playerGuid()))
            {
                continue;
            }
            found = true;
            live += Math.Max(1, item.StackSize);
        }

        long baseline = found || _alternateCurrencyInventoryObserved
            ? live
            : profile.AlternateCurrencyAmount;
        return (int)Math.Clamp(
            baseline - _lastAlternateCurrencyPurchase,
            0L,
            int.MaxValue);
    }

    private void RecordAlternateCurrencyPurchase(VendorShopProfile profile, int price)
    {
        if (profile.AlternateCurrencyWcid == 0u || price <= 0)
            return;
        _lastAlternateCurrencyPurchase = price;
        RefreshMoneyText();
    }

    private void ResetAlternateCurrencyTracking()
    {
        _lastAlternateCurrencyPurchase = 0;
        uint wcid = _vendor.Profile.AlternateCurrencyWcid;
        _alternateCurrencyInventoryObserved = wcid != 0u
            && _objects.Objects.Any(item =>
                item.WeenieClassId == wcid
                && _objects.IsOwnedByObject(item.ObjectId, _playerGuid()));
    }

    private (int ItemSlots, int ContainerSlots) ComputeBuySlotsNeeded(
        IReadOnlyList<(int Amount, uint ItemGuid)> items)
    {
        int itemSlots = 0, containerSlots = 0;
        foreach ((int amount, uint guid) in items)
        {
            if (!TryFindShopItem(guid, out VendorShopItem item))
                continue;
            bool isContainer = ((item.ItemType ?? 0u) & (uint)ItemType.Container) != 0u;
            bool stackable = (item.MaxStackSize ?? 1) > 1;
            if (stackable)
            {
                if (isContainer) containerSlots += 1; else itemSlots += 1;
            }
            else
            {
                if (isContainer) containerSlots += amount; else itemSlots += amount;
            }
        }
        return (itemSlots, containerSlots);
    }

    private (int Items, int Containers) CountPlayerContents()
    {
        int items = 0, containers = 0;
        foreach (uint guid in _objects.GetContents(_playerGuid()))
        {
            ClientObject? obj = _objects.Get(guid);
            bool isContainer = obj is not null
                && (obj.ContainerTypeHint != 0u || (obj.Type & ItemType.Container) != 0);
            if (isContainer) containers++; else items++;
        }
        return (items, containers);
    }

    private void BuyClearItemButtonPressed()
    {
        if (_selection.SelectedObjectId is not { } guid)
            return;

        int amount = TryFindShopItem(guid, out VendorShopItem shopItem)
            ? BuyStagingRemovalAmount(shopItem)
            : -1;
        _buyStaging.Remove(guid, amount);
    }

    private const string CannotSellPartialStackMessage = "Cannot sell part of a stack";

    private void SellItemButtonPressed()
    {
        if (_selection.SelectedObjectId is not { } guid || _objects.Get(guid) is not { } item)
            return;

        uint fullStack = (uint)Math.Max(1, item.StackSize);
        if (fullStack > 1)
        {
            uint live = _splitQuantity.GetObjectSplitSize(guid, guid, fullStack);
            if (live < fullStack)
            {
                _systemMessage?.Invoke(CannotSellPartialStackMessage);
                return;
            }
        }

        if (_itemInteraction.TrySell(_vendor.VendorId, new[] { (1, guid) }))
            _sellStaging.Remove(guid, -1);
    }

    private void SellAllButtonPressed()
    {
        if (_sellStaging.IsEmpty)
            return;

        var items = new List<(int Amount, uint ItemGuid)>(_sellStaging.Entries.Count);
        foreach (VendorStagingEntry entry in _sellStaging.Entries)
            items.Add((entry.Quantity, entry.ItemGuid));

        if (_itemInteraction.TrySell(_vendor.VendorId, items))
            _sellStaging.Clear();
    }

    private void SellClearItemButtonPressed()
    {
        if (_selection.SelectedObjectId is not { } guid)
            return;
        _sellStaging.Remove(guid, -1);
    }

    private void RebuildBuyingList()
    {
        if (_buyingList is not { } list)
            return;

        uint? selectedGuid = _selection.SelectedObjectId;
        using (list.DeferLayout())
        {
            list.Flush();
            foreach (VendorStagingEntry entry in _buyStaging.Entries)
            {
                if (!TryFindShopItem(entry.ItemGuid, out VendorShopItem shopItem))
                    continue;

                uint icon = _resolveIcon(
                    (ItemType)(shopItem.ItemType ?? 0u),
                    shopItem.IconId,
                    shopItem.IconUnderlayId,
                    shopItem.IconOverlayId,
                    shopItem.Effects);
                var cell = new UiItemSlot
                {
                    SpriteResolve = list.SpriteResolve,
                    SlotIndex = list.GetNumUIItems(),
                    AllowDragSource = false,
                    TooltipTextResolve = g => _objects.Get(g)?.GetTooltipDisplayName(),
                };
                cell.SetItem(shopItem.ItemGuid, icon);
                cell.Selected = shopItem.ItemGuid == selectedGuid;
                VendorShopItem captured = shopItem;
                cell.Clicked = () =>
                    _selection.Select(captured.ItemGuid, SelectionChangeSource.Vendor);
                cell.DoubleClicked = () => RemoveOneBuyingUnit(captured.ItemGuid);
                list.AddItem(cell);
            }
        }
    }

    private void RebuildSellingList()
    {
        if (_sellingList is not { } list)
            return;

        uint? selectedGuid = _selection.SelectedObjectId;
        using (list.DeferLayout())
        {
            list.Flush();
            foreach (VendorStagingEntry entry in _sellStaging.Entries)
            {
                if (_objects.Get(entry.ItemGuid) is not { } item)
                    continue;

                uint icon = _resolveIcon(
                    item.Type, item.IconId, item.IconUnderlayId, item.IconOverlayId, item.Effects);
                var cell = new UiItemSlot
                {
                    SpriteResolve = list.SpriteResolve,
                    SlotIndex = list.GetNumUIItems(),
                    AllowDragSource = true,
                    SourceKind = ItemDragSource.Inventory,
                    TooltipTextResolve = g => _objects.Get(g)?.GetTooltipDisplayName(),
                };
                cell.SetItem(item.ObjectId, icon);
                cell.Selected = item.ObjectId == selectedGuid;
                uint captured = item.ObjectId;
                cell.Clicked = () =>
                    _selection.Select(captured, SelectionChangeSource.Vendor);
                cell.DoubleClicked = () => RemoveSellingEntry(captured);
                list.AddItem(cell);
            }
        }
    }


    public void OnDragLift(UiItemList sourceList, UiItemSlot sourceCell, ItemDragPayload payload)
    {
        if (!ReferenceEquals(sourceList, _sellingList) || payload.ObjId == 0u)
            return;

        _selection.Select(payload.ObjId, SelectionChangeSource.Vendor);
        RemoveSellingEntry(payload.ObjId, reportRemoval: false);
        if (_objects.Get(payload.ObjId) is not { } item)
            return;

        uint fullStack = (uint)Math.Max(1, item.StackSize);
        uint selected = _splitQuantity.GetObjectSplitSize(
            payload.ObjId,
            _selection.SelectedObjectId ?? 0u,
            fullStack);
        if (selected < fullStack)
        {
            _itemInteraction.ReportClientLocal(
                "You cannot split items from this panel");
            _splitQuantity.Reset(fullStack);
        }
    }

    private void RemoveOneBuyingUnit(uint itemGuid)
    {
        if (!_buyStaging.TryGet(itemGuid, out _))
            return;
        _selection.Select(itemGuid, SelectionChangeSource.Vendor);
        ReportShoppingListRemoval(itemGuid);
        _buyStaging.Remove(itemGuid, 1);
    }

    private void RemoveSellingEntry(uint itemGuid, bool reportRemoval = true)
    {
        if (!_sellStaging.TryGet(itemGuid, out _))
            return;
        _selection.Select(itemGuid, SelectionChangeSource.Vendor);
        if (reportRemoval)
            ReportShoppingListRemoval(itemGuid);
        _sellStaging.Remove(itemGuid, -1);
    }

    private void ReportShoppingListRemoval(uint itemGuid)
    {
        string? name = _objects.Get(itemGuid)?.GetAppropriateName();
        if (string.IsNullOrWhiteSpace(name))
            name = _vendor.Items.FirstOrDefault(item => item.ItemGuid == itemGuid).Name;
        if (string.IsNullOrWhiteSpace(name))
            name = "that item";
        _itemInteraction.ReportClientLocal(
            $"Removing {name} from shopping list");
    }

    public ItemDragAcceptance OnDragOver(
        UiItemList targetList,
        UiItemSlot targetCell,
        ItemDragPayload payload)
    {
        if (!ReferenceEquals(targetList, _sellingList) || payload.ObjId == 0u)
            return ItemDragAcceptance.Reject;

        return EvaluateSellAcceptability(payload.ObjId, out _) == VendorSellRejection.None
            ? ItemDragAcceptance.Accept
            : ItemDragAcceptance.Reject;
    }

    private void PollDragOver()
    {
        if (_sellingPage.Visible) return;
        if (!_window.IsVisible) return;

        UiRoot? root = _itemsPage.FindRoot();
        if (root?.DragSource is null) return;

        System.Numerics.Vector2 pos = _window.OuterFrame.ScreenPosition;
        float x0 = pos.X, y0 = pos.Y;
        float x1 = x0 + _window.OuterFrame.Width, y1 = y0 + _window.OuterFrame.Height;
        if (root.MouseX > x0 && root.MouseX < x1 && root.MouseY > y0 && root.MouseY < y1)
            ShowTab(VendorPanelTab.Selling);
    }

    public void HandleDropRelease(
        UiItemList targetList,
        UiItemSlot targetCell,
        ItemDragPayload payload)
    {
        if (!ReferenceEquals(targetList, _sellingList) || payload.ObjId == 0u)
            return;

        VendorSellRejection rejection = EvaluateSellAcceptability(payload.ObjId, out int quantity);
        if (rejection != VendorSellRejection.None)
        {
            if (VendorSellAcceptability.MessageFor(rejection) is { } message)
                _systemMessage?.Invoke(message);
            return;
        }

        ShowTab(VendorPanelTab.Selling);
        _selection.Select(payload.ObjId, SelectionChangeSource.Vendor);

        ClientObject item = _objects.Get(payload.ObjId)!;
        IReadOnlyList<uint> contents = _objects.GetContents(payload.ObjId);
        if (contents.Count > 0)
        {
            StageContainerContents(item, contents);
            return;
        }

        int fullStack = Math.Max(1, item.StackSize);
        if (quantity < fullStack)
        {
            _pendingVendorSplit = new PendingVendorSplit(
                payload.ObjId,
                item.WeenieClassId,
                quantity);
            if (!_itemInteraction.TrySplitToContainer(
                    payload.ObjId,
                    item.ContainerId,
                    0u,
                    (uint)quantity))
            {
                _pendingVendorSplit = null;
                _systemMessage?.Invoke("Cannot split the stack to sell it");
                return;
            }

            string name = string.IsNullOrWhiteSpace(item.Name) ? "item" : item.Name;
            _systemMessage?.Invoke($"Splitting the {name} before selling them");
        }
        _sellStaging.Stage(payload.ObjId, quantity);
    }

    /// <summary>
    /// Dropping a pack that holds items sells the pack's contents, not the
    /// pack: one notice names the pack, then each direct child is checked
    /// against the vendor on its own and staged if it is a sellable leaf.
    /// Unsellable children are skipped silently. A nested pack that still
    /// holds items is neither staged nor opened (one level only); an empty
    /// nested pack is an ordinary leaf.
    /// </summary>
    private void StageContainerContents(ClientObject pack, IReadOnlyList<uint> contents)
    {
        string name = string.IsNullOrWhiteSpace(pack.Name) ? "item" : pack.Name;
        _systemMessage?.Invoke($"Selling contents of {name}");

        uint[] children = contents.ToArray();
        foreach (uint child in children)
        {
            if (_objects.GetContents(child).Count > 0)
                continue;
            if (EvaluateSellAcceptability(child, out int childQuantity) != VendorSellRejection.None)
                continue;
            _sellStaging.Stage(child, childQuantity);
        }
    }

    private VendorSellRejection EvaluateSellAcceptability(uint itemGuid, out int quantity)
    {
        quantity = 1;
        if (_objects.Get(itemGuid) is not { } item)
            return VendorSellRejection.WrongType;

        bool ownedByPlayer = _itemInteraction.IsOwnedByPlayer(itemGuid);
        int containedItemCount = _objects.GetContents(itemGuid).Count;
        int perUnitValue = VendorPricing.PerUnitValue(item.Value, item.StackSize);

        VendorSellRejection rejection = VendorSellAcceptability.Evaluate(
            ownedByPlayer,
            containedItemCount,
            (uint)item.Type,
            perUnitValue,
            _vendor.Profile.MerchandiseItemTypes,
            _vendor.Profile.MerchandiseMinValue,
            _vendor.Profile.MerchandiseMaxValue,
            item.PublicWeenieBitfield ?? 0u);

        if (rejection == VendorSellRejection.None)
        {
            uint fullStack = (uint)Math.Max(1, item.StackSize);
            // Never sell more than the stack the player actually holds.
            quantity = (int)Math.Min(
                _splitQuantity.GetObjectSplitSize(
                    itemGuid,
                    _selection.SelectedObjectId ?? 0u,
                    fullStack),
                fullStack);
        }
        return rejection;
    }

    private void TryResolvePendingVendorSplit(ClientObject item)
    {
        if (_pendingVendorSplit is not { } pending
            || item.ObjectId == pending.SourceGuid
            || item.WeenieClassId != pending.WeenieClassId
            || item.StackSize != pending.Quantity
            || !_objects.IsOwnedByObject(item.ObjectId, _playerGuid()))
        {
            return;
        }

        if (_sellStaging.Replace(pending.SourceGuid, item.ObjectId))
            _pendingVendorSplit = null;
    }

    private void OnInventoryRequestFailed(PendingInventoryRequest request, uint _)
    {
        if (_pendingVendorSplit is not { } pending
            || request.Kind != InventoryRequestKind.SplitToContainer
            || request.ItemId != pending.SourceGuid)
        {
            return;
        }

        _sellStaging.Remove(pending.SourceGuid, -1);
        _pendingVendorSplit = null;
    }

    private const string CloseConfirmationMessage =
        "You have not completed all transactions. Are you sure you want to leave this vendor?";

    private void CloseButtonPressed()
    {
        if (_buyStaging.IsEmpty && _sellStaging.IsEmpty)
        {
            _window.Hide();
            return;
        }

        if (_dialogs is null || _closeConfirmContext != 0u)
            return;

        _closeConfirmContext = _dialogs.MakeDialog(
            RetailDialogData.Confirmation(CloseConfirmationMessage),
            result =>
            {
                _closeConfirmContext = 0u;
                if (result.GetBoolean(RetailDialogProperty.ConfirmationResult))
                    _window.Hide();
            });
    }

    private void DismissCloseConfirmationIfOpen()
    {
        if (_closeConfirmContext == 0u)
            return;
        _dialogs?.CloseDialog(_closeConfirmContext);
        _closeConfirmContext = 0u;
    }

    private void ClearContent()
    {
        _presentCategories.Clear();
        _selectedCategoryIndex = -1;
        _typeMenu.Items = Array.Empty<UiMenu.MenuItem>();
        _typeMenu.Selected = null;
        _itemList.Flush();
        ClearSelectionDisplay();
        UpdateBuyTransactionText();
        UpdateSellTransactionText();
    }

    private static void ConfigureEmptyStrip(UiItemList? list, uint emptySlotSprite)
    {
        if (list is null) return;

        list.Flush();
        list.Columns = 1;
        list.SingleRow = true;
        list.HorizontalScroll = true;
        list.CellWidth = 32f;
        list.CellHeight = 32f;
        list.FillVisibleEmptySlots = true;
        if (emptySlotSprite != 0u)
            list.CellEmptySprite = emptySlotSprite;
        list.EmptySlotFactory = () => new UiItemSlot
        {
            SpriteResolve = list.SpriteResolve,
            AllowDragSource = false,
        };
    }

    private static void SetPlainText(UiText text, string value)
    {
        IReadOnlyList<UiText.Line> lines = string.IsNullOrEmpty(value)
            ? Array.Empty<UiText.Line>()
            : new[] { new UiText.Line(value, text.DefaultColor) };
        text.LinesProvider = () => lines;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vendor.Changed -= OnVendorChanged;
        _selection.Changed -= OnSelectionTransition;
        _objects.ObjectAdded -= OnObjectAdded;
        _objects.ObjectRemoved -= OnObjectRemoved;
        _objects.ObjectUpdated -= OnObjectMoneyChanged;
        _objects.StackSizeUpdated -= OnStackSizeUpdated;
        _objects.ObjectMoved -= OnObjectMoved;
        _itemInteraction.RuntimeTransactions.Inventory.RequestFailed -= OnInventoryRequestFailed;
        _itemInteraction.StateChanged -= OnInteractionStateChanged;
        _splitQuantity.Changed -= OnSplitQuantityChanged;
        _buyStaging.Changed -= RebuildBuyingList;
        _buyStaging.Changed -= RefreshItemsTabAvailability;
        _buyStaging.Changed -= UpdateBuyTransactionText;
        _sellStaging.Changed -= RebuildSellingList;
        _sellStaging.Changed -= UpdateSellTransactionText;
        DismissCloseConfirmationIfOpen();
        _dragOverSink.Parent?.RemoveChild(_dragOverSink);
        RetailTabBinding.SetClick(_itemsTab, null);
        RetailTabBinding.SetClick(_buyingTab, null);
        RetailTabBinding.SetClick(_sellingTab, null);
        _typeMenu.OnSelect = null;
        _typeMenu.ButtonLabelProvider = null;
        _itemList.ExamineItemRequested = null;
        _itemList.PrimaryItemPressed = null;
        if (_buyingList is not null)
        {
            _buyingList.ExamineItemRequested = null;
            _buyingList.PrimaryItemPressed = null;
        }
        if (_sellingList is not null)
        {
            _sellingList.ExamineItemRequested = null;
            _sellingList.PrimaryItemPressed = null;
        }
        if (_close is not null)
            _close.OnClick = null;
        if (_buyButton is not null)
            _buyButton.OnClick = null;
        if (_addButton is not null)
            _addButton.OnClick = null;
        if (_buyItemButton is not null)
            _buyItemButton.OnClick = null;
        if (_buyAllButton is not null)
            _buyAllButton.OnClick = null;
        if (_buyClearItemButton is not null)
            _buyClearItemButton.OnClick = null;
        if (_buyClearListButton is not null)
            _buyClearListButton.OnClick = null;
        if (_sellItemButton is not null)
            _sellItemButton.OnClick = null;
        if (_sellAllButton is not null)
            _sellAllButton.OnClick = null;
        if (_sellClearItemButton is not null)
            _sellClearItemButton.OnClick = null;
        if (_sellClearListButton is not null)
            _sellClearListButton.OnClick = null;
    }
}
