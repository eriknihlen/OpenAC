//! The inventory owner against a world built from server messages.
//!
//! Golden bodies come from the `ac_proto` builders, as OpenAC
//! `tests/AcDream.Core.Net.Tests/WorldSessionInventoryActionTests.cs` and
//! `WorldSessionVendorTests.cs` compare a session's sends with
//! `InventoryActions`/`VendorRequests` (one shared action counter). The
//! request gating follows `InventoryTransactionStateTests.cs` and
//! `RuntimeInteractionTransactionStateTests.cs`; the wield switch
//! `AutoWieldController`; trade marks `RuntimeTradeStateTests.ApplyAdd_*`.

use ac_core::items::{EquipMask, InventoryRequestKind, ItemType, ItemUseable, PublicWeenieFlags};
use ac_proto::appraise::{IdentifyObjectResponse, build_appraise};
use ac_proto::inventory::{
    build_drop_item, build_get_and_wield_item, build_give_object, build_no_longer_viewing_contents,
    build_put_item_in_container, build_stackable_merge, build_stackable_split_to_3d,
    build_stackable_split_to_container, build_use, build_use_with_target,
};
use ac_proto::vendor::{ApproachVendor, ItemAmount, VendorItem, VendorProfile, build_buy};
use ac_proto::{
    CreateObject, GameEventType, Inbound, ModelData, PhysicsTimestamps, PublicWeenieDesc,
};

use super::*;
use crate::world::tests::{empty_description, game_event, physics};

const PLAYER: u32 = 0x5000_000A;
const PACK: u32 = 0x6000_0001;
const GEM: u32 = 0x6000_0002;
const GEM2: u32 = 0x6000_0003;
const SWORD: u32 = 0x6000_0004;
const AXE: u32 = 0x6000_0005;
const LOOSE: u32 = 0x7000_0001;
const NPC: u32 = 0x7000_0002;
const OTHER_PLAYER: u32 = 0x5000_00BB;
const VENDOR: u32 = 0x7000_0003;

fn create(guid: u32, name: &str, edit: impl FnOnce(&mut PublicWeenieDesc)) -> Inbound<'static> {
    let mut desc = PublicWeenieDesc {
        name: name.into(),
        ..PublicWeenieDesc::default()
    };
    edit(&mut desc);
    Inbound::CreateObject(Box::new(CreateObject {
        guid,
        model: ModelData::default(),
        physics: physics(
            PhysicsTimestamps {
                instance: 1,
                ..PhysicsTimestamps::default()
            },
            None,
        ),
        weenie: Some(desc),
    }))
}

struct Fixture {
    world: World,
    outbox: Outbox,
    options: CharacterOptions,
    inventory: Inventory,
    now_ms: i64,
    in_peace: bool,
}

impl Fixture {
    /// The player holds a pack, two gem stacks of one kind and a sword; an
    /// axe is worn. A loose gem lies in the world, and an NPC, another
    /// player and a vendor stand there.
    fn new() -> Self {
        Self::with_main_pack_slots(102)
    }

    /// The same, with `main_pack_slots` item slots in the main pack (the
    /// gems and the sword take three).
    fn with_main_pack_slots(main_pack_slots: i8) -> Self {
        let mut world = World::new();
        world.set_player(PLAYER);
        world.apply(&create(PLAYER, "Horan", |d| {
            d.object_description_flags = PublicWeenieFlags::PLAYER.bits();
            d.items_capacity = Some(main_pack_slots);
            d.containers_capacity = Some(7);
            d.item_type = ItemType::CREATURE.bits();
        }));
        world.apply(&create(PACK, "Pack", |d| {
            d.item_type = ItemType::CONTAINER.bits();
            d.items_capacity = Some(24);
        }));
        let gem = |stack: u16| {
            move |d: &mut PublicWeenieDesc| {
                d.item_type = ItemType::GEM.bits();
                d.weenie_class_id = 10;
                d.stack_size = Some(stack);
                d.max_stack_size = Some(100);
            }
        };
        world.apply(&create(GEM, "Gem", gem(5)));
        world.apply(&create(GEM2, "Gem", gem(3)));
        world.apply(&create(LOOSE, "Gem", gem(2)));
        world.apply(&create(SWORD, "Sword", |d| {
            d.item_type = ItemType::MELEE_WEAPON.bits();
            d.valid_locations = Some(EquipMask::MELEE_WEAPON.bits());
            d.combat_use = Some(1);
        }));
        world.apply(&create(AXE, "Axe", |d| {
            d.item_type = ItemType::MELEE_WEAPON.bits();
            d.valid_locations = Some(EquipMask::MELEE_WEAPON.bits());
            d.combat_use = Some(1);
        }));
        world.apply(&create(NPC, "Town Crier", |d| {
            d.item_type = ItemType::CREATURE.bits();
            d.object_description_flags = PublicWeenieFlags::STUCK.bits();
            d.useability = Some(ItemUseable::REMOTE.bits());
        }));
        world.apply(&create(OTHER_PLAYER, "Bob", |d| {
            d.item_type = ItemType::CREATURE.bits();
            d.object_description_flags = PublicWeenieFlags::PLAYER.bits();
        }));
        world.apply(&create(VENDOR, "Shopkeeper", |d| {
            d.item_type = ItemType::CREATURE.bits();
            d.object_description_flags =
                (PublicWeenieFlags::VENDOR | PublicWeenieFlags::STUCK).bits();
        }));
        let mut description = empty_description();
        description.inventory = [(PACK, 1), (GEM, 0), (GEM2, 0), (SWORD, 0)]
            .into_iter()
            .map(|(guid, container_type)| ac_proto::InventoryEntry {
                guid,
                container_type,
            })
            .collect();
        description.equipped = vec![ac_proto::EquippedEntry {
            guid: AXE,
            equip_location: EquipMask::MELEE_WEAPON.bits(),
            priority: 0,
        }];
        world.apply_player_description(&description);
        world.take_table_events();
        Self {
            world,
            outbox: Outbox::new(),
            options: CharacterOptions::new(),
            inventory: Inventory::new(),
            now_ms: 10_000,
            in_peace: true,
        }
    }

    fn ctx(&mut self) -> ItemContext<'_> {
        ItemContext {
            world: &mut self.world,
            outbox: &mut self.outbox,
            options: &self.options,
            active: true,
            now_ms: self.now_ms,
            in_peace: self.in_peace,
            approach: None,
        }
    }

    fn run(&mut self, command: ItemCommand) -> bool {
        let mut inventory = std::mem::take(&mut self.inventory);
        let mut ctx = self.ctx();
        let sent = inventory.execute(&command, &mut ctx);
        inventory.observe(&mut ctx);
        self.inventory = inventory;
        sent
    }

    fn server(&mut self, event_type: GameEventType, payload: &[u32]) {
        let body = game_event(event_type, payload);
        let message = Inbound::parse(&body).unwrap();
        self.world.apply(&message);
        let mut inventory = std::mem::take(&mut self.inventory);
        inventory.apply(&message, &mut self.ctx());
        self.inventory = inventory;
    }

    fn sent(&mut self) -> Vec<Vec<u8>> {
        self.outbox.drain().map(|o| o.body).collect()
    }

    fn notices(&mut self) -> Vec<Notice> {
        self.inventory.drain_notices().collect()
    }

    fn events(&mut self) -> Vec<InventoryEvent> {
        self.inventory.drain_events().collect()
    }
}

fn guid(id: u32) -> ObjectRef {
    ObjectRef::Guid(id)
}

#[test]
fn object_references_parse_ids_names_and_the_player() {
    assert_eq!(ObjectRef::parse("0x6000000A"), ObjectRef::Guid(0x6000_000A));
    assert_eq!(ObjectRef::parse("42"), ObjectRef::Guid(42));
    assert_eq!(ObjectRef::parse("me"), ObjectRef::Player);
    assert_eq!(
        ObjectRef::parse(" Town Crier "),
        ObjectRef::Name("Town Crier".into())
    );
}

#[test]
fn a_move_goes_out_as_the_one_request_in_flight_until_the_server_answers() {
    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::MoveToContainer {
        item: guid(GEM),
        container: guid(PACK),
        slot: 0,
        amount: 0,
        join_stack: false,
    }));
    assert_eq!(f.sent(), [build_put_item_in_container(1, GEM, PACK, 0)]);

    // A second request waits for the answer, with the busy notice.
    assert!(!f.run(ItemCommand::MoveToContainer {
        item: guid(GEM2),
        container: guid(PACK),
        slot: 0,
        amount: 0,
        join_stack: false,
    }));
    assert!(f.sent().is_empty());
    assert_eq!(f.notices(), [Notice::Interface(REQUEST_BUSY.into())]);

    // The server's placement completes it.
    f.server(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[GEM, PACK, 0, 0],
    );
    assert_eq!(f.world.item(GEM).unwrap().container_id, PACK);
    assert!(f.inventory.transactions().can_begin_request());
    assert!(f.run(ItemCommand::MoveToContainer {
        item: guid(GEM2),
        container: guid(PACK),
        slot: 0,
        amount: 0,
        join_stack: false,
    }));
    assert_eq!(f.sent(), [build_put_item_in_container(2, GEM2, PACK, 0)]);
}

#[test]
fn part_of_a_stack_moves_as_a_split() {
    // WorldSessionInventoryActionTests.SendStackableSplitToContainer_*.
    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::MoveToContainer {
        item: guid(GEM),
        container: guid(PACK),
        slot: 4,
        amount: 2,
        join_stack: false,
    }));
    assert_eq!(
        f.sent(),
        [build_stackable_split_to_container(1, GEM, PACK, 4, 2)]
    );
    // More than the stack holds is refused.
    let mut f = Fixture::new();
    assert!(!f.run(ItemCommand::MoveToContainer {
        item: guid(GEM),
        container: guid(PACK),
        slot: 0,
        amount: 6,
        join_stack: false,
    }));
    assert!(f.sent().is_empty());
}

// The join-stack moves: OpenAC `ItemInteractionControllerTests`
// (`AutomationMove*JoiningAStack*`, `AutomationSplitJoiningAStack*`,
// `AutomationMoveWithoutJoiningNeverMerges`), each stack of kind 77 and at
// most 100 unless the test says otherwise.
const JOIN_KIND: u32 = 77;

/// A stack of `size` of `kind` placed into `container` by the server.
fn add_stack(f: &mut Fixture, id: u32, kind: u32, size: u16, container: u32) {
    f.world.apply(&create(id, "Coins", |d| {
        d.item_type = ItemType::MONEY.bits();
        d.weenie_class_id = kind;
        d.stack_size = Some(size);
        d.max_stack_size = Some(100);
    }));
    let slot = u32::try_from(f.world.items().get_contents(container).len()).unwrap();
    f.server(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[id, container, slot, 0],
    );
    assert_eq!(f.world.item(id).unwrap().container_id, container);
}

fn join_move(item: u32, container: u32, amount: u32, slot: i32, join_stack: bool) -> ItemCommand {
    ItemCommand::MoveToContainer {
        item: guid(item),
        container: guid(container),
        slot,
        amount,
        join_stack,
    }
}

#[test]
fn a_joining_move_pours_the_whole_stack_into_the_one_in_the_pack() {
    let mut f = Fixture::new();
    let (source, target) = (0x6100_0040, 0x6100_0041);
    add_stack(&mut f, source, JOIN_KIND, 5, PLAYER);
    add_stack(&mut f, target, JOIN_KIND, 7, PACK);
    assert!(f.run(join_move(source, PACK, 0, 3, true)));
    assert_eq!(f.sent(), [build_stackable_merge(1, source, target, 5)]);
    let pending = f.inventory.transactions().pending().unwrap();
    assert_eq!(pending.kind, InventoryRequestKind::Merge);
    assert_eq!(pending.item_id, source);
}

#[test]
fn a_joining_split_pours_only_the_requested_amount() {
    let mut f = Fixture::new();
    let (source, target) = (0x6100_0042, 0x6100_0043);
    add_stack(&mut f, source, JOIN_KIND, 10, PLAYER);
    add_stack(&mut f, target, JOIN_KIND, 7, PACK);
    assert!(f.run(join_move(source, PACK, 4, 0, true)));
    assert_eq!(f.sent(), [build_stackable_merge(1, source, target, 4)]);
}

#[test]
fn a_joining_move_places_plainly_when_no_stack_takes_all_of_it() {
    // A stack with room for only part of what moves is passed over, as is
    // a stack of something else; nothing matching is left, so the move is
    // the plain one.
    let mut f = Fixture::new();
    let (source, nearly_full, other_kind) = (0x6100_0044, 0x6100_0045, 0x6100_0046);
    add_stack(&mut f, source, JOIN_KIND, 5, PLAYER);
    add_stack(&mut f, nearly_full, JOIN_KIND, 98, PACK);
    add_stack(&mut f, other_kind, JOIN_KIND + 1, 1, PACK);
    assert!(f.run(join_move(source, PACK, 0, 2, true)));
    assert_eq!(f.sent(), [build_put_item_in_container(1, source, PACK, 2)]);
}

#[test]
fn a_move_without_joining_never_merges() {
    let mut f = Fixture::new();
    let (source, target) = (0x6100_0047, 0x6100_0048);
    add_stack(&mut f, source, JOIN_KIND, 5, PLAYER);
    add_stack(&mut f, target, JOIN_KIND, 7, PACK);
    assert!(f.run(join_move(source, PACK, 0, 1, false)));
    assert_eq!(f.sent(), [build_put_item_in_container(1, source, PACK, 1)]);
}

#[test]
fn a_joining_move_looks_at_the_containers_own_items_before_its_packs() {
    // The pack is listed first in the main pack, but the stacks lying loose
    // in the main pack are searched before any pack's contents.
    let mut f = Fixture::new();
    let (source, in_the_pack, loose) = (0x6100_0049, 0x6100_004A, 0x6100_004B);
    add_stack(&mut f, source, JOIN_KIND, 5, PACK);
    add_stack(&mut f, in_the_pack, JOIN_KIND, 7, PACK);
    add_stack(&mut f, loose, JOIN_KIND, 9, PLAYER);
    assert_eq!(f.world.items().get_contents(PLAYER)[0], PACK);
    assert!(f.run(join_move(source, PLAYER, 0, 0, true)));
    assert_eq!(f.sent(), [build_stackable_merge(1, source, loose, 5)]);
}

#[test]
fn merges_drops_and_gives_send_their_requests() {
    // WorldSessionInventoryActionTests: StackableMerge, SplitTo3D, GiveObject.
    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::Merge {
        from: guid(GEM2),
        to: guid(GEM),
        amount: 0,
    }));
    assert_eq!(f.sent(), [build_stackable_merge(1, GEM2, GEM, 3)]);

    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::Drop {
        item: guid(GEM),
        amount: 2,
    }));
    assert_eq!(f.sent(), [build_stackable_split_to_3d(1, GEM, 2)]);

    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::Drop {
        item: guid(SWORD),
        amount: 0,
    }));
    assert_eq!(f.sent(), [build_drop_item(1, SWORD)]);

    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::Give {
        item: guid(GEM),
        target: guid(NPC),
        amount: 3,
    }));
    assert_eq!(f.sent(), [build_give_object(1, NPC, GEM, 3)]);
    // Never to oneself, never something not owned.
    let mut f = Fixture::new();
    assert!(!f.run(ItemCommand::Give {
        item: guid(GEM),
        target: ObjectRef::Player,
        amount: 0,
    }));
    assert!(!f.run(ItemCommand::Give {
        item: guid(LOOSE),
        target: guid(NPC),
        amount: 0,
    }));
}

#[test]
fn a_refused_move_names_the_item_and_the_reason() {
    // OpenAC RuntimeItemInteraction.OnInventoryRequestFailed with
    // InventoryFailureMessages.Compose.
    let mut f = Fixture::new();
    f.run(ItemCommand::MoveToContainer {
        item: guid(SWORD),
        container: guid(PACK),
        slot: 0,
        amount: 0,
        join_stack: false,
    });
    f.sent();
    f.server(GameEventType::INVENTORY_SERVER_SAVE_FAILED, &[SWORD, 0x1D]);
    let notices = f.notices();
    assert!(notices.contains(&Notice::Interface(
        "The Sword can't be put in the container - you're too busy".into()
    )));
    assert!(notices.contains(&Notice::WeenieError(0x1D)));
    assert!(f.inventory.transactions().can_begin_request());
}

