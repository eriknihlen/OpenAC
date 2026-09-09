using AcDream.App.UI;
using AcDream.Core.Items;

namespace AcDream.App.Tests.UI;

public sealed class AutoWieldGenerationTests
{
    private const uint Player = 0x50000001u;

    [Fact]
    public void GenerationReplacementCancelsPendingWeaponSwitch()
    {
        var objects = new ClientObjectTable();
        var blocker = new ClientObject
        {
            ObjectId = 0x60000001u,
            WielderId = Player,
            ValidLocations = EquipMask.MeleeWeapon,
        };
        objects.AddOrUpdate(blocker);
        objects.MoveItem(
            blocker.ObjectId,
            Player,
            newSlot: -1,
            newEquipLocation: EquipMask.MeleeWeapon);
        var requested = new ClientObject
        {
            ObjectId = 0x60000002u,
            ContainerId = Player,
            ValidLocations = EquipMask.MeleeWeapon,
        };
        objects.AddOrUpdate(requested);

        using var controller = new AutoWieldController(
            objects,
            () => Player,
            sendWield: null,
            sendPutItemInContainer: (_, _, _) => { });
        Assert.True(controller.TryWield(requested));
        Assert.True(controller.IsBusy);

        objects.ReplaceGeneration(WorldReplacement(blocker.ObjectId), generation: 2);

        Assert.False(controller.IsBusy);
    }

    private static WeenieData WorldReplacement(uint guid) => new(
        Guid: guid,
        Name: "replacement",
        Type: ItemType.Misc,
        WeenieClassId: 1,
        IconId: 0,
        IconOverlayId: 0,
        IconUnderlayId: 0,
        Effects: 0,
        Value: null,
        StackSize: null,
        StackSizeMax: null,
        Burden: null,
        ContainerId: null,
        WielderId: null,
        ValidLocations: null,
        CurrentWieldedLocation: null,
        Priority: null,
        ItemsCapacity: null,
        ContainersCapacity: null,
        Structure: null,
        MaxStructure: null,
        Workmanship: null);
}
