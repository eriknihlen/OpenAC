//! Walking to an object before acting on it, against a stand-in body.
//!
//! The cases follow OpenAC `tests/AcDream.HostParity.Tests/Scenarios/WalkThenUseParityTests.cs`
//! (a use out of reach walks there and sends nothing until arrival; the
//! arrival sends it; a walk called off sends nothing; a walk that never
//! arrives is given up on and frees the gate), the pickup branches of
//! `SelectionInteractionController.SendPickup` (within reach: turn, then
//! the pickup; out of reach: walk and send now) and
//! `RuntimeApproachCompletionStateTests` for the walk's answers.

use super::*;

/// A body that records the walks begun and answers as told.
#[derive(Debug, Default)]
struct Body {
    within_reach: bool,
    refuse: bool,
    stalled: Option<u32>,
    generation: u64,
    begun: Vec<(u32, bool)>,
    cancelled: u32,
    completions: Vec<ApproachCompletion>,
}

impl Body {
    fn token(&self) -> ApproachToken {
        ApproachToken {
            lifetime: 1,
            generation: self.generation,
        }
    }

    fn end(&mut self, natural: bool) {
        let token = self.token();
        self.completions.push(ApproachCompletion { token, natural });
        self.stalled = None;
    }
}

impl ApproachSource for Body {
    fn has_body(&self) -> bool {
        true
    }
    fn plan(&self, _world: &World, guid: u32, use_radius: f32) -> Option<ApproachPlan> {
        let here = ac_physics::CellPosition::default();
        let mut there = here;
        there.frame.origin.x = if self.within_reach { 0.1 } else { 3.0 };
        Some(ApproachPlan::new(guid, there, here, use_radius, (0.3, 1.0)))
    }
    fn begin(&mut self, _world: &World, plan: &ApproachPlan) -> Option<ApproachToken> {
        if self.refuse {
            return None;
        }
        self.generation += 1;
        self.begun.push((plan.target_guid, plan.is_within_reach));
        self.stalled = Some(0);
        Some(self.token())
    }
    fn stalled_ticks(&self) -> Option<u32> {
        self.stalled
    }
    fn cancel(&mut self) {
        self.cancelled += 1;
        self.stalled = None;
    }
    fn take_completions(&mut self) -> Vec<ApproachCompletion> {
        std::mem::take(&mut self.completions)
    }
}

impl Fixture {
    fn run_walking(&mut self, command: ItemCommand, body: &mut Body) -> bool {
        let mut inventory = std::mem::take(&mut self.inventory);
        let mut ctx = self.ctx();
        ctx.approach = Some(body);
        let sent = inventory.execute(&command, &mut ctx);
        inventory.observe(&mut ctx);
        self.inventory = inventory;
        sent
    }

    fn frame(&mut self, body: &mut Body) {
        let mut inventory = std::mem::take(&mut self.inventory);
        let mut ctx = self.ctx();
        ctx.approach = Some(body);
        inventory.drive_approaches(&mut ctx);
        inventory.observe(&mut ctx);
        self.inventory = inventory;
    }
}

#[test]
fn a_use_out_of_reach_walks_there_and_goes_out_on_arrival() {
    let mut f = Fixture::new();
    let mut body = Body::default();
    assert!(f.run_walking(ItemCommand::Use(guid(NPC)), &mut body));
    // A walk to the object (not a turn), and nothing sent while under way.
    assert_eq!(body.begun, [(NPC, false)]);
    assert!(f.sent().is_empty());
    assert!(f.inventory.has_pending_use());
    // The walk holds the one-request gate.
    assert!(!f.inventory.is_ready(true));
    f.frame(&mut body);
    assert!(f.sent().is_empty());
    body.end(true);
    f.frame(&mut body);
    assert_eq!(f.sent(), [build_use(1, NPC)]);
    assert!(!f.inventory.has_pending_use());
    // Busy until the server's use-done.
    assert!(!f.inventory.is_ready(true));
    f.server(GameEventType::USE_DONE, &[0]);
    assert!(f.inventory.is_ready(true));
}