/// The player as the server describes him: a creature that holds packs and
/// is not useable (the main pack is the character itself).
fn make_the_player_a_pack(f: &mut Fixture) {
    f.world.apply(&create(PLAYER, "Horan", |d| {
        d.object_description_flags = (PublicWeenieFlags::PLAYER | PublicWeenieFlags::STUCK).bits();
        d.items_capacity = Some(102);
        d.containers_capacity = Some(7);
        d.item_type = ItemType::CREATURE.bits();
        d.useability = Some(ItemUseable::NO.bits());
    }));
}

#[test]
fn using_the_player_opens_the_main_pack_without_a_use_request() {
    // OpenAC ItemInteractionControllerTests
    // .UsingThePlayer_opensTheMainPackWithoutAUseRequest: a hotbar shortcut
    // to the main pack opens it in the inventory window and asks the server
    // for nothing. A use request for the character is never answered on some
    // servers, and its busy count then refused every later use.
    let mut f = Fixture::new();
    make_the_player_a_pack(&mut f);
    assert!(f.run(ItemCommand::Use(ObjectRef::Player)));
    assert!(f.sent().is_empty());
    assert_eq!(f.inventory.transactions().busy_count(), 0);
    assert!(f.inventory.transactions().can_begin_request());
    assert_eq!(f.events(), [InventoryEvent::OpenContainedContainer(PLAYER)]);
}

#[test]
fn using_a_carried_pack_sends_one_use_and_opens_it_in_the_inventory() {
    // OpenAC ItemInteractionControllerTests
    // .UsingACarriedPack_sendsOneUseAndOpensThePackInTheInventory.
    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::Use(guid(PACK))));
    assert_eq!(f.sent(), [build_use(1, PACK)]);
    assert_eq!(f.inventory.transactions().busy_count(), 1);
    assert_eq!(f.events(), [InventoryEvent::OpenContainedContainer(PACK)]);
}

#[test]
fn a_use_counts_busy_until_the_use_done_and_is_paced() {
    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::Use(guid(NPC))));
    assert_eq!(f.sent(), [build_use(1, NPC)]);
    assert_eq!(f.inventory.transactions().busy_count(), 1);
    // Busy: an item request is refused.
    assert!(!f.run(ItemCommand::Drop {
        item: guid(SWORD),
        amount: 0,
    }));
    f.notices();
    f.server(GameEventType::USE_DONE, &[0]);
    assert_eq!(f.inventory.transactions().busy_count(), 0);
    // A second use inside 200 ms is swallowed.
    f.now_ms += 100;
    assert!(f.run(ItemCommand::Use(guid(NPC))));
    assert!(f.sent().is_empty());
    f.now_ms += 100;
    assert!(f.run(ItemCommand::Use(guid(NPC))));
    assert_eq!(f.sent(), [build_use(2, NPC)]);
    // A use-done with an error is worded for the player (after the use's
    // own line, the client's "Approaching %s" for a creature).
    f.server(GameEventType::USE_DONE, &[0x0402]);
    assert_eq!(
        f.notices(),
        [
            Notice::Interface("Approaching Town Crier".into()),
            Notice::WeenieError(0x0402)
        ]
    );
}

#[test]
fn using_a_loose_item_picks_it_up_and_joins_a_held_stack() {
    // OpenAC TryPlaceWorldItemInBackpack: a stack that fits whole into one
    // the character holds goes as that merge.
    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::PickUp(guid(LOOSE))));
    assert_eq!(f.sent(), [build_stackable_merge(1, LOOSE, GEM, 2)]);
    // Something that joins nothing goes into the main pack.
    let mut f = Fixture::new();
    f.world.apply(&create(0x7000_0009, "Scroll", |d| {
        d.item_type = ItemType::WRITABLE.bits();
    }));
    assert!(f.run(ItemCommand::Use(guid(0x7000_0009))));
    assert_eq!(
        f.sent(),
        [build_put_item_in_container(1, 0x7000_0009, PLAYER, 0)]
    );
}

#[test]
fn a_pickup_names_the_open_pack_unless_the_main_pack_is_preferred() {
    // OpenAC `RuntimeItemInteraction.PreferredBackpackContainer`: the pack
    // the inventory panel shows open, unless the main pack is asked for or
    // the MainPackPreferred option is on; no open pack is the main pack.
    const SCROLL: u32 = 0x7000_0009;
    let scroll = |f: &mut Fixture| {
        f.world.apply(&create(SCROLL, "Scroll", |d| {
            d.item_type = ItemType::WRITABLE.bits();
        }));
    };
    let mut f = Fixture::new();
    scroll(&mut f);
    assert!(f.run(ItemCommand::SetOpenPack(PACK)));
    assert!(f.run(ItemCommand::PickUp(guid(SCROLL))));
    assert_eq!(f.sent(), [build_put_item_in_container(1, SCROLL, PACK, 0)]);

    let mut f = Fixture::new();
    scroll(&mut f);
    f.run(ItemCommand::SetOpenPack(PACK));
    assert!(f.run(ItemCommand::PickUpToMainPack(guid(SCROLL))));
    assert_eq!(
        f.sent(),
        [build_put_item_in_container(1, SCROLL, PLAYER, 0)]
    );

    let mut f = Fixture::new();
    scroll(&mut f);
    f.run(ItemCommand::SetOpenPack(PACK));
    f.options
        .set_bit(crate::character::option_id::MAIN_PACK_PREFERRED, true);
    assert!(f.run(ItemCommand::PickUp(guid(SCROLL))));
    assert_eq!(
        f.sent(),
        [build_put_item_in_container(1, SCROLL, PLAYER, 0)]
    );
}

#[test]
fn wield_goes_out_at_once_when_the_hand_is_free() {
    let mut f = Fixture::new();
    // Take the axe off first: it goes to the main pack.
    assert!(f.run(ItemCommand::Unwield(guid(AXE))));
    assert_eq!(f.sent(), [build_put_item_in_container(1, AXE, PLAYER, 0)]);
    f.server(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[AXE, PLAYER, 0, 0],
    );
    assert!(f.run(ItemCommand::Wield {
        item: guid(SWORD),
        location: 0,
    }));
    assert_eq!(
        f.sent(),
        [build_get_and_wield_item(
            2,
            SWORD,
            EquipMask::MELEE_WEAPON.bits()
        )]
    );
    f.server(
        GameEventType::WIELD_OBJECT,
        &[SWORD, EquipMask::MELEE_WEAPON.bits()],
    );
    assert!(!f.inventory.is_wield_busy());
    assert_eq!(f.world.item(SWORD).unwrap().wielder_id, PLAYER);
}

#[test]
fn taking_off_into_a_full_main_pack_chooses_a_side_pack() {
    // OpenAC: a wielded item's primary use is PlaceInBackpack
    // (ItemInteractionPolicy.DetermineUseResult), which places through
    // InventoryPlacementSearch.ChooseContainer: the main pack, else the
    // first side pack with room. The main pack here holds three items and
    // takes three.
    let mut f = Fixture::new();
    f.world.apply(&create(PLAYER, "Horan", |d| {
        d.object_description_flags = PublicWeenieFlags::PLAYER.bits();
        d.items_capacity = Some(3);
        d.containers_capacity = Some(7);
        d.item_type = ItemType::CREATURE.bits();
    }));
    f.world.take_table_events();
    assert!(f.run(ItemCommand::Unwield(guid(AXE))));
    assert_eq!(f.sent(), [build_put_item_in_container(1, AXE, PACK, 0)]);
    // Only what is worn is taken off.
    let mut f = Fixture::new();
    assert!(!f.run(ItemCommand::Unwield(guid(GEM))));
    assert!(f.sent().is_empty());
}

#[test]
fn a_new_generation_of_the_blocker_cancels_the_switch() {
    // OpenAC AutoWieldGenerationTests.GenerationReplacementCancelsPendingWeaponSwitch:
    // the worn axe is being moved aside for the sword when the server
    // re-creates it as a new incarnation; the switch is over.
    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::Wield {
        item: guid(SWORD),
        location: 0,
    }));
    assert_eq!(f.sent(), [build_put_item_in_container(1, AXE, PLAYER, 0)]);
    assert!(f.inventory.is_wield_busy());
    f.world.apply(&Inbound::CreateObject(Box::new(CreateObject {
        guid: AXE,
        model: ModelData::default(),
        physics: physics(
            PhysicsTimestamps {
                instance: 2,
                ..PhysicsTimestamps::default()
            },
            None,
        ),
        weenie: Some(PublicWeenieDesc {
            name: "replacement".into(),
            ..PublicWeenieDesc::default()
        }),
    })));
    let mut inventory = std::mem::take(&mut f.inventory);
    inventory.observe(&mut f.ctx());
    f.inventory = inventory;
    assert!(!f.inventory.is_wield_busy());
}

#[test]
fn a_second_weapon_first_clears_the_shield_hand() {
    // OpenAC AutoWieldGenerationTests.SecondaryMeleeIntentClearsShieldBlockerBeforeWield:
    // the shield is moved to the main pack, then the sword is wielded in
    // the shield slot (0x00200000).
    const SHIELD: u32 = 0x6000_0010;
    let mut f = Fixture::new();
    f.world.apply(&create(SHIELD, "Shield", |d| {
        d.item_type = ItemType::ARMOR.bits();
        d.valid_locations = Some(EquipMask::SHIELD.bits());
    }));
    let body = crate::world::tests::game_event(
        GameEventType::WIELD_OBJECT,
        &[SHIELD, EquipMask::SHIELD.bits()],
    );
    f.world.apply(&Inbound::parse(&body).unwrap());
    f.world.take_table_events();
    let mut inventory = std::mem::take(&mut f.inventory);
    assert!(inventory.try_wield_secondary(SWORD, &mut f.ctx()));
    f.inventory = inventory;
    assert_eq!(
        f.sent(),
        [build_put_item_in_container(1, SHIELD, PLAYER, 0)]
    );
    f.server(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[SHIELD, PLAYER, 0, 0],
    );
    assert_eq!(f.sent(), [build_get_and_wield_item(2, SWORD, 0x0020_0000)]);
}

// OpenAC issue #225
// `HandleDropRelease_meleeWeaponOnShieldSlot_wieldsItInTheShieldSlot`: the
// paperdoll's off-hand drop reaches the runtime as a command; with the
// main hand armed and the shield slot empty, the weapon goes straight to
// the shield slot and the main-hand weapon stays.
#[test]
fn an_off_hand_wield_command_wields_in_the_shield_slot() {
    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::WieldSecondary(guid(SWORD))));
    assert_eq!(
        f.sent(),
        [build_get_and_wield_item(1, SWORD, EquipMask::SHIELD.bits())]
    );
    assert_eq!(
        f.world.item(SWORD).unwrap().currently_equipped_location,
        EquipMask::NONE,
        "nothing moves until the server says"
    );
}

// OpenAC issue #225
// `HandleDropRelease_meleeWeaponOnOccupiedShieldSlot_movesTheShieldToThePackFirst`.
#[test]
fn an_off_hand_wield_command_first_moves_the_shield_to_the_pack() {
    const SHIELD: u32 = 0x6000_0010;
    let mut f = Fixture::new();
    f.world.apply(&create(SHIELD, "Buckler", |d| {
        d.item_type = ItemType::ARMOR.bits();
        d.valid_locations = Some(EquipMask::SHIELD.bits());
    }));
    let body = crate::world::tests::game_event(
        GameEventType::WIELD_OBJECT,
        &[SHIELD, EquipMask::SHIELD.bits()],
    );
    f.world.apply(&Inbound::parse(&body).unwrap());
    f.world.take_table_events();
    assert!(f.run(ItemCommand::WieldSecondary(guid(SWORD))));
    assert_eq!(
        f.sent(),
        [build_put_item_in_container(1, SHIELD, PLAYER, 0)]
    );
    assert_eq!(
        f.notices(),
        [Notice::Interface("Moving Buckler to your backpack".into())]
    );
    f.server(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[SHIELD, PLAYER, 0, 0],
    );
    assert_eq!(
        f.sent(),
        [build_get_and_wield_item(2, SWORD, EquipMask::SHIELD.bits())]
    );
}

// OpenAC issue #225
// `HandleDropRelease_shieldOnShieldSlotHoldingAnOffHandWeapon_movesTheWeaponToThePackFirst`:
// a shield dropped on the shield slot while a sword sits there as the off
// hand reaches the runtime as a shield-slot wield; the sword goes to the
// backpack first, then the shield is wielded.
#[test]
fn a_shield_slot_wield_first_moves_the_off_hand_weapon_to_the_pack() {
    const OFF_HAND: u32 = 0x6000_0011;
    const SHIELD: u32 = 0x6000_0012;
    let mut f = Fixture::new();
    f.world.apply(&create(OFF_HAND, "Short Sword", |d| {
        d.item_type = ItemType::MELEE_WEAPON.bits();
        d.valid_locations = Some(EquipMask::MELEE_WEAPON.bits());
        d.combat_use = Some(1);
    }));
    f.world.apply(&create(SHIELD, "Buckler", |d| {
        d.item_type = ItemType::ARMOR.bits();
        d.valid_locations = Some(EquipMask::SHIELD.bits());
    }));
    let body = crate::world::tests::game_event(
        GameEventType::WIELD_OBJECT,
        &[OFF_HAND, EquipMask::SHIELD.bits()],
    );
    f.world.apply(&Inbound::parse(&body).unwrap());
    let body = crate::world::tests::game_event(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[SHIELD, PLAYER, 0, 0],
    );
    f.world.apply(&Inbound::parse(&body).unwrap());
    f.world.take_table_events();
    assert!(f.run(ItemCommand::Wield {
        item: guid(SHIELD),
        location: EquipMask::SHIELD.bits(),
    }));
    assert_eq!(
        f.sent(),
        [build_put_item_in_container(1, OFF_HAND, PLAYER, 0)]
    );
    assert_eq!(
        f.notices(),
        [Notice::Interface(
            "Moving Short Sword to your backpack".into()
        )]
    );
    f.server(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[OFF_HAND, PLAYER, 0, 0],
    );
    assert_eq!(
        f.sent(),
        [build_get_and_wield_item(
            2,
            SHIELD,
            EquipMask::SHIELD.bits()
        )]
    );
}

// -- The stance settlement after a wield in combat ------------------------------
//
// OpenAC `tests/AcDream.App.Tests/UI/ItemInteractionControllerTests.cs`
// (`WandToBow_reassertsMissileModeOnlyForActiveCombatTransaction`,
// `BowToWand_inActiveCombat_reassertsMagicAfterAceTrailingPeace`,
// `ExplicitCombatRequest_cancelsPendingAutoWieldSettlement`,
// `ActiveWeaponReplacement_serverSettlesDirectly_doesNotReassertLaterPeace`),
// with the sword replacing the axe (melee is the ready mode).

use ac_core::combat::CombatMode;
use ac_proto::combat::build_change_combat_mode;

impl Fixture {
    fn mode(&mut self, mode: CombatMode) {
        self.in_peace = mode == CombatMode::NON_COMBAT;
        let mut inventory = std::mem::take(&mut self.inventory);
        inventory.on_combat_mode_changed(mode, &mut self.ctx());
        self.inventory = inventory;
    }

    /// Wield the sword over the axe: the axe goes to the pack, then the
    /// sword is wielded and confirmed, with `during` happening while the
    /// axe moves.
    fn swap_to_sword(&mut self, during: &[CombatMode]) {
        assert!(self.run(ItemCommand::Wield {
            item: guid(SWORD),
            location: 0,
        }));
        for &mode in during {
            self.mode(mode);
        }
        self.server(
            GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
            &[AXE, PLAYER, 0, 0],
        );
        self.server(
            GameEventType::WIELD_OBJECT,
            &[SWORD, EquipMask::MELEE_WEAPON.bits()],
        );
        self.sent();
    }
}

#[test]
fn a_wield_in_combat_asks_for_its_stance_again_after_the_shuffle() {
    let mut f = Fixture::new();
    f.mode(CombatMode::MAGIC);
    f.swap_to_sword(&[CombatMode::MISSILE]);
    f.mode(CombatMode::NON_COMBAT);
    f.mode(CombatMode::MELEE);
    assert!(f.sent().is_empty());
    f.mode(CombatMode::NON_COMBAT);
    assert_eq!(
        f.sent(),
        [build_change_combat_mode(3, CombatMode::MELEE.bits())]
    );
}

