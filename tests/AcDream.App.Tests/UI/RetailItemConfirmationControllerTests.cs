using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.App.Tests.UI.Layout;
using AcDream.Core.Items;

namespace AcDream.App.Tests.UI;

public sealed class RetailItemConfirmationControllerTests
{
    private const uint Player = 0x50000001u;
    private const uint Pack = 0x50000002u;
    private const uint Rare = 0x50000003u;

    [Fact]
    public void VolatileRareSendsUseOnlyAfterPositiveFactoryCallback()
    {
        var objects = BuildObjects(PublicWeenieFlags.VolatileRare);
        var uses = new List<uint>();
        var items = new RuntimeItemInteraction(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: uses.Add,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            nowMs: () => 1000L);
        var root = new UiRoot { Width = 800f, Height = 600f };
        ImportedLayout? shown = null;
        var factory = new RetailDialogFactory(root, _ =>
            shown = FixtureLoader.LoadConfirmationDialog());
        using var confirmations = new RetailItemConfirmationController(factory, items);

        Assert.True(items.ActivateItem(Rare));
        Assert.Empty(uses);
        Assert.Equal(0, items.BusyCount);
        Assert.Equal(
            RetailItemConfirmationController.VolatileRareMessage,
            string.Join(" ", Assert.IsType<UiText>(shown!.FindElement(
                RetailConfirmationDialogView.MessageElementId)).LinesProvider().Select(static line => line.Text)));

        Assert.IsType<UiButton>(shown.FindElement(
            RetailConfirmationDialogView.AcceptButtonId)).OnClick!();

        Assert.Equal([Rare], uses);
        Assert.Equal(1, items.BusyCount);
    }

    [Fact]
    public void RejectedPlayerKillerAltarDoesNotSendUse()
    {
        var objects = BuildObjects(PublicWeenieFlags.PlayerKillerSwitch);
        var uses = new List<uint>();
        var items = new RuntimeItemInteraction(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: uses.Add,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            nowMs: () => 1000L);
        var root = new UiRoot { Width = 800f, Height = 600f };
        ImportedLayout? shown = null;
        var factory = new RetailDialogFactory(root, _ =>
            shown = FixtureLoader.LoadConfirmationDialog());
        using var confirmations = new RetailItemConfirmationController(factory, items);

        Assert.True(items.ActivateItem(Rare));
        Assert.IsType<UiButton>(shown!.FindElement(
            RetailConfirmationDialogView.RejectButtonId)).OnClick!();

        Assert.Empty(uses);
        Assert.Equal(0, items.BusyCount);
    }

    [Fact]
    public void PositiveConfirmationRechecksGlobalInventoryGateBeforeUse()
    {
        var objects = BuildObjects(PublicWeenieFlags.VolatileRare);
        var uses = new List<uint>();
        var messages = new List<string>();
        var items = new RuntimeItemInteraction(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: uses.Add,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            placeInBackpack: static (_, _, _) => { },
            systemMessage: messages.Add,
            nowMs: () => 1000L);
        var root = new UiRoot { Width = 800f, Height = 600f };
        ImportedLayout? shown = null;
        var factory = new RetailDialogFactory(root, _ =>
            shown = FixtureLoader.LoadConfirmationDialog());
        using var confirmations = new RetailItemConfirmationController(factory, items);

        Assert.True(items.ActivateItem(Rare));
        Assert.True(items.PlaceWorldItemInBackpack(0x70000001u));
        Assert.IsType<UiButton>(shown!.FindElement(
            RetailConfirmationDialogView.AcceptButtonId)).OnClick!();

        Assert.Empty(uses);
        Assert.Equal(0, items.BusyCount);
        Assert.Equal(new[] { RuntimeItemInteraction.InventoryRequestBusyMessage }, messages);
    }

    private static ClientObjectTable BuildObjects(PublicWeenieFlags flags)
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Player,
            Name = "Player",
            Type = ItemType.Creature,
            ItemsCapacity = 102,
            ContainersCapacity = 7,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Pack,
            Name = "Backpack",
            Type = ItemType.Container,
        });
        objects.MoveItem(Pack, Player, 0);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Rare,
            Name = "Rare",
            Type = ItemType.Misc,
            Useability = ItemUseability.Contained,
            PublicWeenieBitfield = (uint)flags,
        });
        objects.MoveItem(Rare, Pack, 0);
        return objects;
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void EmptyManaStone_OnlyConfirmedValidTargetIsSent(bool accept, bool retained, bool charged)
    {
        const uint stone = 0x50000004u;
        var objects = BuildObjects(PublicWeenieFlags.None);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = stone, Name = "Mana Stone", Type = ItemType.ManaStone,
            Useability = 0x00080008u, TargetType = (uint)ItemType.Misc,
        });
        objects.MoveItem(stone, Pack, 1);
        var uses = new List<(uint, uint)>();
        using var items = new RuntimeItemInteraction(objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(), () => Player, null,
            (source, target) => uses.Add((source, target)), null, null, nowMs: () => 1000L);
        var root = new UiRoot { Width = 800, Height = 600 };
        ImportedLayout? shown = null;
        var factory = new RetailDialogFactory(root, _ => shown = FixtureLoader.LoadConfirmationDialog());
        using var confirmations = new RetailItemConfirmationController(factory, items);

        Assert.True(items.ActivateItem(stone));
        Assert.True(items.AcquireTarget(Rare));
        Assert.False(items.IsTargetModeActive);
        Assert.Empty(uses);
        Assert.Equal(0, items.BusyCount);
        Assert.Contains("destroy your Rare", string.Join(" ", Assert.IsType<UiText>(shown!.FindElement(
            RetailConfirmationDialogView.MessageElementId)).LinesProvider().Select(l => l.Text)));

        if (retained) objects.Get(Rare)!.PublicWeenieBitfield = (uint)PublicWeenieFlags.Retained;
        if (charged) objects.Get(stone)!.Effects = 1u;
        Assert.IsType<UiButton>(shown.FindElement(accept
            ? RetailConfirmationDialogView.AcceptButtonId
            : RetailConfirmationDialogView.RejectButtonId)).OnClick!();
        if (accept && !retained && !charged)
        {
            Assert.Equal([(stone, Rare)], uses);
            Assert.Equal(1, items.BusyCount);
        }
        else
        {
            Assert.Empty(uses);
            Assert.Equal(0, items.BusyCount);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ManaStone_ChargedSendsDirectly_RetainedOrNoPresenterNeverDestroys(bool charged, bool retained)
    {
        const uint stone = 0x50000004u;
        var objects = BuildObjects(retained ? PublicWeenieFlags.Retained : PublicWeenieFlags.None);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = stone, Name = "Mana Stone", Type = ItemType.ManaStone,
            Effects = charged ? 1u : 0u,
            Useability = 0x00080008u, TargetType = (uint)ItemType.Misc,
        });
        objects.MoveItem(stone, Pack, 1);
        var uses = new List<(uint, uint)>();
        using var items = new RuntimeItemInteraction(objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(), () => Player, null,
            (source, target) => uses.Add((source, target)), null, null, nowMs: () => 1000L);
        Assert.True(items.ActivateItem(stone));
        items.AcquireTarget(Rare);
        Assert.Equal(charged ? 1 : 0, uses.Count);
        Assert.Equal(charged ? 1 : 0, items.BusyCount);
    }
}