#[test]
fn a_use_within_reach_goes_out_at_once() {
    let mut f = Fixture::new();
    let mut body = Body {
        within_reach: true,
        ..Body::default()
    };
    assert!(f.run_walking(ItemCommand::Use(guid(NPC)), &mut body));
    assert!(body.begun.is_empty());
    assert_eq!(f.sent(), [build_use(1, NPC)]);
}

#[test]
fn a_walk_called_off_sends_nothing_and_frees_the_gate() {
    let mut f = Fixture::new();
    let mut body = Body::default();
    assert!(f.run_walking(ItemCommand::Use(guid(NPC)), &mut body));
    body.end(false);
    f.frame(&mut body);
    assert!(f.sent().is_empty());
    assert!(!f.inventory.has_pending_use());
    assert!(f.inventory.is_ready(true));
    // An answer for an older walk is not this walk's.
    f.now_ms += 1_000;
    assert!(f.run_walking(ItemCommand::Use(guid(NPC)), &mut body));
    body.completions.push(ApproachCompletion {
        token: ApproachToken {
            lifetime: 1,
            generation: 1,
        },
        natural: true,
    });
    f.frame(&mut body);
    assert!(f.sent().is_empty());
    assert!(f.inventory.has_pending_use());
}

#[test]
fn a_walk_that_never_arrives_is_given_up_on() {
    let mut f = Fixture::new();
    let mut body = Body::default();
    assert!(f.run_walking(ItemCommand::Use(guid(NPC)), &mut body));
    body.stalled = Some(STALLED_APPROACH_GIVE_UP_TICKS - 1);
    f.frame(&mut body);
    assert!(f.inventory.has_pending_use());
    body.stalled = Some(STALLED_APPROACH_GIVE_UP_TICKS);
    f.frame(&mut body);
    // Called off, nothing sent, the player told, the gate free again.
    assert_eq!(body.cancelled, 1);
    assert!(f.sent().is_empty());
    assert_eq!(
        f.notices(),
        [
            Notice::Interface("Approaching Town Crier".into()),
            Notice::Interface(APPROACH_NEVER_COMPLETED.into())
        ]
    );
    assert!(f.inventory.is_ready(true));
    f.now_ms += 1_000;
    assert!(f.run_walking(ItemCommand::Use(guid(NPC)), &mut body));
    assert_eq!(body.begun.len(), 2);
}

#[test]
fn a_body_that_refuses_the_walk_sends_nothing() {
    let mut f = Fixture::new();
    let mut body = Body {
        refuse: true,
        ..Body::default()
    };
    assert!(!f.run_walking(ItemCommand::Use(guid(NPC)), &mut body));
    assert!(f.sent().is_empty());
    assert!(f.inventory.is_ready(true));
}

#[test]
fn an_object_that_leaves_the_world_takes_its_armed_use_with_it() {
    let mut f = Fixture::new();
    let mut body = Body::default();
    assert!(f.run_walking(ItemCommand::Use(guid(NPC)), &mut body));
    f.world
        .apply(&Inbound::DeleteObject(ac_proto::DeleteObject {
            guid: NPC,
            instance_sequence: 1,
        }));
    let mut inventory = std::mem::take(&mut f.inventory);
    inventory.observe(&mut f.ctx());
    f.inventory = inventory;
    assert!(!f.inventory.has_pending_use());
    assert!(f.inventory.is_ready(true));
}