#[test]
fn a_wield_at_peace_asks_for_no_stance() {
    let mut f = Fixture::new();
    f.swap_to_sword(&[CombatMode::MISSILE]);
    f.mode(CombatMode::MELEE);
    f.mode(CombatMode::NON_COMBAT);
    assert!(f.sent().is_empty());
}

#[test]
fn after_a_trailing_peace_the_ready_stance_is_asked_for_again() {
    let mut f = Fixture::new();
    f.mode(CombatMode::MISSILE);
    f.swap_to_sword(&[CombatMode::MAGIC, CombatMode::NON_COMBAT]);
    f.mode(CombatMode::MELEE);
    assert!(f.sent().is_empty());
    f.mode(CombatMode::NON_COMBAT);
    assert_eq!(
        f.sent(),
        [build_change_combat_mode(3, CombatMode::MELEE.bits())]
    );
}

#[test]
fn a_stance_the_player_asks_for_stands_the_settlement_down() {
    let mut f = Fixture::new();
    f.mode(CombatMode::MISSILE);
    f.swap_to_sword(&[CombatMode::NON_COMBAT]);
    f.mode(CombatMode::MELEE);
    f.inventory.notify_explicit_combat_mode_request();
    f.mode(CombatMode::NON_COMBAT);
    assert!(f.sent().is_empty());
}

#[test]
fn a_server_that_settles_directly_is_not_asked_again() {
    let mut f = Fixture::new();
    f.mode(CombatMode::MAGIC);
    f.swap_to_sword(&[]);
    f.mode(CombatMode::MELEE);
    f.mode(CombatMode::NON_COMBAT);
    assert!(f.sent().is_empty());
}

#[test]
fn wielding_over_a_weapon_first_moves_it_to_the_backpack() {
    // OpenAC AutoWieldController.BeginWeaponReplacement, then OnObjectMoved.
    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::Wield {
        item: guid(SWORD),
        location: 0,
    }));
    assert_eq!(f.sent(), [build_put_item_in_container(1, AXE, PLAYER, 0)]);
    assert_eq!(
        f.notices(),
        [Notice::Interface("Moving Axe to your backpack".into())]
    );
    assert!(f.inventory.is_wield_busy());
    f.server(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[AXE, PLAYER, 0, 0],
    );
    assert_eq!(
        f.sent(),
        [build_get_and_wield_item(
            2,
            SWORD,
            EquipMask::MELEE_WEAPON.bits()
        )]
    );
}

// OpenAC issue #253: with the main pack full, the weapon in hand goes to the
// first side pack with room, not to the main pack the server cannot fill.
// OpenAC `ItemInteractionControllerTests.WieldWithFullMainPack_*`.
#[test]
fn wielding_with_a_full_main_pack_moves_the_weapon_to_a_side_pack() {
    // The gems and the sword fill the three main-pack slots.
    let mut f = Fixture::with_main_pack_slots(3);
    assert!(f.run(ItemCommand::Wield {
        item: guid(SWORD),
        location: 0,
    }));
    assert_eq!(f.sent(), [build_put_item_in_container(1, AXE, PACK, 0)]);
    assert_eq!(
        f.notices(),
        [Notice::Interface("Moving Axe to your backpack".into())]
    );
}

#[test]
fn a_weapon_landing_in_a_side_pack_ends_the_switch_without_wielding() {
    let mut f = Fixture::with_main_pack_slots(3);
    assert!(f.run(ItemCommand::Wield {
        item: guid(SWORD),
        location: 0,
    }));
    f.sent();
    f.server(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[AXE, PACK, 0, 0],
    );
    // The wield follows only a weapon that landed in the main pack.
    assert!(f.sent().is_empty());
    assert!(!f.inventory.is_wield_busy());
    // The switch is over, so the next attempt wields straight away.
    assert!(f.run(ItemCommand::Wield {
        item: guid(SWORD),
        location: 0,
    }));
    assert_eq!(
        f.sent(),
        [build_get_and_wield_item(
            2,
            SWORD,
            EquipMask::MELEE_WEAPON.bits()
        )]
    );
}

#[test]
fn wielding_with_every_pack_full_sends_nothing_and_says_so() {
    // One gem goes into the one-slot side pack; the other gem and the sword
    // fill the two main-pack slots.
    let mut f = Fixture::with_main_pack_slots(2);
    f.world.apply(&create(PACK, "Pack", |d| {
        d.item_type = ItemType::CONTAINER.bits();
        d.items_capacity = Some(1);
    }));
    f.server(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[GEM2, PACK, 0, 0],
    );
    f.notices();
    assert!(!f.run(ItemCommand::Wield {
        item: guid(SWORD),
        location: 0,
    }));
    assert!(f.sent().is_empty());
    assert!(!f.inventory.is_wield_busy());
    assert_eq!(
        f.notices(),
        [
            Notice::Interface("Backpack is completely full!".into()),
            Notice::Interface("Moving Axe to your backpack - cannot unwield the Axe".into()),
        ]
    );
    assert_eq!(
        f.world.item(AXE).unwrap().currently_equipped_location,
        EquipMask::MELEE_WEAPON
    );
}

#[test]
fn an_appraisal_updates_the_item_and_answers_the_host() {
    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::Appraise(guid(SWORD))));
    assert_eq!(f.sent(), [build_appraise(1, SWORD)]);
    // One question at a time.
    assert!(!f.inventory.interaction().can_begin_appraisal(f.now_ms));
    let mut response = IdentifyObjectResponse {
        guid: SWORD,
        success: true,
        ..IdentifyObjectResponse::default()
    };
    response.properties.ints.insert(105, 7);
    response.spell_book = vec![0x0F0];
    f.world.apply_appraisal(&response, 12.5);
    f.inventory.accept_appraisal(&response, &f.world);
    let sword = f.world.item(SWORD).unwrap();
    assert_eq!(sword.properties.ints.get(&105), Some(&7));
    assert_eq!(sword.appraised_spell_ids, [0x0F0]);
    assert_eq!(sword.last_appraisal_time_ms, 12_500);
    match f.events().as_slice() {
        [InventoryEvent::Appraisal(view)] => {
            assert_eq!(view.name.as_deref(), Some("Sword"));
            assert_eq!(view.response.guid, SWORD);
            // The player's own question: the examination window opens on it.
            assert!(view.present_in_ui && view.opens_window);
        }
        other => panic!("expected one appraisal, got {other:?}"),
    }
    assert!(f.inventory.interaction().can_begin_appraisal(f.now_ms));
}

#[test]
fn an_unsuccessful_appraisal_merges_nothing_but_counts_as_an_answer() {
    // OpenAC `GameEventWiringTests`: a server that no longer has
    // an object answers with nothing; the object keeps no property, but
    // remembers that the server answered.
    let mut f = Fixture::new();
    assert!(!f.world.item(SWORD).unwrap().appraisal_answered);
    let mut response = IdentifyObjectResponse {
        guid: SWORD,
        success: false,
        ..IdentifyObjectResponse::default()
    };
    response.properties.ints.insert(25, 42);
    f.world.apply_appraisal(&response, 1.0);
    let sword = f.world.item(SWORD).unwrap();
    assert!(!sword.properties.ints.contains_key(&25));
    assert!(sword.appraisal_answered);
    response.success = true;
    f.world.apply_appraisal(&response, 2.0);
    let sword = f.world.item(SWORD).unwrap();
    assert_eq!(sword.properties.ints.get(&25), Some(&42));
    assert!(sword.appraisal_answered);
}

/// OpenAC `AppraisalUiControllerTests.CreatureResponse_SelectsCreatureSubviewAndRefreshesInCombatWithoutBusy`
/// (the runtime half): the refresh asks again about the object shown,
/// takes no appraisal count, and its answer refreshes the window without
/// opening it; with nothing shown it asks nothing.
#[test]
fn a_refresh_asks_again_about_the_shown_object_without_a_busy_count() {
    let mut f = Fixture::new();
    assert!(!f.run(ItemCommand::RefreshCurrentAppraisal));
    assert!(f.sent().is_empty());
    assert!(f.run(ItemCommand::Appraise(guid(SWORD))));
    let response = IdentifyObjectResponse {
        guid: SWORD,
        success: true,
        ..IdentifyObjectResponse::default()
    };
    f.world.apply_appraisal(&response, 1.0);
    f.inventory.accept_appraisal(&response, &f.world);
    f.sent();
    f.events();
    assert!(f.run(ItemCommand::RefreshCurrentAppraisal));
    assert_eq!(f.sent(), [build_appraise(2, SWORD)]);
    // No question waits: another one may be asked at once.
    assert!(f.inventory.interaction().can_begin_appraisal(f.now_ms));
    f.inventory.accept_appraisal(&response, &f.world);
    match f.events().as_slice() {
        [InventoryEvent::Appraisal(view)] => {
            assert!(view.present_in_ui && !view.opens_window);
        }
        other => panic!("expected one appraisal, got {other:?}"),
    }
}

/// OpenAC `AppraisalUiController.Apply` and the runtime's `PresentInUi`: a
/// question asked for the player (a walk checking whether a door is
/// locked) updates the object but never reaches the examination window.
/// An acceptance run saw `/nav`'s
/// door check open the window on the door.
#[test]
fn a_quiet_appraisal_never_reaches_the_examination_window() {
    let mut f = Fixture::new();
    let mut outbox = std::mem::take(&mut f.outbox);
    assert!(
        f.inventory
            .appraise_quietly(SWORD, f.now_ms, true, &mut outbox)
    );
    f.outbox = outbox;
    assert_eq!(f.sent(), [build_appraise(1, SWORD)]);
    let response = IdentifyObjectResponse {
        guid: SWORD,
        success: true,
        ..IdentifyObjectResponse::default()
    };
    f.world.apply_appraisal(&response, 1.0);
    f.inventory.accept_appraisal(&response, &f.world);
    match f.events().as_slice() {
        [InventoryEvent::Appraisal(view)] => {
            assert!(!view.present_in_ui && !view.opens_window);
        }
        other => panic!("expected one appraisal, got {other:?}"),
    }
}

/// A crafting calculator reads the tinker values off the item table's
/// record: the description's workmanship and material, the appraisal's
/// properties and weapon profile (OpenAC
/// `RuntimeInventoryItemTinkerValuesTests`, here through a real appraisal).
#[test]
fn tinker_values_are_projected_from_the_appraisal() {
    use ac_core::items::TinkerValues;
    use ac_core::properties::{PropertyBool, PropertyFloat, PropertyInt};
    use ac_proto::appraise::WeaponProfile;

    let mut f = Fixture::new();
    f.world.apply(&create(SWORD, "Sword", |d| {
        d.item_type = ItemType::MELEE_WEAPON.bits();
        d.workmanship = Some(7.25);
        d.material_type = Some(63);
        d.structure = Some(83);
    }));
    let before = TinkerValues::of(f.world.item(SWORD).unwrap());
    assert_eq!(before.num_times_tinkered, 0);
    assert!(!before.retained);

    let mut response = IdentifyObjectResponse {
        guid: SWORD,
        success: true,
        weapon_profile: Some(WeaponProfile {
            damage_type: 0x01,
            weapon_time: 30,
            weapon_skill: 45,
            damage: 46,
            damage_variance: 0.42,
            damage_mod: 1.0,
            weapon_length: 1.0,
            max_velocity: 1.0,
            weapon_offense: 1.0,
            max_velocity_estimated: 0,
        }),
        ..IdentifyObjectResponse::default()
    };
    let p = &mut response.properties;
    p.ints.insert(PropertyInt::NUM_TIMES_TINKERED.0, 4);
    p.ints.insert(PropertyInt::IMBUED_EFFECT.0, 0x0080);
    p.ints.insert(PropertyInt::DAMAGE_TYPE.0, 0x40);
    p.floats.insert(PropertyFloat::DAMAGE_VARIANCE.0, 0.9);
    p.bools.insert(PropertyBool::RETAINED.0, true);
    f.world.apply_appraisal(&response, 1.0);

    let v = TinkerValues::of(f.world.item(SWORD).unwrap());
    assert!((v.salvage_workmanship - 7.25).abs() < 1e-4);
    assert_eq!(v.material_type, 63);
    assert_eq!(v.structure, 83);
    assert_eq!(v.num_times_tinkered, 4);
    assert_eq!(v.imbued_effect, 0x0080);
    assert!(v.retained);
    // The appraised weapon wins for damage, its kind and its variance; the
    // wand element stays the property.
    assert_eq!((v.damage, v.max_damage), (46, 46));
    assert_eq!(v.damage_type, 0x01);
    assert_eq!(v.wand_elemental_damage_type, 0x40);
    assert!((v.damage_variance - 0.42).abs() < 1e-9);
}

#[test]
fn names_resolve_to_the_players_items_before_the_world() {
    let mut f = Fixture::new();
    // "gem" is held twice and lies once in the world; the held ones win.
    assert!(f.run(ItemCommand::Appraise(ObjectRef::Name("gem".into()))));
    let sent = f.sent();
    assert!(sent == [build_appraise(1, GEM)] || sent == [build_appraise(1, GEM2)]);
    // A prefix reaches the NPC (no position: nothing nearer).
    let mut f = Fixture::new();
    assert!(!f.run(ItemCommand::Use(ObjectRef::Name("nobody".into()))));
    assert_eq!(
        f.events(),
        [InventoryEvent::Refused(
            "Nothing called \"nobody\" is known.".into()
        )]
    );
}

#[test]
fn a_targeted_use_goes_out_with_its_target() {
    let mut f = Fixture::new();
    f.world.apply(&create(0x6000_0020, "Healing Kit", |d| {
        d.item_type = ItemType::MISC.bits();
        // Used from the pack on oneself or something remote.
        d.useability = Some(0x0022_0008);
        d.target_type = Some(ItemType::CREATURE.bits());
    }));
    f.world.take_table_events();
    let mut description = empty_description();
    description.inventory = vec![ac_proto::InventoryEntry {
        guid: 0x6000_0020,
        container_type: 0,
    }];
    f.world.apply_player_description(&description);
    f.world.take_table_events();
    assert!(f.run(ItemCommand::UseOn {
        item: guid(0x6000_0020),
        target: ObjectRef::Player,
    }));
    assert_eq!(f.sent(), [build_use_with_target(1, 0x6000_0020, PLAYER)]);
}

fn approach() -> ApproachVendor {
    ApproachVendor {
        vendor_guid: VENDOR,
        profile: VendorProfile {
            merchandise_item_types: ItemType::GEM.bits(),
            merchandise_min_value: 0,
            merchandise_max_value: 1000,
            deal_magical_items: false,
            buy_price: 0.5,
            sell_price: 1.5,
            alternate_currency_wcid: 0,
            alternate_currency_amount: 0,
            alternate_currency_plural_name: String::new(),
        },
        items: vec![VendorItem {
            stack_size: -1,
            item_guid: 0x7000_0100,
            desc: PublicWeenieDesc {
                name: "Diamond".into(),
                item_type: ItemType::GEM.bits(),
                value: Some(100),
                ..PublicWeenieDesc::default()
            },
        }],
    }
}

#[test]
fn a_buy_takes_the_shared_action_number_and_waits_for_the_listing() {
    // WorldSessionVendorTests.SendBuy_IncrementsTheSharedGameActionSequence*.
    let mut f = Fixture::new();
    let mut tx = Vec::new();
    f.inventory.apply_vendor(&approach(), &mut f.world, &mut tx);
    match f.events().as_slice() {
        [InventoryEvent::Vendor(Some(view))] => {
            assert_eq!(view.vendor_guid, VENDOR);
            assert_eq!(view.name.as_deref(), Some("Shopkeeper"));
            assert_eq!(view.items[0].name.as_deref(), Some("Diamond"));
        }
        other => panic!("expected the shop, got {other:?}"),
    }
    f.outbox.send_action(ac_proto::build_house_query);
    assert!(f.run(ItemCommand::Buy(vec![(
        ObjectRef::Name("diamond".into()),
        1
    )])));
    let sent = f.sent();
    assert_eq!(
        sent[1],
        build_buy(
            2,
            VENDOR,
            &[ItemAmount {
                amount: 1,
                item_guid: 0x7000_0100
            }],
            0
        )
    );
    assert!(f.inventory.transactions().has_pending_request());
    // The refreshed listing completes the shop request.
    f.inventory.apply_vendor(&approach(), &mut f.world, &mut tx);
    assert!(!f.inventory.transactions().has_pending_request());
}

