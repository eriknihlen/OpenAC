using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Core.Properties;
using AcDream.Core.Selection;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.UI.Layout;

public sealed class VendorUiControllerTests
{
    private const uint VendorGuid = 0x70000010u;
    private const uint ArmorItemGuid = 0x60000101u;
    private const uint FoodItemGuid = 0x60000102u;
    private const uint StackedItemGuid = 0x60000103u;
    private const uint AnotherArmorItemGuid = 0x60000104u;
    private const uint PlayerOwnedArmorGuid = 0x60000201u;
    private const uint PlayerOwnedWeaponGuid = 0x60000202u;
    private const uint PlayerOwnedArmorGuid2 = 0x60000205u;

    private sealed class TestElement : UiElement { }

    [Fact]
    public void Bind_FromRealDatFixture_ResolvesAllRequiredControls()
    {
        ImportedLayout layout = FixtureLoader.LoadVendor();
        var screen = new UiRoot { Width = 1280f, Height = 800f };
        RetailWindowHandle window = RetailWindowFrame.Mount(
            screen,
            layout.Root,
            static _ => (0u, 0, 0),
            new RetailWindowFrame.Options
            {
                WindowName = "vendor-fixture-smoke",
                Chrome = RetailWindowChrome.Imported,
                Visible = false,
            });

        var objects = new ClientObjectTable();
        using var itemInteraction = new ItemInteractionController(
            objects,
            new RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: static () => 0u,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null);
        VendorUiController? controller = VendorUiController.Bind(
            layout,
            new VendorState(),
            window,
            static (_, _, _, _, _) => 0u,
            objects,
            static () => 0u,
            itemInteraction,
            new SelectionState(),
            new StackSplitQuantityState(),
            datFont: null,
            debugFont: null,
            static _ => (0u, 0, 0));

        Assert.NotNull(controller);
    }

    [Fact]
    public void Bind_FromRealDatFixture_BuyingAndSellingLists_ConfiguredForEmptySlotFill()
    {
        ImportedLayout layout = FixtureLoader.LoadVendor();
        var screen = new UiRoot { Width = 1280f, Height = 800f };
        RetailWindowHandle window = RetailWindowFrame.Mount(
            screen,
            layout.Root,
            static _ => (0u, 0, 0),
            new RetailWindowFrame.Options
            {
                WindowName = "vendor-fixture-smoke-2",
                Chrome = RetailWindowChrome.Imported,
                Visible = false,
            });

        var objects = new ClientObjectTable();
        using var itemInteraction = new ItemInteractionController(
            objects,
            new RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: static () => 0u,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null);
        VendorUiController? controller = VendorUiController.Bind(
            layout,
            new VendorState(),
            window,
            static (_, _, _, _, _) => 0u,
            objects,
            static () => 0u,
            itemInteraction,
            new SelectionState(),
            new StackSplitQuantityState(),
            datFont: null,
            debugFont: null,
            static _ => (0u, 0, 0));
        Assert.NotNull(controller);

        var buyingList = Assert.IsType<UiItemList>(layout.FindElement(VendorUiController.BuyingListId));
        var sellingList = Assert.IsType<UiItemList>(layout.FindElement(VendorUiController.SellingListId));
        var buyingScrollbar = Assert.IsType<UiScrollbar>(
            layout.FindElement(VendorUiController.BuyingScrollbarId));
        var sellingScrollbar = Assert.IsType<UiScrollbar>(
            layout.FindElement(VendorUiController.SellingScrollbarId));

        foreach (UiItemList list in new[] { buyingList, sellingList })
        {
            Assert.True(list.SingleRow);
            Assert.True(list.HorizontalScroll);
            Assert.Equal(32f, list.CellWidth);
            Assert.Equal(32f, list.CellHeight);
            Assert.True(list.FillVisibleEmptySlots);
            Assert.NotNull(list.EmptySlotFactory);
            Assert.Equal(0, list.GetNumUIItems()); // never populated
        }

        Assert.Same(buyingList.Scroll, buyingScrollbar.Model);
        Assert.True(buyingScrollbar.Horizontal);
        Assert.Same(sellingList.Scroll, sellingScrollbar.Model);
        Assert.True(sellingScrollbar.Horizontal);
    }

    private sealed class Harness
    {
        public const int DefaultPlayerCoinValue = 1500;
        public const uint PlayerGuid = 0x50000001u;

        public readonly VendorState State = new();
        public readonly SelectionState Selection = new();
        public readonly StackSplitQuantityState SplitQuantity = new();
        public readonly UiRoot Screen = new() { Width = 800f, Height = 600f };
        public readonly ClientObjectTable Objects = new();
        public readonly UiItemList ItemList = new();
        public readonly UiScrollbar ItemScrollbar = new();
        public readonly UiMenu TypeMenu = new();
        public readonly UiText ItemNameText = new();
        public readonly UiText ItemCostText = new();
        public readonly UiText ItemsTab = new();
        public readonly UiText BuyingTab = new();
        public readonly UiText SellingTab = new();
        public readonly UiElement ItemsPage = new TestElement();
        public readonly UiElement BuyingPage = new TestElement();
        public readonly UiElement SellingPage = new TestElement();
        public readonly UiButton CloseButton;
        public readonly UiButton BuyButton;
        public readonly UiButton AddButton;
        public readonly UiItemList BuyingList = new();
        public readonly UiButton BuyItemButton;
        public readonly UiButton BuyAllButton;
        public readonly UiButton BuyClearItemButton;
        public readonly UiButton BuyClearListButton;
        // R3: the Buying/Selling tabs' own staged summary text.
        public readonly UiText BuyListText = new();
        public readonly UiText BuyPurseText = new();
        public readonly UiText SellListText = new();
        public readonly UiText SellPurseText = new();
        public readonly UiItemList SellingList = new();
        public readonly UiButton SellItemButton;
        public readonly UiButton SellAllButton;
        public readonly UiButton SellClearItemButton;
        public readonly UiButton SellClearListButton;
        public readonly RetailWindowHandle Window;
        public readonly VendorUiController Controller;
        public readonly List<uint> Examines = new();
        public readonly List<(uint VendorGuid, uint ItemGuid, int Amount, uint AlternateCurrencyId)> Buys = new();
        public readonly List<(uint VendorGuid, IReadOnlyList<(int Amount, uint ItemGuid)> Items, uint AlternateCurrencyId)> BuyAlls = new();
        public readonly List<(uint VendorGuid, IReadOnlyList<(int Amount, uint ItemGuid)> Items)> Sells = new();
        public readonly List<(uint Item, uint Container, uint Placement, uint Amount)> SplitPuts = new();
        public readonly List<string> SystemMessages = new();
        public readonly ItemInteractionController ItemInteraction;
        public readonly RetailDialogFactory Dialogs;
        public ImportedLayout? ShownDialog;

        public Harness()
        {
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = PlayerGuid,
                Type = ItemType.Creature,
                ItemsCapacity = 102,
                ContainersCapacity = 7,
            });
            var bundle = new PropertyBundle();
            bundle.Ints[(uint)PropertyInt.CoinValue] = DefaultPlayerCoinValue;
            Objects.UpsertProperties(PlayerGuid, bundle);

            var root = new TestElement { Width = 800f, Height = 110f };
            CloseButton = new UiButton(
                new ElementInfo { Id = VendorUiController.CloseId, Type = 1 },
                static _ => (0u, 0, 0));
            BuyButton = new UiButton(
                new ElementInfo { Id = VendorUiController.BuyButtonId, Type = 1 },
                static _ => (0u, 0, 0));
            AddButton = new UiButton(
                new ElementInfo { Id = VendorUiController.AddButtonId, Type = 1 },
                static _ => (0u, 0, 0));
            BuyItemButton = new UiButton(
                new ElementInfo { Id = VendorUiController.BuyItemButtonId, Type = 1 },
                static _ => (0u, 0, 0));
            BuyAllButton = new UiButton(
                new ElementInfo { Id = VendorUiController.BuyAllButtonId, Type = 1 },
                static _ => (0u, 0, 0));
            BuyClearItemButton = new UiButton(
                new ElementInfo { Id = VendorUiController.BuyClearItemButtonId, Type = 1 },
                static _ => (0u, 0, 0));
            BuyClearListButton = new UiButton(
                new ElementInfo { Id = VendorUiController.BuyClearListButtonId, Type = 1 },
                static _ => (0u, 0, 0));
            SellItemButton = new UiButton(
                new ElementInfo { Id = VendorUiController.SellItemButtonId, Type = 1 },
                static _ => (0u, 0, 0));
            SellAllButton = new UiButton(
                new ElementInfo { Id = VendorUiController.SellAllButtonId, Type = 1 },
                static _ => (0u, 0, 0));
            SellClearItemButton = new UiButton(
                new ElementInfo { Id = VendorUiController.SellClearItemButtonId, Type = 1 },
                static _ => (0u, 0, 0));
            SellClearListButton = new UiButton(
                new ElementInfo { Id = VendorUiController.SellClearListButtonId, Type = 1 },
                static _ => (0u, 0, 0));

            root.AddChild(CloseButton);
            root.AddChild(ItemsTab);
            root.AddChild(BuyingTab);
            root.AddChild(SellingTab);
            root.AddChild(ItemsPage);
            root.AddChild(BuyingPage);
            root.AddChild(SellingPage);
            ItemsPage.Width = root.Width; ItemsPage.Height = root.Height;
            BuyingPage.Width = root.Width; BuyingPage.Height = root.Height;
            SellingPage.Width = root.Width; SellingPage.Height = root.Height;
            ItemsPage.AddChild(ItemList);
            ItemsPage.AddChild(ItemScrollbar);
            ItemsPage.AddChild(TypeMenu);
            ItemsPage.AddChild(ItemNameText);
            ItemsPage.AddChild(ItemCostText);
            ItemsPage.AddChild(BuyButton);
            ItemsPage.AddChild(AddButton);
            BuyingPage.AddChild(BuyingList);
            BuyingPage.AddChild(BuyItemButton);
            BuyingPage.AddChild(BuyAllButton);
            BuyingPage.AddChild(BuyClearItemButton);
            BuyingPage.AddChild(BuyClearListButton);
            BuyingPage.AddChild(BuyListText);
            BuyingPage.AddChild(BuyPurseText);
            SellingPage.AddChild(SellingList);
            SellingPage.AddChild(SellItemButton);
            SellingPage.AddChild(SellAllButton);
            SellingPage.AddChild(SellClearItemButton);
            SellingPage.AddChild(SellClearListButton);
            SellingPage.AddChild(SellListText);
            SellingPage.AddChild(SellPurseText);