#[test]
fn a_pickup_within_reach_turns_first_and_one_out_of_reach_goes_now() {
    // (The loose gem joins a stack the player holds: that merge needs no
    // walk, so a ring lies there instead.)
    const RING: u32 = 0x7000_0010;
    let ring = |f: &mut Fixture| {
        f.world.apply(&create(RING, "Ring", |d| {
            d.item_type = ItemType::JEWELRY.bits();
        }));
        f.world.take_table_events();
    };
    let mut f = Fixture::new();
    ring(&mut f);
    let mut body = Body {
        within_reach: true,
        ..Body::default()
    };
    assert!(f.run_walking(ItemCommand::PickUp(guid(RING)), &mut body));
    // A turn to the item; the pickup waits for it.
    assert_eq!(body.begun, [(RING, true)]);
    assert!(f.sent().is_empty());
    assert!(f.inventory.has_pending_pickup());
    body.end(true);
    f.frame(&mut body);
    assert_eq!(f.sent(), [build_put_item_in_container(1, RING, PLAYER, 0)]);
    assert!(!f.inventory.has_pending_pickup());

    // The turn holds the one request and the item is shown waiting in the
    // pack (SelectionInteractionController.SendPickup arms the pickup with
    // the placement's reservation): a second pickup is refused rather than
    // superseding it, and a turn called off releases both.
    let mut f = Fixture::new();
    ring(&mut f);
    let mut body = Body {
        within_reach: true,
        ..Body::default()
    };
    assert!(f.run_walking(ItemCommand::PickUp(guid(RING)), &mut body));
    assert_eq!(
        f.inventory.pending_placement().map(|p| p.item_id),
        Some(RING)
    );
    assert!(!f.inventory.is_ready(true));
    f.world.apply(&create(0x7000_0011, "Ring", |d| {
        d.item_type = ItemType::JEWELRY.bits();
    }));
    f.world.take_table_events();
    assert!(!f.run_walking(ItemCommand::PickUp(guid(0x7000_0011)), &mut body));
    assert_eq!(body.begun.len(), 1);
    assert!(f.inventory.has_pending_pickup());
    assert_eq!(
        f.notices(),
        [Notice::Interface(
            "Already attempting to place Ring here".into()
        )]
    );
    body.end(false);
    f.frame(&mut body);
    assert!(f.sent().is_empty());
    assert!(!f.inventory.has_pending_pickup());
    assert_eq!(f.inventory.pending_placement(), None);
    assert!(f.inventory.is_ready(true));

    // Out of reach: the character walks there and the pickup goes now.
    let mut f = Fixture::new();
    ring(&mut f);
    let mut body = Body::default();
    assert!(f.run_walking(ItemCommand::PickUp(guid(RING)), &mut body));
    assert_eq!(body.begun, [(RING, false)]);
    assert_eq!(f.sent(), [build_put_item_in_container(1, RING, PLAYER, 0)]);
    assert!(!f.inventory.has_pending_pickup());
}

/// The interface lines among the notices.
fn interface_lines(f: &mut Fixture) -> Vec<String> {
    f.notices()
        .into_iter()
        .filter_map(|n| match n {
            Notice::Interface(text) => Some(text),
            _ => None,
        })
        .collect()
}

