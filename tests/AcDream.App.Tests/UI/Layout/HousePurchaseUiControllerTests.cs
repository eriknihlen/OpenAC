using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.UI.Layout;

public sealed class HousePurchaseUiControllerTests
{
    private const uint Player = 0x50000001u;
    private const uint Slumlord = 0x70000100u;
    private sealed class Element : UiElement { }

    private sealed class Harness : IDisposable
    {
        public ClientObjectTable Objects { get; } = new();
        public RuntimeHousePurchaseState State { get; }
        public UiButton BuyTab { get; } = new(new ElementInfo { Id = HousePurchaseUiController.BuyTabId, Type = 1 }, _ => (0u, 0, 0));
        public UiButton RentTab { get; } = new(new ElementInfo { Id = HousePurchaseUiController.MaintenanceTabId, Type = 1 }, _ => (0u, 0, 0));
        public UiElement BuyPage { get; } = new Element();
        public UiElement RentPage { get; } = new Element();
        public UiText BuyText { get; } = new();
        public UiText RentText { get; } = new();
        public UiText BuyOwner { get; } = new();
        public UiItemList BuyList { get; } = new() { Width = 555, Height = 32 };
        public UiItemList RentList { get; } = new() { Width = 555, Height = 32 };
        public UiButton BuyButton { get; } = new(new ElementInfo { Id = HousePurchaseUiController.BuyButtonId, Type = 1 }, _ => (0u, 0, 0));
        public UiButton RentButton { get; } = new(new ElementInfo { Id = HousePurchaseUiController.MaintenanceButtonId, Type = 1 }, _ => (0u, 0, 0));
        public UiButton Close { get; } = new(new ElementInfo { Id = HousePurchaseUiController.CloseButtonId, Type = 1 }, _ => (0u, 0, 0));
        public HousePurchaseUiController Controller { get; }
        public bool Visible;
        public List<string> Reports { get; } = [];
        public List<string> Sent { get; } = [];
        public List<string> Questions { get; } = [];
        public Action<bool>? Answer;
        public Action? Yes => Answer is { } answer ? () => answer(true) : null;
        public Action? No => Answer is { } answer ? () => answer(false) : null;

        public Harness()
        {
            Objects.AddOrUpdate(new ClientObject { ObjectId = Player, Type = ItemType.Creature });
            State = new RuntimeHousePurchaseState(Objects, () => Player);
            var root = new Element();
            foreach (UiElement e in new UiElement[]
                     { BuyTab, RentTab, BuyPage, RentPage, BuyText, RentText, BuyOwner,
                       BuyList, RentList, BuyButton, RentButton, Close })
                root.AddChild(e);
            var layout = new ImportedLayout(root, new()
            {
                [HousePurchaseUiController.BuyTabId] = BuyTab,
                [HousePurchaseUiController.MaintenanceTabId] = RentTab,
                [HousePurchaseUiController.BuyPageId] = BuyPage,
                [HousePurchaseUiController.MaintenancePageId] = RentPage,
                [HousePurchaseUiController.BuyRequirementsTextId] = BuyText,
                [HousePurchaseUiController.MaintenanceRequirementsTextId] = RentText,
                [HousePurchaseUiController.BuyOwnerId] = BuyOwner,
                [HousePurchaseUiController.BuyItemsListId] = BuyList,
                [HousePurchaseUiController.MaintenanceItemsListId] = RentList,
                [HousePurchaseUiController.BuyButtonId] = BuyButton,
                [HousePurchaseUiController.MaintenanceButtonId] = RentButton,
                [HousePurchaseUiController.CloseButtonId] = Close,
            });
            Controller = HousePurchaseUiController.Bind(layout, new(
                Purchase: State.View,
                Objects: Objects,
                Stage: State.Stage,
                Unstage: State.Unstage,
                ClearStaged: State.ClearStaged,
                Revalidate: () => State.Revalidate(),
                Close: State.Close,
                BuyHouse: (s, items) => Sent.Add($"buy:{s:X8}:{string.Join(",", items)}"),
                RentHouse: (s, items) => Sent.Add($"rent:{s:X8}:{string.Join(",", items)}"),
                ResolveIcon: (_, icon, _, _, _) => icon,
                SetWindowVisible: v => Visible = v,
                Report: Reports.Add,
                PlayerGuid: () => Player,
                Confirm: (question, answer) => { Questions.Add(question); Answer = answer; },
                ResolveAppropriateName: ItemTooltipCaptionNames.Resolve))!;
        }

        public void Item(uint id, uint wcid, int stack = 1)
        {
            Objects.AddOrUpdate(new ClientObject { ObjectId = id, WeenieClassId = wcid, StackSize = stack, IconId = 5 });
            Objects.MoveItem(id, Player, 0);
        }