            var layout = new ImportedLayout(root, new Dictionary<uint, UiElement>
            {
                [VendorUiController.CloseId] = CloseButton,
                [VendorUiController.ItemsTabId] = ItemsTab,
                [VendorUiController.BuyingTabId] = BuyingTab,
                [VendorUiController.SellingTabId] = SellingTab,
                [VendorUiController.ItemsPageId] = ItemsPage,
                [VendorUiController.BuyingPageId] = BuyingPage,
                [VendorUiController.SellingPageId] = SellingPage,
                [VendorUiController.ItemListId] = ItemList,
                [VendorUiController.ItemScrollbarId] = ItemScrollbar,
                [VendorUiController.TypeFilterMenuId] = TypeMenu,
                [VendorUiController.ItemNameTextId] = ItemNameText,
                [VendorUiController.ItemCostTextId] = ItemCostText,
                [VendorUiController.BuyButtonId] = BuyButton,
                [VendorUiController.AddButtonId] = AddButton,
                [VendorUiController.BuyingListId] = BuyingList,
                [VendorUiController.BuyItemButtonId] = BuyItemButton,
                [VendorUiController.BuyAllButtonId] = BuyAllButton,
                [VendorUiController.BuyClearItemButtonId] = BuyClearItemButton,
                [VendorUiController.BuyClearListButtonId] = BuyClearListButton,
                [VendorUiController.SellingListId] = SellingList,
                [VendorUiController.SellItemButtonId] = SellItemButton,
                [VendorUiController.SellAllButtonId] = SellAllButton,
                [VendorUiController.SellClearItemButtonId] = SellClearItemButton,
                [VendorUiController.SellClearListButtonId] = SellClearListButton,
                [VendorUiController.BuyingListTextId] = BuyListText,
                [VendorUiController.BuyingPurseTextId] = BuyPurseText,
                [VendorUiController.SellingListTextId] = SellListText,
                [VendorUiController.SellingPurseTextId] = SellPurseText,
            });

            Window = RetailWindowFrame.Mount(
                Screen,
                root,
                static _ => (0u, 0, 0),
                new RetailWindowFrame.Options
                {
                    WindowName = WindowNames.Vendor,
                    Chrome = RetailWindowChrome.Imported,
                    Visible = false,
                    Draggable = false,
                    Resizable = false,
                });

            ItemInteraction = new ItemInteractionController(
                Objects,
                new RuntimeInteractionTransactionState(new InventoryTransactionState(Objects)),
                new InteractionState(),
                playerGuid: static () => PlayerGuid,
                sendUse: null,
                sendUseWithTarget: null,
                sendWield: null,
                sendDrop: null,
                sendExamine: Examines.Add,
                systemMessage: SystemMessages.Add,
                sendSplitToContainer: (item, container, placement, amount) =>
                    SplitPuts.Add((item, container, placement, amount)),
                sendBuy: (vendorGuid, itemGuid, amount, alternateCurrencyId) =>
                {
                    Buys.Add((vendorGuid, itemGuid, amount, alternateCurrencyId));
                    return true;
                },
                sendBuyAll: (vendorGuid, items, alternateCurrencyId) =>
                {
                    BuyAlls.Add((vendorGuid, items, alternateCurrencyId));
                    return true;
                },
                sendSell: (vendorGuid, items) =>
                {
                    Sells.Add((vendorGuid, items));
                    return true;
                });

            Dialogs = new RetailDialogFactory(Screen, _ =>
                ShownDialog = FixtureLoader.LoadConfirmationDialog());