#[test]
fn what_may_not_be_picked_up_is_refused_before_anything_moves() {
    // OpenAC `SelectionInteractionController.ValidatePickupTarget` (asked
    // by `SendPickup` before the approach) over `WorldSelectionQuery`'s
    // `IsCreature`, `IsStuckInWorld`, `IsWieldedPositionState` and
    // `IsPickupable`; the wielded case is `SelectionInteractionControllerTests`
    // `.DirectBackpackPlacementOfARemotesWieldedItemWithdrawsItsWaitingSlot`
    // and `.PickupOfARemotesWieldedItemIsRejectedLocallyWithNoMovementOrRequest`
    // (their stand-in query names the object "Target <id>"; here it is the
    // object's own name, `WorldSelectionQuery.Describe`). Nothing walks,
    // nothing is sent, the waiting slot is withdrawn.
    const WEAPON: u32 = 0x7000_0020;
    const STATUE: u32 = 0x7000_0021;
    const CHEST: u32 = 0x7000_0022;
    const NAMELESS: u32 = 0x7000_0023;
    let mut f = Fixture::new();
    f.world.apply(&create(WEAPON, "Sword of Bob", |d| {
        d.item_type = ItemType::MELEE_WEAPON.bits();
        d.wielder_id = Some(OTHER_PLAYER);
        d.currently_wielded_location = Some(EquipMask::MELEE_WEAPON.bits());
    }));
    f.world.apply(&create(STATUE, "Statue", |d| {
        d.item_type = ItemType::MISC.bits();
        d.object_description_flags = PublicWeenieFlags::STUCK.bits();
    }));
    f.world.apply(&create(CHEST, "Chest", |d| {
        d.item_type = ItemType::CONTAINER.bits();
        d.items_capacity = Some(60);
        d.containers_capacity = Some(10);
    }));
    f.world.apply(&create(NAMELESS, " ", |d| {
        d.item_type = ItemType::MISC.bits();
        d.containers_capacity = Some(1);
    }));
    f.world.take_table_events();
    for (target, said) in [
        (NPC, "You cannot pick up creatures!"),
        (OTHER_PLAYER, "You cannot pick up creatures!"),
        (STATUE, "The Statue cannot be picked up!"),
        (WEAPON, "The Sword of Bob is being wielded by someone else!"),
        (CHEST, "The Chest can't be picked up!"),
        (NAMELESS, "The 0x70000023 can't be picked up!"),
    ] {
        let mut body = Body::default();
        assert!(!f.run_walking(ItemCommand::PickUp(guid(target)), &mut body));
        assert_eq!(interface_lines(&mut f), [said], "{target:#X}");
        assert!(body.begun.is_empty(), "{target:#X}");
        assert!(f.sent().is_empty(), "{target:#X}");
        assert!(f.inventory.pending_placement().is_none(), "{target:#X}");
        assert!(!f.inventory.has_pending_pickup(), "{target:#X}");
        assert!(f.inventory.is_ready(true), "{target:#X}");
    }
    // A host that walks nothing is refused the same way.
    assert!(!f.run(ItemCommand::PickUp(guid(STATUE))));
    assert_eq!(interface_lines(&mut f), ["The Statue cannot be picked up!"]);
    assert!(f.sent().is_empty());
}

#[test]
fn the_pickup_key_refuses_a_creature_before_any_pack_is_chosen() {
    // OpenAC `SelectionInteractionController`: the pick-up key queues a
    // pickup whose dispatch asks `ValidatePickupTarget` before
    // `PlaceWorldItemInBackpack` (`DispatchQueuedInteraction`), so a
    // character (a creature that can hold packs) is refused as a creature
    // even when the player's container slots are all taken (live
    // 2026-10-15: +Acdream with 7 packs was told "Acdream can carry no
    // more containers!"). The placement route (the place-in-inventory key,
    // a plugin's pickup: `PlaceWorldItemInBackpack`) chooses the pack
    // first and refuses for want of room.
    let mut f = Fixture::new();
    f.world.apply(&create(PLAYER, "Horan", |d| {
        d.object_description_flags = PublicWeenieFlags::PLAYER.bits();
        d.items_capacity = Some(102);
        d.containers_capacity = Some(1);
        d.item_type = ItemType::CREATURE.bits();
    }));
    f.world.apply(&create(OTHER_PLAYER, "Bob", |d| {
        d.item_type = ItemType::CREATURE.bits();
        d.object_description_flags = PublicWeenieFlags::PLAYER.bits();
        d.items_capacity = Some(102);
        d.containers_capacity = Some(7);
    }));
    f.world.take_table_events();
    assert_eq!(
        ac_core::items::placement::choose_container(
            f.world.items(),
            OTHER_PLAYER,
            PLAYER,
            PLAYER,
            PLAYER,
            true
        ),
        Err(ac_core::items::placement::PlacementRejection::ContainerCapacityFull)
    );

    let mut body = Body::default();
    assert!(!f.run_walking(ItemCommand::PickUp(guid(OTHER_PLAYER)), &mut body));
    assert_eq!(interface_lines(&mut f), ["You cannot pick up creatures!"]);
    assert!(body.begun.is_empty());
    assert!(f.sent().is_empty());
    assert!(f.inventory.pending_placement().is_none());

    assert!(f.run_walking(ItemCommand::PlaceInPack(guid(OTHER_PLAYER)), &mut body));
    assert_eq!(
        interface_lines(&mut f),
        ["Horan can carry no more containers!"]
    );
    assert!(f.sent().is_empty());
}