#[test]
fn a_purchase_in_an_alternate_currency_sends_the_shop_again_with_the_purse_spent() {
    // VendorUiController.BuyButtonPressed then
    // RecordAlternateCurrencyPurchase: the purse shows the price spent as
    // soon as the purchase goes out, before the inventory shows it.
    let mut f = Fixture::new();
    let mut shop = approach();
    shop.profile.alternate_currency_wcid = 0x3400;
    shop.profile.alternate_currency_amount = 500;
    shop.profile.alternate_currency_plural_name = "Trade Notes".into();
    let mut tx = Vec::new();
    f.inventory.apply_vendor(&shop, &mut f.world, &mut tx);
    f.events();
    assert!(f.run(ItemCommand::Buy(vec![(guid(DIAMOND), 1)])));
    match f.events().as_slice() {
        [InventoryEvent::Vendor(Some(view))] => {
            assert_eq!(view.reason, VendorViewReason::Lists);
            // One diamond at 1.5 x 100.
            assert_eq!(view.lists.money, 350);
            assert_eq!(view.lists.purse_text, "You have 350 Trade Notes.");
        }
        other => panic!("expected the shop again, got {other:?}"),
    }
    // A pyreal shop's purse reads the coin value live: nothing is sent.
    let mut f = Fixture::new();
    f.inventory.apply_vendor(&approach(), &mut f.world, &mut tx);
    f.events();
    assert!(f.run(ItemCommand::Buy(vec![(guid(DIAMOND), 1)])));
    assert!(f.events().is_empty());
}

#[test]
fn trade_staging_marks_the_players_item() {
    // RuntimeTradeStateTests.ApplyAdd_marksTheObjectAndPublishesTheChangeOnce.
    let mut f = Fixture::new();
    f.server(
        GameEventType::REGISTER_TRADE,
        &[OTHER_PLAYER, OTHER_PLAYER, 0, 0],
    );
    f.server(GameEventType::ADD_TO_TRADE, &[GEM, SIDE_SELF, 0]);
    assert_eq!(f.world.item(GEM).unwrap().trade_state, 1);
    assert_eq!(f.inventory.trade().self_items, [GEM]);
    f.server(GameEventType::CLOSE_TRADE, &[0]);
    assert_eq!(f.world.item(GEM).unwrap().trade_state, 0);
    assert!(
        f.notices()
            .contains(&Notice::Interface(TRADE_CANCELLED.into()))
    );
    // Opening asks the server; only a player can be asked.
    assert!(!f.run(ItemCommand::OpenTrade(guid(NPC))));
    assert!(f.run(ItemCommand::OpenTrade(guid(OTHER_PLAYER))));
    assert_eq!(
        f.sent(),
        [ac_proto::trade::build_open_trade_negotiations(
            1,
            OTHER_PLAYER
        )]
    );
}

#[test]
fn the_partners_trade_items_reach_the_destruction_queue() {
    // RuntimeTradeState.PartnerItemAdded / PartnerItemsReleased (OpenAC
    // `81391903`): an offer, an item taken back, and a reset releasing the rest.
    use crate::world::liveness::QueueNote;
    const OFFER: u32 = 0x8000_2001;
    const SECOND: u32 = 0x8000_2003;
    let mut f = Fixture::new();
    f.server(GameEventType::ADD_TO_TRADE, &[OFFER, SIDE_PARTNER, 0]);
    f.server(
        GameEventType::REGISTER_TRADE,
        &[OTHER_PLAYER, OTHER_PLAYER, 0, 0],
    );
    f.world.take_queue_notes();
    f.server(GameEventType::ADD_TO_TRADE, &[OFFER, SIDE_PARTNER, 0]);
    f.server(GameEventType::ADD_TO_TRADE, &[SECOND, SIDE_PARTNER, 0]);
    f.server(GameEventType::ADD_TO_TRADE, &[GEM, SIDE_SELF, 0]);
    f.server(GameEventType::REMOVE_FROM_TRADE, &[SECOND, SIDE_PARTNER]);
    f.server(GameEventType::RESET_TRADE, &[OTHER_PLAYER]);
    f.server(GameEventType::CLOSE_TRADE, &[0]);
    let notes: Vec<QueueNote> = f
        .world
        .take_queue_notes()
        .into_iter()
        .filter(|n| {
            matches!(
                n,
                QueueNote::PartnerItemAdded(_) | QueueNote::PartnerItemsReleased(_)
            )
        })
        .collect();
    assert_eq!(
        notes,
        [
            QueueNote::PartnerItemAdded(OFFER),
            QueueNote::PartnerItemAdded(SECOND),
            QueueNote::PartnerItemsReleased(vec![SECOND]),
            QueueNote::PartnerItemsReleased(vec![OFFER]),
        ]
    );
}

#[test]
fn a_vendors_list_reaches_the_destruction_queue() {
    // OpenAC RuntimeEntityLivenessController.OnVendorChanged.
    use crate::world::liveness::QueueNote;
    let mut f = Fixture::new();
    let mut tx = Vec::new();
    f.world.take_queue_notes();
    f.inventory.apply_vendor(&approach(), &mut f.world, &mut tx);
    let listed: Vec<u32> = f
        .inventory
        .vendor()
        .items()
        .iter()
        .map(|i| i.item_guid)
        .collect();
    assert!(!listed.is_empty());
    assert!(
        f.world
            .take_queue_notes()
            .contains(&QueueNote::VendorListed(listed))
    );
}

const DIAMOND: u32 = 0x7000_0100;
const RUBY: u32 = 0x6000_0010;

impl Fixture {
    /// The player holds 1,000 pyreals and a ruby worth 50; the shopkeeper's
    /// shop is open.
    fn at_the_shop(&mut self) {
        self.world.apply(&Inbound::PrivateUpdatePropertyInt(
            ac_proto::PrivateUpdatePropertyInt {
                sequence: 1,
                property: ac_core::properties::PropertyInt::COIN_VALUE.0,
                value: 1000,
            },
        ));
        self.world.apply(&create(RUBY, "Ruby", |d| {
            d.item_type = ItemType::GEM.bits();
            d.value = Some(50);
            d.container_id = Some(PLAYER);
        }));
        self.world.take_table_events();
        let mut tx = Vec::new();
        self.inventory
            .apply_vendor(&approach(), &mut self.world, &mut tx);
        self.events();
    }

    fn vendor_view(&mut self) -> Box<VendorView> {
        match self.events().pop() {
            Some(InventoryEvent::Vendor(Some(view))) => view,
            other => panic!("expected the shop, got {other:?}"),
        }
    }
}

#[test]
fn the_listing_enters_the_item_table_held_by_the_vendor_until_it_closes() {
    // RuntimeVendorLifecycleTests.ApproachVendorEvent_MaterializesShopItemsIntoTheOwnedObjectTable.
    let mut f = Fixture::new();
    f.at_the_shop();
    let listing = f.world.item(DIAMOND).unwrap();
    assert_eq!(
        (listing.container_id, listing.name.as_str()),
        (VENDOR, "Diamond")
    );
    assert_eq!(f.inventory.shop_items().owned_count(), 1);
    assert!(f.run(ItemCommand::Vendor(VendorCommand::Close)));
    assert!(f.world.item(DIAMOND).is_none());
    assert_eq!(f.inventory.shop_items().owned_count(), 0);
    assert!(f.events().contains(&InventoryEvent::Vendor(None)));
    // A closed shop refuses the window's buttons.
    assert!(!f.run(ItemCommand::Vendor(VendorCommand::BuyAll)));
}

#[test]
fn a_teleport_closes_the_vendors_window() {
    // RuntimeVendorRangeQueryTests.EnforceRange_TeleportQueued_ClosesTheSessionBeforeArrival.
    let mut f = Fixture::new();
    f.at_the_shop();
    let teleport = Inbound::PlayerTeleport(ac_proto::PlayerTeleport {
        teleport_sequence: 2,
    });
    let mut inventory = std::mem::take(&mut f.inventory);
    inventory.apply(&teleport, &mut f.ctx());
    f.inventory = inventory;
    assert_eq!(f.inventory.vendor().vendor_id(), 0);
    assert!(f.world.item(DIAMOND).is_none());
}

#[test]
fn the_buy_list_goes_out_as_one_purchase_priced_at_the_selling_rate() {
    // VendorUiController.BuyAllButtonPressed: money, room, one request, the
    // list empties.
    let mut f = Fixture::new();
    f.at_the_shop();
    assert!(f.run(ItemCommand::Vendor(VendorCommand::AddToBuyList {
        item: ObjectRef::Name("diamond".into()),
        amount: 2,
    })));
    let view = f.vendor_view();
    // 2 x 100 at 1.5.
    assert_eq!(view.lists.buying[0].price, 300);
    assert_eq!(view.lists.buying_text, "Buying 2 items worth 300p");
    assert_eq!(view.lists.purse_text, "You have 1,000p");
    assert_eq!(view.price(DIAMOND, 1), Some(150));
    assert!(f.run(ItemCommand::Vendor(VendorCommand::BuyAll)));
    assert_eq!(
        f.sent(),
        [build_buy(
            1,
            VENDOR,
            &[ItemAmount {
                amount: 2,
                item_guid: DIAMOND
            }],
            0
        )]
    );
    assert!(f.inventory.vendor_lists().buy().is_empty());
    // Seven diamonds cost 1,050p: refused before anything is sent.
    let mut tx = Vec::new();
    f.inventory.apply_vendor(&approach(), &mut f.world, &mut tx);
    f.server(GameEventType::USE_DONE, &[0]);
    f.run(ItemCommand::Vendor(VendorCommand::AddToBuyList {
        item: ObjectRef::Guid(DIAMOND),
        amount: 7,
    }));
    assert!(!f.run(ItemCommand::Vendor(VendorCommand::BuyAll)));
    assert!(f.sent().is_empty());
    assert!(
        f.notices()
            .contains(&Notice::Interface(shop::NOT_ENOUGH_MONEY.into()))
    );
}

#[test]
fn the_sell_list_takes_only_what_the_vendor_buys_and_marks_it() {
    // VendorUiController.HandleDropRelease / SellAllButtonPressed and
    // SyncSellStates.
    let mut f = Fixture::new();
    f.at_the_shop();
    // A sword is not a gem.
    assert!(!f.run(ItemCommand::Vendor(VendorCommand::AddToSellList(
        ObjectRef::Guid(SWORD)
    ))));
    assert!(
        f.notices()
            .contains(&Notice::Interface("You cannot sell that here".into()))
    );
    assert!(f.run(ItemCommand::Vendor(VendorCommand::AddToSellList(
        ObjectRef::Name("ruby".into())
    ))));
    assert_eq!(f.world.item(RUBY).unwrap().sell_state, 1);
    let view = f.vendor_view();
    // 50 at 0.5.
    assert_eq!(view.lists.selling_text, "Selling 1 item worth 25p");
    assert!(f.run(ItemCommand::Vendor(VendorCommand::SellAll)));
    assert_eq!(
        f.sent(),
        [ac_proto::vendor::build_sell(
            1,
            VENDOR,
            &[ItemAmount {
                amount: 1,
                item_guid: RUBY
            }]
        )]
    );
    assert_eq!(f.world.item(RUBY).unwrap().sell_state, 0);
}

#[test]
fn fillcomps_stages_what_the_component_book_wants() {
    // VendorUiController.FillComponentBuyList over VendorComponentFill.Plan:
    // the player wants five of the listed component and carries none.
    let mut f = Fixture::new();
    let mut shop = approach();
    shop.items[0].desc.weenie_class_id = 691;
    shop.items[0].desc.item_type = ItemType::SPELL_COMPONENTS.bits();
    shop.profile.merchandise_item_types = ItemType::SPELL_COMPONENTS.bits();
    let mut tx = Vec::new();
    f.inventory.apply_vendor(&shop, &mut f.world, &mut tx);
    f.world
        .update_spellbook(|book| book.set_desired_component(691, 5));
    let mut inventory = std::mem::take(&mut f.inventory);
    inventory.fill_components(
        ac_core::items::component_fill::ANY_CATEGORY,
        0,
        |wcid| {
            (wcid == 691).then(|| ac_core::items::component_fill::ComponentDescription {
                category: ac_core::items::component_fill::SCARAB_CATEGORY,
                name: "Lead Scarab".into(),
            })
        },
        &mut f.ctx(),
    );
    f.inventory = inventory;
    let staged = f.inventory.vendor_lists().buy().entries().to_vec();
    assert_eq!(staged.len(), 1);
    assert_eq!((staged[0].item_guid, staged[0].quantity), (DIAMOND, 5));
    // The window is told it was a component fill, so it shows the spell
    // components and the buying page (FillComponentBuyList's SelectCategory
    // and ShowTab(Buying)).
    let reasons: Vec<VendorViewReason> = f
        .events()
        .into_iter()
        .filter_map(|e| match e {
            InventoryEvent::Vendor(Some(view)) => Some(view.reason),
            _ => None,
        })
        .collect();
    assert_eq!(reasons.last(), Some(&VendorViewReason::ComponentsFilled));
}

#[test]
fn the_trade_view_names_the_partner_and_the_staged_items() {
    let mut f = Fixture::new();
    f.server(
        GameEventType::REGISTER_TRADE,
        &[OTHER_PLAYER, OTHER_PLAYER, 0, 0],
    );
    f.server(GameEventType::ADD_TO_TRADE, &[GEM, SIDE_SELF, 0]);
    match f.events().pop() {
        Some(InventoryEvent::Trade(view)) => {
            assert_eq!(view.partner_name.as_deref(), Some("Bob"));
            assert_eq!(view.item_names.get(&GEM).map(String::as_str), Some("Gems"));
        }
        other => panic!("expected the trade, got {other:?}"),
    }
}

#[test]
fn nothing_is_sent_outside_the_world() {
    let mut f = Fixture::new();
    let mut inventory = std::mem::take(&mut f.inventory);
    let mut ctx = ItemContext {
        active: false,
        ..f.ctx()
    };
    assert!(!inventory.execute(
        &ItemCommand::Drop {
            item: guid(SWORD),
            amount: 0,
        },
        &mut ctx
    ));
    assert!(f.outbox.is_empty());
}

#[test]
fn the_shortcut_bar_follows_the_players_changes_and_sends_them() {
    // OpenAC `RuntimeInventoryState.TryAddShortcut` / `TryRemoveShortcut`:
    // the request goes out and the store holds the change; a slot off the
    // bar or an empty entry is refused.
    use ac_proto::inventory::{build_add_shortcut, build_remove_shortcut};
    let mut f = Fixture::new();
    let entry = ac_core::items::ShortcutEntry {
        index: 3,
        object_id: SWORD,
        spell_id: 0,
    };
    assert!(f.run(ItemCommand::AddShortcut(entry)));
    let wire = ac_proto::Shortcut {
        index: 3,
        object_id: SWORD,
        spell_id: 0,
    };
    assert_eq!(f.sent(), [build_add_shortcut(1, &wire)]);
    assert_eq!(f.inventory.shortcuts().get(3), SWORD);
    assert!(!f.run(ItemCommand::AddShortcut(entry.with_index(18))));
    assert!(
        !f.run(ItemCommand::AddShortcut(ac_core::items::ShortcutEntry {
            index: 4,
            object_id: 0,
            spell_id: 0,
        }))
    );
    assert!(f.sent().is_empty());
    assert!(f.run(ItemCommand::RemoveShortcut(3)));
    assert_eq!(f.sent(), [build_remove_shortcut(2, 3)]);
    assert!(f.inventory.shortcuts().is_empty(3));
    assert!(!f.run(ItemCommand::RemoveShortcut(-1)));

    // The description's bar replaces the one held.
    f.inventory.load_shortcuts(&[wire]);
    assert_eq!(f.inventory.shortcuts().items(), [entry]);
}