        public void Open(uint owner = 0u, bool free = false, uint type = 1u)
        {
            State.ApplyProfile(new GameEvents.HouseProfile(
                Slumlord, 1u, owner, 1u, -1, -1, -1, -1, free, type, owner == 0u ? "" : "Sammy",
                [new(50_000, 0, 273u, "Pyreal", "Pyreals")],
                [new(5_000, 0, 273u, "Pyreal", "Pyreals")]));
            Controller.Tick();
        }

        public void Drop(UiItemList list, uint id, ItemDragSource source = ItemDragSource.Inventory)
        {
            var cell = new UiItemSlot();
            Controller.HandleDropRelease(list, cell, new ItemDragPayload(id, source, 0, cell));
            Controller.Tick();
        }

        // The strip pads itself with empty slots, so count only filled cells.
        public static int Staged(UiItemList list) =>
            Enumerable.Range(0, list.GetNumUIItems()).Count(i => list.GetItem(i)?.ItemId != 0);

        public static string Text(UiText text) =>
            string.Join("\n", text.LinesProvider().Select(l => l.Text));

        public void Dispose() => Controller.Dispose();
    }

    [Fact]
    public void Hidden_UntilOpened_ThenShownOnBuyForAnUnownedHouse()
    {
        using var h = new Harness();
        Assert.False(h.Visible);

        h.Open();

        Assert.True(h.Visible);
        Assert.True(h.BuyPage.Visible);
        Assert.False(h.RentPage.Visible);
        Assert.Equal("Owner: None", Harness.Text(h.BuyOwner));
        Assert.Equal("50000 Pyreals", Harness.Text(h.BuyText));
    }

    [Fact]
    public void AProfileArriving_NeverSwitchesTheTab_OnlyAClickDoes()
    {
        using var h = new Harness();
        h.Open(owner: Player);

        Assert.True(h.BuyPage.Visible);
        Assert.False(h.RentPage.Visible);

        h.RentTab.OnClick!();
        h.Open();

        Assert.True(h.RentPage.Visible);
        Assert.False(h.BuyPage.Visible);
    }

    [Fact]
    public void Drop_StagesAndEnablesTheButton_WhichSendsTheStagedGuidsAndEmptiesTheStrip()
    {
        using var h = new Harness();
        h.Item(1, 273u, stack: 50_000);
        h.Open();
        Assert.False(h.BuyButton.Enabled);

        h.Drop(h.BuyList, 1);

        Assert.Equal(1, Harness.Staged(h.BuyList));
        Assert.True(h.BuyButton.Enabled);

        h.BuyButton.OnClick!();
        Assert.Equal([HousePurchaseText.BuyConfirmation], h.Questions);
        Assert.Empty(h.Sent);

        h.Yes!();
        h.Controller.Tick();

        Assert.Equal(["buy:70000100:1"], h.Sent);
        Assert.Equal(0, Harness.Staged(h.BuyList));
        Assert.False(h.BuyButton.Enabled);
    }

    [Fact]
    public void BuyingAnApartment_AsksNothing()
    {
        using var h = new Harness();
        h.Item(1, 273u, stack: 50_000);
        h.Open(type: HousePurchaseText.ApartmentType);
        h.Drop(h.BuyList, 1);

        h.BuyButton.OnClick!();

        Assert.Empty(h.Questions);
        Assert.Equal(["buy:70000100:1"], h.Sent);
    }

    [Fact]
    public void Maintenance_AsksOnlyOnSomeoneElsesHouse()
    {
        using var h = new Harness();
        h.Item(1, 273u, stack: 10);
        h.Item(2, 273u, stack: 10);
        h.Open(owner: Player);
        h.Drop(h.RentList, 1);
        h.RentButton.OnClick!();
        Assert.Empty(h.Questions);
        Assert.Equal(["rent:70000100:1"], h.Sent);

        h.Open(owner: 0x50000042u);
        h.Drop(h.RentList, 2);
        h.RentButton.OnClick!();

        Assert.Equal([HousePurchaseText.OthersMaintenanceConfirmation], h.Questions);
        Assert.Single(h.Sent);
    }

    [Fact]
    public void ANo_ClosesTheWindow_AndEmptiesTheStrips()
    {
        using var h = new Harness();
        h.Item(1, 273u, stack: 50_000);
        h.Item(2, 273u, stack: 10);
        h.Open();
        h.Drop(h.BuyList, 1);
        h.BuyButton.OnClick!();

        h.No!();
        h.Controller.Tick();

        Assert.Empty(h.Sent);
        Assert.False(h.State.View.Snapshot.IsOpen);
        Assert.False(h.Visible);
        Assert.Empty(h.State.View.GetStaged(HousePurchaseTab.Buy));

        // Maintenance on someone else's house: a no closes it too.
        h.Open(owner: 0x50000042u);
        h.Drop(h.RentList, 2);
        h.RentButton.OnClick!();

        h.No!();
        h.Controller.Tick();

        Assert.Empty(h.Sent);
        Assert.False(h.Visible);
        Assert.Empty(h.State.View.GetStaged(HousePurchaseTab.Rent));
    }