#[test]
fn a_creature_lying_dead_is_not_refused_as_a_creature() {
    // OpenAC `WorldSelectionQuery.IsCreature`: a creature whose current
    // motion is Dead is not one, so the refusal is what else it is (here
    // stuck in place).
    const BODY: u32 = 0x7000_0030;
    let mut f = Fixture::new();
    f.world.apply(&create(BODY, "Drudge", |d| {
        d.item_type = ItemType::CREATURE.bits();
        d.object_description_flags = PublicWeenieFlags::STUCK.bits();
    }));
    assert!(crate::inventory::pickup::is_creature(&f.world, BODY));
    f.world
        .apply(&Inbound::UpdateMotion(Box::new(ac_proto::UpdateMotion {
            guid: BODY,
            instance_sequence: 1,
            movement_sequence: 2,
            server_control_sequence: 0,
            is_autonomous: false,
            movement: ac_proto::MovementData {
                movement_type: 0,
                motion_flags: 0,
                current_style: 0x003D,
                movement: ac_proto::Movement::Interpreted {
                    state: ac_proto::InterpretedMotionState {
                        forward_command: Some(0x0011),
                        ..ac_proto::InterpretedMotionState::default()
                    },
                    sticky_object: None,
                },
            },
        })));
    f.world.take_table_events();
    assert!(!crate::inventory::pickup::is_creature(&f.world, BODY));
    assert!(!f.run(ItemCommand::PickUp(guid(BODY))));
    assert_eq!(interface_lines(&mut f), ["The Drudge cannot be picked up!"]);
}

#[test]
fn an_item_given_away_and_wielded_by_its_new_owner_is_refused_as_wielded() {
    // Seen live: the server answers a give with InventoryRemoveObject and
    // then Item_ServerSaysContainId naming the receiver; the receiver's
    // wield reaches the giver as a CreateObject that names the wielder, the
    // worn slot and the parent. The containment that came
    // for the removed record is older than that description and must not
    // be laid over it, so the pickup is refused as another's wielded item.
    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::Give {
        item: guid(SWORD),
        target: guid(OTHER_PLAYER),
        amount: 0,
    }));
    f.sent();
    let removed = Inbound::InventoryRemoveObject(ac_proto::InventoryRemoveObject { guid: SWORD });
    f.world.apply(&removed);
    let mut inventory = std::mem::take(&mut f.inventory);
    inventory.apply(&removed, &mut f.ctx());
    f.inventory = inventory;
    f.server(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[SWORD, OTHER_PLAYER, 0, 0],
    );
    let Inbound::CreateObject(mut wielded) = create(SWORD, "Sword", |d| {
        d.item_type = ItemType::MELEE_WEAPON.bits();
        d.valid_locations = Some(EquipMask::MELEE_WEAPON.bits());
        d.wielder_id = Some(OTHER_PLAYER);
        d.currently_wielded_location = Some(EquipMask::MELEE_WEAPON.bits());
    }) else {
        unreachable!()
    };
    wielded.physics.parent = Some(ac_proto::PhysicsAttachment {
        guid: OTHER_PLAYER,
        location_id: 1,
    });
    f.world.apply(&Inbound::CreateObject(wielded));
    f.world.take_table_events();
    f.notices();
    f.events();

    let sword = f.world.item(SWORD).unwrap();
    assert_eq!(sword.container_id, 0);
    assert_eq!(sword.wielder_id, OTHER_PLAYER);
    let mut body = Body::default();
    assert!(!f.run_walking(ItemCommand::PickUp(guid(SWORD)), &mut body));
    assert_eq!(
        interface_lines(&mut f),
        ["The Sword is being wielded by someone else!"]
    );
    assert!(body.begun.is_empty());
    assert!(f.sent().is_empty());
}

