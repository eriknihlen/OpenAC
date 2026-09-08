using System.Collections.Generic;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Core.Selection;
using Xunit;

namespace AcDream.App.Tests.UI.Layout;

public class PaperdollControllerTests
{
    private const uint Player = 0x50000001u;
    private const uint Pack   = 0x40000005u;
    private const uint HeadSlot   = 0x100005ABu;  // HeadWear 0x1
    private const uint ChestSlot  = 0x100001E2u;
    private const uint ChestArmorSlot = 0x100005ACu;
    private const uint ShieldSlot = 0x100001E1u;
    private const uint WeaponSlot = 0x100001DFu;
    private const uint FingerLSlot= 0x100001DCu;
    private const uint BlueAetheriaSlot   = 0x10000595u;
    private const uint YellowAetheriaSlot = 0x10000596u;
    private const uint RedAetheriaSlot    = 0x10000597u;

    private sealed class RootElement : UiElement { }

    private static (ImportedLayout layout, Dictionary<uint, UiItemList> lists) BuildLayout()
    {
        var ids = new[]
        {
            HeadSlot, ChestSlot, ChestArmorSlot, ShieldSlot, WeaponSlot, FingerLSlot,
            BlueAetheriaSlot, YellowAetheriaSlot, RedAetheriaSlot,
        };
        var lists = new Dictionary<uint, UiItemList>();
        var byId  = new Dictionary<uint, UiElement>();
        var root  = new RootElement { Width = 224, Height = 214 };
        foreach (var id in ids)
        {
            var list = new UiItemList { Width = 32, Height = 32 };
            lists[id] = list; byId[id] = list; root.AddChild(list);
        }
        return (new ImportedLayout(root, byId), lists);
    }