    [Fact]
    public void AYesAfterTheWindowClosed_SendsNothing()
    {
        using var h = new Harness();
        h.Item(1, 273u, stack: 50_000);
        h.Open();
        h.Drop(h.BuyList, 1);
        h.BuyButton.OnClick!();

        h.Close.OnClick!();
        h.Yes!();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Buy_StaysDisabledUntilEveryLineIsCovered()
    {
        using var h = new Harness();
        h.Item(1, 273u, stack: 20_000);
        h.Item(2, 273u, stack: 30_000);
        h.Open();

        h.Drop(h.BuyList, 1);
        Assert.False(h.BuyButton.Enabled);

        h.Drop(h.BuyList, 2);
        Assert.True(h.BuyButton.Enabled);
    }

    [Fact]
    public void Buy_CountsOnlyTheStagedItems_NotWhatTheServerSaysIsPaid()
    {
        using var h = new Harness();
        h.Item(1, 273u, stack: 10_000);
        h.State.ApplyProfile(new GameEvents.HouseProfile(
            Slumlord, 1u, 0u, 1u, -1, -1, -1, -1, false, 1u, "",
            [new(50_000, 40_000, 273u, "Pyreal", "Pyreals")],
            [new(5_000, 0, 273u, "Pyreal", "Pyreals")]));
        h.Controller.Tick();

        h.Drop(h.BuyList, 1);

        Assert.Equal(1, Harness.Staged(h.BuyList));
        Assert.False(h.BuyButton.Enabled);
    }

    [Fact]
    public void Drop_Refused_OnlyAnItemNotCarriedIsReported()
    {
        using var h = new Harness();
        h.Item(1, 1234u);
        h.Objects.AddOrUpdate(new ClientObject { ObjectId = 2, WeenieClassId = 273u, StackSize = 10 });
        h.Open();

        h.Drop(h.BuyList, 1);
        Assert.Empty(h.Reports);

        h.Drop(h.BuyList, 2);
        Assert.Equal(["You can only trade items you are carrying"], h.Reports);
        Assert.Equal(0, Harness.Staged(h.BuyList));
    }

    [Fact]
    public void Drop_FromTheGround_ACorpseOrAChest_IsReportedAsNotCarried()
    {
        using var h = new Harness();
        const uint chest = 0x70000500u;
        h.Objects.AddOrUpdate(new ClientObject { ObjectId = chest, Type = ItemType.Container });
        h.Objects.AddOrUpdate(new ClientObject { ObjectId = 2, WeenieClassId = 273u, StackSize = 10 });
        h.Objects.MoveItem(2, chest, 0);
        h.Open();

        h.Drop(h.BuyList, 2, ItemDragSource.Ground);

        Assert.Equal(["You can only trade items you are carrying"], h.Reports);
        Assert.Equal(0, Harness.Staged(h.BuyList));
    }

    [Fact]
    public void Drop_NotCarried_IsReportedEvenWhenTheTabTakesNothing()
    {
        using var h = new Harness();
        h.Objects.AddOrUpdate(new ClientObject { ObjectId = 2, WeenieClassId = 273u, StackSize = 10 });
        h.Open(owner: Player);

        h.Drop(h.BuyList, 2, ItemDragSource.Ground);

        Assert.Equal(["You can only trade items you are carrying"], h.Reports);
    }

    [Theory]
    [InlineData(ItemDragSource.ShortcutBar)]
    [InlineData(ItemDragSource.Vendor)]
    [InlineData(ItemDragSource.Salvage)]
    public void Drop_AnAlias_IsIgnored(ItemDragSource source)
    {
        using var h = new Harness();
        h.Item(2, 273u, stack: 10);
        h.Open();

        h.Drop(h.BuyList, 2, source);

        Assert.Empty(h.Reports);
        Assert.Equal(0, Harness.Staged(h.BuyList));
    }

    // A staged cell's drag stands for the item, so the world and the item
    // windows refuse it: lifting it only unstages it.
    [Fact]
    public void AStagedCell_DragsAsAnAlias()
    {
        using var h = new Harness();
        h.Item(1, 273u, stack: 10);
        h.Open();
        h.Drop(h.BuyList, 1);

        var payload = Assert.IsType<ItemDragPayload>(h.BuyList.GetItem(0)!.GetDragPayload());
        Assert.Equal(ItemDragSource.Vendor, payload.SourceKind);
        Assert.True(payload.SourceKind.IsAlias());
    }

    // The window refuses an alias too: a staged item lifted out and let go
    // over the strip again stays unstaged.
    [Fact]
    public void AStagedItem_DroppedBackOnTheStrip_StaysUnstaged()
    {
        using var h = new Harness();
        h.Item(1, 273u, stack: 10);
        h.Open();
        h.Drop(h.BuyList, 1);
        UiItemSlot lifted = h.BuyList.GetItem(0)!;
        var payload = Assert.IsType<ItemDragPayload>(lifted.GetDragPayload());

        h.Controller.OnDragLift(h.BuyList, lifted, payload);
        h.Controller.Tick();
        Assert.Equal(0, Harness.Staged(h.BuyList));
        UiItemSlot target = h.BuyList.GetItem(0)!;
        Assert.NotEqual(ItemDragAcceptance.Accept,
            h.Controller.OnDragOver(h.BuyList, target, payload));
        h.Controller.HandleDropRelease(h.BuyList, target, payload);
        h.Controller.Tick();

        Assert.Equal(0, Harness.Staged(h.BuyList));
        Assert.Empty(h.Reports);
    }

    [Fact]
    public void TheText_IsRebuiltOnlyWhenANewProfileArrives()
    {
        using var h = new Harness();
        h.Item(1, 273u, stack: 10);
        h.Open(owner: Player);
        string before = Harness.Text(h.RentText);
        Assert.Equal("0/5000 Pyreals", before);

        h.Drop(h.RentList, 1);
        Assert.Equal(before, Harness.Text(h.RentText));

        h.State.ApplyProfile(new GameEvents.HouseProfile(
            Slumlord, 1u, Player, 1u, -1, -1, -1, -1, false, 1u, "Sammy",
            [new(50_000, 0, 273u, "Pyreal", "Pyreals")],
            [new(5_000, 10, 273u, "Pyreal", "Pyreals")]));
        h.Controller.Tick();
        Assert.Equal("10/5000 Pyreals", Harness.Text(h.RentText));
    }

    [Fact]
    public void DragOffAndRightClick_Unstage()
    {
        using var h = new Harness();
        h.Item(1, 273u, stack: 10);
        h.Item(2, 273u, stack: 10);
        h.Open(owner: Player);
        h.Drop(h.RentList, 1);
        h.Drop(h.RentList, 2);

        var cell = new UiItemSlot();
        h.Controller.OnDragLift(h.RentList, cell, new ItemDragPayload(1, ItemDragSource.Inventory, 0, cell));
        h.RentList.ExamineItemRequested!(2);
        h.Controller.Tick();

        Assert.Empty(h.State.View.GetStaged(HousePurchaseTab.Rent));
        Assert.False(h.RentButton.Enabled);
    }

    [Fact]
    public void Buttons_DisabledWhenOwnedBuy_AndFreeMaintenance_EvenWithItemsStaged()
    {
        using var h = new Harness();
        h.Item(1, 273u, stack: 50_000);
        h.Open();
        h.Drop(h.BuyList, 1);
        Assert.True(h.BuyButton.Enabled);

        h.Open(owner: Player);
        Assert.False(h.BuyButton.Enabled);
        h.BuyButton.OnClick!();
        Assert.Empty(h.Sent);

        h.Drop(h.RentList, 1);
        Assert.True(h.RentButton.Enabled);
        h.Open(owner: Player, free: true);
        Assert.False(h.RentButton.Enabled);
        h.RentButton.OnClick!();
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void AStagedStackGrowing_PaysNoMoreThanWhenItWasStaged()
    {
        using var h = new Harness();
        h.Item(1, 273u, stack: 10);
        h.Open();
        h.Drop(h.BuyList, 1);
        Assert.False(h.BuyButton.Enabled);

        h.Objects.Get(1)!.StackSize = 50_000;
        h.Objects.NotifyObjectUpdated(1);
        h.Controller.Tick();

        Assert.Equal(1, Harness.Staged(h.BuyList));
        Assert.False(h.BuyButton.Enabled);
    }

    [Fact]
    public void CloseButton_ClosesAndHides()
    {
        using var h = new Harness();
        h.Open();

        h.Close.OnClick!();
        h.Controller.Tick();

        Assert.False(h.State.View.Snapshot.IsOpen);
        Assert.False(h.Visible);
    }

    [Fact]
    public void AnItemLeavingTheInventory_IsUnstaged()
    {
        using var h = new Harness();
        h.Item(1, 273u, stack: 10);
        h.Open();
        h.Drop(h.BuyList, 1);

        h.Objects.Remove(1);
        h.Controller.Tick();

        Assert.Equal(0, Harness.Staged(h.BuyList));
    }
}