/// A corpse lying in the world.
fn place_corpse(f: &mut Fixture, corpse: u32) {
    f.world.apply(&create(corpse, "Corpse of Drudge", |d| {
        d.item_type = ItemType::CONTAINER.bits();
        d.object_description_flags =
            (PublicWeenieFlags::CORPSE | PublicWeenieFlags::OPENABLE).bits();
        d.useability = Some(ItemUseable::REMOTE.bits());
        d.items_capacity = Some(120);
    }));
    f.world.take_table_events();
}

fn requested_container(f: &Fixture) -> u32 {
    f.inventory.external_containers().requested_container_id()
}

// The route `Loot.Open`, `Objects.Activate` and `Items.Use` of a world
// object take: a plugin's use out of reach goes to the server at once and
// the server walks the character there, as a plugin's use does in retail.
// The client walks nowhere and arms nothing, so no walk of its own can
// stall short of the object; the ground container is asked for with the
// use. (OpenAC walks first, `RuntimeWorldObjectUse.TryUse`.)
#[test]
fn a_plugins_use_out_of_reach_goes_to_the_server_at_once() {
    const CORPSE: u32 = 0x8000_D101;
    let mut f = Fixture::new();
    place_corpse(&mut f, CORPSE);
    let mut body = Body::default();
    assert!(f.run_walking(ItemCommand::UseWorldObject(guid(CORPSE)), &mut body));
    assert!(body.begun.is_empty());
    assert_eq!(f.sent(), [build_use(1, CORPSE)]);
    assert!(!f.inventory.has_pending_use());
    assert_eq!(requested_container(&f), CORPSE);
    // Busy until the server's use-done, as any use.
    assert!(!f.inventory.is_ready(true));
    f.server(GameEventType::USE_DONE, &[0]);
    assert!(f.inventory.is_ready(true));
}

// A plugin's use leaves a walk the character is on alone: a person's walk
// or one the server ordered is neither called off nor answered for.
#[test]
fn a_plugins_use_out_of_reach_calls_off_no_walk() {
    const CORPSE: u32 = 0x8000_D102;
    let mut f = Fixture::new();
    place_corpse(&mut f, CORPSE);
    let mut body = Body::default();
    assert!(f.run_walking(ItemCommand::UseWorldObject(guid(CORPSE)), &mut body));
    f.frame(&mut body);

    assert_eq!(body.cancelled, 0);
    assert!(f.notices().is_empty());
}

// OpenAC `RuntimeItemInteraction.ExecuteUseActions`: a person's own
// double-click arms the ground container with its `SetGroundObject` action
// before its walk, as before.
#[test]
fn a_persons_open_out_of_reach_asks_for_the_container_before_the_walk() {
    const CORPSE: u32 = 0x8000_D103;
    let mut f = Fixture::new();
    place_corpse(&mut f, CORPSE);
    let mut body = Body::default();
    assert!(f.run_walking(ItemCommand::Use(guid(CORPSE)), &mut body));
    assert_eq!(body.begun, [(CORPSE, false)]);
    assert_eq!(requested_container(&f), CORPSE);
}

// OpenAC `RuntimeWorldObjectUse.PerformUse`: the automation route never cuts
// in front of a walk already armed.
#[test]
fn a_plugins_use_does_not_cut_in_front_of_an_armed_walk() {
    const CORPSE: u32 = 0x8000_D104;
    let mut f = Fixture::new();
    place_corpse(&mut f, CORPSE);
    let mut body = Body::default();
    assert!(f.run_walking(ItemCommand::Use(guid(NPC)), &mut body));
    f.now_ms += 1_000;
    assert!(!f.run_walking(ItemCommand::UseWorldObject(guid(CORPSE)), &mut body));
    assert_eq!(body.begun, [(NPC, false)]);
    assert!(f.inventory.has_pending_use());
}

/// A ruby the server lists in a corpse.
fn place_ruby(f: &mut Fixture, ruby: u32) {
    f.world.apply(&create(ruby, "Ruby", |d| {
        d.item_type = ItemType::GEM.bits();
    }));
    f.world.take_table_events();
}