// -- The bar lets go of an item the player no longer holds -----------------
//
// OpenAC issue #219
// `RuntimeInventoryStateTests` (the shortcut ownership cases): on the
// server's move or removal of an item on the bar that the player no longer
// holds, the first slot holding it is cleared and RemoveShortCut (0x019D)
// sent; a merge sent moves the poured stack's shortcut to the stack it
// joins. The gem stands for OpenAC's healing kit.

const KIT_SLOT: i32 = 4;
const CHEST: u32 = 0x7000_0030;

fn shortcut(index: i32, object_id: u32, spell_id: u32) -> ac_proto::Shortcut {
    ac_proto::Shortcut {
        index,
        object_id,
        spell_id,
    }
}

impl Fixture {
    /// The fixture with the gem on the bar.
    fn with_gem_on_the_bar() -> Self {
        let mut f = Self::new();
        f.inventory.load_shortcuts(&[shortcut(KIT_SLOT, GEM, 0)]);
        f
    }

    /// The server's message, then the inventory follows the table.
    fn inbound(&mut self, message: &Inbound<'_>) {
        self.world.apply(message);
        let mut inventory = std::mem::take(&mut self.inventory);
        inventory.observe(&mut self.ctx());
        self.inventory = inventory;
    }

    fn slot(&self, index: i32) -> Option<ac_core::items::ShortcutEntry> {
        self.inventory.shortcuts().get_entry(index)
    }
}

fn server_delete(guid: u32) -> Inbound<'static> {
    Inbound::DeleteObject(ac_proto::DeleteObject {
        guid,
        instance_sequence: 1,
    })
}

// ServerDropToTheGroundClearsTheShortcutAndTellsTheServer.
#[test]
fn a_server_drop_to_the_ground_clears_the_shortcut_and_tells_the_server() {
    use ac_proto::inventory::build_remove_shortcut;
    let mut f = Fixture::with_gem_on_the_bar();
    f.server(GameEventType::INVENTORY_PUT_OBJECT_IN_3D, &[GEM]);
    assert_eq!(f.slot(KIT_SLOT), None);
    assert_eq!(f.sent(), [build_remove_shortcut(1, KIT_SLOT as u32)]);
}

// ItemPutInAContainerThePlayerDoesNotOwnLeavesTheToolbar.
#[test]
fn an_item_put_in_a_container_the_player_does_not_own_leaves_the_bar() {
    use ac_proto::inventory::build_remove_shortcut;
    let mut f = Fixture::with_gem_on_the_bar();
    f.server(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[GEM, CHEST, 0, 0],
    );
    assert_eq!(f.slot(KIT_SLOT), None);
    assert_eq!(f.sent(), [build_remove_shortcut(1, KIT_SLOT as u32)]);
}

// ItemTheServerDestroysOrTakesAwayLeavesTheToolbar and
// AnExactIncarnationDeleteClearsTheShortcut: the server's delete of this
// incarnation.
#[test]
fn an_item_the_server_destroys_leaves_the_bar() {
    use ac_proto::inventory::build_remove_shortcut;
    let mut f = Fixture::with_gem_on_the_bar();
    f.inbound(&server_delete(GEM));
    assert!(f.world.item(GEM).is_none());
    assert_eq!(f.slot(KIT_SLOT), None);
    assert_eq!(f.sent(), [build_remove_shortcut(1, KIT_SLOT as u32)]);
}

// ItemStillHeldByThePlayerKeepsItsShortcut.
#[test]
fn an_item_the_player_still_holds_keeps_its_shortcut() {
    let mut f = Fixture::with_gem_on_the_bar();
    f.server(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[GEM, PACK, 0, 0],
    );
    f.server(
        GameEventType::WIELD_OBJECT,
        &[GEM, EquipMask::MELEE_WEAPON.bits()],
    );
    f.server(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[GEM, PLAYER, 0, 0],
    );
    assert_eq!(
        f.slot(KIT_SLOT).map(|e| e.object_id),
        Some(GEM),
        "kept through its own packs and a wield"
    );
    assert!(f.sent().is_empty());
}

// ALocalProjectionAndAFreshIncarnationKeepTheShortcut, the projection half:
// a move made ahead of the server's answer (here onto the ground, where the
// player would no longer own the gem) is not the server's word. The
// runtime never projects a move itself, so the table is moved directly.
#[test]
fn a_local_projection_of_a_move_keeps_the_shortcut() {
    let mut f = Fixture::with_gem_on_the_bar();
    let moved = f
        .world
        .mutate_items_for_test(|table, events| table.move_item_optimistic(GEM, 0, -1, events));
    assert!(moved);
    let mut inventory = std::mem::take(&mut f.inventory);
    inventory.observe(&mut f.ctx());
    f.inventory = inventory;
    assert!(!f.world.items().is_owned_by_object(GEM, PLAYER));
    assert_eq!(f.slot(KIT_SLOT).map(|e| e.object_id), Some(GEM));
    assert!(f.sent().is_empty());
}

// ALocalProjectionAndAFreshIncarnationKeepTheShortcut, the incarnation half:
// a newer incarnation under the same id replacing the old one is a
// re-description, not the item leaving, even when it is described somewhere
// the player does not own.
#[test]
fn a_fresh_incarnation_keeps_the_shortcut() {
    let mut f = Fixture::with_gem_on_the_bar();
    f.inbound(&Inbound::CreateObject(Box::new(CreateObject {
        guid: GEM,
        model: ModelData::default(),
        physics: physics(
            PhysicsTimestamps {
                instance: 2,
                ..PhysicsTimestamps::default()
            },
            None,
        ),
        weenie: Some(PublicWeenieDesc {
            name: "Gem".into(),
            container_id: Some(CHEST),
            ..PublicWeenieDesc::default()
        }),
    })));
    assert!(!f.world.items().is_owned_by_object(GEM, PLAYER));
    assert_eq!(f.slot(KIT_SLOT).map(|e| e.object_id), Some(GEM));
    assert!(f.sent().is_empty());
}

// NoShortcutIsDroppedWhileNoPlayerIsKnown.
#[test]
fn no_shortcut_is_dropped_while_no_player_is_known() {
    let mut f = Fixture::with_gem_on_the_bar();
    f.world = World::new();
    f.world.apply(&create(GEM, "Gem", |_| {}));
    f.server(GameEventType::INVENTORY_PUT_OBJECT_IN_3D, &[GEM]);
    assert_eq!(f.slot(KIT_SLOT).map(|e| e.object_id), Some(GEM));
    assert!(f.sent().is_empty());
}

// AServerMoveOfAnItemTheClientNeverHeldClearsItsShortcut.
#[test]
fn a_server_move_of_an_item_the_client_never_held_clears_its_shortcut() {
    use ac_proto::inventory::build_remove_shortcut;
    const UNKNOWN: u32 = 0x6000_0099;
    const UNKNOWN_SLOT: i32 = 9;
    let mut f = Fixture::new();
    f.inventory.load_shortcuts(&[
        shortcut(KIT_SLOT, GEM, 0),
        shortcut(UNKNOWN_SLOT, UNKNOWN, 0),
    ]);
    f.server(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[UNKNOWN, PLAYER, 0, 0],
    );
    assert_eq!(f.slot(UNKNOWN_SLOT), None);
    assert_eq!(f.sent(), [build_remove_shortcut(1, UNKNOWN_SLOT as u32)]);
    assert!(f.slot(KIT_SLOT).is_some());
}

// AnItemOnTwoSlotsLosesOnlyTheFirstPerServerMove.
#[test]
fn an_item_on_two_slots_loses_only_the_first_per_server_move() {
    use ac_proto::inventory::build_remove_shortcut;
    const LATER_SLOT: i32 = 11;
    let mut f = Fixture::new();
    f.inventory
        .load_shortcuts(&[shortcut(KIT_SLOT, GEM, 0), shortcut(LATER_SLOT, GEM, 0)]);
    f.server(GameEventType::INVENTORY_PUT_OBJECT_IN_3D, &[GEM]);
    assert_eq!(f.slot(KIT_SLOT), None);
    assert_eq!(f.slot(LATER_SLOT).map(|e| e.object_id), Some(GEM));
    assert_eq!(f.sent(), [build_remove_shortcut(1, KIT_SLOT as u32)]);
}

// SpellShortcutsAreNeverTouchedByItemMovesOrRemovals.
#[test]
fn spell_shortcuts_are_never_touched_by_item_moves_or_removals() {
    use ac_proto::inventory::build_remove_shortcut;
    const SPELL_SLOT: i32 = 6;
    let mut f = Fixture::new();
    f.inventory
        .load_shortcuts(&[shortcut(KIT_SLOT, GEM, 0), shortcut(SPELL_SLOT, 0, 42)]);
    f.server(GameEventType::INVENTORY_PUT_OBJECT_IN_3D, &[GEM]);
    f.inbound(&server_delete(GEM));
    f.server(GameEventType::INVENTORY_PUT_OBJECT_IN_3D, &[0]);
    assert_eq!(
        f.slot(SPELL_SLOT).map(|e| (e.object_id, e.spell_id)),
        Some((0, 42))
    );
    assert_eq!(f.sent(), [build_remove_shortcut(1, KIT_SLOT as u32)]);
}

// AMergeMovesThePouredStacksShortcutAndItsRemovalThenSendsNothing (and the
// toolbar's former ReplaceFullyMergedShortcut_rekeysEntryAndPreservesRawFields):
// the merge request goes out, then the slot's swap.
#[test]
fn a_merge_moves_the_poured_stacks_shortcut_and_its_removal_then_sends_nothing() {
    use ac_proto::inventory::{build_add_shortcut, build_remove_shortcut};
    let mut f = Fixture::new();
    f.inventory
        .load_shortcuts(&[shortcut(KIT_SLOT, GEM, 0x1122_3344)]);
    assert!(f.run(ItemCommand::Merge {
        from: guid(GEM),
        to: guid(GEM2),
        amount: 5,
    }));
    assert_eq!(
        f.sent(),
        [
            build_stackable_merge(1, GEM, GEM2, 5),
            build_remove_shortcut(2, KIT_SLOT as u32),
            build_add_shortcut(3, &shortcut(KIT_SLOT, GEM2, 0x1122_3344)),
        ]
    );
    assert_eq!(
        f.slot(KIT_SLOT).map(|e| (e.object_id, e.spell_id)),
        Some((GEM2, 0x1122_3344))
    );
    f.inbound(&server_delete(GEM));
    assert_eq!(f.slot(KIT_SLOT).map(|e| e.object_id), Some(GEM2));
    assert!(f.sent().is_empty());
}

// The toolbar's former `a_fully_merged_stack_moves_its_shortcut_to_the_merged_stack`
// (OpenAC `ShortcutDropPlanner.PlanFullStackMerge`): when the stack poured
// into is on the bar too, its own slot is emptied and the poured stack's
// slot names it, so it stays on the bar once.
#[test]
fn a_merge_into_a_stack_already_on_the_bar_leaves_it_on_the_bar_once() {
    use ac_proto::inventory::{build_add_shortcut, build_remove_shortcut};
    let mut f = Fixture::new();
    f.inventory
        .load_shortcuts(&[shortcut(4, GEM, 7), shortcut(6, GEM2, 0)]);
    assert!(f.run(ItemCommand::Merge {
        from: guid(GEM),
        to: guid(GEM2),
        amount: 5,
    }));
    assert_eq!(
        f.sent(),
        [
            build_stackable_merge(1, GEM, GEM2, 5),
            build_remove_shortcut(2, 4),
            build_remove_shortcut(3, 6),
            build_add_shortcut(4, &shortcut(4, GEM2, 7)),
        ]
    );
    assert_eq!(
        f.inventory.shortcuts().items(),
        [ac_core::items::ShortcutEntry {
            index: 4,
            object_id: GEM2,
            spell_id: 7,
        }]
    );
}

// The pickup's join into a carried stack is a merge sent like any other
// (OpenAC `RuntimeItemInteraction` raises `MergeAttempted` from its
// backpack placement too): the picked-up stack's shortcut moves to the
// stack it joins.
#[test]
fn a_pickup_that_joins_a_carried_stack_moves_the_shortcut_too() {
    use ac_proto::inventory::{build_add_shortcut, build_remove_shortcut};
    let mut f = Fixture::new();
    f.inventory
        .load_shortcuts(&[shortcut(4, LOOSE, 0), shortcut(6, GEM, 0)]);
    assert!(f.run(ItemCommand::PickUp(guid(LOOSE))));
    assert_eq!(
        f.sent(),
        [
            build_stackable_merge(1, LOOSE, GEM, 2),
            build_remove_shortcut(2, 4),
            build_remove_shortcut(3, 6),
            build_add_shortcut(4, &shortcut(4, GEM, 0)),
        ]
    );
    assert_eq!(f.slot(4).map(|e| e.object_id), Some(GEM));
    assert_eq!(f.slot(6), None);
}

// OpenAC `InventoryController.OnMergeAttempted`: every merge sent, the
// pickup's join and a drag's merge alike, names the stack it joins for the
// selection, once.
#[test]
fn every_merge_sent_names_the_stack_it_joins_for_the_selection() {
    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::PickUp(guid(LOOSE))));
    assert_eq!(f.sent(), [build_stackable_merge(1, LOOSE, GEM, 2)]);
    assert_eq!(f.inventory.take_merged_into(), [GEM]);
    assert!(f.inventory.take_merged_into().is_empty());

    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::Merge {
        from: guid(GEM),
        to: guid(GEM2),
        amount: 5,
    }));
    assert_eq!(f.inventory.take_merged_into(), [GEM2]);
}

#[test]
fn the_use_and_examine_modes_act_on_the_next_object_offered() {
    // OpenAC `RuntimeItemInteraction.OfferPrimaryClick`: the use mode uses
    // the object and ends, the examine mode appraises it and ends; outside
    // a mode an offered click is not taken.
    let mut f = Fixture::new();
    assert!(!f.run(ItemCommand::OfferTarget(guid(GEM))));
    f.events();
    assert!(f.run(ItemCommand::EnterExamineMode));
    assert_eq!(f.inventory.interaction_mode(), InteractionMode::Examine);
    assert!(f.run(ItemCommand::OfferTarget(guid(NPC))));
    assert_eq!(f.sent(), [build_appraise(1, NPC)]);
    assert_eq!(f.inventory.interaction_mode(), InteractionMode::None);
    assert!(f.run(ItemCommand::EnterUseMode));
    assert!(f.run(ItemCommand::CancelTargetMode));
    assert_eq!(f.inventory.interaction_mode(), InteractionMode::None);
    assert!(f.run(ItemCommand::EnterUseMode));
    assert!(f.run(ItemCommand::OfferTarget(guid(NPC))));
    assert_eq!(f.sent(), [build_use(2, NPC)]);
    assert_eq!(f.inventory.interaction_mode(), InteractionMode::None);
}

