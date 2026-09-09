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
        var items = new ItemInteractionController(
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
        var items = new ItemInteractionController(
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
        var items = new ItemInteractionController(
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
        Assert.Equal(new[] { ItemInteractionController.InventoryRequestBusyMessage }, messages);
    }

    private static ClientObjectTable BuildObjects(PublicWeenieFlags flags)
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Player,
            Name = "Player",
            Type = ItemType.Creature,
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
}