// OpenAC `RuntimeWorldObjectUse.PerformUse` (the immediate branch): within
// reach the use goes out at once and the container is asked for with it,
// so the server's contents are taken as the open container's.
#[test]
fn a_plugins_open_within_reach_goes_out_and_takes_the_contents() {
    const CORPSE: u32 = 0x8000_D105;
    const RUBY: u32 = 0x8000_D106;
    let mut f = Fixture::new();
    place_corpse(&mut f, CORPSE);
    place_ruby(&mut f, RUBY);
    let mut body = Body {
        within_reach: true,
        ..Body::default()
    };
    assert!(f.run_walking(ItemCommand::UseWorldObject(guid(CORPSE)), &mut body));
    assert!(body.begun.is_empty());
    assert_eq!(f.sent(), [build_use(1, CORPSE)]);
    assert_eq!(requested_container(&f), CORPSE);

    f.server(GameEventType::VIEW_CONTENTS, &[CORPSE, 1, RUBY, 0]);
    f.server(GameEventType::USE_DONE, &[0]);
    assert_eq!(
        f.inventory.external_containers().current_container_id(),
        CORPSE
    );
    assert_eq!(f.world.items().get_contents(CORPSE), [RUBY]);
    assert!(f.inventory.is_ready(true));
}

// A plugin's open out of reach: the use goes out at once, the server walks
// the character there, and the contents it then sends are taken.
#[test]
fn a_plugins_open_out_of_reach_takes_the_contents_the_server_sends() {
    const CORPSE: u32 = 0x8000_D107;
    const RUBY: u32 = 0x8000_D108;
    let mut f = Fixture::new();
    place_corpse(&mut f, CORPSE);
    place_ruby(&mut f, RUBY);
    let mut body = Body::default();
    assert!(f.run_walking(ItemCommand::UseWorldObject(guid(CORPSE)), &mut body));
    assert!(body.begun.is_empty());
    assert_eq!(f.sent(), [build_use(1, CORPSE)]);

    f.server(GameEventType::VIEW_CONTENTS, &[CORPSE, 1, RUBY, 0]);
    f.server(GameEventType::USE_DONE, &[0]);
    assert_eq!(
        f.inventory.external_containers().current_container_id(),
        CORPSE
    );
    assert_eq!(f.world.items().get_contents(CORPSE), [RUBY]);
}

// OpenAC `RuntimeWorldObjectUse.PerformUse` with another container open
// (`ArmLandscapeContainerRequest` after the dispatch): the use leads, the
// open one is let go of, and the new one's contents are taken.
#[test]
fn a_plugins_open_with_another_corpse_open_lets_that_one_go() {
    const FIRST: u32 = 0x8000_D109;
    const SECOND: u32 = 0x8000_D10A;
    const RUBY: u32 = 0x8000_D10B;
    let mut f = Fixture::new();
    place_corpse(&mut f, FIRST);
    place_corpse(&mut f, SECOND);
    place_ruby(&mut f, RUBY);
    let mut body = Body {
        within_reach: true,
        ..Body::default()
    };
    assert!(f.run_walking(ItemCommand::UseWorldObject(guid(FIRST)), &mut body));
    f.server(GameEventType::VIEW_CONTENTS, &[FIRST, 0]);
    f.server(GameEventType::USE_DONE, &[0]);
    assert_eq!(f.sent(), [build_use(1, FIRST)]);

    f.now_ms += 1_000;
    assert!(f.run_walking(ItemCommand::UseWorldObject(guid(SECOND)), &mut body));
    assert_eq!(
        f.sent(),
        [
            build_use(2, SECOND),
            build_no_longer_viewing_contents(3, FIRST)
        ]
    );
    assert_eq!(requested_container(&f), SECOND);
    f.server(GameEventType::VIEW_CONTENTS, &[SECOND, 1, RUBY, 0]);
    f.server(GameEventType::USE_DONE, &[0]);
    assert_eq!(
        f.inventory.external_containers().current_container_id(),
        SECOND
    );
    assert_eq!(f.world.items().get_contents(SECOND), [RUBY]);
}