#[test]
fn an_inscription_goes_out_only_where_the_panel_lets_the_player_write() {
    // OpenAC AppraisalUiController.SetInscription (the editable gate),
    // HandleInscriptionLosingFocus (what is sent) and
    // ReportInscriptionUnavailable (the refusals); the body is
    // InventoryActions.BuildSetInscription (InventoryActionsTests).
    use ac_proto::inventory::build_set_inscription;
    let mut f = Fixture::new();
    // The sword is not inscribable.
    assert!(!f.run(ItemCommand::Inscribe {
        item: guid(SWORD),
        text: "Mine".into(),
    }));
    assert_eq!(
        f.notices(),
        [Notice::Interface("This item is not inscribable.".into())]
    );
    f.world.apply(&create(SWORD, "Sword", |d| {
        d.item_type = ItemType::MELEE_WEAPON.bits();
        d.valid_locations = Some(EquipMask::MELEE_WEAPON.bits());
        d.combat_use = Some(1);
        d.object_description_flags = PublicWeenieFlags::INSCRIBABLE.bits();
    }));
    f.world.apply(&create(LOOSE, "Gem", |d| {
        d.object_description_flags = PublicWeenieFlags::INSCRIBABLE.bits();
    }));
    f.world.take_table_events();
    // Something in the world is not the player's to inscribe.
    assert!(!f.run(ItemCommand::Inscribe {
        item: guid(LOOSE),
        text: "Mine".into(),
    }));
    assert_eq!(
        f.notices(),
        [Notice::Interface(
            "Item must be in your inventory to inscribe.".into()
        )]
    );
    // Clearing an inscription nobody wrote sends nothing.
    assert!(!f.run(ItemCommand::Inscribe {
        item: guid(SWORD),
        text: String::new(),
    }));
    f.events();
    assert!(f.sent().is_empty());
    assert!(f.run(ItemCommand::Inscribe {
        item: guid(SWORD),
        text: "Café".into(),
    }));
    assert_eq!(f.sent(), [build_set_inscription(1, SWORD, "Café").unwrap()]);
    // Its appraisal says another wrote it: only they may change it.
    let mut response = IdentifyObjectResponse {
        guid: SWORD,
        success: true,
        ..IdentifyObjectResponse::default()
    };
    response.properties.strings.insert(7, "Old words".into());
    response.properties.strings.insert(8, "Bob".into());
    f.world.apply_appraisal(&response, 1.0);
    assert!(!f.run(ItemCommand::Inscribe {
        item: guid(SWORD),
        text: "New words".into(),
    }));
    assert_eq!(
        f.notices(),
        [Notice::Interface(
            "Only Bob can change the inscription".into()
        )]
    );
    // Written by the player: the same words send nothing, an empty text
    // clears it.
    response.properties.strings.insert(8, "horan".into());
    f.world.apply_appraisal(&response, 2.0);
    assert!(!f.run(ItemCommand::Inscribe {
        item: guid(SWORD),
        text: "Old words".into(),
    }));
    assert!(f.run(ItemCommand::Inscribe {
        item: guid(SWORD),
        text: String::new(),
    }));
    assert_eq!(f.sent(), [build_set_inscription(2, SWORD, "").unwrap()]);
}

#[test]
fn a_corpse_is_opened_looted_into_the_packs_and_closed() {
    // OpenAC LootAutomationCloseTests / LootPickupOutcomeTests over the
    // runtime owners: a use opens the corpse (the client waits on it), its
    // contents name items to take, a take of one goes out as a pickup into
    // the main pack (TryPlaceWorldItemInBackpack), and a use of the open
    // corpse closes it.
    const CORPSE: u32 = 0x8000_D001;
    const RUBY: u32 = 0x8000_D002;
    let mut f = Fixture::new();
    f.world.apply(&create(CORPSE, "Corpse of Drudge", |d| {
        d.item_type = ItemType::CONTAINER.bits();
        d.object_description_flags =
            (PublicWeenieFlags::CORPSE | PublicWeenieFlags::OPENABLE).bits();
        d.useability = Some(ItemUseable::REMOTE.bits());
        d.items_capacity = Some(120);
    }));
    f.world.apply(&create(RUBY, "Ruby", |d| {
        d.item_type = ItemType::GEM.bits();
    }));
    f.world.take_table_events();
    assert!(f.run(ItemCommand::Use(guid(CORPSE))));
    assert_eq!(f.sent(), [build_use(1, CORPSE)]);
    assert_eq!(
        f.inventory.external_containers().requested_container_id(),
        CORPSE
    );
    f.server(GameEventType::VIEW_CONTENTS, &[CORPSE, 1, RUBY, 0]);
    f.server(GameEventType::USE_DONE, &[0]);
    assert_eq!(
        f.inventory.external_containers().current_container_id(),
        CORPSE
    );
    let ground = ground_view(&f.world, CORPSE).unwrap();
    assert_eq!(ground.items.len(), 1);
    assert_eq!(ground.items[0].name, "Ruby");
    // A move is only for what the player owns (OpenAC
    // TryMoveItemForAutomation: IsOwnedByPlayer on the item): a corpse's
    // item is taken with a pickup, never moved.
    assert!(!f.run(ItemCommand::MoveToContainer {
        item: guid(RUBY),
        container: ObjectRef::Player,
        slot: 0,
        amount: 0,
        join_stack: false,
    }));
    assert!(f.sent().is_empty());
    f.events();
    // The corpse's contents are named like the player's own items.
    assert!(f.run(ItemCommand::PickUp(ObjectRef::Name("ruby".into()))));
    assert_eq!(f.sent(), [build_put_item_in_container(2, RUBY, PLAYER, 0)]);
    f.server(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[RUBY, PLAYER, 0, 0],
    );
    assert_eq!(f.world.item(RUBY).unwrap().container_id, PLAYER);
    // A use of the open corpse closes it.
    f.now_ms += 1_000;
    assert!(f.run(ItemCommand::Use(guid(CORPSE))));
    assert_eq!(f.sent(), [build_use(3, CORPSE)]);
}

#[test]
fn a_locked_chest_is_used_but_not_viewed_and_the_client_says_it_is_locked() {
    // Golden text and order: the retail client sends the use, then refuses
    // to view a container not described as openable and shows "The %s is
    // locked" as client-local text, and then "Using the %s". The server
    // answers a locked chest's use with a sound and an empty use-done, no
    // text. The corpse already open stays the open container.
    const CORPSE: u32 = 0x8000_D011;
    const CHEST: u32 = 0x8000_D012;
    let mut f = Fixture::new();
    open_empty_corpse(&mut f, CORPSE);
    f.notices();
    f.world.apply(&create(CHEST, "Legendary Chest", |d| {
        d.item_type = ItemType::CONTAINER.bits();
        d.object_description_flags = PublicWeenieFlags::STUCK.bits();
        d.useability = Some(ItemUseable::REMOTE.bits());
        d.items_capacity = Some(120);
    }));
    f.world.take_table_events();
    f.now_ms += 1_000;
    assert!(f.run(ItemCommand::Use(guid(CHEST))));
    assert_eq!(f.sent(), [build_use(2, CHEST)]);
    assert_eq!(
        f.notices(),
        [
            Notice::Interface("The Legendary Chest is locked".into()),
            Notice::Interface("Using the Legendary Chest".into())
        ]
    );
    let external = f.inventory.external_containers();
    assert_eq!(external.requested_container_id(), CORPSE);
    assert_eq!(external.current_container_id(), CORPSE);
}

/// Leave the open corpse `corpse` (with nothing in it) as the open
/// container.
fn open_empty_corpse(f: &mut Fixture, corpse: u32) {
    open_container(
        f,
        corpse,
        PublicWeenieFlags::CORPSE | PublicWeenieFlags::OPENABLE,
        &[],
    );
}

/// Leave `container` (described with `flags`, holding a ruby for each of
/// `contents`) as the open container.
fn open_container(f: &mut Fixture, container: u32, flags: PublicWeenieFlags, contents: &[u32]) {
    f.world.apply(&create(container, "Chest", |d| {
        d.item_type = ItemType::CONTAINER.bits();
        d.object_description_flags = flags.bits();
        d.useability = Some(ItemUseable::REMOTE.bits());
        d.items_capacity = Some(120);
    }));
    for &item in contents {
        f.world.apply(&create(item, "Ruby", |d| {
            d.item_type = ItemType::GEM.bits();
        }));
    }
    f.world.take_table_events();
    assert!(f.run(ItemCommand::Use(guid(container))));
    f.sent();
    let mut view = vec![container, u32::try_from(contents.len()).unwrap()];
    for &item in contents {
        view.extend([item, 0]);
    }
    f.server(GameEventType::VIEW_CONTENTS, &view);
    f.server(GameEventType::USE_DONE, &[0]);
    assert_eq!(
        f.inventory.external_containers().current_container_id(),
        container
    );
}

fn delete(f: &mut Fixture, object: u32) {
    f.world
        .apply(&Inbound::DeleteObject(ac_proto::DeleteObject {
            guid: object,
            instance_sequence: 1,
        }));
    let mut inventory = std::mem::take(&mut f.inventory);
    inventory.observe(&mut f.ctx());
    f.inventory = inventory;
}

#[test]
fn the_open_corpse_leaving_the_world_closes_it_and_sends_nothing() {
    // OpenAC `ExternalContainerController.OnObjectRemoved`: the open
    // container's object left the table, so the viewed container closes
    // (`ExternalContainerState.ApplyClose`, the Closed transition plugins
    // and the window follow); nothing goes to the server. The corpse is
    // also forgotten as opened (`RuntimeInventoryState.OnObjectRemoved`).
    const CORPSE: u32 = 0x8000_D101;
    let mut f = Fixture::new();
    open_empty_corpse(&mut f, CORPSE);
    assert!(
        f.inventory
            .external_containers()
            .has_corpse_been_opened(CORPSE)
    );
    delete(&mut f, CORPSE);
    let external = f.inventory.external_containers();
    assert_eq!(external.current_container_id(), 0);
    assert_eq!(external.requested_container_id(), 0);
    assert!(!external.has_corpse_been_opened(CORPSE));
    assert!(f.sent().is_empty());
}

#[test]
fn the_external_container_probe_is_silent_off_and_names_each_step_on() {
    // OPENAC_PROBE_EXTERNAL_CONTAINER (docs/launch-options.md).
    const CORPSE: u32 = 0x8000_D121;
    let mut f = Fixture::new();
    open_empty_corpse(&mut f, CORPSE);
    assert!(f.inventory.take_probe_lines().is_empty());

    let mut f = Fixture::new();
    f.inventory.enable_external_container_probe();
    open_empty_corpse(&mut f, CORPSE);
    f.server(GameEventType::CLOSE_GROUND_CONTAINER, &[NPC]);
    delete(&mut f, CORPSE);
    let lines = f.inventory.take_probe_lines();
    let tails: Vec<&str> = lines
        .iter()
        .map(|l| {
            assert!(l.starts_with("[external-container] t="), "{l}");
            l.split_once(' ').unwrap().1.split_once(' ').unwrap().1
        })
        .collect();
    assert_eq!(
        tails,
        [
            "open requested 0x8000D121 (requested 0x8000D121, open 0x00000000)",
            "opened 0x8000D121 (requested 0x8000D121, open 0x8000D121)",
            "server closed 0x70000002, not the open one (requested 0x8000D121, open 0x8000D121)",
            "open 0x8000D121 left the world, closed (requested 0x00000000, open 0x00000000)",
        ]
    );
    assert!(f.inventory.take_probe_lines().is_empty());
}

#[test]
fn the_open_corpse_leaving_the_world_drops_its_contents_list() {
    // OpenAC `ExternalContainerLifecycleController.OnChanged`: every
    // Closed transition stops viewing the previous container's contents
    // tree (`ClientObjectTable.StopViewingContentsTree`), the removal's
    // close included; the items themselves stay for the destruction queue.
    const CORPSE: u32 = 0x8000_D131;
    const RUBY: u32 = 0x8000_D132;
    let mut f = Fixture::new();
    open_container(
        &mut f,
        CORPSE,
        PublicWeenieFlags::CORPSE | PublicWeenieFlags::OPENABLE,
        &[RUBY],
    );
    assert_eq!(f.world.items().get_contents(CORPSE), [RUBY]);
    delete(&mut f, CORPSE);
    assert!(f.world.items().get_contents(CORPSE).is_empty());
    assert!(f.world.item(RUBY).is_some());
    assert!(f.sent().is_empty());
}

#[test]
fn an_open_chest_leaving_the_world_closes_as_a_corpse_does() {
    // OpenAC `ExternalContainerController.OnObjectRemoved` compares the
    // open container's id only: a chest (openable, not a corpse) closes
    // the same way.
    const CHEST: u32 = 0x8000_D141;
    const RUBY: u32 = 0x8000_D142;
    let mut f = Fixture::new();
    open_container(&mut f, CHEST, PublicWeenieFlags::OPENABLE, &[RUBY]);
    assert!(
        !f.inventory
            .external_containers()
            .has_corpse_been_opened(CHEST)
    );
    delete(&mut f, CHEST);
    let external = f.inventory.external_containers();
    assert_eq!(external.current_container_id(), 0);
    assert_eq!(external.requested_container_id(), 0);
    assert!(f.world.items().get_contents(CHEST).is_empty());
    assert!(f.sent().is_empty());
}

#[test]
fn another_object_leaving_the_world_keeps_the_open_corpse() {
    // OpenAC `ExternalContainerController.OnObjectRemoved`: only the open
    // container's own object closes it.
    const CORPSE: u32 = 0x8000_D111;
    let mut f = Fixture::new();
    open_empty_corpse(&mut f, CORPSE);
    delete(&mut f, NPC);
    assert_eq!(
        f.inventory.external_containers().current_container_id(),
        CORPSE
    );
    assert!(f.sent().is_empty());
}

// OpenAC `tests/AcDream.Runtime.Tests/Gameplay/FullPackStackPickupTests.cs`:
// a character carrying a part-filled stack of coins with no slot to spare,
// over an open corpse holding more of the same coins and a trinket.

const FULL_CORPSE: u32 = 0x8000_E001;
const LOOSE_COINS: u32 = 0x8000_E002;
const CARRIED_COINS: u32 = 0x8000_E003;
const TRINKET: u32 = 0x8000_E004;
const COIN_STACK_MAX: u16 = 100;

fn full_pack(carried: u16, loose: u16, item_slots: i8) -> Fixture {
    let mut world = World::new();
    world.set_player(PLAYER);
    world.apply(&create(PLAYER, "Player", |d| {
        d.object_description_flags = PublicWeenieFlags::PLAYER.bits();
        d.item_type = ItemType::CREATURE.bits();
        d.items_capacity = Some(item_slots);
        d.containers_capacity = Some(0);
    }));
    let coins = |stack: u16| {
        move |d: &mut PublicWeenieDesc| {
            d.item_type = ItemType::MONEY.bits();
            d.weenie_class_id = 0x111;
            d.stack_size = Some(stack);
            d.max_stack_size = Some(COIN_STACK_MAX);
        }
    };
    world.apply(&create(CARRIED_COINS, "Pyreal", coins(carried)));
    world.apply(&create(FULL_CORPSE, "Corpse", |d| {
        d.item_type = ItemType::CONTAINER.bits();
        d.object_description_flags =
            (PublicWeenieFlags::CORPSE | PublicWeenieFlags::OPENABLE).bits();
        d.useability = Some(ItemUseable::REMOTE.bits());
        d.items_capacity = Some(8);
    }));
    world.apply(&create(LOOSE_COINS, "Pyreal", coins(loose)));
    world.apply(&create(TRINKET, "Trinket", |d| {
        d.item_type = ItemType::MISC.bits();
        d.weenie_class_id = 0x222;
        d.stack_size = Some(1);
        d.max_stack_size = Some(1);
    }));
    let mut description = empty_description();
    description.inventory = vec![ac_proto::InventoryEntry {
        guid: CARRIED_COINS,
        container_type: 0,
    }];
    world.apply_player_description(&description);
    world.take_table_events();
    let mut f = Fixture {
        world,
        outbox: Outbox::new(),
        options: CharacterOptions::new(),
        inventory: Inventory::new(),
        now_ms: 10_000,
        in_peace: true,
    };
    assert!(f.run(ItemCommand::Use(guid(FULL_CORPSE))));
    f.server(
        GameEventType::VIEW_CONTENTS,
        &[FULL_CORPSE, 2, LOOSE_COINS, 0, TRINKET, 0],
    );
    f.server(GameEventType::USE_DONE, &[0]);
    f.sent();
    f.notices();
    f.now_ms += 1_000;
    f
}

#[test]
fn a_full_inventory_still_takes_what_joins_a_stack_it_already_holds() {
    let mut f = full_pack(10, 5, 1);
    assert!(f.run(ItemCommand::PickUp(guid(LOOSE_COINS))));
    assert_eq!(
        f.sent(),
        [build_stackable_merge(2, LOOSE_COINS, CARRIED_COINS, 5)]
    );
    assert!(f.notices().is_empty());
}

#[test]
fn a_full_inventory_over_a_full_stack_is_still_refused() {
    let mut f = full_pack(COIN_STACK_MAX, 5, 1);
    f.run(ItemCommand::PickUp(guid(LOOSE_COINS)));
    assert!(f.sent().is_empty());
    assert!(!f.notices().is_empty());
}

#[test]
fn a_full_inventory_over_a_stack_that_cannot_take_the_whole_amount_is_refused() {
    let mut f = full_pack(COIN_STACK_MAX - 2, 5, 1);
    f.run(ItemCommand::PickUp(guid(LOOSE_COINS)));
    assert!(f.sent().is_empty());
}