    private static PaperdollController Bind(ImportedLayout layout, ClientObjectTable objects,
        List<(uint item, uint mask)>? wields = null, uint emptySlot = 0u,
        SelectionState? selection = null,
        IReadOnlyDictionary<uint, uint>? emptySlotSprites = null,
        List<(uint item, uint container, int placement)>? puts = null,
        List<string>? systemMessages = null,
        List<uint>? examines = null,
        Action<ItemInteractionController>? configureInteraction = null)
    {
        var itemInteraction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: wields is null ? null : (i, m) => wields.Add((i, m)),
            sendDrop: null,
            sendExamine: examines is null ? null : examines.Add,
            sendPutItemInContainer: puts is null
                ? null
                : (item, container, placement) => puts.Add((item, container, placement)),
            systemMessage: systemMessages is null ? null : systemMessages.Add);
        configureInteraction?.Invoke(itemInteraction);
        return PaperdollController.Bind(layout, objects, () => Player,
            iconIds: (_, _, _, _, _) => 0x1234u,
            itemInteraction: itemInteraction,
            emptySlotSprite: emptySlot,
            selection: selection ?? new SelectionState(),
            emptySlotSprites: emptySlotSprites);
    }

    private static void SeedEquipped(ClientObjectTable t, uint guid, EquipMask loc)
    {
        t.AddOrUpdate(new ClientObject { ObjectId = guid, WielderId = Player });
        t.MoveItem(guid, Player, newSlot: -1, newEquipLocation: loc);
    }

    private static void SeedPackItem(ClientObjectTable t, uint guid, EquipMask validLocations)
    {
        t.AddOrUpdate(new ClientObject { ObjectId = guid, ValidLocations = validLocations });
        t.MoveItem(guid, Pack, newSlot: 0);
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

    [Fact]
    public void Populate_shows_equipped_item_in_its_slot()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedEquipped(objects, 0xA01u, EquipMask.HeadWear);
        Bind(layout, objects);
        Assert.Equal(0xA01u, lists[HeadSlot].Cell.ItemId);   // helm in the head slot
        Assert.Equal(0u, lists[ShieldSlot].Cell.ItemId);     // shield slot empty
    }

    [Fact]
    public void RightClickEquippedSlot_selectsAndExaminesItem()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedEquipped(objects, 0xA01u, EquipMask.HeadWear);
        var selection = new SelectionState();
        var appraisals = new List<uint>();
        Bind(
            layout,
            objects,
            selection: selection,
            examines: appraisals);

        UiItemSlot cell = lists[HeadSlot].Cell;
        cell.OnEvent(new UiEvent(0u, cell, UiEventType.RightClick));

        Assert.Equal(0xA01u, selection.SelectedObjectId);
        Assert.True(cell.Selected);
        Assert.Equal(new uint[] { 0xA01u }, appraisals);
    }

    [Fact]
    public void SessionTableClearRemovesOldEquipmentAndLocksAetheriaSlots()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedEquipped(objects, 0xA01u, EquipMask.HeadWear);
        objects.AddOrUpdate(new ClientObject { ObjectId = Player });
        objects.UpdateIntProperty(
            Player,
            AetheriaUnlocks.PropertyId,
            (int)AetheriaUnlockState.Blue);
        Bind(layout, objects);
        Assert.Equal(0xA01u, lists[HeadSlot].Cell.ItemId);
        Assert.True(lists[BlueAetheriaSlot].Visible);

        objects.Clear();

        Assert.Equal(0u, lists[HeadSlot].Cell.ItemId);
        Assert.False(lists[BlueAetheriaSlot].Visible);
    }

    [Fact]
    public void GenerationReplacementRemovesOldEquippedProjection()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedEquipped(objects, 0xA01u, EquipMask.HeadWear);
        Bind(layout, objects);
        Assert.Equal(0xA01u, lists[HeadSlot].Cell.ItemId);

        objects.ReplaceGeneration(WorldReplacement(0xA01u), generation: 2);

        Assert.Equal(0u, lists[HeadSlot].Cell.ItemId);
    }

    [Fact]
    public void MouseDownEquippedItem_updatesSharedSelection()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedEquipped(objects, 0xA01u, EquipMask.HeadWear);
        var selection = new SelectionState();
        Bind(layout, objects, selection: selection);

        UiItemSlot cell = lists[HeadSlot].Cell;
        cell.OnEvent(new UiEvent(0u, cell, UiEventType.MouseDown));

        Assert.Equal(0xA01u, selection.SelectedObjectId);
        Assert.True(lists[HeadSlot].Cell.Selected);
    }

    [Fact]
    public void DragLift_selectsEquippedItem_beforeWaitingVisual()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedEquipped(objects, 0xA01u, EquipMask.HeadWear);
        var selection = new SelectionState();
        var ctrl = Bind(layout, objects, selection: selection);
        var cell = lists[HeadSlot].Cell;
        var payload = new ItemDragPayload(0xA01u, ItemDragSource.Equipment, 0, cell);

        ctrl.OnDragLift(lists[HeadSlot], cell, payload);

        Assert.Equal(0xA01u, selection.SelectedObjectId);
        Assert.True(cell.Selected);
    }

    [Fact]
    public void Populate_matches_a_weapon_into_the_composite_slot()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedEquipped(objects, 0xB01u, EquipMask.MeleeWeapon);     // a sword's actual equip loc
        Bind(layout, objects);
        Assert.Equal(0xB01u, lists[WeaponSlot].Cell.ItemId);
    }

    [Fact]
    public void OnDragOver_accepts_only_valid_locations()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedPackItem(objects, 0xC01u, EquipMask.HeadWear);       // a helm in the pack
        var ctrl = Bind(layout, objects);
        var payload = new ItemDragPayload(0xC01u, ItemDragSource.Inventory, 0, lists[HeadSlot].Cell);
        Assert.Equal(ItemDragAcceptance.Accept,
            ctrl.OnDragOver(lists[HeadSlot], lists[HeadSlot].Cell, payload)); // helm → head OK
        Assert.Equal(ItemDragAcceptance.Reject,
            ctrl.OnDragOver(lists[ShieldSlot], lists[ShieldSlot].Cell, payload)); // helm → shield NO
    }

    [Fact]
    public void ShortcutAlias_isNeutral_andNeverWieldsThroughPaperdoll()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedPackItem(objects, 0xC02u, EquipMask.HeadWear);
        var wields = new List<(uint item, uint mask)>();
        var ctrl = Bind(layout, objects, wields);
        var payload = new ItemDragPayload(
            0xC02u, ItemDragSource.ShortcutBar, 2, lists[HeadSlot].Cell);

        Assert.Equal(ItemDragAcceptance.None,
            ctrl.OnDragOver(lists[HeadSlot], lists[HeadSlot].Cell, payload));
        ctrl.HandleDropRelease(lists[HeadSlot], lists[HeadSlot].Cell, payload);

        Assert.Empty(wields);
        Assert.Equal(EquipMask.None, objects.Get(0xC02u)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void HandleDropRelease_sendsWieldAndWaitsForServerPlacement()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedPackItem(objects, 0xD01u, EquipMask.HeadWear);
        var wields = new List<(uint item, uint mask)>();
        var ctrl = Bind(layout, objects, wields);
        var payload = new ItemDragPayload(0xD01u, ItemDragSource.Inventory, 0, lists[HeadSlot].Cell);
        ctrl.HandleDropRelease(lists[HeadSlot], lists[HeadSlot].Cell, payload);
        Assert.Equal(EquipMask.None, objects.Get(0xD01u)!.CurrentlyEquippedLocation);
        Assert.Equal(Pack, objects.Get(0xD01u)!.ContainerId);
        Assert.Single(wields);
        Assert.Equal((0xD01u, (uint)EquipMask.HeadWear), wields[0]);                       // GetAndWieldItem wire
    }

    [Fact]
    public void HandleDropRelease_pendingPickupRejectsWieldWithoutOptimisticMutation()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        const uint pickup = 0x70000D02u;
        const uint helm = 0x50000D03u;
        SeedPackItem(objects, helm, EquipMask.HeadWear);
        var wields = new List<(uint item, uint mask)>();
        var messages = new List<string>();
        ItemInteractionController? interaction = null;
        var ctrl = Bind(
            layout,
            objects,
            wields,
            systemMessages: messages,
            configureInteraction: value => interaction = value);
        Assert.True(interaction!.TryBeginPendingBackpackPlacement(
            pickup,
            Player,
            0,
            out _));
        ctrl.HandleDropRelease(
            lists[HeadSlot],
            lists[HeadSlot].Cell,
            new ItemDragPayload(helm, ItemDragSource.Inventory, 0, lists[HeadSlot].Cell));

        Assert.Empty(wields);
        Assert.Equal(EquipMask.None, objects.Get(helm)!.CurrentlyEquippedLocation);
        Assert.Equal(new[] { ItemInteractionController.InventoryRequestBusyMessage }, messages);
    }

    [Fact]
    public void HandleDropRelease_weaponSlot_delegatesToConfirmedAutoWieldTransaction()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        const uint bow = 0xD11u;
        const uint sword = 0xD12u;
        SeedEquipped(objects, bow, EquipMask.MissileWeapon);
        objects.Get(bow)!.Name = "Shortbow";
        SeedPackItem(objects, sword, EquipMask.MeleeWeapon);
        objects.Get(sword)!.Name = "Katar";
        var wields = new List<(uint item, uint mask)>();
        var puts = new List<(uint item, uint container, int placement)>();
        var messages = new List<string>();
        var ctrl = Bind(
            layout,
            objects,
            wields,
            puts: puts,
            systemMessages: messages);
        var payload = new ItemDragPayload(
            sword,
            ItemDragSource.Inventory,
            0,
            lists[WeaponSlot].Cell);

        ctrl.HandleDropRelease(lists[WeaponSlot], lists[WeaponSlot].Cell, payload);

        Assert.Equal(new[] { (bow, Player, 0) }, puts);
        Assert.Empty(wields);
        Assert.Equal(EquipMask.None, objects.Get(sword)!.CurrentlyEquippedLocation);
        Assert.Equal(new[] { "Moving Shortbow to your backpack" }, messages);

        Assert.True(objects.ApplyConfirmedServerMove(bow, Player, 0u, 0));

        Assert.Equal(new[] { (sword, (uint)EquipMask.MeleeWeapon) }, wields);
        Assert.Equal(EquipMask.None,
            objects.Get(sword)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void HandleDropRelease_resolves_the_finger_bit_for_a_dual_finger_ring()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedPackItem(objects, 0xE01u, EquipMask.FingerWearLeft | EquipMask.FingerWearRight);
        var wields = new List<(uint item, uint mask)>();
        var ctrl = Bind(layout, objects, wields);
        var payload = new ItemDragPayload(0xE01u, ItemDragSource.Inventory, 0, lists[FingerLSlot].Cell);
        ctrl.HandleDropRelease(lists[FingerLSlot], lists[FingerLSlot].Cell, payload);
        Assert.Equal((uint)EquipMask.FingerWearLeft, wields[0].mask);   // ValidLocations & slotMask = left finger only
    }

    [Fact]
    public void HandleDropRelease_onAutoWearSlot_sendsFullValidLocations()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        const EquipMask coatMask =
            EquipMask.ChestWear
            | EquipMask.UpperArmWear
            | EquipMask.LowerArmWear;
        SeedPackItem(objects, 0xE02u, coatMask);
        var wields = new List<(uint item, uint mask)>();
        var ctrl = Bind(layout, objects, wields);
        var payload = new ItemDragPayload(0xE02u, ItemDragSource.Inventory, 0, lists[ChestSlot].Cell);

        ctrl.HandleDropRelease(lists[ChestSlot], lists[ChestSlot].Cell, payload);

        Assert.Equal((uint)coatMask, wields[0].mask);
        Assert.Equal(EquipMask.None, objects.Get(0xE02u)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void HandleDropRelease_onArmorAutoWearSlot_sendsFullValidLocations()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        const EquipMask hauberkMask =
            EquipMask.ChestArmor
            | EquipMask.AbdomenArmor
            | EquipMask.UpperArmArmor
            | EquipMask.LowerArmArmor;
        SeedPackItem(objects, 0xE03u, hauberkMask);
        var wields = new List<(uint item, uint mask)>();
        var ctrl = Bind(layout, objects, wields);
        var payload = new ItemDragPayload(0xE03u, ItemDragSource.Inventory, 0, lists[ChestArmorSlot].Cell);

        ctrl.HandleDropRelease(lists[ChestArmorSlot], lists[ChestArmorSlot].Cell, payload);

        Assert.Equal((uint)hauberkMask, wields[0].mask);
        Assert.Equal(EquipMask.None, objects.Get(0xE03u)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void Empty_equip_slot_shows_the_configured_frame()
    {
        var (layout, lists) = BuildLayout();
        Bind(layout, new ClientObjectTable(), emptySlot: 0x06004D20u);
        Assert.Equal(0x06004D20u, lists[HeadSlot].Cell.EmptySprite);
    }

    [Fact]
    public void PerLocation_empty_sprites_override_the_generic_fallback()
    {
        var (layout, lists) = BuildLayout();
        var sprites = new Dictionary<uint, uint>
        {
            [HeadSlot]       = 0x06006D7Fu,
            [ChestSlot]      = 0x060032C5u,
            [ChestArmorSlot] = 0x06006D7Bu,
            [ShieldSlot]     = 0x06000F6Cu,
            [WeaponSlot]     = 0x06000F66u,
            [FingerLSlot]    = 0x06000F5Au,
        };

        Bind(layout, new ClientObjectTable(), emptySlot: 0x06004D20u, emptySlotSprites: sprites);

        foreach (var (element, sprite) in sprites)
            Assert.Equal(sprite, lists[element].Cell.EmptySprite);
    }

    [Fact]
    public void Aetheria_slots_default_hidden_without_unlock_property()
    {
        var (layout, lists) = BuildLayout();

        Bind(layout, new ClientObjectTable());

        Assert.False(lists[BlueAetheriaSlot].Visible);
        Assert.False(lists[YellowAetheriaSlot].Visible);
        Assert.False(lists[RedAetheriaSlot].Visible);
    }

    [Fact]
    public void Aetheria_slots_follow_independent_player_unlock_bits_at_login()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Player });
        objects.UpdateIntProperty(
            Player,
            AetheriaUnlocks.PropertyId,
            (int)(AetheriaUnlockState.Blue | AetheriaUnlockState.Red));

        Bind(layout, objects);

        Assert.True(lists[BlueAetheriaSlot].Visible);
        Assert.False(lists[YellowAetheriaSlot].Visible);
        Assert.True(lists[RedAetheriaSlot].Visible);
    }

    [Fact]
    public void Live_aetheria_property_update_unlocks_new_slots()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Player });
        Bind(layout, objects);

        objects.UpdateIntProperty(
            Player,
            AetheriaUnlocks.PropertyId,
            (int)(AetheriaUnlockState.Blue | AetheriaUnlockState.Yellow));

        Assert.True(lists[BlueAetheriaSlot].Visible);
        Assert.True(lists[YellowAetheriaSlot].Visible);
        Assert.False(lists[RedAetheriaSlot].Visible);
    }

    [Fact]
    public void Unlocked_aetheria_slot_populates_its_equipped_sigil()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Player });
        objects.UpdateIntProperty(
            Player,
            AetheriaUnlocks.PropertyId,
            (int)AetheriaUnlockState.Blue);
        SeedEquipped(objects, 0xA37u, EquipMask.SigilOne);

        Bind(layout, objects);

        Assert.True(lists[BlueAetheriaSlot].Visible);
        Assert.Equal(0xA37u, lists[BlueAetheriaSlot].Cell.ItemId);
    }

    [Fact]
    public void Live_player_wield_repaints_the_slot()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        Bind(layout, objects);                                          // slots empty at bind
        SeedPackItem(objects, 0xF01u, EquipMask.HeadWear);              // a helm appears in the pack (not on the doll)
        Assert.Equal(0u, lists[HeadSlot].Cell.ItemId);
        objects.WieldItemOptimistic(0xF01u, Player, EquipMask.HeadWear); // player wields it → ObjectMoved(to=player)
        Assert.Equal(0xF01u, lists[HeadSlot].Cell.ItemId);             // the head slot repainted with the helm
    }

    [Fact]
    public void Confirmed_unwield_to_side_bag_clears_slot_from_old_wielder_state()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        const uint helm = 0xF03u, sideBag = 0x50000020u;
        Bind(layout, objects);
        objects.AddOrUpdate(new ClientObject { ObjectId = helm });
        Assert.True(objects.ApplyServerMove(
            helm,
            newContainerId: 0u,
            newWielderId: Player,
            newEquipLocation: EquipMask.HeadWear));
        Assert.Equal(helm, lists[HeadSlot].Cell.ItemId);

        Assert.True(objects.ApplyServerMove(
            helm,
            newContainerId: sideBag,
            newWielderId: 0u));

        Assert.Equal(0u, lists[HeadSlot].Cell.ItemId);
    }

    [Fact]
    public void Live_npc_equip_does_not_appear_on_the_doll()
    {
        var (layout, lists) = BuildLayout();
        var objects = new ClientObjectTable();
        Bind(layout, objects);
        const uint npc = 0x60000001u;
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0xF02u, WielderId = npc, CurrentlyEquippedLocation = EquipMask.HeadWear,
        });
        Assert.Equal(0u, lists[HeadSlot].Cell.ItemId);                 // the NPC's helm is NOT on the player's doll
    }
}