            Controller = VendorUiController.Bind(
                layout,
                State,
                Window,
                // F5: sum every argument so a test can prove underlay/overlay/
                // effects were actually forwarded (iconId-only would silently
                // regress to dropping the last three).
                static (_, iconId, underlay, overlay, effects) => iconId + underlay + overlay + effects,
                Objects,
                static () => PlayerGuid,
                ItemInteraction,
                Selection,
                SplitQuantity,
                datFont: null,
                debugFont: null,
                static _ => (0u, 0, 0),
                dialogs: Dialogs,
                systemMessage: SystemMessages.Add)!;
            Screen.WindowManager.AttachController(WindowNames.Vendor, Controller);
        }
    }

    private static VendorShopProfile Profile(
        float sellRate = 1.5f, uint altCurrency = 0u, string altName = "", uint altAmount = 0u) =>
        new(0u, 0u, 0u, false, 1.0f, sellRate, altCurrency, altAmount, altName);

    private static VendorShopProfile SellProfile(
        uint merchandiseItemTypes,
        uint minValue = 0u,
        uint maxValue = VendorSellAcceptability.NoLimit) =>
        new(merchandiseItemTypes, minValue, maxValue, false, 1.0f, 1.5f, 0u, 0u, "");

    private static string GetText(UiText text)
        => string.Concat(text.LinesProvider().Select(line => line.Text));

    [Fact]
    public void Bind_WiresTheScrollbarToTheItemListsScrollModel()
    {
        var h = new Harness();

        Assert.True(h.ItemScrollbar.Horizontal);
        Assert.Same(h.ItemList.Scroll, h.ItemScrollbar.Model);
    }

    [Fact]
    public void Opened_ShowsWindowAndPopulatesFirstPresentCategoryInTableOrder()
    {
        var h = new Harness();
        var items = new[]
        {
            new VendorShopItem(FoodItemGuid, -1, 1u, "Bread", (uint)ItemType.Food, 100u, 5),
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        };

        h.State.Apply(VendorGuid, Profile(), items);

        Assert.True(h.Window.IsVisible);
        Assert.True(h.ItemsPage.Visible);
        Assert.False(h.BuyingPage.Visible);
        Assert.False(h.SellingPage.Visible);
        Assert.Equal(1, h.ItemList.GetNumUIItems());
        Assert.Equal(ArmorItemGuid, h.ItemList.GetItem(0)!.ItemId);
        Assert.Equal("Armor", h.TypeMenu.Items.Single(i => Equals(i.Payload, h.TypeMenu.Selected)).Label);
    }

    [Fact]
    public void Closed_HidesWindowAndClearsListAndText()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        h.ItemList.GetItem(0)!.Clicked?.Invoke();
        Assert.True(h.Window.IsVisible);
        Assert.NotEqual(string.Empty, GetText(h.ItemNameText));

        h.State.Close();

        Assert.False(h.Window.IsVisible);
        Assert.Equal(0, h.ItemList.GetNumUIItems());
        Assert.Equal(string.Empty, GetText(h.ItemNameText));
        Assert.Equal(string.Empty, GetText(h.ItemCostText));
        Assert.Empty(h.TypeMenu.Items);
    }

    [Fact]
    public void Reset_HidesWindowAndClearsContent()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });

        h.State.Reset();

        Assert.False(h.Window.IsVisible);
        Assert.Equal(0, h.ItemList.GetNumUIItems());
    }

    [Fact]
    public void SelectingItem_NotInSplitExemptMask_ShowsWholeStackPriceAndFallbackPluralName()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(sellRate: 2.0f), new[]
        {
            new VendorShopItem(
                StackedItemGuid, -1, 3u, "Arrows", (uint)ItemType.MissileWeapon, 300u, 1000,
                DescStackSize: 100),
        });

        h.ItemList.GetItem(0)!.Clicked?.Invoke();
        h.SplitQuantity.Reset(100u, initialValue: 100u);

        Assert.Equal("100 Arrows", GetText(h.ItemNameText));
        Assert.Equal(
            string.Create(
                CultureInfo.InvariantCulture,
                $"cost {2000:N0}p (you have {Harness.DefaultPlayerCoinValue:N0}p)"),
            GetText(h.ItemCostText));
    }

    [Fact]
    public void SelectingItem_InSplitExemptMask_ShowsPerUnitPriceAndSingularName()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(sellRate: 2.0f), new[]
        {
            new VendorShopItem(
                FoodItemGuid, -1, 1u, "Bread", (uint)ItemType.Food, 100u, 500,
                DescStackSize: 50),
        });

        h.ItemList.GetItem(0)!.Clicked?.Invoke();
        h.SplitQuantity.Reset(50u, initialValue: 1u);

        Assert.Equal("Bread", GetText(h.ItemNameText));
        Assert.Equal(
            string.Create(
                CultureInfo.InvariantCulture,
                $"costs {20:N0}p (you have {Harness.DefaultPlayerCoinValue:N0}p)"),
            GetText(h.ItemCostText));
    }

    [Fact]
    public void SelectingItem_WithAuthoredPluralName_UsesItInsteadOfSingular()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(
                StackedItemGuid, -1, 3u, "Iron Key", (uint)ItemType.Key, 50u, 100,
                DescStackSize: 10, PluralName: "Iron Keys"),
        });

        h.ItemList.GetItem(0)!.Clicked?.Invoke();
        // F2: Key is not split-exempt -> full authored stack (10).
        h.SplitQuantity.Reset(10u, initialValue: 10u);

        Assert.Equal("10 Iron Keys", GetText(h.ItemNameText));
    }

    [Fact]
    public void SliderChange_AfterSelection_UpdatesNameAndPriceToLiveQuantity_MatchingWhatBuyWouldCharge()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(sellRate: 2.0f), new[]
        {
            // perUnit = 1000/100 = 10 (MissileWeapon, not split-exempt).
            new VendorShopItem(
                StackedItemGuid, -1, 3u, "Arrows", (uint)ItemType.MissileWeapon, 300u, 1000,
                DescStackSize: 100),
        });
        Assert.Equal("Arrows", GetText(h.ItemNameText));
        Assert.Equal(
            string.Create(
                CultureInfo.InvariantCulture,
                $"costs {20:N0}p (you have {Harness.DefaultPlayerCoinValue:N0}p)"),
            GetText(h.ItemCostText));

        h.SplitQuantity.Reset(100u, initialValue: 40u);

        Assert.Equal("40 Arrows", GetText(h.ItemNameText));
        Assert.Equal(
            string.Create(
                CultureInfo.InvariantCulture,
                $"cost {800:N0}p (you have {Harness.DefaultPlayerCoinValue:N0}p)"),
            GetText(h.ItemCostText));

        h.BuyButton.OnClick!.Invoke();

        Assert.Equal(40, h.Buys.Single().Amount);
    }

    [Fact]
    public void SelectingItem_WithAlternateCurrency_ShowsFullRetailCostSentence()
    {
        var h = new Harness();
        h.State.Apply(
            VendorGuid,
            Profile(sellRate: 1.0f, altCurrency: 0x12345678u, altName: "Trade Notes", altAmount: 12345u),
            new[]
            {
                new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 50),
            });

        h.ItemList.GetItem(0)!.Clicked?.Invoke();

        Assert.Equal(
            "This item costs 50 Trade Notes. You have 12345 Trade Notes.",
            GetText(h.ItemCostText));
    }

    [Fact]
    public void AlternateCurrencyPurchaseUpdatesImmediatelyThenReconcilesToInventory()
    {
        var h = new Harness();
        const uint currencyWcid = 0x12345678u;
        const uint currencyGuid = 0x60000A01u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = currencyGuid,
            WeenieClassId = currencyWcid,
            Name = "Colosseum Coin",
            Type = ItemType.Misc,
            StackSize = 10,
        });
        h.Objects.MoveItem(currencyGuid, Harness.PlayerGuid, 0);
        h.State.Apply(
            VendorGuid,
            Profile(sellRate: 1.0f, altCurrency: currencyWcid, altName: "Colosseum Coins", altAmount: 10u),
            new[]
            {
                new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 2),
            });

        h.BuyButton.OnClick!.Invoke();

        Assert.Contains("You have 8 Colosseum Coins.", GetText(h.ItemCostText));
        Assert.Equal("You have 8 Colosseum Coins.", GetText(h.BuyPurseText));

        Assert.True(h.Objects.UpdateIntProperty(currencyGuid, 0x7FFFu, 1));
        Assert.Contains("You have 8 Colosseum Coins.", GetText(h.ItemCostText));

        Assert.True(h.Objects.UpdateStackSize(currencyGuid, 8, value: 0));
        Assert.Contains("You have 8 Colosseum Coins.", GetText(h.ItemCostText));
        Assert.Equal("You have 8 Colosseum Coins.", GetText(h.BuyPurseText));
    }

    [Fact]
    public void NoSelection_DisablesBuyButton_SelectionEnablesIt()
    {
        var h = new Harness();
        Assert.False(h.BuyButton.Enabled);

        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });

        // F4 auto-selects the sole item on open, so Buy is already enabled
        // — SetState(1), pc:202784-202789.
        Assert.True(h.BuyButton.Enabled);

        h.State.Close();
        Assert.False(h.BuyButton.Enabled);
    }

    [Fact]
    public void AddButton_EnablesWithSelection_NowThatStagingIsWired()
    {
        var h = new Harness();
        Assert.False(h.AddButton.Enabled);

        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });

        // F4/F6 auto-selects the sole item on open -- Add now enables too.
        Assert.True(h.AddButton.Enabled);

        h.State.Close();
        Assert.False(h.AddButton.Enabled);
    }

    [Fact]
    public void ShopItem_WithIconUnderlayOverlayEffects_ForwardsThemToResolveIcon()
    {
        var h = new Harness();
        var item = new VendorShopItem(
            ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, IconId: 200u, Value: 500,
            IconUnderlayId: 10u, IconOverlayId: 100u, Effects: 1000u);
        h.State.Apply(VendorGuid, Profile(), new[] { item });

        Assert.Equal(200u + 10u + 100u + 1000u, h.ItemList.GetItem(0)!.IconTexture);
    }

    [Fact]
    public void SelectingDifferentCategory_FiltersItemListToThatCategoryOnly()
    {
        var h = new Harness();
        var armor = new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500);
        var food = new VendorShopItem(FoodItemGuid, -1, 1u, "Bread", (uint)ItemType.Food, 100u, 5);
        h.State.Apply(VendorGuid, Profile(), new[] { armor, food });
        Assert.Equal(ArmorItemGuid, h.ItemList.GetItem(0)!.ItemId); // default: Armor (table order)

        object? foodPayload = h.TypeMenu.Items.First(i => i.Label == "Food").Payload;
        h.TypeMenu.OnSelect!.Invoke(foodPayload);

        Assert.Equal(1, h.ItemList.GetNumUIItems());
        Assert.Equal(FoodItemGuid, h.ItemList.GetItem(0)!.ItemId);
    }

    [Fact]
    public void SelectingDifferentCategory_AutoSelectsFirstFilteredItem()
    {
        var h = new Harness();
        var armor = new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500);
        var food = new VendorShopItem(FoodItemGuid, -1, 1u, "Bread", (uint)ItemType.Food, 100u, 5);
        h.State.Apply(VendorGuid, Profile(), new[] { armor, food });

        object? foodPayload = h.TypeMenu.Items.First(i => i.Label == "Food").Payload;
        h.TypeMenu.OnSelect!.Invoke(foodPayload);

        Assert.Equal(FoodItemGuid, h.ItemList.GetItem(0)!.ItemId);
        Assert.True(h.ItemList.GetItem(0)!.Selected);
        Assert.NotEqual(string.Empty, GetText(h.ItemNameText));
        Assert.NotEqual(string.Empty, GetText(h.ItemCostText));
        Assert.True(h.BuyButton.Enabled);
    }

    [Fact]
    public void SelectingDifferentCategory_ResetsListScrollToStart()
    {
        var h = new Harness();
        var armor = new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500);
        var food = new VendorShopItem(FoodItemGuid, -1, 1u, "Bread", (uint)ItemType.Food, 100u, 5);
        h.State.Apply(VendorGuid, Profile(), new[] { armor, food });

        object? foodPayload = h.TypeMenu.Items.First(i => i.Label == "Food").Payload;
        h.TypeMenu.OnSelect!.Invoke(foodPayload);

        Assert.Equal(0, h.ItemList.Scroll.ScrollY);
    }

    [Fact]
    public void ItemList_FillsVisibleEmptySlots()
    {
        var h = new Harness();

        Assert.True(h.ItemList.FillVisibleEmptySlots);
        Assert.NotNull(h.ItemList.EmptySlotFactory);
    }

    [Fact]
    public void ShopRow_NeverMintsADragPayload()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });

        UiItemSlot cell = h.ItemList.GetItem(0)!;

        Assert.NotEqual(0u, cell.ItemId); // occupied -- would otherwise be a drag source by default
        Assert.False(cell.IsDragSource);
        Assert.Null(cell.GetDragPayload());
        Assert.True(cell.HandlesClick);
    }

    [Fact]
    public void ShopRow_ClickEvent_SelectsItem_DespiteNotBeingADragSource()
    {
        const uint SecondArmorGuid = 0x60000110u;
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
            new VendorShopItem(SecondArmorGuid, -1, 4u, "Buckler", (uint)ItemType.Armor, 900u, 40),
        });
        Assert.Equal(ArmorItemGuid, h.Selection.SelectedObjectId); // auto-selected first

        UiItemSlot secondCell = h.ItemList.GetItem(1)!;
        Assert.Equal(SecondArmorGuid, secondCell.ItemId);
        Assert.False(secondCell.IsDragSource);   // never a drag source (F3)
        Assert.True(secondCell.HandlesClick);

        secondCell.OnEvent(new UiEvent(0u, secondCell, UiEventType.MouseDown));
        secondCell.OnEvent(new UiEvent(0u, secondCell, UiEventType.Click));

        Assert.Equal(SecondArmorGuid, h.Selection.SelectedObjectId);
        Assert.True(secondCell.Selected);
    }

    [Fact]
    public void Opened_WithDifferentVendor_ResetsToFirstPresentCategory_NotThePreviousVendors()
    {
        const uint OtherVendorGuid = 0x70000020u;
        const uint OtherArmorGuid = 0x60000201u;
        const uint OtherFoodGuid = 0x60000202u;

        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
            new VendorShopItem(FoodItemGuid, -1, 1u, "Bread", (uint)ItemType.Food, 100u, 5),
        });
        object? foodPayload = h.TypeMenu.Items.First(i => i.Label == "Food").Payload;
        h.TypeMenu.OnSelect!.Invoke(foodPayload);
        Assert.Equal("Food", h.TypeMenu.Items.Single(i => Equals(i.Payload, h.TypeMenu.Selected)).Label);

        h.State.Apply(OtherVendorGuid, Profile(), new[]
        {
            new VendorShopItem(OtherArmorGuid, -1, 4u, "Buckler", (uint)ItemType.Armor, 900u, 40),
            new VendorShopItem(OtherFoodGuid, -1, 5u, "Ale", (uint)ItemType.Food, 100u, 3),
        });

        Assert.Equal("Armor", h.TypeMenu.Items.Single(i => Equals(i.Payload, h.TypeMenu.Selected)).Label);
        Assert.Equal(OtherArmorGuid, h.ItemList.GetItem(0)!.ItemId);
    }

    [Fact]
    public void Refreshed_SameVendor_PreservesCategorySelection()
    {
        var h = new Harness();
        var items = new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
            new VendorShopItem(FoodItemGuid, -1, 1u, "Bread", (uint)ItemType.Food, 100u, 5),
        };
        h.State.Apply(VendorGuid, Profile(), items);
        object? foodPayload = h.TypeMenu.Items.First(i => i.Label == "Food").Payload;
        h.TypeMenu.OnSelect!.Invoke(foodPayload);

        // SAME vendor id re-approaches -> VendorStateTransitionKind.Refreshed.
        h.State.Apply(VendorGuid, Profile(), items);

        Assert.Equal("Food", h.TypeMenu.Items.Single(i => Equals(i.Payload, h.TypeMenu.Selected)).Label);
    }

    [Fact]
    public void Refreshed_SameVendor_ReselectsFirstItemUnconditionally_NotThePreviouslySelectedSurvivor()
    {
        const uint SecondArmorGuid = 0x60000110u;
        var h = new Harness();
        var items = new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
            new VendorShopItem(SecondArmorGuid, -1, 4u, "Buckler", (uint)ItemType.Armor, 900u, 40),
        };
        h.State.Apply(VendorGuid, Profile(), items);
        Assert.Equal(ArmorItemGuid, h.ItemList.GetItem(0)!.ItemId); // auto-selected first

        // Player explicitly picks the SECOND item.
        h.ItemList.GetItem(1)!.Clicked?.Invoke();
        Assert.True(h.ItemList.GetItem(1)!.Selected);

        // SAME vendor, SAME two items, SAME order -> Refreshed transition.
        h.State.Apply(VendorGuid, Profile(), items);

        Assert.True(h.ItemList.GetItem(0)!.Selected);
        Assert.False(h.ItemList.GetItem(1)!.Selected);
        Assert.Equal("Chainmail", GetText(h.ItemNameText));
    }

    [Fact]
    public void CategoryMenu_OpensAndSelectsThroughRealHitPath_UsingAuthoredPopupGeometry()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
            new VendorShopItem(FoodItemGuid, -1, 1u, "Bread", (uint)ItemType.Food, 100u, 5),
        });

        // Wiring: without these UiMenu.OnDrawOverlay early-returns (nothing
        // renders) and the button face never draws either.
        Assert.NotNull(h.TypeMenu.SpriteResolve);
        Assert.NotEqual(0u, h.TypeMenu.NormalSprite);
        Assert.NotEqual(0u, h.TypeMenu.ItemNormalSprite);
        Assert.NotEqual(0u, h.TypeMenu.ItemHighlightSprite);

        Assert.Equal(6, h.TypeMenu.RowsPerColumn);
        Assert.Equal(18f, h.TypeMenu.RowHeight);
        Assert.Equal(100f, h.TypeMenu.ColumnWidth);
        Assert.True(h.TypeMenu.PopupSizeToContent);
        Assert.True(h.TypeMenu.PopupScrollbarHideWhenDisabled);
        Assert.Equal(2 * h.TypeMenu.RowHeight + 2 * 5f, h.TypeMenu.PopupOuterHeight);
        Assert.Equal(h.TypeMenu.ColumnWidth + 2 * 5f, h.TypeMenu.PopupOuterWidth);

        // Open via the real widget event path.
        Assert.True(h.TypeMenu.OnEvent(new UiEvent(0, h.TypeMenu, UiEventType.MouseDown, 0, 10, 5)));

        const int border = 5;
        const int targetRow = 1;
        float iy = targetRow * h.TypeMenu.RowHeight + h.TypeMenu.RowHeight / 2f;
        float ly = h.TypeMenu.Height + iy + border;

        Assert.True(h.TypeMenu.OnEvent(new UiEvent(0, h.TypeMenu, UiEventType.MouseDown, 0, 10, (int)ly)));

        Assert.Equal("Food", h.TypeMenu.Items.Single(i => Equals(i.Payload, h.TypeMenu.Selected)).Label);
        Assert.Equal(FoodItemGuid, h.ItemList.GetItem(0)!.ItemId);
    }

    [Fact]
    public void CategoryMenu_OpensDownward_NotUpward_MatchingTheAuthoredAbsentAttribute5()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
            new VendorShopItem(FoodItemGuid, -1, 1u, "Bread", (uint)ItemType.Food, 100u, 5),
        });

        Assert.False(h.TypeMenu.OpenUpward);

        Assert.True(h.TypeMenu.OnEvent(new UiEvent(0, h.TypeMenu, UiEventType.MouseDown, 0, 10, 5)));   // open

        const int border = 5;
        float outerH = h.TypeMenu.PopupOuterHeight;
        const int targetRow = 1;
        float iy = targetRow * h.TypeMenu.RowHeight + h.TypeMenu.RowHeight / 2f;
        float oldUpwardLy = iy - outerH + border;
        Assert.True(h.TypeMenu.OnEvent(new UiEvent(0, h.TypeMenu, UiEventType.MouseDown, 0, 10, (int)oldUpwardLy)));

        // Selection is unchanged (still Armor, the fresh-open default) and the
        // item list was not re-scoped to Food — proving the old position no
        // longer hits the popup at all.
        Assert.Equal("Armor", h.TypeMenu.Items.Single(i => Equals(i.Payload, h.TypeMenu.Selected)).Label);
        Assert.Equal(ArmorItemGuid, h.ItemList.GetItem(0)!.ItemId);
    }

    [Fact]
    public void CategoryMenu_ArrowCapSprites_AreWiredAndDistinctForOpenVsClosed()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });

        Assert.NotEqual(0u, h.TypeMenu.ArrowCapClosedSprite);
        Assert.NotEqual(0u, h.TypeMenu.ArrowCapOpenSprite);
        Assert.NotEqual(h.TypeMenu.ArrowCapClosedSprite, h.TypeMenu.ArrowCapOpenSprite);

        Assert.Equal(h.TypeMenu.ArrowCapClosedSprite, h.TypeMenu.CurrentArrowCapSprite);
        Assert.True(h.TypeMenu.OnEvent(new UiEvent(0, h.TypeMenu, UiEventType.MouseDown, 0, 10, 5)));   // open
        Assert.Equal(h.TypeMenu.ArrowCapOpenSprite, h.TypeMenu.CurrentArrowCapSprite);
    }

    [Fact]
    public void CategoryMenu_TextIndentsAreFlushLeft_NotChatsCheckboxLedOffsets()
    {
        var h = new Harness();
        Assert.Equal(0f, h.TypeMenu.TextIndent);
        Assert.Equal(0f, h.TypeMenu.ButtonTextIndent);
    }

    [Fact]
    public void CloseButton_HidesTheWindowOnly_LeavesTheSessionOpenForARefreshInPlaceReopen()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });

        h.CloseButton.OnClick!.Invoke();

        Assert.False(h.Window.IsVisible);
        Assert.Equal(VendorGuid, h.State.VendorId);

        var kinds = new List<VendorStateTransitionKind>();
        h.State.Changed += t => kinds.Add(t.Kind);
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });

        Assert.True(h.Window.IsVisible);
        Assert.Equal([VendorStateTransitionKind.Refreshed], kinds);
    }

    [Fact]
    public void SellingTab_SwitchesPageAndStartsWithAnEmptyStagedSellList()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });

        h.SellingTab.OnClick!.Invoke();

        Assert.True(h.SellingPage.Visible);
        Assert.False(h.ItemsPage.Visible);
        Assert.False(h.BuyingPage.Visible);
        Assert.NotEmpty(h.SellingPage.Children);
        Assert.Equal(0, h.SellingList.GetNumUIItems());
    }

    [Fact]
    public void BuyingTab_SwitchesPageAndStartsWithAnEmptyStagedBuyList()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });

        h.BuyingTab.OnClick!.Invoke();

        Assert.True(h.BuyingPage.Visible);
        Assert.False(h.ItemsPage.Visible);
        Assert.NotEmpty(h.BuyingPage.Children);
        Assert.Equal(0, h.BuyingList.GetNumUIItems());
    }

    [Fact]
    public void ItemsTab_ReselectedAfterVisitingOtherTabs_ShowsBrowseListAgain()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        h.SellingTab.OnClick!.Invoke();

        h.ItemsTab.OnClick!.Invoke();

        Assert.True(h.ItemsPage.Visible);
        Assert.False(h.SellingPage.Visible);
        Assert.Equal(1, h.ItemList.GetNumUIItems());
    }

    [Fact]
    public void RightClickShopRow_SelectsAndRoutesThroughItemInteractionExamine()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
            new VendorShopItem(FoodItemGuid, -1, 1u, "Bread", (uint)ItemType.Food, 100u, 5),
        });
        UiItemSlot? foodCell = null;
        object? foodPayload = h.TypeMenu.Items.First(i => i.Label == "Food").Payload;
        h.TypeMenu.OnSelect!.Invoke(foodPayload);
        foodCell = h.ItemList.GetItem(0);
        Assert.Equal(FoodItemGuid, foodCell!.ItemId);

        foodCell.OnEvent(new UiEvent(0u, foodCell, UiEventType.RightClick));

        Assert.Equal(new[] { FoodItemGuid }, h.Examines);
        Assert.Equal("Bread", GetText(h.ItemNameText));
        Assert.True(foodCell.Selected);
    }


    [Fact]
    public void BuyButton_Press_NonStackedItem_BuysQuantityOne()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });

        h.BuyButton.OnClick!.Invoke();

        Assert.Equal(
            new[] { (VendorGuid, ArmorItemGuid, 1, 0u) },
            h.Buys);
    }

    [Fact]
    public void BuyButton_Press_StackedItem_UsesTheLiveSplitSliderQuantity()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(
                StackedItemGuid, -1, 3u, "Arrows", (uint)ItemType.MissileWeapon, 300u, 1000,
                DescStackSize: 100),
        });
        h.SplitQuantity.Reset(100u, initialValue: 25u);

        h.BuyButton.OnClick!.Invoke();

        Assert.Equal(25, h.Buys.Single().Amount);
    }

    [Fact]
    public void BuyButton_Press_UnlimitedStockItemUsingMaxStackSizeCeiling_SendsTheSliderQuantity()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(
                StackedItemGuid, -1, 3u, "Prismatic Taper", (uint)ItemType.SpellComponents, 300u, 1000,
                DescStackSize: null, MaxStackSize: 1000),
        });
        h.SplitQuantity.Reset(1000u, initialValue: 300u);

        h.BuyButton.OnClick!.Invoke();

        Assert.Equal(300, h.Buys.Single().Amount);
    }

    [Fact]
    public void BuyButton_Press_NonStackedItem_IgnoresAStaleSliderFromAPreviouslySelectedStackableItem()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        h.SplitQuantity.Reset(50u, initialValue: 30u);

        h.BuyButton.OnClick!.Invoke();

        Assert.Equal(1, h.Buys.Single().Amount);
    }

    [Fact]
    public void BuyButton_Press_AlternateCurrencyVendor_ForwardsTheVendorsTradeWcid()
    {
        var h = new Harness();
        h.State.Apply(
            VendorGuid,
            Profile(altCurrency: 0x12345678u, altName: "Trade Notes", altAmount: 500u),
            new[]
            {
                new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
            });

        h.BuyButton.OnClick!.Invoke();

        Assert.Equal(0x12345678u, h.Buys.Single().AlternateCurrencyId);
    }

    [Fact]
    public void BuyButton_DisablesTheInstantAPurchaseIsInFlight_AndReenablesOnCompletion()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        Assert.True(h.BuyButton.Enabled);

        h.BuyButton.OnClick!.Invoke();

        // TryBuy's reservation increments BusyCount synchronously, before
        // any wire response — the button must reflect that immediately,
        // with no per-frame polling (ItemInteractionController.StateChanged
        // drives RecomputeBuyButtonEnabled).
        Assert.False(h.BuyButton.Enabled);

        h.ItemInteraction.CompleteUse(0);

        Assert.True(h.BuyButton.Enabled);
    }

    [Fact]
    public void BuyButton_NoSelection_PressDoesNothing()
    {
        var h = new Harness();

        h.BuyButton.OnClick!.Invoke();

        Assert.Empty(h.Buys);
    }

    [Fact]
    public void ReconciliationRoundTrip_MoneyCreateObjectAndApproachVendorRefresh_FlowThroughExistingMachinery()
    {
        var h = new Harness();
        const uint PurchasedItemGuid = 0x60000900u;
        h.State.Apply(VendorGuid, Profile(sellRate: 1.0f), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        Assert.Equal(1, h.ItemList.GetNumUIItems());
        Assert.True(h.BuyButton.Enabled);

        h.BuyButton.OnClick!.Invoke();
        Assert.Single(h.Buys);
        Assert.False(h.BuyButton.Enabled);

        int newCoinValue = Harness.DefaultPlayerCoinValue - 500;
        h.Objects.UpdateIntProperty(Harness.PlayerGuid, (uint)PropertyInt.CoinValue, newCoinValue);

        h.Objects.Ingest(new WeenieData(
            Guid: PurchasedItemGuid,
            Name: "Chainmail",
            Type: ItemType.Armor,
            WeenieClassId: 2u,
            IconId: 200u,
            IconOverlayId: 0u,
            IconUnderlayId: 0u,
            Effects: 0u,
            Value: 500,
            StackSize: null,
            StackSizeMax: null,
            Burden: null,
            ContainerId: Harness.PlayerGuid,
            WielderId: 0u,
            ValidLocations: null,
            CurrentWieldedLocation: null,
            Priority: null,
            ItemsCapacity: null,
            ContainersCapacity: null,
            Structure: null,
            MaxStructure: null,
            Workmanship: null));

        // (3) ApproachVendor refresh: sold out of the ONLY armor stack, so
        // the SAME vendor's next snapshot no longer lists it.
        h.State.Apply(VendorGuid, Profile(sellRate: 1.0f), System.Array.Empty<VendorShopItem>());

        h.ItemInteraction.CompleteUse(0);

        Assert.Equal(newCoinValue, h.Objects.Get(Harness.PlayerGuid)?.Properties.GetInt((uint)PropertyInt.CoinValue));
        Assert.Equal(Harness.PlayerGuid, h.Objects.Get(PurchasedItemGuid)?.ContainerId);
        Assert.Equal(0, h.ItemList.GetNumUIItems());
        Assert.Equal(string.Empty, GetText(h.ItemNameText));
        Assert.False(h.BuyButton.Enabled);
    }


    [Fact]
    public void AddToBuyList_StagesTheSelectedItem()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        // F4/F6 auto-selects the sole item on open.

        h.AddButton.OnClick!.Invoke();

        Assert.Equal(1, h.BuyingList.GetNumUIItems());
        Assert.Equal(ArmorItemGuid, h.BuyingList.GetItem(0)!.ItemId);
    }

    [Fact]
    public void AddToBuyList_ReAddingTheSameItemUpsertsRatherThanDuplicatingTheRow()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });

        h.AddButton.OnClick!.Invoke();
        h.AddButton.OnClick!.Invoke();

        Assert.Equal(1, h.BuyingList.GetNumUIItems());
    }

    [Fact]
    public void AddToBuyList_NothingSelected_IsANoOp()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), Array.Empty<VendorShopItem>());

        h.AddButton.OnClick!.Invoke();

        Assert.Equal(0, h.BuyingList.GetNumUIItems());
    }

    [Fact]
    public void AddToBuyList_ReAddingTheSameStackableItem_AccumulatesTheStagedQuantity()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(
                StackedItemGuid, -1, 3u, "Arrows", (uint)ItemType.MissileWeapon, 300u, 1000,
                DescStackSize: 100),
        });
        h.SplitQuantity.Reset(100u, initialValue: 10u);
        h.AddButton.OnClick!.Invoke();
        h.SplitQuantity.SetValue(15u);
        h.AddButton.OnClick!.Invoke();
        Assert.Equal(1, h.BuyingList.GetNumUIItems()); // one row, not two

        h.BuyAllButton.OnClick!.Invoke();

        (_, IReadOnlyList<(int Amount, uint ItemGuid)> items, _) = Assert.Single(h.BuyAlls);
        Assert.Equal(new (int Amount, uint ItemGuid)[] { (25, StackedItemGuid) }, items);
    }

    [Fact]
    public void AddToBuyList_AccumulatingPastTheCap_ShowsRetailsNoticeAndLeavesStagingUnchanged()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(
                StackedItemGuid, -1, 3u, "Arrows", (uint)ItemType.MissileWeapon, 300u, 1000,
                DescStackSize: 100, MaxStackSize: 100),
        });
        h.SplitQuantity.Reset(5000u, initialValue: 4990u);
        h.AddButton.OnClick!.Invoke();
        h.SplitQuantity.Reset(5000u, initialValue: 20u);

        h.AddButton.OnClick!.Invoke();

        Assert.Equal(new[] { VendorStagingList.TooMuchMessage }, h.SystemMessages);
        Assert.Equal(1, h.BuyingList.GetNumUIItems());
        h.Objects.Get(Harness.PlayerGuid)!.Properties.Ints[(uint)PropertyInt.CoinValue] = 10_000_000;
        h.BuyAllButton.OnClick!.Invoke();
        (_, IReadOnlyList<(int Amount, uint ItemGuid)> items, _) = Assert.Single(h.BuyAlls);
        Assert.Equal(4990, items.Single().Amount);
    }

    [Fact]
    public void StagingConsumesLimitedShopSupply_HidingTheRowWhenExhaustedAndRestoringItWhenUnstaged()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, 2, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 100),
        });
        Assert.Equal(1, h.ItemList.GetNumUIItems());

        h.AddButton.OnClick!.Invoke(); // stages 1 of 2 available
        Assert.Equal(1, h.ItemList.GetNumUIItems()); // still visible — 1 remains

        h.AddButton.OnClick!.Invoke(); // stages 2 of 2 available — exhausted

        Assert.Equal(0, h.ItemList.GetNumUIItems()); // row hidden
        Assert.Null(h.Selection.SelectedObjectId);

        h.BuyClearListButton.OnClick!.Invoke(); // un-stage everything

        Assert.Equal(1, h.ItemList.GetNumUIItems()); // row restored
        Assert.Equal(ArmorItemGuid, h.ItemList.GetItem(0)!.ItemId);
    }

    [Fact]
    public void UnlimitedSupplyShopItem_IsNeverHiddenNoMatterHowMuchIsStaged()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 100),
        });

        for (int i = 0; i < 5; i++)
            h.AddButton.OnClick!.Invoke();

        Assert.Equal(1, h.ItemList.GetNumUIItems());
    }

    [Fact]
    public void BuyAllButton_SendsOneBatchedBuyForEveryStagedEntryAndClearsStagingOnSuccess()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
            new VendorShopItem(AnotherArmorItemGuid, -1, 4u, "Helm", (uint)ItemType.Armor, 200u, 150),
        });

        h.ItemList.GetItem(0)!.Clicked?.Invoke();
        h.AddButton.OnClick!.Invoke();
        h.ItemList.GetItem(1)!.Clicked?.Invoke();
        h.AddButton.OnClick!.Invoke();
        Assert.Equal(2, h.BuyingList.GetNumUIItems());

        h.BuyAllButton.OnClick!.Invoke();

        (uint vendorGuid, IReadOnlyList<(int Amount, uint ItemGuid)> items, uint currency) =
            Assert.Single(h.BuyAlls);
        Assert.Equal(VendorGuid, vendorGuid);
        Assert.Equal(
            new (int Amount, uint ItemGuid)[] { (1, ArmorItemGuid), (1, AnotherArmorItemGuid) },
            items);
        Assert.Equal(0u, currency);
        Assert.Equal(0, h.BuyingList.GetNumUIItems());
    }

    [Fact]
    public void BuyAllButton_WithNothingStaged_IsANoOp()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), Array.Empty<VendorShopItem>());

        h.BuyAllButton.OnClick!.Invoke();

        Assert.Empty(h.BuyAlls);
    }


    [Fact]
    public void BuyAllButton_InsufficientPyrealFunds_BlocksWithRetailsNoticeAndStagingIntact()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(sellRate: 1.5f), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 2000),
        });
        h.AddButton.OnClick!.Invoke();
        Assert.Equal(1, h.BuyingList.GetNumUIItems());

        h.BuyAllButton.OnClick!.Invoke();

        Assert.Empty(h.BuyAlls);
        Assert.Equal(1, h.BuyingList.GetNumUIItems());
        Assert.Equal(new[] { "You don't have enough money" }, h.SystemMessages);
    }

    [Fact]
    public void BuyAllButton_InsufficientAltCurrency_BlocksWithRetailsNoticeAndStagingIntact()
    {
        var h = new Harness();
        h.State.Apply(
            VendorGuid,
            Profile(altCurrency: 0x12345678u, altName: "Trade Notes", altAmount: 10u),
            new[]
            {
                new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
            });
        h.AddButton.OnClick!.Invoke();

        h.BuyAllButton.OnClick!.Invoke();

        Assert.Empty(h.BuyAlls);
        Assert.Equal(1, h.BuyingList.GetNumUIItems());
        Assert.Equal(new[] { "You don't have enough money" }, h.SystemMessages);
    }

    [Fact]
    public void BuyAllButton_InsufficientContainerSlots_BlocksWithRetailsNoticeAndStagingIntact()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Birch Backpack", (uint)ItemType.Container, 200u, 100),
        });
        h.AddButton.OnClick!.Invoke();
        h.Objects.Get(Harness.PlayerGuid)!.ContainersCapacity = 0;

        h.BuyAllButton.OnClick!.Invoke();

        Assert.Empty(h.BuyAlls);
        Assert.Equal(1, h.BuyingList.GetNumUIItems());
        Assert.Equal(new[] { "You must empty some slots in your backpack first" }, h.SystemMessages);
    }

    [Fact]
    public void BuyAllButton_InsufficientItemSlots_BlocksWithRetailsNoticeAndStagingIntact()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        h.AddButton.OnClick!.Invoke();
        h.Objects.Get(Harness.PlayerGuid)!.ItemsCapacity = 0;

        h.BuyAllButton.OnClick!.Invoke();

        Assert.Empty(h.BuyAlls);
        Assert.Equal(1, h.BuyingList.GetNumUIItems());
        Assert.Equal(new[] { "You must empty some slots in your backpack first" }, h.SystemMessages);
    }

    [Fact]
    public void BuyAllButton_StrayCapacityFieldOnNonContainerItem_DoesNotFalseBlockWithFreeSlots()
    {
        var h = new Harness();
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x60002001u,
            Type = ItemType.Armor,
            ItemsCapacity = 3,
        });
        h.Objects.InitializeInventoryManifest(Harness.PlayerGuid, new[]
        {
            new ContainerContentEntry(0x60002001u, 0u),
        });
        h.Objects.Get(Harness.PlayerGuid)!.ContainersCapacity = 1;

        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Birch Backpack", (uint)ItemType.Container, 200u, 100),
        });
        h.AddButton.OnClick!.Invoke();

        h.BuyAllButton.OnClick!.Invoke();

        Assert.Single(h.BuyAlls);
        Assert.Empty(h.SystemMessages);
    }

    [Fact]
    public void BuyAllButton_HintOnlyContainer_StillCountsAgainstContainerCapacity()
    {
        var h = new Harness();
        h.Objects.InitializeInventoryManifest(Harness.PlayerGuid, new[]
        {
            new ContainerContentEntry(0x60002010u, 1u),
        });

        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Birch Backpack", (uint)ItemType.Container, 200u, 100),
        });
        h.AddButton.OnClick!.Invoke();
        h.Objects.Get(Harness.PlayerGuid)!.ContainersCapacity = 1;

        h.BuyAllButton.OnClick!.Invoke();

        Assert.Empty(h.BuyAlls);
        Assert.Equal(new[] { "You must empty some slots in your backpack first" }, h.SystemMessages);
    }

    [Fact]
    public void BuyItemButton_BuysTheSelectedStagedItemAndRemovesItFromStagingOnSuccess()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        h.AddButton.OnClick!.Invoke();
        Assert.Equal(1, h.BuyingList.GetNumUIItems());

        h.BuyItemButton.OnClick!.Invoke();

        Assert.Equal(new[] { (VendorGuid, ArmorItemGuid, 1, 0u) }, h.Buys);
        Assert.Equal(0, h.BuyingList.GetNumUIItems());
    }

    [Fact]
    public void BuyClearItemButton_RemovesOnlyTheSelectedStagedEntryWithoutBuying()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
            new VendorShopItem(AnotherArmorItemGuid, -1, 4u, "Helm", (uint)ItemType.Armor, 200u, 150),
        });
        h.ItemList.GetItem(0)!.Clicked?.Invoke();
        h.AddButton.OnClick!.Invoke();
        h.ItemList.GetItem(1)!.Clicked?.Invoke();
        h.AddButton.OnClick!.Invoke();
        Assert.Equal(2, h.BuyingList.GetNumUIItems());

        h.BuyClearItemButton.OnClick!.Invoke();

        Assert.Equal(1, h.BuyingList.GetNumUIItems());
        Assert.Equal(ArmorItemGuid, h.BuyingList.GetItem(0)!.ItemId);
        Assert.Empty(h.Buys);
    }

    [Fact]
    public void BuyClearListButton_ClearsEveryStagedEntryWithoutBuying()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        h.AddButton.OnClick!.Invoke();

        h.BuyClearListButton.OnClick!.Invoke();

        Assert.Equal(0, h.BuyingList.GetNumUIItems());
        Assert.Empty(h.Buys);
    }

    [Fact]
    public void DoubleClickStagedBuyingRow_RemovesOneUnitAndReportsRetailsNotice()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(
                StackedItemGuid, -1, 3u, "Arrows", (uint)ItemType.MissileWeapon, 300u, 1000,
                DescStackSize: 100),
        });
        h.SplitQuantity.Reset(100u, initialValue: 3u);
        h.AddButton.OnClick!.Invoke();

        h.BuyingList.GetItem(0)!.DoubleClicked!.Invoke();

        Assert.Equal(1, h.BuyingList.GetNumUIItems());
        Assert.Equal(StackedItemGuid, h.Selection.SelectedObjectId);
        Assert.Equal(new[] { "Removing Arrows from shopping list" }, h.SystemMessages);
        h.BuyAllButton.OnClick!.Invoke();
        (_, IReadOnlyList<(int Amount, uint ItemGuid)> items, _) = Assert.Single(h.BuyAlls);
        Assert.Equal(new[] { (2, StackedItemGuid) }, items);
    }

    [Fact]
    public void SelectingADifferentStagedBuyingRow_RepaintsBothRowsHighlight()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
            new VendorShopItem(AnotherArmorItemGuid, -1, 4u, "Helm", (uint)ItemType.Armor, 200u, 150),
        });
        h.ItemList.GetItem(0)!.Clicked?.Invoke();
        h.AddButton.OnClick!.Invoke();
        h.ItemList.GetItem(1)!.Clicked?.Invoke();
        h.AddButton.OnClick!.Invoke();
        Assert.Equal(2, h.BuyingList.GetNumUIItems());

        UiItemSlot firstRow = h.BuyingList.GetItem(0)!;
        UiItemSlot secondRow = h.BuyingList.GetItem(1)!;
        firstRow.Clicked?.Invoke();

        Assert.Equal(firstRow.ItemId, h.Selection.SelectedObjectId);
        Assert.True(firstRow.Selected);
        Assert.False(secondRow.Selected);
    }


    private static void MakePlayerOwned(
        Harness h,
        uint guid,
        ItemType type,
        int value,
        int stackSize = 1,
        uint weenieClassId = 0u)
    {
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = guid,
            Name = $"Item {guid:X8}",
            WeenieClassId = weenieClassId,
            Type = type,
            Value = value,
            StackSize = stackSize,
        });
        h.Objects.MoveItem(guid, Harness.PlayerGuid, h.Objects.GetContents(Harness.PlayerGuid).Count);
    }

    private static ItemDragPayload DragFromInventory(uint guid) =>
        new(guid, ItemDragSource.Inventory, 0, new UiItemSlot());

    [Fact]
    public void OnDragOver_TargetIsNotTheSellingList_RejectsRegardlessOfAcceptability()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedArmorGuid, ItemType.Armor, 100);

        ItemDragAcceptance result = h.Controller.OnDragOver(
            h.ItemList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid));

        Assert.Equal(ItemDragAcceptance.Reject, result);
    }

    [Fact]
    public void OnDragOver_AcceptableItemOverSellingList_Accepts()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedArmorGuid, ItemType.Armor, 100);

        ItemDragAcceptance result = h.Controller.OnDragOver(
            h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid));

        Assert.Equal(ItemDragAcceptance.Accept, result);
    }

    [Fact]
    public void OnDragOver_UnacceptableItemOverSellingList_RejectsSilently()
    {
        var h = new Harness();
        // Vendor only deals in Armor -- a Weapon is a type mismatch.
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedWeaponGuid, ItemType.Weapon, 100);

        ItemDragAcceptance result = h.Controller.OnDragOver(
            h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedWeaponGuid));

        Assert.Equal(ItemDragAcceptance.Reject, result);
        // silent=1 on hover -- no rejection string yet (VendorSellUI::
        // OnItemListDragOver, pc:201320-201339).
        Assert.Empty(h.SystemMessages);
    }

    [Fact]
    public void DragOverTheVendorWindow_ThroughTheRealPointerPipeline_AutoSwitchesToSellingAndStages()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedArmorGuid, ItemType.Armor, 100);

        h.SellingList.Width = 32f;
        h.SellingList.Height = 32f;
        using (h.SellingList.DeferLayout()) { }

        var sourceCell = new UiItemSlot { Left = 700f, Top = 550f, Width = 32f, Height = 32f };
        sourceCell.SetItem(PlayerOwnedArmorGuid, 0u);
        h.Screen.AddChild(sourceCell);

        Assert.True(h.ItemsPage.Visible);
        Assert.False(h.SellingPage.Visible);

        h.Screen.OnMouseDown(UiMouseButton.Left, 710, 560);
        h.Screen.OnMouseMove(400, 50);
        Assert.Same(sourceCell, h.Screen.DragSource);
        Assert.False(h.SellingPage.Visible);

        h.Screen.Tick(0.016, 1L);

        Assert.True(h.SellingPage.Visible);
        Assert.False(h.ItemsPage.Visible);

        h.Screen.OnMouseMove(20, 15);
        h.Screen.OnMouseUp(UiMouseButton.Left, 20, 15);

        Assert.Null(h.Screen.DragSource);
        Assert.Equal(1, h.SellingList.GetNumUIItems());
        Assert.Equal(PlayerOwnedArmorGuid, h.SellingList.GetItem(0)!.ItemId);
        Assert.Equal(PlayerOwnedArmorGuid, h.Selection.SelectedObjectId);
        Assert.Empty(h.SystemMessages);
    }

    [Fact]
    public void HandleDropRelease_AcceptableItem_StagesItSwitchesToSellingTabAndSelectsIt()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedArmorGuid, ItemType.Armor, 100);

        h.Controller.HandleDropRelease(
            h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid));

        Assert.Equal(1, h.SellingList.GetNumUIItems());
        Assert.Equal(PlayerOwnedArmorGuid, h.SellingList.GetItem(0)!.ItemId);
        Assert.True(h.SellingPage.Visible);
        Assert.False(h.ItemsPage.Visible);
        Assert.Equal(PlayerOwnedArmorGuid, h.Selection.SelectedObjectId);
        Assert.Empty(h.SystemMessages);
    }

    [Fact]
    public void HandleDropRelease_PartialStack_SplitsThenStagesTheNewExactStack()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.MissileWeapon), Array.Empty<VendorShopItem>());
        const uint wcid = 0x2345u;
        const uint splitGuid = 0x60000222u;
        MakePlayerOwned(
            h,
            PlayerOwnedWeaponGuid,
            ItemType.MissileWeapon,
            100,
            stackSize: 20,
            weenieClassId: wcid);
        h.Selection.Select(PlayerOwnedWeaponGuid, SelectionChangeSource.Vendor);
        h.SplitQuantity.Reset(20u, initialValue: 5u);

        h.Controller.HandleDropRelease(
            h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedWeaponGuid));

        Assert.Equal(
            new[] { (PlayerOwnedWeaponGuid, Harness.PlayerGuid, 0u, 5u) },
            h.SplitPuts);
        Assert.Equal(PlayerOwnedWeaponGuid, h.SellingList.GetItem(0)!.ItemId);
        Assert.Equal(
            new[] { "Splitting the Item 60000202 before selling them" },
            h.SystemMessages);

        Assert.True(h.Objects.UpdateStackSize(PlayerOwnedWeaponGuid, 15, value: 75));
        MakePlayerOwned(
            h,
            splitGuid,
            ItemType.MissileWeapon,
            25,
            stackSize: 5,
            weenieClassId: wcid);
        Assert.Equal(splitGuid, h.SellingList.GetItem(0)!.ItemId);

        h.SellAllButton.OnClick!.Invoke();

        (_, IReadOnlyList<(int Amount, uint ItemGuid)> items) = Assert.Single(h.Sells);
        Assert.Equal(new (int Amount, uint ItemGuid)[] { (5, splitGuid) }, items);
    }

    [Fact]
    public void HandleDropRelease_PartialStackFailure_RemovesTheTemporarySellRow()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.MissileWeapon), Array.Empty<VendorShopItem>());
        MakePlayerOwned(
            h,
            PlayerOwnedWeaponGuid,
            ItemType.MissileWeapon,
            100,
            stackSize: 10,
            weenieClassId: 0x2345u);
        h.Selection.Select(PlayerOwnedWeaponGuid, SelectionChangeSource.Vendor);
        h.SplitQuantity.Reset(10u, initialValue: 2u);

        h.Controller.HandleDropRelease(
            h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedWeaponGuid));
        Assert.Equal(1, h.SellingList.GetNumUIItems());

        h.Objects.RejectMove(PlayerOwnedWeaponGuid, weenieError: 0x29u);

        Assert.Equal(0, h.SellingList.GetNumUIItems());
        Assert.Empty(h.Sells);
    }

    [Fact]
    public void HandleDropRelease_WrongTargetList_IsIgnored()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedArmorGuid, ItemType.Armor, 100);

        h.Controller.HandleDropRelease(
            h.ItemList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid));

        Assert.Equal(0, h.SellingList.GetNumUIItems());
    }

    [Fact]
    public void HandleDropRelease_UnacceptableType_ShowsTheGenericRejectionMessageAndDoesNotStage()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedWeaponGuid, ItemType.Weapon, 100);

        h.Controller.HandleDropRelease(
            h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedWeaponGuid));

        Assert.Equal(0, h.SellingList.GetNumUIItems());
        Assert.Equal(new[] { "You cannot sell that here" }, h.SystemMessages);
    }

    [Fact]
    public void HandleDropRelease_NoValueItem_ShowsTheNoValueRejectionMessage()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedArmorGuid, ItemType.Armor, value: 0);

        h.Controller.HandleDropRelease(
            h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid));

        Assert.Equal(0, h.SellingList.GetNumUIItems());
        Assert.Equal(new[] { "That item has no value and cannot be sold" }, h.SystemMessages);
    }

    [Fact]
    public void HandleDropRelease_NotOwnedByPlayer_ShowsTheOwnershipRejectionMessage()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = PlayerOwnedArmorGuid,
            Name = "Someone else's chainmail",
            Type = ItemType.Armor,
            Value = 100,
            StackSize = 1,
        });

        h.Controller.HandleDropRelease(
            h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid));

        Assert.Equal(0, h.SellingList.GetNumUIItems());
        Assert.Equal(new[] { "You can only sell items you are carrying" }, h.SystemMessages);
    }

    [Fact]
    public void HandleDropRelease_RetainedItem_RejectsEvenWhenTheTypeMaskMatches()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = PlayerOwnedArmorGuid,
            Name = "Heirloom Chainmail",
            Type = ItemType.Armor,
            Value = 100,
            StackSize = 1,
            PublicWeenieBitfield = (uint)PublicWeenieFlags.Retained,
        });
        h.Objects.MoveItem(PlayerOwnedArmorGuid, Harness.PlayerGuid, 0);

        h.Controller.HandleDropRelease(
            h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid));

        Assert.Equal(0, h.SellingList.GetNumUIItems());
        Assert.Equal(new[] { "You cannot sell that here" }, h.SystemMessages);
    }

    [Fact]
    public void SellAllButton_SendsOneBatchedSellForEveryStagedEntryAndClearsStagingOnSuccess()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedArmorGuid, ItemType.Armor, 100);
        MakePlayerOwned(h, PlayerOwnedArmorGuid2, ItemType.Armor, 100);
        h.Controller.HandleDropRelease(h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid));
        h.Controller.HandleDropRelease(h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid2));
        Assert.Equal(2, h.SellingList.GetNumUIItems());

        h.SellAllButton.OnClick!.Invoke();

        (uint vendorGuid, IReadOnlyList<(int Amount, uint ItemGuid)> items) = Assert.Single(h.Sells);
        Assert.Equal(VendorGuid, vendorGuid);
        Assert.Equal(
            new (int Amount, uint ItemGuid)[] { (1, PlayerOwnedArmorGuid), (1, PlayerOwnedArmorGuid2) },
            items);
        Assert.Equal(0, h.SellingList.GetNumUIItems());
    }

    [Fact]
    public void SellAllButton_WithNothingStaged_IsANoOp()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());

        h.SellAllButton.OnClick!.Invoke();

        Assert.Empty(h.Sells);
    }

    [Fact]
    public void SellItemButton_SellsTheSelectedStagedItemAndRemovesItUnconditionallyOnSuccess()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedArmorGuid, ItemType.Armor, 100);
        h.Controller.HandleDropRelease(h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid));
        Assert.Equal(1, h.SellingList.GetNumUIItems());

        h.SellItemButton.OnClick!.Invoke();

        (uint vendorGuid, IReadOnlyList<(int Amount, uint ItemGuid)> items) = Assert.Single(h.Sells);
        Assert.Equal(VendorGuid, vendorGuid);
        Assert.Equal(new (int Amount, uint ItemGuid)[] { (1, PlayerOwnedArmorGuid) }, items);
        Assert.Equal(0, h.SellingList.GetNumUIItems());
    }

    [Fact]
    public void SellItemButton_ActsOnTheGlobalSelectionEvenWhenNeverStaged()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedArmorGuid, ItemType.Armor, 100);
        h.Selection.Select(PlayerOwnedArmorGuid, SelectionChangeSource.Vendor);
        Assert.Equal(0, h.SellingList.GetNumUIItems()); // never staged/dropped

        h.SellItemButton.OnClick!.Invoke();

        (uint vendorGuid, IReadOnlyList<(int Amount, uint ItemGuid)> items) = Assert.Single(h.Sells);
        Assert.Equal(VendorGuid, vendorGuid);
        Assert.Equal(new (int Amount, uint ItemGuid)[] { (1, PlayerOwnedArmorGuid) }, items);
    }

    [Fact]
    public void SellItemButton_PartialStackSelected_RefusesWithRetailsNoticeAndSendsNothing()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.MissileWeapon), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedWeaponGuid, ItemType.MissileWeapon, 100, stackSize: 20);
        h.Selection.Select(PlayerOwnedWeaponGuid, SelectionChangeSource.Vendor);
        h.SplitQuantity.Reset(20u, initialValue: 5u); // a PARTIAL amount, not the full stack of 20

        h.SellItemButton.OnClick!.Invoke();

        Assert.Empty(h.Sells);
        Assert.Equal(new[] { "Cannot sell part of a stack" }, h.SystemMessages);
    }

    [Fact]
    public void SellItemButton_FullStackSelected_SendsLiteralAmountOneNotTheStackSize()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.MissileWeapon), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedWeaponGuid, ItemType.MissileWeapon, 100, stackSize: 20);
        h.Selection.Select(PlayerOwnedWeaponGuid, SelectionChangeSource.Vendor);
        h.SplitQuantity.Reset(20u, initialValue: 20u); // the FULL stack

        h.SellItemButton.OnClick!.Invoke();

        (_, IReadOnlyList<(int Amount, uint ItemGuid)> items) = Assert.Single(h.Sells);
        Assert.Equal(new (int Amount, uint ItemGuid)[] { (1, PlayerOwnedWeaponGuid) }, items);
    }

    [Fact]
    public void SellClearItemButton_RemovesOnlyTheSelectedStagedEntryWithoutSelling()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedArmorGuid, ItemType.Armor, 100);
        MakePlayerOwned(h, PlayerOwnedArmorGuid2, ItemType.Armor, 100);
        h.Controller.HandleDropRelease(h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid));
        h.Controller.HandleDropRelease(h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid2));
        Assert.Equal(2, h.SellingList.GetNumUIItems());

        h.SellClearItemButton.OnClick!.Invoke();

        Assert.Equal(1, h.SellingList.GetNumUIItems());
        Assert.Equal(PlayerOwnedArmorGuid, h.SellingList.GetItem(0)!.ItemId);
        Assert.Empty(h.Sells);
    }

    [Fact]
    public void SellClearListButton_ClearsEveryStagedEntryWithoutSelling()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedArmorGuid, ItemType.Armor, 100);
        h.Controller.HandleDropRelease(h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid));

        h.SellClearListButton.OnClick!.Invoke();

        Assert.Equal(0, h.SellingList.GetNumUIItems());
        Assert.Empty(h.Sells);
    }

    [Fact]
    public void DoubleClickStagedSellingRow_RemovesTheEntryAndReportsRetailsNotice()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedArmorGuid, ItemType.Armor, 100);
        h.Controller.HandleDropRelease(
            h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid));

        h.SellingList.GetItem(0)!.DoubleClicked!.Invoke();

        Assert.Equal(0, h.SellingList.GetNumUIItems());
        Assert.Equal(PlayerOwnedArmorGuid, h.Selection.SelectedObjectId);
        Assert.Equal(
            new[] { "Removing Item 60000201 from shopping list" },
            h.SystemMessages);
        Assert.Empty(h.Sells);
    }

    [Fact]
    public void DragStagedSellingRow_RemovesItAndPartialSelectionPrintsExactRefusalThenResets()
    {
        var h = new Harness();
        h.State.Apply(
            VendorGuid,
            SellProfile((uint)ItemType.MissileWeapon),
            Array.Empty<VendorShopItem>());
        MakePlayerOwned(
            h,
            PlayerOwnedWeaponGuid,
            ItemType.MissileWeapon,
            100,
            stackSize: 10);
        h.Controller.HandleDropRelease(
            h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedWeaponGuid));
        h.SplitQuantity.Reset(10u, initialValue: 2u);
        UiItemSlot staged = h.SellingList.GetItem(0)!;

        h.Controller.OnDragLift(
            h.SellingList,
            staged,
            new ItemDragPayload(
                PlayerOwnedWeaponGuid,
                ItemDragSource.Inventory,
                staged.SlotIndex,
                staged));

        Assert.Equal(0, h.SellingList.GetNumUIItems());
        Assert.Equal(
            new[] { "You cannot split items from this panel" },
            h.SystemMessages);
        Assert.Equal(10u, h.SplitQuantity.Value);
        Assert.Equal(10u, h.SplitQuantity.Maximum);
    }

    [Fact]
    public void RightClickStagedBuyingAndSellingRows_SelectsAndExaminesBoth()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), new[]
        {
            new VendorShopItem(
                ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        h.AddButton.OnClick!.Invoke();
        MakePlayerOwned(h, PlayerOwnedArmorGuid, ItemType.Armor, 100);
        h.Controller.HandleDropRelease(
            h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid));

        UiItemSlot buying = h.BuyingList.GetItem(0)!;
        buying.OnEvent(new UiEvent(0u, buying, UiEventType.RightClick));
        UiItemSlot selling = h.SellingList.GetItem(0)!;
        selling.OnEvent(new UiEvent(0u, selling, UiEventType.RightClick));

        Assert.Equal(new[] { ArmorItemGuid, PlayerOwnedArmorGuid }, h.Examines);
        Assert.Equal(PlayerOwnedArmorGuid, h.Selection.SelectedObjectId);
    }


    [Fact]
    public void RemovingAStagedSellItemFromClientObjectTable_SilentlyUnstagesIt()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedArmorGuid, ItemType.Armor, 100);
        h.Controller.HandleDropRelease(h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid));
        Assert.Equal(1, h.SellingList.GetNumUIItems());

        h.Objects.Remove(PlayerOwnedArmorGuid);

        Assert.Equal(0, h.SellingList.GetNumUIItems());
        Assert.Empty(h.SystemMessages);
    }

    [Fact]
    public void ShopItemLeavingClientObjectTable_UnstagesTheBuyEntryWithRetailsNotice()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ArmorItemGuid,
            Name = "Chainmail",
            Type = ItemType.Armor,
            ContainerId = VendorGuid,
        });
        h.AddButton.OnClick!.Invoke();
        Assert.Equal(1, h.BuyingList.GetNumUIItems());

        h.Objects.Remove(ArmorItemGuid);

        Assert.Equal(0, h.BuyingList.GetNumUIItems());
        Assert.Equal(new[] { "Removing Chainmail from shopping list" }, h.SystemMessages);
    }


    [Fact]
    public void CloseButtonPressed_WithNoStaging_HidesImmediatelyWithoutADialog()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        Assert.True(h.Window.IsVisible);

        h.CloseButton.OnClick!.Invoke();

        Assert.False(h.Window.IsVisible);
        Assert.False(h.Dialogs.IsOpen);
    }

    [Fact]
    public void CloseButtonPressed_WithStagedBuyItems_ShowsConfirmDialogInsteadOfHidingImmediately()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        h.AddButton.OnClick!.Invoke();

        h.CloseButton.OnClick!.Invoke();

        Assert.True(h.Window.IsVisible);
        Assert.True(h.Dialogs.IsOpen);
        Assert.NotNull(h.ShownDialog);
        Assert.Equal(
            "You have not completed all transactions. Are you sure you want to leave this vendor?",
            string.Join(" ", Assert.IsType<UiText>(h.ShownDialog!.FindElement(
                RetailConfirmationDialogView.MessageElementId)).LinesProvider().Select(static line => line.Text)));
    }

    [Fact]
    public void CloseConfirmDialog_Accepted_HidesTheWindowAndLeavesStagingIntact()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        h.AddButton.OnClick!.Invoke();
        h.CloseButton.OnClick!.Invoke();

        Assert.IsType<UiButton>(h.ShownDialog!.FindElement(
            RetailConfirmationDialogView.AcceptButtonId)).OnClick!();

        Assert.False(h.Window.IsVisible);
        Assert.False(h.Dialogs.IsOpen);
        Assert.Equal(1, h.BuyingList.GetNumUIItems());
    }

    [Fact]
    public void CloseConfirmDialog_Rejected_KeepsTheWindowOpenAndStagingIntact()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        h.AddButton.OnClick!.Invoke();
        h.CloseButton.OnClick!.Invoke();

        Assert.IsType<UiButton>(h.ShownDialog!.FindElement(
            RetailConfirmationDialogView.RejectButtonId)).OnClick!();

        Assert.True(h.Window.IsVisible);
        Assert.False(h.Dialogs.IsOpen);
        Assert.Equal(1, h.BuyingList.GetNumUIItems());
    }

    [Fact]
    public void CloseButtonPressed_WhileAConfirmationIsAlreadyUp_DoesNotOpenASecondOne()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        h.AddButton.OnClick!.Invoke();
        h.CloseButton.OnClick!.Invoke();
        Assert.Equal(1, h.Dialogs.ActiveCount);

        h.CloseButton.OnClick!.Invoke();

        Assert.Equal(1, h.Dialogs.ActiveCount);
    }

    [Fact]
    public void SessionClose_ClearsBothStagingLists()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        h.AddButton.OnClick!.Invoke();
        MakePlayerOwned(h, PlayerOwnedArmorGuid, ItemType.Armor, 100);
        h.Controller.HandleDropRelease(h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid));
        Assert.Equal(1, h.BuyingList.GetNumUIItems());
        Assert.Equal(1, h.SellingList.GetNumUIItems());

        h.State.Close();

        Assert.Equal(0, h.BuyingList.GetNumUIItems());
        Assert.Equal(0, h.SellingList.GetNumUIItems());
    }

    [Fact]
    public void SessionReset_ClearsBothStagingLists()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        h.AddButton.OnClick!.Invoke();

        h.State.Reset();

        Assert.Equal(0, h.BuyingList.GetNumUIItems());
    }

    [Fact]
    public void OpeningADifferentVendor_ClearsStaleStagingFromThePreviousVendor()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });
        h.AddButton.OnClick!.Invoke();
        Assert.Equal(1, h.BuyingList.GetNumUIItems());

        const uint otherVendor = 0x70000099u;
        h.State.Apply(otherVendor, Profile(), new[]
        {
            new VendorShopItem(FoodItemGuid, -1, 1u, "Bread", (uint)ItemType.Food, 100u, 5),
        });

        Assert.Equal(0, h.BuyingList.GetNumUIItems());
    }

    [Fact]
    public void RefreshedTransition_SameVendor_DoesNotClearAnUntouchedStagingList()
    {
        var h = new Harness();
        var items = new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        };
        h.State.Apply(VendorGuid, Profile(), items);
        h.AddButton.OnClick!.Invoke();
        Assert.Equal(1, h.BuyingList.GetNumUIItems());

        // Same vendor id re-approaching -- sameVendor==1, a Refreshed
        // transition (e.g. post buy/sell ApproachVendor refresh).
        h.State.Apply(VendorGuid, Profile(), items);

        Assert.Equal(1, h.BuyingList.GetNumUIItems());
    }


    [Fact]
    public void BuyingTabSummaryText_TracksStagedCountValueAndPlayerPurse()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(ArmorItemGuid, -1, 2u, "Chainmail", (uint)ItemType.Armor, 200u, 500),
        });

        Assert.Equal("Buying 0 items worth 0p", GetText(h.BuyListText));
        Assert.Equal(
            string.Create(
                CultureInfo.InvariantCulture,
                $"You have {Harness.DefaultPlayerCoinValue:N0}p"),
            GetText(h.BuyPurseText));

        h.AddButton.OnClick!.Invoke();

        Assert.Equal("Buying 1 item worth 750p", GetText(h.BuyListText));
        Assert.Equal(
            string.Create(
                CultureInfo.InvariantCulture,
                $"You have {Harness.DefaultPlayerCoinValue:N0}p"),
            GetText(h.BuyPurseText));

        h.BuyClearListButton.OnClick!.Invoke();

        Assert.Equal("Buying 0 items worth 0p", GetText(h.BuyListText));
    }

    [Fact]
    public void BuyingTabPurseText_UpdatesOnAPlayerMoneyChangeAloneWithNoStagingChange()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), Array.Empty<VendorShopItem>());
        Assert.Equal(
            string.Create(
                CultureInfo.InvariantCulture,
                $"You have {Harness.DefaultPlayerCoinValue:N0}p"),
            GetText(h.BuyPurseText));

        var bundle = new PropertyBundle();
        bundle.Ints[(uint)PropertyInt.CoinValue] = 42;
        h.Objects.UpsertProperties(Harness.PlayerGuid, bundle);

        Assert.Equal("You have 42p", GetText(h.BuyPurseText));
        // Selling's purse text shares the SAME live holding read.
        Assert.Equal("You have 42p", GetText(h.SellPurseText));
    }

    [Fact]
    public void SellingTabSummaryText_TracksStagedCountValueAndPlayerPurse()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, SellProfile((uint)ItemType.Armor), Array.Empty<VendorShopItem>());
        MakePlayerOwned(h, PlayerOwnedArmorGuid, ItemType.Armor, 100);

        Assert.Equal("Selling 0 items worth 0p", GetText(h.SellListText));
        Assert.Equal(
            string.Create(
                CultureInfo.InvariantCulture,
                $"You have {Harness.DefaultPlayerCoinValue:N0}p"),
            GetText(h.SellPurseText));

        h.Controller.HandleDropRelease(
            h.SellingList, new UiItemSlot(), DragFromInventory(PlayerOwnedArmorGuid));

        Assert.Equal("Selling 1 item worth 100p", GetText(h.SellListText));
        Assert.Equal(
            string.Create(
                CultureInfo.InvariantCulture,
                $"You have {Harness.DefaultPlayerCoinValue:N0}p"),
            GetText(h.SellPurseText));

        h.SellClearListButton.OnClick!.Invoke();

        Assert.Equal("Selling 0 items worth 0p", GetText(h.SellListText));
    }

    [Fact]
    public void BuyingTabSummaryText_CountsStagedQuantityNotRowCountForAStackedItem()
    {
        var h = new Harness();
        h.State.Apply(VendorGuid, Profile(), new[]
        {
            new VendorShopItem(
                StackedItemGuid, -1, 3u, "Prismatic Taper", (uint)ItemType.SpellComponents, 300u, 1000,
                DescStackSize: 100),
        });
        h.SplitQuantity.Reset(100u, initialValue: 2u);

        h.AddButton.OnClick!.Invoke();

        Assert.Equal(1, h.BuyingList.GetNumUIItems());
        Assert.Equal("Buying 2 items worth 30p", GetText(h.BuyListText));
    }
}