#[test]
fn a_full_inventory_still_refuses_something_that_does_not_stack() {
    let mut f = full_pack(10, 5, 1);
    f.run(ItemCommand::PickUp(guid(TRINKET)));
    assert!(f.sent().is_empty());
    assert!(!f.notices().is_empty());
}

#[test]
fn an_inventory_with_room_still_prefers_the_stack_over_a_free_slot() {
    let mut f = full_pack(10, 5, 8);
    assert!(f.run(ItemCommand::PickUp(guid(LOOSE_COINS))));
    assert_eq!(
        f.sent(),
        [build_stackable_merge(2, LOOSE_COINS, CARRIED_COINS, 5)]
    );
}

#[test]
fn opening_another_chest_retires_the_open_one() {
    // OpenAC `ExternalContainerLifecycleControllerTests`
    // (ReplacementRequest_RetiresPreviousRootBeforeViewContents,
    // AuthoredCloseAndReset_DoNotSendNoLongerViewing): asking for another
    // container while one is open tells the server the player stopped
    // viewing it and drops its contents list (the objects stay); the
    // second contents list of the new one retires nothing more; a close
    // by the server sends nothing.
    const CHEST_A: u32 = 0x7000_00D1;
    const CHEST_B: u32 = 0x7000_00D2;
    const RUBY: u32 = 0x8000_D102;
    let mut f = Fixture::new();
    for (chest, name) in [(CHEST_A, "Chest"), (CHEST_B, "Coffer")] {
        f.world.apply(&create(chest, name, |d| {
            d.item_type = ItemType::CONTAINER.bits();
            d.object_description_flags = PublicWeenieFlags::OPENABLE.bits();
            d.useability = Some(ItemUseable::REMOTE.bits());
            d.items_capacity = Some(24);
        }));
    }
    f.world.apply(&create(RUBY, "Ruby", |d| {
        d.item_type = ItemType::GEM.bits();
    }));
    f.world.take_table_events();
    assert!(f.run(ItemCommand::Use(guid(CHEST_A))));
    f.server(GameEventType::VIEW_CONTENTS, &[CHEST_A, 1, RUBY, 0]);
    f.server(GameEventType::USE_DONE, &[0]);
    assert_eq!(f.world.items().get_contents(CHEST_A), [RUBY]);
    assert_eq!(f.sent(), [build_use(1, CHEST_A)]);

    f.now_ms += 1_000;
    assert!(f.run(ItemCommand::Use(guid(CHEST_B))));
    assert_eq!(
        f.sent(),
        // The container is armed once the use has gone out
        // (`RuntimeWorldObjectUse`), so the use leads.
        [
            build_use(2, CHEST_B),
            build_no_longer_viewing_contents(3, CHEST_A)
        ]
    );
    assert!(f.world.items().contents_list(CHEST_A).is_none());
    assert!(f.world.item(RUBY).is_some());
    f.server(GameEventType::VIEW_CONTENTS, &[CHEST_B, 0]);
    f.server(GameEventType::VIEW_CONTENTS, &[CHEST_B, 0]);
    f.server(GameEventType::USE_DONE, &[0]);
    assert!(f.sent().is_empty());

    f.server(GameEventType::CLOSE_GROUND_CONTAINER, &[CHEST_B]);
    assert!(f.world.items().contents_list(CHEST_B).is_none());
    assert_eq!(f.inventory.external_containers().current_container_id(), 0);
    assert!(f.sent().is_empty());
}

fn place(item: u32, target: Option<u32>, amount: u32) -> ItemCommand {
    ItemCommand::Place {
        item: guid(item),
        target: target.map(guid),
        amount,
    }
}

#[test]
fn an_item_dragged_onto_a_creature_is_given_and_onto_scenery_dropped() {
    // OpenAC ItemInteractionControllerTests.InventoryDragOnNpc_sendsGiveWithoutOptimisticInventoryMutation,
    // SelectedPartialStackDragOnNpc_sendsSelectedGiveAmount and
    // InventoryDragOnNonCreature_usesRetailGroundFallback
    // (RuntimeItemInteraction.PlaceIn3D over ItemInteractionPolicy.DecidePlacement).
    let mut f = Fixture::new();
    assert!(f.run(place(SWORD, Some(NPC), 0)));
    assert_eq!(f.sent(), [build_give_object(1, NPC, SWORD, 1)]);
    // The item stays where it is until the server moves it.
    assert_eq!(f.world.item(SWORD).unwrap().container_id, PLAYER);
    f.server(GameEventType::INVENTORY_SERVER_SAVE_FAILED, &[SWORD, 0]);
    f.notices();
    // Part of a stack: the chosen amount.
    assert!(f.run(place(GEM, Some(NPC), 3)));
    assert_eq!(f.sent(), [build_give_object(2, NPC, GEM, 3)]);
    f.server(GameEventType::INVENTORY_SERVER_SAVE_FAILED, &[GEM, 0]);
    f.notices();
    // Something that is no creature and no container: the ground.
    const STATUE: u32 = 0x7000_0099;
    f.world.apply(&create(STATUE, "Statue", |d| {
        d.item_type = ItemType::MISC.bits();
    }));
    assert!(f.run(place(SWORD, Some(STATUE), 0)));
    assert_eq!(f.sent(), [build_drop_item(3, SWORD)]);
    f.server(GameEventType::INVENTORY_SERVER_SAVE_FAILED, &[SWORD, 0]);
    f.notices();
    // Part of a stack on the ground splits it off.
    assert!(f.run(place(GEM, None, 2)));
    assert_eq!(f.sent(), [build_stackable_split_to_3d(4, GEM, 2)]);
}

#[test]
fn an_item_dragged_onto_a_player_follows_the_secure_trade_option() {
    // InventoryDragOnPlayer_honorsRetailSecureTradeOption: with the option a
    // secure trade is asked for (nothing given); without it a gift.
    use ac_proto::trade::build_open_trade_negotiations;
    let mut f = Fixture::new();
    f.options.set_bit(
        crate::character::option_id::DRAG_ITEM_ON_PLAYER_OPENS_SECURE_TRADE,
        true,
    );
    assert!(!f.run(place(SWORD, Some(OTHER_PLAYER), 0)));
    assert_eq!(f.sent(), [build_open_trade_negotiations(1, OTHER_PLAYER)]);
    f.options.set_bit(
        crate::character::option_id::DRAG_ITEM_ON_PLAYER_OPENS_SECURE_TRADE,
        false,
    );
    assert!(f.run(place(SWORD, Some(OTHER_PLAYER), 0)));
    assert_eq!(f.sent(), [build_give_object(2, OTHER_PLAYER, SWORD, 1)]);
}

#[test]
fn an_item_dragged_onto_an_open_chest_goes_in_and_a_loose_one_must_be_picked_up() {
    // DropOwnedItemOnOpenExternalContainer_SendsPutWithoutOptimisticMove,
    // and the policy's "You must first pick up" and locked/unopened
    // container refusals (ItemInteractionPolicyTests).
    const CHEST: u32 = 0x7000_00C1;
    let mut f = Fixture::new();
    f.world.apply(&create(CHEST, "Chest", |d| {
        d.item_type = ItemType::CONTAINER.bits();
        d.object_description_flags = PublicWeenieFlags::OPENABLE.bits();
        d.useability = Some(ItemUseable::REMOTE.bits());
        d.items_capacity = Some(24);
    }));
    f.world.take_table_events();
    assert!(!f.run(place(SWORD, Some(CHEST), 0)));
    assert_eq!(
        f.notices(),
        [Notice::Interface("You must open the Chest first".into())]
    );
    assert!(f.run(ItemCommand::Use(guid(CHEST))));
    f.server(GameEventType::VIEW_CONTENTS, &[CHEST, 0]);
    f.server(GameEventType::USE_DONE, &[0]);
    f.sent();
    assert!(f.run(place(SWORD, Some(CHEST), 0)));
    assert_eq!(f.sent(), [build_put_item_in_container(2, SWORD, CHEST, 0)]);
    assert_eq!(f.world.item(SWORD).unwrap().container_id, PLAYER);
    f.server(GameEventType::INVENTORY_SERVER_SAVE_FAILED, &[SWORD, 0]);
    f.notices();
    assert!(!f.run(place(LOOSE, None, 0)));
    assert_eq!(
        f.notices(),
        [Notice::Interface("You must first pick up the Gems".into())]
    );
    // Onto the player: into the pack (a pickup).
    assert!(f.run(place(LOOSE, Some(PLAYER), 0)));
    assert_eq!(f.sent().len(), 1);
}

const UST: u32 = 0x6000_0020;
const SCRAP: u32 = 0x6000_0021;

/// The player carries a salvaging tool and a suitable piece of scrap.
fn salvage_fixture() -> Fixture {
    let mut f = Fixture::new();
    f.world.apply(&create(UST, "Ust", |d| {
        d.item_type = ItemType::TINKERING_TOOL.bits();
        d.container_id = Some(PLAYER);
    }));
    f.world.apply(&create(SCRAP, "Scrap", |d| {
        d.material_type = Some(12);
        d.structure = Some(50);
        d.container_id = Some(PLAYER);
    }));
    f.world.take_table_events();
    f
}

#[test]
fn salvage_needs_a_carried_tool_and_suitable_items_of_the_players() {
    // OpenAC ItemInteractionControllerTests.TrySalvageItems_sendsOwnedSuitableItem,
    // _rejectsInvalidMaterialGroup, AutomationSalvageRequiresRetailToolAndSuitableOwnedItems
    // (RuntimeItemInteraction.TrySalvageItems); the body is
    // InventoryActions.BuildCreateTinkeringTool.
    use ac_proto::inventory::build_create_tinkering_tool;
    let salvage = |tool, items: &[u32]| ItemCommand::Salvage {
        tool: guid(tool),
        items: items.iter().map(|&i| guid(i)).collect(),
    };
    let mut f = salvage_fixture();
    assert!(f.run(salvage(UST, &[SCRAP])));
    assert_eq!(
        f.sent(),
        [build_create_tinkering_tool(1, UST, &[SCRAP]).unwrap()]
    );
    // Not a tool, the tool itself, an item twice, an item in the world, an
    // empty list: nothing goes out.
    for (tool, items) in [
        (SWORD, vec![SCRAP]),
        (UST, vec![UST]),
        (UST, vec![SCRAP, SCRAP]),
        (UST, vec![LOOSE]),
        (UST, vec![]),
    ] {
        assert!(!f.run(salvage(tool, &items)), "{tool:x} {items:?}");
    }
    assert!(f.sent().is_empty());
    // Worn out (structure 100) or of a material that is no salvage.
    for material in [3, 9, 56, 65, 72] {
        f.world.apply(&create(SCRAP, "Scrap", |d| {
            d.material_type = Some(material);
            d.structure = Some(50);
            d.container_id = Some(PLAYER);
        }));
        assert!(!f.run(salvage(UST, &[SCRAP])), "material {material}");
    }
    f.world.apply(&create(SCRAP, "Scrap", |d| {
        d.material_type = Some(12);
        d.structure = Some(100);
        d.container_id = Some(PLAYER);
    }));
    assert!(!f.run(salvage(UST, &[SCRAP])));
    assert!(f.sent().is_empty());
}

#[test]
fn using_a_carried_salvage_tool_opens_the_salvage_panel() {
    // OpenAC ItemPrimaryUseResult.OpenSalvage → the policy action the
    // salvage panel answers (SalvageUiController.HandlePolicyAction);
    // nothing goes out.
    let mut f = salvage_fixture();
    assert!(f.run(ItemCommand::Use(guid(UST))));
    assert!(f.sent().is_empty());
    assert_eq!(f.events(), [InventoryEvent::OpenSalvage(UST)]);
}

#[test]
fn a_salvage_result_reads_in_the_salvaging_class() {
    // OpenAC GameEventWiringTests.WireAll_SalvageOperationsResult_* (the
    // three lines and their class).
    use ac_core::chat::LogTextType;
    let mut f = salvage_fixture();
    let eight = 8.0f64.to_bits();
    f.server(
        GameEventType::SALVAGE_OPERATIONS_RESULT,
        &[28, 0, 1, 63, eight as u32, (eight >> 32) as u32, 1, 0],
    );
    assert_eq!(
        f.notices(),
        [Notice::Text(
            "You obtain 1 Silver (ws 8.00) using your knowledge of Weapon Tinkering.".into(),
            LogTextType::Salvaging
        )]
    );
    // An item the server would not take is named; one never described
    // reads "item".
    f.server(
        GameEventType::SALVAGE_OPERATIONS_RESULT,
        &[28, 2, SCRAP, 0x6000_0099, 0, 0],
    );
    assert_eq!(
        f.notices(),
        [Notice::Text(
            " The following were not suitable for salvaging: Scrap, item".into(),
            LogTextType::Salvaging
        )]
    );
    f.server(GameEventType::SALVAGE_OPERATIONS_RESULT, &[28, 0, 0, 0]);
    assert_eq!(
        f.notices(),
        [Notice::Text(
            "Salvaging Failed!".into(),
            LogTextType::Salvaging
        )]
    );
}

#[test]
fn a_mana_query_goes_out_and_its_answer_is_kept_until_the_session_ends() {
    // OpenAC WorldSession.SendQueryItemMana (SocialActions.BuildQueryItemMana),
    // GameEventWiringTests.WireAll_QueryItemManaResponse_* and
    // ItemManaStateTests (a valid answer caches, an invalid one drops).
    use ac_core::items::ItemManaChange;
    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::QueryMana(guid(SWORD))));
    assert_eq!(f.sent(), [ac_proto::magic::build_query_item_mana(1, SWORD)]);
    f.server(
        GameEventType::QUERY_ITEM_MANA_RESPONSE,
        &[SWORD, 0.75f32.to_bits(), 1],
    );
    assert_eq!(
        f.events(),
        [InventoryEvent::ItemMana(ItemManaChange {
            item_id: SWORD,
            fraction: 0.75,
            valid: true
        })]
    );
    assert_eq!(f.inventory.item_mana().try_mana_fraction(SWORD), Some(0.75));
    f.server(GameEventType::QUERY_ITEM_MANA_RESPONSE, &[SWORD, 0, 0]);
    assert!(!f.inventory.item_mana().has_mana(SWORD));
    f.server(
        GameEventType::QUERY_ITEM_MANA_RESPONSE,
        &[SWORD, 0.5f32.to_bits(), 1],
    );
    f.inventory.reset_session(&mut f.world);
    assert_eq!(f.inventory.item_mana().count(), 0);
}

mod approach;
mod placement;

// -- Item-use confirmations ---------------------------------------------------
// OpenAC `tests/AcDream.App.Tests/UI/RetailItemConfirmationControllerTests.cs`:
// the policy asks first, and only a yes sends the use (through the session,
// the answer comes back as `LocalRequest::ConfirmedUse`).

const RARE: u32 = 0x6000_0010;

fn with_rare(flags: PublicWeenieFlags) -> Fixture {
    let mut f = Fixture::new();
    f.world.apply(&create(RARE, "Rare", |d| {
        d.object_description_flags = flags.bits();
        d.useability = Some(ItemUseable::CONTAINED.bits());
        d.container_id = Some(PACK);
    }));
    f.options
        .set_bit(crate::character::option_id::CONFIRM_VOLATILE_RARE_USE, true);
    f
}

// OpenAC v0.1.25 (#217) `RetailItemConfirmationControllerTests`:
// an empty mana stone destroys the item it drains, so it asks first.
const STONE: u32 = 0x6000_0011;

fn with_stone(rare: PublicWeenieFlags, charged: bool) -> Fixture {
    let mut f = Fixture::new();
    f.world.apply(&create(RARE, "Rare", |d| {
        d.object_description_flags = rare.bits();
        d.item_type = ItemType::MISC.bits();
        d.useability = Some(ItemUseable::CONTAINED.bits());
        d.container_id = Some(PACK);
    }));
    f.world.apply(&create(STONE, "Mana Stone", |d| {
        d.item_type = ItemType::MANA_STONE.bits();
        d.useability = Some(0x0008_0008);
        d.target_type = Some(ItemType::MISC.bits());
        d.ui_effects = charged.then_some(1);
        d.container_id = Some(PACK);
    }));
    f.world.take_table_events();
    f
}

fn drain_on_rare(f: &mut Fixture) -> bool {
    assert!(f.run(ItemCommand::Use(guid(STONE))));
    f.notices();
    f.run(ItemCommand::OfferTarget(guid(RARE)))
}

fn confirmed_drain(f: &mut Fixture) -> bool {
    let mut inventory = std::mem::take(&mut f.inventory);
    let sent = inventory.execute_confirmed_mana_stone_drain(STONE, RARE, &mut f.ctx());
    f.inventory = inventory;
    sent
}

#[test]
fn an_empty_mana_stone_asks_before_destroying_its_target() {
    use crate::communication::ClientConfirmation;
    // EmptyManaStone_OnlyConfirmedValidTargetIsSent: (accept, retained
    // after the question, charged after the question).
    for (accept, retained, charged) in [
        (false, false, false),
        (true, false, false),
        (true, true, false),
        (true, false, true),
    ] {
        let mut f = with_stone(PublicWeenieFlags::NONE, false);
        assert!(drain_on_rare(&mut f));
        assert!(
            f.inventory.interaction_mode().source().is_none(),
            "target mode ends"
        );
        assert!(f.sent().is_empty());
        assert_eq!(f.inventory.transactions().busy_count(), 0);
        let question = ClientConfirmation::ManaStoneDrain {
            stone: STONE,
            target: RARE,
            message: "Are you sure you want to attempt to destroy your Rare and drain its mana into this stone?".into(),
        };
        assert_eq!(
            f.notices(),
            [
                Notice::Interface("Using the Mana Stone with the Rare".into()),
                Notice::Confirm(question)
            ]
        );

        if retained {
            f.world.apply(&create(RARE, "Rare", |d| {
                d.object_description_flags = PublicWeenieFlags::RETAINED.bits();
                d.item_type = ItemType::MISC.bits();
                d.useability = Some(ItemUseable::CONTAINED.bits());
                d.container_id = Some(PACK);
            }));
        }
        if charged {
            f.world.apply(&create(STONE, "Mana Stone", |d| {
                d.item_type = ItemType::MANA_STONE.bits();
                d.useability = Some(0x0008_0008);
                d.target_type = Some(ItemType::MISC.bits());
                d.ui_effects = Some(1);
                d.container_id = Some(PACK);
            }));
        }
        // A no is the question closing: nothing reaches the inventory.
        let sent = accept && confirmed_drain(&mut f);
        if accept && !retained && !charged {
            assert!(sent);
            assert_eq!(f.sent(), [build_use_with_target(1, STONE, RARE)]);
            assert_eq!(f.inventory.transactions().busy_count(), 1);
        } else {
            assert!(!sent);
            assert!(f.sent().is_empty());
            assert_eq!(f.inventory.transactions().busy_count(), 0);
        }
    }
}

#[test]
fn a_charged_stone_sends_at_once_and_a_retained_target_is_never_drained() {
    // ManaStone_ChargedSendsDirectly_RetainedOrNoPresenterNeverDestroys.
    for (charged, retained) in [(true, false), (false, true)] {
        let flags = if retained {
            PublicWeenieFlags::RETAINED
        } else {
            PublicWeenieFlags::NONE
        };
        let mut f = with_stone(flags, charged);
        drain_on_rare(&mut f);
        if charged {
            assert_eq!(f.sent(), [build_use_with_target(1, STONE, RARE)]);
            assert_eq!(f.inventory.transactions().busy_count(), 1);
        } else {
            assert!(f.sent().is_empty());
            assert_eq!(f.inventory.transactions().busy_count(), 0);
            // The pick's own line comes first, then the refusal.
            assert_eq!(
                f.notices(),
                [
                    Notice::Interface("Using the Mana Stone with the Rare".into()),
                    Notice::Interface(
                        "You cannot drain the mana of this item because it is retained.".into()
                    )
                ]
            );
        }
    }
}

fn confirmed_use(f: &mut Fixture, object: u32) -> bool {
    let mut inventory = std::mem::take(&mut f.inventory);
    let sent = inventory.execute_confirmed_use(object, &mut f.ctx());
    f.inventory = inventory;
    sent
}

#[test]
fn a_volatile_rare_is_used_only_after_a_yes() {
    use crate::communication::{ClientConfirmation, ItemUseConfirmation};
    let mut f = with_rare(PublicWeenieFlags::VOLATILE_RARE);
    assert!(f.run(ItemCommand::Use(guid(RARE))));
    assert!(f.sent().is_empty());
    assert_eq!(f.inventory.transactions().busy_count(), 0);
    let question = ClientConfirmation::ItemUse {
        kind: ItemUseConfirmation::VolatileRare,
        object_id: RARE,
    };
    assert_eq!(
        question.text(),
        "Are you sure you want to use this rare item?"
    );
    assert_eq!(f.notices(), [Notice::Confirm(question)]);
    assert!(confirmed_use(&mut f, RARE));
    assert_eq!(f.sent(), [build_use(1, RARE)]);
    assert_eq!(f.inventory.transactions().busy_count(), 1);
    // Golden text: the retail client's yes to a use question prints "Using
    // the %s" once the use is out.
    assert_eq!(f.notices(), [Notice::Interface("Using the Rare".into())]);
}

#[test]
fn a_target_pick_says_what_it_does_or_why_it_cannot() {
    // Golden text: the retail client's target pick prints "Using the %s with
    // the %s" (" on %s" for a player) before the use goes out, and the
    // refusal ("Cannot use the %s with the %s") when the target does not
    // fit; the target mode ends either way.
    let mut f = with_stone(PublicWeenieFlags::NONE, true);
    assert!(drain_on_rare(&mut f));
    assert_eq!(f.sent(), [build_use_with_target(1, STONE, RARE)]);
    assert_eq!(
        f.notices(),
        [Notice::Interface(
            "Using the Mana Stone with the Rare".into()
        )]
    );

    let mut f = with_stone(PublicWeenieFlags::NONE, true);
    assert!(f.run(ItemCommand::Use(guid(STONE))));
    f.notices();
    f.run(ItemCommand::OfferTarget(guid(NPC)));
    assert!(f.sent().is_empty());
    assert!(f.inventory.interaction_mode().source().is_none());
    assert_eq!(
        f.notices(),
        [Notice::Interface(
            "You can't use the Mana Stone on what you don't own".into()
        )]
    );
}

#[test]
fn a_player_killer_altar_asks_and_a_no_sends_nothing() {
    use crate::communication::{ClientConfirmation, ItemUseConfirmation};
    let mut f = with_rare(PublicWeenieFlags::PLAYER_KILLER_SWITCH);
    assert!(f.run(ItemCommand::Use(guid(RARE))));
    assert_eq!(
        f.notices(),
        [Notice::Confirm(ClientConfirmation::ItemUse {
            kind: ItemUseConfirmation::PlayerKillerSwitch,
            object_id: RARE,
        })]
    );
    // A no is the question closing: nothing reaches the inventory.
    assert!(f.sent().is_empty());
    assert_eq!(f.inventory.transactions().busy_count(), 0);
}

#[test]
fn a_yes_rechecks_the_inventory_gate_before_the_use() {
    let mut f = with_rare(PublicWeenieFlags::VOLATILE_RARE);
    assert!(f.run(ItemCommand::Use(guid(RARE))));
    f.notices();
    // A pick-up goes out while the question is open.
    assert!(f.run(ItemCommand::PickUp(guid(LOOSE))));
    f.sent();
    assert!(!confirmed_use(&mut f, RARE));
    assert!(f.sent().is_empty());
    assert_eq!(f.notices(), [Notice::Interface(REQUEST_BUSY.into())]);
}

// -- The slumlord window -------------------------------------------------------

const SLUMLORD: u32 = 0x7000_0100;

/// A 0x021D profile from `SLUMLORD` for a house nobody owns, its price
/// eight gems (class 10, named "Gem"), no maintenance lines.
fn gem_house_profile() -> Vec<u32> {
    vec![
        SLUMLORD,
        0x1234_0001, // dwelling
        0,           // owner
        1,           // bitmask
        u32::MAX,    // min level -1
        u32::MAX,    // max level
        u32::MAX,    // min rank
        u32::MAX,    // max rank
        0,           // maintenance free
        1,           // cottage
        0,           // owner name ""
        1,           // one buy line
        8,
        0,
        10,
        u32::from_le_bytes([3, 0, b'G', b'e']),
        u32::from_le_bytes([b'm', 0, 0, 0]),
        0, // plural ""
        0, // no rent lines
    ]
}

fn house(command: HousePurchaseCommand) -> ItemCommand {
    ItemCommand::HousePurchase(command)
}

// OpenAC #244: `RuntimeHousePurchaseWiringTests` (the profile reaches the
// state), `HousePurchaseUiControllerTests.Drop_StagesAndEnablesTheButton_*`
// (the button sends the staged guids and empties the strip; a yes after the
// window moved on sends nothing) and `Drop_Refused_OnlyAnItemNotCarriedIsReported`.
#[test]
fn a_house_profile_opens_the_slumlord_window_and_its_button_buys_with_the_staged_items() {
    let mut f = Fixture::new();
    f.server(GameEventType::HOUSE_PROFILE, &gem_house_profile());
    let view = f.inventory.house_purchase_view(&f.world);
    assert!(view.is_open());
    assert_eq!(view.profile.as_ref().unwrap().buy[0].name, "Gem");

    // An item the player does not carry is refused, and said.
    assert!(!f.run(house(HousePurchaseCommand::Stage {
        tab: HousePurchaseTab::Buy,
        item: LOOSE,
    })));
    assert_eq!(
        f.notices(),
        [Notice::Text(
            house_purchase::NOT_CARRYING.into(),
            ac_core::chat::LogTextType::ClientLocal
        )]
    );

    for gem in [GEM, GEM2] {
        assert!(f.run(house(HousePurchaseCommand::Stage {
            tab: HousePurchaseTab::Buy,
            item: gem,
        })));
    }
    assert!(f.notices().is_empty());
    assert!(
        f.inventory
            .house_purchase_view(&f.world)
            .can_submit(HousePurchaseTab::Buy)
    );

    // A yes that names another slumlord (the window moved on) sends nothing.
    assert!(!f.run(house(HousePurchaseCommand::Submit {
        tab: HousePurchaseTab::Buy,
        slumlord: SLUMLORD + 1,
    })));
    assert!(f.sent().is_empty());

    assert!(f.run(house(HousePurchaseCommand::Submit {
        tab: HousePurchaseTab::Buy,
        slumlord: SLUMLORD,
    })));
    assert_eq!(
        f.sent(),
        [ac_proto::house::build_buy_house(1, SLUMLORD, &[GEM, GEM2])]
    );
    let view = f.inventory.house_purchase_view(&f.world);
    assert!(view.buy.is_empty(), "sending empties the strip");
    assert_eq!(view.staged_amount(HousePurchaseTab::Buy, 0), 0);
}

// OpenAC #244: `RuntimeHousePurchaseRangeQueryTests` (a teleport closes the
// window) and `HousePurchaseUiControllerTests.AnItemLeavingTheInventory_IsUnstaged`.
#[test]
fn a_teleport_closes_the_slumlord_window_and_a_staged_item_leaving_is_unstaged() {
    let mut f = Fixture::new();
    f.server(GameEventType::HOUSE_PROFILE, &gem_house_profile());
    f.run(house(HousePurchaseCommand::Stage {
        tab: HousePurchaseTab::Buy,
        item: GEM,
    }));
    f.world.apply(&Inbound::InventoryRemoveObject(
        ac_proto::InventoryRemoveObject { guid: GEM },
    ));
    let mut inventory = std::mem::take(&mut f.inventory);
    inventory.observe(&mut f.ctx());
    f.inventory = inventory;
    let view = f.inventory.house_purchase_view(&f.world);
    assert!(view.buy.is_empty());
    assert_eq!(view.staged_amount(HousePurchaseTab::Buy, 0), 0);

    let teleport = Inbound::PlayerTeleport(ac_proto::PlayerTeleport {
        teleport_sequence: 1,
    });
    let mut inventory = std::mem::take(&mut f.inventory);
    inventory.apply(&teleport, &mut f.ctx());
    f.inventory = inventory;
    assert!(!f.inventory.house_purchase_view(&f.world).is_open());
}

// HousePurchaseUiControllerTests.Drop_NotCarried_IsReportedEvenWhenTheTabTakesNothing
// (review of #244, defect 4): the not-carried refusal comes before the
// tab's own, so a ground item dropped on Buy of an owned house is said.
#[test]
fn an_item_not_carried_is_said_even_when_the_tab_takes_nothing() {
    let mut f = Fixture::new();
    let mut owned = gem_house_profile();
    owned[2] = PLAYER;
    f.server(GameEventType::HOUSE_PROFILE, &owned);
    f.run(house(HousePurchaseCommand::Stage {
        tab: HousePurchaseTab::Buy,
        item: LOOSE,
    }));
    assert_eq!(
        f.notices(),
        [Notice::Text(
            house_purchase::NOT_CARRYING.into(),
            ac_core::chat::LogTextType::ClientLocal
        )]
    );
    // An item the player carries is refused silently there.
    f.run(house(HousePurchaseCommand::Stage {
        tab: HousePurchaseTab::Buy,
        item: GEM,
    }));
    assert!(f.notices().is_empty());
}

#[test]
fn a_game_board_in_the_world_begins_its_game_instead_of_being_used() {
    // OpenAC ItemInteractionControllerTests
    // .GameBoardInTheWorld_BeginsItsGameInsteadOfBeingUsed: someone else's
    // board begins its game (the session joins it); the player's own board,
    // anything else and an unknown object do not, and no use goes out.
    const BOARD: u32 = 0x7A9B_4001;
    const OWNED_BOARD: u32 = 0x50_000A33;
    const MISC: u32 = 0x7A9B_4002;
    let mut f = Fixture::new();
    f.world.apply(&create(BOARD, "Chess Board", |d| {
        d.item_type = ItemType::GAMEBOARD.bits();
        d.object_description_flags = PublicWeenieFlags::STUCK.bits();
    }));
    f.world.apply(&create(OWNED_BOARD, "Chess Board", |d| {
        d.item_type = ItemType::GAMEBOARD.bits();
        d.container_id = Some(PLAYER);
    }));
    f.world.apply(&create(MISC, "Thing", |d| {
        d.item_type = ItemType::MISC.bits();
        d.object_description_flags = PublicWeenieFlags::STUCK.bits();
    }));

    assert!(f.run(ItemCommand::Use(guid(BOARD))));
    assert_eq!(f.inventory.take_begun_games(), [BOARD]);
    assert!(f.sent().is_empty());

    f.now_ms += 10_000;
    f.run(ItemCommand::Use(guid(OWNED_BOARD)));
    f.now_ms += 10_000;
    f.run(ItemCommand::Use(guid(MISC)));
    f.now_ms += 10_000;
    f.run(ItemCommand::Use(guid(0x7A9B_4003)));
    assert!(f.inventory.take_begun_games().is_empty());

    // A plugin's use of the board joins it the same way.
    let mut f = Fixture::new();
    f.world.apply(&create(BOARD, "Chess Board", |d| {
        d.item_type = ItemType::GAMEBOARD.bits();
        d.object_description_flags = PublicWeenieFlags::STUCK.bits();
    }));
    assert!(f.run(ItemCommand::UseWorldObject(guid(BOARD))));
    assert_eq!(f.inventory.take_begun_games(), [BOARD]);
    assert!(f.sent().is_empty());
}

#[test]
fn a_game_board_used_while_a_request_is_pending_says_busy_and_begins_nothing() {
    // OpenAC RuntimeItemInteraction.ActivateItem: the request gate comes
    // before the board's primary use.
    const BOARD: u32 = 0x7A9B_4001;
    let mut f = Fixture::new();
    f.world.apply(&create(BOARD, "Chess Board", |d| {
        d.item_type = ItemType::GAMEBOARD.bits();
        d.object_description_flags = PublicWeenieFlags::STUCK.bits();
    }));
    assert!(f.run(ItemCommand::Use(guid(NPC))));
    f.sent();
    f.notices();
    f.now_ms += 10_000;
    assert!(!f.run(ItemCommand::Use(guid(BOARD))));
    assert!(f.inventory.take_begun_games().is_empty());
    assert!(f.sent().is_empty());
    assert_eq!(f.notices(), [Notice::Interface(REQUEST_BUSY.to_owned())]);
}
